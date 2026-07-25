using System;
using Game.Simulation;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class CommandHost
    {
        private readonly RailEventSource m_RailEvents;
        private readonly RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe m_HotPathProbe;

        public CommandHost(ModRuntimeHostSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_RailEvents = runtime.m_RailEventSource;
            m_HotPathProbe = runtime.m_RuntimeHotPathProbe;
        }

        public EntityManager EntityManager { get; }
        public SimulationSystem SimulationSystem { get; }
        public TimedLogger Log { get; }

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return m_RailEvents.TryReadPublicTransportForWrite(vehicle, out PublicTransport value)
                ? value
                : EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        public Target ReadTarget(Entity vehicle) => m_RailEvents.TryReadTargetForWrite(vehicle, out Target value)
            ? value
            : EntityManager.GetComponentData<Target>(vehicle);
        public PathOwner ReadPath(Entity vehicle) => m_RailEvents.TryReadPathForWrite(vehicle, out PathOwner value)
            ? value
            : EntityManager.GetComponentData<PathOwner>(vehicle);

        public bool TryGetRouteWaypoints(Entity vehicle, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            waypoints = default;
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
                return false;

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (route == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(route))
                return false;

            waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            return waypoints.Length >= 2;
        }

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport value)
        {
            m_RailEvents.AppendPublicTransportWrite(vehicle, value, SimulationSystem.frameIndex);
        }

        public void AppendTargetWrite(Entity vehicle, Target value)
        {
            m_RailEvents.AppendTargetWrite(vehicle, value, SimulationSystem.frameIndex);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, int pathElementCount)
        {
            m_RailEvents.AppendPathWrite(vehicle, value, hasPathElements, pathElementCount, SimulationSystem.frameIndex);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, DynamicBuffer<PathElement> path)
        {
            m_RailEvents.AppendPathWrite(
                vehicle,
                value,
                hasPathElements,
                path.Length,
                SimulationSystem.frameIndex);
        }

        public void CountPathDetailRead() => m_HotPathProbe.CountPathDetailRead();

    }
}
