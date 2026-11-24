using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;
namespace RapidTransitMod.TrackModel
{
    internal readonly struct TrackLineDeletedFact
    {
        internal readonly Entity Line;
        internal readonly LineKey LineKey;
        internal readonly TransitMode Mode;

        internal TrackLineDeletedFact(Entity line, LineKey lineKey, TransitMode mode)
        {
            Line = line;
            LineKey = lineKey;
            Mode = mode;
        }
    }

    internal enum TrackTraversalDir : byte
    {
        Unknown = 0,
        Forward = 1,
        Reverse = 2,
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
    internal readonly struct TrackJunctionMarker
    {
        public readonly int SegmentIndex;
        public readonly int AtomIndex;
        public readonly Entity SignalLane;
        public TrackJunctionMarker(
            int segmentIndex,
            int atomIndex,
            Entity signalLane)
        {
            SegmentIndex = segmentIndex;
            AtomIndex = atomIndex;
            SignalLane = signalLane;
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
        public ControlEdge(
            int startControlPointIndex,
            int endControlPointIndex,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            StartControlPointIndex = startControlPointIndex;
            EndControlPointIndex = endControlPointIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
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
        public readonly string StationId;
        public TraversalEvent(
            int eventIndex,
            TraversalEventKind kind,
            Entity building,
            int waypointIndex,
            int passIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            string stationId = "")
        {
            EventIndex = eventIndex;
            Kind = kind;
            Building = building;
            WaypointIndex = waypointIndex;
            PassIndex = passIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
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
        public TraversalRunSlice(
            int sliceIndex,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int startEventIndex,
            int endEventIndex,
            Entity[] physicalLaneKeys)
        {
            SliceIndex = sliceIndex;
            StartAtomIndex = startAtomIndex;
            EndAtomIndexExclusive = endAtomIndexExclusive;
            StartEventIndex = startEventIndex;
            EndEventIndex = endEventIndex;
            PhysicalLaneKeys = physicalLaneKeys ?? Array.Empty<Entity>();
        }
    }
    internal sealed class LineTraversalProfile
    {
        public readonly List<TraversalEvent> Events = new List<TraversalEvent>();
        public readonly List<TraversalRunSlice> RunSlices = new List<TraversalRunSlice>();
        public int[] AtomToRunSliceIndex = Array.Empty<int>();
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
    internal readonly struct TrackExtensionRange
    {
        public readonly int StartAtomIndex;
        public readonly int ForwardEndAtomIndexExclusive;
        public readonly int ResumeAtomIndex;

        public TrackExtensionRange(
            int startAtomIndex,
            int forwardEndAtomIndexExclusive,
            int resumeAtomIndex)
        {
            StartAtomIndex = startAtomIndex;
            ForwardEndAtomIndexExclusive = forwardEndAtomIndexExclusive;
            ResumeAtomIndex = resumeAtomIndex;
        }
    }
    internal sealed class LineTrackChain
    {
        public Entity LineEntity;
        public TransitMode Mode;
        public ulong Signature;
        public ulong TraversalSignature;
        public bool ChainComplete;
        public bool HasStationTrackExtensions;
        public List<TrackExtensionRange> TrackExtensionRanges = new List<TrackExtensionRange>();
        public List<TrackAtom> TrackAtoms = new List<TrackAtom>();
        public Entity[] AtomStationBuildings = Array.Empty<Entity>();
        public Dictionary<Entity, List<int>> AtomIndicesByLane = new Dictionary<Entity, List<int>>();
        public List<TrackSegmentRange> SegmentRanges = new List<TrackSegmentRange>();
        public List<TrackJunctionMarker> JunctionMarkers = new List<TrackJunctionMarker>();
        public List<ControlPointMarker> ControlPoints = new List<ControlPointMarker>();
        public List<EndpointMarker> EndpointMarkers = new List<EndpointMarker>();
        public List<ControlEdge> ControlEdges = new List<ControlEdge>();
        public LineTraversalProfile TraversalProfile = new LineTraversalProfile();
        public List<TurnbackBoundary> TurnbackBoundaries = new List<TurnbackBoundary>();
        public List<RunChartTurnbackRegion> RunChartTurnbackRegions = new List<RunChartTurnbackRegion>();
        public TrackWaypointInputBaseline[] WaypointInputs = Array.Empty<TrackWaypointInputBaseline>();
        public TrackSegmentInputBaseline[] SegmentInputs = Array.Empty<TrackSegmentInputBaseline>();
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
