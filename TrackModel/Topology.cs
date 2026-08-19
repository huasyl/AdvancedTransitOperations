using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using RapidTransitMod.Bypass;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal enum TrackTraversalDir : byte
    {
        Unknown = 0,
        Forward = 1,
        Reverse = 2,
    }

    internal enum SharedTraversalRelation : byte
    {
        Unknown = 0,
        SameDirection = 1,
        OppositeDirection = 2,
    }

    internal enum RelativeToTrunkState : byte
    {
        Unknown = 0,
        OffTrunk = 1,
        OnTrunkAlongCanonical = 2,
        OnTrunkAgainstCanonical = 3,
        ApproachingTrunkAlongCanonical = 4,
        ApproachingTrunkAgainstCanonical = 5,
        DepartingFromTrunk = 6,
        FutureReturnOnly = 7,
    }

    internal enum TrackAtomClass : byte
    {
        Unknown = 0,
        PrimaryLane = 1,
        ConnectionHelper = 2,
        FilteredNoise = 3,
    }

    internal enum ControlPointKind : byte
    {
        Unknown = 0,
        Stop = 1,
        Bypass = 2,
        Branch = 3,
        Merge = 4,
        SharedEntry = 5,
        SharedExit = 6,
    }

    internal readonly struct TrackAtomKey : IEquatable<TrackAtomKey>
    {
        public readonly Entity PhysicalLaneKey;
        public readonly Entity PreviousTarget;
        public readonly Entity NextTarget;

        public TrackAtomKey(Entity physicalLaneKey, Entity previousTarget, Entity nextTarget)
        {
            PhysicalLaneKey = physicalLaneKey;
            PreviousTarget = previousTarget;
            NextTarget = nextTarget;
        }

        public bool Equals(TrackAtomKey other)
        {
            return PhysicalLaneKey == other.PhysicalLaneKey
                && PreviousTarget == other.PreviousTarget
                && NextTarget == other.NextTarget;
        }

        public override bool Equals(object obj)
        {
            return obj is TrackAtomKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = PhysicalLaneKey.GetHashCode();
                hashCode = (hashCode * 397) ^ PreviousTarget.GetHashCode();
                hashCode = (hashCode * 397) ^ NextTarget.GetHashCode();
                return hashCode;
            }
        }

        public override string ToString()
        {
            string previous = PreviousTarget == Entity.Null ? "null" : PreviousTarget.Index.ToString();
            string next = NextTarget == Entity.Null ? "null" : NextTarget.Index.ToString();
            return previous + "->" + PhysicalLaneKey.Index + "->" + next;
        }
    }

    internal readonly struct TrackAtom
    {
        public readonly TrackAtomKey Key;
        public readonly Entity SourceTarget;
        public readonly float2 TargetDelta;
        public readonly PathElementFlags SourceFlags;
        public readonly TrackAtomClass AtomClass;
        public readonly TrackTraversalDir TraversalDir;

        public TrackAtom(
            TrackAtomKey key,
            Entity sourceTarget,
            float2 targetDelta,
            PathElementFlags sourceFlags,
            TrackAtomClass atomClass,
            TrackTraversalDir traversalDir)
        {
            Key = key;
            SourceTarget = sourceTarget;
            TargetDelta = targetDelta;
            SourceFlags = sourceFlags;
            AtomClass = atomClass;
            TraversalDir = traversalDir;
        }
    }

    internal readonly struct TrackSegmentRange
    {
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;

        public TrackSegmentRange(int startAtomIndex, int endAtomIndexExclusive)
        {
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
        }
    }

    internal readonly struct ControlPointMarker
    {
        public readonly int AtomIndex;
        public readonly int WaypointIndex;
        public readonly Entity Building;
        public readonly ControlPointKind Kind;

        public ControlPointMarker(int atomIndex, int waypointIndex, Entity building, ControlPointKind kind)
        {
            AtomIndex = atomIndex;
            WaypointIndex = waypointIndex;
            Building = building;
            Kind = kind;
        }
    }

    internal readonly struct EndpointMarker
    {
        public readonly int AtomIndex;
        public readonly int WaypointIndex;
        public readonly Entity Waypoint;
        public readonly Entity OutsideConnection;
        public readonly RouteWaypointEndpointKind Kind;
        public readonly RouteWaypointEndpointDirection Direction;

        public EndpointMarker(
            int atomIndex,
            int waypointIndex,
            Entity waypoint,
            Entity outsideConnection,
            RouteWaypointEndpointKind kind,
            RouteWaypointEndpointDirection direction)
        {
            AtomIndex = atomIndex;
            WaypointIndex = waypointIndex;
            Waypoint = waypoint;
            OutsideConnection = outsideConnection;
            Kind = kind;
            Direction = direction;
        }
    }

    internal readonly struct ControlEdge
    {
        public readonly int StartControlPointIndex;
        public readonly int EndControlPointIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly float BaseFrames;

        public ControlEdge(
            int startControlPointIndex,
            int endControlPointIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            float baseFrames)
        {
            StartControlPointIndex = startControlPointIndex;
            EndControlPointIndex = endControlPointIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            BaseFrames = baseFrames;
        }
    }

    internal enum TraversalEventKind : byte
    {
        Unknown = 0,
        Stop = 1,
        Pass = 2,
        ApproachSplitBoundary = 3,
        DepartureSplitBoundary = 4,
        OutsideEndpointBoundary = 5,
        BreakBoundary = 6,
    }

    internal readonly struct TraversalEvent
    {
        public readonly int EventIndex;
        public readonly TraversalEventKind Kind;
        public readonly Entity Building;
        public readonly int WaypointIndex;
        public readonly int PassIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly float StopFrames;
        public readonly string StationId;

        public TraversalEvent(
            int eventIndex,
            TraversalEventKind kind,
            Entity building,
            int waypointIndex,
            int passIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            float stopFrames,
            string stationId = "")
        {
            EventIndex = eventIndex;
            Kind = kind;
            Building = building;
            WaypointIndex = waypointIndex;
            PassIndex = passIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            StopFrames = stopFrames;
            StationId = stationId ?? string.Empty;
        }
    }

    internal readonly struct TraversalRunSlice
    {
        public readonly int SliceIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int StartEventIndex;
        public readonly int EndEventIndex;
        public readonly Entity[] PhysicalLaneKeys;
        public readonly float RunFrames;

        public TraversalRunSlice(
            int sliceIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int startEventIndex,
            int endEventIndex,
            Entity[] physicalLaneKeys,
            float runFrames)
        {
            SliceIndex = sliceIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            StartEventIndex = startEventIndex;
            EndEventIndex = endEventIndex;
            PhysicalLaneKeys = physicalLaneKeys ?? Array.Empty<Entity>();
            RunFrames = runFrames;
        }
    }

    internal sealed class LineTraversalProfile
    {
        public readonly List<TraversalEvent> Events = new List<TraversalEvent>();
        public readonly List<TraversalRunSlice> RunSlices = new List<TraversalRunSlice>();
        public int[] AtomToRunSliceIndex = Array.Empty<int>();
        public float[][] SegmentSliceCutPointProgresses = Array.Empty<float[]>();
    }

    internal readonly struct TurnbackBoundary
    {
        public readonly int AtomIndex;
        public readonly int BeforeSliceIndex;
        public readonly int AfterSliceIndex;
        public readonly int BoundaryEventIndex;
        public readonly bool IsLearned;
        public readonly int MatchedAtomCount;
        public readonly int MatchedUniqueLaneCount;

        public TurnbackBoundary(
            int atomIndex,
            int beforeSliceIndex,
            int afterSliceIndex,
            int boundaryEventIndex,
            bool isLearned,
            int matchedAtomCount,
            int matchedUniqueLaneCount)
        {
            AtomIndex = atomIndex;
            BeforeSliceIndex = beforeSliceIndex;
            AfterSliceIndex = afterSliceIndex;
            BoundaryEventIndex = boundaryEventIndex;
            IsLearned = isLearned;
            MatchedAtomCount = matchedAtomCount;
            MatchedUniqueLaneCount = matchedUniqueLaneCount;
        }
    }

    internal readonly struct RunChartTurnbackRegion
    {
        public readonly int BoundaryAtomIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;

        public RunChartTurnbackRegion(
            int boundaryAtomIndex,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            BoundaryAtomIndex = boundaryAtomIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
        }
    }

    internal readonly struct TrackTurnbackStationBoundary
    {
        public readonly Entity StationEntity;
        public readonly int WaypointIndex;
        public readonly int AtomIndex;
        public readonly int BoundaryEventIndex;

        public TrackTurnbackStationBoundary(
            Entity stationEntity,
            int waypointIndex,
            int atomIndex,
            int boundaryEventIndex)
        {
            StationEntity = stationEntity;
            WaypointIndex = waypointIndex;
            AtomIndex = atomIndex;
            BoundaryEventIndex = boundaryEventIndex;
        }
    }

    internal readonly struct TraversalTimingEstimate
    {
        public readonly float RunFrames;
        public readonly float StopFrames;
        public readonly float TotalFrames;

        public TraversalTimingEstimate(float runFrames, float stopFrames)
        {
            RunFrames = runFrames;
            StopFrames = stopFrames;
            TotalFrames = runFrames + stopFrames;
        }
    }

    internal sealed class LineTrackChain
    {
        public Entity LineEntity;
        public ulong Signature;
        public ulong TraversalSignature;
        public bool ChainComplete;
        public List<TrackAtom> TrackAtoms = new List<TrackAtom>();
        public Entity[] AtomStationBuildings = Array.Empty<Entity>();
        public Dictionary<Entity, List<int>> AtomIndicesByLane = new Dictionary<Entity, List<int>>();
        public List<TrackSegmentRange> SegmentRanges = new List<TrackSegmentRange>();
        public List<ControlPointMarker> ControlPoints = new List<ControlPointMarker>();
        public List<EndpointMarker> EndpointMarkers = new List<EndpointMarker>();
        public List<ControlEdge> ControlEdges = new List<ControlEdge>();
        public LineTraversalProfile TraversalProfile = new LineTraversalProfile();
        public List<SharedTrackRun> SharedRuns = new List<SharedTrackRun>();
        public Dictionary<Entity, List<SharedTrackRun>> SharedRunsByOtherLine = new Dictionary<Entity, List<SharedTrackRun>>();
        public List<ControlEdgeSharedSpan> ControlEdgeSharedSpans = new List<ControlEdgeSharedSpan>();
        public List<BypassProtectedInterval> BypassProtectedIntervals = new List<BypassProtectedInterval>();
        public List<ProtectedSharedInterval> ProtectedSharedIntervals = new List<ProtectedSharedInterval>();
        public List<ProtectedIntervalSummary> ProtectedIntervalSummaries = new List<ProtectedIntervalSummary>();
        public List<TurnbackBoundary> TurnbackBoundaries = new List<TurnbackBoundary>();
        public List<RunChartTurnbackRegion> RunChartTurnbackRegions = new List<RunChartTurnbackRegion>();
        public LocalBypassWaypointSceneBinding[] LocalBypassWaypointScenes = Array.Empty<LocalBypassWaypointSceneBinding>();
        public uint LocalBypassWaypointScenesVersion;
        public uint SharedRunsVersion;
        public uint BypassPipelineReadyVersion;
        public bool ControlEdgeSharedSpansReady;
        public bool BypassProtectedIntervalsReady;
        public bool ProtectedSharedIntervalsReady;
        public bool ProtectedIntervalSummariesReady;
        public string TurnbackBuildMode = string.Empty;
        public string TurnbackBuildNote = string.Empty;
        public int TurnbackBuildSegmentPairIndex = -1;
    }

    internal readonly struct DevSightLaneOccurrence
    {
        public readonly Entity LineEntity;
        public readonly LineTrackChain Chain;
        public readonly List<int> AtomIndices;

        public DevSightLaneOccurrence(Entity lineEntity, LineTrackChain chain, List<int> atomIndices)
        {
            LineEntity = lineEntity;
            Chain = chain;
            AtomIndices = atomIndices;
        }
    }

    internal readonly struct SharedTrackRun
    {
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly bool HasMirroredContext;
        public readonly int SharedLineCount;

        public SharedTrackRun(int startAtomIndex, int endAtomIndexExclusive, bool hasMirroredContext, int sharedLineCount)
        {
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            HasMirroredContext = hasMirroredContext;
            SharedLineCount = sharedLineCount;
        }
    }

    internal readonly struct ControlEdgeSharedSpan
    {
        public readonly int ControlEdgeIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly bool HasMirroredContext;
        public readonly int SharedLineCount;

        public ControlEdgeSharedSpan(int controlEdgeIndex, int startAtomIndex, int endAtomIndexExclusive, bool hasMirroredContext, int sharedLineCount)
        {
            ControlEdgeIndex = controlEdgeIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            HasMirroredContext = hasMirroredContext;
            SharedLineCount = sharedLineCount;
        }
    }

    internal readonly struct BypassProtectedInterval
    {
        public readonly int StartControlPointIndex;
        public readonly int EndControlPointIndex;
        public readonly int StartControlEdgeIndex;
        public readonly int EndControlEdgeIndexInclusive;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly float BaseFrames;

        public BypassProtectedInterval(int startControlPointIndex, int endControlPointIndex, int startControlEdgeIndex, int endControlEdgeIndexInclusive, int startAtomIndex, int endAtomIndexExclusive, float baseFrames)
        {
            StartControlPointIndex = startControlPointIndex;
            EndControlPointIndex = endControlPointIndex;
            StartControlEdgeIndex = startControlEdgeIndex;
            EndControlEdgeIndexInclusive = endControlEdgeIndexInclusive;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            BaseFrames = baseFrames;
        }
    }

    internal readonly struct ProtectedSharedInterval
    {
        public readonly int ProtectedIntervalIndex;
        public readonly int ControlEdgeIndex;
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly bool HasMirroredContext;
        public readonly int SharedLineCount;
        public readonly float EntryOffsetFrames;
        public readonly float ClearOffsetFrames;

        public ProtectedSharedInterval(int protectedIntervalIndex, int controlEdgeIndex, int startAtomIndex, int endAtomIndexExclusive, bool hasMirroredContext, int sharedLineCount, float entryOffsetFrames, float clearOffsetFrames)
        {
            ProtectedIntervalIndex = protectedIntervalIndex;
            ControlEdgeIndex = controlEdgeIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            HasMirroredContext = hasMirroredContext;
            SharedLineCount = sharedLineCount;
            EntryOffsetFrames = entryOffsetFrames;
            ClearOffsetFrames = clearOffsetFrames;
        }
    }

    internal readonly struct ProtectedIntervalSummary
    {
        public readonly int ProtectedIntervalIndex;
        public readonly int SharedSegmentCount;
        public readonly int MaxSharedLineCount;
        public readonly bool HasMirroredContext;
        public readonly float MinEntryOffsetFrames;
        public readonly float MaxClearOffsetFrames;

        public ProtectedIntervalSummary(int protectedIntervalIndex, int sharedSegmentCount, int maxSharedLineCount, bool hasMirroredContext, float minEntryOffsetFrames, float maxClearOffsetFrames)
        {
            ProtectedIntervalIndex = protectedIntervalIndex;
            SharedSegmentCount = sharedSegmentCount;
            MaxSharedLineCount = maxSharedLineCount;
            HasMirroredContext = hasMirroredContext;
            MinEntryOffsetFrames = minEntryOffsetFrames;
            MaxClearOffsetFrames = maxClearOffsetFrames;
        }
    }

    internal readonly struct TrunkSkeleton
    {
        public readonly int LocalSharedStartAtomIndex;
        public readonly int LocalSharedEndAtomIndexExclusive;
        public readonly int ExpressSharedStartAtomIndex;
        public readonly int ExpressSharedEndAtomIndexExclusive;
        public readonly int LocalAnchorStartAtomIndex;
        public readonly int LocalAnchorEndAtomIndexExclusive;
        public readonly int ExpressAnchorStartAtomIndex;
        public readonly int ExpressAnchorEndAtomIndexExclusive;
        public readonly int LocalSharedSliceCount;
        public readonly int ExpressSharedSliceCount;
        public readonly int LocalBridgedGapAtoms;
        public readonly int ExpressBridgedGapAtoms;
        public readonly int PhysicalOverlap;
        public readonly int OrderedRun;
        public readonly SharedTraversalRelation TraversalRelation;
        public readonly bool HasCanonicalDirection;
        public readonly bool LocalAlongCanonical;
        public readonly bool ExpressAlongCanonical;

        public TrunkSkeleton(
            int localSharedStartAtomIndex,
            int localSharedEndAtomIndexExclusive,
            int expressSharedStartAtomIndex,
            int expressSharedEndAtomIndexExclusive,
            int localAnchorStartAtomIndex,
            int localAnchorEndAtomIndexExclusive,
            int expressAnchorStartAtomIndex,
            int expressAnchorEndAtomIndexExclusive,
            int localSharedSliceCount,
            int expressSharedSliceCount,
            int localBridgedGapAtoms,
            int expressBridgedGapAtoms,
            int physicalOverlap,
            int orderedRun,
            SharedTraversalRelation traversalRelation,
            bool hasCanonicalDirection,
            bool localAlongCanonical,
            bool expressAlongCanonical)
        {
            LocalSharedStartAtomIndex = localSharedStartAtomIndex;
            LocalSharedEndAtomIndexExclusive = localSharedEndAtomIndexExclusive;
            ExpressSharedStartAtomIndex = expressSharedStartAtomIndex;
            ExpressSharedEndAtomIndexExclusive = expressSharedEndAtomIndexExclusive;
            LocalAnchorStartAtomIndex = localAnchorStartAtomIndex;
            LocalAnchorEndAtomIndexExclusive = localAnchorEndAtomIndexExclusive;
            ExpressAnchorStartAtomIndex = expressAnchorStartAtomIndex;
            ExpressAnchorEndAtomIndexExclusive = expressAnchorEndAtomIndexExclusive;
            LocalSharedSliceCount = localSharedSliceCount;
            ExpressSharedSliceCount = expressSharedSliceCount;
            LocalBridgedGapAtoms = localBridgedGapAtoms;
            ExpressBridgedGapAtoms = expressBridgedGapAtoms;
            PhysicalOverlap = physicalOverlap;
            OrderedRun = orderedRun;
            TraversalRelation = traversalRelation;
            HasCanonicalDirection = hasCanonicalDirection;
            LocalAlongCanonical = localAlongCanonical;
            ExpressAlongCanonical = expressAlongCanonical;
        }
    }

    internal readonly struct SharedRunBand
    {
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int SharedSliceCount;
        public readonly int BridgedGapAtoms;
        public readonly bool HasMirroredContext;
        public readonly int MaxSharedLineCount;

        public SharedRunBand(
            int startAtomIndex,
            int endAtomIndexExclusive,
            int sharedSliceCount,
            int bridgedGapAtoms,
            bool hasMirroredContext,
            int maxSharedLineCount)
        {
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            SharedSliceCount = sharedSliceCount;
            BridgedGapAtoms = bridgedGapAtoms;
            HasMirroredContext = hasMirroredContext;
            MaxSharedLineCount = maxSharedLineCount;
        }
    }

    internal readonly struct AtomWindowSlice
    {
        public readonly int StartAtomIndex;
        public readonly int EndAtomIndexExclusive;
        public readonly int SharedSliceCount;
        public readonly int BridgedGapAtoms;
        public readonly bool HasMirroredContext;
        public readonly int MaxSharedLineCount;

        public AtomWindowSlice(
            int startAtomIndex,
            int endAtomIndexExclusive,
            int sharedSliceCount,
            int bridgedGapAtoms,
            bool hasMirroredContext,
            int maxSharedLineCount)
        {
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            SharedSliceCount = sharedSliceCount;
            BridgedGapAtoms = bridgedGapAtoms;
            HasMirroredContext = hasMirroredContext;
            MaxSharedLineCount = maxSharedLineCount;
        }
    }

    internal readonly struct DirectedSharedPairSegment
    {
        public readonly int LocalStartAtomIndex;
        public readonly int LocalEndAtomIndexExclusive;
        public readonly int ExpressStartAtomIndex;
        public readonly int ExpressEndAtomIndexExclusive;
        public readonly int LocalSharedSliceCount;
        public readonly int ExpressSharedSliceCount;
        public readonly int LocalBridgedGapAtoms;
        public readonly int ExpressBridgedGapAtoms;
        public readonly int PhysicalOverlap;
        public readonly int OrderedRun;
        public readonly bool HasMirroredContext;
        public readonly int MaxSharedLineCount;
        public readonly SharedTraversalRelation TraversalRelation;
        public readonly bool HasCanonicalDirection;
        public readonly bool LocalAlongCanonical;
        public readonly bool ExpressAlongCanonical;

        public DirectedSharedPairSegment(
            int localStartAtomIndex,
            int localEndAtomIndexExclusive,
            int expressStartAtomIndex,
            int expressEndAtomIndexExclusive,
            int localSharedSliceCount,
            int expressSharedSliceCount,
            int localBridgedGapAtoms,
            int expressBridgedGapAtoms,
            int physicalOverlap,
            int orderedRun,
            bool hasMirroredContext,
            int maxSharedLineCount,
            SharedTraversalRelation traversalRelation,
            bool hasCanonicalDirection,
            bool localAlongCanonical,
            bool expressAlongCanonical)
        {
            LocalStartAtomIndex = localStartAtomIndex;
            LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
            ExpressStartAtomIndex = expressStartAtomIndex;
            ExpressEndAtomIndexExclusive = expressEndAtomIndexExclusive;
            LocalSharedSliceCount = localSharedSliceCount;
            ExpressSharedSliceCount = expressSharedSliceCount;
            LocalBridgedGapAtoms = localBridgedGapAtoms;
            ExpressBridgedGapAtoms = expressBridgedGapAtoms;
            PhysicalOverlap = physicalOverlap;
            OrderedRun = orderedRun;
            HasMirroredContext = hasMirroredContext;
            MaxSharedLineCount = maxSharedLineCount;
            TraversalRelation = traversalRelation;
            HasCanonicalDirection = hasCanonicalDirection;
            LocalAlongCanonical = localAlongCanonical;
            ExpressAlongCanonical = expressAlongCanonical;
        }
    }

    internal readonly struct TrunkPhaseAlignment : IEquatable<TrunkPhaseAlignment>
    {
        public readonly bool Available;
        public readonly int LocalTraversalPhaseIndex;
        public readonly int LocalPhaseStartAtomIndex;
        public readonly int LocalPhaseEndAtomExclusive;
        public readonly int ExpressTraversalPhaseIndex;
        public readonly int ExpressPhaseStartAtomIndex;
        public readonly int ExpressPhaseEndAtomExclusive;

        public TrunkPhaseAlignment(
            bool available,
            int localTraversalPhaseIndex,
            int localPhaseStartAtomIndex,
            int localPhaseEndAtomExclusive,
            int expressTraversalPhaseIndex,
            int expressPhaseStartAtomIndex,
            int expressPhaseEndAtomExclusive)
        {
            Available = available;
            LocalTraversalPhaseIndex = localTraversalPhaseIndex;
            LocalPhaseStartAtomIndex = localPhaseStartAtomIndex;
            LocalPhaseEndAtomExclusive = localPhaseEndAtomExclusive;
            ExpressTraversalPhaseIndex = expressTraversalPhaseIndex;
            ExpressPhaseStartAtomIndex = expressPhaseStartAtomIndex;
            ExpressPhaseEndAtomExclusive = expressPhaseEndAtomExclusive;
        }

        public bool Equals(TrunkPhaseAlignment other)
        {
            return Available == other.Available
                && LocalTraversalPhaseIndex == other.LocalTraversalPhaseIndex
                && LocalPhaseStartAtomIndex == other.LocalPhaseStartAtomIndex
                && LocalPhaseEndAtomExclusive == other.LocalPhaseEndAtomExclusive
                && ExpressTraversalPhaseIndex == other.ExpressTraversalPhaseIndex
                && ExpressPhaseStartAtomIndex == other.ExpressPhaseStartAtomIndex
                && ExpressPhaseEndAtomExclusive == other.ExpressPhaseEndAtomExclusive;
        }

        public override bool Equals(object obj)
        {
            return obj is TrunkPhaseAlignment other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Available.GetHashCode();
                hash = (hash * 397) ^ LocalTraversalPhaseIndex;
                hash = (hash * 397) ^ LocalPhaseStartAtomIndex;
                hash = (hash * 397) ^ LocalPhaseEndAtomExclusive;
                hash = (hash * 397) ^ ExpressTraversalPhaseIndex;
                hash = (hash * 397) ^ ExpressPhaseStartAtomIndex;
                hash = (hash * 397) ^ ExpressPhaseEndAtomExclusive;
                return hash;
            }
        }
    }

    internal readonly struct GlobalSharedTrunkSegment : IEquatable<GlobalSharedTrunkSegment>
    {
        public readonly int LocalCorridorStartAtomIndex;
        public readonly int LocalCorridorEndAtomIndexExclusive;
        public readonly int ExpressCorridorStartAtomIndex;
        public readonly int ExpressCorridorEndAtomIndexExclusive;
        public readonly int LocalAnchorStartAtomIndex;
        public readonly int LocalAnchorEndAtomIndexExclusive;
        public readonly int ExpressAnchorStartAtomIndex;
        public readonly int ExpressAnchorEndAtomIndexExclusive;
        public readonly int LocalSharedSliceCount;
        public readonly int ExpressSharedSliceCount;
        public readonly int LocalBridgedGapAtoms;
        public readonly int ExpressBridgedGapAtoms;
        public readonly int PhysicalOverlap;
        public readonly int OrderedRun;
        public readonly bool HasMirroredContext;
        public readonly int MaxSharedLineCount;
        public readonly SharedTraversalRelation TraversalRelation;
        public readonly bool HasCanonicalDirection;
        public readonly bool LocalAlongCanonical;
        public readonly bool ExpressAlongCanonical;
        public readonly TrunkPhaseAlignment PhaseAlignment;

        public GlobalSharedTrunkSegment(
            int localCorridorStartAtomIndex,
            int localCorridorEndAtomIndexExclusive,
            int expressCorridorStartAtomIndex,
            int expressCorridorEndAtomIndexExclusive,
            int localAnchorStartAtomIndex,
            int localAnchorEndAtomIndexExclusive,
            int expressAnchorStartAtomIndex,
            int expressAnchorEndAtomIndexExclusive,
            int localSharedSliceCount,
            int expressSharedSliceCount,
            int localBridgedGapAtoms,
            int expressBridgedGapAtoms,
            int physicalOverlap,
            int orderedRun,
            bool hasMirroredContext,
            int maxSharedLineCount,
            SharedTraversalRelation traversalRelation,
            bool hasCanonicalDirection,
            bool localAlongCanonical,
            bool expressAlongCanonical,
            TrunkPhaseAlignment phaseAlignment)
        {
            LocalCorridorStartAtomIndex = localCorridorStartAtomIndex;
            LocalCorridorEndAtomIndexExclusive = localCorridorEndAtomIndexExclusive;
            ExpressCorridorStartAtomIndex = expressCorridorStartAtomIndex;
            ExpressCorridorEndAtomIndexExclusive = expressCorridorEndAtomIndexExclusive;
            LocalAnchorStartAtomIndex = localAnchorStartAtomIndex;
            LocalAnchorEndAtomIndexExclusive = localAnchorEndAtomIndexExclusive;
            ExpressAnchorStartAtomIndex = expressAnchorStartAtomIndex;
            ExpressAnchorEndAtomIndexExclusive = expressAnchorEndAtomIndexExclusive;
            LocalSharedSliceCount = localSharedSliceCount;
            ExpressSharedSliceCount = expressSharedSliceCount;
            LocalBridgedGapAtoms = localBridgedGapAtoms;
            ExpressBridgedGapAtoms = expressBridgedGapAtoms;
            PhysicalOverlap = physicalOverlap;
            OrderedRun = orderedRun;
            HasMirroredContext = hasMirroredContext;
            MaxSharedLineCount = maxSharedLineCount;
            TraversalRelation = traversalRelation;
            HasCanonicalDirection = hasCanonicalDirection;
            LocalAlongCanonical = localAlongCanonical;
            ExpressAlongCanonical = expressAlongCanonical;
            PhaseAlignment = phaseAlignment;
        }

        public bool Equals(GlobalSharedTrunkSegment other)
        {
            return LocalCorridorStartAtomIndex == other.LocalCorridorStartAtomIndex
                && LocalCorridorEndAtomIndexExclusive == other.LocalCorridorEndAtomIndexExclusive
                && ExpressCorridorStartAtomIndex == other.ExpressCorridorStartAtomIndex
                && ExpressCorridorEndAtomIndexExclusive == other.ExpressCorridorEndAtomIndexExclusive
                && LocalAnchorStartAtomIndex == other.LocalAnchorStartAtomIndex
                && LocalAnchorEndAtomIndexExclusive == other.LocalAnchorEndAtomIndexExclusive
                && ExpressAnchorStartAtomIndex == other.ExpressAnchorStartAtomIndex
                && ExpressAnchorEndAtomIndexExclusive == other.ExpressAnchorEndAtomIndexExclusive
                && LocalSharedSliceCount == other.LocalSharedSliceCount
                && ExpressSharedSliceCount == other.ExpressSharedSliceCount
                && LocalBridgedGapAtoms == other.LocalBridgedGapAtoms
                && ExpressBridgedGapAtoms == other.ExpressBridgedGapAtoms
                && PhysicalOverlap == other.PhysicalOverlap
                && OrderedRun == other.OrderedRun
                && HasMirroredContext == other.HasMirroredContext
                && MaxSharedLineCount == other.MaxSharedLineCount
                && TraversalRelation == other.TraversalRelation
                && HasCanonicalDirection == other.HasCanonicalDirection
                && LocalAlongCanonical == other.LocalAlongCanonical
                && ExpressAlongCanonical == other.ExpressAlongCanonical
                && PhaseAlignment.Equals(other.PhaseAlignment);
        }

        public override bool Equals(object obj)
        {
            return obj is GlobalSharedTrunkSegment other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = LocalCorridorStartAtomIndex;
                hash = (hash * 397) ^ LocalCorridorEndAtomIndexExclusive;
                hash = (hash * 397) ^ ExpressCorridorStartAtomIndex;
                hash = (hash * 397) ^ ExpressCorridorEndAtomIndexExclusive;
                hash = (hash * 397) ^ LocalAnchorStartAtomIndex;
                hash = (hash * 397) ^ LocalAnchorEndAtomIndexExclusive;
                hash = (hash * 397) ^ ExpressAnchorStartAtomIndex;
                hash = (hash * 397) ^ ExpressAnchorEndAtomIndexExclusive;
                hash = (hash * 397) ^ LocalSharedSliceCount;
                hash = (hash * 397) ^ ExpressSharedSliceCount;
                hash = (hash * 397) ^ LocalBridgedGapAtoms;
                hash = (hash * 397) ^ ExpressBridgedGapAtoms;
                hash = (hash * 397) ^ PhysicalOverlap;
                hash = (hash * 397) ^ OrderedRun;
                hash = (hash * 397) ^ HasMirroredContext.GetHashCode();
                hash = (hash * 397) ^ MaxSharedLineCount;
                hash = (hash * 397) ^ (int)TraversalRelation;
                hash = (hash * 397) ^ HasCanonicalDirection.GetHashCode();
                hash = (hash * 397) ^ LocalAlongCanonical.GetHashCode();
                hash = (hash * 397) ^ ExpressAlongCanonical.GetHashCode();
                hash = (hash * 397) ^ PhaseAlignment.GetHashCode();
                return hash;
            }
        }
    }

    internal readonly struct SharedTrackOccurrence
    {
        public readonly Entity LineEntity;
        public readonly int AtomIndex;
        public readonly int WaypointSegmentIndex;

        public SharedTrackOccurrence(Entity lineEntity, int atomIndex, int waypointSegmentIndex)
        {
            LineEntity = lineEntity;
            AtomIndex = atomIndex;
            WaypointSegmentIndex = waypointSegmentIndex;
        }
    }

    internal readonly struct ProtectedIntervalMatch
    {
        public readonly bool Found;
        public readonly bool Ambiguous;
        public readonly int ProtectedIntervalIndex;
        public readonly int OverlapCount;

        public ProtectedIntervalMatch(bool found, bool ambiguous, int protectedIntervalIndex, int overlapCount)
        {
            Found = found;
            Ambiguous = ambiguous;
            ProtectedIntervalIndex = protectedIntervalIndex;
            OverlapCount = overlapCount;
        }
    }

    internal readonly struct SharedPhysicalOccurrence
    {
        public readonly Entity LineEntity;
        public readonly int AtomIndex;
        public readonly int WaypointSegmentIndex;
        public readonly Entity PreviousTarget;
        public readonly Entity NextTarget;

        public SharedPhysicalOccurrence(Entity lineEntity, int atomIndex, int waypointSegmentIndex, Entity previousTarget, Entity nextTarget)
        {
            LineEntity = lineEntity;
            AtomIndex = atomIndex;
            WaypointSegmentIndex = waypointSegmentIndex;
            PreviousTarget = previousTarget;
            NextTarget = nextTarget;
        }
    }

    internal readonly struct PhysicalSharedWindowMatch
    {
        public readonly bool Found;
        public readonly bool Ambiguous;
        public readonly BypassProtectedInterval LocalSharedWindow;
        public readonly BypassProtectedInterval ExpressSharedWindow;
        public readonly int OverlapCount;
        public readonly int OrderedRun;

        public PhysicalSharedWindowMatch(
            bool found,
            bool ambiguous,
            BypassProtectedInterval localSharedWindow,
            BypassProtectedInterval expressSharedWindow,
            int overlapCount,
            int orderedRun)
        {
            Found = found;
            Ambiguous = ambiguous;
            LocalSharedWindow = localSharedWindow;
            ExpressSharedWindow = expressSharedWindow;
            OverlapCount = overlapCount;
            OrderedRun = orderedRun;
        }
    }

    internal readonly struct TrackModelSequenceItem
    {
        public readonly float DistanceMeters;
        public readonly int KindOrder;
        public readonly string Label;

        public TrackModelSequenceItem(float distanceMeters, int kindOrder, string label)
        {
            DistanceMeters = distanceMeters;
            KindOrder = kindOrder;
            Label = label;
        }
    }
}
