using System;
using RapidTransitMod.Core;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.RailEtaHost
{
    public enum RailEtaMode : byte
    {
        // Existing authoritative closure: related lines, blockers, reservations, signals and overlaps.
        Full = 0,
        // Request-frame target path plus trains currently occupying the same physical lanes.
        PathOccupants = 1,
        // Target train and path physics only; all blocking and dispatch facts are intentionally absent.
        Theory = 2
    }

    public interface IRailEtaHotModule : IDisposable
    {
        string BuildId { get; }
        bool Busy { get; }
        bool NeedsTick { get; }
        void Attach(RailEtaHotContext context);
        void Submit(RailEtaHotCommand command);
        JobHandle Tick(uint simulationFrame, JobHandle inputDependency);
        bool PrepareForReload(out long ticket, out string summary);
        bool TryGetComparisonSummary(long ticket, out string summary);
        void Cancel(long ticket);
        void Clear(int generation);
    }

    public sealed class RailEtaHotContext
    {
        public RailEtaHotContext(
            World world,
            Func<uint> simulationFrame,
            object railTravel,
            RailEtaRuntimeReadPort runtimeReadPort,
            RailEtaWorker worker,
            Action<RailEtaPublicResult> publishResult,
            Action<string> log)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            SimulationFrame = simulationFrame ?? throw new ArgumentNullException(nameof(simulationFrame));
            RailTravel = railTravel ?? throw new ArgumentNullException(nameof(railTravel));
            RuntimeReadPort = runtimeReadPort ?? throw new ArgumentNullException(nameof(runtimeReadPort));
            Worker = worker ?? throw new ArgumentNullException(nameof(worker));
            PublishResult = publishResult ?? throw new ArgumentNullException(nameof(publishResult));
            Log = log ?? (_ => { });
        }

        public World World { get; }
        public Func<uint> SimulationFrame { get; }
        public object RailTravel { get; }
        public RailEtaRuntimeReadPort RuntimeReadPort { get; }
        public RailEtaWorker Worker { get; }
        public Action<RailEtaPublicResult> PublishResult { get; }
        public Action<string> Log { get; }
    }

    public sealed class RailEtaRuntimeReadPort
    {
        public Func<ClockSnapshot> ClockSnapshot { get; set; }
        public Func<Entity, int> LineDwellMinutes { get; set; }
        public TryReadRailEtaOriginScheduledHold TryReadOriginScheduledHold { get; set; }
        public TryReadRailEtaHold TryReadHold { get; set; }
        public TryReadRailEtaTrackChain TryReadTrackChain { get; set; }
    }

    public delegate bool TryReadRailEtaOriginScheduledHold(Entity vehicle, uint frame, out uint earliestReleaseFrame);
    public delegate bool TryReadRailEtaHold(Entity vehicle, uint frame, out RailEtaRuntimeHoldFact fact);
    public delegate bool TryReadRailEtaTrackChain(Entity line, out RailEtaRuntimeTrackChainFact fact);

    public struct RailEtaRuntimeHoldFact
    {
        public Entity ReleaseVehicle;
        public Entity ReleaseLine;
        public float ReleaseCoordinate;
        public int IntervalStartAtomIndex;
        public int IntervalEndAtomIndexExclusive;
        public ulong ExpectedChainSignature;
    }

    public sealed class RailEtaRuntimeTrackChainFact
    {
        public Entity Line { get; set; }
        public ulong Signature { get; set; }
        public RailEtaRuntimeTrackAtomFact[] Atoms { get; set; } = Array.Empty<RailEtaRuntimeTrackAtomFact>();
    }

    public struct RailEtaRuntimeTrackAtomFact
    {
        public Entity PhysicalLane;
        public Entity PreviousTarget;
        public Entity NextTarget;
        public float Start;
        public float End;
        public uint SourceFlags;
        public byte AtomClass;
        public sbyte Direction;
    }

    public readonly struct RailEtaHotCommand
    {
        public RailEtaHotCommand(long ticket, int generation, int vehicleIndex, int vehicleVersion, long targetWaypoint, RailEtaMode mode,
            int depotIndex = 0, int depotVersion = 0, int modelIndex = 0, int modelVersion = 0,
            int secondaryModelIndex = 0, int secondaryModelVersion = 0)
        {
            Ticket = ticket;
            Generation = generation;
            VehicleIndex = vehicleIndex;
            VehicleVersion = vehicleVersion;
            TargetWaypoint = targetWaypoint;
            Mode = mode;
            DepotIndex = depotIndex;
            DepotVersion = depotVersion;
            ModelIndex = modelIndex;
            ModelVersion = modelVersion;
            SecondaryModelIndex = secondaryModelIndex;
            SecondaryModelVersion = secondaryModelVersion;
        }

        public long Ticket { get; }
        public int Generation { get; }
        public int VehicleIndex { get; }
        public int VehicleVersion { get; }
        public long TargetWaypoint { get; }
        public RailEtaMode Mode { get; }
        public int DepotIndex { get; }
        public int DepotVersion { get; }
        public int ModelIndex { get; }
        public int ModelVersion { get; }
        public int SecondaryModelIndex { get; }
        public int SecondaryModelVersion { get; }
    }

    public sealed class RailEtaPublicResult
    {
        public long Ticket { get; set; }
        public string State { get; set; } = "Idle";
        public string Failure { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public long TargetVehicle { get; set; }
        public long TargetWaypoint { get; set; }
        public uint EtaFrame { get; set; }
        public uint OriginFrame { get; set; }
        public string Source { get; set; } = "hot";
        public string Build { get; set; } = string.Empty;
        public long Generation { get; set; }
        public bool Incomplete { get; set; }
        public RailEtaMode Mode { get; set; }
        public string ComparisonSummary { get; set; } = string.Empty;
    }
}
