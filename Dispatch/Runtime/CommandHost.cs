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
        private readonly Func<Entity, Entity> m_ReadVehicleLine;
        private readonly RailEventSource m_RailEvents;
        private readonly RuntimeWorksets m_Worksets;

        public CommandHost(ModRuntimeHostSystem runtime)
        {
            EntityManager = runtime.EntityManager;
            SimulationSystem = runtime.m_SimulationSystem;
            Log = runtime.log;
            m_ReadVehicleLine = vehicle => runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null;
            m_RailEvents = runtime.m_RailEventSource;
            m_Worksets = runtime.m_RuntimeWorksets;
        }

        public EntityManager EntityManager { get; }
        public SimulationSystem SimulationSystem { get; }
        public TimedLogger Log { get; }

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return m_RailEvents.ReadPublicTransport(vehicle);
        }

        public Target ReadTarget(Entity vehicle) => m_RailEvents.ReadTarget(vehicle);
        public PathOwner ReadPath(Entity vehicle) => m_RailEvents.ReadPath(vehicle);

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
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendTargetWrite(Entity vehicle, Target value)
        {
            m_RailEvents.AppendTargetWrite(vehicle, value, SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, int pathElementCount)
        {
            m_RailEvents.AppendPathWrite(vehicle, value, hasPathElements, pathElementCount, 0UL, SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner value, bool hasPathElements, DynamicBuffer<PathElement> path)
        {
            m_RailEvents.AppendPathWrite(
                vehicle,
                value,
                hasPathElements,
                path.Length,
                RailEventSource.PathSignature(path),
                SimulationSystem.frameIndex);
            m_Worksets.AddCandidate(vehicle);
        }

        public bool TryGetVehicleLine(Entity vehicle, out Entity line)
        {
            line = m_ReadVehicleLine(vehicle);
            return line != Entity.Null;
        }

    }
}
