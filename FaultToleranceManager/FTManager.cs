/*
 * Naiad ver. 0.6
 * Copyright (c) Microsoft Corporation
 * All rights reserved. 
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0 
 *
 * THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR
 * CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT
 * LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR
 * A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.
 *
 * See the Apache Version 2.0 License for specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Collections.Concurrent;

using Microsoft.Research.Naiad.Frameworks.DifferentialDataflow;
using Microsoft.Research.Naiad.Frameworks.Lindi;
using Microsoft.Research.Naiad.Runtime.FaultTolerance;
using Microsoft.Research.Naiad.Runtime.Progress;
using Microsoft.Research.Naiad.Dataflow;
using Microsoft.Research.Naiad.Dataflow.StandardVertices;

namespace Microsoft.Research.Naiad.FaultToleranceManager
{
    /// <summary>
    /// The Microsoft.Research.Naiad.FaultToleranceManager namespace provides the classes for incrementally keeping
    /// track of the most recent global checkpoint that is available
    /// </summary>
    class NamespaceDoc
    {
    }

    internal class DiscardList
    {
        /// <summary>
        /// For a Pair p in messages, if p.second.Count == 0 the entry is discarded, otherwise p.First is a downstream time
        /// and p.second is a list of upstream times. Some entries of p.second may have -ve location in which case they are
        /// ignored.
        /// </summary>
        public List<Pair<Pointstamp, List<Pointstamp>>> messages;
        /// <summary>
        ///  count of discarded entries in messages
        /// </summary>
        public int empty;

        public DiscardList()
        {
            this.messages = new List<Pair<Pointstamp, List<Pointstamp>>>();
        }
    }

    internal struct NodeState
    {
        public int gcUpdateSendVertexId;
        public bool downwardClosed;
        public FTFrontier currentRestoration;
        public FTFrontier currentNotification;
        public HashSet<FTFrontier> checkpoints;
        public Dictionary<LexStamp, int[]> deliveredMessages;
        public HashSet<Pointstamp> deliveredNotifications;
        /// <summary>
        /// Key is downstream stage ID. For each downstream stage, there is a list of pairs where the first element
        /// is the downstream time dt, and the second element is a list of upstream times that discarded a message sent to dt.
        /// </summary>
        public Dictionary<int, DiscardList> discardedMessages;

        public NodeState(bool downwardClosed, int gcUpdateSendVertexId)
        {
            this.gcUpdateSendVertexId = gcUpdateSendVertexId;
            this.downwardClosed = downwardClosed;
            this.currentRestoration = new FTFrontier(false);
            this.currentNotification = new FTFrontier(false);
            this.checkpoints = new HashSet<FTFrontier>();
            this.checkpoints.Add(currentRestoration);
            this.deliveredMessages = new Dictionary<LexStamp, int[]>();
            this.deliveredNotifications = new HashSet<Pointstamp>();
            this.discardedMessages = new Dictionary<int, DiscardList>();
        }
    }

    /// <summary>
    /// class to keep track of the most recent global checkpoint for a Naiad computation
    /// </summary>
    public class FTManager
    {
        public FTManager(Func<string, LogStream> logStreamFactory)
        {
            this.logStreamFactory = logStreamFactory;
        }

        private List<Stage> denseStages;
        internal List<Stage> DenseStages { get { return this.denseStages; } }

        private int[] toDenseStage;

        private Thread managerThread;

        private readonly Dictionary<SV, NodeState> nodeState = new Dictionary<SV, NodeState>();

        private readonly Dictionary<SV, SV[]> upstreamEdges = new Dictionary<SV, SV[]>();
        private readonly Dictionary<int, SV[]> upstreamStage = new Dictionary<int, SV[]>();
        private readonly Dictionary<int, SortedDictionary<FTFrontier, int>> stageFrontiers = new Dictionary<int, SortedDictionary<FTFrontier, int>>();

        private void AddStageFrontier(int stage, FTFrontier frontier)
        {
            var stageDictionary = this.stageFrontiers[stage];
            int count;
            if (stageDictionary.TryGetValue(frontier, out count))
            {
                stageDictionary[frontier] = count + 1;
            }
            else
            {
                stageDictionary[frontier] = 1;
            }
        }

        private void RemoveStageFrontier(int stage, FTFrontier frontier)
        {
            var stageDictionary = this.stageFrontiers[stage];
            if (!stageDictionary.ContainsKey(frontier))
            {
                throw new ApplicationException("Looking up bad frontier " + stage + "." + frontier);
            }
            int count = stageDictionary[frontier];
            if (count == 1)
            {
                stageDictionary.Remove(frontier);
            }
            else
            {
                stageDictionary[frontier] = count - 1;
            }
        }

        private FTFrontier StageFrontier(int stageId)
        {
            return this.stageFrontiers[stageId].First().Key;
        }

        private Func<string, LogStream> logStreamFactory;
        private System.Diagnostics.Stopwatch stopwatch;
        private LogStream checkpointLog = null;
        internal LogStream CheckpointLog
        {
            get
            {
                if (checkpointLog == null)
                {
                    this.checkpointLog = logStreamFactory("ftmanager.log");
                }
                return checkpointLog;
            }
        }

        public enum LogLevel
        {
            Verbose,
            Regular,
            Minimal
        }
        private LogLevel logLevel = LogLevel.Regular;

        public void WriteLog(string entry)
        {
            lock (this)
            {
                long microseconds = this.stopwatch.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency;
                var log = this.CheckpointLog.Log;
                lock (log)
                {
                    log.WriteLine(String.Format("{0:D11}: {1}", microseconds, entry));
                }
            }
        }

        private Computation computation;

        private HashSet<int> stagesToMonitor = new HashSet<int>();

        private enum State
        {
            Incremental,
            PreparingForRollback,
            DrainingForRollback,
            AddedTemporaryForRollback,
            RevokingTemporaryForRollback,
            DrainingForExit,
            Stopping
        }
        private State state = State.Incremental;

        // initialize this to non-null because the first computation is triggered by GetGraph and
        // expects there to be non-null pendingUpdates when it terminates
        private List<CheckpointUpdate> pendingUpdates = new List<CheckpointUpdate>();
        private List<CheckpointUpdate> temporaryUpdates = null;
        private Dictionary<SV,CheckpointLowWatermark> rollbackFrontiers = null;
        private List<CheckpointLowWatermark> pendingGCUpdates = null;
        private ManualResetEventSlim quiescenceBarrier = null;

        private InputCollection<Edge> graph;
        private InputCollection<Checkpoint> checkpointStream;
        private InputCollection<DeliveredMessage> deliveredMessages;
        private InputCollection<Notification> deliveredNotifications;
        private InputCollection<DiscardedMessage> discardedMessages;

        private int epoch = -1;

        private IEnumerable<Checkpoint> InitializeCheckpoints()
        {
            foreach (Stage stage in this.denseStages)
            {
                for (int vertexId=0; vertexId<stage.Placement.Count; ++vertexId)
                {
                    SV node = new SV(this.toDenseStage[stage.StageId], vertexId);
                    NodeState state = this.nodeState[node];

                    yield return new Checkpoint(node, state.currentRestoration, state.downwardClosed);
                }
            }
        }

        private void GetGraph(object o, Diagnostics.GraphMaterializedEventArgs args)
        {
            this.denseStages = new List<Stage>();
            this.toDenseStage = new int[args.stages.Select(s => s.StageId).Max() + 1];
            foreach (Stage stage in args.stages)
            {
                int denseStageId = this.denseStages.Count;
                this.toDenseStage[stage.StageId] = denseStageId;
                this.stageFrontiers.Add(denseStageId, new SortedDictionary<FTFrontier, int>());
                this.denseStages.Add(stage);
            }

            foreach (Pair<Pair<int, int>, int> ftVertex in args.ftmanager)
            {
                int denseStageId = this.toDenseStage[ftVertex.First.First];
                Stage stage = this.denseStages[denseStageId];
                SV node = new SV(denseStageId, ftVertex.First.Second);
                NodeState state = new NodeState(
                    !CheckpointProperties.IsStateful(stage.CheckpointType), ftVertex.Second);
                this.nodeState.Add(node, state);
                this.AddStageFrontier(denseStageId, new FTFrontier(false));
            }

            foreach (Pair<SV, SV[]> edgeList in args.edges
                .Select(e => new SV(this.toDenseStage[e.First.First], e.First.Second).PairWith(
                    new SV(this.toDenseStage[e.Second.First], e.Second.Second)))
                .GroupBy(e => e.Second)
                .Select(e => e.Key.PairWith(e.Select(ee => ee.First).ToArray())))
            {
                this.upstreamEdges.Add(edgeList.First, edgeList.Second);
            }

            foreach (SV node in this.nodeState.Keys.Where(n => !this.upstreamEdges.ContainsKey(n)))
            {
                this.upstreamEdges.Add(node, new SV[0]);
            }

            foreach (Pair<int, SV[]> edgeList in args.edges
                .Select(e => new SV(this.toDenseStage[e.First.First], e.First.Second).PairWith(this.toDenseStage[e.Second.First]))
                .GroupBy(e => e.Second)
                .Select(e => e.Key.PairWith(e.Select(ee => ee.First).Distinct().ToArray())))
            {
                this.upstreamStage.Add(edgeList.First, edgeList.Second);
            }

            foreach (int denseStage in this.nodeState.Keys.Select(sv => sv.DenseStageId).Distinct().Where(n => !this.upstreamStage.ContainsKey(n)))
            {
                this.upstreamStage.Add(denseStage, new SV[0]);
            }

            foreach (Pair<SV, int> edgeList in args.edges
                .Select(e => new SV(this.toDenseStage[e.First.First], e.First.Second).PairWith(this.toDenseStage[e.Second.First]))
                .Distinct())
            {
                this.nodeState[edgeList.First].discardedMessages.Add(edgeList.Second, new DiscardList());
            }

            this.graph.OnNext(args.edges.Select(e => new Edge
            {
                src = new SV(this.toDenseStage[e.First.First], e.First.Second),
                dst = new SV(this.toDenseStage[e.Second.First], e.Second.Second)
            }));
            this.graph.OnCompleted();

            this.checkpointStream.OnNext(this.InitializeCheckpoints());

            this.deliveredMessages.OnNext();
            this.deliveredNotifications.OnNext();
            this.discardedMessages.OnNext();

            ++this.epoch;
        }

        private void AddChangesFromUpdate(
            CheckpointUpdate update,
            int updateWeight,
            List<Weighted<Checkpoint>> checkpointChanges,
            List<Weighted<Notification>> notificationChanges,
            List<Weighted<DeliveredMessage>> deliveredMessageChanges,
            List<Weighted<DiscardedMessage>> discardedMessageChanges)
        {
            int denseStageId = this.toDenseStage[update.stageId];
            Stage stage = this.denseStages[denseStageId];
            SV node = new SV(denseStageId, update.vertexId);
            NodeState state = this.nodeState[node];

#if false
#if true
            if (update.stageId == 46 && update.vertexId == 0)
            {
                if (update.frontier.maximalElement.Timestamp[1] > 1)
                {
                    Console.WriteLine("Ignoring update " + stage + "[" + update.vertexId + "] " + update.frontier);
                    this.nodeState[node] = state;
                    return;
                }
            }
#else
            if (update.stageId == 33 && update.vertexId == 0)
            {
                Pointstamp p = new Pointstamp();
                p.Location = update.stageId;
                p.Timestamp.a = 0;
                p.Timestamp.b = 2;
                p.Timestamp.c = 3;
                p.Timestamp.Length = 3;

                if (update.frontier.Contains(p))
                {
                    this.nodeState[node] = state;
                    return;
                }
            }

            if (update.stageId == 49 && update.vertexId == 0)
            {
                Pointstamp p = new Pointstamp();
                p.Location = update.stageId;
                p.Timestamp.a = 3;
                p.Timestamp.Length = 1;

                if (update.frontier.Contains(p))
                {
                    this.nodeState[node] = state;
                    return;
                }
            }
#endif
#endif

            if (state.currentRestoration.Contains(update.frontier))
            {
                throw new ApplicationException("FT checkpoints received out of order");
            }

            if (!update.isTemporary && state.downwardClosed)
            {
                FTFrontier oldFrontier = state.checkpoints.Single();

                if (oldFrontier.Contains(update.frontier))
                {
                    throw new ApplicationException("FT checkpoints received out of order");
                }

                state.checkpoints.Remove(oldFrontier);
                state.checkpoints.Add(update.frontier);
                if (this.logLevel == LogLevel.Verbose)
                {
                    this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + oldFrontier + "->" + update.frontier + " AC");
                }

                checkpointChanges.AddRange(new Weighted<Checkpoint>[]
                    {
                        new Weighted<Checkpoint>(new Checkpoint(node, update.frontier, true), 1),
                        new Weighted<Checkpoint>(new Checkpoint(node, oldFrontier, true), -1)
                    });
            }
            else
            {
                if (!update.isTemporary)
                {
                    foreach (var checkpoint in state.checkpoints)
                    {
                        if (checkpoint.Contains(update.frontier))
                        {
                            throw new ApplicationException("FT checkpoints received out of order");
                        }
                    }

                    state.checkpoints.Add(update.frontier);
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + update.frontier + " AC");
                    }
                }

                checkpointChanges.AddRange(new Weighted<Checkpoint>[]
                    {
                        new Weighted<Checkpoint>(new Checkpoint(node, update.frontier, false), updateWeight)
                    });
            }

            IEnumerable<DeliveredMessage> messages =
                update.deliveredMessages.SelectMany(srcStage =>
                    srcStage.Second.Select(time =>
                            new DeliveredMessage
                            {
                                srcDenseStage = this.toDenseStage[srcStage.First],
                                dst = node,
                                dstTime = new LexStamp(time)
                            }));

            if (!update.isTemporary)
            {
                foreach (var time in messages.GroupBy(m => m.dstTime))
                {
                    if (state.currentRestoration.Contains(time.Key.Time(
                                                            node.StageId(this),
                                                            this.DenseStages[node.DenseStageId].DefaultVersion.Timestamp.Length)))
                    {
                        throw new ApplicationException("Stale Delivered message");
                    }
                    var srcs = time.Select(m => m.srcDenseStage).ToArray();
                    state.deliveredMessages.Add(time.Key, srcs);
                    this.numberOfDelivered += srcs.Length;
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        foreach (var src in srcs)
                        {
                            this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + this.DenseStages[src].StageId + "->" + time.Key + " AM");
                        }
                    }
                }
            }
            deliveredMessageChanges
                .AddRange(messages.Select(m => new Weighted<DeliveredMessage>(m, updateWeight)));

            if (!update.isTemporary)
            {
                foreach (var time in update.notifications)
                {
                    if (state.currentRestoration.Contains(time))
                    {
                        throw new ApplicationException("Stale Delivered notification");
                    }
                    state.deliveredNotifications.Add(time);
                    ++this.numberOfNotifications;
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + time.Timestamp + " AN");
                    }
                }
            }
            notificationChanges
                .AddRange(update.notifications.Select(time =>
                    new Weighted<Notification>(new Notification { node = node, time = new LexStamp(time) }, updateWeight)));

            foreach (var downstreamStage in update.discardedMessages)
            {
                int denseDownStageId = this.toDenseStage[downstreamStage.First];
                var dstFrontier = this.StageFrontier(denseDownStageId);
                var stageTimes = state.discardedMessages[denseDownStageId];
                foreach (var upstreamTime in downstreamStage.Second)
                {
                    if (state.currentRestoration.Contains(upstreamTime.First))
                    {
                        throw new ApplicationException("Stale Discarded message");
                    }
                    foreach (var downstreamTime in upstreamTime.Second.Distinct())
                    {
                        if (!dstFrontier.Contains(downstreamTime))
                        {
                            discardedMessageChanges.Add(new Weighted<DiscardedMessage>(
                                new DiscardedMessage
                                {
                                    src = node,
                                    dstDenseStage = denseDownStageId,
                                    srcTime = new LexStamp(upstreamTime.First),
                                    dstTime = new LexStamp(downstreamTime)
                                }, updateWeight));
                            ++this.numberOfDiscarded;
                            if (!update.isTemporary)
                            {
                                bool found = false;
                                for (int i=0; i<stageTimes.messages.Count; ++i)
                                {
                                    // if the list is empty that means the entry was pruned
                                    if (stageTimes.messages[i].Second.Count > 0 &&
                                        stageTimes.messages[i].First.Equals(downstreamTime))
                                    {
                                        stageTimes.messages[i].Second.Add(upstreamTime.First);
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found)
                                {
                                    if (stageTimes.empty > 0)
                                    {
                                        // find a pruned entry and re-use it
                                        for (int i = 0; i < stageTimes.messages.Count; ++i)
                                        {
                                            var oldList = stageTimes.messages[i].Second;
                                            if (oldList.Count == 0)
                                            {
                                                oldList.Add(upstreamTime.First);
                                                stageTimes.messages[i] = downstreamTime.PairWith(oldList);
                                            }
                                        }
                                        --stageTimes.empty;
                                    }
                                    else
                                    {
                                        List<Pointstamp> newList = new List<Pointstamp>();
                                        newList.Add(upstreamTime.First);
                                        stageTimes.messages.Add(downstreamTime.PairWith(newList));
                                    }
                                }
                                if (this.logLevel == LogLevel.Verbose)
                                {
                                    this.WriteLog(node + " " + upstreamTime.First + "->" + downstreamStage.First + "." + downstreamTime.Timestamp);
                                }
                            }
                        }
                    }
                }
            }

            this.nodeState[node] = state;
        }

        private void HandleCheckpointChanges(
            SV node, ref NodeState state, FTFrontier newFrontier, bool isLowWatermark,
            List<Weighted<Checkpoint>> checkpointChanges)
        {
            var thisCheckpoints = state.checkpoints
                .Where(c => !newFrontier.Equals(c) &&
                            isLowWatermark == newFrontier.Contains(c))
                .ToArray();
            if (state.downwardClosed)
            {
                if (isLowWatermark)
                {
                    if (thisCheckpoints.Length > 0)
                    {
                        throw new ApplicationException("Multiple downward-closed checkpoints");
                    }
                    if (state.checkpoints.Count != 1)
                    {
                        throw new ApplicationException("No downward-closed checkpoint");
                    }
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + state.checkpoints.Single() + "-" + newFrontier + " LWM");
                    }
                }
                else
                {
                    if (thisCheckpoints.Length > 0)
                    {
                        if (thisCheckpoints.Length > 1 || state.checkpoints.Count != 1)
                        {
                            throw new ApplicationException("Multiple downward-closed checkpoints");
                        }

                        state.checkpoints.Remove(thisCheckpoints[0]);
                        state.checkpoints.Add(newFrontier);
                        if (this.logLevel == LogLevel.Verbose)
                        {
                            this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + thisCheckpoints[0] + "->" + newFrontier + " RC");
                        }

                        checkpointChanges.AddRange(new Weighted<Checkpoint>[] {
                                new Weighted<Checkpoint>(
                                    new Checkpoint(node, newFrontier, true), 1),
                                new Weighted<Checkpoint>(
                                    new Checkpoint(node, thisCheckpoints[0], true), -1) });
                    }
                }
            }
            else
            {
                bool dc = state.downwardClosed;
                checkpointChanges.AddRange(thisCheckpoints
                        .Select(c => new Weighted<Checkpoint>(new Checkpoint(node, c, dc), -1)));
                foreach (var c in thisCheckpoints)
                {
                    state.checkpoints.Remove(c);
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + c + " RC");
                    }
                }
            }
        }

        private void HandleNotificationChanges(
            SV node, ref NodeState state, FTFrontier newFrontier, bool isLowWatermark,
            List<Weighted<Notification>> notificationChanges)
        {
            var thisNotifications = state.deliveredNotifications
                .Where(n => isLowWatermark == newFrontier.Contains(n))
                .ToArray();
            notificationChanges.AddRange(thisNotifications
                    .Select(n => new Weighted<Notification>(
                        new Notification { node = node, time = new LexStamp(n) }, -1)));
            foreach (var n in thisNotifications)
            {
                state.deliveredNotifications.Remove(n);
                if (this.logLevel == LogLevel.Verbose)
                {
                    this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + n.Timestamp + " RN");
                }
            }
        }

        private void HandleDeliveredMessageChanges(
            SV node, ref NodeState state, FTFrontier newFrontier, bool isLowWatermark,
            List<Weighted<DeliveredMessage>> deliveredMessageChanges)
        {
            var thisDeliveredMessages = state.deliveredMessages
                .Where(m => isLowWatermark == newFrontier.Contains(m.Key.Time(
                                                                        node.StageId(this),
                                                                        this.DenseStages[node.DenseStageId].DefaultVersion.Timestamp.Length)))
                .ToArray();
            deliveredMessageChanges.AddRange(thisDeliveredMessages
                    .SelectMany(t => t.Value
                        .Select(m => new Weighted<DeliveredMessage>(
                            new DeliveredMessage
                            {
                                srcDenseStage = m,
                                dst = node,
                                dstTime = t.Key
                            }, -1))));
            foreach (var m in thisDeliveredMessages)
            {
                if (this.logLevel == LogLevel.Verbose)
                {
                    foreach (var i in state.deliveredMessages[m.Key])
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + i + "->" + m.Key + " RM");
                    }
                }
                state.deliveredMessages.Remove(m.Key);
            }
        }

        private void HandleDiscardedMessageChanges(
            SV node, ref NodeState state, FTFrontier newFrontier, FTFrontier oldFrontier, bool isLowWatermark,
            List<Weighted<DiscardedMessage>> discardedMessageChanges, HashSet<int> newStageFrontiers)
        {
            if (isLowWatermark)
            {
                this.RemoveStageFrontier(node.DenseStageId, oldFrontier);
                this.AddStageFrontier(node.DenseStageId, newFrontier);
                FTFrontier newStageFrontier = this.StageFrontier(node.DenseStageId);
                if (!oldFrontier.Equals(newStageFrontier))
                {
                    newStageFrontiers.Add(node.StageId(this));

                    // For each upstream vertex, prune its discarded messages, removing any whose destination timestamp
                    // is within the new stage frontier.
                    foreach (SV upstream in this.upstreamStage[node.DenseStageId])
                    {
                        NodeState upstreamState = this.nodeState[upstream];

                        var downstreamTimes = upstreamState.discardedMessages[node.DenseStageId];
                        foreach (var downstreamTime in downstreamTimes.messages)
                        {
                            if (downstreamTime.Second.Count > 0 &&
                                newStageFrontier.Contains(downstreamTime.First))
                            {
                                discardedMessageChanges.AddRange(downstreamTime.Second
                                    // only remove times that weren't deleted earlier
                                    .Where(t => t.Location >= 0)
                                    .Select(
                                        upstreamTime => new Weighted<DiscardedMessage>(
                                            new DiscardedMessage
                                            {
                                                src = upstream,
                                                dstDenseStage = node.DenseStageId,
                                                srcTime = new LexStamp(upstreamTime),
                                                dstTime = new LexStamp(downstreamTime.First)
                                            }, -1)));
                                // mark the entry as pruned
                                downstreamTime.Second.Clear();
                                ++downstreamTimes.empty;
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var downstreamStage in state.discardedMessages)
                {
                    foreach (var downstreamTime in downstreamStage.Value.messages)
                    {
                        for (int i = 0; i < downstreamTime.Second.Count; ++i)
                        {
                            var upstreamTime = downstreamTime.Second[i];
                            if (upstreamTime.Location >= 0 &&
                                !newFrontier.Contains(upstreamTime))
                            {
                                // If we remove the last downstreamTime don't worry about garbage collecting the upstreamTime since
                                // it will happen eventually in a low watermark collection.
                                discardedMessageChanges.Add(new Weighted<DiscardedMessage>(
                                    new DiscardedMessage
                                    {
                                        src = node,
                                        dstDenseStage = downstreamStage.Key,
                                        srcTime = new LexStamp(upstreamTime),
                                        dstTime = new LexStamp(downstreamTime.First)
                                    }, -1));
                                // mark this as deleted
                                Pointstamp deleted = new Pointstamp();
                                deleted.Location = -1;
                                downstreamTime.Second[i] = deleted;
                            }
                        }
                    }
                }
            }
        }

        private void InjectChangesFromComputedUpdate(
            SV node, FTFrontier oldFrontier, FTFrontier newFrontier, bool isLowWatermark,
            List<Weighted<Checkpoint>> checkpointChanges,
            List<Weighted<Notification>> notificationChanges,
            List<Weighted<DeliveredMessage>> deliveredMessageChanges,
            List<Weighted<DiscardedMessage>> discardedMessageChanges,
            HashSet<int> newStageFrontiers
            )
        {
            NodeState state = this.nodeState[node];

            HandleCheckpointChanges(node, ref state, newFrontier, isLowWatermark, checkpointChanges);
            HandleNotificationChanges(node, ref state, newFrontier, isLowWatermark, notificationChanges);
            HandleDeliveredMessageChanges(node, ref state, newFrontier, isLowWatermark, deliveredMessageChanges);
            HandleDiscardedMessageChanges(
                node, ref state, newFrontier, oldFrontier, isLowWatermark,
                discardedMessageChanges, newStageFrontiers);

            this.nodeState[node] = state;
        }

        private bool AddChangesFromComputedUpdate(
            IGrouping<SV, Weighted<Frontier>> computedUpdate,
            List<Weighted<Checkpoint>> checkpointChanges,
            List<Weighted<Notification>> notificationChanges,
            List<Weighted<DeliveredMessage>> deliveredMessageChanges,
            List<Weighted<DiscardedMessage>> discardedMessageChanges,
            List<CheckpointLowWatermark> gcUpdates,
            HashSet<int> newStageUpdates)
        {
            SV node = computedUpdate.Key;
            NodeState state = this.nodeState[node];
            FTFrontier oldRestoration = state.currentRestoration;

            foreach (var change in computedUpdate.GroupBy(u => u.record.isNotification))
            {
                var updates = change.OrderBy(c => c.weight).ToArray();
                if (change.Key)
                {
                    FTFrontier f0 = updates[0].record.ToFrontier(this);
                    if (!((updates.Length == 2 &&
                           updates[0].weight == -1 && f0.Equals(state.currentNotification) &&
                           updates[1].weight == 1) ||
                          (updates.Length == 1 &&
                           updates[0].weight == 1 && f0.Empty && state.currentNotification.Empty)))
                    {
                        throw new ApplicationException("Bad incremental logic");
                    }
                    state.currentNotification = updates.Last().record.ToFrontier(this);
                }
                else
                {
                    FTFrontier f0 = updates[0].record.ToFrontier(this);
                    if (!((updates.Length == 2 &&
                           updates[0].weight == -1 && f0.Equals(state.currentRestoration) &&
                           updates[1].weight == 1 && updates[1].record.ToFrontier(this).Contains(state.currentRestoration)) ||
                          (updates.Length == 1 &&
                           updates[0].weight == 1 && f0.Empty && state.currentRestoration.Empty)))
                    {
                        Console.WriteLine(updates.Length + " updates for " + state.currentRestoration);
                        foreach (var update in updates)
                        {
                            Console.WriteLine(update.weight + ": " + update.record.node.StageId(this) + "." + update.record.node.VertexId + " " + update.record.frontier);
                        }
                        this.CheckpointLog.Flush();
                        throw new ApplicationException("Bad incremental logic");
                    }
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(node.StageId(this) + "." + node.VertexId + " " + state.currentRestoration + "->" + updates.Last().record.frontier);
                    }
                    state.currentRestoration = updates.Last().record.ToFrontier(this);
                }
            }

            this.nodeState[node] = state;

            if (oldRestoration.Equals(state.currentRestoration))
            {
                return false;
            }

            if (oldRestoration.Contains(state.currentRestoration))
            {
                throw new ApplicationException("Bad incremental logic");
            }

            gcUpdates.Add(new CheckpointLowWatermark
            {
                managerVertex = state.gcUpdateSendVertexId,
                stageId = node.StageId(this), vertexId = node.VertexId,
                dstStageId = -1, dstVertexId = -1,
                frontier = state.currentRestoration
            });

            foreach (SV upstream in this.upstreamEdges[node])
            {
                NodeState upstreamState = this.nodeState[upstream];
                gcUpdates.Add(new CheckpointLowWatermark
                    {
                        managerVertex = upstreamState.gcUpdateSendVertexId,
                        stageId = upstream.StageId(this), vertexId = upstream.VertexId,
                        dstStageId = node.StageId(this), dstVertexId = node.VertexId,
                        frontier = state.currentRestoration
                    });
            }

            this.InjectChangesFromComputedUpdate(node, oldRestoration, state.currentRestoration, true,
                checkpointChanges, notificationChanges, deliveredMessageChanges, discardedMessageChanges, newStageUpdates);

            return true;
        }

        private bool InjectUpdates(
            IEnumerable<CheckpointUpdate> updates,
            IEnumerable<Weighted<Frontier>> changes,
            bool sendGCUpdates)
        {
            List<Weighted<Checkpoint>> checkpointChanges = new List<Weighted<Checkpoint>>();
            List<Weighted<Notification>> notificationChanges = new List<Weighted<Notification>>();
            List<Weighted<DeliveredMessage>> deliveredMessageChanges = new List<Weighted<DeliveredMessage>>();
            List<Weighted<DiscardedMessage>> discardedMessageChanges = new List<Weighted<DiscardedMessage>>();
            List<CheckpointLowWatermark> gcUpdates;
            HashSet<int> newStageUpdates = new HashSet<int>();
            
            if (sendGCUpdates)
            {
                gcUpdates = new List<CheckpointLowWatermark>();
            }
            else
            {
                gcUpdates = this.pendingGCUpdates;
            }

            bool didAnything = false;

            this.WriteLog("INJECTING");

            foreach (CheckpointUpdate update in updates)
            {
                if (this.logLevel != LogLevel.Minimal)
                {
                    this.WriteLog(update.stageId + "." + update.vertexId + " " + update.frontier + " " + (update.isTemporary ? "TA" : "UA"));
                }
                this.AddChangesFromUpdate(update, 1,
                    checkpointChanges, notificationChanges, deliveredMessageChanges, discardedMessageChanges);
                didAnything = true;
            }

            if (changes != null)
            {
                this.WriteLog("ADDING CHANGES");
                if (this.logLevel == LogLevel.Verbose)
                {
                    foreach (IGrouping<SV, Weighted<Frontier>> u in changes.GroupBy(c => c.record.node).OrderBy(s => s.Key.denseId))
                    {
                        StringBuilder sb = new StringBuilder(u.Key.StageId(this) + "." + u.Key.VertexId);
                        foreach (var f in u)
                        {
                            sb.Append(" " + f.weight + " " + (f.record.isNotification ? "N" : "F") + f.record.frontier);
                        }
                        this.WriteLog(sb.ToString());
                    }
                }
                foreach (IGrouping<SV, Weighted<Frontier>> computedUpdate in changes.GroupBy(c => c.record.node))
                {
                    didAnything = this.AddChangesFromComputedUpdate(computedUpdate,
                        checkpointChanges, notificationChanges, deliveredMessageChanges, discardedMessageChanges,
                        gcUpdates, newStageUpdates)
                        || didAnything;
                }
                this.WriteLog("DONE ADDING CHANGES");
            }

            if (didAnything)
            {
                foreach (int stage in newStageUpdates.Where(s => this.stagesToMonitor.Contains(s)))
                {
                    foreach (int sendVertex in this.nodeState.Values.Select(s => s.gcUpdateSendVertexId).Distinct())
                    {
                        gcUpdates.Add(new CheckpointLowWatermark
                        {
                            managerVertex = sendVertex,
                            stageId = stage,
                            vertexId = -1,
                            dstStageId = -1,
                            dstVertexId = -1,
                            frontier = this.StageFrontier(this.toDenseStage[stage])
                        });
                    }
                }

                if (gcUpdates.Count > 0 && this.computation != null && sendGCUpdates)
                {
                    if (this.logLevel != LogLevel.Minimal)
                    {
                        foreach (var update in gcUpdates)
                        {
                            this.WriteLog(update.stageId + "." + update.vertexId + " " + update.frontier + " " + "G" + update.dstStageId + "." + update.dstVertexId);
                        }
                    }
                    this.computation.ReceiveCheckpointUpdates(gcUpdates);
                }

                this.WriteLog("START");

                this.checkpointStream.OnNext(checkpointChanges);
                this.deliveredNotifications.OnNext(notificationChanges);
                this.deliveredMessages.OnNext(deliveredMessageChanges);
                this.discardedMessages.OnNext(discardedMessageChanges);

                ++this.epoch;
            }

            return didAnything;
        }

        private void InjectRollbackUpdates(IEnumerable<CheckpointUpdate> updates)
        {
            List<Weighted<Checkpoint>> checkpointChanges = new List<Weighted<Checkpoint>>();
            List<Weighted<Notification>> notificationChanges = new List<Weighted<Notification>>();
            List<Weighted<DeliveredMessage>> deliveredMessageChanges = new List<Weighted<DeliveredMessage>>();
            List<Weighted<DiscardedMessage>> discardedMessageChanges = new List<Weighted<DiscardedMessage>>();

            foreach (CheckpointUpdate update in updates)
            {
                this.AddChangesFromUpdate(
                    update, -1,
                    checkpointChanges, notificationChanges, deliveredMessageChanges, discardedMessageChanges);
            }

            foreach (KeyValuePair<SV, CheckpointLowWatermark> rollback in this.rollbackFrontiers)
            {
                if (!rollback.Value.frontier.Complete)
                {
                    this.InjectChangesFromComputedUpdate(rollback.Key, new FTFrontier(false), rollback.Value.frontier, false,
                        checkpointChanges, notificationChanges, deliveredMessageChanges, discardedMessageChanges, null);
                }
            }

            this.checkpointStream.OnNext(checkpointChanges);
            this.deliveredNotifications.OnNext(notificationChanges);
            this.deliveredMessages.OnNext(deliveredMessageChanges);
            this.discardedMessages.OnNext(discardedMessageChanges);

            ++this.epoch;
        }

        private long numberOfUpdates = 0;
        private long numberOfNotifications = 0;
        private long numberOfDelivered = 0;
        private long numberOfDiscarded = 0;
        private long nextLog = 0;

        private void GetUpdate(object o, Diagnostics.CheckpointPersistedEventArgs args)
        {
            CheckpointUpdate update = args.checkpoint;

            if (this.logLevel != LogLevel.Minimal)
            {
                this.WriteLog(update.stageId + "." + update.vertexId + " " + update.frontier + " " + (update.isTemporary ? "T" : "U"));
            }

            lock (this)
            {
                ++this.numberOfUpdates;
                if (this.stopwatch.ElapsedMilliseconds > nextLog)
                {
                    this.WriteLog("UPDATES " + this.numberOfUpdates + " " + this.numberOfNotifications + " " + this.numberOfDelivered + " " + this.numberOfDiscarded);
                    nextLog = this.stopwatch.ElapsedMilliseconds + 1000;
                }
                if (update.isTemporary)
                {
                    if (!(this.state == State.PreparingForRollback || this.state == State.DrainingForRollback))
                    {
                        throw new ApplicationException("Got temporary update in state " + this.state);
                    }

                    this.temporaryUpdates.Add(update);
                    return;
                }

                if (!(this.state == State.Incremental || this.state == State.PreparingForRollback))
                {
                    throw new ApplicationException("Got update in state " + this.state);
                }

                if (this.pendingUpdates == null)
                {
                    // there is no computation in progress, so we're going to start one

                    // make a list that subsequent updates will be queued in while the new computation is ongoing
                    this.pendingUpdates = new List<CheckpointUpdate>();
                }
                else
                {
                    this.pendingUpdates.Add(update);
                    return;
                }
            }

            // if we got this far, start a new computation with a single update
            this.InjectUpdates(new CheckpointUpdate[] { update }, null, true);
        }

        /// <summary>
        /// Called while lock is held!!!
        /// </summary>
        /// <param name="changes">updates computed for temporary rollback</param>
        private void DealWithComputedRollbackFrontiers(IEnumerable<Weighted<Frontier>> changes)
        {
            // we just computed the necessary frontiers

            // fill in the low watermark for everyone first
            foreach (var state in this.nodeState)
            {
                if (this.logLevel == LogLevel.Verbose)
                {
                    this.WriteLog(state.Key.StageId(this) + "." + state.Key.VertexId + " " + state.Value.currentRestoration + " LW");
                }
                this.rollbackFrontiers.Add(state.Key, new CheckpointLowWatermark
                    {
                        stageId = state.Key.StageId(this),
                        vertexId = state.Key.VertexId,
                        managerVertex = state.Value.gcUpdateSendVertexId,
                        frontier = state.Value.currentRestoration,
                        dstStageId = -2,
                        dstVertexId = -2
                    });
            }

            List<Pair<SV, FTFrontier>> rollbacks = new List<Pair<SV, FTFrontier>>();

            foreach (var change in changes.Where(c => !c.record.isNotification))
            {
                CheckpointLowWatermark current = this.rollbackFrontiers[change.record.node];

                if (change.weight == 1)
                {
                    FTFrontier f = change.record.ToFrontier(this);
                    if (f.Equals(current.frontier) || current.frontier.Contains(f))
                    {
                        throw new ApplicationException("Rollback below low watermark");
                    }

                    current.frontier = f;
                    this.rollbackFrontiers[change.record.node] = current;
                    if (this.logLevel == LogLevel.Verbose)
                    {
                        this.WriteLog(change.record.node.StageId(this) + "." + change.record.node.VertexId + " " + current.frontier + " RB");
                    }
                }
                else if (change.weight == -1)
                {
                    if (!change.record.ToFrontier(this).Equals(this.nodeState[change.record.node].currentRestoration))
                    {
                        throw new ApplicationException("Rollback doesn't match state");
                    }
                }
                else
                {
                    throw new ApplicationException("Rollback has weight " + change.weight);
                }

            }

            // now revert the temporary updates and discard any state and deltas that have been invalidated
            // by the rollback
            this.InjectRollbackUpdates(this.temporaryUpdates);
        }

        /// <summary>
        /// Called while lock is held!!!
        /// </summary>
        /// <param name="changes">updates computed for temporary rollback</param>
        private void CleanUpAfterRollback(IEnumerable<Weighted<Frontier>> changes)
        {
            foreach (var change in changes.Where(c => c.weight > 0))
            {
                if (change.weight != 1)
                {
                    throw new ApplicationException("Rollback has weight " + change.weight);
                }

                NodeState current = this.nodeState[change.record.node];

                if (change.record.isNotification && !change.record.ToFrontier(this).Equals(current.currentNotification))
                {
                    throw new ApplicationException("Bad rollback reversion");
                }
                if (!change.record.isNotification && !change.record.ToFrontier(this).Equals(current.currentRestoration))
                {
                    throw new ApplicationException("Bad rollback reversion");
                }
            }

            // tell the rollback thread we are ready to proceed
            this.quiescenceBarrier.Set();
            this.quiescenceBarrier = null;
        }

        private void ReactToFrontiers(IEnumerable<Weighted<Frontier>> changes)
        {
            this.WriteLog("COMPLETE");

            while (true)
            {
                List<CheckpointUpdate> queuedUpdates = new List<CheckpointUpdate>();
                State currentState;

                lock (this)
                {
                    currentState = this.state;

                    switch (currentState)
                    {
                        case State.Incremental:
                        case State.PreparingForRollback:
                            if (changes == null && this.pendingUpdates.Count == 0)
                            {
                                // there's nothing more to do, so indicate that there is no computation in progress
                                this.pendingUpdates = null;
                                return;
                            }
                            else
                            {
                                // get hold of any updates that were sent in while we were computing
                                queuedUpdates = this.pendingUpdates;
                                // make sure subsequent updates continue to get queued
                                this.pendingUpdates = new List<CheckpointUpdate>();
                            }
                            break;

                        case State.DrainingForRollback:
                            if (changes == null && this.pendingUpdates.Count == 0)
                            {
                                // there's nothing more to do, so start the rollback computation
                                this.WriteLog("START ROLLBACK");
                                this.state = State.AddedTemporaryForRollback;
                                this.pendingUpdates = null;
                                queuedUpdates = this.temporaryUpdates;
                            }
                            else
                            {
                                // get hold of any updates that were sent in while we were computing
                                queuedUpdates = this.pendingUpdates;
                                // make sure subsequent updates continue to get queued
                                this.pendingUpdates = new List<CheckpointUpdate>();
                            }
                            break;

                        case State.DrainingForExit:
                            // no point in continuing to update things since we are exiting
                            this.computation.ReceiveCheckpointUpdates(null);
                            this.computation = null;
                            return;

                        case State.Stopping:
                            // no point in continuing to update things since we are exiting
                            this.quiescenceBarrier.Set();
                            this.quiescenceBarrier = null;
                            return;

                        case State.AddedTemporaryForRollback:
                            if (this.pendingUpdates!= null)
                            {
                                throw new ApplicationException("New updates during rollback");
                            }

                            this.WriteLog("START REVOKING");
                            this.state = State.RevokingTemporaryForRollback;
                            queuedUpdates = this.temporaryUpdates;
                            break;

                        case State.RevokingTemporaryForRollback:
                            if (this.pendingUpdates!= null)
                            {
                                throw new ApplicationException("New updates during rollback reversion");
                            }

                            this.WriteLog("FINISHED REVOKING");
                            this.state = State.Incremental;
                            break;
                    }
                }

                switch (currentState)
                {
                    case State.Incremental:
                    case State.PreparingForRollback:
                    case State.DrainingForRollback:
                        if (this.InjectUpdates(queuedUpdates, changes, currentState != State.DrainingForRollback))
                        {
                            // we started a new computation, so we don't need to do any more here
                            return;
                        }

                        // we didn't start a new computation so go around the loop in case somebody added a new pending
                        // update in the meantime
                        changes = null;
                        break;

                    case State.AddedTemporaryForRollback:
                        this.DealWithComputedRollbackFrontiers(changes);
                        return;

                    case State.RevokingTemporaryForRollback:
                        this.CleanUpAfterRollback(changes);
                        return;

                    case State.DrainingForExit:
                    case State.Stopping:
                        throw new ApplicationException("Bad case " + currentState);
                }
            }
        }

        private void ShowRollback()
        {
            foreach (var state in this.rollbackFrontiers.OrderBy(s => s.Key.denseId))
            {
                Console.WriteLine(this.denseStages[state.Key.DenseStageId] + "[" + state.Key.VertexId + "] " +
                    state.Value.frontier);
            }
        }

        private void ShowState(bool fullState)
        {
            foreach (var state in this.nodeState.OrderBy(s => s.Key.denseId))
            {
                Console.WriteLine(this.denseStages[state.Key.DenseStageId] + "[" + state.Key.VertexId + "] " +
                    state.Value.currentRestoration + "; " + state.Value.currentNotification);

                if (fullState)
                {
                    Console.Write(" ");
                    foreach (var checkpoint in state.Value.checkpoints.OrderBy(c => c))
                    {
                        Console.Write(" " + checkpoint);
                    }
                    Console.WriteLine();

                    Console.Write(" ");
                    foreach (var time in state.Value.deliveredNotifications.OrderBy(t => new LexStamp(t)))
                    {
                        Console.Write(" " + time.Timestamp);
                    }
                    Console.WriteLine();

                    Console.Write(" ");
                    foreach (var time in state.Value.deliveredMessages.OrderBy(t => t.Key))
                    {
                        Console.Write(" " + time.Key + ":");
                        foreach (var src in time.Value)
                        {
                            Console.Write(" " + src);
                        }
                        Console.Write(";");
                    }
                    Console.WriteLine();

                    Console.Write(" ");
                    foreach (var stage in state.Value.discardedMessages.OrderBy(t => t.Key))
                    {
                        Console.Write(" " + stage.Key + ":");
                        foreach (var dst in stage.Value.messages)
                        {
                            foreach (var src in dst.Second)
                            {
                                Console.Write(" " + dst.First.Timestamp + "=" + src.Timestamp);
                            }
                        }
                        Console.Write(";");
                    }
                    Console.WriteLine();
                }
            }
        }

        private void Manage(ManualResetEventSlim startBarrier, ManualResetEventSlim stopBarrier, int workerCount)
        {
            Configuration config = new Configuration();
            config.MaxLatticeInternStaleTimes = 10;
            config.WorkerCount = workerCount;

            using (Computation reconciliation = NewComputation.FromConfig(config))
            {
                this.graph = reconciliation.NewInputCollection<Edge>();
                this.checkpointStream = reconciliation.NewInputCollection<Checkpoint>();
                this.deliveredMessages = reconciliation.NewInputCollection<DeliveredMessage>();
                this.deliveredNotifications = reconciliation.NewInputCollection<Notification>();
                this.discardedMessages = reconciliation.NewInputCollection<DiscardedMessage>();

                Collection<Frontier, Epoch> initial = this.checkpointStream
                    .Max(c => c.node.denseId, c => c.checkpoint.value)
                    .SelectMany(c => new Frontier[] {
                    new Frontier(c.node, c.checkpoint, false),
                    new Frontier(c.node, c.checkpoint, true) });

                var frontiers = initial
                    .FixedPoint((c, f) =>
                        {
                            var reducedDiscards = f
                                .ReduceForDiscarded(
                                    this.checkpointStream.EnterLoop(c), this.discardedMessages.EnterLoop(c), this);

                            var reduced = f
                                .Reduce(
                                    this.checkpointStream.EnterLoop(c), this.deliveredMessages.EnterLoop(c),
                                    this.deliveredNotifications.EnterLoop(c), this.graph.EnterLoop(c),
                                    this);

                            return reduced.Concat(reducedDiscards).Concat(f)
                                .Min(ff => (ff.node.denseId + (ff.isNotification ? 0x10000 : 0)), ff => ff.frontier.value);
                        })
                    .Consolidate();

                var sync = frontiers.Subscribe(changes => ReactToFrontiers(changes));

                reconciliation.Activate();

                startBarrier.Set();

                // the streams will now be fed by other threads until the computation exits

                stopBarrier.Wait();

                ManualResetEventSlim finalBarrier = null;
                lock (this)
                {
                    if (this.pendingUpdates != null)
                    {
                        // there is a computation running
                        this.state = State.Stopping;
                        this.quiescenceBarrier = new ManualResetEventSlim(false);
                        finalBarrier = this.quiescenceBarrier;
                    }
                }

                if (finalBarrier != null)
                {
                    finalBarrier.Wait();
                }

                this.checkpointStream.OnCompleted();
                this.deliveredMessages.OnCompleted();
                this.deliveredNotifications.OnCompleted();
                this.discardedMessages.OnCompleted();

                reconciliation.Join();
            }

            this.ShowState(false);
        }

        public void NotifyComputationExiting()
        {
            lock (this)
            {
                if (this.pendingUpdates == null)
                {
                    // there is no computation running, so shut down the update input
                    this.computation.ReceiveCheckpointUpdates(null);
                    this.computation = null;
                }
                else
                {
                    // there is a computation running, so get it to shut down the update input when it completes
                    this.state = State.DrainingForExit;
                }
            }
        }

        private void ComputeRollback()
        {
            this.WriteLog("COMPUTATION START ROLLBACK");
            this.computation.StartRollback(this.WriteLog);
            this.WriteLog("COMPUTATION STARTED ROLLBACK");

            // once we get here, everybody should have stopped sending any updates though there may
            // still be a final computation going on

            ManualResetEventSlim barrier = new ManualResetEventSlim();

            bool mustStart = false;

            lock (this)
            {
                if (this.temporaryUpdates.Count == 0)
                {
                    throw new ApplicationException("No temporary updates received");
                }
                if (this.pendingUpdates == null)
                {
                    // there is no computation, so we have to start it ourselves
                    this.state = State.AddedTemporaryForRollback;
                    mustStart = true;
                }
                else
                {
                    // there is a computation going on: tell it to start the rollback when it finishes
                    this.state = State.DrainingForRollback;
                }

                // the machinery will now turn over until everything is computed
                this.rollbackFrontiers = new Dictionary<SV, CheckpointLowWatermark>();
                this.quiescenceBarrier = barrier;
            }

            if (mustStart)
            {
                bool didAnything = this.InjectUpdates(this.temporaryUpdates, null, false);
                if (!didAnything)
                {
                    throw new ApplicationException("No temporary updates to inject");
                }
            }

            barrier.Wait();

            this.WriteLog("ROLLBACK COMPLETE");

            lock (this)
            {
                // we shouldn't get any more temporary updates
                this.temporaryUpdates = null;

                // open up for business doing incremental updates from the computation again
                this.state = State.Incremental;
            }
        }

        private Random random = new Random();
        private HashSet<int> failedProcesses = new HashSet<int>();
        private ManualResetEventSlim failureRestartEvent = null;

        public void FailProcess(HashSet<int> processes)
        {
            lock (this)
            {
                foreach (var processId in processes)
                {
                    if (this.failedProcesses.Contains(processId))
                    {
                        throw new ApplicationException("Failing process twice");
                    }

                    this.failedProcesses.Add(processId);
                }
            }

            foreach (var processId in processes)
            {
                int restartDelay = 2500 + this.random.Next(1000);

                Console.WriteLine("Sending failure request to " + processId + " delay " + restartDelay);
                this.computation.SimulateFailure(processId, restartDelay);
            }
        }

        public void OnSimulatedProcessRestart(object o, Diagnostics.ProcessRestartedEventArgs args)
        {
            int processId = args.processId;

            Console.WriteLine("Got process restart message from " + processId);

            lock (this)
            {
                if (!this.failedProcesses.Contains(processId))
                {
                    throw new ApplicationException("Non-failed process has restarted");
                }

                this.failedProcesses.Remove(processId);

                if (this.failedProcesses.Count == 0 && this.failureRestartEvent != null)
                {
                    this.failureRestartEvent.Set();
                    this.failureRestartEvent = null;
                }
            }
        }

        public void WaitForSimulatedFailures()
        {
            ManualResetEventSlim restartEvent = null;
            lock (this)
            {
                if (this.failedProcesses.Count > 0)
                {
                    this.failureRestartEvent = new ManualResetEventSlim(false);
                    restartEvent = this.failureRestartEvent;
                }
            }

            if (restartEvent != null)
            {
                Console.WriteLine("Waiting for failed processes to restart");
                restartEvent.Wait();
                restartEvent.Dispose();
                Console.WriteLine("Failed processes have restarted");
            }
        }

        public void PerformRollback(IEnumerable<int> pauseImmediately, IEnumerable<int> pauseAfterRecovery, IEnumerable<int> pauseLast)
        {
            lock (this)
            {
                this.state = State.PreparingForRollback;
                this.temporaryUpdates = new List<CheckpointUpdate>();
                this.pendingGCUpdates = new List<CheckpointLowWatermark>();
            }

            this.computation.PausePeerProcesses(pauseImmediately);

            this.WaitForSimulatedFailures();

            this.computation.PausePeerProcesses(pauseAfterRecovery);
            this.computation.PausePeerProcesses(pauseLast);

            this.ComputeRollback();

            if (this.logLevel != LogLevel.Minimal)
            {
                this.ShowRollback();
            }

            this.WriteLog("SHOWN ROLLBACK");

            IEnumerable<CheckpointLowWatermark> frontiers;
            List<CheckpointLowWatermark> gcUpdates;
            Computation computation;

            lock (this)
            {
                frontiers = this.rollbackFrontiers.Values;
                gcUpdates = this.pendingGCUpdates;
                computation = this.computation;
                this.rollbackFrontiers = null;
                this.pendingGCUpdates = null;
            }

            this.WriteLog("SWAPPED ROLLBACK DATA");

            if (computation != null)
            {
                computation.RestoreToFrontiers(frontiers, this.WriteLog);
                this.WriteLog("RESTORED COMPUTATION");
                if (gcUpdates.Count > 0)
                {
                    computation.ReceiveCheckpointUpdates(gcUpdates);
                }
            }

            this.WriteLog("FINISHED ROLLBACK TASKS");
        }

        /// <summary>
        /// Start monitoring the checkpoints for Naiad computation <paramref name="computation"/>
        /// </summary>
        /// <param name="computation">the computation to be managed</param>
        public void Initialize(Computation computation, IEnumerable<int> stagesToMonitor, int workerCount, bool minimalLogging)
        {
            this.computation = computation;
            this.stopwatch = computation.Controller.Stopwatch;
            if (minimalLogging)
            {
                this.logLevel = LogLevel.Minimal;
            }

            foreach (int stage in stagesToMonitor)
            {
                this.stagesToMonitor.Add(stage);
            }

            ManualResetEventSlim startBarrier = new ManualResetEventSlim(false);
            ManualResetEventSlim stopBarrier = new ManualResetEventSlim(false);

            this.managerThread = new Thread(() => this.Manage(startBarrier, stopBarrier, workerCount));
            this.managerThread.Start();

            // wait for the manager to initialize
            startBarrier.Wait();

            this.computation.OnMaterialized += this.GetGraph;
            this.computation.OnCheckpointPersisted += this.GetUpdate;
            this.computation.OnProcessRestarted += this.OnSimulatedProcessRestart;
            this.computation.OnShutdown += (o, a) => stopBarrier.Set();
        }

        /// <summary>
        /// wait for the manager thread to exit
        /// </summary>
        public void Join()
        {
            this.managerThread.Join();
            this.managerThread = null;
        }
    }
}
