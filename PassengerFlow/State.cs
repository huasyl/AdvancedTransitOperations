using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class State
    {
        internal readonly Dictionary<Entity, OpenStop> OpenStops = new Dictionary<Entity, OpenStop>();
        internal readonly Queue<PendingSample> PendingSamples = new Queue<PendingSample>();
        internal readonly Dictionary<Entity, PassengerBaseline> Baselines = new Dictionary<Entity, PassengerBaseline>();
        internal readonly Dictionary<Entity, uint> LastProbeFrames = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, uint> LastLaunchFrames = new Dictionary<Entity, uint>();
        internal readonly Trips Trips = new Trips();
        internal readonly Aggregates Aggregates = new Aggregates();
        internal readonly Anchors Anchors = new Anchors();
        internal readonly Sections Sections = new Sections();
        internal readonly HashSet<TimeBucketKey> RollingWindow = new HashSet<TimeBucketKey>();

        internal int ServiceDayKey;
        internal int LastMinute = -1;
        internal int LastBucketUpdateMinute = -1;
        internal TimeBucketKey CurrentBucket = new TimeBucketKey(19700101, 0);
        internal int CurrentAbsoluteBucketIndex;
        internal uint LastPendingCleanupFrame;
        internal uint LastProbeScanFrame;
        internal uint LastSnapshotSummaryLogFrame;
        internal PassengerFlowPersistedStationVolume[] LegacyStationVolumes = System.Array.Empty<PassengerFlowPersistedStationVolume>();
        internal PassengerFlowPersistedSectionVolume[] LegacySectionVolumes = System.Array.Empty<PassengerFlowPersistedSectionVolume>();
        internal PassengerFlowPersistedOdFlow[] LegacyOdFlows = System.Array.Empty<PassengerFlowPersistedOdFlow>();
        internal PassengerFlowPersistedWarning[] LegacyWarnings = System.Array.Empty<PassengerFlowPersistedWarning>();

        internal void Clear()
        {
            OpenStops.Clear();
            PendingSamples.Clear();
            Baselines.Clear();
            LastProbeFrames.Clear();
            LastLaunchFrames.Clear();
            Trips.Clear();
            Aggregates.Clear();
            Anchors.Clear();
            Sections.Clear();
            RollingWindow.Clear();
            ServiceDayKey = 0;
            LastMinute = -1;
            LastBucketUpdateMinute = -1;
            CurrentBucket = new TimeBucketKey(19700101, 0);
            CurrentAbsoluteBucketIndex = 0;
            LastPendingCleanupFrame = 0;
            LastProbeScanFrame = 0;
            LastSnapshotSummaryLogFrame = 0;
            LegacyStationVolumes = System.Array.Empty<PassengerFlowPersistedStationVolume>();
            LegacySectionVolumes = System.Array.Empty<PassengerFlowPersistedSectionVolume>();
            LegacyOdFlows = System.Array.Empty<PassengerFlowPersistedOdFlow>();
            LegacyWarnings = System.Array.Empty<PassengerFlowPersistedWarning>();
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

        internal OpenStop(
            Entity vehicle,
            TransitMode mode,
            string lineId,
            Entity line,
            int openWaypointIndex,
            int openStationSakIndex,
            uint openFrame,
            int waitingPassengersSnapshot)
        {
            Vehicle = vehicle;
            Mode = mode;
            LineId = lineId ?? string.Empty;
            Line = line;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
            OpenFrame = openFrame;
            WaitingPassengersSnapshot = waitingPassengersSnapshot;
        }
    }

    internal readonly struct PendingSample
    {
        internal readonly uint SampleFrame;
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly Entity Line;
        internal readonly Entity Vehicle;
        internal readonly Entity RuntimeVehicle;
        internal readonly int OpenWaypointIndex;
        internal readonly int OpenStationSakIndex;
        internal readonly int NextWaypointIndex;
        internal readonly int NextStationSakIndex;

        internal PendingSample(
            uint sampleFrame,
            TransitMode mode,
            string lineId,
            Entity line,
            Entity vehicle,
            Entity runtimeVehicle,
            int openWaypointIndex,
            int openStationSakIndex,
            int nextWaypointIndex,
            int nextStationSakIndex)
        {
            SampleFrame = sampleFrame;
            Mode = mode;
            LineId = lineId ?? string.Empty;
            Line = line;
            Vehicle = vehicle;
            RuntimeVehicle = runtimeVehicle;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
            NextWaypointIndex = nextWaypointIndex;
            NextStationSakIndex = nextStationSakIndex;
        }

        internal VehicleSampleRequest ToJobRequest()
        {
            return new VehicleSampleRequest(
                SampleFrame,
                Mode,
                Line,
                Vehicle,
                RuntimeVehicle,
                OpenWaypointIndex,
                OpenStationSakIndex,
                NextWaypointIndex,
                NextStationSakIndex);
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
        internal readonly int NextWaypointIndex;
        internal readonly int NextStationSakIndex;

        internal VehicleSampleRequest(
            uint sampleFrame,
            TransitMode mode,
            Entity line,
            Entity vehicle,
            Entity runtimeVehicle,
            int openWaypointIndex,
            int openStationSakIndex,
            int nextWaypointIndex,
            int nextStationSakIndex)
        {
            SampleFrame = sampleFrame;
            Mode = mode;
            Line = line;
            Vehicle = vehicle;
            RuntimeVehicle = runtimeVehicle;
            OpenWaypointIndex = openWaypointIndex;
            OpenStationSakIndex = openStationSakIndex;
            NextWaypointIndex = nextWaypointIndex;
            NextStationSakIndex = nextStationSakIndex;
        }

        internal Entity BaselineKey
        {
            get { return RuntimeVehicle != Entity.Null ? RuntimeVehicle : Vehicle; }
        }
    }

    internal sealed class PassengerBaseline
    {
        internal readonly List<Entity> Passengers = new List<Entity>();

        internal void Replace(List<Entity> passengers)
        {
            Passengers.Clear();
            if (passengers == null)
                return;

            for (int i = 0; i < passengers.Count; i++)
                Passengers.Add(passengers[i]);
        }
    }

}
