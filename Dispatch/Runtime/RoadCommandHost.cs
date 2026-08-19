using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Commands;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum RoadPreparingResult
    {
        None,
        Pin,
        Retarget,
        Pending,
        Stuck,
        UnsafeState,
        InvalidOrigin
    }

    internal enum RoadOriginGuardResult
    {
        None,
        Pin,
        Protected,
        Boarding,
        InvalidOrigin
    }

    internal sealed class RoadCommandHost : IPublicTransportWritePort
    {
        private readonly RoadEventSource m_RoadEvents;

        internal RoadCommandHost(ModRuntimeHostSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_RoadEvents = runtime.m_RoadEventSource;
        }

        internal EntityManager EntityManager { get; }
        internal SimulationSystem SimulationSystem { get; }
        internal TimedLogger Log { get; }
        public uint Frame => SimulationSystem.frameIndex;

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return m_RoadEvents.TryReadPublicTransportForWrite(vehicle, out PublicTransport value)
                ? value
                : EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport value)
        {
            m_RoadEvents.AppendPublicTransportWrite(vehicle, value, Frame);
        }

        public void CommitPublicTransport(Entity vehicle, PublicTransport value)
        {
            m_RoadEvents.AppendPublicTransportWrite(vehicle, value, Frame);
            EntityManager.SetComponentData(vehicle, value);
        }

        public Target ReadTarget(Entity vehicle)
        {
            return EntityManager.GetComponentData<Target>(vehicle);
        }

        public Owner ReadOwner(Entity vehicle)
        {
            return EntityManager.GetComponentData<Owner>(vehicle);
        }

        internal RoadPreparingResult EnsurePreparingOrigin(
            Entity vehicle,
            Entity line,
            EntityCommandBuffer ecb)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle)
                || !EntityManager.HasComponent<PathOwner>(vehicle)
                || !EntityManager.HasComponent<PublicTransport>(vehicle)
                || EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != line
                || !RouteWaypointEndpointResolver.TryGetRoadOrigin(EntityManager, line, out Entity origin))
            {
                return RoadPreparingResult.InvalidOrigin;
            }

            PublicTransport publicTransport = ReadPublicTransport(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            if (target.m_Target == origin)
            {
                if (boarding || !PinOrigin(vehicle, ref publicTransport, ecb))
                    return RoadPreparingResult.None;

                return RoadPreparingResult.Pin;
            }

            PublicTransportFlags unsafeFlags = PublicTransportFlags.Testing
                | PublicTransportFlags.Arriving
                | PublicTransportFlags.Boarding;
            if ((publicTransport.m_State & unsafeFlags) != 0)
                return RoadPreparingResult.UnsafeState;

            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(vehicle);
            if ((pathOwner.m_State & PathFlags.Stuck) != 0)
                return RoadPreparingResult.Stuck;
            if ((pathOwner.m_State & PathFlags.Pending) != 0)
                return RoadPreparingResult.Pending;

            VehicleUtils.SetTarget(ref pathOwner, ref target, origin);
            m_RoadEvents.AppendPreparingTargetWrite(vehicle, target);
            ecb.SetComponent(vehicle, target);
            ecb.SetComponent(vehicle, pathOwner);
            PinOrigin(vehicle, ref publicTransport, ecb);
            return RoadPreparingResult.Retarget;
        }

        internal RoadOriginGuardResult EnsureRunningOriginStop(
            Entity vehicle,
            Entity line,
            EntityCommandBuffer ecb)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !EntityManager.HasComponent<PublicTransport>(vehicle)
                || EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != line
                || !RouteWaypointEndpointResolver.TryGetRoadOrigin(EntityManager, line, out Entity origin))
            {
                return RoadOriginGuardResult.InvalidOrigin;
            }

            PublicTransport publicTransport = ReadPublicTransport(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Boarding) != 0)
                return RoadOriginGuardResult.Boarding;

            if ((publicTransport.m_State & PublicTransportFlags.RequireStop) != 0)
                return RoadOriginGuardResult.Protected;

            publicTransport.m_State |= PublicTransportFlags.RequireStop;
            AppendPublicTransportWrite(vehicle, publicTransport);
            ecb.SetComponent(vehicle, publicTransport);
            return RoadOriginGuardResult.Pin;
        }

        internal RoadOriginGuardResult EnsureRunningTimedStop(
            Entity vehicle,
            Entity line,
            int waypointIndex,
            EntityCommandBuffer ecb)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex < 0
                || !EntityManager.Exists(vehicle)
                || !EntityManager.Exists(line)
                || !EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle)
                || !EntityManager.HasComponent<PublicTransport>(vehicle)
                || !EntityManager.HasBuffer<RouteWaypoint>(line)
                || EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route != line)
            {
                return RoadOriginGuardResult.InvalidOrigin;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length
                || waypoints[waypointIndex].m_Waypoint == Entity.Null
                || EntityManager.GetComponentData<Target>(vehicle).m_Target != waypoints[waypointIndex].m_Waypoint)
            {
                return RoadOriginGuardResult.InvalidOrigin;
            }

            PublicTransport publicTransport = ReadPublicTransport(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Boarding) != 0)
                return RoadOriginGuardResult.Boarding;

            if ((publicTransport.m_State & PublicTransportFlags.RequireStop) != 0)
                return RoadOriginGuardResult.Protected;

            publicTransport.m_State |= PublicTransportFlags.RequireStop;
            AppendPublicTransportWrite(vehicle, publicTransport);
            ecb.SetComponent(vehicle, publicTransport);
            return RoadOriginGuardResult.Pin;
        }

        private bool PinOrigin(
            Entity vehicle,
            ref PublicTransport publicTransport,
            EntityCommandBuffer ecb)
        {
            if ((publicTransport.m_State & PublicTransportFlags.RequireStop) != 0)
                return false;

            publicTransport.m_State |= PublicTransportFlags.RequireStop;
            AppendPublicTransportWrite(vehicle, publicTransport);
            ecb.SetComponent(vehicle, publicTransport);
            return true;
        }
    }
}
