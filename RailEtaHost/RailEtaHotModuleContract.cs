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

    public sealed class RailEtaTheorySegmentRequest
    {
        public int SegmentIndex { get; set; }
        public int PathSlotIndex { get; set; }
        public int FromWaypointIndex { get; set; }
        public int FromWaypointVersion { get; set; }
        public int ToWaypointIndex { get; set; }
        public int ToWaypointVersion { get; set; }
        public int SegmentFromWaypointIndex { get; set; }
        public int SegmentToWaypointIndex { get; set; }
    }

    public sealed class RailEtaTheorySegmentResult
    {
        public int SegmentIndex { get; set; }
        public int FromWaypointIndex { get; set; }
        public int ToWaypointIndex { get; set; }
        public uint SegmentFrames { get; set; }
        public ulong RouteSignature { get; set; }
        public ulong PathSignature { get; set; }
        public ulong ModelSignature { get; set; }
        public int PathSourceElementCount { get; set; }
        public int PathSkippedElementCount { get; set; }
        public RailEtaTheoryPathFact[] PathFacts { get; set; } = Array.Empty<RailEtaTheoryPathFact>();
        public string State { get; set; } = string.Empty;
        public string Failure { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public sealed class RailEtaTheoryPathFact
    {
        public int PathSlotIndex { get; set; }
        public int PathOwnerIndex { get; set; }
        public int PathOwnerVersion { get; set; }
        public int PathElementIndex { get; set; } = -1;
        public int PathOwnerStable { get; set; }
        public int PreviousLaneIndex { get; set; } = -1;
        public int PreviousLaneVersion { get; set; }
        public int NextLaneIndex { get; set; } = -1;
        public int NextLaneVersion { get; set; }
        public int FromRouteLaneSide { get; set; } = -1;
        public int ToRouteLaneSide { get; set; } = -1;
        public int Direction { get; set; }
        public int FromWaypointIndex { get; set; }
        public int FromWaypointVersion { get; set; }
        public int FromWaypointEntityIndex { get; set; }
        public int FromWaypointEntityVersion { get; set; }
        public int ToWaypointIndex { get; set; }
        public int ToWaypointVersion { get; set; }
        public int ToWaypointEntityIndex { get; set; }
        public int ToWaypointEntityVersion { get; set; }
        public int FromRouteLanePresent { get; set; }
        public int FromStartLaneIndex { get; set; }
        public int FromStartLaneVersion { get; set; }
        public int FromEndLaneIndex { get; set; }
        public int FromEndLaneVersion { get; set; }
        public float FromStartCurve { get; set; }
        public float FromEndCurve { get; set; }
        public int ToRouteLanePresent { get; set; }
        public int ToStartLaneIndex { get; set; }
        public int ToStartLaneVersion { get; set; }
        public int ToEndLaneIndex { get; set; }
        public int ToEndLaneVersion { get; set; }
        public float ToStartCurve { get; set; }
        public float ToEndCurve { get; set; }
        public int PathElementsPresent { get; set; }
        public int PathElementCount { get; set; }
        public ulong PathElementsSignature { get; set; }
        public ulong RouteNetworkSignature { get; set; }
        public int LaneIndex { get; set; }
        public int LaneVersion { get; set; }
        public int Kind { get; set; }
        public float StartFraction { get; set; }
        public float EndFraction { get; set; }
        public float CurveLength { get; set; }
        public float Length { get; set; }
        public uint PathFlags { get; set; }
        public uint TrackFlags { get; set; }
        public uint ConnectionFlags { get; set; }
        public uint ConnectionTrackTypes { get; set; }
        public uint ConnectionRoadTypes { get; set; }
        public int AccessRestrictionIndex { get; set; } = -1;
        public int AccessRestrictionVersion { get; set; }
        public float EdgeDeltaStart { get; set; }
        public float EdgeDeltaEnd { get; set; }
        public int EdgeConnectedStartCount { get; set; }
        public int EdgeConnectedEndCount { get; set; }
        public float CurveAX { get; set; }
        public float CurveAY { get; set; }
        public float CurveAZ { get; set; }
        public float CurveBX { get; set; }
        public float CurveBY { get; set; }
        public float CurveBZ { get; set; }
        public float CurveCX { get; set; }
        public float CurveCY { get; set; }
        public float CurveCZ { get; set; }
        public float CurveDX { get; set; }
        public float CurveDY { get; set; }
        public float CurveDZ { get; set; }
        public float SpeedLimit { get; set; }
        public float Curviness { get; set; }
    }

    public sealed class RailEtaTheoryFailure
    {
        public int SegmentIndex { get; set; } = -1;
        public int FromWaypointIndex { get; set; } = -1;
        public int ToWaypointIndex { get; set; } = -1;
        public string Failure { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public static class RailEtaTheorySignatures
    {
        public const int MaxPathFacts = 8192;
        public static ulong Seed => 1469598103934665603UL;

        public static ulong Mix(ulong hash, int value)
        {
            unchecked { return (hash ^ (uint)value) * 1099511628211UL; }
        }

        public static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                hash = Mix(hash, (int)value);
                return Mix(hash, (int)(value >> 32));
            }
        }

        public static ulong Mix(ulong hash, float value) => Mix(hash, value.GetHashCode());

        public static ulong Mix(ulong hash, string value)
        {
            value = value ?? string.Empty;
            hash = Mix(hash, value.Length);
            for (int i = 0; i < value.Length; i++) hash = Mix(hash, value[i]);
            return hash;
        }

        public static ulong MixPathElement(ulong hash, int index, int targetIndex, int targetVersion,
            float startFraction, float endFraction, int flags)
        {
            hash = Mix(hash, index);
            hash = Mix(hash, targetIndex);
            hash = Mix(hash, targetVersion);
            hash = Mix(hash, startFraction);
            hash = Mix(hash, endFraction);
            return Mix(hash, flags);
        }

        public static ulong RouteNetworkSignature(RailEtaTheoryPathFact fact)
        {
            if (fact == null) return 0;
            ulong hash = Seed;
            hash = Mix(hash, fact.PathSlotIndex);
            hash = Mix(hash, fact.PathOwnerIndex);
            hash = Mix(hash, fact.PathOwnerVersion);
            hash = Mix(hash, fact.FromWaypointIndex);
            hash = Mix(hash, fact.FromWaypointVersion);
            hash = Mix(hash, fact.FromWaypointEntityIndex);
            hash = Mix(hash, fact.FromWaypointEntityVersion);
            hash = Mix(hash, fact.ToWaypointIndex);
            hash = Mix(hash, fact.ToWaypointVersion);
            hash = Mix(hash, fact.ToWaypointEntityIndex);
            hash = Mix(hash, fact.ToWaypointEntityVersion);
            hash = Mix(hash, fact.FromRouteLanePresent);
            hash = Mix(hash, fact.FromStartLaneIndex);
            hash = Mix(hash, fact.FromStartLaneVersion);
            hash = Mix(hash, fact.FromEndLaneIndex);
            hash = Mix(hash, fact.FromEndLaneVersion);
            hash = Mix(hash, fact.FromStartCurve);
            hash = Mix(hash, fact.FromEndCurve);
            hash = Mix(hash, fact.ToRouteLanePresent);
            hash = Mix(hash, fact.ToStartLaneIndex);
            hash = Mix(hash, fact.ToStartLaneVersion);
            hash = Mix(hash, fact.ToEndLaneIndex);
            hash = Mix(hash, fact.ToEndLaneVersion);
            hash = Mix(hash, fact.ToStartCurve);
            hash = Mix(hash, fact.ToEndCurve);
            hash = Mix(hash, fact.PathElementsPresent);
            hash = Mix(hash, fact.PathElementCount);
            return Mix(hash, fact.PathElementsSignature);
        }

        public static ulong MixPath(ulong hash, int segmentIndex, int fromWaypointIndex, int toWaypointIndex,
            int sourceElementCount, int skippedElementCount, RailEtaTheoryPathFact[] facts)
        {
            hash = Mix(hash, segmentIndex);
            hash = Mix(hash, fromWaypointIndex);
            hash = Mix(hash, toWaypointIndex);
            hash = Mix(hash, sourceElementCount);
            hash = Mix(hash, skippedElementCount);
            hash = Mix(hash, facts?.Length ?? 0);
            if (facts == null) return hash;
            for (int i = 0; i < facts.Length; i++)
            {
                RailEtaTheoryPathFact fact = facts[i];
                if (fact == null)
                {
                    hash = Mix(hash, -1);
                    continue;
                }
                hash = Mix(hash, i);
                hash = Mix(hash, fact.PathSlotIndex);
                hash = Mix(hash, fact.PathOwnerIndex);
                hash = Mix(hash, fact.PathOwnerVersion);
                hash = Mix(hash, fact.PathElementIndex);
                hash = Mix(hash, fact.PathOwnerStable);
                hash = Mix(hash, fact.PreviousLaneIndex);
                hash = Mix(hash, fact.PreviousLaneVersion);
                hash = Mix(hash, fact.NextLaneIndex);
                hash = Mix(hash, fact.NextLaneVersion);
                hash = Mix(hash, fact.FromRouteLaneSide);
                hash = Mix(hash, fact.ToRouteLaneSide);
                hash = Mix(hash, fact.Direction);
                hash = Mix(hash, fact.FromWaypointIndex);
                hash = Mix(hash, fact.FromWaypointVersion);
                hash = Mix(hash, fact.FromWaypointEntityIndex);
                hash = Mix(hash, fact.FromWaypointEntityVersion);
                hash = Mix(hash, fact.ToWaypointIndex);
                hash = Mix(hash, fact.ToWaypointVersion);
                hash = Mix(hash, fact.ToWaypointEntityIndex);
                hash = Mix(hash, fact.ToWaypointEntityVersion);
                hash = Mix(hash, fact.FromRouteLanePresent);
                hash = Mix(hash, fact.FromStartLaneIndex);
                hash = Mix(hash, fact.FromStartLaneVersion);
                hash = Mix(hash, fact.FromEndLaneIndex);
                hash = Mix(hash, fact.FromEndLaneVersion);
                hash = Mix(hash, fact.FromStartCurve);
                hash = Mix(hash, fact.FromEndCurve);
                hash = Mix(hash, fact.ToRouteLanePresent);
                hash = Mix(hash, fact.ToStartLaneIndex);
                hash = Mix(hash, fact.ToStartLaneVersion);
                hash = Mix(hash, fact.ToEndLaneIndex);
                hash = Mix(hash, fact.ToEndLaneVersion);
                hash = Mix(hash, fact.ToStartCurve);
                hash = Mix(hash, fact.ToEndCurve);
                hash = Mix(hash, fact.PathElementsPresent);
                hash = Mix(hash, fact.PathElementCount);
                hash = Mix(hash, fact.PathElementsSignature);
                hash = Mix(hash, fact.RouteNetworkSignature);
                hash = Mix(hash, fact.LaneIndex);
                hash = Mix(hash, fact.LaneVersion);
                hash = Mix(hash, fact.Kind);
                hash = Mix(hash, fact.StartFraction);
                hash = Mix(hash, fact.EndFraction);
                hash = Mix(hash, fact.CurveLength);
                hash = Mix(hash, fact.Length);
                hash = Mix(hash, (ulong)fact.PathFlags);
                hash = Mix(hash, (ulong)fact.TrackFlags);
                hash = Mix(hash, (ulong)fact.ConnectionFlags);
                hash = Mix(hash, (ulong)fact.ConnectionTrackTypes);
                hash = Mix(hash, (ulong)fact.ConnectionRoadTypes);
                hash = Mix(hash, fact.AccessRestrictionIndex);
                hash = Mix(hash, fact.AccessRestrictionVersion);
                hash = Mix(hash, fact.EdgeDeltaStart);
                hash = Mix(hash, fact.EdgeDeltaEnd);
                hash = Mix(hash, fact.EdgeConnectedStartCount);
                hash = Mix(hash, fact.EdgeConnectedEndCount);
                hash = Mix(hash, fact.CurveAX);
                hash = Mix(hash, fact.CurveAY);
                hash = Mix(hash, fact.CurveAZ);
                hash = Mix(hash, fact.CurveBX);
                hash = Mix(hash, fact.CurveBY);
                hash = Mix(hash, fact.CurveBZ);
                hash = Mix(hash, fact.CurveCX);
                hash = Mix(hash, fact.CurveCY);
                hash = Mix(hash, fact.CurveCZ);
                hash = Mix(hash, fact.CurveDX);
                hash = Mix(hash, fact.CurveDY);
                hash = Mix(hash, fact.CurveDZ);
                hash = Mix(hash, fact.SpeedLimit);
                hash = Mix(hash, fact.Curviness);
            }
            return hash;
        }
    }

    public readonly struct RailEtaHotCommand
    {
        public RailEtaHotCommand(long ticket, int generation, int vehicleIndex, int vehicleVersion, long targetWaypoint, RailEtaMode mode,
            int depotIndex = 0, int depotVersion = 0, int modelIndex = 0, int modelVersion = 0,
            int secondaryModelIndex = 0, int secondaryModelVersion = 0,
            RailEtaTheorySegmentRequest[] theorySegments = null,
            ulong routeSignature = 0,
            ulong pathSignature = 0,
            ulong modelSignature = 0)
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
            TheorySegments = theorySegments ?? Array.Empty<RailEtaTheorySegmentRequest>();
            RouteSignature = routeSignature;
            PathSignature = pathSignature;
            ModelSignature = modelSignature;
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
        public RailEtaTheorySegmentRequest[] TheorySegments { get; }
        public ulong RouteSignature { get; }
        public ulong PathSignature { get; }
        public ulong ModelSignature { get; }
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
        public RailEtaTheorySegmentResult[] TheorySegments { get; set; } = Array.Empty<RailEtaTheorySegmentResult>();
        public RailEtaTheoryFailure TheoryFailure { get; set; }
        public ulong RouteSignature { get; set; }
        public ulong PathSignature { get; set; }
        public ulong ModelSignature { get; set; }
        public string ComparisonSummary { get; set; } = string.Empty;
    }
}
