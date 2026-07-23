using System;
using System.Collections.Generic;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum VehicleFactKind : byte { Registered, Rebound, Removed, Boarding, Route, PublicTransport, Target, Path, Lane, Motion }
    internal enum DispatchFactKind : byte { State, Target, Slot, Removed }
    internal enum FrameEventKind : byte { Vehicle, Stop, Bypass, Dispatch }

    internal readonly struct VehicleEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong Sequence;
        public readonly VehicleFactKind Kind;
        public readonly PublicTransportFlags PublicTransportState;
        public readonly Entity Route;
        public readonly RailSnapshot PreviousRail;
        public readonly RailSnapshot CurrentRail;

        public VehicleEvent(Entity vehicle, uint sourceFrame, ulong sequence, VehicleFactKind kind,
            PublicTransportFlags publicTransportState = default, Entity route = default,
            RailSnapshot previousRail = default, RailSnapshot currentRail = default)
        {
            Vehicle = vehicle;
            SourceFrame = sourceFrame;
            Sequence = sequence;
            Kind = kind;
            PublicTransportState = publicTransportState;
            Route = route;
            PreviousRail = previousRail;
            CurrentRail = currentRail;
        }
    }

    internal readonly struct DispatchEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong Sequence;
        public readonly DispatchFactKind Kind;
        public readonly VehicleState PreviousState;
        public readonly VehicleState CurrentState;
        public readonly Entity Line;
        public readonly int PreviousValue;
        public readonly int CurrentValue;

        public DispatchEvent(Entity vehicle, uint sourceFrame, ulong sequence, DispatchFactKind kind,
            VehicleState previousState, VehicleState currentState, Entity line = default, int previousValue = -1, int currentValue = -1)
        {
            Vehicle = vehicle;
            SourceFrame = sourceFrame;
            Sequence = sequence;
            Kind = kind;
            PreviousState = previousState;
            CurrentState = currentState;
            Line = line;
            PreviousValue = previousValue;
            CurrentValue = currentValue;
        }
    }

    internal readonly struct StopEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong Sequence;
        public StopEvent(Entity vehicle, uint sourceFrame, ulong sequence) { Vehicle = vehicle; SourceFrame = sourceFrame; Sequence = sequence; }
    }

    internal readonly struct BypassEvent
    {
        public readonly Entity Vehicle;
        public readonly uint SourceFrame;
        public readonly ulong Sequence;
        public BypassEvent(Entity vehicle, uint sourceFrame, ulong sequence) { Vehicle = vehicle; SourceFrame = sourceFrame; Sequence = sequence; }
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

        public void AppendVehicle(Entity vehicle, uint sourceFrame, VehicleFactKind kind,
            PublicTransportFlags publicTransportState = default, Entity route = default,
            RailSnapshot previousRail = default, RailSnapshot currentRail = default)
        {
            m_VehicleEvents.Add(new VehicleEvent(vehicle, sourceFrame, NextSequence(), kind, publicTransportState, route, previousRail, currentRail));
        }

        public void AppendDispatch(Entity vehicle, uint sourceFrame, DispatchFactKind kind, VehicleState previousState, VehicleState currentState,
            Entity line = default, int previousValue = -1, int currentValue = -1)
        {
            m_DispatchEvents.Add(new DispatchEvent(vehicle, sourceFrame, NextSequence(), kind, previousState, currentState, line, previousValue, currentValue));
        }

        public void AppendStop(Entity vehicle, uint sourceFrame) => m_StopEvents.Add(new StopEvent(vehicle, sourceFrame, NextSequence()));
        public void AppendBypass(Entity vehicle, uint sourceFrame) => m_BypassEvents.Add(new BypassEvent(vehicle, sourceFrame, NextSequence()));

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
