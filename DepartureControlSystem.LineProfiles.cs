using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private float GetDistanceToOriginMeters(Entity v, DynamicBuffer<RouteWaypoint> wps)
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

        private bool ShouldEvaluateRunningOriginSettleCheck(
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
                || m_OriginArrivalCandidateSinceFrame.ContainsKey(vehicle))
            {
                m_PerfProbeOriginSettleFastPathHits++;
                return true;
            }

            m_PerfProbeOriginSettleSlowPathEntered++;
            bool hasCurrentSnapshot = m_VehicleTrackCursorFrameSnapshots.TryGetValue(vehicle, out VehicleTrackCursorFrameSnapshot snapshot)
                && snapshot.Frame == m_SimulationSystem.frameIndex
                && snapshot.Available;
            if (!hasCurrentSnapshot)
                m_PerfProbeOriginSettlePreSnapshotMisses++;

            Entity line = ResolveVehicleLine(vehicle);
            if (line == Entity.Null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || !hasCurrentSnapshot
                || snapshot.LineEntity != line
                || snapshot.ChainSignature != chain.Signature
                || !snapshot.Available)
            {
                return false;
            }

            int atomCursorIndex = snapshot.Cursor.AtomCursorIndex;
            if (atomCursorIndex < 0 || atomCursorIndex >= chain.TrackAtoms.Count)
                return false;

            const int originAtomWindow = 2;
            bool inOriginWindow = atomCursorIndex <= originAtomWindow
                || atomCursorIndex >= math.max(0, chain.TrackAtoms.Count - 1 - originAtomWindow);
            if (inOriginWindow)
                m_PerfProbeOriginSettleWindowHits++;
            return inOriginWindow;
        }

        private bool ShouldSettleRunningAtOrigin(
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
                    || m_NearingTerminus.Contains(v)
                    || m_VehicleCurrentSlot.ContainsKey(v));

            if (!waitingAtOrigin)
            {
                float originDist = GetDistanceToOriginMeters(v, wps);
                if (originDist > ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }

                if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }

                if (nextWaypointIndex != 0 || segmentPosition < ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS)
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }
            }

            if (!m_OriginArrivalCandidateSinceFrame.TryGetValue(v, out uint sinceFrame))
            {
                m_OriginArrivalCandidateSinceFrame[v] = nowFrame;
                return false;
            }

            if ((nowFrame - sinceFrame) < ORIGIN_FORCE_IDLE_SETTLE_FRAMES)
                return false;

            return true;
        }

        private bool IsBorderlineOriginArrivalCandidate(Entity v, DynamicBuffer<RouteWaypoint> wps)
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
                if (!m_VehicleState.TryGetValue(v, out var state) || state != VehicleState.Running) continue;
                if (m_VehicleTargetMin.TryGetValue(v, out int target) && target >= 0) continue;
                if (m_LaunchCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil) continue;
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
            if (m_LaunchCooldownUntil.TryGetValue(nearestVehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return false;

            if (GetDistanceToOriginMeters(nearestVehicle, wps) <= ORIGIN_CONGESTION_RADIUS_METERS)
                return true;

            if (TryGetRouteProgress(nearestVehicle, out int nextWaypointIndex, out float segmentPosition))
                return nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f);

            return false;
        }

        private bool IsWaitingForcedOriginDwell(Entity v, uint nowFrame)
        {
            return m_ForcedOriginReadyFrame.TryGetValue(v, out uint readyFrame) && nowFrame < readyFrame;
        }

        private bool TryGetRouteProgress(Entity transportVehicle, out int nextWaypointIndex, out float segmentPosition)
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
    }
}
