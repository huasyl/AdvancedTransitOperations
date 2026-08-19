namespace RapidTransitMod.RailEtaHost
{
    public readonly struct RailEtaPublicTicket
    {
        public RailEtaPublicTicket(long value) { Value = value; }
        public long Value { get; }
        public bool IsValid => Value != 0;
    }

    internal readonly struct RailEtaPublicRequest
    {
        public RailEtaPublicRequest(int vehicleIndex, int vehicleVersion, long targetCheckpointId, RailEtaMode mode = RailEtaMode.Full)
            : this(vehicleIndex, vehicleVersion, targetCheckpointId, mode, 0, 0, 0, 0, 0, 0)
        {
        }

        public RailEtaPublicRequest(int lineIndex, int lineVersion, long targetCheckpointId, RailEtaMode mode,
            int depotIndex, int depotVersion, int modelIndex, int modelVersion,
            int secondaryModelIndex = 0, int secondaryModelVersion = 0)
        {
            VehicleIndex = lineIndex;
            VehicleVersion = lineVersion;
            TargetCheckpointId = targetCheckpointId;
            Mode = mode;
            DepotIndex = depotIndex;
            DepotVersion = depotVersion;
            ModelIndex = modelIndex;
            ModelVersion = modelVersion;
            SecondaryModelIndex = secondaryModelIndex;
            SecondaryModelVersion = secondaryModelVersion;
        }

        public int VehicleIndex { get; }
        public int VehicleVersion { get; }
        public long TargetCheckpointId { get; }
        public RailEtaMode Mode { get; }
        public int DepotIndex { get; }
        public int DepotVersion { get; }
        public int ModelIndex { get; }
        public int ModelVersion { get; }
        public int SecondaryModelIndex { get; }
        public int SecondaryModelVersion { get; }
    }

    public sealed class RailEtaPublicStatus
    {
        public RailEtaPublicTicket Ticket { get; internal set; }
        public string State { get; internal set; } = "Idle";
        public string Failure { get; internal set; } = string.Empty;
        public string Detail { get; internal set; } = string.Empty;
        public long TargetVehicle { get; internal set; }
        public long TargetWaypoint { get; internal set; }
        public uint EtaFrame { get; internal set; }
        public uint OriginFrame { get; internal set; }
        public string Source { get; internal set; } = string.Empty;
        public string Build { get; internal set; } = string.Empty;
        public long Generation { get; internal set; }
        public long ClockEpoch { get; internal set; }
        public bool Incomplete { get; internal set; }
        public RailEtaMode Mode { get; internal set; }
        public RailEtaTheorySegmentResult[] TheorySegments { get; internal set; } = new RailEtaTheorySegmentResult[0];
        public RailEtaTheoryFailure TheoryFailure { get; internal set; }
        public ulong RouteSignature { get; internal set; }
        public ulong PathSignature { get; internal set; }
        public ulong ModelSignature { get; internal set; }
        public string ComparisonSummary { get; internal set; } = string.Empty;
    }
}
