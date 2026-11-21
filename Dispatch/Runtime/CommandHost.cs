using System;
using Game.Simulation;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Commands;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class CommandHost : IPublicTransportWritePort
    {
        private readonly RailEventSource m_RailEvents;

        public CommandHost(ModRuntimeHostSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_RailEvents = runtime.m_RailEventSource;
        }

        public EntityManager EntityManager { get; }
        public SimulationSystem SimulationSystem { get; }
        public TimedLogger Log { get; }
        public uint Frame => SimulationSystem.frameIndex;

        public PublicTransport ReadPublicTransport(Entity vehicle) => EntityManager.GetComponentData<PublicTransport>(vehicle);
        public Target ReadTarget(Entity vehicle) => EntityManager.GetComponentData<Target>(vehicle);
        public Owner ReadOwner(Entity vehicle) => EntityManager.GetComponentData<Owner>(vehicle);
        public PathOwner ReadPath(Entity vehicle) => EntityManager.GetComponentData<PathOwner>(vehicle);

        public int ReadPathElementCount(Entity vehicle) => EntityManager.Exists(vehicle) && EntityManager.HasBuffer<PathElement>(vehicle)
            ? EntityManager.GetBuffer<PathElement>(vehicle, true).Length
            : 0;

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

        public void SetPublicTransport(Entity vehicle, PublicTransport value)
        {
            m_RailEvents.RecordPublicTransportWrite(vehicle, value, Frame);
            EntityManager.SetComponentData(vehicle, value);
        }

        public void SetTarget(Entity vehicle, Target value)
        {
            bool changed = ReadTarget(vehicle).m_Target != value.m_Target;
            EntityManager.SetComponentData(vehicle, value);
            if (changed)
                m_RailEvents.InvalidateProjection(vehicle);
        }

        public void SetPath(Entity vehicle, PathOwner value)
        {
            EntityManager.SetComponentData(vehicle, value);
            m_RailEvents.RecordPathChange(vehicle);
        }

        public bool HasConsumedPath(Entity entity)
        {
            if (entity == Entity.Null
                || !EntityManager.Exists(entity)
                || !EntityManager.HasBuffer<TrainNavigationLane>(entity)
                || EntityManager.GetBuffer<TrainNavigationLane>(entity, true).Length != 0
                || !EntityManager.HasBuffer<PathElement>(entity)
                || !EntityManager.HasComponent<PathOwner>(entity))
            {
                return false;
            }

            PathOwner pathOwner = ReadPath(entity);
            int pathElementCount = ReadPathElementCount(entity);
            return pathElementCount >= 0 && pathOwner.m_ElementIndex >= pathElementCount;
        }

    }
}
