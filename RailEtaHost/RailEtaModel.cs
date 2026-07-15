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
        public RailEtaPublicRequest(int vehicleIndex, int vehicleVersion, long targetCheckpointId)
        {
            VehicleIndex = vehicleIndex;
            VehicleVersion = vehicleVersion;
            TargetCheckpointId = targetCheckpointId;
        }

        public int VehicleIndex { get; }
        public int VehicleVersion { get; }
        public long TargetCheckpointId { get; }
    }

    public sealed class RailEtaPublicStatus
    {
        public RailEtaPublicTicket Ticket { get; internal set; }
        public string State { get; internal set; } = "Idle";
        public string Failure { get; internal set; } = string.Empty;
        public string Detail { get; internal set; } = string.Empty;
        public uint EtaFrame { get; internal set; }
        public string Source { get; internal set; } = string.Empty;
        public string Build { get; internal set; } = string.Empty;
        public long Generation { get; internal set; }
        public bool Incomplete { get; internal set; }
        public string ComparisonSummary { get; internal set; } = string.Empty;
    }
}
