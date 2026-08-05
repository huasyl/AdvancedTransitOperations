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

        public void CommitVehicleDepartureState(
            Entity vehicle,
            PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb)
        {
            m_Host.AppendTargetWrite(vehicle, target);
            ecb.SetComponent(vehicle, target);
            m_Host.AppendPublicTransportWrite(vehicle, publicTransport);
            ecb.SetComponent(vehicle, publicTransport);
            ecb.AddComponent<Updated>(vehicle);
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
                m_Host.AppendPathWrite(vehicle, pathOwner, true, 0);
                ecb.SetComponent(vehicle, pathOwner);
            }

            ecb.SetBuffer<PathElement>(vehicle).Clear();
            m_Host.AppendTargetWrite(vehicle, target);
            ecb.SetComponent(vehicle, target);
            m_Host.AppendPublicTransportWrite(vehicle, publicTransport);
            ecb.SetComponent(vehicle, publicTransport);
            ecb.AddComponent<PathfindUpdated>(vehicle);
            ecb.AddComponent<Updated>(vehicle);
        }

        public bool TryApplyLaunchSegmentPath(
            Entity vehicle,
            ref PublicTransport publicTransport,
            ref Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            EntityCommandBuffer ecb)
        {
            if (waypoints.Length < 2)
                return false;

            Entity route = m_Host.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Host.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;

            if (route == Entity.Null || !m_Host.EntityManager.Exists(route) || !m_Host.EntityManager.HasBuffer<RouteSegment>(route))
                return false;

            DynamicBuffer<RouteSegment> segments = m_Host.EntityManager.GetBuffer<RouteSegment>(route, true);
            if (segments.Length == 0)
                return false;

            Entity firstSegment = segments[0].m_Segment;
            if (firstSegment == Entity.Null
                || !m_Host.EntityManager.Exists(firstSegment)
                || !m_Host.EntityManager.HasBuffer<PathElement>(firstSegment))
            {
                return false;
            }

            DynamicBuffer<PathElement> segmentPath = m_Host.EntityManager.GetBuffer<PathElement>(firstSegment, true);
            if (segmentPath.Length == 0)
                return false;

            target.m_Target = waypoints[1].m_Waypoint;
            CommitVehicleDepartureState(vehicle, publicTransport, target, ecb);

            if (m_Host.EntityManager.HasComponent<PathOwner>(vehicle))
            {
                PathOwner pathOwner = new PathOwner(PathFlags.Updated);
                m_Host.AppendPathWrite(vehicle, pathOwner, true, segmentPath);
                ecb.SetComponent(vehicle, pathOwner);
            }

            DynamicBuffer<PathElement> targetPath = ecb.SetBuffer<PathElement>(vehicle);
            targetPath.Clear();
            m_Host.CountPathDetailRead();
            for (int i = 0; i < segmentPath.Length; i++)
                targetPath.Add(segmentPath[i]);

            ecb.AddComponent<Updated>(vehicle);
            return true;
        }
    }
}
