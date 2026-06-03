using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class LineProfile
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public LineProfile(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public float DistanceToOrigin(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            EntityManager entityManager = m_Runtime.EntityManager;
            if (!entityManager.HasComponent<Game.Objects.Transform>(vehicle))
                return float.MaxValue;

            Entity stop = waypoints[0].m_Waypoint;
            if (entityManager.HasComponent<Connected>(stop))
                stop = entityManager.GetComponentData<Connected>(stop).m_Connected;
            if (stop == Entity.Null || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                return float.MaxValue;

            float3 vehiclePos = entityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPos = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            return math.distance(vehiclePos, stopPos);
        }

        public bool ShouldEvaluateOriginSettle(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            bool boarding,
            bool lastBoarding,
            int targetMin)
        {
            m_Runtime.m_PerfProbeOriginSettleCalls++;
            if (vehicle == Entity.Null || waypoints.Length == 0)
                return false;

            if (atOrigin
                || boarding
                || lastBoarding
                || targetMin >= 0
                || m_Runtime.m_VehicleStateStore.OriginArrivalCandidateSinceFrame.ContainsKey(vehicle))
            {
                m_Runtime.m_PerfProbeOriginSettleFastPathHits++;
                return true;
            }

            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            if (line == Entity.Null
                || !m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            m_Runtime.m_PerfProbeOriginSettleSlowPathEntered++;
            if (!m_Runtime.m_TrackProjection.TrySnapshot(
                    vehicle,
                    line,
                    chain.Signature,
                    m_Runtime.m_SimulationSystem.frameIndex,
                    out VehicleTrackCursor cursor))
            {
                m_Runtime.m_PerfProbeOriginSettlePreSnapshotMisses++;
                return false;
            }

            int atomCursorIndex = cursor.AtomCursorIndex;
            if (atomCursorIndex < 0 || atomCursorIndex >= chain.TrackAtoms.Count)
                return false;

            const int originAtomWindow = 2;
            bool inOriginWindow = atomCursorIndex <= originAtomWindow
                || atomCursorIndex >= math.max(0, chain.TrackAtoms.Count - 1 - originAtomWindow);
            if (inOriginWindow)
                m_Runtime.m_PerfProbeOriginSettleWindowHits++;
            return inOriginWindow;
        }

        public bool ShouldSettleAtOrigin(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            bool atOrigin,
            bool boarding,
            bool lastBoarding,
            int targetMin)
        {
            bool waitingAtOrigin = atOrigin
                && (boarding
                    || lastBoarding
                    || targetMin >= 0
                    || m_Runtime.m_VehicleView.IsInbound(vehicle)
                    || m_Runtime.m_VehicleStateStore.CurrentSlot.ContainsKey(vehicle));

            if (!waitingAtOrigin)
            {
                float originDist = DistanceToOrigin(vehicle, waypoints);
                if (originDist > DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }

                if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }

                if (nextWaypointIndex != 0 || segmentPosition < 0.92f)
                {
                    m_Runtime.m_RuntimeController.ClearOriginCandidate(vehicle);
                    return false;
                }
            }

            if (!m_Runtime.m_VehicleView.TryGetOrigin(vehicle, out uint sinceFrame))
            {
                m_Runtime.m_RuntimeController.SetOriginCandidate(vehicle, nowFrame);
                return false;
            }

            return (nowFrame - sinceFrame) >= 180;
        }

        public bool IsBorderlineOriginArrivalCandidate(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (DistanceToOrigin(vehicle, waypoints) > DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS)
                return false;

            if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return false;

            return nextWaypointIndex == 0 && segmentPosition >= 0.92f;
        }

        public bool HasBorderlineOriginArrivalCandidate(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            float slotFramesAway,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            float waitFrames = 2f * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                return false;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i].m_Vehicle;
                if (!m_Runtime.EntityManager.Exists(vehicle))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) || state != VehicleState.Running)
                    continue;
                if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) && target >= 0)
                    continue;
                if (m_Runtime.m_VehicleView.TryGetCooldown(vehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                    continue;
                if (!IsBorderlineOriginArrivalCandidate(vehicle, waypoints))
                    continue;

                float eta = m_Runtime.EstimateRunningArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
                if (eta != float.MaxValue && eta <= slotFramesAway + waitFrames)
                    return true;
            }

            return false;
        }

        public bool ShouldHoldSpawnForNearestRunningCandidate(
            Entity nearestVehicle,
            VehicleState nearestState,
            float nearestEta,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (nearestVehicle == Entity.Null || nearestState != VehicleState.Running || nearestEta == float.MaxValue)
                return false;

            float waitFrames = 2f * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            if (nearestEta > waitFrames)
                return false;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (m_Runtime.m_VehicleView.TryGetCooldown(nearestVehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return false;

            if (DistanceToOrigin(nearestVehicle, waypoints) <= DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS)
                return true;

            if (m_Runtime.m_RouteProgress.Try(nearestVehicle, out int nextWaypointIndex, out float segmentPosition))
                return nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f);

            return false;
        }

        public bool HasInboundNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            EntityManager entityManager = m_Runtime.EntityManager;
            Entity station = waypoints[0].m_Waypoint;
            Entity stop = station != Entity.Null
                && entityManager.Exists(station)
                && entityManager.HasComponent<Connected>(station)
                ? entityManager.GetComponentData<Connected>(station).m_Connected
                : Entity.Null;
            if (stop == Entity.Null || !entityManager.HasComponent<Game.Objects.Transform>(stop))
                return false;

            float3 stationPos = entityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
            float radiusSq = radiusMeters * radiusMeters;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                return false;

            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(vehicles[i].m_Vehicle);
                if (vehicle == ignoreVehicle || !entityManager.Exists(vehicle))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                    continue;
                bool isPreparing = state == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles)
                    continue;
                if (!isPreparing && !m_Runtime.m_VehicleView.IsInbound(vehicle))
                    continue;
                if (!entityManager.HasComponent<Game.Objects.Transform>(vehicle))
                    continue;

                float3 delta = entityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position - stationPos;
                if (math.lengthsq(delta) <= radiusSq)
                    return true;
            }

            return false;
        }
    }
}
