using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Signals
{
#if RT_BUS_SIGNAL_WAIT_MEASURE
    internal enum BusSignalBlockerKind : byte
    {
        None,
        DirectSignal,
        QueuedSignal,
    }

    internal readonly struct BusSignalBlocker
    {
        internal readonly bool Available;
        internal readonly BusSignalBlockerKind Kind;
        internal readonly Entity TerminalVehicle;
        internal readonly Entity SignalPetitioner;
        internal readonly byte ChainDepth;

        internal BusSignalBlocker(
            BusSignalBlockerKind kind,
            Entity terminalVehicle,
            Entity signalPetitioner,
            byte chainDepth)
        {
            Available = true;
            Kind = kind;
            TerminalVehicle = terminalVehicle;
            SignalPetitioner = signalPetitioner;
            ChainDepth = chainDepth;
        }
    }
#endif

#if RT_SIGNAL_WAIT_MEASURE || RT_BUS_SIGNAL_WAIT_MEASURE
    internal enum SignalTraceEntityKind : byte
    {
        Null,
        Human,
        Car,
        Train,
        Other,
    }

    internal struct SignalTraceSnapshot
    {
        internal Entity Intersection;
        internal Entity TargetPetitioner;
        internal Entity TargetBlocker;
        internal Entity HighestPetitioner;
        internal ushort TargetGroupMask;
        internal ushort HighestGroupMask;
        internal sbyte TargetPriority;
        internal sbyte TargetDefault;
        internal sbyte HighestPriority;
        internal byte State;
        internal byte Timer;
        internal byte CurrentSignalGroup;
        internal byte NextSignalGroup;
        internal byte SignalGroupCount;
        internal byte TargetSignal;
        internal SignalTraceEntityKind TargetPetitionerKind;
        internal SignalTraceEntityKind TargetBlockerKind;
        internal SignalTraceEntityKind HighestPetitionerKind;
    }

#if RT_SIGNAL_WAIT_MEASURE
    internal struct SignalTraceBlocker
    {
        internal Entity Entity;
        internal BlockerType Type;
        internal byte MaxSpeed;
        internal SignalTraceEntityKind Kind;
    }
#endif
#endif

    internal readonly struct SignalLaneFact
    {
        internal readonly Entity Lane;
        internal readonly Entity Intersection;
        internal readonly ushort GroupMask;
        internal readonly ushort CurrentGroupBit;
        internal readonly sbyte Priority;
        internal readonly Entity Petitioner;
        internal readonly Entity Blocker;
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
            GroupMask = signal.m_GroupMask;
            CurrentGroupBit = currentGroupBit;
            Priority = signal.m_Priority;
            Petitioner = signal.m_Petitioner;
            Blocker = signal.m_Blocker;
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

        internal bool TrySubmit(Entity lane, Entity vehicle, sbyte priority, out SignalLaneFact before)
        {
            if (!TryRead(lane, out before) || priority <= before.Priority)
                return false;
            LaneSignal signal = m_Entities.GetComponentData<LaneSignal>(lane);
            signal.m_Petitioner = lane;
            signal.m_Priority = priority;
            m_Entities.SetComponentData(lane, signal);
            return true;
        }

        internal bool HasBusTargetSignalBlocker(
            Entity vehicle,
            Entity targetLane,
            Entity targetIntersection,
            ushort targetGroup)
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
                && lanes[0].m_Lane == targetLane
                && TryRead(targetLane, out SignalLaneFact signal)
                && signal.Intersection == targetIntersection
                && signal.GroupMask == targetGroup
                && signal.Signal != LaneSignalType.Go;
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

#if RT_BUS_SIGNAL_WAIT_MEASURE
        internal BusSignalBlocker ReadBusBlocker(Entity vehicle)
        {
            if (!TryReadControllerBlocker(vehicle, out Entity terminalVehicle, out Blocker blocker))
                return default;
            if (blocker.m_MaxSpeed >= 6)
                return new BusSignalBlocker(BusSignalBlockerKind.None, terminalVehicle, blocker.m_Blocker, 0);
            if (blocker.m_Type == BlockerType.Signal)
                return new BusSignalBlocker(BusSignalBlockerKind.DirectSignal, terminalVehicle, blocker.m_Blocker, 0);
            if (blocker.m_Type != BlockerType.Continuing
                || blocker.m_Blocker == Entity.Null)
            {
                return new BusSignalBlocker(BusSignalBlockerKind.None, terminalVehicle, blocker.m_Blocker, 0);
            }

            Entity next = blocker.m_Blocker;
            for (byte depth = 1; depth < 100; depth++)
            {
                if (!TryReadControllerBlocker(next, out terminalVehicle, out blocker))
                    return default;
                if (blocker.m_MaxSpeed >= 6)
                    return new BusSignalBlocker(BusSignalBlockerKind.None, terminalVehicle, blocker.m_Blocker, depth);
                if (blocker.m_Type == BlockerType.Signal)
                    return new BusSignalBlocker(BusSignalBlockerKind.QueuedSignal, terminalVehicle, blocker.m_Blocker, depth);
                if (blocker.m_Type != BlockerType.Continuing
                    || blocker.m_Blocker == Entity.Null)
                {
                    return new BusSignalBlocker(BusSignalBlockerKind.None, terminalVehicle, blocker.m_Blocker, depth);
                }
                next = blocker.m_Blocker;
            }

            return default;
        }

        internal bool TryReadBusTerminalSignal(Entity terminalVehicle, out SignalLaneFact fact)
        {
            fact = default;
            if (terminalVehicle == Entity.Null || !m_Entities.Exists(terminalVehicle)
                || !m_Entities.HasBuffer<CarNavigationLane>(terminalVehicle))
            {
                return false;
            }
            DynamicBuffer<CarNavigationLane> navigation = m_Entities.GetBuffer<CarNavigationLane>(terminalVehicle, true);
            return navigation.Length > 0 && TryRead(navigation[0].m_Lane, out fact);
        }

#endif

#if RT_SIGNAL_WAIT_MEASURE || RT_BUS_SIGNAL_WAIT_MEASURE
        internal bool TryReadTrace(Entity lane, out SignalTraceSnapshot snapshot)
        {
            snapshot = default;
            if (!TryRead(lane, out SignalLaneFact fact)
                || !m_Entities.HasBuffer<SubLane>(fact.Intersection))
            {
                return false;
            }

            TrafficLights lights = m_Entities.GetComponentData<TrafficLights>(fact.Intersection);
            LaneSignal target = m_Entities.GetComponentData<LaneSignal>(lane);
            DynamicBuffer<SubLane> subLanes = m_Entities.GetBuffer<SubLane>(fact.Intersection, true);
            sbyte highestPriority = 0;
            ushort highestGroupMask = 0;
            Entity highestPetitioner = Entity.Null;
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity subLane = subLanes[i].m_SubLane;
                if (!m_Entities.HasComponent<LaneSignal>(subLane))
                {
                    continue;
                }

                LaneSignal signal = m_Entities.GetComponentData<LaneSignal>(subLane);
                if (signal.m_Priority > highestPriority)
                {
                    highestPriority = signal.m_Priority;
                    highestGroupMask = signal.m_GroupMask;
                    highestPetitioner = signal.m_Petitioner;
                }
                else if (signal.m_Priority == highestPriority)
                {
                    highestGroupMask |= signal.m_GroupMask;
                }
            }

            snapshot.Intersection = fact.Intersection;
            snapshot.State = (byte)lights.m_State;
            snapshot.Timer = lights.m_Timer;
            snapshot.CurrentSignalGroup = lights.m_CurrentSignalGroup;
            snapshot.NextSignalGroup = lights.m_NextSignalGroup;
            snapshot.SignalGroupCount = lights.m_SignalGroupCount;
            snapshot.TargetGroupMask = target.m_GroupMask;
            snapshot.TargetSignal = (byte)target.m_Signal;
            snapshot.TargetPriority = target.m_Priority;
            snapshot.TargetDefault = target.m_Default;
            snapshot.TargetPetitioner = target.m_Petitioner;
            snapshot.TargetPetitionerKind = Classify(target.m_Petitioner);
            snapshot.TargetBlocker = target.m_Blocker;
            snapshot.TargetBlockerKind = Classify(target.m_Blocker);
            snapshot.HighestPriority = highestPriority;
            snapshot.HighestGroupMask = highestGroupMask;
            snapshot.HighestPetitioner = highestPetitioner;
            snapshot.HighestPetitionerKind = Classify(highestPetitioner);
            return true;
        }

#if RT_SIGNAL_WAIT_MEASURE
        internal bool TryReadTraceBlocker(Entity vehicle, out SignalTraceBlocker blocker)
        {
            blocker = default;
            if (vehicle == Entity.Null
                || !m_Entities.Exists(vehicle)
                || !m_Entities.HasComponent<Blocker>(vehicle))
            {
                return false;
            }

            Blocker source = m_Entities.GetComponentData<Blocker>(vehicle);
            blocker.Entity = source.m_Blocker;
            blocker.Type = source.m_Type;
            blocker.MaxSpeed = source.m_MaxSpeed;
            blocker.Kind = Classify(source.m_Blocker);
            return true;
        }
#endif

        private SignalTraceEntityKind Classify(Entity entity)
        {
            if (entity == Entity.Null)
            {
                return SignalTraceEntityKind.Null;
            }

            if (!m_Entities.Exists(entity))
            {
                return SignalTraceEntityKind.Other;
            }

            if (m_Entities.HasComponent<Game.Creatures.Human>(entity))
            {
                return SignalTraceEntityKind.Human;
            }

            if (m_Entities.HasComponent<Car>(entity))
            {
                return SignalTraceEntityKind.Car;
            }

            if (m_Entities.HasComponent<Train>(entity))
            {
                return SignalTraceEntityKind.Train;
            }

            return SignalTraceEntityKind.Other;
        }
#endif
    }
}
