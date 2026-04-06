using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
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
            m_BypassTrackModelShadowLogCache.Clear();
            m_BypassTrackModelShadowThrottleCache.Clear();
            m_BypassTrackModelShadowLastLogFrame.Clear();
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
            m_BypassTrackModelShadowSnapshots.Clear();
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

            List<Entity> shadowSnapshotKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassTrackModelShadowSnapshot> entry in m_BypassTrackModelShadowSnapshots)
            {
                if (entry.Value.Line != line)
                    continue;

                shadowSnapshotKeysToRemove ??= new List<Entity>();
                shadowSnapshotKeysToRemove.Add(entry.Key);
            }

            if (shadowSnapshotKeysToRemove != null)
            {
                for (int i = 0; i < shadowSnapshotKeysToRemove.Count; i++)
                {
                    Entity vehicle = shadowSnapshotKeysToRemove[i];
                    m_BypassTrackModelShadowSnapshots.Remove(vehicle);
                    m_BypassTrackModelShadowLogCache.Remove(vehicle);
                    m_BypassTrackModelShadowThrottleCache.Remove(vehicle);
                    m_BypassTrackModelShadowLastLogFrame.Remove(vehicle);
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

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
                return false;

            ulong signature = ComputeLineTrackChainSignature(line, waypoints, segments);
            LineTrackChain previousChain = null;
            if (m_LineTrackChains.TryGetValue(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                return chain.TrackAtoms.Count > 0;
            }

            previousChain = chain;

            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            if (previousChain != null)
                RemoveDevSightLaneIndexForChain(previousChain);
            m_LineTrackChains[line] = chain;
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
            BuildTurnbackBoundaries(chain);
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

        private bool IsStaticTurnbackBoundary(
            TraversalRunSlice beforeSlice,
            TraversalRunSlice afterSlice)
        {
            if (beforeSlice.PhysicalLaneKeys == null
                || afterSlice.PhysicalLaneKeys == null
                || beforeSlice.PhysicalLaneKeys.Length == 0
                || afterSlice.PhysicalLaneKeys.Length == 0)
            {
                return false;
            }

            int forwardScore = ComputeOrderedPhysicalKeyLcsLength(beforeSlice.PhysicalLaneKeys, afterSlice.PhysicalLaneKeys);
            int reverseScore = ComputeOrderedPhysicalKeyLcsLength(beforeSlice.PhysicalLaneKeys, ReversePhysicalLaneKeys(afterSlice.PhysicalLaneKeys));
            int minSharedCount = math.min(beforeSlice.PhysicalLaneKeys.Length, afterSlice.PhysicalLaneKeys.Length);
            if (minSharedCount <= 0)
                return false;

            int requiredReverseScore = math.min(3, minSharedCount);
            if (requiredReverseScore < 2)
                requiredReverseScore = minSharedCount;

            return reverseScore >= requiredReverseScore
                && reverseScore > forwardScore;
        }

        private void BuildTurnbackBoundaries(LineTrackChain chain)
        {
            if (chain == null)
                return;

            chain.TurnbackBoundaries.Clear();
            if (chain.TraversalProfile == null || chain.TraversalProfile.RunSlices.Count < 2)
                return;

            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count - 1; sliceIndex++)
            {
                TraversalRunSlice beforeSlice = chain.TraversalProfile.RunSlices[sliceIndex];
                TraversalRunSlice afterSlice = chain.TraversalProfile.RunSlices[sliceIndex + 1];
                if (beforeSlice.EndAtomIndexExclusive != afterSlice.StartAtomIndex)
                    continue;
                if (!IsStaticTurnbackBoundary(beforeSlice, afterSlice))
                    continue;

                chain.TurnbackBoundaries.Add(new TurnbackBoundary(
                    beforeSlice.EndAtomIndexExclusive,
                    beforeSlice.SliceIndex,
                    afterSlice.SliceIndex,
                    beforeSlice.EndEventIndex));
            }
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
            if (chain == null || chain.TrackAtoms.Count == 0 || endAtomIndexExclusive <= startAtomIndex)
                return Array.Empty<Entity>();

            HashSet<Entity> keys = new HashSet<Entity>();
            int start = math.max(0, startAtomIndex);
            int end = math.min(endAtomIndexExclusive, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                keys.Add(atom.Key.PhysicalLaneKey);
            }

            if (keys.Count == 0)
                return Array.Empty<Entity>();

            Entity[] result = new Entity[keys.Count];
            keys.CopyTo(result);
            return result;
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

        private static string ClassifyProtectedIntervalShadowRisk(ProtectedIntervalSummary summary)
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
