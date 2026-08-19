using System;
using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Session
    {
        public string SnapshotId = string.Empty;
        public string Status = "empty";
        public uint AppliedFrame;
        public uint UpdatedFrame;
        public DateTime AppliedDate = DateTime.MinValue;
        public readonly Dictionary<string, Trip> Trips =
            new Dictionary<string, Trip>(StringComparer.Ordinal);
        public readonly List<StopEvent> Stops = new List<StopEvent>();
        public readonly List<BypassEvent> Bypass = new List<BypassEvent>();
        public readonly List<CorridorDto> Corridors =
            new List<CorridorDto>();
    }

    internal sealed class Trip
    {
        public string Id = string.Empty;
        public string BaseKey = string.Empty;
        public string State = "pending";
        public string LineId = string.Empty;
        public string RowId = string.Empty;
        public string Source = string.Empty;
        public string ServiceKind = string.Empty;
        public string ServiceDate = string.Empty;
        public int ServiceDayIndex = -1;
        public int OccurrenceIndex = 1;
        public int TargetMin = -1;
        public int ActualMin = -1;
        public Entity Line = Entity.Null;
        public Entity Vehicle = Entity.Null;
        public uint LaunchFrame;
        public string BindingConfidence = "seeded";
        public string ReasonCode = string.Empty;
        public uint UpdatedFrame;
    }

    internal enum MonitorTripState : byte
    {
        Active,
        Completed,
        Missed,
        Cleared
    }

    internal enum MonitorEndReason : byte
    {
        None,
        Rebound,
        Removed,
        Retired,
        Relaunched
    }

    internal sealed class MonitorTrip
    {
        public string Key = string.Empty;
        public string LineKey = string.Empty;
        public string LineId = string.Empty;
        public string RowId = string.Empty;
        public string ServiceKind = string.Empty;
        public string StopSig = string.Empty;
        public Entity Line = Entity.Null;
        public Entity Vehicle = Entity.Null;
        public int ServiceDateKey;
        public int SlotMinute = -1;
        // 持久元素仍保留旧字段；内存权威改由始发站实际离站表达。
        public int ActualStartMinute
        {
            get => Stops.Count > 0 ? Stops[0].ActualDeparture : -1;
            set { }
        }
        public int NextArrivalOrder = 1;
        public int VisibleStopCount;
        public int SuppressPlanFrom = int.MaxValue;
        public string LastFactStopKey = string.Empty;
        public uint LastFactFrame;
        public bool LastFactArrival;
        public MonitorTripState State;
        public MonitorEndReason EndReason;
        public uint LaunchFrame;
        public uint UpdatedFrame;
        public readonly List<MonitorStop> Stops = new List<MonitorStop>();
    }

    internal sealed class MonitorStop
    {
        public string StopKey = string.Empty;
        public Entity Station = Entity.Null;
        public int WaypointIndex = -1;
        public int PlannedArrival = -1;
        public int PlannedDeparture = -1;
        public int ActualArrival = -1;
        public int ActualDeparture = -1;
        public uint ActualArrivalFrame;
        public uint ActualDepartureFrame;
        public uint OpenIntervalMaxFrames;
        public bool Skipped;
        public bool Cleared;
    }

    internal sealed class MonitorDateSlot
    {
        public int DateKey;
        public readonly Dictionary<string, MonitorTrip> Trips =
            new Dictionary<string, MonitorTrip>(StringComparer.Ordinal);
    }

    internal sealed class StopEvent
    {
        public string EventId = string.Empty;
        public string EventType = string.Empty;
        public string TripId = string.Empty;
        public string RowId = string.Empty;
        public string LineId = string.Empty;
        public string ServiceDate = string.Empty;
        public int ServiceDayIndex = -1;
        public int OccurrenceIndex = 1;
        public Entity Line = Entity.Null;
        public Entity Vehicle = Entity.Null;
        public int TargetMin = -1;
        public Entity Station = Entity.Null;
        public ResolvedStopKind Kind = ResolvedStopKind.Stop;
        public int WaypointIndex = -1;
        public bool IsOrigin;
        public string ArrivalTime = string.Empty;
        public string DepartureTime = string.Empty;
        public uint ArrivalFrame;
        public uint DepartureFrame;
        public uint UpdatedFrame;
    }

    internal sealed class BypassEvent
    {
        public string EventId = string.Empty;
        public string State = "holding";
        public string LocalTripId = string.Empty;
        public string LocalRowId = string.Empty;
        public string LocalServiceDate = string.Empty;
        public int LocalServiceDayIndex = -1;
        public int LocalOccurrenceIndex = 1;
        public string PriorityTripId = string.Empty;
        public string PriorityRowId = string.Empty;
        public string PriorityServiceDate = string.Empty;
        public int PriorityServiceDayIndex = -1;
        public int PriorityOccurrenceIndex = 1;
        public Entity LocalLine = Entity.Null;
        public Entity PriorityLine = Entity.Null;
        public Entity LocalVehicle = Entity.Null;
        public Entity PriorityVehicle = Entity.Null;
        public int LocalTargetMin = -1;
        public int PriorityTargetMin = -1;
        public Entity HoldStation = Entity.Null;
        public int WaypointIndex = -1;
        public uint HoldStartFrame;
        public uint HoldReleaseFrame;
        public string DecisionReason = string.Empty;
        public string ReleaseReason = string.Empty;
        public string SceneKey = string.Empty;
        public int ProtectedIntervalIndex = -1;
        public uint UpdatedFrame;
    }

    internal sealed class VehicleTrace
    {
        public Entity Vehicle;
        public Entity Line;
        public string Kind = "local";
        public int NextSeq = 1;
        public readonly List<TripTrace> Trips = new List<TripTrace>();
    }

    internal sealed class TripTrace
    {
        public int Seq;
        public readonly List<StopTrace> Stops = new List<StopTrace>();
        public uint Frame;
    }

    internal sealed class StopTrace
    {
        public Entity Stop;
        public ResolvedStopKind Kind;
        public string Arrival = string.Empty;
        public string Departure = string.Empty;
        public uint Frame;
    }

    internal readonly struct TrainHeadSnapshot
    {
        public readonly uint Frame;
        public readonly Entity HeadVehicle;
        public readonly Entity FrontLane;
        public readonly Entity RearLane;
        public readonly bool Reversed;
        public readonly int WaypointIndex;

        public TrainHeadSnapshot(
            uint frame,
            Entity headVehicle,
            Entity frontLane,
            Entity rearLane,
            bool reversed,
            int waypointIndex)
        {
            Frame = frame;
            HeadVehicle = headVehicle;
            FrontLane = frontLane;
            RearLane = rearLane;
            Reversed = reversed;
            WaypointIndex = waypointIndex;
        }
    }
}
