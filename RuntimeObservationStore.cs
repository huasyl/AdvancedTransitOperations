using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeObservationSession
    {
        public string SnapshotId = string.Empty;
        public string Status = "empty";
        public uint AppliedAtFrame;
        public uint LastUpdatedFrame;
        public DateTime AppliedGameDate = DateTime.MinValue;
        public readonly Dictionary<string, RuntimeObservedTrip> TripsById =
            new Dictionary<string, RuntimeObservedTrip>(StringComparer.Ordinal);
        public readonly List<RuntimeObservedStopEvent> StopEvents = new List<RuntimeObservedStopEvent>();
        public readonly List<RuntimeObservedBypassEvent> BypassEvents = new List<RuntimeObservedBypassEvent>();
        public readonly List<DispatchRuntimeSystem.RuntimeObservedCorridorPassageDto> CorridorPassages =
            new List<DispatchRuntimeSystem.RuntimeObservedCorridorPassageDto>();
    }

    internal sealed class RuntimeObservedTrip
    {
        public string TripObservationId = string.Empty;
        public string BaseObservationKey = string.Empty;
        public string State = "pending";
        public string LineId = string.Empty;
        public string RowId = string.Empty;
        public string Source = string.Empty;
        public string ServiceKind = string.Empty;
        public string ServiceDate = string.Empty;
        public int ServiceDayIndex = -1;
        public int OccurrenceIndex = 1;
        public int TargetMinute = -1;
        public int ActualDepartureMinute = -1;
        public Entity Line = Entity.Null;
        public Entity Vehicle = Entity.Null;
        public uint LaunchFrame;
        public string BindingConfidence = "seeded";
        public string ReasonCode = string.Empty;
        public uint LastUpdatedFrame;
    }

    internal sealed class RuntimeObservedStopEvent
    {
        public string EventId = string.Empty;
        public string EventType = string.Empty;
        public string TripObservationId = string.Empty;
        public string RowId = string.Empty;
        public string LineId = string.Empty;
        public string ServiceDate = string.Empty;
        public int ServiceDayIndex = -1;
        public int OccurrenceIndex = 1;
        public Entity Line = Entity.Null;
        public Entity Vehicle = Entity.Null;
        public int TargetMinute = -1;
        public Entity Station = Entity.Null;
        public ResolvedStopKind Kind = ResolvedStopKind.Stop;
        public int WaypointIndex = -1;
        public bool IsOrigin;
        public string ArrivalTime = string.Empty;
        public string DepartureTime = string.Empty;
        public uint ArrivalFrame;
        public uint DepartureFrame;
        public uint LastUpdatedFrame;
    }

    internal sealed class RuntimeObservedBypassEvent
    {
        public string EventId = string.Empty;
        public string State = "holding";
        public string LocalTripObservationId = string.Empty;
        public string LocalRowId = string.Empty;
        public string LocalServiceDate = string.Empty;
        public int LocalServiceDayIndex = -1;
        public int LocalOccurrenceIndex = 1;
        public string PriorityTripObservationId = string.Empty;
        public string PriorityRowId = string.Empty;
        public string PriorityServiceDate = string.Empty;
        public int PriorityServiceDayIndex = -1;
        public int PriorityOccurrenceIndex = 1;
        public Entity LocalLine = Entity.Null;
        public Entity PriorityLine = Entity.Null;
        public Entity LocalVehicle = Entity.Null;
        public Entity PriorityVehicle = Entity.Null;
        public int LocalTargetMinute = -1;
        public int PriorityTargetMinute = -1;
        public Entity HoldStation = Entity.Null;
        public int WaypointIndex = -1;
        public uint HoldStartFrame;
        public uint HoldReleaseFrame;
        public string DecisionReason = string.Empty;
        public string ReleaseReason = string.Empty;
        public string SceneKey = string.Empty;
        public int ProtectedIntervalIndex = -1;
        public uint LastUpdatedFrame;
    }

    internal sealed class RuntimeObservationStore
    {
        private RuntimeObservationSession m_RuntimeObservationSession;
        private readonly Dictionary<string, List<RuntimeObservedTrip>> m_RuntimeObservedTripsByLineSlot =
            new Dictionary<string, List<RuntimeObservedTrip>>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, List<RuntimeObservedTrip>> m_RuntimeObservedTripsByVehicle =
            new Dictionary<Entity, List<RuntimeObservedTrip>>();
        private readonly Dictionary<Entity, RuntimeObservedBypassEvent> m_RuntimeActiveBypassByVehicle =
            new Dictionary<Entity, RuntimeObservedBypassEvent>();

        internal RuntimeObservationSession Session
        {
            get => m_RuntimeObservationSession;
            set => m_RuntimeObservationSession = value;
        }

        internal Dictionary<string, List<RuntimeObservedTrip>> TripsByLineSlot => m_RuntimeObservedTripsByLineSlot;
        internal Dictionary<Entity, List<RuntimeObservedTrip>> TripsByVehicle => m_RuntimeObservedTripsByVehicle;
        internal Dictionary<Entity, RuntimeObservedBypassEvent> ActiveBypassByVehicle => m_RuntimeActiveBypassByVehicle;

        internal void Clear()
        {
            m_RuntimeObservationSession = null;
            m_RuntimeObservedTripsByLineSlot.Clear();
            m_RuntimeObservedTripsByVehicle.Clear();
            m_RuntimeActiveBypassByVehicle.Clear();
        }

        internal void ClearIndexes()
        {
            m_RuntimeObservedTripsByLineSlot.Clear();
            m_RuntimeObservedTripsByVehicle.Clear();
            m_RuntimeActiveBypassByVehicle.Clear();
        }
    }
}
