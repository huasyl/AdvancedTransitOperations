using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    internal delegate bool RuntimeReadyDelegate();
    internal delegate bool TryVehicleStateDelegate(Entity vehicle, out VehicleState state);
    internal delegate int ComputeWaypointIndexDelegate(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints);
    internal delegate bool TryOriginProgressDelegate(Entity vehicle, out int waypointIndex, out float segmentPosition);

    internal sealed class OriginRepairPort
    {
        private readonly RuntimeReadyDelegate m_IsReady;
        private readonly TryVehicleStateDelegate m_TryVehicleState;
        private readonly ComputeWaypointIndexDelegate m_ComputeWaypointIndex;
        private readonly TryOriginProgressDelegate m_TryOriginProgress;

        public OriginRepairPort(
            RuntimeReadyDelegate isReady,
            TryVehicleStateDelegate tryVehicleState,
            ComputeWaypointIndexDelegate computeWaypointIndex,
            TryOriginProgressDelegate tryOriginProgress)
        {
            m_IsReady = isReady;
            m_TryVehicleState = tryVehicleState;
            m_ComputeWaypointIndex = computeWaypointIndex;
            m_TryOriginProgress = tryOriginProgress;
        }

        public bool IsReady()
        {
            return m_IsReady();
        }

        public bool TryVehicleState(Entity vehicle, out VehicleState state)
        {
            return m_TryVehicleState(vehicle, out state);
        }

        public int ComputeWaypointIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints)
        {
            return m_ComputeWaypointIndex(vehicle, waypoints);
        }

        public bool TryOriginProgress(Entity vehicle, out int waypointIndex, out float segmentPosition)
        {
            return m_TryOriginProgress(vehicle, out waypointIndex, out segmentPosition);
        }
    }
}
