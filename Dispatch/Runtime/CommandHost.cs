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

        public PublicTransport ReadPublicTransport(Entity vehicle)
        {
            return m_RailEvents.TryReadPublicTransportForWrite(vehicle, out PublicTransport value)
                ? value
                : EntityManager.GetComponentData<PublicTransport>(vehicle);
        }

        public Target ReadTarget(Entity vehicle) => m_RailEvents.TryReadTargetForWrite(vehicle, out Target value)
            ? value
            : EntityManager.GetComponentData<Target>(vehicle);
        public Owner ReadOwner(Entity vehicle) => EntityManager.GetComponentData<Owner>(vehicle);
        public PathOwner ReadPath(Entity vehicle) => m_RailEvents.TryReadPathForWrite(vehicle, out PathOwner value)
            ? value
            : EntityManager.GetComponentData<PathOwner>(vehicle);

        public int ReadPathElementCount(Entity vehicle) => m_RailEvents.TryReadPathElementCountForWrite(vehicle, out int value)
            ? value
            : EntityManager.Exists(vehicle) && EntityManager.HasBuffer<PathElement>(vehicle)
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

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport value)
        {
            m_RailEvents.AppendPublicTransportWrite(vehicle, value, SimulationSystem.frameIndex);
        }

        public void SetPublicTransport(Entity vehicle, PublicTransport value)
        {
            m_RailEvents.AppendPublicTransportWrite(vehicle, value, SimulationSystem.frameIndex);
            EntityManager.SetComponentData(vehicle, value);
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

        public void ResetLaunchNavigation(Entity vehicle, EntityCommandBuffer ecb)
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
            m_RailEvents.AppendLaunchNavigationWrite(vehicle, headLane);
            ecb.SetComponent(head, headLane);
            if (tail != head)
            {
                // 原版消费新路径时可能折返，当前尾部后端随后成为实际头部前端。
                TrainCurrentLane tailLane = EntityManager.GetComponentData<TrainCurrentLane>(tail);
                tailLane.m_Rear.m_LaneFlags &= ~TrainLaneFlags.EndOfPath;
                ecb.SetComponent(tail, tailLane);
            }

            ecb.SetBuffer<TrainNavigationLane>(vehicle).Clear();
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
