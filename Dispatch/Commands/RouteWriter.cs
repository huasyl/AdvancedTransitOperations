using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Commands
{
    internal sealed class RouteWriter
    {
        private readonly CommandHost m_Host;

        public RouteWriter(CommandHost host)
        {
            m_Host = host;
        }

        public void Repath(
            Entity vehicle,
            PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb)
        {
            if (m_Host.EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = m_Host.ReadPath(vehicle);
                pathOwner.m_State = PathFlags.Obsolete;
                pathOwner.m_ElementIndex = 0;
                m_Host.SetPath(vehicle, pathOwner);
            }

            m_Host.EntityManager.GetBuffer<PathElement>(vehicle).Clear();
            m_Host.SetTarget(vehicle, target);
            m_Host.SetPublicTransport(vehicle, publicTransport);
            ecb.AddComponent<PathfindUpdated>(vehicle);
            ecb.AddComponent<Updated>(vehicle);
        }

    }
}
