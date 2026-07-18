using System;
using RapidTransitMod.RailEtaHost;

namespace RapidTransitMod.RailEta.Contracts
{
    public struct RailVehicleId { public RailVehicleId(long value) { Value = value; } public long Value { get; set; } }
    public struct RailLaneId { public RailLaneId(long value) { Value = value; } public long Value { get; set; } }
    public struct RailResourceId { public RailResourceId(long value) { Value = value; } public long Value { get; set; } }
    public struct RailCheckpointId { public RailCheckpointId(long value) { Value = value; } public long Value { get; set; } }
    public struct RailStationId { public RailStationId(long value) { Value = value; } public long Value { get; set; } }

    public static class RailEtaCheckpointIdentity
    {
        public static RailCheckpointId RemainingPathEndpoint(RailLaneId laneId)
            => laneId.Value == 0 ? default : new RailCheckpointId(laneId.Value);
    }

    public enum RailEtaConfidence { Full, FreeRunOnly, TieUncertain, ScopeTruncated, Unknown, BlockedCycle, NotConverged }
    public enum RailEtaFailure { None, InvalidInput, Cancelled, Busy, PathPending, PathFailed, PathIncomplete, FuturePathfindFailed, TargetChanged, TargetGone, GeometryPending, RouteGeometryMissing, ScopeTruncated, SnapshotUnstable, StoppedByDispatchHold, ControlledHoldUnknown, ReservationDataIncomplete, BlockedCycle, NotConverged, InvalidResult, ResultStale, WorkerLost }
    public enum RailEtaDiagnosticSeverity { Info, Warning, Error }

    public enum RailExternalBlockerKind { None, RailVehicle, RoadVehicle, Creature, LevelCrossing, Unknown }
    public enum RailBlockerType { None, Continuing, Crossing, Signal, Temporary, Limit, Caution, Spawn, Oncoming }
    public enum RailLaneSignalType { None, Stop, SafeStop, Yield, Go }
    public enum RailControlledHoldKind { None, OriginScheduled, BypassYield, UnknownControlledHold }

    public sealed class RailEntityIdentity
    {
        public int Index { get; set; }
        public int Version { get; set; }
    }

    public sealed class RailTrainPhysics
    {
        public double MaximumSpeedMetresPerSecond { get; set; }
        public double AccelerationMetresPerSecondSquared { get; set; }
        public double BrakingMetresPerSecondSquared { get; set; }
        public double TurningLowRadiansPerSecond { get; set; }
        public double TurningHighRadiansPerSecond { get; set; }
        public double StopSpeedThresholdMetresPerSecond { get; set; }
    }

    public sealed class RailPathSegment
    {
        public RailLaneId LaneId { get; set; }
        public RailLaneId PhysicalLaneId { get; set; }
        public double LengthMetres { get; set; }
        public double SpeedLimitMetresPerSecond { get; set; }
        public double Curviness { get; set; }
        public bool IsConnectionLane { get; set; }
        public double StartFraction { get; set; }
        public double EndFraction { get; set; }
        public uint NavigationFlags { get; set; }
        public uint TrackFlags { get; set; }
        public double Ax { get; set; } public double Ay { get; set; } public double Az { get; set; }
        public double Bx { get; set; } public double By { get; set; } public double Bz { get; set; }
        public double Cx { get; set; } public double Cy { get; set; } public double Cz { get; set; }
        public double Dx { get; set; } public double Dy { get; set; } public double Dz { get; set; }
        public RailCheckpointId EndCheckpointId { get; set; }
    }

    public sealed class RailConsistSnapshot
    {
        public double LengthMetres { get; set; }
        public int UnitCount { get; set; }
        public RailConsistUnitSnapshot[] Units { get; set; } = Array.Empty<RailConsistUnitSnapshot>();
        public RailTrainPhysics Physics { get; set; } = new RailTrainPhysics();
    }

    public sealed class RailConsistUnitSnapshot
    {
        public RailEntityIdentity Entity { get; set; } = new RailEntityIdentity();
        public RailEntityIdentity Prefab { get; set; } = new RailEntityIdentity();
        public double LengthMetres { get; set; }
        public double FrontBogieOffsetMetres { get; set; }
        public double RearBogieOffsetMetres { get; set; }
        public double FrontAttachOffsetMetres { get; set; }
        public double RearAttachOffsetMetres { get; set; }
    }

    public sealed class RailVehicleSnapshot
    {
        public RailVehicleId VehicleId { get; set; }
        public RailEntityIdentity Entity { get; set; } = new RailEntityIdentity();
        public RailEntityIdentity Target { get; set; } = new RailEntityIdentity();
        public RailEntityIdentity Controller { get; set; } = new RailEntityIdentity();
        public RailEntityIdentity Line { get; set; } = new RailEntityIdentity();
        public double SpeedMetresPerSecond { get; set; }
        public bool IsBoarding { get; set; }
        public uint DepartureFrame { get; set; }
        public uint PathState { get; set; }
        public int PathElementIndex { get; set; }
        public ulong PathSignature { get; set; }
        public ulong ResourceSignature { get; set; }
        public ulong LineTrackChainSignature { get; set; }
        public int VehiclePriority { get; set; }
        public RailCurrentLaneSnapshot CurrentLane { get; set; } = new RailCurrentLaneSnapshot();
        public RailExternalBlockerKind ExternalBlockerKind { get; set; }
        public RailConsistSnapshot Consist { get; set; } = new RailConsistSnapshot();
        public RailPathSegment[] RemainingPath { get; set; } = Array.Empty<RailPathSegment>();
    }

    public sealed class RailLineTopologySnapshot
    {
        public RailEntityIdentity Line { get; set; } = new RailEntityIdentity();
        public ulong ChainSignature { get; set; }
        public int SegmentCount { get; set; }
    }

    public sealed class RailControlledHoldSnapshot
    {
        public RailControlledHoldKind Kind { get; set; }
        public uint EarliestReleaseFrame { get; set; }
        public RailVehicleId ReleaseVehicleId { get; set; }
        public RailLaneId ReleaseLaneId { get; set; }
        public double ReleaseLaneFraction { get; set; }
        public int ReleaseDirection { get; set; }
        public ulong TrackModelSignature { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
    }

    public sealed class RailCurrentLaneSnapshot
    {
        public RailLaneId FrontLaneId { get; set; }
        public double FrontPosition { get; set; }
        public RailLaneId RearLaneId { get; set; }
        public double RearPosition { get; set; }
        public RailLaneId FrontCacheLaneId { get; set; }
        public RailLaneId RearCacheLaneId { get; set; }
        public uint FrontFlags { get; set; }
        public uint RearFlags { get; set; }
    }

    public sealed class RailBlockerSnapshot { public RailVehicleId VehicleId { get; set; } public RailVehicleId BlockerVehicleId { get; set; } public RailBlockerType Type { get; set; } public byte MaximumSpeedCode { get; set; } public double MaximumSpeedMetresPerSecond { get; set; } }
    public sealed class RailReservationSnapshot { public RailResourceId ResourceId { get; set; } public RailVehicleId BlockerVehicleId { get; set; } public RailExternalBlockerKind ExternalBlockerKind { get; set; } public int PreviousPriority { get; set; } public double PreviousOffset { get; set; } public int NextPriority { get; set; } public double NextOffset { get; set; } public uint UpdateFrameIndex { get; set; } public bool HasUpdateFrame { get; set; } }
    public sealed class RailSignalSnapshot { public RailLaneId LaneId { get; set; } public RailVehicleId PetitionerVehicleId { get; set; } public RailVehicleId BlockerVehicleId { get; set; } public RailExternalBlockerKind PetitionerExternalKind { get; set; } public RailExternalBlockerKind BlockerExternalKind { get; set; } public RailLaneSignalType SignalType { get; set; } public int Priority { get; set; } public uint Flags { get; set; } }
    public sealed class RailLaneOccupancySnapshot { public RailLaneId LaneId { get; set; } public RailVehicleId VehicleId { get; set; } public double StartFraction { get; set; } public double EndFraction { get; set; } }
    public sealed class RailResourceApproachSnapshot { public RailLaneId LaneId { get; set; } public double StartFraction { get; set; } public double EndFraction { get; set; } public uint OverlapFlags { get; set; } public int PriorityDelta { get; set; } }
    public sealed class RailResourceSnapshot { public RailResourceId ResourceId { get; set; } public RailLaneId[] LaneIds { get; set; } = Array.Empty<RailLaneId>(); public RailResourceApproachSnapshot[] Approaches { get; set; } = Array.Empty<RailResourceApproachSnapshot>(); public RailVehicleId OccupantVehicleId { get; set; } public int PriorityDelta { get; set; } }

    public sealed class RailEtaWorldSnapshot
    {
        public RailEtaMode Mode { get; set; }
        public const int ContractMajor = 0;
        public const int ContractMinor = 7;
        public uint OriginFrame { get; set; }
        public uint NavigationPhase { get; set; }
        public long BatchId { get; set; }
        public int ServiceGeneration { get; set; }
        public bool ClosureValidated { get; set; }
        public uint SharedIndexVersion { get; set; }
        public int ScopeLineCount { get; set; }
        public RailLineTopologySnapshot[] Lines { get; set; } = Array.Empty<RailLineTopologySnapshot>();
        public RailVehicleSnapshot[] Vehicles { get; set; } = Array.Empty<RailVehicleSnapshot>();
        public RailBlockerSnapshot[] Blockers { get; set; } = Array.Empty<RailBlockerSnapshot>();
        public RailReservationSnapshot[] Reservations { get; set; } = Array.Empty<RailReservationSnapshot>();
        public RailSignalSnapshot[] Signals { get; set; } = Array.Empty<RailSignalSnapshot>();
        public RailLaneOccupancySnapshot[] Occupancies { get; set; } = Array.Empty<RailLaneOccupancySnapshot>();
        public RailResourceSnapshot[] Resources { get; set; } = Array.Empty<RailResourceSnapshot>();
    }

    public sealed class RailEtaRequest { public string RequestId { get; set; } = string.Empty; public RailEtaMode Mode { get; set; } public RailVehicleId VehicleId { get; set; } public RailCheckpointId TargetCheckpointId { get; set; } public RailEntityIdentity ExpectedTarget { get; set; } = new RailEntityIdentity(); }
    public sealed class RailEtaWorkspace
    {
        public int MaxEvents { get; set; } = 65536;
        public int MaxTraceEvents { get; set; } = 256;
        public int MaxDiagnostics { get; set; } = 32;
        public int MaxCheckpoints { get; set; } = 2048;
        public int MaxVehicles { get; set; } = 256;
        public int MaxResources { get; set; } = 2048;
        public int MaxBlockerDepth { get; set; } = 100;
    }
    public readonly struct RailEtaCancellation { private readonly Func<bool> m_IsCancellationRequested; public RailEtaCancellation(Func<bool> value) { m_IsCancellationRequested = value; } public bool IsCancellationRequested => m_IsCancellationRequested != null && m_IsCancellationRequested(); }
    public sealed class RailEtaCheckpointPrediction { public RailCheckpointId CheckpointId { get; set; } public uint ArrivalFrame { get; set; } }
    public sealed class RailEtaTraceEvent
    {
        public int Sequence { get; set; }
        public string Kind { get; set; } = string.Empty;
        public RailVehicleId VehicleId { get; set; }
        public RailVehicleId OtherVehicleId { get; set; }
        public RailResourceId ResourceId { get; set; }
        public uint StartFrame { get; set; }
        public uint EndFrame { get; set; }
        public uint DelayFrames { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public RailEtaBlockerEvidence StartEvidence { get; set; }
        public RailEtaBlockerEvidence EndEvidence { get; set; }
    }
    public sealed class RailEtaBlockerEvidence
    {
        public int Source { get; set; }
        public long BlockerEntityId { get; set; }
        public RailLaneId TargetLaneId { get; set; }
        public double TargetPosition { get; set; }
        public RailLaneId CheckedLaneId { get; set; }
        public RailLaneId OtherLaneId { get; set; }
        public RailLaneId BlockerFrontLaneId { get; set; }
        public double BlockerFrontPosition { get; set; }
        public RailLaneId BlockerRearLaneId { get; set; }
        public double BlockerRearPosition { get; set; }
        public RailCheckpointId BlockerTargetId { get; set; }
        public bool BlockerBoarding { get; set; }
        public double OccupancyStart { get; set; }
        public double OccupancyEnd { get; set; }
        public int ReservationPriority { get; set; }
        public double ReservationOffset { get; set; }
        public int OverlapFlags { get; set; }
        public double OverlapThisStart { get; set; }
        public double OverlapThisEnd { get; set; }
        public double OverlapOtherStart { get; set; }
        public double OverlapOtherEnd { get; set; }
        public int PriorityDelta { get; set; }
        public double Parallelism { get; set; }
        public double Distance { get; set; }
        public double DistanceFactor { get; set; }
        public double DistanceOffset { get; set; }
        public double SpeedBefore { get; set; }
        public double LimitedSpeed { get; set; }
    }
    public sealed class RailEtaDiagnosticRecord
    {
        public string Code { get; set; } = string.Empty;
        public RailEtaDiagnosticSeverity Severity { get; set; }
        public string Message { get; set; } = string.Empty;
        public RailVehicleId VehicleId { get; set; }
        public RailResourceId ResourceId { get; set; }
        public uint Frame { get; set; }
        public double NumericValue { get; set; }
    }
    public sealed class RailEtaStageTiming
    {
        public string Code { get; set; } = string.Empty;
        public long WallTicks { get; set; }
        public double WallMilliseconds { get; set; }
        public int InputCount { get; set; }
        public long AllocationBytes { get; set; }
    }
    public sealed class RailEtaInputScale
    {
        public int VehicleCount { get; set; }
        public int PathSegmentCount { get; set; }
        public int BlockerCount { get; set; }
        public int ReservationCount { get; set; }
        public int SignalCount { get; set; }
        public int OccupancyCount { get; set; }
        public int ResourceCount { get; set; }
        public int EventCount { get; set; }
        public int CheckpointCount { get; set; }
    }
    public sealed class RailEtaPrediction
    {
        public string RequestId { get; set; } = string.Empty;
        public RailEtaConfidence Confidence { get; set; }
        public RailEtaFailure Failure { get; set; }
        public uint PredictedArrivalFrame { get; set; }
        public RailEtaCheckpointPrediction[] Checkpoints { get; set; } = Array.Empty<RailEtaCheckpointPrediction>();
        public string PredictorSource { get; set; } = string.Empty;
        public string PredictorBuildId { get; set; } = string.Empty;
        public long PredictorGeneration { get; set; }
        public bool TraceTruncated { get; set; }
        public int EventCount { get; set; }
        public double WorkerMilliseconds { get; set; }
        public RailEtaInputScale InputScale { get; set; } = new RailEtaInputScale();
        public RailEtaTraceEvent[] Trace { get; set; } = Array.Empty<RailEtaTraceEvent>();
        public RailEtaDiagnosticRecord[] Diagnostics { get; set; } = Array.Empty<RailEtaDiagnosticRecord>();
        public RailEtaStageTiming[] StageTimings { get; set; } = Array.Empty<RailEtaStageTiming>();
        public string Reason { get; set; } = string.Empty;
    }
}
