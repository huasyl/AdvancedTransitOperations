using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        internal float GetDistanceToOriginMeters(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(v)) return float.MaxValue;
            Entity stopA = wps[0].m_Waypoint;
            if (EntityManager.HasComponent<Connected>(stopA))
                stopA = EntityManager.GetComponentData<Connected>(stopA).m_Connected;
            if (stopA == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                return float.MaxValue;

            float3 vehiclePos = EntityManager.GetComponentData<Game.Objects.Transform>(v).m_Position;
            float3 stopPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
            return math.distance(vehiclePos, stopPos);
        }

        internal bool ShouldEvaluateRunningOriginSettleCheck(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atA,
            bool boarding,
            bool lastBoarding,
            int targetMin)
        {
            m_PerfProbeOriginSettleCalls++;
            if (vehicle == Entity.Null || waypoints.Length == 0)
                return false;

            if (atA
                || boarding
                || lastBoarding
                || targetMin >= 0
                || m_VehicleRuntime.OriginArrivalCandidateSinceFrame.ContainsKey(vehicle))
            {
                m_PerfProbeOriginSettleFastPathHits++;
                return true;
            }

            Entity line = ResolveVehicleLine(vehicle);
            if (line == Entity.Null
                || !m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            m_PerfProbeOriginSettleSlowPathEntered++;
            if (!m_TrackProjection.TrySnapshot(
                    vehicle,
                    line,
                    chain.Signature,
                    m_SimulationSystem.frameIndex,
                    out VehicleTrackCursor cursor))
            {
                m_PerfProbeOriginSettlePreSnapshotMisses++;
                return false;
            }

            int atomCursorIndex = cursor.AtomCursorIndex;
            if (atomCursorIndex < 0 || atomCursorIndex >= chain.TrackAtoms.Count)
                return false;

            const int originAtomWindow = 2;
            bool inOriginWindow = atomCursorIndex <= originAtomWindow
                || atomCursorIndex >= math.max(0, chain.TrackAtoms.Count - 1 - originAtomWindow);
            if (inOriginWindow)
                m_PerfProbeOriginSettleWindowHits++;
            return inOriginWindow;
        }

        internal bool ShouldSettleRunningAtOrigin(
            Entity v,
            DynamicBuffer<RouteWaypoint> wps,
            uint nowFrame,
            bool atA,
            bool boarding,
            bool lastBoarding,
            int targetMin)
        {
            bool waitingAtOrigin = atA
                && (boarding
                    || lastBoarding
                    || targetMin >= 0
                    || m_VehicleView.IsInbound(v)
                    || m_VehicleRuntime.CurrentSlot.ContainsKey(v));

            if (!waitingAtOrigin)
            {
                float originDist = GetDistanceToOriginMeters(v, wps);
                if (originDist > ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    m_RuntimeController.ClearOriginCandidate(v);
                    return false;
                }

                if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                {
                    m_RuntimeController.ClearOriginCandidate(v);
                    return false;
                }

                if (nextWaypointIndex != 0 || segmentPosition < ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS)
                {
                    m_RuntimeController.ClearOriginCandidate(v);
                    return false;
                }
            }

            if (!m_VehicleView.TryGetOrigin(v, out uint sinceFrame))
            {
                m_RuntimeController.SetOriginCandidate(v, nowFrame);
                return false;
            }

            if ((nowFrame - sinceFrame) < ORIGIN_FORCE_IDLE_SETTLE_FRAMES)
                return false;

            return true;
        }

        internal bool IsBorderlineOriginArrivalCandidate(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (GetDistanceToOriginMeters(v, wps) > ORIGIN_FORCE_IDLE_RADIUS_METERS)
                return false;

            if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                return false;
            if (nextWaypointIndex != 0 || segmentPosition < ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS)
                return false;
            return true;
        }

        private bool HasBorderlineOriginArrivalCandidate(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            float slotFramesAway,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            float waitFrames = ORIGIN_ARRIVAL_HOLD_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!EntityManager.Exists(v)) continue;
                if (!m_VehicleView.TryGetState(v, out var state) || state != VehicleState.Running) continue;
                if (m_VehicleView.TryGetTarget(v, out int target) && target >= 0) continue;
                if (m_VehicleView.TryGetCooldown(v, out uint cooldownUntil) && nowFrame < cooldownUntil) continue;
                if (!IsBorderlineOriginArrivalCandidate(v, wps)) continue;

                float eta = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                if (eta == float.MaxValue) continue;
                if (eta <= slotFramesAway + waitFrames)
                    return true;
            }

            return false;
        }

        private bool ShouldHoldSpawnForNearestRunningCandidate(
            Entity nearestVehicle,
            VehicleState nearestState,
            float nearestETA,
            DynamicBuffer<RouteWaypoint> wps)
        {
            if (nearestVehicle == Entity.Null || nearestState != VehicleState.Running)
                return false;
            if (nearestETA == float.MaxValue)
                return false;

            float waitFrames = ORIGIN_ARRIVAL_HOLD_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            if (nearestETA > waitFrames)
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_VehicleView.TryGetCooldown(nearestVehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return false;

            if (GetDistanceToOriginMeters(nearestVehicle, wps) <= ORIGIN_CONGESTION_RADIUS_METERS)
                return true;

            if (TryGetRouteProgress(nearestVehicle, out int nextWaypointIndex, out float segmentPosition))
                return nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f);

            return false;
        }

        internal bool IsWaitingForcedOriginDwell(Entity v, uint nowFrame)
        {
            return m_VehicleView.TryGetReady(v, out uint readyFrame) && nowFrame < readyFrame;
        }

        internal bool TryGetRouteProgress(Entity transportVehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            bool hit = TryGetRouteProgressCurrentFrame(transportVehicle, out nextWaypointIndex, out segmentPosition);
            if (hit)
                return true;

            if (EntityManager.HasComponent<CurrentRoute>(transportVehicle))
            {
                var currentRoute = EntityManager.GetComponentData<CurrentRoute>(transportVehicle);
                if (EntityManager.HasComponent<PathInformation>(transportVehicle))
                {
                    var pathInfo = EntityManager.GetComponentData<PathInformation>(transportVehicle);
                    if (EntityManager.HasComponent<Waypoint>(pathInfo.m_Destination)
                        && EntityManager.HasBuffer<RouteSegment>(currentRoute.m_Route))
                    {
                        var destinationWp = EntityManager.GetComponentData<Waypoint>(pathInfo.m_Destination);
                        DynamicBuffer<RouteSegment> routeSegments = EntityManager.GetBuffer<RouteSegment>(currentRoute.m_Route, true);
                        if (routeSegments.Length > 0)
                        {
                            nextWaypointIndex = destinationWp.m_Index;
                            int index = math.select(nextWaypointIndex - 1, routeSegments.Length - 1, nextWaypointIndex == 0);
                            RouteSegment routeSegment = routeSegments[index];
                            if (EntityManager.HasBuffer<PathElement>(routeSegment.m_Segment))
                            {
                                DynamicBuffer<PathElement> segmentPath = EntityManager.GetBuffer<PathElement>(routeSegment.m_Segment, true);
                                if (segmentPath.Length != 0)
                                {
                                    int remaining = 0;
                                    if (EntityManager.HasComponent<PathOwner>(transportVehicle)
                                        && EntityManager.HasBuffer<PathElement>(transportVehicle))
                                    {
                                        var pathOwner = EntityManager.GetComponentData<PathOwner>(transportVehicle);
                                        DynamicBuffer<PathElement> vehiclePath = EntityManager.GetBuffer<PathElement>(transportVehicle, true);
                                        remaining += math.max(0, vehiclePath.Length - pathOwner.m_ElementIndex);
                                    }
                                    if (EntityManager.HasBuffer<CarNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<CarNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<TrainNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<TrainNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<WatercraftNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<WatercraftNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<AircraftNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<AircraftNavigationLane>(transportVehicle, true).Length;

                                    segmentPosition = math.saturate((float)(segmentPath.Length - remaining) / (float)segmentPath.Length);
                                    StoreRouteProgressCurrentFrame(transportVehicle, currentRoute.m_Route, pathInfo.m_Destination, nextWaypointIndex, segmentPosition);
                                    return true;
                                }
                            }
                        }
                    }
                }

                if (EntityManager.HasComponent<Target>(transportVehicle))
                {
                    var target = EntityManager.GetComponentData<Target>(transportVehicle);
                    if (EntityManager.HasComponent<Waypoint>(target.m_Target)
                        && EntityManager.HasBuffer<RouteWaypoint>(currentRoute.m_Route))
                    {
                        var targetWp = EntityManager.GetComponentData<Waypoint>(target.m_Target);
                        DynamicBuffer<RouteWaypoint> routeWaypoints = EntityManager.GetBuffer<RouteWaypoint>(currentRoute.m_Route, true);
                        if (routeWaypoints.Length > 0)
                        {
                            nextWaypointIndex = targetWp.m_Index;
                            int index2 = math.select(nextWaypointIndex - 1, routeWaypoints.Length - 1, nextWaypointIndex == 0);
                            RouteWaypoint previousWaypoint = routeWaypoints[index2];
                            if (EntityManager.HasComponent<Game.Objects.Transform>(transportVehicle)
                                && EntityManager.HasComponent<Position>(previousWaypoint.m_Waypoint)
                                && EntityManager.HasComponent<Position>(target.m_Target))
                            {
                                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(transportVehicle);
                                var prevPos = EntityManager.GetComponentData<Position>(previousWaypoint.m_Waypoint);
                                var targetPos = EntityManager.GetComponentData<Position>(target.m_Target);
                                float toTarget = math.distance(transform.m_Position, targetPos.m_Position);
                                float segmentLength = math.max(1f, math.distance(prevPos.m_Position, targetPos.m_Position));
                                segmentPosition = math.saturate((segmentLength - toTarget) / segmentLength);
                                StoreRouteProgressCurrentFrame(transportVehicle, currentRoute.m_Route, target.m_Target, nextWaypointIndex, segmentPosition);
                                return true;
                            }
                        }
                    }
                }
            }

            nextWaypointIndex = 0;
            segmentPosition = 0f;
            return false;
        }

        private bool TryGetRouteProgressCurrentFrame(Entity vehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            nextWaypointIndex = 0;
            segmentPosition = 0f;
            if (vehicle == Entity.Null
                || !m_RouteProgressFrameSnapshots.TryGetValue(vehicle, out RouteProgressFrameSnapshot snapshot))
            {
                return false;
            }

            if (snapshot.Frame != m_SimulationSystem.frameIndex)
                return false;
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle) || !EntityManager.HasComponent<Target>(vehicle))
                return false;

            CurrentRoute currentRoute = EntityManager.GetComponentData<CurrentRoute>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);
            if (snapshot.Route != currentRoute.m_Route || snapshot.Target != target.m_Target)
                return false;

            nextWaypointIndex = snapshot.NextWaypointIndex;
            segmentPosition = snapshot.SegmentPosition;
            return true;
        }

        private void StoreRouteProgressCurrentFrame(
            Entity vehicle,
            Entity route,
            Entity target,
            int nextWaypointIndex,
            float segmentPosition)
        {
            if (vehicle == Entity.Null || route == Entity.Null || nextWaypointIndex < 0)
                return;

            m_RouteProgressFrameSnapshots[vehicle] = new RouteProgressFrameSnapshot(
                m_SimulationSystem.frameIndex,
                route,
                target,
                nextWaypointIndex,
                segmentPosition);
        }
        internal void ClearForcedMidStopClosingConsist(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        internal int ComputeWpIndex(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            bool hit = TryGetWaypointIndexCurrentFrame(v, wps, out int cachedWaypointIndex);
            if (hit)
                return cachedWaypointIndex;

            int computedWaypointIndex = ComputeWpIndexUncached(v, wps);
            Entity route = ResolveVehicleLine(v);
            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
            m_WaypointIndexFrameSnapshots[v] = new WaypointIndexFrameSnapshot(
                m_SimulationSystem.frameIndex,
                route,
                boarding,
                computedWaypointIndex);

            return computedWaypointIndex;
        }

        internal bool IsRuntimeReadyForOriginArrivingRepair()
        {
            return m_SystemReady;
        }

        internal bool TryGetRuntimeVehicleState(Entity vehicle, out VehicleState state)
        {
            state = default;
            return m_VehicleRuntime.State.IsCreated && m_VehicleView.TryGetState(vehicle, out state);
        }

        internal int ComputeWaypointIndexForOriginArrivingRepair(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            return ComputeWpIndex(vehicle, waypoints);
        }

        internal bool TryGetOriginArrivalRouteProgressForOriginArrivingRepair(
            Entity vehicle,
            out int nextWaypointIndex,
            out float segmentPosition)
        {
            if (!TryGetRouteProgress(vehicle, out nextWaypointIndex, out segmentPosition))
                return false;

            return nextWaypointIndex == 0 && segmentPosition >= ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS;
        }

        private bool TryGetWaypointIndexCurrentFrame(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints, out int waypointIndex)
        {
            waypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_WaypointIndexFrameSnapshots.TryGetValue(vehicle, out WaypointIndexFrameSnapshot snapshot))
            {
                return false;
            }

            if (snapshot.Frame != m_SimulationSystem.frameIndex)
                return false;

            Entity route = ResolveVehicleLine(vehicle);
            if (snapshot.Route != route)
                return false;

            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
            if (snapshot.Boarding != boarding)
                return false;

            waypointIndex = snapshot.WaypointIndex;
            return true;
        }

        internal bool TryGetLineWaypointIndexLookup(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineWaypointIndexLookup lookup)
        {
            return m_TrackModel.TryGetWaypointIndexLookup(line, waypoints, out lookup);
        }

        private int ComputeWpIndexUncached(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            Entity line = ResolveVehicleLine(v);
            bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v).m_State & PublicTransportFlags.Boarding) != 0;
            int targetWaypointIndex = -1;
            LineWaypointIndexLookup lookup = null;
            if (line != Entity.Null)
                TryGetLineWaypointIndexLookup(line, wps, out lookup);
            if (EntityManager.HasComponent<Target>(v))
            {
                Entity targetWaypoint = EntityManager.GetComponentData<Target>(v).m_Target;
                if (lookup != null
                    && targetWaypoint != Entity.Null
                    && lookup.WaypointIndexByWaypoint.TryGetValue(targetWaypoint, out int indexedTargetWaypointIndex))
                {
                    targetWaypointIndex = indexedTargetWaypointIndex;
                }
                else if (EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
            }

            int bvWi = -1;
            if (lookup != null && boarding)
            {
                foreach (KeyValuePair<Entity, int> entry in lookup.WaypointIndexByStop)
                {
                    Entity stop = entry.Key;
                    if (!EntityManager.Exists(stop)
                        || !EntityManager.HasComponent<BoardingVehicle>(stop)
                        || EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v)
                    {
                        continue;
                    }

                    bvWi = entry.Value;
                    break;
                }
            }
            else
            {
                for (int wi = 0; wi < wps.Length; wi++)
                {
                    Entity wp = wps[wi].m_Waypoint;
                    Entity stop = EntityManager.HasComponent<Connected>(wp)
                        ? EntityManager.GetComponentData<Connected>(wp).m_Connected : Entity.Null;
                    if (stop == Entity.Null) continue;

                    if (!EntityManager.HasComponent<BoardingVehicle>(stop)) continue;
                    if (EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v) continue;
                    bvWi = wi;
                    break;
                }
            }

            bool allowTrackWaypointAnchoring = ENABLE_TRACK_WAYPOINT_ANCHORING
                && m_VehicleView.TryGetState(v, out VehicleState trackState)
                && trackState != VehicleState.Retiring;

            if (allowTrackWaypointAnchoring
                && TryResolveWaypointIndexByTrackCursor(v, wps, targetWaypointIndex, bvWi, -1, out int anchoredWaypointIndex, out string anchorDetail, out string anchorStableKey))
            {
                LogVehicleStateOnce(
                    m_BvTrackAnchorRecoveryLogCache,
                    v,
                    "track-anchor|" + anchorStableKey,
                    "[定位接管] 车辆" + v.Index + " 按track锚定 wp[" + anchoredWaypointIndex + "] " + anchorDetail);
                return anchoredWaypointIndex;
            }

            // Fallback when track cursor is temporarily unavailable:
            // trust explicit boarding-stop ownership only, never world-distance nearest-stop.
            if (boarding && bvWi >= 0)
                return bvWi;

            return -1;
        }

        private bool TryResolveWaypointIndexByTrackCursor(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int closestWaypointIndex,
            out int waypointIndex,
            out string detail,
            out string stableKey)
        {
            waypointIndex = -1;
            detail = string.Empty;
            stableKey = string.Empty;
            Entity line = ResolveVehicleLine(vehicle);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || !m_TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            var candidateIndices = new HashSet<int>();
            void AddCandidate(int index)
            {
                if (index >= 0 && index < waypoints.Length)
                    candidateIndices.Add(index);
            }

            AddCandidate(targetWaypointIndex);
            AddCandidate(boardingWaypointIndex);
            AddCandidate(closestWaypointIndex);
            if (cursor.SegmentIndex >= 0)
            {
                AddCandidate(cursor.SegmentIndex);
                AddCandidate(cursor.SegmentIndex + 1);
                AddCandidate(cursor.SegmentIndex - 1);
                AddCandidate(cursor.SegmentIndex + 2);
            }

            int bestWaypointIndex = -1;
            int bestWindowStart = -1;
            int bestWindowEndExclusive = -1;
            int bestDistance = int.MaxValue;
            const int anchorSlackAtoms = 3;

            foreach (int candidateIndex in candidateIndices)
            {
                if (!TryGetWaypointTraversalAtomWindow(chain, candidateIndex, cursor.AtomCursorIndex, out int windowStart, out int windowEndExclusive))
                    continue;

                int expandedStart = math.max(0, windowStart - anchorSlackAtoms);
                int expandedEndExclusive = math.min(chain.TrackAtoms.Count, windowEndExclusive + anchorSlackAtoms);
                if (cursor.AtomCursorIndex < expandedStart || cursor.AtomCursorIndex >= expandedEndExclusive)
                    continue;

                int distance = cursor.AtomCursorIndex < windowStart
                    ? windowStart - cursor.AtomCursorIndex
                    : cursor.AtomCursorIndex >= windowEndExclusive
                        ? cursor.AtomCursorIndex - (windowEndExclusive - 1)
                        : 0;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestWaypointIndex = candidateIndex;
                bestWindowStart = windowStart;
                bestWindowEndExclusive = windowEndExclusive;
            }

            if (bestWaypointIndex < 0)
                return false;

            waypointIndex = bestWaypointIndex;
            stableKey = "wp=" + bestWaypointIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
            detail = "atom=" + cursor.AtomCursorIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
            return true;
        }

        internal bool TryGetWaypointTraversalAtomWindow(
            LineTrackChain chain,
            int waypointIndex,
            int referenceAtomIndex,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || waypointIndex < 0)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.WaypointIndex != waypointIndex
                    || (traversalEvent.Kind != TraversalEventKind.Stop && traversalEvent.Kind != TraversalEventKind.Pass))
                {
                    continue;
                }

                int candidateStart = traversalEvent.StartAtomIndex;
                int candidateEndExclusive = math.max(candidateStart + 1, traversalEvent.EndAtomIndexExclusive);
                int candidateDistance = referenceAtomIndex < candidateStart
                    ? candidateStart - referenceAtomIndex
                    : referenceAtomIndex >= candidateEndExclusive
                        ? referenceAtomIndex - (candidateEndExclusive - 1)
                        : 0;
                if (candidateDistance >= bestDistance)
                    continue;

                bestDistance = candidateDistance;
                startAtomIndex = candidateStart;
                endAtomIndexExclusive = candidateEndExclusive;
            }

            return startAtomIndex >= 0 && endAtomIndexExclusive > startAtomIndex;
        }

        internal enum CursorAtomWindowRelation : byte
        {
            Unknown = 0,
            Before = 1,
            Inside = 2,
            After = 3,
        }

        internal static CursorAtomWindowRelation CompareCursorToAtomWindow(
            int cursorAtomIndex,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (cursorAtomIndex < 0 || startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex)
                return CursorAtomWindowRelation.Unknown;

            if (cursorAtomIndex < startAtomIndex)
                return CursorAtomWindowRelation.Before;

            if (cursorAtomIndex >= endAtomIndexExclusive)
                return CursorAtomWindowRelation.After;

            return CursorAtomWindowRelation.Inside;
        }

        internal bool TryGetCursorWaypointWindowRelation(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            out CursorAtomWindowRelation relation,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            relation = CursorAtomWindowRelation.Unknown;
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (!TryGetWaypointTraversalAtomWindow(
                    chain,
                    waypointIndex,
                    cursorAtomIndex,
                    out startAtomIndex,
                    out endAtomIndexExclusive))
            {
                return false;
            }

            relation = CompareCursorToAtomWindow(cursorAtomIndex, startAtomIndex, endAtomIndexExclusive);
            return relation != CursorAtomWindowRelation.Unknown;
        }

        internal bool IsSuppressedForcedMidStopBoardingGhost(
            Entity vehicle,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out int targetWaypointIndex)
        {
            targetWaypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil))
            {
                return false;
            }

            if (nowFrame >= graceUntil)
            {
                m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
                return false;
            }

            if (!EntityManager.HasComponent<Waypoint>(target.m_Target))
                return false;

            targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypointIndex < 0 || targetWaypointIndex >= waypoints.Length)
                return false;

            Entity targetStop = GetConnectedStopForWaypoint(waypoints[targetWaypointIndex].m_Waypoint);
            if (targetStop == Entity.Null
                || !EntityManager.HasComponent<BoardingVehicle>(targetStop)
                || EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle != vehicle
                || !EntityManager.HasComponent<Game.Objects.Transform>(targetStop)
                || !EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            float3 vehiclePosition = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPosition = EntityManager.GetComponentData<Game.Objects.Transform>(targetStop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > AT_STOP_MAX_DIST;
        }

        private Entity GetConnectedStopForWaypoint(Entity waypoint)
        {
            if (waypoint == Entity.Null
                || !EntityManager.Exists(waypoint)
                || !EntityManager.HasComponent<Connected>(waypoint))
                return Entity.Null;

            Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return connected != Entity.Null && EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
        }

        internal float CalculateLineDuration(Entity line)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line)) return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return 0f;
            float stopDuration = EntityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration;

            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            var segBuffers = GetBufferLookup<RouteSegment>(true);
            var pathInfoLookup = GetComponentLookup<PathInformation>(true);
            var vehicleTimingLookup = GetComponentLookup<VehicleTiming>(true);

            if (!wpBuffers.TryGetBuffer(line, out var waypoints)) return 0f;
            if (!segBuffers.TryGetBuffer(line, out var segments)) return 0f;
            if (waypoints.Length == 0 || segments.Length == 0) return 0f;

            int firstWaypoint = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (vehicleTimingLookup.HasComponent(waypoints[w].m_Waypoint))
                {
                    firstWaypoint = w;
                    break;
                }
            }

            float duration = 0f;
            for (int i = 0; i < waypoints.Length; i++)
            {
                int wi = (firstWaypoint + i) % waypoints.Length;
                int wi1 = (wi + 1) % waypoints.Length;
                Entity seg = segments[wi].m_Segment;
                if (pathInfoLookup.TryGetComponent(seg, out var pathInfo))
                    duration += pathInfo.m_Duration;
                if (vehicleTimingLookup.HasComponent(waypoints[wi1].m_Waypoint))
                    duration += stopDuration;
            }
            return duration;
        }

        internal bool HasInboundVehicleNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            Entity stationA = wps[0].m_Waypoint;
            Entity stopA = stationA != Entity.Null
                && EntityManager.Exists(stationA)
                && EntityManager.HasComponent<Connected>(stationA)
                ? EntityManager.GetComponentData<Connected>(stationA).m_Connected
                : Entity.Null;
            if (stopA == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                return false;

            float3 stationPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
            float radiusSq = radiusMeters * radiusMeters;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity nearV = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (nearV == ignoreVehicle) continue;
                if (!EntityManager.Exists(nearV)) continue;
                if (!m_VehicleView.TryGetState(nearV, out var nearState)) continue;
                bool isPreparing = nearState == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles) continue;
                bool isTaggedInbound = m_VehicleView.IsInbound(nearV);
                if (!isPreparing && !isTaggedInbound) continue;
                if (!EntityManager.HasComponent<Game.Objects.Transform>(nearV)) continue;

                float3 nearPos = EntityManager.GetComponentData<Game.Objects.Transform>(nearV).m_Position;
                float3 delta = nearPos - stationPos;
                float distSq = math.lengthsq(delta);
                if (distSq <= radiusSq)
                    return true;
            }

            return false;
        }

        internal bool IsFreshDispatchedPreparingVehicle(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleView.TryGetDispatch(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= PREPARING_ROUTE_FIX_GRACE_FRAMES;
        }

        internal int CountActiveVehicles(Entity line, BufferLookup<RouteVehicle> rvBuffers)
        {
            int count = 0;
            if (!rvBuffers.TryGetBuffer(line, out var rvs)) return 0;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (!EntityManager.Exists(v)) continue;
                if (m_VehicleView.TryGetState(v, out var st) && st == VehicleState.Retiring) continue;
                count++;
            }
            return count;
        }

        internal float GetRemainingRange(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v)) return float.MaxValue;
            if (!EntityManager.HasComponent<PrefabRef>(v)) return float.MaxValue;
            float cur = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(pref)) return float.MaxValue;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
            return (range > 0f) ? (range - cur) : float.MaxValue;
        }

        internal bool NeedsMaintenance(Entity v)
        {
            var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
            if ((pt.m_State & PublicTransportFlags.RequiresMaintenance) != 0) return true;
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return false;
            float dist = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return false;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            return range > 0f && (dist / range) >= MAINTENANCE_THRESHOLD;
        }

        internal bool CanFinishNextLap(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return true;
            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return true;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            if (range <= 0f) return true;
            float remaining = range - current;
            if (m_LapObservations.TryDistance(v, out float lapDist) && lapDist > 0f)
                return remaining >= lapDist;
            return (current / range) < MAINTENANCE_THRESHOLD;
        }

    }
}
