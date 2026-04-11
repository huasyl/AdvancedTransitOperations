using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void InvalidateTrackModel(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_DirtyTrackLines.Add(line);
            m_LineTrackChainFrameSnapshots.Remove(line);
            m_LineWaypointIndexLookups.Remove(line);
            if (m_LineTrackChains.TryGetValue(line, out LineTrackChain existingChain) && existingChain != null)
                RemoveDevSightLaneIndexForChain(existingChain);
            m_LineTrackChains.Remove(line);
            ClearBypassRuntimeStateForLine(line);
            m_SharedTrackIndexDirty = true;
        }

        private void InvalidateAllTrackModels()
        {
            m_DirtyTrackLines.Clear();
            m_LineTrackChains.Clear();
            m_LineTrackChainFrameSnapshots.Clear();
            m_LineWaypointIndexLookups.Clear();
            m_DevSightLaneIndex.Clear();
            m_SharedTrackIndex.Clear();
            m_SharedPhysicalTrackIndex.Clear();
            m_VehicleTrackCursorHints.Clear();
            m_VehicleTrackCursorFrameSnapshots.Clear();
            m_SuspectProgressSinceFrame.Clear();
            m_SuspectProgressLastValidationFrame.Clear();
            m_SuspectProgressProjectionInvalid.Clear();
            m_SuspectProgressReason.Clear();
            m_SuspectProgressLogCache.Clear();
            m_SuspectProgressRecoveryWaypoint.Clear();
            m_SuspectProgressValidationCount.Clear();
            m_SuspectProgressFirstSample.Clear();
            ClearBypassTrackModelRuntimeState();
            m_SharedTrackIndexDirty = true;
        }

        private void ClearBypassTrackModelRuntimeState()
        {
            m_BypassTrackModelDecisionLogCache.Clear();
            m_BypassTrackModelDecisionThrottleCache.Clear();
            m_BypassTrackModelDecisionLastLogFrame.Clear();
            m_BypassTrackModelCompareLogCache.Clear();
            m_BypassTrackModelCompareThrottleCache.Clear();
            m_BypassTrackModelCompareLastLogFrame.Clear();
            m_BypassSelectedBlockerDetailLogCache.Clear();
            m_BypassSelectedBlockerDetailLastLogFrame.Clear();
            m_TrainLaneSourceDiagnosticLogCache.Clear();
            m_SameStationMissLogCache.Clear();
            m_SharedWindowAuditSummaryLogCache.Clear();
            m_SharedWindowAuditThrottleCache.Clear();
            m_SharedWindowAuditLastLogFrame.Clear();
            m_SharedWindowAuditPairStateCache.Clear();
            m_SharedWindowMatchSnapshots.Clear();
            m_LocalBypassSceneStaticSnapshots.Clear();
            m_GlobalSharedTrunkSnapshots.Clear();
            m_ProtectedIntervalPairMetricsSnapshots.Clear();
            m_LocalSceneExpressStaticMatchSnapshots.Clear();
            m_LocalSceneCandidateExpressLinesSnapshots.Clear();
            m_ActiveConflictCorridorSnapshots.Clear();
            m_ActiveConflictCorridorSnapshotFrame = 0;
            m_TrackModelSequenceLogCache.Clear();
            m_BypassTrackModelDecisionSnapshots.Clear();
            m_LineOrderedRuntimeStates.Clear();
            m_LineOrderedRuntimeForceRefreshReasons.Clear();
            m_LineBypassExecutionModeLogCache.Clear();
            m_LineOrderedRuntimeLogCache.Clear();
            m_LineOrderedFallbackCaseLogCache.Clear();
            m_LineOrderedFallbackCaseLastLogFrame.Clear();
            m_TrackModelTurnbackBuildLogCache.Clear();
            m_TrackModelTurnbackSignalLogCache.Clear();
            m_TrackModelTurnbackSignalLastLogFrame.Clear();
            m_TrackModelTurnbackLearnLogCache.Clear();
            m_TrackModelTurnbackLearnLastLogFrame.Clear();
            m_LearnedTurnbackBoundaryClustersByLine.Clear();
            m_TurnbackLearnVehicleStates.Clear();
        }

        private void ClearBypassTrackModelRuntimeStateForLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<SharedWindowMatchCacheKey> sharedWindowKeysToRemove = null;
            foreach (KeyValuePair<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> entry in m_SharedWindowMatchSnapshots)
            {
                SharedWindowMatchCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                sharedWindowKeysToRemove ??= new List<SharedWindowMatchCacheKey>();
                sharedWindowKeysToRemove.Add(key);
            }

            if (sharedWindowKeysToRemove != null)
            {
                for (int i = 0; i < sharedWindowKeysToRemove.Count; i++)
                    m_SharedWindowMatchSnapshots.Remove(sharedWindowKeysToRemove[i]);
            }

            List<LocalBypassSceneStaticKey> localSceneKeysToRemove = null;
            foreach (KeyValuePair<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot> entry in m_LocalBypassSceneStaticSnapshots)
            {
                if (entry.Key.Line != line)
                    continue;

                localSceneKeysToRemove ??= new List<LocalBypassSceneStaticKey>();
                localSceneKeysToRemove.Add(entry.Key);
            }

            if (localSceneKeysToRemove != null)
            {
                for (int i = 0; i < localSceneKeysToRemove.Count; i++)
                    m_LocalBypassSceneStaticSnapshots.Remove(localSceneKeysToRemove[i]);
            }

            List<GlobalSharedTrunkCacheKey> trunkKeysToRemove = null;
            foreach (KeyValuePair<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> entry in m_GlobalSharedTrunkSnapshots)
            {
                GlobalSharedTrunkCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                trunkKeysToRemove ??= new List<GlobalSharedTrunkCacheKey>();
                trunkKeysToRemove.Add(key);
            }

            if (trunkKeysToRemove != null)
            {
                for (int i = 0; i < trunkKeysToRemove.Count; i++)
                    m_GlobalSharedTrunkSnapshots.Remove(trunkKeysToRemove[i]);
            }

            List<ProtectedIntervalPairMetricsCacheKey> pairMetricKeysToRemove = null;
            foreach (KeyValuePair<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> entry in m_ProtectedIntervalPairMetricsSnapshots)
            {
                ProtectedIntervalPairMetricsCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                pairMetricKeysToRemove ??= new List<ProtectedIntervalPairMetricsCacheKey>();
                pairMetricKeysToRemove.Add(key);
            }

            if (pairMetricKeysToRemove != null)
            {
                for (int i = 0; i < pairMetricKeysToRemove.Count; i++)
                    m_ProtectedIntervalPairMetricsSnapshots.Remove(pairMetricKeysToRemove[i]);
            }

            m_LocalSceneExpressStaticMatchSnapshots.Clear();
            m_LocalSceneCandidateExpressLinesSnapshots.Clear();

            List<ActiveConflictCorridorCacheKey> activeCorridorKeysToRemove = null;
            foreach (KeyValuePair<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot> entry in m_ActiveConflictCorridorSnapshots)
            {
                ActiveConflictCorridorCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                activeCorridorKeysToRemove ??= new List<ActiveConflictCorridorCacheKey>();
                activeCorridorKeysToRemove.Add(key);
            }

            if (activeCorridorKeysToRemove != null)
            {
                for (int i = 0; i < activeCorridorKeysToRemove.Count; i++)
                    m_ActiveConflictCorridorSnapshots.Remove(activeCorridorKeysToRemove[i]);
            }

            List<Entity> decisionSnapshotKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassTrackModelDecisionSnapshot> entry in m_BypassTrackModelDecisionSnapshots)
            {
                if (entry.Value.Line != line)
                    continue;

                decisionSnapshotKeysToRemove ??= new List<Entity>();
                decisionSnapshotKeysToRemove.Add(entry.Key);
            }

            if (decisionSnapshotKeysToRemove != null)
            {
                for (int i = 0; i < decisionSnapshotKeysToRemove.Count; i++)
                {
                    Entity vehicle = decisionSnapshotKeysToRemove[i];
                    m_BypassTrackModelDecisionSnapshots.Remove(vehicle);
                    m_BypassTrackModelDecisionLogCache.Remove(vehicle);
                    m_BypassTrackModelDecisionThrottleCache.Remove(vehicle);
                    m_BypassTrackModelDecisionLastLogFrame.Remove(vehicle);
                    m_BypassSelectedBlockerDetailLogCache.Remove(vehicle);
                    m_BypassSelectedBlockerDetailLastLogFrame.Remove(vehicle);
                    m_TrainLaneSourceDiagnosticLogCache.Remove(vehicle);
                    m_SameStationMissLogCache.Remove(vehicle);
                    m_BypassTrackModelCompareLogCache.Remove(vehicle);
                    m_BypassTrackModelCompareThrottleCache.Remove(vehicle);
                    m_BypassTrackModelCompareLastLogFrame.Remove(vehicle);
                    m_SharedWindowAuditSummaryLogCache.Remove(vehicle);
                    m_SharedWindowAuditThrottleCache.Remove(vehicle);
                    m_SharedWindowAuditLastLogFrame.Remove(vehicle);
                    List<SharedWindowPairStateKey> sharedPairKeysToRemove = null;
                    foreach (KeyValuePair<SharedWindowPairStateKey, string> pairEntry in m_SharedWindowAuditPairStateCache)
                    {
                        if (pairEntry.Key.LocalVehicle != vehicle)
                            continue;

                        sharedPairKeysToRemove ??= new List<SharedWindowPairStateKey>();
                        sharedPairKeysToRemove.Add(pairEntry.Key);
                    }

                    if (sharedPairKeysToRemove != null)
                    {
                        for (int removeIndex = 0; removeIndex < sharedPairKeysToRemove.Count; removeIndex++)
                            m_SharedWindowAuditPairStateCache.Remove(sharedPairKeysToRemove[removeIndex]);
                    }

                    m_TrackModelSequenceLogCache.Remove(vehicle);
                }
            }

            m_LineOrderedRuntimeStates.Remove(line);
            m_LineOrderedRuntimeForceRefreshReasons.Remove(line);
            m_LineBypassExecutionModeLogCache.Remove(line);
            m_LineOrderedRuntimeLogCache.Remove(line);
            m_LineOrderedFallbackCaseLogCache.Remove(line);
            m_LineOrderedFallbackCaseLastLogFrame.Remove(line);
            m_TrackModelTurnbackBuildLogCache.Remove(line);
            m_TrackModelTurnbackSignalLogCache.Remove(line);
            m_TrackModelTurnbackSignalLastLogFrame.Remove(line);
            m_TrackModelTurnbackLearnLogCache.Remove(line);
            m_TrackModelTurnbackLearnLastLogFrame.Remove(line);
            m_LearnedTurnbackBoundaryClustersByLine.Remove(line);

            List<Entity> turnbackLearnVehiclesToRemove = null;
            foreach (KeyValuePair<Entity, TurnbackLearnVehicleSampleState> entry in m_TurnbackLearnVehicleStates)
            {
                TurnbackLearnVehicleSampleState state = entry.Value;
                if (state == null || state.Line != line)
                    continue;

                turnbackLearnVehiclesToRemove ??= new List<Entity>();
                turnbackLearnVehiclesToRemove.Add(entry.Key);
            }

            if (turnbackLearnVehiclesToRemove != null)
            {
                for (int i = 0; i < turnbackLearnVehiclesToRemove.Count; i++)
                    m_TurnbackLearnVehicleStates.Remove(turnbackLearnVehiclesToRemove[i]);
            }
        }

        private ulong ComputeLineTrackChainSignature(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, line.Index);
            hash = MixLineSignature(hash, waypoints.Length);
            hash = MixLineSignature(hash, segments.Length);

            for (int i = 0; i < waypoints.Length; i++)
                hash = MixLineSignature(hash, waypoints[i].m_Waypoint.Index);

            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                hash = MixLineSignature(hash, segmentEntity.Index);

                if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
                    continue;

                DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                hash = MixLineSignature(hash, pathElements.Length);
                for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
                {
                    PathElement pathElement = pathElements[pathIndex];
                    hash = MixLineSignature(hash, pathElement.m_Target.Index);
                    hash = MixLineSignature(hash, (int)pathElement.m_Flags);
                }
            }

            return hash;
        }

        private bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null
                || waypoints.Length == 0
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                return false;
            }

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_LineTrackChainFrameSnapshots.TryGetValue(line, out LineTrackChainFrameSnapshot frameSnapshot)
                && frameSnapshot.Frame == nowFrame
                && frameSnapshot.WaypointCount == waypoints.Length)
            {
                chain = frameSnapshot.Chain;
                return frameSnapshot.Available;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
            {
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null);
                return false;
            }

            ulong signature = ComputeLineTrackChainSignature(line, waypoints, segments);
            LineTrackChain previousChain = null;
            if (m_LineTrackChains.TryGetValue(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                bool available = chain.TrackAtoms.Count > 0;
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    available,
                    available ? chain : null);
                return available;
            }

            previousChain = chain;

            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
            {
                m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                    nowFrame,
                    waypoints.Length,
                    false,
                    null);
                return false;
            }

            if (previousChain != null)
                RemoveDevSightLaneIndexForChain(previousChain);
            m_LineTrackChains[line] = chain;
            m_LineTrackChainFrameSnapshots[line] = new LineTrackChainFrameSnapshot(
                nowFrame,
                waypoints.Length,
                true,
                chain);
            AddDevSightLaneIndexForChain(chain);
            m_DirtyTrackLines.Remove(line);
            m_SharedTrackIndexDirty = true;
            return true;
        }

        private LineTrackChain BuildLineTrackChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature)
        {
            var chain = new LineTrackChain
            {
                LineEntity = line,
                Signature = signature
            };

            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int startAtomIndex = chain.TrackAtoms.Count;
                Entity segmentEntity = segments[waypointIndex].m_Segment;
                if (EntityManager.HasBuffer<PathElement>(segmentEntity))
                {
                    DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                    AppendSegmentTrackAtoms(chain.TrackAtoms, pathElements);
                }

                int endAtomIndexExclusive = chain.TrackAtoms.Count;
                chain.SegmentRanges.Add(new TrackSegmentRange(startAtomIndex, endAtomIndexExclusive));
                TryAppendControlPoint(chain.ControlPoints, waypoints, waypointIndex, startAtomIndex);
            }

            BuildControlEdges(chain, line, waypoints);
            BuildTraversalProfile(chain, line, waypoints);
            BuildTurnbackBoundaries(chain, line, waypoints);
            LogTrackModelTurnbackBuild(chain);
            BuildAtomIndicesByLane(chain);
            return chain;
        }

        private bool TryClassifyTrackAtom(
            DynamicBuffer<PathElement> pathElements,
            int pathIndex,
            out TrackAtom atom)
        {
            atom = default;
            if (pathIndex < 0 || pathIndex >= pathElements.Length)
                return false;

            PathElement element = pathElements[pathIndex];
            if (element.m_Target == Entity.Null)
                return false;

            TrackAtomClass atomClass = ClassifyPathElementTarget(element);
            TrackTraversalDir traversalDir = ResolveTraversalDirection(pathElements, pathIndex);
            Entity previousTarget = pathIndex > 0 ? pathElements[pathIndex - 1].m_Target : Entity.Null;
            Entity nextTarget = pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target : Entity.Null;
            TrackAtomKey key = new TrackAtomKey(element.m_Target, previousTarget, nextTarget);
            atom = new TrackAtom(key, element.m_Target, element.m_Flags, atomClass, traversalDir);
            return true;
        }

        private TrackAtomClass ClassifyPathElementTarget(PathElement element)
        {
            if ((element.m_Flags & (PathElementFlags.Action | PathElementFlags.WaitPosition | PathElementFlags.Hangaround)) != 0)
                return TrackAtomClass.FilteredNoise;

            if (EntityManager.HasComponent<TrackLane>(element.m_Target))
                return TrackAtomClass.PrimaryLane;

            if (EntityManager.HasComponent<ConnectionLane>(element.m_Target))
            {
                ConnectionLane connectionLane = EntityManager.GetComponentData<ConnectionLane>(element.m_Target);
                if (connectionLane.m_TrackTypes != TrackTypes.None)
                    return TrackAtomClass.ConnectionHelper;
            }

            if (EntityManager.HasComponent<EdgeLane>(element.m_Target))
                return TrackAtomClass.ConnectionHelper;

            if ((element.m_Flags & (PathElementFlags.Secondary | PathElementFlags.Return | PathElementFlags.Leader)) != 0)
                return TrackAtomClass.ConnectionHelper;

            return TrackAtomClass.PrimaryLane;
        }

        private TrackTraversalDir ResolveTraversalDirection(DynamicBuffer<PathElement> pathElements, int pathIndex)
        {
            PathElement current = pathElements[pathIndex];
            if (current.m_Target == Entity.Null)
                return TrackTraversalDir.Unknown;

            bool reverseFlag = (current.m_Flags & PathElementFlags.Reverse) != 0;
            if (EntityManager.HasComponent<EdgeLane>(current.m_Target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(current.m_Target);
                bool edgeForward = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                if (EntityManager.HasComponent<TrackLane>(current.m_Target))
                {
                    TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                    if ((trackLane.m_Flags & TrackLaneFlags.Invert) != 0)
                        edgeForward = !edgeForward;
                }

                if (reverseFlag)
                    edgeForward = !edgeForward;

                return edgeForward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (EntityManager.HasComponent<TrackLane>(current.m_Target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                bool forward = (trackLane.m_Flags & TrackLaneFlags.Invert) == 0;
                if (reverseFlag)
                    forward = !forward;
                return forward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (pathElements.Length <= 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            float laneProgress = current.m_TargetDelta.y - current.m_TargetDelta.x;
            if (math.abs(laneProgress) > 0.0001f)
            {
                bool forwardByDelta = laneProgress >= 0f;
                if (reverseFlag)
                    forwardByDelta = !forwardByDelta;
                return forwardByDelta ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (pathIndex == 0 || pathIndex == pathElements.Length - 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            PathElement previous = pathElements[pathIndex - 1];
            PathElement next = pathElements[pathIndex + 1];
            if (previous.m_Target != Entity.Null && previous.m_Target == next.m_Target)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Forward;
        }

        private void TryAppendControlPoint(
            List<ControlPointMarker> controlPoints,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            int atomIndex)
        {
            Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
            if (building == Entity.Null)
                return;

            ControlPointKind kind = ControlPointKind.Stop;
            if (IsBypassStation(building))
                kind = ControlPointKind.Bypass;

            controlPoints.Add(new ControlPointMarker(atomIndex, waypointIndex, building, kind));
        }

        private void BuildControlEdges(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain.ControlPoints.Count < 2)
                return;

            float lineFrames = GetLineLoopFramesEstimate(line, waypoints);
            int atomCount = math.max(1, chain.TrackAtoms.Count);
            bool hasProfile = TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile);

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count - 1; controlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[controlPointIndex];
                ControlPointMarker end = chain.ControlPoints[controlPointIndex + 1];
                int startAtomIndex = math.clamp(start.AtomIndex, 0, atomCount - 1);
                int endAtomIndexExclusive = math.clamp(math.max(startAtomIndex + 1, end.AtomIndex), 1, atomCount);
                float baseFrames = 0f;
                if (hasProfile)
                {
                    int startWaypointIndex = math.clamp(start.WaypointIndex, 0, waypoints.Length - 1);
                    int endWaypointIndex = math.clamp(end.WaypointIndex, 0, waypoints.Length - 1);
                    baseFrames = ComputeDepartureToWaypointFramesFromProfile(profile, startWaypointIndex, endWaypointIndex);
                }

                if (!(baseFrames > 0f))
                {
                    float ratio = (endAtomIndexExclusive - startAtomIndex) / (float)atomCount;
                    baseFrames = lineFrames > 0f ? lineFrames * ratio : 0f;
                }

                chain.ControlEdges.Add(new ControlEdge(
                    controlPointIndex,
                    controlPointIndex + 1,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }
        }

        private readonly struct StationPassRange
        {
            public readonly Entity Building;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly int WaypointIndex;
            public readonly float StopFrames;
            public readonly int PassIndex;

            public StationPassRange(
                Entity building,
                int startAtomIndex,
                int endAtomIndexExclusive,
                int waypointIndex,
                float stopFrames,
                int passIndex)
            {
                Building = building;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                WaypointIndex = waypointIndex;
                StopFrames = stopFrames;
                PassIndex = passIndex;
            }
        }

        private void BuildTraversalProfile(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null)
                return;

            chain.TraversalProfile.Events.Clear();
            chain.TraversalProfile.RunSlices.Clear();
            chain.TraversalProfile.AtomToRunSliceIndex = Array.Empty<int>();
            chain.TraversalProfile.SegmentSliceCutPointProgresses = Array.Empty<float[]>();
            if (chain.TrackAtoms.Count == 0)
                return;

            bool hasTimeProfile = TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader timeProfile);
            float lineFrames = GetLineLoopFramesEstimate(line, waypoints);
            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints);
            if (stationPasses.Count == 0)
                return;

            Dictionary<int, int> boundaryEventIndexByAtom = new Dictionary<int, int>();
            List<int> boundaries = new List<int> { 0, chain.TrackAtoms.Count };

            for (int passIndex = 0; passIndex < stationPasses.Count; passIndex++)
            {
                StationPassRange pass = stationPasses[passIndex];
                if (!boundaries.Contains(pass.StartAtomIndex))
                    boundaries.Add(pass.StartAtomIndex);
                if (!boundaries.Contains(pass.EndAtomIndexExclusive))
                    boundaries.Add(pass.EndAtomIndexExclusive);

                int approachIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    approachIndex,
                    TraversalEventKind.ApproachSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.StartAtomIndex,
                    0f));
                boundaryEventIndexByAtom[pass.StartAtomIndex] = approachIndex;

                int stationEventIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    stationEventIndex,
                    pass.WaypointIndex >= 0 ? TraversalEventKind.Stop : TraversalEventKind.Pass,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.EndAtomIndexExclusive,
                    pass.StopFrames));

                int departureIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    departureIndex,
                    TraversalEventKind.DepartureSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.EndAtomIndexExclusive,
                    pass.EndAtomIndexExclusive,
                    0f));
                boundaryEventIndexByAtom[pass.EndAtomIndexExclusive] = departureIndex;
            }

            boundaries.Sort();
            for (int boundaryIndex = 0; boundaryIndex < boundaries.Count - 1; boundaryIndex++)
            {
                int startAtomIndex = boundaries[boundaryIndex];
                int endAtomIndexExclusive = boundaries[boundaryIndex + 1];
                if (endAtomIndexExclusive <= startAtomIndex)
                    continue;

                int sliceIndex = chain.TraversalProfile.RunSlices.Count;
                boundaryEventIndexByAtom.TryGetValue(startAtomIndex, out int startEventIndex);
                boundaryEventIndexByAtom.TryGetValue(endAtomIndexExclusive, out int endEventIndex);
                chain.TraversalProfile.RunSlices.Add(new TraversalRunSlice(
                    sliceIndex,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    startEventIndex,
                    endEventIndex,
                    CollectTraversalSlicePhysicalLaneKeys(chain, startAtomIndex, endAtomIndexExclusive),
                    EstimateTraversalRunSliceFrames(
                        chain,
                        startAtomIndex,
                        endAtomIndexExclusive,
                        hasTimeProfile,
                        timeProfile,
                        lineFrames)));
            }

            int[] atomToRunSliceIndex = new int[chain.TrackAtoms.Count];
            for (int atomIndex = 0; atomIndex < atomToRunSliceIndex.Length; atomIndex++)
                atomToRunSliceIndex[atomIndex] = -1;
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                int startAtomIndex = math.clamp(slice.StartAtomIndex, 0, atomToRunSliceIndex.Length);
                int endAtomIndexExclusive = math.clamp(slice.EndAtomIndexExclusive, startAtomIndex, atomToRunSliceIndex.Length);
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                    atomToRunSliceIndex[atomIndex] = sliceIndex;
            }

            chain.TraversalProfile.AtomToRunSliceIndex = atomToRunSliceIndex;

            List<float>[] segmentCutPoints = new List<float>[chain.SegmentRanges.Count];
            void AddSegmentCutPoint(int boundaryAtomIndex)
            {
                if (boundaryAtomIndex < 0 || boundaryAtomIndex > chain.TrackAtoms.Count)
                    return;

                for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
                {
                    TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                    int segmentStartAtomIndex = segmentRange.StartAtomIndex;
                    int segmentEndAtomExclusive = segmentRange.EndAtomIndexExclusive;
                    if (segmentEndAtomExclusive <= segmentStartAtomIndex)
                        continue;

                    bool insideSegment = boundaryAtomIndex > segmentStartAtomIndex && boundaryAtomIndex < segmentEndAtomExclusive;
                    bool atSegmentStart = boundaryAtomIndex == segmentStartAtomIndex;
                    bool atSegmentEnd = boundaryAtomIndex == segmentEndAtomExclusive;
                    if (!insideSegment && !atSegmentStart && !atSegmentEnd)
                        continue;

                    float segmentLength = math.max(1f, segmentEndAtomExclusive - segmentStartAtomIndex);
                    float progress = math.saturate((boundaryAtomIndex - segmentStartAtomIndex) / (float)segmentLength);
                    segmentCutPoints[segmentIndex] ??= new List<float>();

                    bool duplicate = false;
                    for (int progressIndex = 0; progressIndex < segmentCutPoints[segmentIndex].Count; progressIndex++)
                    {
                        if (math.abs(segmentCutPoints[segmentIndex][progressIndex] - progress) <= 0.01f)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        segmentCutPoints[segmentIndex].Add(progress);
                }
            }

            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                AddSegmentCutPoint(slice.StartAtomIndex);
                AddSegmentCutPoint(slice.EndAtomIndexExclusive);
            }

            float[][] segmentSliceCutPointProgresses = new float[chain.SegmentRanges.Count][];
            for (int segmentIndex = 0; segmentIndex < segmentCutPoints.Length; segmentIndex++)
            {
                if (segmentCutPoints[segmentIndex] == null || segmentCutPoints[segmentIndex].Count == 0)
                {
                    segmentSliceCutPointProgresses[segmentIndex] = Array.Empty<float>();
                    continue;
                }

                segmentCutPoints[segmentIndex].Sort();
                segmentSliceCutPointProgresses[segmentIndex] = segmentCutPoints[segmentIndex].ToArray();
            }

            chain.TraversalProfile.SegmentSliceCutPointProgresses = segmentSliceCutPointProgresses;
        }

        private static bool HasOppositeTraversalDirection(TrackAtom previousAtom, TrackAtom currentAtom)
        {
            return previousAtom.TraversalDir != TrackTraversalDir.Unknown
                && currentAtom.TraversalDir != TrackTraversalDir.Unknown
                && previousAtom.TraversalDir != currentAtom.TraversalDir;
        }

        private static bool HasMirroredTraversalContext(TrackAtom previousAtom, TrackAtom currentAtom)
        {
            return previousAtom.Key.PreviousTarget != Entity.Null
                && previousAtom.Key.NextTarget != Entity.Null
                && currentAtom.Key.PreviousTarget != Entity.Null
                && currentAtom.Key.NextTarget != Entity.Null
                && previousAtom.Key.PreviousTarget == currentAtom.Key.NextTarget
                && previousAtom.Key.NextTarget == currentAtom.Key.PreviousTarget;
        }

        private static bool TryMatchReverseRepeatedPrimaryAtom(
            TrackAtom previousAtom,
            TrackAtom currentAtom,
            out bool strongContextMatch,
            out bool oppositeDirectionMatch)
        {
            strongContextMatch = false;
            oppositeDirectionMatch = false;

            if (previousAtom.AtomClass != TrackAtomClass.PrimaryLane
                || currentAtom.AtomClass != TrackAtomClass.PrimaryLane
                || previousAtom.Key.PhysicalLaneKey == Entity.Null
                || previousAtom.Key.PhysicalLaneKey != currentAtom.Key.PhysicalLaneKey)
            {
                return false;
            }

            strongContextMatch = HasMirroredTraversalContext(previousAtom, currentAtom);
            oppositeDirectionMatch = HasOppositeTraversalDirection(previousAtom, currentAtom);
            return strongContextMatch || oppositeDirectionMatch;
        }

        private bool TryMeasureReverseRepeatedPrimaryRun(
            LineTrackChain chain,
            List<int> primaryAtomIndices,
            int previousPrimaryPosition,
            int currentPrimaryPosition,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || primaryAtomIndices == null
                || previousPrimaryPosition < 0
                || currentPrimaryPosition < 0
                || previousPrimaryPosition >= primaryAtomIndices.Count
                || currentPrimaryPosition >= primaryAtomIndices.Count)
            {
                return false;
            }

            int strongContextMatchCount = 0;
            int oppositeDirectionMatchCount = 0;
            HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
            int leftPosition = previousPrimaryPosition;
            int rightPosition = currentPrimaryPosition;
            while (leftPosition >= 0 && rightPosition < primaryAtomIndices.Count)
            {
                TrackAtom previousAtom = chain.TrackAtoms[primaryAtomIndices[leftPosition]];
                TrackAtom currentAtom = chain.TrackAtoms[primaryAtomIndices[rightPosition]];
                if (!TryMatchReverseRepeatedPrimaryAtom(
                        previousAtom,
                        currentAtom,
                        out bool strongContextMatch,
                        out bool oppositeDirectionMatch))
                {
                    break;
                }

                matchedAtomCount++;
                if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                    matchedUniqueLaneCount++;
                if (strongContextMatch)
                    strongContextMatchCount++;
                if (oppositeDirectionMatch)
                    oppositeDirectionMatchCount++;

                leftPosition--;
                rightPosition++;
            }

            return matchedAtomCount >= TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                && matchedUniqueLaneCount >= TURNBACK_REPEAT_MIN_UNIQUE_LANES
                && (strongContextMatchCount > 0 || oppositeDirectionMatchCount >= 2);
        }

        private static List<int> CollectPrimaryAtomIndices(LineTrackChain chain)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return primaryAtomIndices;

            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private static List<int> CollectPrimaryAtomIndicesForRange(
            LineTrackChain chain,
            TrackSegmentRange range)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || range.EndAtomIndexExclusive <= range.StartAtomIndex)
            {
                return primaryAtomIndices;
            }

            int startAtomIndex = math.clamp(range.StartAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int endAtomIndexExclusive = math.clamp(range.EndAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);
            for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private bool TryMeasureAdjacentSegmentReverseOverlap(
            LineTrackChain chain,
            int leftSegmentIndex,
            int rightSegmentIndex,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || leftSegmentIndex < 0
                || rightSegmentIndex <= leftSegmentIndex
                || rightSegmentIndex >= chain.SegmentRanges.Count)
            {
                return false;
            }

            List<int> leftPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[leftSegmentIndex]);
            List<int> rightPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[rightSegmentIndex]);
            if (leftPrimaryAtomIndices.Count == 0 || rightPrimaryAtomIndices.Count == 0)
                return false;

            int bestBoundaryAtomIndex = -1;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int maxLeftSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, leftPrimaryAtomIndices.Count - 1));
            int maxRightSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, rightPrimaryAtomIndices.Count - 1));
            for (int leftSkip = 0; leftSkip <= maxLeftSkip; leftSkip++)
            {
                for (int rightSkip = 0; rightSkip <= maxRightSkip; rightSkip++)
                {
                    int leftPosition = leftPrimaryAtomIndices.Count - 1 - leftSkip;
                    int rightPosition = rightSkip;
                    if (leftPosition < 0 || rightPosition >= rightPrimaryAtomIndices.Count)
                        continue;

                    int candidateMatchedAtomCount = 0;
                    int candidateMatchedUniqueLaneCount = 0;
                    int strongContextMatchCount = 0;
                    int oppositeDirectionMatchCount = 0;
                    HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
                    int currentLeftPosition = leftPosition;
                    int currentRightPosition = rightPosition;
                    while (currentLeftPosition >= 0 && currentRightPosition < rightPrimaryAtomIndices.Count)
                    {
                        TrackAtom previousAtom = chain.TrackAtoms[leftPrimaryAtomIndices[currentLeftPosition]];
                        TrackAtom currentAtom = chain.TrackAtoms[rightPrimaryAtomIndices[currentRightPosition]];
                        if (!TryMatchReverseRepeatedPrimaryAtom(
                                previousAtom,
                                currentAtom,
                                out bool strongContextMatch,
                                out bool oppositeDirectionMatch))
                        {
                            break;
                        }

                        candidateMatchedAtomCount++;
                        if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                            candidateMatchedUniqueLaneCount++;
                        if (strongContextMatch)
                            strongContextMatchCount++;
                        if (oppositeDirectionMatch)
                            oppositeDirectionMatchCount++;

                        currentLeftPosition--;
                        currentRightPosition++;
                    }

                    bool qualifies = candidateMatchedAtomCount >= TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                        && candidateMatchedUniqueLaneCount >= TURNBACK_REPEAT_MIN_UNIQUE_LANES
                        && (strongContextMatchCount > 0 || oppositeDirectionMatchCount >= 2);
                    if (!qualifies)
                        continue;

                    bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                        || (candidateMatchedAtomCount == bestMatchedAtomCount
                            && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                    if (!better)
                        continue;

                    bestMatchedAtomCount = candidateMatchedAtomCount;
                    bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                    bestBoundaryAtomIndex = rightPrimaryAtomIndices[rightPosition];
                }
            }

            if (bestBoundaryAtomIndex < 0)
                return false;

            boundaryAtomIndex = bestBoundaryAtomIndex;
            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            return true;
        }

        private bool TryFindAdjacentSegmentTurnbackBoundary(
            LineTrackChain chain,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount,
            out int segmentPairIndex,
            out string note)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            segmentPairIndex = -1;
            note = "no-adjacent-reverse-overlap";
            if (chain == null || chain.SegmentRanges.Count < 2)
            {
                note = "insufficient-segments";
                return false;
            }

            bool foundAnyPrimary = false;
            int bestBoundaryAtomIndex = -1;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int bestSegmentPairIndex = -1;
            for (int segmentIndex = 1; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                List<int> leftPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[segmentIndex - 1]);
                List<int> rightPrimaryAtomIndices = CollectPrimaryAtomIndicesForRange(chain, chain.SegmentRanges[segmentIndex]);
                if (leftPrimaryAtomIndices.Count > 0 && rightPrimaryAtomIndices.Count > 0)
                    foundAnyPrimary = true;

                if (!TryMeasureAdjacentSegmentReverseOverlap(
                        chain,
                        segmentIndex - 1,
                        segmentIndex,
                        out int candidateBoundaryAtomIndex,
                        out int candidateMatchedAtomCount,
                        out int candidateMatchedUniqueLaneCount))
                {
                    continue;
                }

                bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                    || (candidateMatchedAtomCount == bestMatchedAtomCount
                        && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                if (!better)
                    continue;

                bestBoundaryAtomIndex = candidateBoundaryAtomIndex;
                bestMatchedAtomCount = candidateMatchedAtomCount;
                bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                bestSegmentPairIndex = segmentIndex - 1;
            }

            if (bestBoundaryAtomIndex < 0)
            {
                if (!foundAnyPrimary)
                    note = "adjacent-segments-without-primary";
                return false;
            }

            boundaryAtomIndex = bestBoundaryAtomIndex;
            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            segmentPairIndex = bestSegmentPairIndex;
            note = "segment-pair=" + bestSegmentPairIndex + " match=" + bestMatchedAtomCount + " unique=" + bestMatchedUniqueLaneCount;
            return true;
        }

        private static List<int> CollectPrimaryAtomIndicesForWindow(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return primaryAtomIndices;
            }

            int start = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count);
            int end = math.clamp(endAtomIndexExclusive, start, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }

            return primaryAtomIndices;
        }

        private static int NormalizeCircularBoundary(int atomIndex, int atomCount)
        {
            if (atomCount <= 0)
                return 0;

            int normalized = atomIndex % atomCount;
            if (normalized < 0)
                normalized += atomCount;
            return normalized;
        }

        private static void AppendPrimaryAtomIndicesForRange(
            LineTrackChain chain,
            List<int> primaryAtomIndices,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null
                || primaryAtomIndices == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return;
            }

            int start = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count);
            int end = math.clamp(endAtomIndexExclusive, start, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                if (chain.TrackAtoms[atomIndex].AtomClass == TrackAtomClass.PrimaryLane)
                    primaryAtomIndices.Add(atomIndex);
            }
        }

        private static List<int> CollectPrimaryAtomIndicesForCircularWindow(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            var primaryAtomIndices = new List<int>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return primaryAtomIndices;

            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return primaryAtomIndices;

            if (start < end)
            {
                AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, start, end);
                return primaryAtomIndices;
            }

            AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, start, atomCount);
            AppendPrimaryAtomIndicesForRange(chain, primaryAtomIndices, 0, end);
            return primaryAtomIndices;
        }

        private static Entity[] ReversePhysicalLaneKeys(Entity[] keys)
        {
            if (keys == null || keys.Length == 0)
                return Array.Empty<Entity>();

            Entity[] reversed = new Entity[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                reversed[i] = keys[keys.Length - 1 - i];
            return reversed;
        }

        private static int ComputeOrderedPhysicalKeyLcsLength(Entity[] left, Entity[] right)
        {
            if (left == null || right == null || left.Length == 0 || right.Length == 0)
                return 0;

            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int leftIndex = 0; leftIndex < left.Length; leftIndex++)
            {
                Entity leftKey = left[leftIndex];
                for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                {
                    if (leftKey == right[rightIndex - 1])
                        current[rightIndex] = previous[rightIndex - 1] + 1;
                    else
                        current[rightIndex] = math.max(previous[rightIndex], current[rightIndex - 1]);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
                Array.Clear(current, 0, current.Length);
            }

            return previous[right.Length];
        }

        private bool TryMeasureReverseOverlapBetweenLaneSequences(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int matchedLaneCount)
        {
            matchedLaneCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            matchedLaneCount = ComputeOrderedPhysicalKeyLcsLength(
                inboundKeys,
                ReversePhysicalLaneKeys(outboundKeys));
            if (matchedLaneCount < 2)
                return false;

            float coverageRatio = matchedLaneCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.35f;
        }

        private bool TryMeasureReverseOverlapBetweenTrackContainerSequences(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int matchedContainerCount,
            out int matchedUniqueContainerCount)
        {
            matchedContainerCount = 0;
            matchedUniqueContainerCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            matchedContainerCount = ComputeOrderedPhysicalKeyLcsLength(
                inboundKeys,
                ReversePhysicalLaneKeys(outboundKeys));
            if (matchedContainerCount < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS)
                return false;

            HashSet<Entity> inboundDistinct = new HashSet<Entity>(inboundKeys);
            HashSet<Entity> matchedDistinct = new HashSet<Entity>();
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundDistinct.Contains(outboundKeys[i]))
                    matchedDistinct.Add(outboundKeys[i]);
            }

            matchedUniqueContainerCount = matchedDistinct.Count;
            if (matchedUniqueContainerCount <= 0)
                return false;

            float coverageRatio = matchedContainerCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.35f;
        }

        private bool TryMeasureSharedPhysicalLaneSetOverlap(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int sharedLaneCount)
        {
            sharedLaneCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSlicePhysicalLaneKeys(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            HashSet<Entity> inboundSet = new HashSet<Entity>(inboundKeys);
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundSet.Contains(outboundKeys[i]))
                    sharedLaneCount++;
            }

            if (sharedLaneCount < 4)
                return false;

            float coverageRatio = sharedLaneCount / (float)math.max(1, math.min(inboundKeys.Length, outboundKeys.Length));
            return coverageRatio >= 0.20f;
        }

        private bool TryMeasureSharedTrackContainerSetOverlap(
            LineTrackChain chain,
            int inboundStartAtomIndex,
            int inboundEndAtomIndexExclusive,
            int outboundStartAtomIndex,
            int outboundEndAtomIndexExclusive,
            out int sharedContainerCount)
        {
            sharedContainerCount = 0;
            if (chain == null)
                return false;

            Entity[] inboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            Entity[] outboundKeys = CollectTraversalSliceTrackContainerSequenceCircular(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundKeys.Length == 0 || outboundKeys.Length == 0)
                return false;

            HashSet<Entity> inboundSet = new HashSet<Entity>(inboundKeys);
            HashSet<Entity> sharedSet = new HashSet<Entity>();
            for (int i = 0; i < outboundKeys.Length; i++)
            {
                if (inboundSet.Contains(outboundKeys[i]))
                    sharedSet.Add(outboundKeys[i]);
            }

            sharedContainerCount = sharedSet.Count;
            if (sharedContainerCount <= 0)
                return false;

            float coverageRatio = sharedContainerCount / (float)math.max(1, math.min(inboundSet.Count, new HashSet<Entity>(outboundKeys).Count));
            return coverageRatio >= 0.20f;
        }

        private bool TryMeasureReverseOverlapBetweenPrimaryWindows(
            LineTrackChain chain,
            List<int> inboundPrimaryAtomIndices,
            List<int> outboundPrimaryAtomIndices,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount)
        {
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            if (chain == null
                || inboundPrimaryAtomIndices == null
                || outboundPrimaryAtomIndices == null
                || inboundPrimaryAtomIndices.Count == 0
                || outboundPrimaryAtomIndices.Count == 0)
            {
                return false;
            }

            const float minCoverageRatio = 0.35f;
            int bestMatchedAtomCount = 0;
            int bestMatchedUniqueLaneCount = 0;
            int maxLeftSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, inboundPrimaryAtomIndices.Count - 1));
            int maxRightSkip = math.min(TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP, math.max(0, outboundPrimaryAtomIndices.Count - 1));
            for (int leftSkip = 0; leftSkip <= maxLeftSkip; leftSkip++)
            {
                for (int rightSkip = 0; rightSkip <= maxRightSkip; rightSkip++)
                {
                    int leftPosition = inboundPrimaryAtomIndices.Count - 1 - leftSkip;
                    int rightPosition = rightSkip;
                    if (leftPosition < 0 || rightPosition >= outboundPrimaryAtomIndices.Count)
                        continue;

                    int candidateMatchedAtomCount = 0;
                    int candidateMatchedUniqueLaneCount = 0;
                    int strongContextMatchCount = 0;
                    int oppositeDirectionMatchCount = 0;
                    HashSet<Entity> uniqueLaneKeys = new HashSet<Entity>();
                    int currentLeftPosition = leftPosition;
                    int currentRightPosition = rightPosition;
                    while (currentLeftPosition >= 0 && currentRightPosition < outboundPrimaryAtomIndices.Count)
                    {
                        TrackAtom previousAtom = chain.TrackAtoms[inboundPrimaryAtomIndices[currentLeftPosition]];
                        TrackAtom currentAtom = chain.TrackAtoms[outboundPrimaryAtomIndices[currentRightPosition]];
                        if (!TryMatchReverseRepeatedPrimaryAtom(
                                previousAtom,
                                currentAtom,
                                out bool strongContextMatch,
                                out bool oppositeDirectionMatch))
                        {
                            break;
                        }

                        candidateMatchedAtomCount++;
                        if (uniqueLaneKeys.Add(currentAtom.Key.PhysicalLaneKey))
                            candidateMatchedUniqueLaneCount++;
                        if (strongContextMatch)
                            strongContextMatchCount++;
                        if (oppositeDirectionMatch)
                            oppositeDirectionMatchCount++;

                        currentLeftPosition--;
                        currentRightPosition++;
                    }

                    if (candidateMatchedAtomCount < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS
                        || candidateMatchedUniqueLaneCount < TURNBACK_REPEAT_MIN_UNIQUE_LANES
                        || (strongContextMatchCount <= 0 && oppositeDirectionMatchCount < 2))
                    {
                        continue;
                    }

                    float coverageRatio = candidateMatchedAtomCount / (float)math.max(1, math.min(inboundPrimaryAtomIndices.Count, outboundPrimaryAtomIndices.Count));
                    if (coverageRatio < minCoverageRatio)
                        continue;

                    bool better = candidateMatchedAtomCount > bestMatchedAtomCount
                        || (candidateMatchedAtomCount == bestMatchedAtomCount
                            && candidateMatchedUniqueLaneCount > bestMatchedUniqueLaneCount);
                    if (!better)
                        continue;

                    bestMatchedAtomCount = candidateMatchedAtomCount;
                    bestMatchedUniqueLaneCount = candidateMatchedUniqueLaneCount;
                }
            }

            if (bestMatchedAtomCount <= 0)
                return false;

            matchedAtomCount = bestMatchedAtomCount;
            matchedUniqueLaneCount = bestMatchedUniqueLaneCount;
            return true;
        }

        private bool TryConfirmTurnbackBoundaryAtStationPass(
            LineTrackChain chain,
            List<StationPassRange> stationPasses,
            int stationPassIndex,
            out int boundaryAtomIndex,
            out int matchedAtomCount,
            out int matchedUniqueLaneCount,
            out string note)
        {
            boundaryAtomIndex = -1;
            matchedAtomCount = 0;
            matchedUniqueLaneCount = 0;
            note = string.Empty;
            if (chain == null
                || stationPasses == null
                || stationPassIndex < 0
                || stationPassIndex >= stationPasses.Count)
            {
                return false;
            }

            StationPassRange currentPass = stationPasses[stationPassIndex];
            if (currentPass.Building == Entity.Null || currentPass.WaypointIndex < 0)
                return false;

            int stationPassCount = stationPasses.Count;
            if (stationPassCount < 2)
                return false;

            int previousPassIndex = (stationPassIndex - 1 + stationPassCount) % stationPassCount;
            int nextPassIndex = (stationPassIndex + 1) % stationPassCount;
            StationPassRange previousPass = stationPasses[previousPassIndex];
            StationPassRange nextPass = stationPasses[nextPassIndex];

            bool isMirrorCandidate = stationPassCount >= 3
                && previousPass.Building != Entity.Null
                && previousPass.Building == nextPass.Building;
            bool isLapSeamCandidate = stationPassIndex == stationPassCount - 1;
            if (!isMirrorCandidate && !isLapSeamCandidate)
                return false;

            int inboundStartAtomIndex = previousPass.EndAtomIndexExclusive;
            int inboundEndAtomIndexExclusive = currentPass.StartAtomIndex;
            int outboundStartAtomIndex = currentPass.EndAtomIndexExclusive;
            int outboundEndAtomIndexExclusive = nextPass.StartAtomIndex;

            List<int> inboundPrimaryAtomIndices = CollectPrimaryAtomIndicesForCircularWindow(
                chain,
                inboundStartAtomIndex,
                inboundEndAtomIndexExclusive);
            List<int> outboundPrimaryAtomIndices = CollectPrimaryAtomIndicesForCircularWindow(
                chain,
                outboundStartAtomIndex,
                outboundEndAtomIndexExclusive);
            if (inboundPrimaryAtomIndices.Count == 0
                || outboundPrimaryAtomIndices.Count == 0)
            {
                return false;
            }

            if (!TryMeasureReverseOverlapBetweenPrimaryWindows(
                    chain,
                    inboundPrimaryAtomIndices,
                    outboundPrimaryAtomIndices,
                    out matchedAtomCount,
                    out matchedUniqueLaneCount))
            {
                if (!TryMeasureReverseOverlapBetweenLaneSequences(
                        chain,
                        inboundStartAtomIndex,
                        inboundEndAtomIndexExclusive,
                        outboundStartAtomIndex,
                        outboundEndAtomIndexExclusive,
                        out int matchedLaneCount))
                {
                    if (!TryMeasureSharedPhysicalLaneSetOverlap(
                            chain,
                            inboundStartAtomIndex,
                            inboundEndAtomIndexExclusive,
                            outboundStartAtomIndex,
                            outboundEndAtomIndexExclusive,
                            out int sharedLaneCount))
                    {
                        if (!TryMeasureReverseOverlapBetweenTrackContainerSequences(
                                chain,
                                inboundStartAtomIndex,
                                inboundEndAtomIndexExclusive,
                                outboundStartAtomIndex,
                                outboundEndAtomIndexExclusive,
                                out int matchedContainerCount,
                                out int matchedUniqueContainerCount))
                        {
                            if (!TryMeasureSharedTrackContainerSetOverlap(
                                    chain,
                                    inboundStartAtomIndex,
                                    inboundEndAtomIndexExclusive,
                                    outboundStartAtomIndex,
                                    outboundEndAtomIndexExclusive,
                                    out int sharedContainerCount))
                            {
                                return false;
                            }

                            matchedAtomCount = sharedContainerCount;
                            matchedUniqueLaneCount = sharedContainerCount;
                            boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                            note = (isMirrorCandidate ? "mirror-stop-container-set" : "seam-stop-container-set")
                                + " wp=" + currentPass.WaypointIndex
                                + " match=" + sharedContainerCount;
                            return true;
                        }

                        matchedAtomCount = matchedContainerCount;
                        matchedUniqueLaneCount = matchedUniqueContainerCount;
                        boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                        note = (isMirrorCandidate ? "mirror-stop-container" : "seam-stop-container")
                            + " wp=" + currentPass.WaypointIndex
                            + " match=" + matchedContainerCount
                            + " unique=" + matchedUniqueContainerCount;
                        return true;
                    }

                    matchedAtomCount = sharedLaneCount;
                    matchedUniqueLaneCount = sharedLaneCount;
                    boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                    note = (isMirrorCandidate ? "mirror-stop-set" : "seam-stop-set")
                        + " wp=" + currentPass.WaypointIndex
                        + " match=" + sharedLaneCount;
                    return true;
                }

                matchedAtomCount = matchedLaneCount;
                matchedUniqueLaneCount = matchedLaneCount;
                boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
                note = (isMirrorCandidate ? "mirror-stop-lcs" : "seam-stop-lcs")
                    + " wp=" + currentPass.WaypointIndex
                    + " match=" + matchedLaneCount;
                return true;
            }

            boundaryAtomIndex = math.clamp(outboundStartAtomIndex, 0, chain.TrackAtoms.Count - 1);
            note = (isMirrorCandidate ? "mirror-stop" : "seam-stop")
                + " wp=" + currentPass.WaypointIndex
                + " match=" + matchedAtomCount
                + " unique=" + matchedUniqueLaneCount;
            return true;
        }

        private int ResolveTraversalRunSliceIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            if (chain?.TraversalProfile == null
                || chain.TraversalProfile.RunSlices == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return -1;
            }

            int clampedAtomIndex = math.clamp(atomIndex, 0, chain.TrackAtoms.Count - 1);
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                if (clampedAtomIndex >= slice.StartAtomIndex
                    && clampedAtomIndex < slice.EndAtomIndexExclusive)
                {
                    return slice.SliceIndex;
                }
            }

            return -1;
        }

        private int ResolveTurnbackBoundaryEventIndex(LineTrackChain chain, int boundaryAtomIndex)
        {
            if (chain?.TraversalProfile == null || chain.TraversalProfile.Events == null)
                return -1;

            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.StartAtomIndex == boundaryAtomIndex
                    || traversalEvent.EndAtomIndexExclusive == boundaryAtomIndex)
                {
                    return traversalEvent.EventIndex;
                }
            }

            return -1;
        }

        private int ResolveVehicleTargetWaypointIndex(Entity vehicle)
        {
            if (vehicle == Entity.Null || !EntityManager.HasComponent<Target>(vehicle))
                return -1;

            Entity target = EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || !EntityManager.HasComponent<Waypoint>(target))
                return -1;

            return EntityManager.GetComponentData<Waypoint>(target).m_Index;
        }

        private TurnbackLearnVehicleSampleState GetOrCreateTurnbackLearnVehicleSampleState(
            Entity vehicle,
            Entity line,
            ulong chainSignature)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return null;

            if (!m_TurnbackLearnVehicleStates.TryGetValue(vehicle, out TurnbackLearnVehicleSampleState state)
                || state == null
                || state.Line != line
                || state.ChainSignature != chainSignature)
            {
                state = new TurnbackLearnVehicleSampleState
                {
                    Line = line,
                    ChainSignature = chainSignature,
                    LastStrongSampleFrame = 0,
                    LastTargetWaypointIndex = -1,
                    LastObservedPathSignature = 0
                };
                m_TurnbackLearnVehicleStates[vehicle] = state;
            }

            return state;
        }

        private static bool IsLearnedTurnbackClusterConfirmed(LearnedTurnbackBoundaryCluster cluster)
        {
            return cluster != null && cluster.HitCount >= TURNBACK_LEARN_CONFIRM_HIT_COUNT;
        }

        private void TryLogTrackModelTurnbackSignal(
            Entity line,
            Entity vehicle,
            string signalKey,
            string message)
        {
            if (!IsTrackModelTurnbackSignalLoggingEnabled() || line == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_TrackModelTurnbackSignalLogCache,
                    m_TrackModelTurnbackSignalLastLogFrame,
                    line,
                    signalKey,
                    nowFrame,
                    TURNBACK_SIGNAL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            log.Info(message);
        }

        private bool TryUpsertLearnedTurnbackBoundaryCluster(
            Entity line,
            LineTrackChain chain,
            int sampleAtomIndex,
            Entity vehicle,
            uint nowFrame,
            out LearnedTurnbackBoundaryCluster cluster,
            out bool createdNewCluster,
            out bool atomIndexChanged,
            out bool confirmedBefore,
            out bool confirmedAfter)
        {
            cluster = null;
            createdNewCluster = false;
            atomIndexChanged = false;
            confirmedBefore = false;
            confirmedAfter = false;
            if (line == Entity.Null || chain == null || sampleAtomIndex < 0)
                return false;

            if (!m_LearnedTurnbackBoundaryClustersByLine.TryGetValue(line, out List<LearnedTurnbackBoundaryCluster> clusters))
            {
                clusters = new List<LearnedTurnbackBoundaryCluster>();
                m_LearnedTurnbackBoundaryClustersByLine[line] = clusters;
            }

            for (int i = clusters.Count - 1; i >= 0; i--)
            {
                if (clusters[i] == null || clusters[i].ChainSignature != chain.Signature)
                    clusters.RemoveAt(i);
            }

            int bestIndex = -1;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < clusters.Count; i++)
            {
                int distance = math.abs(clusters[i].AtomIndex - sampleAtomIndex);
                if (distance > TURNBACK_LEARN_CLUSTER_MERGE_ATOM_RADIUS || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = i;
            }

            if (bestIndex < 0)
            {
                cluster = new LearnedTurnbackBoundaryCluster(sampleAtomIndex, chain.Signature, nowFrame, vehicle);
                clusters.Add(cluster);
                createdNewCluster = true;
                confirmedAfter = IsLearnedTurnbackClusterConfirmed(cluster);
                return true;
            }

            cluster = clusters[bestIndex];
            confirmedBefore = IsLearnedTurnbackClusterConfirmed(cluster);
            int previousAtomIndex = cluster.AtomIndex;
            cluster.AtomIndex = (int)math.round(
                ((cluster.AtomIndex * (float)cluster.HitCount) + sampleAtomIndex)
                / math.max(1f, cluster.HitCount + 1f));
            cluster.HitCount++;
            cluster.ChainSignature = chain.Signature;
            cluster.LastHitFrame = nowFrame;
            cluster.LastVehicle = vehicle;
            atomIndexChanged = cluster.AtomIndex != previousAtomIndex;
            confirmedAfter = IsLearnedTurnbackClusterConfirmed(cluster);
            return true;
        }

        private bool ApplyLearnedTurnbackBoundariesToChain(Entity line, LineTrackChain chain)
        {
            if (line == Entity.Null || chain == null)
                return false;

            List<int> previousLearnedAtoms = new List<int>();

            for (int i = chain.TurnbackBoundaries.Count - 1; i >= 0; i--)
            {
                if (chain.TurnbackBoundaries[i].IsLearned)
                {
                    previousLearnedAtoms.Add(chain.TurnbackBoundaries[i].AtomIndex);
                    chain.TurnbackBoundaries.RemoveAt(i);
                }
            }

            if (!m_LearnedTurnbackBoundaryClustersByLine.TryGetValue(line, out List<LearnedTurnbackBoundaryCluster> clusters)
                || clusters == null
                || clusters.Count == 0
                || chain.TrackAtoms.Count == 0)
            {
                return previousLearnedAtoms.Count > 0;
            }

            previousLearnedAtoms.Sort();
            List<int> appliedLearnedAtoms = new List<int>();
            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                LearnedTurnbackBoundaryCluster cluster = clusters[clusterIndex];
                if (cluster == null
                    || cluster.ChainSignature != chain.Signature
                    || !IsLearnedTurnbackClusterConfirmed(cluster))
                {
                    continue;
                }

                int atomIndex = math.clamp(cluster.AtomIndex, 0, chain.TrackAtoms.Count - 1);
                bool overlapsStaticBoundary = false;

                for (int existingIndex = chain.TurnbackBoundaries.Count - 1; existingIndex >= 0; existingIndex--)
                {
                    TurnbackBoundary existingBoundary = chain.TurnbackBoundaries[existingIndex];
                    if (existingBoundary.IsLearned)
                        continue;

                    if (math.abs(existingBoundary.AtomIndex - atomIndex) <= TURNBACK_LEARN_CLUSTER_MERGE_ATOM_RADIUS)
                    {
                        overlapsStaticBoundary = true;
                        break;
                    }
                }

                if (overlapsStaticBoundary)
                    continue;

                int beforeSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, math.max(0, atomIndex - 1));
                int afterSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, atomIndex);
                int boundaryEventIndex = ResolveTurnbackBoundaryEventIndex(chain, atomIndex);
                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    atomIndex,
                    beforeSliceIndex,
                    afterSliceIndex,
                    boundaryEventIndex,
                    true,
                    cluster.HitCount,
                    0));
                appliedLearnedAtoms.Add(atomIndex);
            }

            appliedLearnedAtoms.Sort();
            chain.TurnbackBoundaries.Sort((left, right) =>
            {
                int atomCompare = left.AtomIndex.CompareTo(right.AtomIndex);
                if (atomCompare != 0)
                    return atomCompare;
                if (left.IsLearned != right.IsLearned)
                    return left.IsLearned ? -1 : 1;
                return left.MatchedAtomCount.CompareTo(right.MatchedAtomCount);
            });

            if (previousLearnedAtoms.Count != appliedLearnedAtoms.Count)
                return true;

            for (int i = 0; i < previousLearnedAtoms.Count; i++)
            {
                if (previousLearnedAtoms[i] != appliedLearnedAtoms[i])
                    return true;
            }

            return false;
        }

        private void TryLearnTurnbackBoundaryFromStrongSignal(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            TurnbackLearnVehicleSampleState learnState,
            int currentTargetWaypointIndex,
            string signalMode)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || learnState == null
                || chain == null
                || chain.LineEntity != line
                || chain.TrackAtoms.Count == 0)
            {
                return;
            }

            if (learnState.LastStrongSampleFrame > 0
                && nowFrame < learnState.LastStrongSampleFrame + TURNBACK_LEARN_SAMPLE_COOLDOWN_FRAMES)
            {
                return;
            }

            learnState.LastStrongSampleFrame = nowFrame;
            int sampleAtomIndex = math.clamp(cursor.AtomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            if (!TryUpsertLearnedTurnbackBoundaryCluster(
                    line,
                    chain,
                    sampleAtomIndex,
                    vehicle,
                    nowFrame,
                    out LearnedTurnbackBoundaryCluster cluster,
                    out bool createdNewCluster,
                    out bool atomIndexChanged,
                    out bool confirmedBefore,
                    out bool confirmedAfter))
            {
                return;
            }

            if (!confirmedAfter)
            {
                TryLogTrackModelTurnbackSignal(
                    line,
                    vehicle,
                    "turnback-strong-pending",
                    "[TrackModelTurnbackSignal] line=" + line.Index
                        + " vehicle=" + vehicle.Index
                        + " status=pending"
                        + " mode=" + signalMode
                        + " hits=" + cluster.HitCount
                        + " targetWp=" + currentTargetWaypointIndex
                        + " atom=" + cluster.AtomIndex);
                return;
            }

            bool shouldRefresh = (!confirmedBefore) || atomIndexChanged;
            bool chainChanged = false;
            if (shouldRefresh)
            {
                chainChanged = ApplyLearnedTurnbackBoundariesToChain(line, chain);
                if (chainChanged)
                {
                    m_LineRunningVehicleFrameSnapshots.Remove(line);
                    RequestLineOrderedRuntimeForceRefresh(
                        line,
                        confirmedBefore ? "learned-turnback-shift" : "learned-turnback-promote");
                }
                else
                {
                    TryLogTrackModelTurnbackSignal(
                        line,
                        vehicle,
                        "turnback-confirmed-near-static",
                        "[TrackModelTurnbackSignal] line=" + line.Index
                            + " vehicle=" + vehicle.Index
                            + " status=confirmed-near-static"
                            + " mode=" + signalMode
                            + " hits=" + cluster.HitCount
                            + " targetWp=" + currentTargetWaypointIndex
                            + " atom=" + cluster.AtomIndex);
                }
            }

            if (!chainChanged)
                return;

            string eventType = confirmedBefore ? "shift" : "promote";
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_TrackModelTurnbackLearnLogCache,
                    m_TrackModelTurnbackLearnLastLogFrame,
                    line,
                    "turnback-learn-" + eventType,
                    nowFrame,
                    TURNBACK_LEARN_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            log.Info("[TrackModelTurnbackLearn] line=" + line.Index
                + " vehicle=" + vehicle.Index
                + " event=" + eventType
                + " atom=" + cluster.AtomIndex
                + " sampleAtom=" + sampleAtomIndex
                + " hits=" + cluster.HitCount
                + " targetWp=" + currentTargetWaypointIndex
                + " mode=" + signalMode
                + " new=" + (createdNewCluster ? "1" : "0")
                + " boundaries=" + chain.TurnbackBoundaries.Count);
        }

        private void TryLearnTurnbackBoundaryFromOriginalReturn(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            TurnbackLearnVehicleSampleState learnState,
            int currentTargetWaypointIndex,
            bool hasReturnEndReached,
            string signalMode)
        {
            if (!hasReturnEndReached)
                return;

            TryLearnTurnbackBoundaryFromStrongSignal(
                vehicle,
                line,
                chain,
                cursor,
                learnState,
                currentTargetWaypointIndex,
                signalMode);
        }

        private void BuildTurnbackBoundaries(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null)
                return;

            chain.TurnbackBoundaries.Clear();
            chain.TurnbackBuildMode = "none";
            chain.TurnbackBuildNote = string.Empty;
            chain.TurnbackBuildSegmentPairIndex = -1;
            if (chain.TrackAtoms.Count == 0)
            {
                chain.TurnbackBuildNote = "no-track-atoms";
                return;
            }

            List<int> primaryAtomIndices = CollectPrimaryAtomIndices(chain);
            if (primaryAtomIndices.Count < TURNBACK_REPEAT_MIN_PRIMARY_ATOMS * 2)
            {
                chain.TurnbackBuildNote = "insufficient-primary-atoms";
                return;
            }

            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints);
            List<string> candidateNotes = new List<string>();
            for (int stationPassIndex = 0; stationPassIndex < stationPasses.Count; stationPassIndex++)
            {
                if (!TryConfirmTurnbackBoundaryAtStationPass(
                        chain,
                        stationPasses,
                        stationPassIndex,
                        out int boundaryAtomIndex,
                        out int matchedAtomCount,
                        out int matchedUniqueLaneCount,
                        out string stationNote))
                {
                    continue;
                }

                int beforeSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, math.max(0, boundaryAtomIndex - 1));
                int afterSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, boundaryAtomIndex);
                int boundaryEventIndex = ResolveTurnbackBoundaryEventIndex(chain, boundaryAtomIndex);
                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    boundaryAtomIndex,
                    beforeSliceIndex,
                    afterSliceIndex,
                    boundaryEventIndex,
                    false,
                    matchedAtomCount,
                    matchedUniqueLaneCount));
                candidateNotes.Add(stationNote);
            }

            if (chain.TurnbackBoundaries.Count > 0)
            {
                chain.TurnbackBoundaries.Sort((left, right) => left.AtomIndex.CompareTo(right.AtomIndex));
                chain.TurnbackBuildMode = "station-local-overlap";
                chain.TurnbackBuildNote = string.Join(";", candidateNotes);
                ApplyLearnedTurnbackBoundariesToChain(chain.LineEntity, chain);
                return;
            }

            if (TryFindAdjacentSegmentTurnbackBoundary(
                    chain,
                    out int adjacentBoundaryAtomIndex,
                    out int adjacentMatchedAtomCount,
                    out int adjacentMatchedUniqueLaneCount,
                    out int adjacentSegmentPairIndex,
                    out string adjacentNote))
            {
                int beforeSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, math.max(0, adjacentBoundaryAtomIndex - 1));
                int afterSliceIndex = ResolveTraversalRunSliceIndexForAtom(chain, adjacentBoundaryAtomIndex);
                int boundaryEventIndex = ResolveTurnbackBoundaryEventIndex(chain, adjacentBoundaryAtomIndex);
                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    adjacentBoundaryAtomIndex,
                    beforeSliceIndex,
                    afterSliceIndex,
                    boundaryEventIndex,
                    false,
                    adjacentMatchedAtomCount,
                    adjacentMatchedUniqueLaneCount));
                chain.TurnbackBuildMode = "adjacent-segment-fallback";
                chain.TurnbackBuildNote = adjacentNote;
                chain.TurnbackBuildSegmentPairIndex = adjacentSegmentPairIndex;
                ApplyLearnedTurnbackBoundariesToChain(chain.LineEntity, chain);
            }
            else
            {
                chain.TurnbackBuildMode = "none";
                chain.TurnbackBuildNote = "station-local-failed;adjacent-failed";
            }

            ApplyLearnedTurnbackBoundariesToChain(chain.LineEntity, chain);
        }

        private void LogTrackModelTurnbackBuild(LineTrackChain chain)
        {
            if (!IsTrackModelTurnbackBuildLoggingEnabled() || chain == null)
                return;

            List<int> primaryAtomIndices = CollectPrimaryAtomIndices(chain);
            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModelTurnbackBuild] line=").Append(chain.LineEntity.Index)
                .Append(" atoms=").Append(chain.TrackAtoms.Count)
                .Append(" primary=").Append(primaryAtomIndices.Count)
                .Append(" boundaries=").Append(chain.TurnbackBoundaries.Count)
                .Append(" mode=").Append(string.IsNullOrWhiteSpace(chain.TurnbackBuildMode) ? "none" : chain.TurnbackBuildMode)
                .Append(" note=").Append(string.IsNullOrWhiteSpace(chain.TurnbackBuildNote) ? "-" : chain.TurnbackBuildNote)
                .Append(" pair=").Append(chain.TurnbackBuildSegmentPairIndex);
            int limit = math.min(chain.TurnbackBoundaries.Count, 8);
            for (int i = 0; i < limit; i++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[i];
                sb.Append(" | tb").Append(i).Append("=atom").Append(boundary.AtomIndex)
                    .Append(boundary.IsLearned ? " learnedHits=" : " match=").Append(boundary.MatchedAtomCount)
                    .Append(boundary.IsLearned ? string.Empty : " unique=" + boundary.MatchedUniqueLaneCount)
                    .Append(" slices=").Append(boundary.BeforeSliceIndex).Append("->").Append(boundary.AfterSliceIndex);
            }
            string summary = sb.ToString();
            if (m_TrackModelTurnbackBuildLogCache.TryGetValue(chain.LineEntity, out string previousSummary)
                && previousSummary == summary)
            {
                return;
            }

            m_TrackModelTurnbackBuildLogCache[chain.LineEntity] = summary;
            log.Info(summary);
        }

        private void LogTraversalSliceCutPointsBuild(LineTrackChain chain)
        {
            if (!IsTrackModelTurnbackBuildLoggingEnabled()
                || chain == null
                || chain.TraversalProfile == null
                || chain.SegmentRanges.Count == 0
                || chain.TraversalProfile.SegmentSliceCutPointProgresses == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("[TraversalSliceCutPoints] line=").Append(chain.LineEntity.Index);
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                if (segmentIndex >= chain.TraversalProfile.SegmentSliceCutPointProgresses.Length)
                    break;

                float[] cutPoints = chain.TraversalProfile.SegmentSliceCutPointProgresses[segmentIndex];
                if (cutPoints == null || cutPoints.Length == 0)
                    continue;

                TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                int segmentStartAtomIndex = segmentRange.StartAtomIndex;
                int segmentLengthAtoms = math.max(1, segmentRange.EndAtomIndexExclusive - segmentStartAtomIndex);
                sb.Append(" | seg").Append(segmentIndex).Append('=');
                for (int cutPointIndex = 0; cutPointIndex < cutPoints.Length; cutPointIndex++)
                {
                    if (cutPointIndex > 0)
                        sb.Append(',');

                    float progress = cutPoints[cutPointIndex];
                    int atomIndex = math.clamp(
                        segmentStartAtomIndex + (int)math.round(progress * segmentLengthAtoms),
                        segmentStartAtomIndex,
                        math.max(segmentStartAtomIndex, segmentRange.EndAtomIndexExclusive - 1));
                    sb.Append(progress.ToString("0.00"))
                        .Append("@a")
                        .Append(atomIndex);
                }
            }

            log.Info(sb.ToString());
        }

        private List<StationPassRange> CollectStationPassRanges(
            LineTrackChain chain,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            List<StationPassRange> stationPasses = new List<StationPassRange>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return stationPasses;

            bool hasLinePrefabData = TryGetTraversalProfileLineData(line, out Game.Prefabs.TransportLineData prefabLineData);
            Dictionary<Entity, int> passCountByBuilding = new Dictionary<Entity, int>();

            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count;)
            {
                Entity building = ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                {
                    atomIndex++;
                    continue;
                }

                int startAtomIndex = atomIndex;
                atomIndex++;
                while (atomIndex < chain.TrackAtoms.Count
                    && ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget) == building)
                {
                    atomIndex++;
                }

                int endAtomIndexExclusive = atomIndex;
                int passIndex = passCountByBuilding.TryGetValue(building, out int existingPassCount)
                    ? existingPassCount
                    : 0;
                passCountByBuilding[building] = passIndex + 1;

                int waypointIndex = -1;
                float stopFrames = 0f;
                if (TryFindTraversalStopWaypointIndex(chain, building, startAtomIndex, endAtomIndexExclusive, out int matchedWaypointIndex))
                {
                    waypointIndex = matchedWaypointIndex;
                    if (hasLinePrefabData)
                        stopFrames = GetProfileWaypointStopFrames(line, waypoints, matchedWaypointIndex, prefabLineData);
                }

                stationPasses.Add(new StationPassRange(
                    building,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    waypointIndex,
                    stopFrames,
                    passIndex));
            }

            return stationPasses;
        }

        private bool TryGetTraversalProfileLineData(Entity line, out Game.Prefabs.TransportLineData prefabLineData)
        {
            prefabLineData = default;
            if (line == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(line))
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<Game.Prefabs.TransportLineData>(prefab))
                return false;

            prefabLineData = EntityManager.GetComponentData<Game.Prefabs.TransportLineData>(prefab);
            return true;
        }

        private bool TryFindTraversalStopWaypointIndex(
            LineTrackChain chain,
            Entity building,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (chain == null || building == Entity.Null)
                return false;

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
            {
                ControlPointMarker marker = chain.ControlPoints[controlPointIndex];
                if ((marker.Kind != ControlPointKind.Stop && marker.Kind != ControlPointKind.Bypass)
                    || marker.Building != building
                    || marker.AtomIndex < startAtomIndex
                    || marker.AtomIndex >= endAtomIndexExclusive)
                {
                    continue;
                }

                waypointIndex = marker.WaypointIndex;
                return true;
            }

            return false;
        }

        private static Entity[] CollectTraversalSlicePhysicalLaneKeys(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return Array.Empty<Entity>();

            HashSet<Entity> seenKeys = new HashSet<Entity>();
            List<Entity> orderedKeys = new List<Entity>();
            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return Array.Empty<Entity>();

            void AppendRange(int rangeStart, int rangeEndExclusive)
            {
                for (int atomIndex = rangeStart; atomIndex < rangeEndExclusive; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (atom.Key.PhysicalLaneKey != Entity.Null && seenKeys.Add(atom.Key.PhysicalLaneKey))
                        orderedKeys.Add(atom.Key.PhysicalLaneKey);
                }
            }

            if (start < end)
                AppendRange(start, end);
            else
            {
                AppendRange(start, atomCount);
                AppendRange(0, end);
            }

            if (orderedKeys.Count == 0)
                return Array.Empty<Entity>();

            return orderedKeys.ToArray();
        }

        private Entity ResolveTrackContainerKey(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Game.Net.Edge>(current)
                    || EntityManager.HasComponent<Game.Net.Node>(current)
                    || EntityManager.HasBuffer<Game.Net.SubLane>(current))
                {
                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return Entity.Null;
        }

        private Entity[] CollectTraversalSliceTrackContainerSequenceCircular(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return Array.Empty<Entity>();

            List<Entity> orderedKeys = new List<Entity>();
            int atomCount = chain.TrackAtoms.Count;
            int start = NormalizeCircularBoundary(startAtomIndex, atomCount);
            int end = NormalizeCircularBoundary(endAtomIndexExclusive, atomCount);
            if (start == end)
                return Array.Empty<Entity>();

            void AppendRange(int rangeStart, int rangeEndExclusive)
            {
                for (int atomIndex = rangeStart; atomIndex < rangeEndExclusive; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    Entity containerKey = ResolveTrackContainerKey(atom.Key.PhysicalLaneKey);
                    if (containerKey != Entity.Null)
                        orderedKeys.Add(containerKey);
                }
            }

            if (start < end)
                AppendRange(start, end);
            else
            {
                AppendRange(start, atomCount);
                AppendRange(0, end);
            }

            if (orderedKeys.Count == 0)
                return Array.Empty<Entity>();

            return orderedKeys.ToArray();
        }

        private float EstimateTraversalRunSliceFrames(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            bool hasTimeProfile,
            LineTimeProfileHeader timeProfile,
            float lineFrames)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return 0f;
            }

            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);

            if (hasTimeProfile && timeProfile.m_Count == chain.SegmentRanges.Count)
            {
                float runFrames = 0f;
                for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
                {
                    TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                    int overlapStart = math.max(startAtomIndex, range.StartAtomIndex);
                    int overlapEndExclusive = math.min(endAtomIndexExclusive, range.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    int segmentAtomLength = math.max(1, range.EndAtomIndexExclusive - range.StartAtomIndex);
                    int overlapAtomLength = overlapEndExclusive - overlapStart;
                    runFrames += m_LineTimeProfileSegmentFrames[timeProfile.m_Offset + segmentIndex]
                        * (overlapAtomLength / (float)segmentAtomLength);
                }

                if (runFrames > 0f)
                    return runFrames;
            }

            float atomCount = math.max(1f, chain.TrackAtoms.Count);
            return lineFrames > 0f
                ? lineFrames * ((endAtomIndexExclusive - startAtomIndex) / atomCount)
                : 0f;
        }

        private void AppendSegmentTrackAtoms(List<TrackAtom> atoms, DynamicBuffer<PathElement> pathElements)
        {
            if (pathElements.Length == 0)
                return;

            for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
            {
                PathElement element = pathElements[pathIndex];
                if (!TryClassifyTrackAtom(pathElements, pathIndex, out TrackAtom atom))
                    continue;

                if (atom.AtomClass == TrackAtomClass.FilteredNoise)
                    continue;

                atoms.Add(atom);
            }
        }

        private void BuildAtomIndicesByLane(LineTrackChain chain)
        {
            if (chain == null)
                return;

            chain.AtomIndicesByLane.Clear();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                AddAtomIndexForLane(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForLane(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);

                AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);
            }
        }

        private static void AddAtomIndexForLane(Dictionary<Entity, List<int>> indexByLane, Entity lane, int atomIndex)
        {
            if (lane == Entity.Null)
                return;

            if (!indexByLane.TryGetValue(lane, out List<int> atomIndices))
            {
                atomIndices = new List<int>();
                indexByLane[lane] = atomIndices;
            }

            atomIndices.Add(atomIndex);
        }

        private void AddAtomIndexForNetOwnerChain(Dictionary<Entity, List<int>> indexByLane, Entity entity, int atomIndex)
        {
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                if (EntityManager.HasComponent<Game.Net.Edge>(owner)
                    || EntityManager.HasComponent<Game.Net.Node>(owner)
                    || EntityManager.HasBuffer<Game.Net.SubLane>(owner))
                {
                    AddAtomIndexForLane(indexByLane, owner, atomIndex);
                }

                current = owner;
            }
        }

        private void AddDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || entry.Value == null
                    || entry.Value.Count == 0)
                    continue;

                if (!m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences))
                {
                    occurrences = new List<DevSightLaneOccurrence>();
                    m_DevSightLaneIndex[entry.Key] = occurrences;
                }

                occurrences.Add(new DevSightLaneOccurrence(chain.LineEntity, chain, entry.Value));
            }
        }

        private void RemoveDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || !m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                for (int i = occurrences.Count - 1; i >= 0; i--)
                {
                    if (occurrences[i].LineEntity == chain.LineEntity)
                        occurrences.RemoveAt(i);
                }

                if (occurrences.Count == 0)
                    m_DevSightLaneIndex.Remove(entry.Key);
            }
        }

        private void RebuildSharedTrackIndex()
        {
            m_SharedTrackIndex.Clear();
            m_SharedPhysicalTrackIndex.Clear();

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                    continue;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (!m_SharedTrackIndex.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences))
                    {
                        occurrences = new List<SharedTrackOccurrence>();
                        m_SharedTrackIndex[atom.Key] = occurrences;
                    }

                    int waypointSegmentIndex = ResolveWaypointSegmentIndex(chain, atomIndex);
                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, waypointSegmentIndex));

                    Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                    if (!m_SharedPhysicalTrackIndex.TryGetValue(physicalLaneKey, out List<SharedPhysicalOccurrence> physicalOccurrences))
                    {
                        physicalOccurrences = new List<SharedPhysicalOccurrence>();
                        m_SharedPhysicalTrackIndex[physicalLaneKey] = physicalOccurrences;
                    }

                    physicalOccurrences.Add(new SharedPhysicalOccurrence(
                        line,
                        atomIndex,
                        waypointSegmentIndex,
                        atom.Key.PreviousTarget,
                        atom.Key.NextTarget));
                }
            }

            m_SharedTrackIndexDirty = false;
            m_SharedTrackIndexVersion++;
        }

        private void EnsureSharedTrackIndexCurrent()
        {
            if (!m_SharedTrackIndexDirty)
                return;

            RebuildSharedTrackIndex();
        }

        private static int ResolveWaypointSegmentIndex(LineTrackChain chain, int atomIndex)
        {
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        private void RefreshSharedRuns(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            if (chain.SharedRunsVersion == m_SharedTrackIndexVersion)
                return;

            chain.SharedRuns.Clear();
            chain.SharedRunsByOtherLine.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ControlEdgeSharedSpansReady = false;
            chain.BypassProtectedIntervalsReady = false;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;

            int runStart = -1;
            bool runMirrored = false;
            int runSharedLineCount = 0;
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane
                    || !TryGetSharedPhysicalContext(chain.LineEntity, atom, out int sharedLineCount, out bool mirroredContext))
                {
                    if (runStart >= 0)
                    {
                        chain.SharedRuns.Add(new SharedTrackRun(runStart, atomIndex, runMirrored, runSharedLineCount));
                        runStart = -1;
                        runMirrored = false;
                        runSharedLineCount = 0;
                    }

                    continue;
                }

                if (runStart < 0)
                {
                    runStart = atomIndex;
                    runMirrored = mirroredContext;
                    runSharedLineCount = sharedLineCount;
                    continue;
                }

                runMirrored |= mirroredContext;
                runSharedLineCount = math.max(runSharedLineCount, sharedLineCount);
            }

            if (runStart >= 0)
                chain.SharedRuns.Add(new SharedTrackRun(runStart, chain.TrackAtoms.Count, runMirrored, runSharedLineCount));

            RefreshSharedRunsByOtherLine(chain);
            chain.SharedRunsVersion = m_SharedTrackIndexVersion;
        }

        private void RefreshSharedRunsByOtherLine(LineTrackChain chain)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return;

            var candidateLines = new HashSet<Entity>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                if (!m_SharedPhysicalTrackIndex.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                foreach (SharedPhysicalOccurrence occurrence in occurrences)
                {
                    if (occurrence.LineEntity != chain.LineEntity)
                        candidateLines.Add(occurrence.LineEntity);
                }
            }

            foreach (Entity otherLine in candidateLines)
            {
                int runStart = -1;
                bool runMirrored = false;
                List<SharedTrackRun> runs = null;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane
                        || !TryGetSharedPhysicalContextForLine(chain.LineEntity, atom, otherLine, out bool mirroredContext))
                    {
                        if (runStart >= 0)
                        {
                            runs ??= new List<SharedTrackRun>();
                            runs.Add(new SharedTrackRun(runStart, atomIndex, runMirrored, 1));
                            runStart = -1;
                            runMirrored = false;
                        }

                        continue;
                    }

                    if (runStart < 0)
                    {
                        runStart = atomIndex;
                        runMirrored = mirroredContext;
                        continue;
                    }

                    runMirrored |= mirroredContext;
                }

                if (runStart >= 0)
                {
                    runs ??= new List<SharedTrackRun>();
                    runs.Add(new SharedTrackRun(runStart, chain.TrackAtoms.Count, runMirrored, 1));
                }

                if (runs != null && runs.Count > 0)
                    chain.SharedRunsByOtherLine[otherLine] = runs;
            }
        }

        private void EnsureTrackChainBypassPipelineReady(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            if (chain.BypassPipelineReadyVersion == m_SharedTrackIndexVersion
                && chain.ProtectedIntervalSummariesReady)
            {
                return;
            }

            RefreshSharedRuns(chain);
            RefreshControlEdgeSharedSpans(chain);
            RefreshBypassProtectedIntervals(chain);
            RefreshProtectedSharedIntervals(chain);
            RefreshProtectedIntervalSummaries(chain);

            chain.BypassPipelineReadyVersion =
                chain.SharedRunsVersion == m_SharedTrackIndexVersion
                && chain.ControlEdgeSharedSpansReady
                && chain.BypassProtectedIntervalsReady
                && chain.ProtectedSharedIntervalsReady
                && chain.ProtectedIntervalSummariesReady
                    ? m_SharedTrackIndexVersion
                    : 0;
        }

        private void EnsureLocalBypassWaypointScenesReady(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null || waypoints.Length == 0)
                return;

            if (chain.LocalBypassWaypointScenesVersion == chain.BypassPipelineReadyVersion
                && chain.LocalBypassWaypointScenesVersion != 0
                && chain.LocalBypassWaypointScenes != null
                && chain.LocalBypassWaypointScenes.Length == waypoints.Length)
            {
                return;
            }

            LocalBypassWaypointSceneBinding[] bindings = new LocalBypassWaypointSceneBinding[waypoints.Length];
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!TryGetBypassWaypointContext(
                        waypoints,
                        waypointIndex,
                        out Entity currentBypassBuilding,
                        out _,
                        out Entity nextBypassBuilding))
                {
                    continue;
                }

                if (!TryResolveBypassProtectedInterval(
                        chain,
                        waypoints,
                        waypointIndex,
                        out int protectedIntervalIndex,
                        out BypassProtectedInterval protectedInterval)
                    || protectedIntervalIndex < 0
                    || protectedIntervalIndex >= chain.ProtectedIntervalSummaries.Count)
                {
                    continue;
                }

                ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[protectedIntervalIndex];
                float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(chain, protectedInterval, currentBypassBuilding);
                float intervalDisplayLength = GetProtectedIntervalDisplayLength(protectedInterval);
                SceneKey sceneKey = new SceneKey(
                    chain.LineEntity,
                    currentBypassBuilding,
                    nextBypassBuilding,
                    protectedIntervalIndex);
                bindings[waypointIndex] = new LocalBypassWaypointSceneBinding(
                    true,
                    sceneKey,
                    currentBypassBuilding,
                    nextBypassBuilding,
                    protectedIntervalIndex,
                    protectedInterval,
                    summary,
                    departureReleaseCoordinate,
                    intervalDisplayLength);
            }

            chain.LocalBypassWaypointScenes = bindings;
            chain.LocalBypassWaypointScenesVersion = chain.BypassPipelineReadyVersion;
        }

        private void EnsureLineBypassExecutionModeReady(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null || waypoints.Length == 0)
                return;

            EnsureTrackChainBypassPipelineReady(chain);
            EnsureLocalBypassWaypointScenesReady(chain, waypoints);
            if (chain.BypassExecutionModeVersion == chain.LocalBypassWaypointScenesVersion
                && chain.BypassExecutionModeVersion != 0)
            {
                return;
            }

            int sceneCount = 0;
            int maxExpressLinesPerScene = 0;
            int multiTrunkSceneCount = 0;
            var uniqueScenes = new HashSet<SceneKey>();
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);

            for (int waypointIndex = 0; waypointIndex < chain.LocalBypassWaypointScenes.Length; waypointIndex++)
            {
                LocalBypassWaypointSceneBinding binding = chain.LocalBypassWaypointScenes[waypointIndex];
                if (!binding.Available || !uniqueScenes.Add(binding.SceneKey))
                    continue;

                sceneCount++;
                List<Entity> candidateExpressLines = GetCandidateExpressLinesForLocalScene(
                    chain,
                    binding.ProtectedInterval,
                    binding.CurrentBypassBuilding);
                int expressLineCount = candidateExpressLines != null ? candidateExpressLines.Count : 0;
                if (expressLineCount > maxExpressLinesPerScene)
                    maxExpressLinesPerScene = expressLineCount;

                bool sceneHasMultiTrunk = false;
                if (candidateExpressLines != null)
                {
                    for (int expressIndex = 0; expressIndex < candidateExpressLines.Count; expressIndex++)
                    {
                        Entity expressLine = candidateExpressLines[expressIndex];
                        if (expressLine == Entity.Null
                            || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                            || !TryBuildSceneExpressRelation(
                                chain,
                                binding.ProtectedIntervalIndex,
                                binding.ProtectedInterval,
                                binding.CurrentBypassBuilding,
                                expressLine,
                                expressWaypoints,
                                out SceneExpressRelation relation))
                        {
                            continue;
                        }

                        if (relation.TrunkCandidates != null && relation.TrunkCandidates.Segments.Count > 1)
                        {
                            sceneHasMultiTrunk = true;
                            break;
                        }
                    }
                }

                if (sceneHasMultiTrunk)
                    multiTrunkSceneCount++;
            }

            chain.LocalBypassSceneCount = sceneCount;
            chain.MaxExpressLinesPerScene = maxExpressLinesPerScene;
            chain.MultiTrunkSceneCount = multiTrunkSceneCount;
            chain.ExecutionMode =
                sceneCount <= 2
                && maxExpressLinesPerScene <= 1
                && multiTrunkSceneCount == 0
                    ? BypassExecutionMode.SimpleSceneScan
                    : BypassExecutionMode.ComplexLineModel;
            chain.BypassExecutionModeVersion = chain.LocalBypassWaypointScenesVersion;
            if (IsLineOrderedRuntimeLoggingEnabled())
            {
                string modeSummary = chain.ExecutionMode
                    + "|scenes=" + sceneCount
                    + "|maxExpressPerScene=" + maxExpressLinesPerScene
                    + "|multiTrunkScenes=" + multiTrunkSceneCount;
                if (!m_LineBypassExecutionModeLogCache.TryGetValue(chain.LineEntity, out string previousSummary)
                    || previousSummary != modeSummary)
                {
                    m_LineBypassExecutionModeLogCache[chain.LineEntity] = modeSummary;
                    log.Info("[LineBypassMode] line=" + chain.LineEntity.Index
                        + " mode=" + chain.ExecutionMode
                        + " scenes=" + sceneCount
                        + " maxExpressPerScene=" + maxExpressLinesPerScene
                        + " multiTrunkScenes=" + multiTrunkSceneCount);
                }
            }
        }

        private void RefreshControlEdgeSharedSpans(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdgeSharedSpansReady)
                return;

            chain.ControlEdgeSharedSpans.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.SharedRuns.Count == 0 || chain.ControlEdges.Count == 0)
            {
                chain.ControlEdgeSharedSpansReady = true;
                return;
            }

            for (int controlEdgeIndex = 0; controlEdgeIndex < chain.ControlEdges.Count; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                for (int runIndex = 0; runIndex < chain.SharedRuns.Count; runIndex++)
                {
                    SharedTrackRun run = chain.SharedRuns[runIndex];
                    int overlapStart = math.max(edge.StartAtomIndex, run.StartAtomIndex);
                    int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, run.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    chain.ControlEdgeSharedSpans.Add(new ControlEdgeSharedSpan(
                        controlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        run.HasMirroredContext,
                        run.SharedLineCount));
                }
            }

            chain.ControlEdgeSharedSpansReady = true;
        }

        private void RefreshBypassProtectedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.BypassProtectedIntervalsReady)
                return;

            chain.BypassProtectedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.ControlPoints.Count < 2 || chain.ControlEdges.Count == 0)
            {
                chain.BypassProtectedIntervalsReady = true;
                return;
            }

            for (int startControlPointIndex = 0; startControlPointIndex < chain.ControlPoints.Count - 1; startControlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[startControlPointIndex];
                if (start.Kind != ControlPointKind.Bypass)
                    continue;

                int endControlPointIndex = -1;
                for (int candidateIndex = startControlPointIndex + 1; candidateIndex < chain.ControlPoints.Count; candidateIndex++)
                {
                    if (chain.ControlPoints[candidateIndex].Kind == ControlPointKind.Bypass)
                    {
                        endControlPointIndex = candidateIndex;
                        break;
                    }
                }

                if (endControlPointIndex <= startControlPointIndex)
                    continue;

                int startControlEdgeIndex = startControlPointIndex;
                int endControlEdgeIndexInclusive = endControlPointIndex - 1;
                if (startControlEdgeIndex < 0 || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
                    continue;

                int startAtomIndex = math.max(0, chain.ControlPoints[startControlPointIndex].AtomIndex);
                int endAtomIndexExclusive = math.max(startAtomIndex + 1, chain.ControlPoints[endControlPointIndex].AtomIndex);
                float baseFrames = 0f;
                for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
                    baseFrames += chain.ControlEdges[controlEdgeIndex].BaseFrames;

                chain.BypassProtectedIntervals.Add(new BypassProtectedInterval(
                    startControlPointIndex,
                    endControlPointIndex,
                    startControlEdgeIndex,
                    endControlEdgeIndexInclusive,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }

            chain.BypassProtectedIntervalsReady = true;
        }

        private void RefreshProtectedSharedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedSharedIntervalsReady)
                return;

            chain.ProtectedSharedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.BypassProtectedIntervals.Count == 0 || chain.ControlEdgeSharedSpans.Count == 0)
            {
                chain.ProtectedSharedIntervalsReady = true;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                BypassProtectedInterval interval = chain.BypassProtectedIntervals[protectedIntervalIndex];
                for (int spanIndex = 0; spanIndex < chain.ControlEdgeSharedSpans.Count; spanIndex++)
                {
                    ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[spanIndex];
                    if (span.ControlEdgeIndex < interval.StartControlEdgeIndex || span.ControlEdgeIndex > interval.EndControlEdgeIndexInclusive)
                        continue;

                    int overlapStart = math.max(interval.StartAtomIndex, span.StartAtomIndex);
                    int overlapEndExclusive = math.min(interval.EndAtomIndexExclusive, span.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    float entryOffsetFrames = EstimateFramesBetweenAtoms(chain, interval.StartControlEdgeIndex, span.ControlEdgeIndex, interval.StartAtomIndex, overlapStart);
                    float clearOffsetFrames = EstimateFramesBetweenAtoms(chain, span.ControlEdgeIndex, interval.EndControlEdgeIndexInclusive, overlapEndExclusive, interval.EndAtomIndexExclusive);
                    chain.ProtectedSharedIntervals.Add(new ProtectedSharedInterval(
                        protectedIntervalIndex,
                        span.ControlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        span.HasMirroredContext,
                        span.SharedLineCount,
                        entryOffsetFrames,
                        clearOffsetFrames));
                }
            }

            chain.ProtectedSharedIntervalsReady = true;
        }

        private void RefreshProtectedIntervalSummaries(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedIntervalSummariesReady)
                return;

            chain.ProtectedIntervalSummaries.Clear();
            chain.BypassPipelineReadyVersion = 0;
            if (chain.BypassProtectedIntervals.Count == 0)
            {
                chain.ProtectedIntervalSummariesReady = true;
                chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_SharedTrackIndexVersion ? m_SharedTrackIndexVersion : 0;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                int sharedSegmentCount = 0;
                int maxSharedLineCount = 0;
                bool hasMirroredContext = false;
                float minEntryOffsetFrames = float.MaxValue;
                float maxClearOffsetFrames = 0f;

                for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
                {
                    ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                    if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                        continue;

                    sharedSegmentCount++;
                    maxSharedLineCount = math.max(maxSharedLineCount, interval.SharedLineCount);
                    hasMirroredContext |= interval.HasMirroredContext;
                    minEntryOffsetFrames = math.min(minEntryOffsetFrames, interval.EntryOffsetFrames);
                    maxClearOffsetFrames = math.max(maxClearOffsetFrames, interval.ClearOffsetFrames);
                }

                if (sharedSegmentCount == 0)
                {
                    minEntryOffsetFrames = 0f;
                    maxClearOffsetFrames = 0f;
                }

                chain.ProtectedIntervalSummaries.Add(new ProtectedIntervalSummary(
                    protectedIntervalIndex,
                    sharedSegmentCount,
                    maxSharedLineCount,
                    hasMirroredContext,
                    minEntryOffsetFrames,
                    maxClearOffsetFrames));
            }

            chain.ProtectedIntervalSummariesReady = true;
            chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_SharedTrackIndexVersion ? m_SharedTrackIndexVersion : 0;
        }

        private float EstimateFramesBetweenAtoms(LineTrackChain chain, int startControlEdgeIndex, int endControlEdgeIndexInclusive, int fromAtomIndex, int toAtomIndexExclusive)
        {
            if (toAtomIndexExclusive <= fromAtomIndex
                || startControlEdgeIndex < 0
                || endControlEdgeIndexInclusive < startControlEdgeIndex
                || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
            {
                return 0f;
            }

            float frames = 0f;
            for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                int overlapStart = math.max(edge.StartAtomIndex, fromAtomIndex);
                int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, toAtomIndexExclusive);
                if (overlapEndExclusive <= overlapStart)
                    continue;

                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                int overlapAtomLength = overlapEndExclusive - overlapStart;
                frames += edge.BaseFrames * (overlapAtomLength / (float)edgeAtomLength);
            }

            return frames;
        }

        private static float EstimateAverageControlEdgeFramesPerAtom(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdges == null || chain.ControlEdges.Count == 0)
                return 0f;

            float totalFrames = 0f;
            int totalAtoms = 0;
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                totalFrames += edge.BaseFrames;
                totalAtoms += edgeAtomLength;
            }

            return totalAtoms > 0 ? totalFrames / totalAtoms : 0f;
        }

        private bool TryResolveBypassProtectedInterval(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null || waypoints.Length == 0)
                return false;

            if (!TryGetBypassWaypointContext(
                    waypoints,
                    currentWaypointIndex,
                    out Entity currentBypassBuilding,
                    out _,
                    out Entity nextBypassBuilding))
            {
                return false;
            }

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                int startWaypointIndex = chain.ControlPoints[candidate.StartControlPointIndex].WaypointIndex;
                int endWaypointIndex = chain.ControlPoints[candidate.EndControlPointIndex].WaypointIndex;
                Entity startBuilding = chain.ControlPoints[candidate.StartControlPointIndex].Building;
                Entity endBuilding = chain.ControlPoints[candidate.EndControlPointIndex].Building;
                if (startWaypointIndex != currentWaypointIndex
                    || startBuilding != currentBypassBuilding
                    || endBuilding != nextBypassBuilding)
                {
                    continue;
                }

                protectedIntervalIndex = i;
                protectedInterval = candidate;
                return true;
            }

            return false;
        }

        private bool TryGetLocalBypassSceneStaticSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out LineTrackChain chain,
            out LocalBypassSceneStaticSnapshot snapshot)
        {
            chain = null;
            snapshot = default;
            if (line == Entity.Null
                || currentWaypointIndex < 0
                || currentWaypointIndex >= waypoints.Length
                || !TryGetLineTrackChain(line, waypoints, out chain))
            {
                return false;
            }

            EnsureTrackChainBypassPipelineReady(chain);
            EnsureLocalBypassWaypointScenesReady(chain, waypoints);
            LocalBypassSceneStaticKey key = new LocalBypassSceneStaticKey(line, currentWaypointIndex);
            if (m_LocalBypassSceneStaticSnapshots.TryGetValue(key, out snapshot)
                && snapshot.LineChainSignature == chain.Signature)
            {
                return true;
            }

            if (chain.LocalBypassWaypointScenes == null
                || currentWaypointIndex < 0
                || currentWaypointIndex >= chain.LocalBypassWaypointScenes.Length)
            {
                return false;
            }

            LocalBypassWaypointSceneBinding binding = chain.LocalBypassWaypointScenes[currentWaypointIndex];
            if (!binding.Available)
                return false;

            snapshot = new LocalBypassSceneStaticSnapshot(
                binding.SceneKey,
                chain.Signature,
                binding.CurrentBypassBuilding,
                binding.NextBypassBuilding,
                binding.ProtectedIntervalIndex,
                binding.ProtectedInterval,
                binding.Summary,
                binding.DepartureReleaseCoordinate,
                binding.IntervalDisplayLength);
            m_LocalBypassSceneStaticSnapshots[key] = snapshot;
            return true;
        }

        private bool TryGetLocalSceneDefinition(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out LineTrackChain chain,
            out SceneDefinition scene)
        {
            chain = null;
            scene = default;
            if (!TryGetLocalBypassSceneStaticSnapshot(
                    line,
                    waypoints,
                    currentWaypointIndex,
                    out chain,
                    out LocalBypassSceneStaticSnapshot snapshot))
            {
                return false;
            }

            scene = new SceneDefinition(
                snapshot.SceneKey,
                line,
                currentWaypointIndex,
                snapshot.CurrentBypassBuilding,
                snapshot.NextBypassBuilding,
                snapshot.ProtectedIntervalIndex,
                snapshot.ProtectedInterval,
                snapshot.Summary,
                snapshot.DepartureReleaseCoordinate,
                snapshot.IntervalDisplayLength);
            return true;
        }

        private static int FindControlPointIndex(LineTrackChain chain, int waypointIndex, Entity building, ControlPointKind kind)
        {
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker marker = chain.ControlPoints[i];
                if (marker.Kind == kind
                    && marker.WaypointIndex == waypointIndex
                    && marker.Building == building)
                {
                    return i;
                }
            }

            return -1;
        }

        private int CountProtectedSharedIntervals(LineTrackChain chain, int protectedIntervalIndex, out bool hasMirroredContext)
        {
            hasMirroredContext = false;
            if (protectedIntervalIndex < 0)
                return 0;

            int count = 0;
            for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
            {
                ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                    continue;

                count++;
                hasMirroredContext |= interval.HasMirroredContext;
            }

            return count;
        }

        private static string FormatProtectedIntervalSummary(ProtectedIntervalSummary summary, BypassProtectedInterval interval)
        {
            return "trackModel[p=" + summary.ProtectedIntervalIndex
                + " cp=" + interval.StartControlPointIndex + "->" + interval.EndControlPointIndex
                + " edges=" + interval.StartControlEdgeIndex + ".." + interval.EndControlEdgeIndexInclusive
                + " shared=" + summary.SharedSegmentCount
                + " maxSharedLines=" + summary.MaxSharedLineCount
                + " mirrored=" + (summary.HasMirroredContext ? "1" : "0")
                + " minEntry=" + summary.MinEntryOffsetFrames.ToString("F1")
                + " maxClear=" + summary.MaxClearOffsetFrames.ToString("F1")
                + "]";
        }

        private static string ClassifyProtectedIntervalTrackModelRisk(ProtectedIntervalSummary summary)
        {
            if (summary.SharedSegmentCount <= 0)
                return "none";

            if (summary.HasMirroredContext)
                return "mirrored-shared";

            return "shared";
        }

        private bool TryGetForwardStationExitCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out float stationExitCoordinate)
        {
            stationExitCoordinate = -1f;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            int lastForwardStationAtomIndex = -1;
            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                lastForwardStationAtomIndex = atomIndex;
            }

            if (lastForwardStationAtomIndex < localProtectedInterval.StartAtomIndex)
                return false;

            stationExitCoordinate = (lastForwardStationAtomIndex - localProtectedInterval.StartAtomIndex) + 1f;
            return true;
        }

        private bool TryGetForwardStationExitAtomIndex(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out int stationExitAtomIndex)
        {
            stationExitAtomIndex = -1;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                stationExitAtomIndex = atomIndex;
            }

            return stationExitAtomIndex >= localProtectedInterval.StartAtomIndex;
        }

        private float ComputeForwardDepartureReleaseCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding)
        {
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(localProtectedInterval);
            float fallback = math.min(intervalDisplayLength, LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
            if (!TryGetForwardStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return fallback;

            return math.min(intervalDisplayLength, stationExitCoordinate + LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
        }
    }
}
