using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class State
    {
        internal readonly Dictionary<Entity, OpenStop> OpenStops = new Dictionary<Entity, OpenStop>();
        internal readonly Queue<OpenStop> StopSamples = new Queue<OpenStop>();
        internal readonly Queue<PendingSample> PendingSamples = new Queue<PendingSample>();
        internal readonly Dictionary<Entity, PassengerBaseline> Baselines = new Dictionary<Entity, PassengerBaseline>();
        internal readonly Dictionary<Entity, uint> LastLaunchFrames = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, string> VehicleIds = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, VehicleLoadState> VehicleLoads = new Dictionary<Entity, VehicleLoadState>();
        internal readonly HashSet<Entity> InitialLoadSamples = new HashSet<Entity>();
        internal readonly Dictionary<Entity, LineLoadState> LineLoads = new Dictionary<Entity, LineLoadState>();
        private readonly LoadState[] m_NetworkLoads =
            { new LoadState(), new LoadState(), new LoadState(), new LoadState() };
        internal readonly Dictionary<StationWaitingSource, WaitingBaseline> WaitingBaselines = new Dictionary<StationWaitingSource, WaitingBaseline>();
        internal readonly List<PassengerFlowVehicleElement> RestoredVehicles = new List<PassengerFlowVehicleElement>();
        internal readonly List<PassengerFlowBaselineElement> RestoredBaselines = new List<PassengerFlowBaselineElement>();
        internal readonly List<PassengerFlowSampleElement> RestoredSamples = new List<PassengerFlowSampleElement>();
        internal readonly List<PassengerFlowSectionElement> RestoredSections = new List<PassengerFlowSectionElement>();
        internal readonly List<PassengerFlowStopElement> RestoredStops = new List<PassengerFlowStopElement>();
        internal readonly List<PassengerFlowTripElement> RestoredTrips = new List<PassengerFlowTripElement>();
        internal readonly List<PassengerFlowPlanElement> RestoredPlans = new List<PassengerFlowPlanElement>();
        internal readonly Trips Trips = new Trips();
        internal readonly Aggregates Aggregates = new Aggregates();
        internal readonly Anchors Anchors = new Anchors();
        internal readonly Sections Sections = new Sections();

        internal int DayIndex;
        internal int LastMinute = -1;
        internal int LastBucketUpdateMinute = -1;
        internal TimeBucketKey CurrentBucket = new TimeBucketKey(0, 0);
        internal int CurrentAbsoluteBucketIndex;
        internal uint LastPendingCleanupFrame;
        internal uint LastStopScanFrame;
        internal uint LastSnapshotSummaryLogFrame;
        internal uint LastWaitingSampleFrame;
        internal uint NextWaitingSampleFrame;
        internal uint NextFastRepresentativeFrame;
        internal uint NextSlowRepresentativeFrame;
        internal uint LastTimeFlushFrame;
        internal bool RuntimeRestorePending;
        internal bool LoadsInitialized;

        internal void Clear()
        {
            OpenStops.Clear();
            StopSamples.Clear();
            PendingSamples.Clear();
            Baselines.Clear();
            LastLaunchFrames.Clear();
            VehicleIds.Clear();
            VehicleLoads.Clear();
            InitialLoadSamples.Clear();
            LineLoads.Clear();
            foreach (LoadState load in m_NetworkLoads)
                load.Clear();
            WaitingBaselines.Clear();
            RestoredVehicles.Clear();
            RestoredBaselines.Clear();
            RestoredSamples.Clear();
            RestoredSections.Clear();
            RestoredStops.Clear();
            RestoredTrips.Clear();
            RestoredPlans.Clear();
            Trips.Clear();
            Aggregates.Clear();
            Anchors.Clear();
            Sections.Clear();
            DayIndex = 0;
            LastMinute = -1;
            LastBucketUpdateMinute = -1;
            CurrentBucket = new TimeBucketKey(0, 0);
            CurrentAbsoluteBucketIndex = 0;
            LastPendingCleanupFrame = 0;
            LastStopScanFrame = 0;
            LastSnapshotSummaryLogFrame = 0;
            LastWaitingSampleFrame = 0;
            NextWaitingSampleFrame = 0;
            NextFastRepresentativeFrame = 0;
            NextSlowRepresentativeFrame = 0;
            LastTimeFlushFrame = 0;
            RuntimeRestorePending = false;
            LoadsInitialized = false;
        }

        internal string GetVehicleId(Entity vehicle)
        {
            if (!VehicleIds.TryGetValue(vehicle, out string vehicleId))
            {
                for (int i = 0; i < RestoredVehicles.Count; i++)
                {
                    if (RestoredVehicles[i].m_Vehicle == vehicle)
                    {
                        vehicleId = RestoredVehicles[i].m_VehicleId.ToString();
                        VehicleIds[vehicle] = vehicleId;
                        return vehicleId;
                    }
                }
                vehicleId = "pfv-" + System.Guid.NewGuid().ToString("N");
                VehicleIds[vehicle] = vehicleId;
            }

            return vehicleId;
        }

        internal void RegisterVehicle(Entity vehicle, Entity line, bool qualified,
            TransitMode mode, string lineId, uint frame, Core.ClockSnapshot clock)
        {
            bool exists = VehicleLoads.TryGetValue(vehicle, out VehicleLoadState old);
            bool firstService = !exists || !old.Qualified || old.Line != line;
            if (exists)
            {
                if (old.Qualified)
                {
                    CloseLine(old.Line, frame, clock);
                    ApplyVehicle(old, -1, frame, clock);
                }
                old.Line = line;
                old.Qualified = qualified;
            }
            else
            {
                old = new VehicleLoadState { Line = line, Qualified = qualified };
            }
            if (qualified)
            {
                if (!LineLoads.ContainsKey(line))
                    LineLoads[line] = new LineLoadState(mode, lineId, frame, Aggregates.BindTimeLoad(mode, lineId));
                else
                    CloseLine(line, frame, clock);
                ApplyVehicle(old, 1, frame, clock);
            }
            VehicleLoads[vehicle] = old;
            if (qualified && firstService && !old.HasPassengers)
                InitialLoadSamples.Add(vehicle);
            else if (!qualified)
                InitialLoadSamples.Remove(vehicle);
        }

        internal void SetVehicleLoad(Entity vehicle, int passengers, int capacity, bool hasCapacity,
            bool valid, uint frame, Core.ClockSnapshot clock)
        {
            if (!VehicleLoads.TryGetValue(vehicle, out VehicleLoadState current))
                return;
            if (current.Qualified)
            {
                CloseLine(current.Line, frame, clock);
                ApplyVehicle(current, -1, frame, clock);
            }
            current.HasPassengers = valid;
            current.Passengers = passengers;
            current.HasCapacity = valid && hasCapacity;
            current.Capacity = capacity;
            VehicleLoads[vehicle] = current;
            if (current.Qualified)
                ApplyVehicle(current, 1, frame, clock);
        }

        internal void EndVehicleService(Entity vehicle, Entity line, uint frame, Core.ClockSnapshot clock)
        {
            if (!VehicleLoads.TryGetValue(vehicle, out VehicleLoadState current) || current.Line != line)
                return;
            if (current.Qualified)
            {
                CloseLine(line, frame, clock);
                ApplyVehicle(current, -1, frame, clock);
            }
            current.Qualified = false;
            InitialLoadSamples.Remove(vehicle);
            VehicleLoads[vehicle] = current;
        }

        internal void RemoveVehicleLoad(Entity vehicle, uint frame, Core.ClockSnapshot clock)
        {
            if (!VehicleLoads.TryGetValue(vehicle, out VehicleLoadState current))
                return;
            if (current.Qualified)
            {
                CloseLine(current.Line, frame, clock);
                ApplyVehicle(current, -1, frame, clock);
            }
            VehicleLoads.Remove(vehicle);
            InitialLoadSamples.Remove(vehicle);
        }

        internal void CloseAllLines(uint frame, Core.ClockSnapshot clock)
        {
            foreach (LineLoadState line in LineLoads.Values)
                CloseLine(line, frame, clock);
            CloseNetworks(frame, clock);
        }

        internal void StartNetworkLoads(uint frame)
        {
            foreach (LoadState load in m_NetworkLoads)
                load.AccumulatedThroughFrame = frame;
        }

        private void CloseNetworks(uint frame, Core.ClockSnapshot clock)
        {
            if (!LoadsInitialized)
                return;
            for (int i = 0; i < m_NetworkLoads.Length; i++)
                CloseLoad(m_NetworkLoads[i], frame, clock, (TransitMode)(i + 1));
        }

        internal void UpdateAppliedLines(HashSet<Entity> applied, uint frame, Core.ClockSnapshot clock)
        {
            List<Entity> remove = null;
            foreach (KeyValuePair<Entity, LineLoadState> entry in LineLoads)
            {
                bool active = applied.Contains(entry.Key);
                LineLoadState line = entry.Value;
                if (line.IsApplied != active)
                {
                    CloseLine(entry.Key, frame, clock);
                    ApplyNetwork(line, active ? 1 : -1, frame, clock);
                    line.IsApplied = active;
                }
                if (!active && line.QualifiedVehicles == 0)
                {
                    if (remove == null)
                        remove = new List<Entity>();
                    remove.Add(entry.Key);
                }
            }
            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                {
                    Aggregates.UnbindTimeLoad(LineLoads[remove[i]].Series);
                    LineLoads.Remove(remove[i]);
                }
            }
        }

        internal void EnsureAppliedLine(Entity line, TransitMode mode, string lineId,
            uint frame, Core.ClockSnapshot clock)
        {
            if (LineLoads.TryGetValue(line, out LineLoadState existing))
            {
                if (!existing.IsApplied)
                {
                    CloseLine(line, frame, clock);
                    ApplyNetwork(existing, 1, frame, clock);
                    existing.IsApplied = true;
                }
                return;
            }
            LineLoads[line] = new LineLoadState(mode, lineId, frame, Aggregates.BindTimeLoad(mode, lineId));
        }
        private void ApplyVehicle(VehicleLoadState vehicle, int sign, uint frame, Core.ClockSnapshot clock)
        {
            LineLoadState line = LineLoads[vehicle.Line];
            long passengers = vehicle.HasPassengers ? vehicle.Passengers : 0;
            long capacity = vehicle.HasPassengers && vehicle.HasCapacity ? vehicle.Capacity : 0;
            int unknownPassengers = vehicle.HasPassengers ? 0 : 1;
            int unknownCapacity = vehicle.HasPassengers && vehicle.HasCapacity ? 0 : 1;
            if (line.IsApplied)
            {
                CloseNetworks(frame, clock);
                ChangeLoad(m_NetworkLoads[(int)line.Mode - 1], passengers, capacity, unknownPassengers, unknownCapacity, sign);
            }
            line.QualifiedVehicles += sign;
            ChangeLoad(line, passengers, capacity, unknownPassengers, unknownCapacity, sign);
        }

        private void ApplyNetwork(LineLoadState line, int sign, uint frame, Core.ClockSnapshot clock)
        {
            CloseNetworks(frame, clock);
            ChangeLoad(m_NetworkLoads[(int)line.Mode - 1], line.Passengers, line.Capacity,
                line.UnknownPassengers, line.UnknownCapacity, sign);
        }

        private static void ChangeLoad(LoadState load, long passengers, long capacity,
            int unknownPassengers, int unknownCapacity, int sign)
        {
            load.Passengers += sign * passengers;
            load.Capacity += sign * capacity;
            load.UnknownPassengers += sign * unknownPassengers;
            load.UnknownCapacity += sign * unknownCapacity;
        }

        private void CloseLine(Entity lineEntity, uint frame, Core.ClockSnapshot clock)
        {
            if (LineLoads.TryGetValue(lineEntity, out LineLoadState line))
                CloseLine(line, frame, clock);
        }

        private void CloseLine(LineLoadState line, uint frame, Core.ClockSnapshot clock)
        {
            if (frame <= line.AccumulatedThroughFrame)
                return;
            if (!line.IsApplied)
            {
                line.AccumulatedThroughFrame = frame;
                return;
            }
            CloseLoad(line, frame, clock);
        }

        private void CloseLoad(LoadState load, uint frame, Core.ClockSnapshot clock,
            TransitMode mode = TransitMode.Unknown)
        {
            if (frame <= load.AccumulatedThroughFrame)
                return;
            uint remaining = frame - load.AccumulatedThroughFrame;
            double cursorMinute = clock.DayIndex * 1440d
                + clock.NowMinuteExact;
            while (remaining > 0)
            {
                double bucketStart = System.Math.Floor((cursorMinute - 1e-6) / 15d) * 15d;
                uint inBucket = (uint)System.Math.Max(1d,
                    System.Math.Ceiling((cursorMinute - bucketStart) * clock.FramesPerMinute));
                uint covered = System.Math.Min(remaining, inBucket);
                TimeBucketKey bucket = SamplingSystem.BucketFromAbsoluteIndex(
                    (int)(bucketStart / Snapshot.BucketMinutes));
                if (load.UnknownPassengers == 0)
                {
                    long frames = covered;
                    bool completeCapacity = load.UnknownCapacity == 0;
                    LineTimeAggregate value = new LineTimeAggregate
                    {
                        PassengerFrames = load.Passengers * frames, ObservedFrames = frames,
                        RatedPassengerFrames = completeCapacity ? load.Passengers * frames : 0L,
                        CapacityFrames = completeCapacity ? load.Capacity * frames : 0L,
                        CapacityObservedFrames = completeCapacity ? frames : 0L
                    };
                    if (load is LineLoadState line)
                        Aggregates.RecordTimeLoad(line.Series, bucket, value);
                    else
                        Aggregates.RecordNetworkLoad(mode, bucket, value);
                }
                remaining -= covered;
                cursorMinute -= covered / clock.FramesPerMinute;
            }
            load.AccumulatedThroughFrame = frame;
        }

        internal void RemoveWaitingLine(Entity line)
        {
            List<StationWaitingSource> remove = null;
            foreach (StationWaitingSource source in WaitingBaselines.Keys)
            {
                if (source.Line != line)
                    continue;
                if (remove == null)
                    remove = new List<StationWaitingSource>();
                remove.Add(source);
            }
            if (remove == null)
                return;
            for (int i = 0; i < remove.Count; i++)
                WaitingBaselines.Remove(remove[i]);
        }
    }

    internal struct VehicleLoadState
    {
        internal Entity Line;
        internal bool Qualified;
        internal bool HasPassengers;
        internal int Passengers;
        internal bool HasCapacity;
        internal int Capacity;
    }

    internal class LoadState
    {
        internal uint AccumulatedThroughFrame;
        internal int UnknownPassengers;
        internal int UnknownCapacity;
        internal long Passengers;
        internal long Capacity;

        internal void Clear()
        {
            AccumulatedThroughFrame = 0;
            UnknownPassengers = 0; UnknownCapacity = 0; Passengers = 0; Capacity = 0;
        }
    }

    internal sealed class LineLoadState : LoadState
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly Aggregates.LineLoadSeries Series;
        internal int QualifiedVehicles;
        internal bool IsApplied = true;

        internal LineLoadState(TransitMode mode, string lineId, uint frame, Aggregates.LineLoadSeries series)
        {
            Mode = mode; LineId = lineId; AccumulatedThroughFrame = frame; Series = series;
        }
    }

    internal readonly struct StopPosition
    {
        internal readonly Entity Stop;
        internal readonly Entity Waypoint;
        internal readonly int WaypointIndex;
        internal readonly int StationOccurrence;
        internal bool IsValid => Stop != Entity.Null && Waypoint != Entity.Null && StationOccurrence > 0;

        internal StopPosition(Entity stop, Entity waypoint, int waypointIndex, int stationOccurrence)
        {
            Stop = stop; Waypoint = waypoint; WaypointIndex = waypointIndex;
            StationOccurrence = stationOccurrence;
        }
    }

    internal readonly struct OpenStop
    {
        internal readonly Entity Vehicle;
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly Entity Line;
        internal readonly int OpenWaypointIndex;
        internal readonly int OpenStationSakIndex;
        internal readonly uint OpenFrame;
        internal readonly int WaitingPassengersSnapshot;
        internal readonly StopPosition Position;

        internal OpenStop(
            Entity vehicle,
            TransitMode mode,
            string lineId,
            Entity line,
            int openWaypointIndex,
            int openStationSakIndex,
            uint openFrame,
            int waitingPassengersSnapshot, StopPosition position = default)
        {
            Vehicle = vehicle;
            Mode = mode;
            LineId = lineId;
            Line = line;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
            OpenFrame = openFrame;
            WaitingPassengersSnapshot = waitingPassengersSnapshot;
            Position = position;
        }
    }

    internal readonly struct PendingSample
    {
        internal readonly uint SampleFrame;
        internal readonly uint OpenFrame;
        internal readonly uint DepartureFrame;
        internal readonly TimeBucketKey DepartureBucket;
        internal readonly string VehicleId;
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly Entity Line;
        internal readonly Entity Vehicle;
        internal readonly Entity RuntimeVehicle;
        internal readonly int OpenWaypointIndex;
        internal readonly int OpenStationSakIndex;
        internal readonly StopPosition Position;

        internal PendingSample(
            uint sampleFrame,
            uint openFrame,
            uint departureFrame,
            TimeBucketKey departureBucket,
            string vehicleId,
            TransitMode mode,
            string lineId,
            Entity line,
            Entity vehicle,
            Entity runtimeVehicle,
            int openWaypointIndex,
            int openStationSakIndex,
            StopPosition position,
            SectionSegment[] stopSections, SectionSegment[] trackSections)
        {
            SampleFrame = sampleFrame;
            OpenFrame = openFrame;
            DepartureFrame = departureFrame;
            DepartureBucket = departureBucket;
            VehicleId = vehicleId;
            Mode = mode;
            LineId = lineId;
            Line = line;
            Vehicle = vehicle;
            RuntimeVehicle = runtimeVehicle;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
            Position = position;
            StopSections = stopSections;
            TrackSections = trackSections;
        }

        internal readonly SectionSegment[] StopSections;
        internal readonly SectionSegment[] TrackSections;

        internal VehicleSampleRequest ToJobRequest()
        {
            return new VehicleSampleRequest(
                SampleFrame,
                Mode,
                Line,
                Vehicle,
                RuntimeVehicle,
                OpenWaypointIndex,
                OpenStationSakIndex);
        }
    }

    internal readonly struct VehicleSampleRequest
    {
        internal readonly uint SampleFrame;
        internal readonly TransitMode Mode;
        internal readonly Entity Line;
        internal readonly Entity Vehicle;
        internal readonly Entity RuntimeVehicle;
        internal readonly int OpenWaypointIndex;
        internal readonly int OpenStationSakIndex;

        internal VehicleSampleRequest(
            uint sampleFrame,
            TransitMode mode,
            Entity line,
            Entity vehicle,
            Entity runtimeVehicle,
            int openWaypointIndex,
            int openStationSakIndex)
        {
            SampleFrame = sampleFrame;
            Mode = mode;
            Line = line;
            Vehicle = vehicle;
            RuntimeVehicle = runtimeVehicle;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
        }

        internal Entity BaselineKey
        {
            get { return RuntimeVehicle != Entity.Null ? RuntimeVehicle : Vehicle; }
        }
    }

    internal enum PurposeCategory : byte
    {
        Unknown, Work, School, Shopping, Leisure, Sightseeing,
        Home, Hotel, MovingIn, MovingAway, LeavingCity, Other
    }

    internal readonly struct PurposeInfo : System.IEquatable<PurposeInfo>
    {
        internal readonly int RawPurpose;
        internal readonly PurposeCategory Category;

        internal PurposeInfo(int rawPurpose, PurposeCategory category)
        {
            RawPurpose = rawPurpose;
            Category = category;
        }

        internal static PurposeInfo Unknown => new PurposeInfo(-1, PurposeCategory.Unknown);
        public bool Equals(PurposeInfo other)
            => RawPurpose == other.RawPurpose && Category == other.Category;
        public override bool Equals(object obj) => obj is PurposeInfo other && Equals(other);
        public override int GetHashCode() => (RawPurpose * 397) ^ (int)Category;
        internal string CategoryCode
        {
            get
            {
                switch (Category)
                {
                    case PurposeCategory.Work: return "work";
                    case PurposeCategory.School: return "school";
                    case PurposeCategory.Shopping: return "shopping";
                    case PurposeCategory.Leisure: return "leisure";
                    case PurposeCategory.Sightseeing: return "sightseeing";
                    case PurposeCategory.Home: return "home";
                    case PurposeCategory.Hotel: return "hotel";
                    case PurposeCategory.MovingIn: return "movingIn";
                    case PurposeCategory.MovingAway: return "movingAway";
                    case PurposeCategory.LeavingCity: return "leavingCity";
                    case PurposeCategory.Other: return "other";
                    default: return "unknown";
                }
            }
        }
    }

    internal sealed class PassengerBaseline
    {
        internal readonly List<Entity> Passengers = new List<Entity>();
        internal readonly Dictionary<Entity, PurposeInfo> Purposes = new Dictionary<Entity, PurposeInfo>();

        internal void Replace(List<Entity> passengers, Dictionary<Entity, PurposeInfo> purposes)
        {
            Passengers.Clear();
            Purposes.Clear();
            for (int i = 0; i < passengers.Count; i++)
            {
                Passengers.Add(passengers[i]);
                Purposes[passengers[i]] = purposes[passengers[i]];
            }
        }
    }

    internal readonly struct StationWaitingSource : System.IEquatable<StationWaitingSource>
    {
        internal readonly Entity Line;
        internal readonly Entity Waypoint;

        internal StationWaitingSource(Entity line, Entity waypoint)
        {
            Line = line;
            Waypoint = waypoint;
        }

        public bool Equals(StationWaitingSource other)
            => Line == other.Line && Waypoint == other.Waypoint;

        public override bool Equals(object obj)
            => obj is StationWaitingSource other && Equals(other);

        public override int GetHashCode()
            => (Line.GetHashCode() * 397) ^ Waypoint.GetHashCode();
    }

    internal sealed class WaitingBaseline
    {
        internal TransitMode Mode;
        internal string LineId;
        internal int StationOccurrence;
        internal int StationSakIndex;
        internal int WaitingCount;
        internal double EstimatedWaitFrames;
        internal uint ObservedFrame;
        internal uint AccumulatedThroughFrame;

        internal WaitingBaseline(
            TransitMode mode, string lineId, int stationOccurrence, int stationSakIndex,
            int waitingCount, double estimatedWaitFrames, uint frame)
        {
            Mode = mode;
            LineId = lineId;
            StationOccurrence = stationOccurrence;
            StationSakIndex = stationSakIndex;
            WaitingCount = waitingCount;
            EstimatedWaitFrames = estimatedWaitFrames;
            ObservedFrame = frame;
            AccumulatedThroughFrame = frame;
        }

        internal void Observe(uint frame)
        {
            ObservedFrame = frame;
            AccumulatedThroughFrame = frame;
        }
    }

}
