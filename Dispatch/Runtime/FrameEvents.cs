using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum LifecycleFactKind : byte
    {
        Registered,
        Rebound,
        Removed
    }

    internal enum DispatchFactKind : byte
    {
        State,
        Target,
        Slot,
        LaunchConfirmed,
        UnplannedRun,
        RetireRequested,
        PathFault,
        RunningRecovery
    }

    internal enum FrameEventKind : byte
    {
        Lifecycle,
        Stop,
        Bypass,
        Dispatch
    }

    internal readonly struct LifecycleEvent
    {
        public readonly Entity Vehicle;
        public readonly uint Frame;
        public readonly ulong Sequence;
        public readonly LifecycleFactKind Kind;
        public readonly Entity PreviousLine;
        public readonly Entity Line;
        public readonly VehicleState State;

        public LifecycleEvent(
            Entity vehicle,
            uint frame,
            ulong sequence,
            LifecycleFactKind kind,
            Entity previousLine,
            Entity line,
            VehicleState state)
        {
            Vehicle = vehicle;
            Frame = frame;
            Sequence = sequence;
            Kind = kind;
            PreviousLine = previousLine;
            Line = line;
            State = state;
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

        public DispatchBusinessFact(int targetMinute, int slotMinute, int actualMinute, bool late, string reason)
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
        public readonly uint Frame;
        public readonly ulong Sequence;
        public readonly DispatchFactKind Kind;
        public readonly VehicleState PreviousState;
        public readonly VehicleState CurrentState;
        public readonly Entity Line;
        public readonly int PreviousValue;
        public readonly int CurrentValue;
        public readonly DispatchBusinessFact Fact;

        public DispatchEvent(
            Entity vehicle,
            uint frame,
            ulong sequence,
            DispatchFactKind kind,
            VehicleState previousState,
            VehicleState currentState,
            Entity line,
            int previousValue,
            int currentValue,
            DispatchBusinessFact fact)
        {
            Vehicle = vehicle;
            Frame = frame;
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
        public readonly uint Frame;
        public readonly ulong Sequence;

        public StopEvent(StopFact fact, uint frame, ulong sequence)
        {
            Fact = fact;
            Vehicle = fact.Vehicle;
            Frame = frame;
            Sequence = sequence;
        }
    }

    internal enum BypassFactKind : byte
    {
        Held,
        BypassHoldCadence,
        Released,
        Cleared,
        Expired,
        Rescued
    }

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

        public BypassFact(
            BypassFactKind kind,
            Entity vehicle,
            Entity line,
            Entity blocker,
            int waypointIndex,
            bool shouldHold,
            bool canClearAfterExit,
            string reason = null)
        {
            Kind = kind;
            Vehicle = vehicle;
            Line = line;
            Blocker = blocker;
            WaypointIndex = waypointIndex;
            ShouldHold = shouldHold;
            CanClearAfterExit = canClearAfterExit;
            Reason = reason;
        }
    }

    internal readonly struct BypassEvent
    {
        public readonly BypassFact Fact;
        public readonly Entity Vehicle;
        public readonly uint Frame;
        public readonly ulong Sequence;

        public BypassEvent(BypassFact fact, uint frame, ulong sequence)
        {
            Fact = fact;
            Vehicle = fact.Vehicle;
            Frame = frame;
            Sequence = sequence;
        }
    }

    internal readonly struct FrameEventRef
    {
        public readonly FrameEventKind Kind;
        public readonly ulong Sequence;
        public readonly int Index;

        public FrameEventRef(FrameEventKind kind, ulong sequence, int index)
        {
            Kind = kind;
            Sequence = sequence;
            Index = index;
        }
    }

    internal sealed class FrameEvents : IDisposable
    {
        private readonly List<LifecycleEvent> m_LifecycleEvents = new List<LifecycleEvent>();
        private readonly List<DispatchEvent> m_DispatchEvents = new List<DispatchEvent>();
        private readonly List<StopEvent> m_StopEvents = new List<StopEvent>();
        private readonly List<BypassEvent> m_BypassEvents = new List<BypassEvent>();
        private readonly List<FrameEventRef> m_Merged = new List<FrameEventRef>();
        private Action m_FactCounter;
        private ulong m_NextSequence;

        public IReadOnlyList<LifecycleEvent> LifecycleEvents => m_LifecycleEvents;
        public IReadOnlyList<DispatchEvent> DispatchEvents => m_DispatchEvents;
        public IReadOnlyList<StopEvent> StopEvents => m_StopEvents;
        public IReadOnlyList<BypassEvent> BypassEvents => m_BypassEvents;

        public void SetFactCounter(Action factCounter) => m_FactCounter = factCounter;

        public void BeginFrame()
        {
            m_LifecycleEvents.Clear();
            m_DispatchEvents.Clear();
            m_StopEvents.Clear();
            m_BypassEvents.Clear();
            m_Merged.Clear();
            m_NextSequence = 0;
        }

        public void ResetCity() => BeginFrame();

        public void AppendLifecycle(
            Entity vehicle,
            uint frame,
            LifecycleFactKind kind,
            Entity previousLine = default,
            Entity line = default,
            VehicleState state = default)
        {
            m_LifecycleEvents.Add(new LifecycleEvent(vehicle, frame, NextSequence(), kind, previousLine, line, state));
            CountFact();
        }

        public void AppendDispatch(
            Entity vehicle,
            uint frame,
            DispatchFactKind kind,
            VehicleState previousState,
            VehicleState currentState,
            Entity line = default,
            int previousValue = -1,
            int currentValue = -1,
            DispatchBusinessFact fact = default)
        {
            m_DispatchEvents.Add(new DispatchEvent(vehicle, frame, NextSequence(), kind, previousState, currentState, line, previousValue, currentValue, fact));
            CountFact();
        }

        public void AppendLaunchConfirmed(Entity vehicle, uint frame, Entity line, int targetMinute, int slotMinute, int actualMinute, bool late, string reason)
        {
            AppendDispatch(vehicle, frame, DispatchFactKind.LaunchConfirmed, default, default, line,
                fact: new DispatchBusinessFact(targetMinute, slotMinute, actualMinute, late, reason));
        }

        public void AppendUnplannedRun(Entity vehicle, uint frame, Entity line, string reason)
        {
            AppendDispatch(vehicle, frame, DispatchFactKind.UnplannedRun, default, default, line,
                fact: new DispatchBusinessFact(-1, -1, -1, false, reason));
        }

        public void AppendRetireRequested(Entity vehicle, uint frame, Entity line, string reason)
        {
            AppendDispatch(vehicle, frame, DispatchFactKind.RetireRequested, default, default, line,
                fact: new DispatchBusinessFact(-1, -1, -1, false, reason));
        }

        public void AppendStop(StopFact fact, uint frame)
        {
            m_StopEvents.Add(new StopEvent(fact, frame, NextSequence()));
            CountFact();
        }

        public void AppendBypass(BypassFact fact, uint frame)
        {
            m_BypassEvents.Add(new BypassEvent(fact, frame, NextSequence()));
            CountFact();
        }

        public IReadOnlyList<FrameEventRef> MergeBySequence()
        {
            m_Merged.Clear();
            for (int i = 0; i < m_LifecycleEvents.Count; i++) m_Merged.Add(new FrameEventRef(FrameEventKind.Lifecycle, m_LifecycleEvents[i].Sequence, i));
            for (int i = 0; i < m_StopEvents.Count; i++) m_Merged.Add(new FrameEventRef(FrameEventKind.Stop, m_StopEvents[i].Sequence, i));
            for (int i = 0; i < m_BypassEvents.Count; i++) m_Merged.Add(new FrameEventRef(FrameEventKind.Bypass, m_BypassEvents[i].Sequence, i));
            for (int i = 0; i < m_DispatchEvents.Count; i++) m_Merged.Add(new FrameEventRef(FrameEventKind.Dispatch, m_DispatchEvents[i].Sequence, i));
            m_Merged.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            return m_Merged;
        }

        public void Dispose()
        {
            ResetCity();
            m_FactCounter = null;
        }

        private void CountFact() => m_FactCounter?.Invoke();
        private ulong NextSequence() => ++m_NextSequence;
    }
}
