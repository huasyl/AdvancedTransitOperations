using Game;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class LaunchActions
    {
        private readonly CommandHost m_Host;
        private readonly RouteWriter m_RouteWriter;

        public LaunchActions(CommandHost host, RouteWriter routeWriter)
        {
            m_Host = host;
            m_RouteWriter = routeWriter;
        }

        public bool EnsurePreparingRoute(
            Entity vehicle,
            Entity line,
            int currentWaypointIndex,
            EntityCommandBuffer ecb)
        {
            if (line == Entity.Null
                || !m_Host.EntityManager.HasComponent<Target>(vehicle)
                || !m_Host.TryGetRouteWaypoints(vehicle, out DynamicBuffer<RouteWaypoint> waypoints))
            {
                return false;
            }

            PublicTransport publicTransport = m_Host.ReadPublicTransport(vehicle);
            Target target = m_Host.ReadTarget(vehicle);
            Entity stationA = waypoints[0].m_Waypoint;
            bool wrongTarget = target.m_Target != stationA;
            bool driftedToMidStop = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0
                && currentWaypointIndex > 0;
            if (!wrongTarget && !driftedToMidStop)
                return false;

            publicTransport.m_State &= ~PublicTransportFlags.Boarding;
            publicTransport.m_DepartureFrame = m_Host.SimulationSystem.frameIndex + 9999;
            target.m_Target = stationA;
            m_RouteWriter.Repath(vehicle, publicTransport, target, ecb);
            return true;
        }

    }
}
