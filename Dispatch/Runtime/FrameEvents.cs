using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum VehicleFactKind : byte { Registered, Rebound, Removed, Boarding, Route, Target, Waypoint, Moving, PathReady, OriginRange }
    internal enum DispatchFactKind : byte { State, Target, Slot, Removed, LaunchConfirmed, UnplannedRun, RetireRequested, PathFault, RunningRecovery }
    internal enum FrameEventKind : byte { Vehicle, Stop, Bypass, Dispatch }

    internal readonly struct VehicleEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong SourceGeneration;
        public readonly ulong Sequence;
        public readonly VehicleFactKind Kind;
        public readonly Entity PreviousLine;
        public readonly Entity CurrentLine;
        public readonly VehicleState State;
        public readonly bool PreviousBoarding;
        public readonly bool CurrentBoarding;
        public readonly bool PreviousMoving;
        public readonly bool CurrentMoving;
        public readonly int PreviousWaypointIndex;
        public readonly int CurrentWaypointIndex;
        public readonly int RecoveryWaypointIndex;
        public readonly int WaypointCount;
        public readonly bool AtOrigin;
        public readonly bool NearOrigin;
        public readonly bool PathReady;

        public Entity Line => CurrentLine;

        public VehicleEvent(Entity vehicle, uint sourceFrame, ulong sequence, VehicleFactKind kind,
            Entity previousLine = default, Entity line = default, VehicleState state = default,
            bool previousBoarding = false, bool currentBoarding = false,
            bool previousMoving = false, bool currentMoving = false,
            int previousWaypointIndex = -1, int currentWaypointIndex = -1,
            int recoveryWaypointIndex = -1, int waypointCount = 0,
            bool atOrigin = false, bool nearOrigin = false, bool pathReady = false,
            ulong sourceGeneration = 0UL)
        {
            Vehicle = vehicle;
            SourceFrame = sourceFrame;
            SourceGeneration = sourceGeneration;
            Sequence = sequence;
            Kind = kind;
            PreviousLine = previousLine;
            CurrentLine = line;
            State = state;
            PreviousBoarding = previousBoarding;
            CurrentBoarding = currentBoarding;
            PreviousMoving = previousMoving;
            CurrentMoving = currentMoving;
            PreviousWaypointIndex = previousWaypointIndex;
            CurrentWaypointIndex = currentWaypointIndex;
            RecoveryWaypointIndex = recoveryWaypointIndex;
            WaypointCount = waypointCount;
            AtOrigin = atOrigin;
            NearOrigin = nearOrigin;
            PathReady = pathReady;
        }
    }

    internal readonly struct DispatchBusinessFact
    {
        public readonly bool Exists;
        public readonly int TargetMinute;
        public readonly int SlotMinute;
        public readonly int ActualMinute;
        public readonly bool Late;
        public readonly string Reason;

        public DispatchBusinessFact(
            int targetMinute,
            int slotMinute,
            int actualMinute,
            bool late,
            string reason,
            ulong sourceGeneration = 0UL)
        {
            Exists = true;
            TargetMinute = targetMinute;
            SlotMinute = slotMinute;
            ActualMinute = actualMinute;
            Late = late;
            Reason = reason;
        }
    }

    internal readonly struct DispatchEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong SourceGeneration;
        public readonly ulong Sequence;
        public readonly DispatchFactKind Kind;
        public readonly VehicleState PreviousState;
        public readonly VehicleState CurrentState;
        public readonly Entity Line;
        public readonly int PreviousValue;
        public readonly int CurrentValue;
        public readonly DispatchBusinessFact Fact;

        public DispatchEvent(Entity vehicle, uint sourceFrame, ulong sequence, DispatchFactKind kind,
            VehicleState previousState, VehicleState currentState, Entity line = default, int previousValue = -1, int currentValue = -1,
            DispatchBusinessFact fact = default, ulong sourceGeneration = 0UL)
        {
            Vehicle = vehicle;
            SourceFrame = sourceFrame;
            SourceGeneration = sourceGeneration;
            Sequence = sequence;
            Kind = kind;
            PreviousState = previousState;
            CurrentState = currentState;
            Line = line;
            PreviousValue = previousValue;
            CurrentValue = currentValue;
            Fact = fact;
        }
    }

    internal readonly struct StopEvent
    {
        public readonly StopFact Fact;
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong SourceGeneration;
        public readonly ulong Sequence;
        public StopEvent(StopFact fact, uint sourceFrame, ulong sequence, ulong sourceGeneration)
        {
            Fact = fact;
            Vehicle = fact.Vehicle;
            SourceFrame = sourceFrame;
            SourceGeneration = sourceGeneration;
            Sequence = sequence;
        }
    }

    internal enum BypassFactKind : byte { Held, BypassHoldCadence, Released, Cleared, Expired, Rescued }

    internal readonly struct BypassFact
    {
        public readonly BypassFactKind Kind;
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly Entity Blocker;
        public readonly int WaypointIndex;
        public readonly bool ShouldHold;
        public readonly bool CanClearAfterExit;
        public readonly string Reason;
        public readonly ulong SourceGeneration;

        public BypassFact(
            BypassFactKind kind,
            Entity vehicle,
            Entity line,
            Entity blocker,
            int waypointIndex,
            bool shouldHold,
            bool canClearAfterExit,
            string reason = null,
            ulong sourceGeneration = 0UL)
        {
            Kind = kind;
            Vehicle = vehicle;
            Line = line;
            Blocker = blocker;
            WaypointIndex = waypointIndex;
            ShouldHold = shouldHold;
            CanClearAfterExit = canClearAfterExit;
            Reason = reason;
            SourceGeneration = sourceGeneration;
        }
    }

    internal readonly struct BypassEvent
    {
        public readonly BypassFact Fact;
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong SourceGeneration;
        public readonly ulong Sequence;
        public BypassEvent(BypassFact fact, uint sourceFrame, ulong sequence, ulong sourceGeneration)
        {
            Fact = fact;
            Vehicle = fact.Vehicle;
            SourceFrame = sourceFrame;
            SourceGeneration = sourceGeneration;
            Sequence = sequence;
        }
    }

    internal readonly struct FrameEventRef
    {
        public readonly FrameEventKind Kind;
        public readonly ulong Sequence;
        public readonly int Index;
        public FrameEventRef(FrameEventKind kind, ulong sequence, int index) { Kind = kind; Sequence = sequence; Index = index; }
    }

    internal sealed class FrameEvents : IDisposable
    {
        // 本帧数据：BeginFrame 清四类批次并从零开始本帧取号；不触及任何 owner。
        private readonly List<VehicleEvent> m_VehicleEvents = new List<VehicleEvent>();
        private readonly List<DispatchEvent> m_DispatchEvents = new List<DispatchEvent>();
        private readonly List<StopEvent> m_StopEvents = new List<StopEvent>();
        private readonly List<BypassEvent> m_BypassEvents = new List<BypassEvent>();
        private ulong m_NextSequence;

        public IReadOnlyList<VehicleEvent> VehicleEvents => m_VehicleEvents;
        public IReadOnlyList<DispatchEvent> DispatchEvents => m_DispatchEvents;
        public IReadOnlyList<StopEvent> StopEvents => m_StopEvents;
        public IReadOnlyList<BypassEvent> BypassEvents => m_BypassEvents;

        public void BeginFrame()
        {
            m_VehicleEvents.Clear();
            m_DispatchEvents.Clear();
            m_StopEvents.Clear();
            m_BypassEvents.Clear();
            m_NextSequence = 0;
        }

        public void ResetCity()
        {
            // 读档/整体清理：除当前批次外无跨城事件状态可保留。
            BeginFrame();
        }

        public ulong AppendVehicle(Entity vehicle, uint sourceFrame, VehicleFactKind kind,
            Entity previousLine = default, Entity line = default, VehicleState state = default,
            bool previousBoarding = false, bool currentBoarding = false,
            bool previousMoving = false, bool currentMoving = false,
            int previousWaypointIndex = -1, int currentWaypointIndex = -1,
            int recoveryWaypointIndex = -1, int waypointCount = 0,
            bool atOrigin = false, bool nearOrigin = false, bool pathReady = false,
            ulong sourceGeneration = 0UL)
        {
            ulong sequence = NextSequence();
            m_VehicleEvents.Add(new VehicleEvent(
                vehicle,
                sourceFrame,
                sequence,
                kind,
                previousLine,
                line,
                state,
                previousBoarding,
                currentBoarding,
                previousMoving,
                currentMoving,
                previousWaypointIndex,
                currentWaypointIndex,
                recoveryWaypointIndex,
                waypointCount,
                atOrigin,
                nearOrigin,
                pathReady,
                sourceGeneration));
            return sequence;
        }

        public void AppendDispatch(Entity vehicle, uint sourceFrame, DispatchFactKind kind, VehicleState previousState, VehicleState currentState,
            Entity line = default, int previousValue = -1, int currentValue = -1,
            DispatchBusinessFact fact = default, ulong sourceGeneration = 0UL)
        {
            m_DispatchEvents.Add(new DispatchEvent(vehicle, sourceFrame, NextSequence(), kind, previousState, currentState, line, previousValue, currentValue, fact, sourceGeneration));
        }

        public void AppendLaunchConfirmed(
            Entity vehicle,
            uint sourceFrame,
            Entity line,
            int targetMinute,
            int slotMinute,
            int actualMinute,
            bool late,
            string reason,
            ulong sourceGeneration = 0UL)
        {
            AppendDispatch(
                vehicle,
                sourceFrame,
                DispatchFactKind.LaunchConfirmed,
                default,
                default,
                line,
                fact: new DispatchBusinessFact(targetMinute, slotMinute, actualMinute, late, reason),
                sourceGeneration: sourceGeneration);
        }

        public void AppendUnplannedRun(Entity vehicle, uint sourceFrame, Entity line, string reason, ulong sourceGeneration = 0UL)
        {
            AppendDispatch(
                vehicle,
                sourceFrame,
                DispatchFactKind.UnplannedRun,
                default,
                default,
                line,
                fact: new DispatchBusinessFact(-1, -1, -1, false, reason),
                sourceGeneration: sourceGeneration);
        }

        public void AppendRetireRequested(Entity vehicle, uint sourceFrame, Entity line, string reason, ulong sourceGeneration = 0UL)
        {
            AppendDispatch(
                vehicle,
                sourceFrame,
                DispatchFactKind.RetireRequested,
                default,
                default,
                line,
                fact: new DispatchBusinessFact(-1, -1, -1, false, reason),
                sourceGeneration: sourceGeneration);
        }

        public void AppendStop(StopFact fact, uint sourceFrame, ulong sourceGeneration = 0UL)
            => m_StopEvents.Add(new StopEvent(fact, sourceFrame, NextSequence(), sourceGeneration != 0UL ? sourceGeneration : fact.SourceGeneration));
        public void AppendBypass(BypassFact fact, uint sourceFrame, ulong sourceGeneration = 0UL)
            => m_BypassEvents.Add(new BypassEvent(fact, sourceFrame, NextSequence(), sourceGeneration != 0UL ? sourceGeneration : fact.SourceGeneration));

        // 第四步消费者使用此入口；第二步仅作只读结构验收。
        public IReadOnlyList<FrameEventRef> MergeBySequence()
        {
            List<FrameEventRef> merged = new List<FrameEventRef>(m_VehicleEvents.Count + m_DispatchEvents.Count + m_StopEvents.Count + m_BypassEvents.Count);
            for (int i = 0; i < m_VehicleEvents.Count; i++) merged.Add(new FrameEventRef(FrameEventKind.Vehicle, m_VehicleEvents[i].Sequence, i));
            for (int i = 0; i < m_StopEvents.Count; i++) merged.Add(new FrameEventRef(FrameEventKind.Stop, m_StopEvents[i].Sequence, i));
            for (int i = 0; i < m_BypassEvents.Count; i++) merged.Add(new FrameEventRef(FrameEventKind.Bypass, m_BypassEvents[i].Sequence, i));
            for (int i = 0; i < m_DispatchEvents.Count; i++) merged.Add(new FrameEventRef(FrameEventKind.Dispatch, m_DispatchEvents[i].Sequence, i));
            merged.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            return merged;
        }

        public void Dispose() => ResetCity();
        private ulong NextSequence() => ++m_NextSequence;
    }
}
