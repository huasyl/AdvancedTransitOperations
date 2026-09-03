using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.SignalPriority
{
    internal readonly struct SignalLaneFact
    {
        internal readonly Entity Lane;
        internal readonly Entity Intersection;
        internal readonly LaneSignal Value;
        internal readonly ushort GroupMask;
        internal readonly ushort CurrentGroupBit;
        internal readonly sbyte Priority;
        internal readonly LaneSignalType Signal;
        internal readonly uint UpdateFrame;

        internal SignalLaneFact(
            Entity lane,
            Entity intersection,
            LaneSignal signal,
            ushort currentGroupBit,
            uint updateFrame)
        {
            Lane = lane;
            Intersection = intersection;
            Value = signal;
            GroupMask = signal.m_GroupMask;
            CurrentGroupBit = currentGroupBit;
            Priority = signal.m_Priority;
            Signal = signal.m_Signal;
            UpdateFrame = updateFrame;
        }
    }

    internal sealed class SignalLanePort
    {
        private readonly EntityManager m_Entities;

        internal SignalLanePort(EntityManager entities)
        {
            m_Entities = entities;
        }

        internal bool TryRead(Entity lane, out SignalLaneFact fact)
        {
            fact = default;
            if (lane == Entity.Null
                || !m_Entities.Exists(lane)
                || !m_Entities.HasComponent<LaneSignal>(lane)
                || !m_Entities.HasComponent<Owner>(lane))
            {
                return false;
            }
            Entity intersection = m_Entities.GetComponentData<Owner>(lane).m_Owner;
            if (intersection == Entity.Null
                || !m_Entities.Exists(intersection)
                || !m_Entities.HasComponent<TrafficLights>(intersection)
                || !m_Entities.HasComponent<UpdateFrame>(intersection))
            {
                return false;
            }
            TrafficLights lights = m_Entities.GetComponentData<TrafficLights>(intersection);
            if ((lights.m_Flags & (TrafficLightFlags.LevelCrossing | TrafficLightFlags.MoveableBridge)) != 0)
                return false;
            LaneSignal signal = m_Entities.GetComponentData<LaneSignal>(lane);
            if (signal.m_GroupMask == 0)
                return false;
            uint updateFrame = m_Entities.GetSharedComponent<UpdateFrame>(intersection).m_Index;
            if (updateFrame >= 16u)
                return false;
            ushort currentGroupBit = lights.m_CurrentSignalGroup >= 1
                && lights.m_CurrentSignalGroup <= 16
                ? (ushort)(1 << (lights.m_CurrentSignalGroup - 1))
                : (ushort)0;
            fact = new SignalLaneFact(
                lane,
                intersection,
                signal,
                currentGroupBit,
                updateFrame);
            return true;
        }

        internal bool TryIntersection(Entity lane, out Entity intersection)
        {
            intersection = Entity.Null;
            if (lane == Entity.Null
                || !m_Entities.Exists(lane)
                || !m_Entities.HasComponent<Owner>(lane))
            {
                return false;
            }
            intersection = m_Entities.GetComponentData<Owner>(lane).m_Owner;
            return intersection != Entity.Null
                && m_Entities.Exists(intersection)
                && m_Entities.HasComponent<TrafficLights>(intersection);
        }

        internal bool TrySubmit(
            SignalLaneFact before,
            sbyte priority)
        {
            if (priority <= before.Priority)
                return false;
            LaneSignal signal = before.Value;
            // 提交者必须使用信号车道；若写入真实公交或电车实体，原版 blocker 链可能形成循环并误删车辆。
            signal.m_Petitioner = before.Lane;
            signal.m_Priority = priority;
            m_Entities.SetComponentData(before.Lane, signal);
            return true;
        }

        internal bool HasBusTargetSignalBlocker(
            Entity vehicle,
            SignalLaneFact target)
        {
            if (!TryReadControllerBlocker(
                    vehicle,
                    out Entity controller,
                    out Blocker blocker)
                || blocker.m_Type != BlockerType.Signal
                || blocker.m_MaxSpeed >= 6
                || !m_Entities.HasBuffer<CarNavigationLane>(controller))
            {
                return false;
            }
            DynamicBuffer<CarNavigationLane> lanes = m_Entities.GetBuffer<
                CarNavigationLane>(controller, true);
            return lanes.Length > 0
                && lanes[0].m_Lane == target.Lane
                && target.Signal != LaneSignalType.Go;
        }

        private bool TryReadControllerBlocker(
            Entity entity,
            out Entity controller,
            out Blocker blocker)
        {
            controller = Entity.Null;
            blocker = default;
            if (entity == Entity.Null || !m_Entities.Exists(entity))
                return false;
            if (m_Entities.HasComponent<Controller>(entity))
            {
                entity = m_Entities.GetComponentData<Controller>(entity)
                    .m_Controller;
            }
            if (entity == Entity.Null
                || !m_Entities.Exists(entity)
                || !m_Entities.HasComponent<Blocker>(entity))
            {
                return false;
            }
            controller = entity;
            blocker = m_Entities.GetComponentData<Blocker>(entity);
            return true;
        }

    }
}
