using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackProjection
{
    internal sealed class TrackProjectionService
    {
        internal const uint SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES = 60;
        internal const int SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS = 1;
        internal const int SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD = 12;
        internal const float SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS = 120f;

        private readonly ITrackProjectionRuntimeContext m_Runtime;
        private readonly ProgressCheck m_ProgressCheck;

        internal TrackProjectionService(ITrackProjectionRuntimeContext runtime)
        {
            m_Runtime = runtime;
            m_Cursors = new VehicleTrackCursorCache();
            m_ProgressCheck = new ProgressCheck(this);
        }

        internal ITrackProjectionRuntimeContext Runtime => m_Runtime;
        internal VehicleTrackCursorCache Cursors => m_Cursors;
        private readonly VehicleTrackCursorCache m_Cursors;
        private readonly Dictionary<Entity, VehicleTrackFacts> m_Facts = new Dictionary<Entity, VehicleTrackFacts>();
        internal readonly Dictionary<Entity, LineRunningVehicleFrameSnapshot> LineRunningVehicleFrameSnapshots = new Dictionary<Entity, LineRunningVehicleFrameSnapshot>();

        internal void Clear()
        {
            m_Cursors.Clear();
            m_Facts.Clear();
            m_ProgressCheck.Clear();
        }

        internal void ClearLineRunningVehicleSnapshots()
        {
            LineRunningVehicleFrameSnapshots.Clear();
        }

        internal bool TryGetLineRunningVehicleFrameSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineRunningVehicleFrameSnapshot snapshot)
        {
            snapshot = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            if (LineRunningVehicleFrameSnapshots.TryGetValue(line, out snapshot)
                && snapshot != null
                && snapshot.Frame == nowFrame
                && snapshot.Line == line)
            {
                return true;
            }

            BufferLookup<RouteVehicle> routeVehicleBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            if (snapshot == null)
            {
                snapshot = new LineRunningVehicleFrameSnapshot();
                LineRunningVehicleFrameSnapshots[line] = snapshot;
            }

            snapshot.Frame = nowFrame;
            snapshot.Line = line;
            snapshot.Vehicles.Clear();

            bool hasTrackChain = m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain trackChain);

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = routeVehicles[i].m_Vehicle;
                if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                    continue;
                if (!m_Runtime.TryGetVehicleRuntimeState(vehicle, out VehicleState vehicleState) || vehicleState != VehicleState.Running)
                    continue;

                bool hasTrackCursor = false;
                VehicleTrackCursor trackCursor = default;
                int currentControlEdgeIndex = -1;
                float ownLineAtomCoordinate = 0f;
                int phaseEndAtomExclusive = -1;
                int traversalPhaseIndex = -1;
                int traversalPhaseStartAtomIndex = -1;
                int traversalPhaseEndAtomExclusive = -1;
                int nextTurnbackBoundaryAtomIndex = -1;
                if (hasTrackChain)
                {
                    hasTrackCursor = TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                        vehicle,
                        line,
                        waypoints,
                        trackChain,
                        out trackCursor,
                        out currentControlEdgeIndex,
                        out ownLineAtomCoordinate,
                        out phaseEndAtomExclusive,
                        out traversalPhaseIndex,
                        out traversalPhaseStartAtomIndex,
                        out traversalPhaseEndAtomExclusive,
                        out nextTurnbackBoundaryAtomIndex);
                }

                snapshot.Vehicles.Add(new LineRunningVehicleSnapshot(
                    vehicle,
                    m_Runtime.IsVehicleBoarding(vehicle),
                    false,
                    0f,
                    hasTrackCursor,
                    trackCursor,
                    currentControlEdgeIndex,
                    ownLineAtomCoordinate,
                    phaseEndAtomExclusive,
                    traversalPhaseIndex,
                    traversalPhaseStartAtomIndex,
                    traversalPhaseEndAtomExclusive,
                    nextTurnbackBoundaryAtomIndex));
            }

            return true;
        }

        internal bool TrySnapshot(Entity vehicle, Entity line, ulong chainSignature, uint frame, out VehicleTrackCursor cursor)
        {
            return m_Cursors.TrySnapshot(vehicle, line, chainSignature, frame, out cursor);
        }

        internal void ClearFacts(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_Facts.Remove(vehicle);
        }

        internal void ClearVehicle(Entity vehicle)
        {
            ClearVehicleProgressSuspect(vehicle);
            m_Cursors.Remove(vehicle);
            m_Facts.Remove(vehicle);
        }

        internal void MarkVehicleProgressSuspect(Entity vehicle, string reason) => m_ProgressCheck.MarkVehicleProgressSuspect(vehicle, reason);
        internal void ClearVehicleProgressSuspect(Entity vehicle, string reason = null) => m_ProgressCheck.ClearVehicleProgressSuspect(vehicle, reason);
        internal void NoteVehicleProgressSuspectRecoveryBoarding(Entity vehicle, int waypointIndex) => m_ProgressCheck.NoteVehicleProgressSuspectRecoveryBoarding(vehicle, waypointIndex);
        internal void TryClearVehicleProgressSuspectOnStableDeparture(Entity vehicle, int departedWaypointIndex) => m_ProgressCheck.TryClearVehicleProgressSuspectOnStableDeparture(vehicle, departedWaypointIndex);
        internal bool IsVehicleProgressProjectionInvalid(Entity vehicle, Entity line, LineTrackChain chain, int segmentIndex, int projectedAtomIndex) => m_ProgressCheck.IsVehicleProgressProjectionInvalid(vehicle, line, chain, segmentIndex, projectedAtomIndex);

        private bool TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition) => m_Runtime.TryRouteProgress(vehicle, out nextWaypointIndex, out segmentPosition);
        private static bool TryResolveTraversalOrderingPhase(LineTrackChain chain, int atomIndex, out int traversalPhaseIndex, out int phaseStartAtomIndex, out int phaseEndAtomExclusive, out int nextTurnbackBoundaryAtomIndex)
        {
            traversalPhaseIndex = -1;
            phaseStartAtomIndex = -1;
            phaseEndAtomExclusive = -1;
            nextTurnbackBoundaryAtomIndex = -1;
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            int cursor = math.clamp(atomIndex, 0, chain.TrackAtoms.Count - 1);
            int phaseStart = 0;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                int boundaryAtomIndex = math.clamp(chain.TurnbackBoundaries[boundaryIndex].AtomIndex, 0, chain.TrackAtoms.Count);
                if (cursor < boundaryAtomIndex)
                {
                    traversalPhaseIndex = boundaryIndex;
                    phaseStartAtomIndex = phaseStart;
                    phaseEndAtomExclusive = boundaryAtomIndex;
                    nextTurnbackBoundaryAtomIndex = boundaryAtomIndex;
                    return true;
                }

                phaseStart = boundaryAtomIndex;
            }

            traversalPhaseIndex = chain.TurnbackBoundaries.Count;
            phaseStartAtomIndex = phaseStart;
            phaseEndAtomExclusive = chain.TrackAtoms.Count;
            return true;
        }

        private static bool TryGetExpressCurrentForwardPhaseWindow(LineTrackChain chain, int atomIndex, out int phaseEndAtomExclusive)
        {
            return TryResolveTraversalOrderingPhase(chain, atomIndex, out _, out _, out phaseEndAtomExclusive, out _);
        }

        private static bool TryFindClosestAtomIndexForLane(
            LineTrackChain chain,
            Entity lane,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int referenceAtomIndex,
            out int atomIndex)
        {
            atomIndex = -1;
            if (chain == null
                || lane == Entity.Null
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            if (!chain.AtomIndicesByLane.TryGetValue(lane, out List<int> candidateAtomIndices)
                || candidateAtomIndices == null
                || candidateAtomIndices.Count == 0)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);
            for (int candidateIndex = 0; candidateIndex < candidateAtomIndices.Count; candidateIndex++)
            {
                int index = candidateAtomIndices[candidateIndex];
                if (index < startAtomIndex || index >= endAtomIndexExclusive)
                    continue;

                int distance = math.abs(index - referenceAtomIndex);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                atomIndex = index;
            }

            return atomIndex >= 0;
        }

        private static int ResolveSegmentIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            if (chain == null || chain.SegmentRanges.Count == 0 || atomIndex < 0)
                return -1;

            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        internal bool TryGetTrackAtomWorldPosition(LineTrackChain chain, int atomIndex, out float3 position)
        {
            position = default;
            if (chain == null || atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return false;

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (TryGetTrackAtomCurveWorldPosition(atom, out position))
                return true;

            return TryGetEntityWorldPosition(atom.SourceTarget, out position)
                || TryGetEntityWorldPosition(atom.Key.PhysicalLaneKey, out position);
        }

        private bool TryGetTrackAtomCurveWorldPosition(TrackAtom atom, out float3 position)
        {
            position = default;
            if (TryGetEntityCurveWorldPosition(atom.SourceTarget, atom.TargetDelta.x, out position))
                return true;

            if (atom.Key.PhysicalLaneKey != atom.SourceTarget
                && TryGetEntityCurveWorldPosition(atom.Key.PhysicalLaneKey, atom.TargetDelta.x, out position))
            {
                return true;
            }

            return false;
        }

        private bool TryGetEntityCurveWorldPosition(Entity entity, float curvePosition, out float3 position)
        {
            position = default;
            if (entity == Entity.Null
                || !m_Runtime.EntityManager.Exists(entity)
                || !m_Runtime.EntityManager.HasComponent<Curve>(entity))
            {
                return false;
            }

            Curve curve = m_Runtime.EntityManager.GetComponentData<Curve>(entity);
            position = MathUtils.Position(curve.m_Bezier, math.saturate(curvePosition));
            return true;
        }

        private bool TryGetEntityWorldPosition(Entity entity, out float3 position)
        {
            position = default;
            if (entity == Entity.Null || !m_Runtime.EntityManager.Exists(entity))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Position>(entity))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(entity).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                return true;
            }

            return false;
        }

        private bool TryResolveWaypointAnchorConflict(
            float3 vehiclePosition,
            DynamicBuffer<RouteWaypoint> waypoints,
            int routeProgressNextWaypointIndex,
            out int nearbyWaypointIndex)
        {
            nearbyWaypointIndex = -1;
            const float stationAnchorRadiusMeters = 420f;

            float bestDistance = float.MaxValue;
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!TryGetWaypointWorldPosition(waypoints[waypointIndex].m_Waypoint, out float3 waypointPosition))
                    continue;

                float distance = math.distance(vehiclePosition, waypointPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearbyWaypointIndex = waypointIndex;
                }
            }

            if (nearbyWaypointIndex < 0 || bestDistance > stationAnchorRadiusMeters)
                return false;

            int waypointDelta = math.abs(routeProgressNextWaypointIndex - nearbyWaypointIndex);
            bool wrappedNeighbor =
                (routeProgressNextWaypointIndex == 0 && nearbyWaypointIndex == waypoints.Length - 1)
                || (nearbyWaypointIndex == 0 && routeProgressNextWaypointIndex == waypoints.Length - 1);

            return waypointDelta >= 1 && !wrappedNeighbor;
        }

        internal bool TryGetVehicleWorldPosition(Entity vehicle, out float3 position)
        {
            position = default;
            if (!m_Runtime.EntityManager.Exists(vehicle))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Position>(vehicle))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(vehicle).m_Position;
                return true;
            }

            return false;
        }

        private bool TryGetWaypointWorldPosition(Entity waypoint, out float3 position)
        {
            position = default;
            if (waypoint == Entity.Null || !m_Runtime.EntityManager.Exists(waypoint))
                return false;

            if (m_Runtime.EntityManager.HasComponent<Position>(waypoint))
            {
                position = m_Runtime.EntityManager.GetComponentData<Position>(waypoint).m_Position;
                return true;
            }

            if (m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(waypoint))
            {
                position = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(waypoint).m_Position;
                return true;
            }

            return false;
        }

        private bool TryResolveTrainCurrentLaneCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null
                || chain == null
                || !m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(vehicle))
            {
                return false;
            }

            m_Runtime.CountNavigationDetailRead();
            TrainCurrentLane currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(vehicle);
            Entity frontLane = currentLane.m_Front.m_Lane;
            Entity rearLane = currentLane.m_Rear.m_Lane;
            float frontCurvePosition = math.saturate(currentLane.m_Front.m_CurvePosition.x);
            float rearCurvePosition = math.saturate(currentLane.m_Rear.m_CurvePosition.x);

            int referenceAtomIndex = m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature
                ? hint.AtomCursorIndex
                : -1;

            int preferredSegmentIndex = m_Cursors.TryCursor(vehicle, out VehicleTrackCursor segmentHint)
                && segmentHint.LineEntity == line
                && segmentHint.ChainSignature == chain.Signature
                ? segmentHint.SegmentIndex
                : -1;

            bool found = TryResolveSemanticLaneAtomCandidate(
                vehicle,
                line,
                waypoints,
                chain,
                frontLane,
                referenceAtomIndex,
                out int atomIndex);
            float atomPosition01 = frontCurvePosition;
            if (!found)
            {
                found = TryResolveSemanticLaneAtomCandidate(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    rearLane,
                    referenceAtomIndex,
                    out atomIndex);
                atomPosition01 = rearCurvePosition;
            }

            int searchStartAtomIndex = 0;
            int searchEndAtomIndexExclusive = chain.TrackAtoms.Count;
            if (preferredSegmentIndex >= 0 && preferredSegmentIndex < chain.SegmentRanges.Count)
            {
                int searchStartSegmentIndex = math.max(0, preferredSegmentIndex - 1);
                int searchEndSegmentIndex = math.min(chain.SegmentRanges.Count - 1, preferredSegmentIndex + 1);
                searchStartAtomIndex = chain.SegmentRanges[searchStartSegmentIndex].StartAtomIndex;
                searchEndAtomIndexExclusive = chain.SegmentRanges[searchEndSegmentIndex].EndAtomIndexExclusive;
            }

            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, frontLane, searchStartAtomIndex, searchEndAtomIndexExclusive, referenceAtomIndex, out atomIndex);
                atomPosition01 = frontCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, rearLane, searchStartAtomIndex, searchEndAtomIndexExclusive, referenceAtomIndex, out atomIndex);
                atomPosition01 = rearCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, frontLane, 0, chain.TrackAtoms.Count, referenceAtomIndex, out atomIndex);
                atomPosition01 = frontCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, rearLane, 0, chain.TrackAtoms.Count, referenceAtomIndex, out atomIndex);
                atomPosition01 = rearCurvePosition;
            }
            if (!found)
                return false;

            int segmentIndex = ResolveSegmentIndexForAtom(chain, atomIndex);
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                atomIndex,
                atomPosition01,
                1f,
                VehicleTrackCursorSource.CurrentLane);
            return true;
        }

        private bool TryResolveSemanticLaneAtomCandidate(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            Entity lane,
            int referenceAtomIndex,
            out int atomIndex)
        {
            atomIndex = -1;
            if (lane == Entity.Null
                || chain == null
                || chain.SegmentRanges.Count == 0
                || waypoints.Length == 0)
            {
                return false;
            }

            int segmentCount = chain.SegmentRanges.Count;
            List<int> semanticSegments = new List<int>(4);
            void AddSegmentCandidate(int segmentIndex)
            {
                if (segmentIndex < 0)
                    return;

                int normalized = segmentIndex % segmentCount;
                if (normalized < 0)
                    normalized += segmentCount;

                if (!semanticSegments.Contains(normalized))
                    semanticSegments.Add(normalized);
            }

            void AddWaypointAnchor(int waypointIndex)
            {
                if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                    return;

                AddSegmentCandidate(waypointIndex == 0 ? segmentCount - 1 : waypointIndex - 1);
            }

            if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                AddSegmentCandidate(hint.SegmentIndex);
            }

            if (m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out int cachedWaypointIndex))
                AddWaypointAnchor(cachedWaypointIndex);

            if (m_Runtime.EntityManager.HasComponent<Target>(vehicle))
            {
                Entity targetWaypoint = m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target;
                if (m_Runtime.EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    AddWaypointAnchor(m_Runtime.EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index);
            }

            if (TryRouteProgress(vehicle, out int nextWaypointIndex, out _))
                AddWaypointAnchor(nextWaypointIndex);

            if (semanticSegments.Count == 0)
                return false;

            int[] segmentOffsets = new[] { 0, -1, 1 };
            int bestDistance = int.MaxValue;
            foreach (int baseSegmentIndex in semanticSegments)
            {
                for (int offsetIndex = 0; offsetIndex < segmentOffsets.Length; offsetIndex++)
                {
                    int segmentIndex = baseSegmentIndex + segmentOffsets[offsetIndex];
                    if (segmentIndex < 0)
                        segmentIndex += segmentCount;
                    else if (segmentIndex >= segmentCount)
                        segmentIndex -= segmentCount;

                    TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                    if (!TryFindClosestAtomIndexForLane(
                            chain,
                            lane,
                            segmentRange.StartAtomIndex,
                            segmentRange.EndAtomIndexExclusive,
                            referenceAtomIndex,
                            out int candidateAtomIndex))
                    {
                        continue;
                    }

                    if (referenceAtomIndex < 0)
                    {
                        atomIndex = candidateAtomIndex;
                        return true;
                    }

                    int candidateDistance = math.abs(candidateAtomIndex - referenceAtomIndex);
                    if (candidateDistance >= bestDistance)
                        continue;

                    bestDistance = candidateDistance;
                    atomIndex = candidateAtomIndex;
                }
            }

            return atomIndex >= 0;
        }

        internal bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (!m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || chain.SegmentRanges.Count == 0)
            {
                return false;
            }

            return TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor);
        }

        internal bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (chain == null || chain.SegmentRanges.Count == 0)
                return false;

            if (TryResolveTrainCurrentLaneCursor(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    out cursor))
            {
                if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor trainHint)
                    && trainHint.LineEntity == line
                    && trainHint.ChainSignature == chain.Signature)
                {
                    bool wrappedForward = trainHint.SegmentIndex >= chain.SegmentRanges.Count - 2 && cursor.SegmentIndex <= 1;
                    bool monotonicForward = cursor.SegmentIndex >= trainHint.SegmentIndex || wrappedForward;
                    if (!monotonicForward)
                    {
                        cursor = new VehicleTrackCursor(
                            cursor.LineEntity,
                            cursor.ChainSignature,
                            cursor.SegmentIndex,
                            cursor.AtomStartIndex,
                            cursor.AtomEndIndexExclusive,
                            cursor.AtomCursorIndex,
                            cursor.AtomPosition01,
                            cursor.Confidence * 0.7f,
                            cursor.Source);
                    }
                }

                if (IsVehicleProgressProjectionInvalid(vehicle, line, chain, cursor.SegmentIndex, cursor.AtomCursorIndex))
                    return false;

                return true;
            }

            bool trustedRouteProgress = TryRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition);
            VehicleTrackCursorSource cursorSource = trustedRouteProgress
                ? VehicleTrackCursorSource.RouteProgress
                : VehicleTrackCursorSource.CachedWaypoint;
            if (!trustedRouteProgress)
            {
                if (!m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out nextWaypointIndex))
                    return false;
                segmentPosition = 0f;
            }

            bool boarding = m_Runtime.IsVehicleBoarding(vehicle);

            if (boarding)
            {
                // Avoid recursive dependency between cursor projection and
                // waypoint anchoring when boarding vehicles lose a reliable
                // TrainCurrentLane projection.
                if (m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out int cachedWaypointIndex)
                    && cachedWaypointIndex >= 0
                    && cachedWaypointIndex < waypoints.Length)
                {
                    nextWaypointIndex = cachedWaypointIndex;
                    segmentPosition = 0f;
                    trustedRouteProgress = false;
                    cursorSource = VehicleTrackCursorSource.CachedWaypoint;
                }
                else if (trustedRouteProgress && TryResolveStationAnchoredProgressFallback(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    nextWaypointIndex,
                    segmentPosition,
                    out int anchoredWaypointIndex))
                {
                    nextWaypointIndex = anchoredWaypointIndex;
                    segmentPosition = 0f;
                    trustedRouteProgress = false;
                    cursorSource = VehicleTrackCursorSource.AnchoredRouteProgress;
                }
            }
            else if (trustedRouteProgress && TryResolveStationAnchoredProgressFallback(
                vehicle,
                line,
                waypoints,
                chain,
                nextWaypointIndex,
                segmentPosition,
                out int anchoredWaypointIndex))
            {
                nextWaypointIndex = anchoredWaypointIndex;
                segmentPosition = 0f;
                trustedRouteProgress = false;
                cursorSource = VehicleTrackCursorSource.AnchoredRouteProgress;
            }

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int segmentIndex = nextWaypointIndex == 0
                ? math.max(0, chain.SegmentRanges.Count - 1)
                : nextWaypointIndex - 1;
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            if (segmentRange.EndAtomIndexExclusive <= segmentRange.StartAtomIndex)
                return false;

            int segmentAtomLength = math.max(1, segmentRange.EndAtomIndexExclusive - segmentRange.StartAtomIndex);
            int approximateAtomIndex = segmentRange.StartAtomIndex
                + math.min(segmentAtomLength - 1, (int)math.floor(segmentAtomLength * math.saturate(segmentPosition)));

            float confidence = trustedRouteProgress ? 1f : 0.7f;
            if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                if (hint.SegmentIndex == segmentIndex)
                {
                    approximateAtomIndex = math.max(approximateAtomIndex, hint.AtomCursorIndex);
                }
                else
                {
                    bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && segmentIndex <= 1;
                    bool monotonicForward = segmentIndex >= hint.SegmentIndex || wrappedForward;
                    if (!monotonicForward)
                    {
                        confidence *= 0.4f;
                        approximateAtomIndex = math.max(segmentRange.StartAtomIndex, math.min(segmentRange.EndAtomIndexExclusive - 1, hint.AtomCursorIndex));
                    }
                }
            }

            if (IsVehicleProgressProjectionInvalid(vehicle, line, chain, segmentIndex, approximateAtomIndex))
                return false;

            approximateAtomIndex = math.clamp(approximateAtomIndex, segmentRange.StartAtomIndex, segmentRange.EndAtomIndexExclusive - 1);
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                approximateAtomIndex,
                math.saturate(segmentPosition),
                confidence,
                cursorSource);
            return true;
        }

        internal bool TryGetVehicleTrackCursorCurrentFrame(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return false;

            uint nowFrame = m_Runtime.Frame;
            return m_Cursors.TryPosition(
                vehicle,
                line,
                chain.Signature,
                nowFrame,
                Project,
                out cursor);

            bool Project(out VehicleTrackCursor projected)
            {
                return TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out projected);
            }
        }

        internal bool TryFacts(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackFacts facts)
        {
            facts = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return false;

            uint nowFrame = m_Runtime.Frame;
            if (m_Facts.TryGetValue(vehicle, out facts)
                && facts.Frame == nowFrame
                && facts.Vehicle == vehicle
                && facts.Line == line
                && facts.ChainSignature == chain.Signature)
            {
                return true;
            }

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
                return false;

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            float ownLineAtomCoordinate = math.max(0f, cursor.AtomCursorIndex + math.saturate(cursor.AtomPosition01));
            TryGetExpressCurrentForwardPhaseWindow(chain, cursor.AtomCursorIndex, out int phaseEndAtomExclusive);
            if (!TryResolveTraversalOrderingPhase(
                    chain,
                    cursor.AtomCursorIndex,
                    out int traversalPhaseIndex,
                    out int traversalPhaseStartAtomIndex,
                    out int traversalPhaseEndAtomExclusive,
                    out int nextTurnbackBoundaryAtomIndex))
            {
                return false;
            }

            facts = new VehicleTrackFacts(
                nowFrame,
                vehicle,
                line,
                chain.Signature,
                cursor,
                currentControlEdgeIndex,
                ownLineAtomCoordinate,
                phaseEndAtomExclusive,
                traversalPhaseIndex,
                traversalPhaseStartAtomIndex,
                traversalPhaseEndAtomExclusive,
                nextTurnbackBoundaryAtomIndex);
            m_Facts[vehicle] = facts;
            return true;
        }

        internal bool TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out int currentControlEdgeIndex,
            out float ownLineAtomCoordinate,
            out int phaseEndAtomExclusive,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive,
            out int nextTurnbackBoundaryAtomIndex)
        {
            cursor = default;
            currentControlEdgeIndex = -1;
            ownLineAtomCoordinate = 0f;
            phaseEndAtomExclusive = -1;
            traversalPhaseIndex = -1;
            traversalPhaseStartAtomIndex = -1;
            traversalPhaseEndAtomExclusive = -1;
            nextTurnbackBoundaryAtomIndex = -1;

            if (!TryFacts(vehicle, line, waypoints, chain, out VehicleTrackFacts facts))
                return false;

            cursor = facts.Cursor;
            currentControlEdgeIndex = facts.CurrentControlEdgeIndex;
            ownLineAtomCoordinate = facts.OwnLineAtomCoordinate;
            phaseEndAtomExclusive = facts.PhaseEndAtomExclusive;
            traversalPhaseIndex = facts.TraversalPhaseIndex;
            traversalPhaseStartAtomIndex = facts.TraversalPhaseStartAtomIndex;
            traversalPhaseEndAtomExclusive = facts.TraversalPhaseEndAtomExclusive;
            nextTurnbackBoundaryAtomIndex = facts.NextTurnbackBoundaryAtomIndex;
            return true;
        }

        private bool TryResolveStationAnchoredProgressFallback(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            int nextWaypointIndex,
            float segmentPosition,
            out int anchoredWaypointIndex)
        {
            anchoredWaypointIndex = -1;
            if (!TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            if (TryResolveWaypointAnchorConflict(
                    vehiclePosition,
                    waypoints,
                    nextWaypointIndex,
                    out int cachedAnchorWaypointIndex)
                && m_Runtime.CachedWaypointIndex.TryGetValue(vehicle, out int cachedWpIdx)
                && cachedWpIdx == cachedAnchorWaypointIndex)
            {
                anchoredWaypointIndex = cachedAnchorWaypointIndex;
                return true;
            }

            if (m_Cursors.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                int hintedWaypointIndex = hint.SegmentIndex >= chain.SegmentRanges.Count - 1
                    ? 0
                    : hint.SegmentIndex + 1;
                if (TryResolveWaypointAnchorConflict(
                        vehiclePosition,
                        waypoints,
                        nextWaypointIndex,
                        out int nearbyWaypointIndex)
                    && nearbyWaypointIndex == hintedWaypointIndex)
                {
                    anchoredWaypointIndex = nearbyWaypointIndex;
                    return true;
                }

                bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && nextWaypointIndex <= 1;
                bool monotonicForward = (nextWaypointIndex == 0 ? chain.SegmentRanges.Count - 1 : nextWaypointIndex - 1) >= hint.SegmentIndex || wrappedForward;
                if (!monotonicForward && math.saturate(segmentPosition) <= 0.15f)
                {
                    anchoredWaypointIndex = hintedWaypointIndex;
                    return true;
                }
            }

            return false;
        }

        internal bool TryProjectTrackModelRuntimePosition(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BypassProtectedInterval protectedInterval,
            out TrackModelRuntimePosition runtimePosition)
        {
            runtimePosition = default;
            if (!m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || !TryFacts(vehicle, line, waypoints, chain, out VehicleTrackFacts facts))
            {
                return false;
            }

            VehicleTrackCursor cursor = facts.Cursor;
            TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(
                facts.CurrentControlEdgeIndex,
                cursor.AtomCursorIndex,
                protectedInterval);

            runtimePosition = new TrackModelRuntimePosition(
                facts.CurrentControlEdgeIndex,
                cursor.AtomCursorIndex,
                cursor.AtomPosition01,
                relative,
                cursor.Confidence,
                facts.TraversalPhaseIndex,
                facts.TraversalPhaseStartAtomIndex,
                facts.TraversalPhaseEndAtomExclusive,
                facts.NextTurnbackBoundaryAtomIndex);
            return true;
        }

        internal static bool TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
            LineRunningVehicleSnapshot runningVehicle,
            BypassProtectedInterval protectedInterval,
            out TrackModelRuntimePosition runtimePosition)
        {
            runtimePosition = default;
            if (!runningVehicle.HasTrackCursor)
                return false;

            runtimePosition = new TrackModelRuntimePosition(
                runningVehicle.CurrentControlEdgeIndex,
                runningVehicle.TrackCursor.AtomCursorIndex,
                runningVehicle.TrackCursor.AtomPosition01,
                ResolveRelativeToProtectedInterval(
                    runningVehicle.CurrentControlEdgeIndex,
                    runningVehicle.TrackCursor.AtomCursorIndex,
                    protectedInterval),
                runningVehicle.TrackCursor.Confidence,
                runningVehicle.TraversalPhaseIndex,
                runningVehicle.TraversalPhaseStartAtomIndex,
                runningVehicle.TraversalPhaseEndAtomExclusive,
                runningVehicle.NextTurnbackBoundaryAtomIndex);
            return true;
        }

        internal static float GetProtectedIntervalDisplayLength(BypassProtectedInterval interval)
        {
            return math.max(1f, interval.EndAtomIndexExclusive - interval.StartAtomIndex);
        }

        internal static float MapRuntimePositionToOwnProtectedIntervalCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval interval,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return -0.5f;
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return intervalLength + 0.5f;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    float atomOffset = math.clamp(runtimePosition.CurrentAtomIndex - interval.StartAtomIndex, 0, math.max(0, interval.EndAtomIndexExclusive - interval.StartAtomIndex - 1));
                    return math.clamp(atomOffset + math.saturate(runtimePosition.AtomPosition01), 0f, intervalLength);
                }
                default:
                    include = false;
                    return 0f;
            }
        }

        internal static float MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval interval,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            float rawCoordinate = (runtimePosition.CurrentAtomIndex - interval.StartAtomIndex) + math.saturate(runtimePosition.AtomPosition01);
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return math.min(-0.5f, rawCoordinate);
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return math.max(intervalLength + 0.5f, rawCoordinate);
                case TrackModelRelativeToProtectedInterval.Inside:
                    return math.clamp(rawCoordinate, 0f, intervalLength);
                default:
                    include = false;
                    return 0f;
            }
        }

        internal static float MapAtomIndexToProtectedIntervalCoordinateExact(
            BypassProtectedInterval interval,
            int atomIndex)
        {
            return atomIndex - interval.StartAtomIndex;
        }

        internal static float MapRuntimePositionToReferenceProtectedIntervalCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceInterval,
            float referenceLength,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return -0.5f;
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return referenceLength + 0.5f;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    float sourceLength = GetProtectedIntervalDisplayLength(sourceInterval);
                    float atomOffset = math.clamp(runtimePosition.CurrentAtomIndex - sourceInterval.StartAtomIndex, 0, math.max(0, sourceInterval.EndAtomIndexExclusive - sourceInterval.StartAtomIndex - 1));
                    float sourceCoordinate = math.clamp(atomOffset + math.saturate(runtimePosition.AtomPosition01), 0f, sourceLength);
                    float progress01 = math.saturate(sourceCoordinate / sourceLength);
                    return progress01 * referenceLength;
                }
                default:
                    include = false;
                    return 0f;
            }
        }

        internal static float MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceInterval,
            float referenceLength,
            bool includeApproachers,
            out bool include)
        {
            float sourceLength = GetProtectedIntervalDisplayLength(sourceInterval);
            float sourceCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                runtimePosition,
                sourceInterval,
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            return sourceCoordinate / sourceLength * referenceLength;
        }

        private static float MapRuntimePositionToReferenceWindowCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceWindow,
            BypassProtectedInterval referenceWindow,
            BypassProtectedInterval referenceEnvelope,
            bool includeApproachers,
            out bool include)
        {
            float mappedInWindow = MapRuntimePositionToReferenceProtectedIntervalCoordinate(
                runtimePosition,
                sourceWindow,
                GetProtectedIntervalDisplayLength(referenceWindow),
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            float envelopeLength = GetProtectedIntervalDisplayLength(referenceEnvelope);
            float windowOffset = math.clamp(referenceWindow.StartAtomIndex - referenceEnvelope.StartAtomIndex, 0f, envelopeLength);
            return math.clamp(windowOffset + mappedInWindow, -0.5f, envelopeLength + 0.5f);
        }

        private static float MapRuntimePositionToReferenceWindowCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceWindow,
            BypassProtectedInterval referenceWindow,
            BypassProtectedInterval referenceEnvelope,
            bool includeApproachers,
            out bool include)
        {
            float mappedInWindow = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                runtimePosition,
                sourceWindow,
                GetProtectedIntervalDisplayLength(referenceWindow),
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            float envelopeLength = GetProtectedIntervalDisplayLength(referenceEnvelope);
            float windowOffset = math.clamp(referenceWindow.StartAtomIndex - referenceEnvelope.StartAtomIndex, 0f, envelopeLength);
            return windowOffset + mappedInWindow;
        }

        internal static float MapControlPointToProtectedIntervalCoordinate(LineTrackChain chain, BypassProtectedInterval interval, int controlPointIndex)
        {
            if (chain == null
                || controlPointIndex < 0
                || controlPointIndex >= chain.ControlPoints.Count)
            {
                return 0f;
            }

            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            int atomIndex = chain.ControlPoints[controlPointIndex].AtomIndex;
            return math.clamp(atomIndex - interval.StartAtomIndex, 0f, intervalLength);
        }

        internal static int ResolveControlEdgeIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                if (atomIndex >= edge.StartAtomIndex && atomIndex < edge.EndAtomIndexExclusive)
                    return i;
            }

            return -1;
        }

        internal static TrackModelRelativeToProtectedInterval ResolveRelativeToProtectedInterval(int currentControlEdgeIndex, int currentAtomIndex, BypassProtectedInterval protectedInterval)
        {
            // Atom bounds are the true physical window. Control-edge bounds are
            // only a coarse fallback for lines whose control graph is sparse.
            // If we prioritize control-edge first, any single-edge line will mark
            // the entire edge as Inside even when the atom lies outside the
            // actual shared/protected atom range.
            if (currentAtomIndex >= 0)
            {
                if (currentAtomIndex < protectedInterval.StartAtomIndex)
                    return TrackModelRelativeToProtectedInterval.Before;
                if (currentAtomIndex >= protectedInterval.EndAtomIndexExclusive)
                    return TrackModelRelativeToProtectedInterval.After;
                return TrackModelRelativeToProtectedInterval.Inside;
            }

            if (currentControlEdgeIndex >= 0)
            {
                if (currentControlEdgeIndex < protectedInterval.StartControlEdgeIndex)
                    return TrackModelRelativeToProtectedInterval.Before;
                if (currentControlEdgeIndex > protectedInterval.EndControlEdgeIndexInclusive)
                    return TrackModelRelativeToProtectedInterval.After;
                return TrackModelRelativeToProtectedInterval.Inside;
            }

            return TrackModelRelativeToProtectedInterval.Unknown;
        }

        internal static string FormatRuntimePosition(TrackModelRuntimePosition runtimePosition)
        {
            return "pos[edge="
                + runtimePosition.CurrentControlEdgeIndex
                + " atom="
                + runtimePosition.CurrentAtomIndex
                + " p="
                + runtimePosition.AtomPosition01.ToString("0.00")
                + " rel="
                + runtimePosition.RelativeToProtectedInterval
                + " conf="
                + runtimePosition.Confidence.ToString("0.00")
                + "]";
        }

    }
}
