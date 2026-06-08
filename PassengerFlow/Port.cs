using Game.Routes;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Port
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        internal Port(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal uint Frame()
            => m_Runtime.m_SimulationSystem != null ? m_Runtime.m_SimulationSystem.frameIndex : 0u;

        internal int FramesPerMinute()
            => (int)Unity.Mathematics.math.round((float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);

        internal NativeArray<Entity> Vehicles(Allocator allocator)
            => m_Runtime.m_VehicleView.Keys(allocator);

        internal bool TryState(Entity vehicle, out VehicleState state)
            => m_Runtime.TryGetRuntimeVehicleState(vehicle, out state);

        internal bool TryLine(Entity vehicle, out Entity line)
            => m_Runtime.m_VehicleView.TryGetLine(vehicle, out line);

        internal bool TryLaunchFrame(Entity vehicle, out uint launchFrame)
            => m_Runtime.m_VehicleView.TryGetLaunch(vehicle, out launchFrame);

        internal bool TryCachedWaypoint(Entity vehicle, out int waypointIndex)
            => m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out waypointIndex);

        internal bool IsBoarding(Entity vehicle)
            => m_Runtime.IsVehicleBoarding(vehicle);

        internal bool TryAcceptedBoarding(Entity vehicle, out bool boarding)
            => m_Runtime.m_LastBoarding.TryGetValue(vehicle, out boarding);

        internal bool HasWaypoints(Entity line)
            => line != Entity.Null && m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line);

        internal DynamicBuffer<RouteWaypoint> Waypoints(Entity line)
            => m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);

        internal bool TryWaypoint(Entity line, int waypointIndex, out Entity waypoint)
        {
            waypoint = Entity.Null;
            if (line == Entity.Null
                || waypointIndex < 0
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length)
                return false;

            waypoint = waypoints[waypointIndex].m_Waypoint;
            return waypoint != Entity.Null;
        }

        internal bool TryDwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            anchor = default;
            return m_Runtime.m_Observation != null && m_Runtime.m_Observation.DwellAnchor(line, waypointIndex, out anchor);
        }

        internal bool TryTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            chain = null;
            return m_Runtime.m_TrackModel != null && m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out chain);
        }

        internal string LineId(Entity line)
        {
            TransitMode mode = ModeOf(line);
            return LineId(line, mode);
        }

        internal string LineId(Entity line, TransitMode mode)
        {
            string lineId = m_Runtime.LineId(line);
            return LineIdentityService.GetId(LineIdentityService.GetKey(lineId, mode));
        }

        internal TransitMode ModeOf(Entity line)
            => TransportModeResolver.Resolve(m_Runtime.EntityManager, line);

        internal string Name(Entity entity)
            => entity != Entity.Null ? m_Runtime.EntityName(entity) : string.Empty;

        internal string StationName(Entity stopEntity)
            => stopEntity != Entity.Null && m_Runtime.m_Resolve != null
                ? m_Runtime.m_Resolve.StationName(stopEntity)
                : string.Empty;

        internal string EnsureSak(Entity anchor)
            => m_Runtime.m_Resolve != null ? m_Runtime.m_Resolve.EnsureSak(anchor) : string.Empty;

        internal Entity RuntimeVehicle(Entity vehicle)
            => m_Runtime.m_Resolve != null ? m_Runtime.m_Resolve.RuntimeVehicle(vehicle) : vehicle;

        internal void Log(string message)
        {
            if (!string.IsNullOrEmpty(message))
                m_Runtime.log.Info(message);
        }
    }
}
