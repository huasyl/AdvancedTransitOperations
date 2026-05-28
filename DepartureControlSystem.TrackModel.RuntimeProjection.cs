using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
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

        private bool TryGetTrackAtomWorldPosition(LineTrackChain chain, int atomIndex, out float3 position)
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
                || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<Curve>(entity))
            {
                return false;
            }

            Curve curve = EntityManager.GetComponentData<Curve>(entity);
            position = MathUtils.Position(curve.m_Bezier, math.saturate(curvePosition));
            return true;
        }

        private bool TryGetEntityWorldPosition(Entity entity, out float3 position)
        {
            position = default;
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return false;

            if (EntityManager.HasComponent<Position>(entity))
            {
                position = EntityManager.GetComponentData<Position>(entity).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
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

        private bool TryGetVehicleWorldPosition(Entity vehicle, out float3 position)
        {
            position = default;
            if (!EntityManager.Exists(vehicle))
                return false;

            if (EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Position>(vehicle))
            {
                position = EntityManager.GetComponentData<Position>(vehicle).m_Position;
                return true;
            }

            return false;
        }

        private bool TryGetWaypointWorldPosition(Entity waypoint, out float3 position)
        {
            position = default;
            if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                return false;

            if (EntityManager.HasComponent<Position>(waypoint))
            {
                position = EntityManager.GetComponentData<Position>(waypoint).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Objects.Transform>(waypoint))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(waypoint).m_Position;
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
                || !EntityManager.HasComponent<TrainCurrentLane>(vehicle))
            {
                return false;
            }

            TrainCurrentLane currentLane = EntityManager.GetComponentData<TrainCurrentLane>(vehicle);
            Entity frontLane = currentLane.m_Front.m_Lane;
            Entity rearLane = currentLane.m_Rear.m_Lane;
            float frontCurvePosition = math.saturate(currentLane.m_Front.m_CurvePosition.x);
            float rearCurvePosition = math.saturate(currentLane.m_Rear.m_CurvePosition.x);

            int referenceAtomIndex = m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature
                ? hint.AtomCursorIndex
                : -1;

            int preferredSegmentIndex = m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor segmentHint)
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
                1f);
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

            if (m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                AddSegmentCandidate(hint.SegmentIndex);
            }

            if (m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex))
                AddWaypointAnchor(cachedWaypointIndex);

            if (EntityManager.HasComponent<Target>(vehicle))
            {
                Entity targetWaypoint = EntityManager.GetComponentData<Target>(vehicle).m_Target;
                if (EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    AddWaypointAnchor(EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index);
            }

            if (TryGetRouteProgress(vehicle, out int nextWaypointIndex, out _))
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

        private bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain.SegmentRanges.Count == 0)
            {
                return false;
            }

            return TryProjectVehicleTrackCursor(vehicle, line, waypoints, chain, out cursor);
        }

        private bool TryProjectVehicleTrackCursor(
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
                if (m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor trainHint)
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
                            cursor.Confidence * 0.7f);
                    }
                }

                if (IsVehicleProgressProjectionInvalid(vehicle, line, chain, cursor.SegmentIndex, cursor.AtomCursorIndex))
                    return false;

                return true;
            }

            bool trustedRouteProgress = TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition);
            if (!trustedRouteProgress)
            {
                if (!m_CachedWpIdx.TryGetValue(vehicle, out nextWaypointIndex))
                    return false;
                segmentPosition = 0f;
            }

            bool boarding = false;
            if (EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                boarding = (EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
            }

            if (boarding)
            {
                // Avoid recursive dependency between cursor projection and
                // waypoint anchoring when boarding vehicles lose a reliable
                // TrainCurrentLane projection.
                if (m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                    && cachedWaypointIndex >= 0
                    && cachedWaypointIndex < waypoints.Length)
                {
                    nextWaypointIndex = cachedWaypointIndex;
                    segmentPosition = 0f;
                    trustedRouteProgress = false;
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
            if (m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor hint)
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
                confidence);
            return true;
        }

        private bool TryGetVehicleTrackCursorCurrentFrame(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            return m_TrackProjector.TryPosition(
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

        private bool TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
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

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
                return false;

            currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            ownLineAtomCoordinate = math.max(0f, cursor.AtomCursorIndex + math.saturate(cursor.AtomPosition01));
            TryGetExpressCurrentForwardPhaseWindow(chain, cursor.AtomCursorIndex, out phaseEndAtomExclusive);
            if (!TryResolveTraversalOrderingPhase(
                    chain,
                    cursor.AtomCursorIndex,
                    out traversalPhaseIndex,
                    out traversalPhaseStartAtomIndex,
                    out traversalPhaseEndAtomExclusive,
                    out nextTurnbackBoundaryAtomIndex))
            {
                return false;
            }
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
                && m_CachedWpIdx.TryGetValue(vehicle, out int cachedWpIdx)
                && cachedWpIdx == cachedAnchorWaypointIndex)
            {
                anchoredWaypointIndex = cachedAnchorWaypointIndex;
                return true;
            }

            if (m_TrackProjector.TryCursor(vehicle, out VehicleTrackCursor hint)
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

        private void MarkVehicleProgressSuspect(Entity vehicle, string reason)
        {
            if (vehicle == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            m_SuspectProgressSinceFrame[vehicle] = nowFrame;
            m_SuspectProgressReason[vehicle] = reason ?? "unknown";
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_TrackProjector.Remove(vehicle, keepCursor: true);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            string summary = vehicle.Index + "|" + reason;
            if (m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) && previous == summary)
                return;

            m_SuspectProgressLogCache[vehicle] = summary;
            log.Info("[ProgressSuspect] 杞﹁締" + vehicle.Index + " reason=" + reason + " sinceFrame=" + nowFrame);
        }

        private void ClearVehicleProgressSuspect(Entity vehicle, string reason = null)
        {
            if (vehicle == Entity.Null)
                return;

            bool hadState = m_SuspectProgressSinceFrame.Remove(vehicle);
            m_SuspectProgressLastValidationFrame.Remove(vehicle);
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_TrackProjector.Remove(vehicle, keepCursor: true);
            m_SuspectProgressReason.Remove(vehicle);
            m_SuspectProgressLogCache.Remove(vehicle);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            if (hadState)
            {
                log.Info("[ProgressSuspectClear] 杞﹁締" + vehicle.Index
                    + (!string.IsNullOrWhiteSpace(reason) ? " reason=" + reason : string.Empty));
            }
        }

        private void NoteVehicleProgressSuspectRecoveryBoarding(Entity vehicle, int waypointIndex)
        {
            if (vehicle == Entity.Null
                || waypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return;
            }

            m_SuspectProgressRecoveryWaypoint[vehicle] = waypointIndex;
        }

        private void TryClearVehicleProgressSuspectOnStableDeparture(Entity vehicle, int departedWaypointIndex)
        {
            if (vehicle == Entity.Null
                || departedWaypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle)
                || !m_SuspectProgressRecoveryWaypoint.TryGetValue(vehicle, out int recoveryWaypointIndex)
                || recoveryWaypointIndex != departedWaypointIndex)
            {
                return;
            }

            ClearVehicleProgressSuspect(vehicle, "stable-stop-cycle wp=" + departedWaypointIndex);
        }

        private bool IsVehicleProgressProjectionInvalid(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int segmentIndex,
            int projectedAtomIndex)
        {
            if (vehicle == Entity.Null
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return false;
            }

            if (m_SuspectProgressProjectionInvalid.TryGetValue(vehicle, out bool alreadyInvalid) && alreadyInvalid)
                return true;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_SuspectProgressLastValidationFrame.TryGetValue(vehicle, out uint lastValidationFrame)
                && nowFrame - lastValidationFrame < SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES)
            {
                return false;
            }

            m_SuspectProgressLastValidationFrame[vehicle] = nowFrame;
            if (!TryValidateSuspectVehicleProjection(vehicle, line, chain, segmentIndex, projectedAtomIndex, out SuspectProgressSample sample, out string validationSummary, out bool projectionInvalid))
                return false;

            int validationCount = m_SuspectProgressValidationCount.TryGetValue(vehicle, out int previousCount)
                ? previousCount + 1
                : 1;
            m_SuspectProgressValidationCount[vehicle] = validationCount;
            if (!m_SuspectProgressFirstSample.ContainsKey(vehicle))
                m_SuspectProgressFirstSample[vehicle] = sample;

            string logKey = vehicle.Index + "|" + validationSummary;
            if (!m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) || previous != logKey)
            {
                m_SuspectProgressLogCache[vehicle] = logKey;
                log.Info("[ProgressSuspectCheck] " + validationSummary);
            }

            if (validationCount % 36 == 0
                && m_SuspectProgressFirstSample.TryGetValue(vehicle, out SuspectProgressSample firstSample))
            {
                log.Info("[ProgressSuspectWindow] vehicle=" + vehicle.Index
                    + " scans=" + validationCount
                    + " startAtom=" + firstSample.ProjectedAtomIndex
                    + " startBestAtom=" + firstSample.BestAtomIndex
                    + " startPos=(" + firstSample.VehiclePosition.x.ToString("F1")
                    + "," + firstSample.VehiclePosition.y.ToString("F1")
                    + "," + firstSample.VehiclePosition.z.ToString("F1") + ")"
                    + " startDist=" + firstSample.ProjectedDistanceMeters.ToString("F1")
                    + "/" + firstSample.BestDistanceMeters.ToString("F1")
                    + " currentAtom=" + sample.ProjectedAtomIndex
                    + " currentBestAtom=" + sample.BestAtomIndex
                    + " currentPos=(" + sample.VehiclePosition.x.ToString("F1")
                    + "," + sample.VehiclePosition.y.ToString("F1")
                    + "," + sample.VehiclePosition.z.ToString("F1") + ")"
                    + " currentDist=" + sample.ProjectedDistanceMeters.ToString("F1")
                    + "/" + sample.BestDistanceMeters.ToString("F1")
                    + (projectionInvalid ? " invalid=true" : " invalid=false"));
            }

            if (!projectionInvalid)
                return false;

            m_SuspectProgressProjectionInvalid[vehicle] = true;
            return true;
        }

        private bool TryValidateSuspectVehicleProjection(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int projectedSegmentIndex,
            int projectedAtomIndex,
            out SuspectProgressSample sample,
            out string validationSummary,
            out bool projectionInvalid)
        {
            sample = default;
            validationSummary = string.Empty;
            projectionInvalid = false;

            if (!TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            if (!TryGetTrackAtomWorldPosition(chain, projectedAtomIndex, out float3 projectedAtomPosition))
                return false;

            float projectedDistance = math.distance(vehiclePosition, projectedAtomPosition);
            int candidateStartSegment = math.max(0, projectedSegmentIndex - SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);
            int candidateEndSegment = math.min(chain.SegmentRanges.Count - 1, projectedSegmentIndex + SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);

            int bestAtomIndex = -1;
            float bestDistance = float.MaxValue;
            for (int seg = candidateStartSegment; seg <= candidateEndSegment; seg++)
            {
                TrackSegmentRange candidateRange = chain.SegmentRanges[seg];
                for (int atomIndex = candidateRange.StartAtomIndex; atomIndex < candidateRange.EndAtomIndexExclusive; atomIndex++)
                {
                    if (!TryGetTrackAtomWorldPosition(chain, atomIndex, out float3 atomPosition))
                        continue;

                    float distance = math.distance(vehiclePosition, atomPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestAtomIndex = atomIndex;
                    }
                }
            }

            if (bestAtomIndex < 0)
                return false;

            int atomDelta = math.abs(bestAtomIndex - projectedAtomIndex);
            projectionInvalid =
                atomDelta >= SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD
                && projectedDistance - bestDistance >= SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS;

            sample = new SuspectProgressSample(
                projectedAtomIndex,
                bestAtomIndex,
                vehiclePosition,
                projectedDistance,
                bestDistance);

            validationSummary = "vehicle=" + vehicle.Index
                + " line=" + line.Index
                + " projectedAtom=" + projectedAtomIndex
                + " bestAtom=" + bestAtomIndex
                + " projectedDist=" + projectedDistance.ToString("F1")
                + "m bestDist=" + bestDistance.ToString("F1")
                + "m delta=" + atomDelta
                + (projectionInvalid ? " invalid=true" : " invalid=false");
            return true;
        }

        private bool TryProjectTrackModelRuntimePosition(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BypassProtectedInterval protectedInterval,
            out TrackModelRuntimePosition runtimePosition)
        {
            runtimePosition = default;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, cursor.AtomCursorIndex, protectedInterval);
            float confidence = cursor.Confidence;
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

            runtimePosition = new TrackModelRuntimePosition(
                currentControlEdgeIndex,
                cursor.AtomCursorIndex,
                cursor.AtomPosition01,
                relative,
                confidence,
                traversalPhaseIndex,
                traversalPhaseStartAtomIndex,
                traversalPhaseEndAtomExclusive,
                nextTurnbackBoundaryAtomIndex);
            return true;
        }

        private static bool TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
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

        private static float GetProtectedIntervalDisplayLength(BypassProtectedInterval interval)
        {
            return math.max(1f, interval.EndAtomIndexExclusive - interval.StartAtomIndex);
        }

        private static float MapRuntimePositionToOwnProtectedIntervalCoordinate(
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

        private static float MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
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

        private static float MapAtomIndexToProtectedIntervalCoordinateExact(
            BypassProtectedInterval interval,
            int atomIndex)
        {
            return atomIndex - interval.StartAtomIndex;
        }

        private static float MapRuntimePositionToReferenceProtectedIntervalCoordinate(
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

        private static float MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
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

        private static float MapControlPointToProtectedIntervalCoordinate(LineTrackChain chain, BypassProtectedInterval interval, int controlPointIndex)
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

        private static int ResolveControlEdgeIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                if (atomIndex >= edge.StartAtomIndex && atomIndex < edge.EndAtomIndexExclusive)
                    return i;
            }

            return -1;
        }

        private static TrackModelRelativeToProtectedInterval ResolveRelativeToProtectedInterval(int currentControlEdgeIndex, int currentAtomIndex, BypassProtectedInterval protectedInterval)
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

        private static string FormatRuntimePosition(TrackModelRuntimePosition runtimePosition)
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
