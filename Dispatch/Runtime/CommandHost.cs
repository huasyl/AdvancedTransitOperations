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

        public void CountPathDetailRead() => m_HotPathProbe.CountPathDetailRead();

        public void ResetLaunchNavigation(Entity vehicle)
        {
            Entity head = vehicle;
            Entity tail = vehicle;
            if (EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length != 0)
                {
                    head = layout[0].m_Vehicle;
                    tail = layout[layout.Length - 1].m_Vehicle;
                }
            }

            TrainCurrentLane headLane = EntityManager.GetComponentData<TrainCurrentLane>(head);
            // 对齐原版寻路准备及非追加导航更新，保留到达位置与其他标志。
            headLane.m_Front.m_LaneFlags &= ~(TrainLaneFlags.EndOfPath | TrainLaneFlags.Return);
            headLane.m_Rear.m_LaneFlags &= ~TrainLaneFlags.EndOfPath;
            EntityManager.SetComponentData(head, headLane);
            if (tail != head)
            {
                // 原版消费新路径时可能折返，当前尾部后端随后成为实际头部前端。
                TrainCurrentLane tailLane = EntityManager.GetComponentData<TrainCurrentLane>(tail);
                tailLane.m_Rear.m_LaneFlags &= ~TrainLaneFlags.EndOfPath;
                EntityManager.SetComponentData(tail, tailLane);
            }

            EntityManager.GetBuffer<TrainNavigationLane>(vehicle).Clear();
            m_RailEvents.InvalidateProjection(vehicle);
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
