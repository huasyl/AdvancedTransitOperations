using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private const int MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS = 3;
        private const int MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN = 2;
        private const float PROTECTED_INTERVAL_TAIL_CLEARANCE_ATOMS = 1.25f;
        private const float SAME_DIRECTION_AHEAD_MARGIN_ATOMS = 0.75f;
        private const float TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES = 1f;
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 32f;
        private const float LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS = 8f;
        private const int MAX_CONFLICT_CORRIDOR_GAP_ATOMS = 6;
        private const uint SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES = 60;
        private const int SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS = 1;
        private const int SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD = 12;
        private const float SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS = 120f;

        private enum TrackTraversalDir : byte
        {
            Unknown = 0,
            Forward = 1,
            Reverse = 2,
        }

        private enum SharedTraversalRelation : byte
        {
            Unknown = 0,
            SameDirection = 1,
            OppositeDirection = 2,
        }

        private enum TrackAtomClass : byte
        {
            Unknown = 0,
            PrimaryLane = 1,
            ConnectionHelper = 2,
            FilteredNoise = 3,
        }

        private enum ControlPointKind : byte
        {
            Unknown = 0,
            Stop = 1,
            Bypass = 2,
            Branch = 3,
            Merge = 4,
            SharedEntry = 5,
            SharedExit = 6,
        }

        private readonly struct TrackAtomKey : IEquatable<TrackAtomKey>
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

        private readonly struct TrackAtom
        {
            public readonly TrackAtomKey Key;
            public readonly Entity SourceTarget;
            public readonly PathElementFlags SourceFlags;
            public readonly TrackAtomClass AtomClass;
            public readonly TrackTraversalDir TraversalDir;

            public TrackAtom(
                TrackAtomKey key,
                Entity sourceTarget,
                PathElementFlags sourceFlags,
                TrackAtomClass atomClass,
                TrackTraversalDir traversalDir)
            {
                Key = key;
                SourceTarget = sourceTarget;
                SourceFlags = sourceFlags;
                AtomClass = atomClass;
                TraversalDir = traversalDir;
            }
        }

        private readonly struct TrackSegmentRange
        {
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;

            public TrackSegmentRange(int startAtomIndex, int endAtomIndexExclusive)
            {
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
            }
        }

        private readonly struct ControlPointMarker
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

        private readonly struct ControlEdge
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

        private enum TraversalEventKind : byte
        {
            Unknown = 0,
            Stop = 1,
            Pass = 2,
            ApproachSplitBoundary = 3,
            DepartureSplitBoundary = 4,
        }

        private readonly struct TraversalEvent
        {
            public readonly int EventIndex;
            public readonly TraversalEventKind Kind;
            public readonly Entity Building;
            public readonly int WaypointIndex;
            public readonly int PassIndex;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly float StopFrames;

            public TraversalEvent(
                int eventIndex,
                TraversalEventKind kind,
                Entity building,
                int waypointIndex,
                int passIndex,
                int startAtomIndex,
                int endAtomIndexExclusive,
                float stopFrames)
            {
                EventIndex = eventIndex;
                Kind = kind;
                Building = building;
                WaypointIndex = waypointIndex;
                PassIndex = passIndex;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                StopFrames = stopFrames;
            }
        }

        private readonly struct TraversalRunSlice
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

        private sealed class LineTraversalProfile
        {
            public readonly List<TraversalEvent> Events = new List<TraversalEvent>();
            public readonly List<TraversalRunSlice> RunSlices = new List<TraversalRunSlice>();
        }

        private readonly struct TraversalTimingEstimate
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

        private sealed class LineTrackChain
        {
            public Entity LineEntity;
            public ulong Signature;
            public List<TrackAtom> TrackAtoms = new List<TrackAtom>();
            public Dictionary<Entity, List<int>> AtomIndicesByLane = new Dictionary<Entity, List<int>>();
            public List<TrackSegmentRange> SegmentRanges = new List<TrackSegmentRange>();
            public List<ControlPointMarker> ControlPoints = new List<ControlPointMarker>();
            public List<ControlEdge> ControlEdges = new List<ControlEdge>();
            public LineTraversalProfile TraversalProfile = new LineTraversalProfile();
            public List<SharedTrackRun> SharedRuns = new List<SharedTrackRun>();
            public Dictionary<Entity, List<SharedTrackRun>> SharedRunsByOtherLine = new Dictionary<Entity, List<SharedTrackRun>>();
            public List<ControlEdgeSharedSpan> ControlEdgeSharedSpans = new List<ControlEdgeSharedSpan>();
            public List<BypassProtectedInterval> BypassProtectedIntervals = new List<BypassProtectedInterval>();
            public List<ProtectedSharedInterval> ProtectedSharedIntervals = new List<ProtectedSharedInterval>();
            public List<ProtectedIntervalSummary> ProtectedIntervalSummaries = new List<ProtectedIntervalSummary>();
            public uint SharedRunsVersion;
            public uint BypassPipelineReadyVersion;
            public bool ControlEdgeSharedSpansReady;
            public bool BypassProtectedIntervalsReady;
            public bool ProtectedSharedIntervalsReady;
            public bool ProtectedIntervalSummariesReady;
        }

        private readonly struct DevSightLaneOccurrence
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

        private readonly struct SharedTrackRun
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

        private readonly struct ControlEdgeSharedSpan
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

        private readonly struct BypassProtectedInterval
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

        private readonly struct ProtectedSharedInterval
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

        private readonly struct ProtectedIntervalSummary
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

        private enum TrackModelRelativeToProtectedInterval : byte
        {
            Unknown = 0,
            Before = 1,
            Inside = 2,
            After = 3,
        }

        private readonly struct TrackModelRuntimePosition
        {
            public readonly int CurrentControlEdgeIndex;
            public readonly int CurrentAtomIndex;
            public readonly float AtomPosition01;
            public readonly TrackModelRelativeToProtectedInterval RelativeToProtectedInterval;
            public readonly float Confidence;

            public TrackModelRuntimePosition(
                int currentControlEdgeIndex,
                int currentAtomIndex,
                float atomPosition01,
                TrackModelRelativeToProtectedInterval relativeToProtectedInterval,
                float confidence)
            {
                CurrentControlEdgeIndex = currentControlEdgeIndex;
                CurrentAtomIndex = currentAtomIndex;
                AtomPosition01 = atomPosition01;
                RelativeToProtectedInterval = relativeToProtectedInterval;
                Confidence = confidence;
            }
        }

        private readonly struct BypassTrackModelShadowDecision
        {
            public readonly bool Available;
            public readonly bool ShouldYield;
            public readonly string ReasonCode;
            public readonly int ProtectedIntervalIndex;
            public readonly bool HasReliableLocalPosition;
            public readonly Entity BlockerVehicle;
            public readonly bool UsedFallbackResolution;

            public BypassTrackModelShadowDecision(
                bool available,
                bool shouldYield,
                string reasonCode,
                int protectedIntervalIndex,
                bool hasReliableLocalPosition,
                Entity blockerVehicle,
                bool usedFallbackResolution)
            {
                Available = available;
                ShouldYield = shouldYield;
                ReasonCode = reasonCode;
                ProtectedIntervalIndex = protectedIntervalIndex;
                HasReliableLocalPosition = hasReliableLocalPosition;
                BlockerVehicle = blockerVehicle;
                UsedFallbackResolution = usedFallbackResolution;
            }
        }

        private readonly struct SharedRunBand
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

        private readonly struct AtomWindowSlice
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

        private readonly struct DirectedSharedPairSegment
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
                SharedTraversalRelation traversalRelation)
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
            }
        }

        private readonly struct GlobalSharedTrunkSegment
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
                SharedTraversalRelation traversalRelation)
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
            }
        }

        private readonly struct BypassTrackModelShadowSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Line;
            public readonly int CurrentWaypointIndex;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly BypassTrackModelShadowDecision Decision;

            public BypassTrackModelShadowSnapshot(
                uint frame,
                Entity line,
                int currentWaypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                BypassTrackModelShadowDecision decision)
            {
                Frame = frame;
                Line = line;
                CurrentWaypointIndex = currentWaypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                Decision = decision;
            }
        }

        private readonly struct SharedTrackOccurrence
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

        private readonly struct VehicleTrackCursor
        {
            public readonly Entity LineEntity;
            public readonly ulong ChainSignature;
            public readonly int SegmentIndex;
            public readonly int AtomStartIndex;
            public readonly int AtomEndIndexExclusive;
            public readonly int AtomCursorIndex;
            public readonly float AtomPosition01;
            public readonly float Confidence;

            public VehicleTrackCursor(
                Entity lineEntity,
                ulong chainSignature,
                int segmentIndex,
                int atomStartIndex,
                int atomEndIndexExclusive,
                int atomCursorIndex,
                float atomPosition01,
                float confidence)
            {
                LineEntity = lineEntity;
                ChainSignature = chainSignature;
                SegmentIndex = segmentIndex;
                AtomStartIndex = atomStartIndex;
                AtomEndIndexExclusive = atomEndIndexExclusive;
                AtomCursorIndex = atomCursorIndex;
                AtomPosition01 = atomPosition01;
                Confidence = confidence;
            }
        }

        private readonly struct VehicleTrackCursorFrameSnapshot
        {
            public readonly Entity LineEntity;
            public readonly ulong ChainSignature;
            public readonly uint Frame;
            public readonly bool Available;
            public readonly VehicleTrackCursor Cursor;

            public VehicleTrackCursorFrameSnapshot(
                Entity lineEntity,
                ulong chainSignature,
                uint frame,
                bool available,
                VehicleTrackCursor cursor)
            {
                LineEntity = lineEntity;
                ChainSignature = chainSignature;
                Frame = frame;
                Available = available;
                Cursor = cursor;
            }
        }

        private readonly struct ProtectedIntervalMatch
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

        private readonly struct SharedPhysicalOccurrence
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

        private readonly struct PhysicalSharedWindowMatch
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

        private readonly struct SharedWindowMatchCacheKey : IEquatable<SharedWindowMatchCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity ExpressLine;
            public readonly Entity CurrentBypassBuilding;
            public readonly int LocalStartAtomIndex;
            public readonly int LocalEndAtomIndexExclusive;

            public SharedWindowMatchCacheKey(
                Entity localLine,
                Entity expressLine,
                Entity currentBypassBuilding,
                int localStartAtomIndex,
                int localEndAtomIndexExclusive)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
                CurrentBypassBuilding = currentBypassBuilding;
                LocalStartAtomIndex = localStartAtomIndex;
                LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
            }

            public bool Equals(SharedWindowMatchCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine
                    && CurrentBypassBuilding == other.CurrentBypassBuilding
                    && LocalStartAtomIndex == other.LocalStartAtomIndex
                    && LocalEndAtomIndexExclusive == other.LocalEndAtomIndexExclusive;
            }

            public override bool Equals(object obj)
            {
                return obj is SharedWindowMatchCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalLine.GetHashCode();
                    hash = (hash * 397) ^ ExpressLine.GetHashCode();
                    hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ LocalStartAtomIndex;
                    hash = (hash * 397) ^ LocalEndAtomIndexExclusive;
                    return hash;
                }
            }
        }

        private readonly struct SharedWindowMatchSnapshot
        {
            public readonly uint Frame;
            public readonly uint SharedTrackVersion;
            public readonly ulong LocalChainSignature;
            public readonly ulong ExpressChainSignature;
            public readonly PhysicalSharedWindowMatch Match;

            public SharedWindowMatchSnapshot(
                uint frame,
                uint sharedTrackVersion,
                ulong localChainSignature,
                ulong expressChainSignature,
                PhysicalSharedWindowMatch match)
            {
                Frame = frame;
                SharedTrackVersion = sharedTrackVersion;
                LocalChainSignature = localChainSignature;
                ExpressChainSignature = expressChainSignature;
                Match = match;
            }
        }

        private readonly struct GlobalSharedTrunkCacheKey : IEquatable<GlobalSharedTrunkCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity ExpressLine;

            public GlobalSharedTrunkCacheKey(Entity localLine, Entity expressLine)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
            }

            public bool Equals(GlobalSharedTrunkCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine;
            }

            public override bool Equals(object obj)
            {
                return obj is GlobalSharedTrunkCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (LocalLine.GetHashCode() * 397) ^ ExpressLine.GetHashCode();
                }
            }
        }

        private sealed class GlobalSharedTrunkSnapshot
        {
            public uint SharedTrackVersion;
            public ulong LocalChainSignature;
            public ulong ExpressChainSignature;
            public readonly List<GlobalSharedTrunkSegment> Segments = new List<GlobalSharedTrunkSegment>();
        }

        private readonly struct ProtectedIntervalPairMetricsCacheKey : IEquatable<ProtectedIntervalPairMetricsCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity ExpressLine;

            public ProtectedIntervalPairMetricsCacheKey(Entity localLine, Entity expressLine)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
            }

            public bool Equals(ProtectedIntervalPairMetricsCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine;
            }

            public override bool Equals(object obj)
            {
                return obj is ProtectedIntervalPairMetricsCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (LocalLine.GetHashCode() * 397) ^ ExpressLine.GetHashCode();
                }
            }
        }

        private readonly struct ProtectedIntervalPairMetrics
        {
            public readonly int OverlapCount;
            public readonly int OrderedRun;

            public ProtectedIntervalPairMetrics(int overlapCount, int orderedRun)
            {
                OverlapCount = overlapCount;
                OrderedRun = orderedRun;
            }
        }

        private sealed class ProtectedIntervalPairMetricsSnapshot
        {
            public uint SharedTrackVersion;
            public ulong LocalChainSignature;
            public ulong ExpressChainSignature;
            public int LocalIntervalCount;
            public int ExpressIntervalCount;
            public ProtectedIntervalPairMetrics[] Metrics = Array.Empty<ProtectedIntervalPairMetrics>();
        }

        private readonly struct ActiveConflictCorridorCacheKey : IEquatable<ActiveConflictCorridorCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity ExpressLine;
            public readonly Entity CurrentBypassBuilding;
            public readonly int LocalStartControlPointIndex;
            public readonly int LocalEndControlPointIndex;
            public readonly int ExpressStartControlPointIndex;
            public readonly int ExpressEndControlPointIndex;
            public readonly int LocalStartAtomIndex;
            public readonly int LocalEndAtomIndexExclusive;
            public readonly int ExpressStartAtomIndex;
            public readonly int ExpressEndAtomIndexExclusive;
            public readonly int ExpressCurrentAtomIndex;

            public ActiveConflictCorridorCacheKey(
                Entity localLine,
                Entity expressLine,
                Entity currentBypassBuilding,
                int localStartControlPointIndex,
                int localEndControlPointIndex,
                int expressStartControlPointIndex,
                int expressEndControlPointIndex,
                int localStartAtomIndex,
                int localEndAtomIndexExclusive,
                int expressStartAtomIndex,
                int expressEndAtomIndexExclusive,
                int expressCurrentAtomIndex)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
                CurrentBypassBuilding = currentBypassBuilding;
                LocalStartControlPointIndex = localStartControlPointIndex;
                LocalEndControlPointIndex = localEndControlPointIndex;
                ExpressStartControlPointIndex = expressStartControlPointIndex;
                ExpressEndControlPointIndex = expressEndControlPointIndex;
                LocalStartAtomIndex = localStartAtomIndex;
                LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
                ExpressStartAtomIndex = expressStartAtomIndex;
                ExpressEndAtomIndexExclusive = expressEndAtomIndexExclusive;
                ExpressCurrentAtomIndex = expressCurrentAtomIndex;
            }

            public bool Equals(ActiveConflictCorridorCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine
                    && CurrentBypassBuilding == other.CurrentBypassBuilding
                    && LocalStartControlPointIndex == other.LocalStartControlPointIndex
                    && LocalEndControlPointIndex == other.LocalEndControlPointIndex
                    && ExpressStartControlPointIndex == other.ExpressStartControlPointIndex
                    && ExpressEndControlPointIndex == other.ExpressEndControlPointIndex
                    && LocalStartAtomIndex == other.LocalStartAtomIndex
                    && LocalEndAtomIndexExclusive == other.LocalEndAtomIndexExclusive
                    && ExpressStartAtomIndex == other.ExpressStartAtomIndex
                    && ExpressEndAtomIndexExclusive == other.ExpressEndAtomIndexExclusive
                    && ExpressCurrentAtomIndex == other.ExpressCurrentAtomIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is ActiveConflictCorridorCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalLine.GetHashCode();
                    hash = (hash * 397) ^ ExpressLine.GetHashCode();
                    hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ LocalStartControlPointIndex;
                    hash = (hash * 397) ^ LocalEndControlPointIndex;
                    hash = (hash * 397) ^ ExpressStartControlPointIndex;
                    hash = (hash * 397) ^ ExpressEndControlPointIndex;
                    hash = (hash * 397) ^ LocalStartAtomIndex;
                    hash = (hash * 397) ^ LocalEndAtomIndexExclusive;
                    hash = (hash * 397) ^ ExpressStartAtomIndex;
                    hash = (hash * 397) ^ ExpressEndAtomIndexExclusive;
                    hash = (hash * 397) ^ ExpressCurrentAtomIndex;
                    return hash;
                }
            }
        }

        private readonly struct SharedWindowPairStateKey : IEquatable<SharedWindowPairStateKey>
        {
            public readonly Entity LocalVehicle;
            public readonly int ProtectedIntervalIndex;
            public readonly Entity ExpressVehicle;

            public SharedWindowPairStateKey(Entity localVehicle, int protectedIntervalIndex, Entity expressVehicle)
            {
                LocalVehicle = localVehicle;
                ProtectedIntervalIndex = protectedIntervalIndex;
                ExpressVehicle = expressVehicle;
            }

            public bool Equals(SharedWindowPairStateKey other)
            {
                return LocalVehicle == other.LocalVehicle
                    && ProtectedIntervalIndex == other.ProtectedIntervalIndex
                    && ExpressVehicle == other.ExpressVehicle;
            }

            public override bool Equals(object obj)
            {
                return obj is SharedWindowPairStateKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalVehicle.GetHashCode();
                    hash = (hash * 397) ^ ProtectedIntervalIndex;
                    hash = (hash * 397) ^ ExpressVehicle.GetHashCode();
                    return hash;
                }
            }
        }

        private readonly struct ActiveConflictCorridorSnapshot
        {
            public readonly uint Frame;
            public readonly uint SharedTrackVersion;
            public readonly ulong LocalChainSignature;
            public readonly ulong ExpressChainSignature;
            public readonly bool Available;
            public readonly ConflictCorridor LocalCorridor;
            public readonly ConflictCorridor ExpressCorridor;
            public readonly GlobalSharedTrunkSegment TrunkSegment;

            public ActiveConflictCorridorSnapshot(
                uint frame,
                uint sharedTrackVersion,
                ulong localChainSignature,
                ulong expressChainSignature,
                bool available,
                ConflictCorridor localCorridor,
                ConflictCorridor expressCorridor,
                GlobalSharedTrunkSegment trunkSegment)
            {
                Frame = frame;
                SharedTrackVersion = sharedTrackVersion;
                LocalChainSignature = localChainSignature;
                ExpressChainSignature = expressChainSignature;
                Available = available;
                LocalCorridor = localCorridor;
                ExpressCorridor = expressCorridor;
                TrunkSegment = trunkSegment;
            }
        }

        private readonly struct TrackModelSequenceItem
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

        private readonly Dictionary<Entity, LineTrackChain> m_LineTrackChains = new Dictionary<Entity, LineTrackChain>();
        private readonly Dictionary<Entity, List<DevSightLaneOccurrence>> m_DevSightLaneIndex = new Dictionary<Entity, List<DevSightLaneOccurrence>>();
        private readonly Dictionary<TrackAtomKey, List<SharedTrackOccurrence>> m_SharedTrackIndex = new Dictionary<TrackAtomKey, List<SharedTrackOccurrence>>();
        private readonly Dictionary<Entity, List<SharedPhysicalOccurrence>> m_SharedPhysicalTrackIndex = new Dictionary<Entity, List<SharedPhysicalOccurrence>>();
        private readonly Dictionary<Entity, VehicleTrackCursor> m_VehicleTrackCursorHints = new Dictionary<Entity, VehicleTrackCursor>();
        private readonly Dictionary<Entity, VehicleTrackCursorFrameSnapshot> m_VehicleTrackCursorFrameSnapshots = new Dictionary<Entity, VehicleTrackCursorFrameSnapshot>();
        private readonly Dictionary<Entity, uint> m_SuspectProgressSinceFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_SuspectProgressLastValidationFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, bool> m_SuspectProgressProjectionInvalid = new Dictionary<Entity, bool>();
        private readonly Dictionary<Entity, string> m_SuspectProgressReason = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SuspectProgressLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, int> m_SuspectProgressRecoveryWaypoint = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_SuspectProgressValidationCount = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, SuspectProgressSample> m_SuspectProgressFirstSample = new Dictionary<Entity, SuspectProgressSample>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelShadowLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelShadowThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelShadowLastLogFrame = new Dictionary<Entity, uint>();
        private const bool ENABLE_TRACKMODEL_DIAGNOSTIC_LOGS = true;
        private readonly Dictionary<Entity, string> m_BypassSelectedBlockerDetailLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_TrainLaneSourceDiagnosticLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelCompareLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditSummaryLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_SharedWindowAuditLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<SharedWindowPairStateKey, string> m_SharedWindowAuditPairStateCache = new Dictionary<SharedWindowPairStateKey, string>();
        private readonly Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> m_SharedWindowMatchSnapshots = new Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot>();
        private readonly Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> m_GlobalSharedTrunkSnapshots = new Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot>();
        private readonly Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> m_ProtectedIntervalPairMetricsSnapshots = new Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot>();
        private readonly Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot> m_ActiveConflictCorridorSnapshots = new Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot>();
        private uint m_ActiveConflictCorridorSnapshotFrame;
        private readonly Dictionary<Entity, string> m_TrackModelSequenceLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, BypassTrackModelShadowSnapshot> m_BypassTrackModelShadowSnapshots = new Dictionary<Entity, BypassTrackModelShadowSnapshot>();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapSourceKeys = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapMatchedKeys = new HashSet<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedSourceKeys = new List<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedCandidateKeys = new List<Entity>();
        private readonly List<int> m_ProtectedIntervalOrderedSourceAtomIndices = new List<int>();
        private readonly List<int> m_ProtectedIntervalOrderedCandidateAtomIndices = new List<int>();
        private uint m_SharedTrackIndexVersion = 1;
        private readonly HashSet<Entity> m_DirtyTrackLines = new HashSet<Entity>();
        private bool m_SharedTrackIndexDirty = true;

        private void InvalidateTrackModel(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_DirtyTrackLines.Add(line);
            if (m_LineTrackChains.TryGetValue(line, out LineTrackChain existingChain) && existingChain != null)
                RemoveDevSightLaneIndexForChain(existingChain);
            m_LineTrackChains.Remove(line);
            ClearBypassRuntimeStateForLine(line);
            m_SharedTrackIndexDirty = true;
        }

        private void InvalidateAllTrackModels()
        {
            m_DirtyTrackLines.Clear();
            m_LineTrackChains.Clear();
            m_DevSightLaneIndex.Clear();
            m_SharedTrackIndex.Clear();
            m_SharedPhysicalTrackIndex.Clear();
            m_VehicleTrackCursorHints.Clear();
            m_VehicleTrackCursorFrameSnapshots.Clear();
            m_SuspectProgressSinceFrame.Clear();
            m_SuspectProgressLastValidationFrame.Clear();
            m_SuspectProgressProjectionInvalid.Clear();
            m_SuspectProgressReason.Clear();
            m_SuspectProgressLogCache.Clear();
            m_SuspectProgressRecoveryWaypoint.Clear();
            m_SuspectProgressValidationCount.Clear();
            m_SuspectProgressFirstSample.Clear();
            ClearBypassTrackModelRuntimeState();
            m_SharedTrackIndexDirty = true;
        }

        private void ClearBypassTrackModelRuntimeState()
        {
            m_BypassTrackModelShadowLogCache.Clear();
            m_BypassTrackModelShadowThrottleCache.Clear();
            m_BypassTrackModelShadowLastLogFrame.Clear();
            m_BypassTrackModelCompareLogCache.Clear();
            m_BypassTrackModelCompareThrottleCache.Clear();
            m_BypassTrackModelCompareLastLogFrame.Clear();
            m_TrainLaneSourceDiagnosticLogCache.Clear();
            m_SharedWindowAuditSummaryLogCache.Clear();
            m_SharedWindowAuditThrottleCache.Clear();
            m_SharedWindowAuditLastLogFrame.Clear();
            m_SharedWindowAuditPairStateCache.Clear();
            m_SharedWindowMatchSnapshots.Clear();
            m_GlobalSharedTrunkSnapshots.Clear();
            m_ProtectedIntervalPairMetricsSnapshots.Clear();
            m_ActiveConflictCorridorSnapshots.Clear();
            m_ActiveConflictCorridorSnapshotFrame = 0;
            m_TrackModelSequenceLogCache.Clear();
            m_BypassTrackModelShadowSnapshots.Clear();
        }

        private void ClearBypassTrackModelRuntimeStateForLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            List<SharedWindowMatchCacheKey> sharedWindowKeysToRemove = null;
            foreach (KeyValuePair<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> entry in m_SharedWindowMatchSnapshots)
            {
                SharedWindowMatchCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                sharedWindowKeysToRemove ??= new List<SharedWindowMatchCacheKey>();
                sharedWindowKeysToRemove.Add(key);
            }

            if (sharedWindowKeysToRemove != null)
            {
                for (int i = 0; i < sharedWindowKeysToRemove.Count; i++)
                    m_SharedWindowMatchSnapshots.Remove(sharedWindowKeysToRemove[i]);
            }

            List<GlobalSharedTrunkCacheKey> trunkKeysToRemove = null;
            foreach (KeyValuePair<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> entry in m_GlobalSharedTrunkSnapshots)
            {
                GlobalSharedTrunkCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                trunkKeysToRemove ??= new List<GlobalSharedTrunkCacheKey>();
                trunkKeysToRemove.Add(key);
            }

            if (trunkKeysToRemove != null)
            {
                for (int i = 0; i < trunkKeysToRemove.Count; i++)
                    m_GlobalSharedTrunkSnapshots.Remove(trunkKeysToRemove[i]);
            }

            List<ProtectedIntervalPairMetricsCacheKey> pairMetricKeysToRemove = null;
            foreach (KeyValuePair<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> entry in m_ProtectedIntervalPairMetricsSnapshots)
            {
                ProtectedIntervalPairMetricsCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                pairMetricKeysToRemove ??= new List<ProtectedIntervalPairMetricsCacheKey>();
                pairMetricKeysToRemove.Add(key);
            }

            if (pairMetricKeysToRemove != null)
            {
                for (int i = 0; i < pairMetricKeysToRemove.Count; i++)
                    m_ProtectedIntervalPairMetricsSnapshots.Remove(pairMetricKeysToRemove[i]);
            }

            List<ActiveConflictCorridorCacheKey> activeCorridorKeysToRemove = null;
            foreach (KeyValuePair<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot> entry in m_ActiveConflictCorridorSnapshots)
            {
                ActiveConflictCorridorCacheKey key = entry.Key;
                if (key.LocalLine != line && key.ExpressLine != line)
                    continue;

                activeCorridorKeysToRemove ??= new List<ActiveConflictCorridorCacheKey>();
                activeCorridorKeysToRemove.Add(key);
            }

            if (activeCorridorKeysToRemove != null)
            {
                for (int i = 0; i < activeCorridorKeysToRemove.Count; i++)
                    m_ActiveConflictCorridorSnapshots.Remove(activeCorridorKeysToRemove[i]);
            }

            List<Entity> shadowSnapshotKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassTrackModelShadowSnapshot> entry in m_BypassTrackModelShadowSnapshots)
            {
                if (entry.Value.Line != line)
                    continue;

                shadowSnapshotKeysToRemove ??= new List<Entity>();
                shadowSnapshotKeysToRemove.Add(entry.Key);
            }

            if (shadowSnapshotKeysToRemove != null)
            {
                for (int i = 0; i < shadowSnapshotKeysToRemove.Count; i++)
                {
                    Entity vehicle = shadowSnapshotKeysToRemove[i];
                    m_BypassTrackModelShadowSnapshots.Remove(vehicle);
                    m_BypassTrackModelShadowLogCache.Remove(vehicle);
                    m_BypassTrackModelShadowThrottleCache.Remove(vehicle);
                    m_BypassTrackModelShadowLastLogFrame.Remove(vehicle);
                    m_TrainLaneSourceDiagnosticLogCache.Remove(vehicle);
                    m_BypassTrackModelCompareLogCache.Remove(vehicle);
                    m_BypassTrackModelCompareThrottleCache.Remove(vehicle);
                    m_BypassTrackModelCompareLastLogFrame.Remove(vehicle);
                    m_SharedWindowAuditSummaryLogCache.Remove(vehicle);
                    m_SharedWindowAuditThrottleCache.Remove(vehicle);
                    m_SharedWindowAuditLastLogFrame.Remove(vehicle);
                    List<SharedWindowPairStateKey> sharedPairKeysToRemove = null;
                    foreach (KeyValuePair<SharedWindowPairStateKey, string> pairEntry in m_SharedWindowAuditPairStateCache)
                    {
                        if (pairEntry.Key.LocalVehicle != vehicle)
                            continue;

                        sharedPairKeysToRemove ??= new List<SharedWindowPairStateKey>();
                        sharedPairKeysToRemove.Add(pairEntry.Key);
                    }

                    if (sharedPairKeysToRemove != null)
                    {
                        for (int removeIndex = 0; removeIndex < sharedPairKeysToRemove.Count; removeIndex++)
                            m_SharedWindowAuditPairStateCache.Remove(sharedPairKeysToRemove[removeIndex]);
                    }
                    m_TrackModelSequenceLogCache.Remove(vehicle);
                }
            }
        }

        private ulong ComputeLineTrackChainSignature(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, line.Index);
            hash = MixLineSignature(hash, waypoints.Length);
            hash = MixLineSignature(hash, segments.Length);

            for (int i = 0; i < waypoints.Length; i++)
                hash = MixLineSignature(hash, waypoints[i].m_Waypoint.Index);

            for (int i = 0; i < segments.Length; i++)
            {
                Entity segmentEntity = segments[i].m_Segment;
                hash = MixLineSignature(hash, segmentEntity.Index);

                if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
                    continue;

                DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                hash = MixLineSignature(hash, pathElements.Length);
                for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
                {
                    PathElement pathElement = pathElements[pathIndex];
                    hash = MixLineSignature(hash, pathElement.m_Target.Index);
                    hash = MixLineSignature(hash, (int)pathElement.m_Flags);
                }
            }

            return hash;
        }

        private bool TryGetLineTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            chain = null;
            if (line == Entity.Null
                || waypoints.Length == 0
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                return false;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
                return false;

            ulong signature = ComputeLineTrackChainSignature(line, waypoints, segments);
            LineTrackChain previousChain = null;
            if (m_LineTrackChains.TryGetValue(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                return chain.TrackAtoms.Count > 0;
            }

            previousChain = chain;

            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            if (previousChain != null)
                RemoveDevSightLaneIndexForChain(previousChain);
            m_LineTrackChains[line] = chain;
            AddDevSightLaneIndexForChain(chain);
            m_DirtyTrackLines.Remove(line);
            m_SharedTrackIndexDirty = true;
            return true;
        }

        private LineTrackChain BuildLineTrackChain(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature)
        {
            var chain = new LineTrackChain
            {
                LineEntity = line,
                Signature = signature
            };

            for (int waypointIndex = 0; waypointIndex < segments.Length; waypointIndex++)
            {
                int startAtomIndex = chain.TrackAtoms.Count;
                Entity segmentEntity = segments[waypointIndex].m_Segment;
                if (EntityManager.HasBuffer<PathElement>(segmentEntity))
                {
                    DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
                    AppendSegmentTrackAtoms(chain.TrackAtoms, pathElements);
                }

                int endAtomIndexExclusive = chain.TrackAtoms.Count;
                chain.SegmentRanges.Add(new TrackSegmentRange(startAtomIndex, endAtomIndexExclusive));
                TryAppendControlPoint(chain.ControlPoints, waypoints, waypointIndex, startAtomIndex);
            }

            BuildControlEdges(chain, line, waypoints);
            BuildTraversalProfile(chain, line, waypoints);
            BuildAtomIndicesByLane(chain);
            return chain;
        }

        private void AppendSegmentTrackAtoms(List<TrackAtom> atoms, DynamicBuffer<PathElement> pathElements)
        {
            if (pathElements.Length == 0)
                return;

            for (int pathIndex = 0; pathIndex < pathElements.Length; pathIndex++)
            {
                PathElement element = pathElements[pathIndex];
                if (!TryClassifyTrackAtom(pathElements, pathIndex, out TrackAtom atom))
                    continue;

                if (atom.AtomClass == TrackAtomClass.FilteredNoise)
                    continue;

                atoms.Add(atom);
            }
        }

        private void BuildAtomIndicesByLane(LineTrackChain chain)
        {
            if (chain == null)
                return;

            chain.AtomIndicesByLane.Clear();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                AddAtomIndexForLane(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForLane(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);

                AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.Key.PhysicalLaneKey, atomIndex);
                if (atom.SourceTarget != atom.Key.PhysicalLaneKey)
                    AddAtomIndexForNetOwnerChain(chain.AtomIndicesByLane, atom.SourceTarget, atomIndex);
            }
        }

        private static void AddAtomIndexForLane(Dictionary<Entity, List<int>> indexByLane, Entity lane, int atomIndex)
        {
            if (lane == Entity.Null)
                return;

            if (!indexByLane.TryGetValue(lane, out List<int> atomIndices))
            {
                atomIndices = new List<int>();
                indexByLane[lane] = atomIndices;
            }

            atomIndices.Add(atomIndex);
        }

        private void AddAtomIndexForNetOwnerChain(Dictionary<Entity, List<int>> indexByLane, Entity entity, int atomIndex)
        {
            Entity current = entity;
            for (int i = 0; i < 4 && current != Entity.Null; i++)
            {
                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                if (EntityManager.HasComponent<Game.Net.Edge>(owner)
                    || EntityManager.HasComponent<Game.Net.Node>(owner)
                    || EntityManager.HasBuffer<Game.Net.SubLane>(owner))
                {
                    AddAtomIndexForLane(indexByLane, owner, atomIndex);
                }

                current = owner;
            }
        }

        private void AddDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || entry.Value == null
                    || entry.Value.Count == 0)
                    continue;

                if (!m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences))
                {
                    occurrences = new List<DevSightLaneOccurrence>();
                    m_DevSightLaneIndex[entry.Key] = occurrences;
                }

                occurrences.Add(new DevSightLaneOccurrence(chain.LineEntity, chain, entry.Value));
            }
        }

        private void RemoveDevSightLaneIndexForChain(LineTrackChain chain)
        {
            if (chain == null || chain.AtomIndicesByLane == null)
                return;

            foreach (KeyValuePair<Entity, List<int>> entry in chain.AtomIndicesByLane)
            {
                if (entry.Key == Entity.Null
                    || !m_DevSightLaneIndex.TryGetValue(entry.Key, out List<DevSightLaneOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                for (int i = occurrences.Count - 1; i >= 0; i--)
                {
                    if (occurrences[i].LineEntity == chain.LineEntity)
                        occurrences.RemoveAt(i);
                }

                if (occurrences.Count == 0)
                    m_DevSightLaneIndex.Remove(entry.Key);
            }
        }

        public string BuildDevSightLaneTooltipSummary(Entity laneEntity)
        {
            if (laneEntity == Entity.Null)
                return "target  null";

            bool hasIndex = m_DevSightLaneIndex.TryGetValue(laneEntity, out List<DevSightLaneOccurrence> occurrences);

            if (!hasIndex)
            {
                foreach (var kvp in m_DevSightLaneIndex)
                {
                    if (kvp.Key.Index == laneEntity.Index)
                    {
                        occurrences = kvp.Value;
                        hasIndex = true;
                        break;
                    }
                }
            }

            if (!hasIndex || occurrences == null || occurrences.Count == 0)
            {
                return "target  " + FormatEntityRef(laneEntity) + "\ntrack model  no-chain-hit";
            }
            StringBuilder result = new StringBuilder(256);
            result.Append("target  ").Append(FormatEntityRef(laneEntity));

            for (int i = 0; i < occurrences.Count; i++)
            {
                result.Append('\n').Append(FormatDevSightOccurrence(occurrences[i]));
            }

            return result.ToString();
        }

        private string FormatDevSightOccurrence(DevSightLaneOccurrence occurrence)
        {
            string lineLabel = ResolveTrackModelLineLabel(occurrence.LineEntity, includeEntityFallback: false);
            return lineLabel + " atoms=" + FormatDevSightAtomIndices(occurrence.AtomIndices);
        }

        private string FormatDevSightAtomIndices(List<int> atomIndices)
        {
            if (atomIndices == null || atomIndices.Count == 0)
                return "[]";

            if (atomIndices.Count <= 3)
                return "[" + string.Join(",", atomIndices) + "]";

            return "[" + atomIndices[0] + "," + atomIndices[1] + ".." + atomIndices[atomIndices.Count - 1] + " x" + atomIndices.Count + "]";
        }

        private static string FormatEntityRef(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index + ":" + entity.Version;
        }

        private bool TryClassifyTrackAtom(
            DynamicBuffer<PathElement> pathElements,
            int pathIndex,
            out TrackAtom atom)
        {
            atom = default;
            if (pathIndex < 0 || pathIndex >= pathElements.Length)
                return false;

            PathElement element = pathElements[pathIndex];
            if (element.m_Target == Entity.Null)
                return false;

            TrackAtomClass atomClass = ClassifyPathElementTarget(element);
            TrackTraversalDir traversalDir = ResolveTraversalDirection(pathElements, pathIndex);
            Entity previousTarget = pathIndex > 0 ? pathElements[pathIndex - 1].m_Target : Entity.Null;
            Entity nextTarget = pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target : Entity.Null;
            TrackAtomKey key = new TrackAtomKey(element.m_Target, previousTarget, nextTarget);
            atom = new TrackAtom(key, element.m_Target, element.m_Flags, atomClass, traversalDir);
            return true;
        }

        private TrackAtomClass ClassifyPathElementTarget(PathElement element)
        {
            if ((element.m_Flags & (PathElementFlags.Action | PathElementFlags.WaitPosition | PathElementFlags.Hangaround)) != 0)
                return TrackAtomClass.FilteredNoise;

            if (EntityManager.HasComponent<TrackLane>(element.m_Target))
                return TrackAtomClass.PrimaryLane;

            if (EntityManager.HasComponent<ConnectionLane>(element.m_Target))
            {
                ConnectionLane connectionLane = EntityManager.GetComponentData<ConnectionLane>(element.m_Target);
                if (connectionLane.m_TrackTypes != TrackTypes.None)
                    return TrackAtomClass.ConnectionHelper;
            }

            if (EntityManager.HasComponent<EdgeLane>(element.m_Target))
                return TrackAtomClass.ConnectionHelper;

            if ((element.m_Flags & (PathElementFlags.Secondary | PathElementFlags.Return | PathElementFlags.Leader)) != 0)
                return TrackAtomClass.ConnectionHelper;

            return TrackAtomClass.PrimaryLane;
        }

        private TrackTraversalDir ResolveTraversalDirection(DynamicBuffer<PathElement> pathElements, int pathIndex)
        {
            PathElement current = pathElements[pathIndex];
            if (current.m_Target == Entity.Null)
                return TrackTraversalDir.Unknown;

            bool reverseFlag = (current.m_Flags & PathElementFlags.Reverse) != 0;
            if (EntityManager.HasComponent<EdgeLane>(current.m_Target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(current.m_Target);
                bool edgeForward = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                if (EntityManager.HasComponent<TrackLane>(current.m_Target))
                {
                    TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                    if ((trackLane.m_Flags & TrackLaneFlags.Invert) != 0)
                        edgeForward = !edgeForward;
                }

                if (reverseFlag)
                    edgeForward = !edgeForward;

                return edgeForward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (EntityManager.HasComponent<TrackLane>(current.m_Target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                bool forward = (trackLane.m_Flags & TrackLaneFlags.Invert) == 0;
                if (reverseFlag)
                    forward = !forward;
                return forward ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (pathElements.Length <= 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            float laneProgress = current.m_TargetDelta.y - current.m_TargetDelta.x;
            if (math.abs(laneProgress) > 0.0001f)
            {
                bool forwardByDelta = laneProgress >= 0f;
                if (reverseFlag)
                    forwardByDelta = !forwardByDelta;
                return forwardByDelta ? TrackTraversalDir.Forward : TrackTraversalDir.Reverse;
            }

            if (pathIndex == 0 || pathIndex == pathElements.Length - 1)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            PathElement previous = pathElements[pathIndex - 1];
            PathElement next = pathElements[pathIndex + 1];
            if (previous.m_Target != Entity.Null && previous.m_Target == next.m_Target)
                return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Unknown;

            return reverseFlag ? TrackTraversalDir.Reverse : TrackTraversalDir.Forward;
        }

        private string DescribeTraversalInputs(DynamicBuffer<PathElement> pathElements, int pathIndex)
        {
            if (pathIndex < 0 || pathIndex >= pathElements.Length)
                return "ctx=invalid";

            PathElement current = pathElements[pathIndex];
            Entity previousTarget = pathIndex > 0 ? pathElements[pathIndex - 1].m_Target : Entity.Null;
            Entity nextTarget = pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target : Entity.Null;
            bool reverseFlag = (current.m_Flags & PathElementFlags.Reverse) != 0;

            StringBuilder sb = new StringBuilder();
            sb.Append(" prev=").Append(previousTarget == Entity.Null ? "null" : previousTarget.Index.ToString())
              .Append(" curr=").Append(current.m_Target == Entity.Null ? "null" : current.m_Target.Index.ToString())
              .Append(" next=").Append(nextTarget == Entity.Null ? "null" : nextTarget.Index.ToString())
              .Append(" reverseFlag=").Append(reverseFlag ? "1" : "0");

            if (EntityManager.HasComponent<TrackLane>(current.m_Target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(current.m_Target);
                bool invert = (trackLane.m_Flags & TrackLaneFlags.Invert) != 0;
                sb.Append(" invert=").Append(invert ? "1" : "0");
            }

            if (EntityManager.HasComponent<EdgeLane>(current.m_Target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(current.m_Target);
                bool edgeForward = edgeLane.m_EdgeDelta.y >= edgeLane.m_EdgeDelta.x;
                sb.Append(" edgeForward=").Append(edgeForward ? "1" : "0")
                  .Append(" edgeDelta=(")
                  .Append(edgeLane.m_EdgeDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(edgeLane.m_EdgeDelta.y.ToString("F2"))
                  .Append(")");
            }

            float laneProgress = current.m_TargetDelta.y - current.m_TargetDelta.x;
            sb.Append(" laneProgress=").Append(laneProgress.ToString("F2"));
            return sb.ToString();
        }

        private string DescribePathElementTarget(Entity target)
        {
            if (target == Entity.Null || !EntityManager.Exists(target))
                return "null";

            StringBuilder sb = new StringBuilder();
            sb.Append("target=").Append(target.Index);

            if (EntityManager.HasComponent<TrackLane>(target))
            {
                TrackLane trackLane = EntityManager.GetComponentData<TrackLane>(target);
                sb.Append(" TrackLane")
                  .Append(" flags=").Append(trackLane.m_Flags)
                  .Append(" speed=").Append(trackLane.m_SpeedLimit.ToString("F1"));
            }

            if (EntityManager.HasComponent<Lane>(target))
                sb.Append(" Lane");

            if (EntityManager.HasComponent<EdgeLane>(target))
            {
                EdgeLane edgeLane = EntityManager.GetComponentData<EdgeLane>(target);
                sb.Append(" EdgeLane")
                  .Append(" edgeDelta=(")
                  .Append(edgeLane.m_EdgeDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(edgeLane.m_EdgeDelta.y.ToString("F2"))
                  .Append(")");
            }

            if (EntityManager.HasComponent<ConnectionLane>(target))
            {
                ConnectionLane connectionLane = EntityManager.GetComponentData<ConnectionLane>(target);
                sb.Append(" ConnectionLane")
                  .Append(" trackTypes=").Append(connectionLane.m_TrackTypes)
                  .Append(" flags=").Append(connectionLane.m_Flags);
            }

            if (EntityManager.HasComponent<TrainTrack>(target))
                sb.Append(" TrainTrack");
            if (EntityManager.HasComponent<TramTrack>(target))
                sb.Append(" TramTrack");
            if (EntityManager.HasComponent<SubwayTrack>(target))
                sb.Append(" SubwayTrack");

            return sb.ToString();
        }

        private void LogRouteSegmentPathElementDiagnostics(Entity line, int waypointIndex, Entity segmentEntity)
        {
            if (segmentEntity == Entity.Null || !EntityManager.Exists(segmentEntity))
            {
                log.Info("[TrackModelRaw] line=" + line.Index + " wp=" + waypointIndex + " segment=null");
                return;
            }

            if (!EntityManager.HasBuffer<PathElement>(segmentEntity))
            {
                log.Info("[TrackModelRaw] line=" + line.Index + " wp=" + waypointIndex + " segment=" + segmentEntity.Index + " pathElements=none");
                return;
            }

            DynamicBuffer<PathElement> pathElements = EntityManager.GetBuffer<PathElement>(segmentEntity, true);
            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModelRaw] line=").Append(line.Index)
              .Append(" wp=").Append(waypointIndex)
              .Append(" segment=").Append(segmentEntity.Index)
              .Append(" pathCount=").Append(pathElements.Length);

            int limit = math.min(pathElements.Length, 16);
            for (int pathIndex = 0; pathIndex < limit; pathIndex++)
            {
                PathElement element = pathElements[pathIndex];
                TrackAtomClass atomClass = ClassifyPathElementTarget(element);
                TrackTraversalDir traversalDir = ResolveTraversalDirection(pathElements, pathIndex);
                sb.Append(" | ")
                  .Append(pathIndex)
                  .Append(":")
                  .Append(DescribePathElementTarget(element.m_Target))
                  .Append(" flags=").Append(element.m_Flags)
                  .Append(" delta=(")
                  .Append(element.m_TargetDelta.x.ToString("F2"))
                  .Append(",")
                  .Append(element.m_TargetDelta.y.ToString("F2"))
                  .Append(")")
                  .Append(" class=").Append(atomClass)
                  .Append(" dir=").Append(traversalDir)
                  .Append(" token=").Append(pathIndex > 0 ? pathElements[pathIndex - 1].m_Target.Index.ToString() : "null")
                  .Append("->").Append(element.m_Target.Index)
                  .Append("->").Append(pathIndex + 1 < pathElements.Length ? pathElements[pathIndex + 1].m_Target.Index.ToString() : "null")
                  .Append(DescribeTraversalInputs(pathElements, pathIndex));
            }

            log.Info(sb.ToString());
        }

        private void LogSharedTrackIndexSummary()
        {
            EnsureSharedTrackIndexCurrent();

            int sharedAtomKeys = 0;
            int sharedOccurrences = 0;
            var contextCountByPhysicalTarget = new Dictionary<Entity, int>();
            var adjacencyByPhysicalTarget = new Dictionary<Entity, HashSet<string>>();
            foreach (KeyValuePair<TrackAtomKey, List<SharedTrackOccurrence>> entry in m_SharedTrackIndex)
            {
                if (!contextCountByPhysicalTarget.TryGetValue(entry.Key.PhysicalLaneKey, out int contextCount))
                    contextCount = 0;

                contextCountByPhysicalTarget[entry.Key.PhysicalLaneKey] = contextCount + 1;

                if (!adjacencyByPhysicalTarget.TryGetValue(entry.Key.PhysicalLaneKey, out HashSet<string> adjacencySet))
                {
                    adjacencySet = new HashSet<string>(StringComparer.Ordinal);
                    adjacencyByPhysicalTarget[entry.Key.PhysicalLaneKey] = adjacencySet;
                }

                string previous = entry.Key.PreviousTarget == Entity.Null ? "null" : entry.Key.PreviousTarget.Index.ToString();
                string next = entry.Key.NextTarget == Entity.Null ? "null" : entry.Key.NextTarget.Index.ToString();
                adjacencySet.Add(previous + ">" + next);

                if (entry.Value == null || entry.Value.Count <= 1)
                    continue;

                sharedAtomKeys++;
                sharedOccurrences += entry.Value.Count;
            }

            int contextSplitTargets = 0;
            int mirroredTargets = 0;
            foreach (KeyValuePair<Entity, int> entry in contextCountByPhysicalTarget)
            {
                if (entry.Value > 1)
                    contextSplitTargets++;
            }

            foreach (KeyValuePair<Entity, HashSet<string>> entry in adjacencyByPhysicalTarget)
            {
                bool hasMirror = false;
                foreach (string pair in entry.Value)
                {
                    int separator = pair.IndexOf('>');
                    if (separator < 0)
                        continue;

                    string previous = pair.Substring(0, separator);
                    string next = pair.Substring(separator + 1);
                    if (entry.Value.Contains(next + ">" + previous))
                    {
                        hasMirror = true;
                        break;
                    }
                }

                if (hasMirror)
                    mirroredTargets++;
            }

            log.Info("[TrackModelShared] keys=" + m_SharedTrackIndex.Count
                + " physicalTargets=" + contextCountByPhysicalTarget.Count
                + " sharedKeys=" + sharedAtomKeys
                + " sharedOccurrences=" + sharedOccurrences
                + " contextSplitTargets=" + contextSplitTargets
                + " mirroredTargets=" + mirroredTargets);
        }

        private void TryAppendControlPoint(
            List<ControlPointMarker> controlPoints,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            int atomIndex)
        {
            Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
            if (building == Entity.Null)
                return;

            ControlPointKind kind = ControlPointKind.Stop;
            if (IsBypassStation(building))
                kind = ControlPointKind.Bypass;

            controlPoints.Add(new ControlPointMarker(atomIndex, waypointIndex, building, kind));
        }

        private void BuildControlEdges(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain.ControlPoints.Count < 2)
                return;

            float lineFrames = GetLineLoopFramesEstimate(line, waypoints);
            int atomCount = math.max(1, chain.TrackAtoms.Count);
            bool hasProfile = TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile);

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count - 1; controlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[controlPointIndex];
                ControlPointMarker end = chain.ControlPoints[controlPointIndex + 1];
                int startAtomIndex = math.clamp(start.AtomIndex, 0, atomCount - 1);
                int endAtomIndexExclusive = math.clamp(math.max(startAtomIndex + 1, end.AtomIndex), 1, atomCount);
                float baseFrames = 0f;
                if (hasProfile)
                {
                    int startWaypointIndex = math.clamp(start.WaypointIndex, 0, waypoints.Length - 1);
                    int endWaypointIndex = math.clamp(end.WaypointIndex, 0, waypoints.Length - 1);
                    baseFrames = ComputeDepartureToWaypointFramesFromProfile(profile, startWaypointIndex, endWaypointIndex);
                }

                if (!(baseFrames > 0f))
                {
                    float ratio = (endAtomIndexExclusive - startAtomIndex) / (float)atomCount;
                    baseFrames = lineFrames > 0f ? lineFrames * ratio : 0f;
                }

                chain.ControlEdges.Add(new ControlEdge(
                    controlPointIndex,
                    controlPointIndex + 1,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }
        }

        private readonly struct StationPassRange
        {
            public readonly Entity Building;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly int WaypointIndex;
            public readonly float StopFrames;
            public readonly int PassIndex;

            public StationPassRange(
                Entity building,
                int startAtomIndex,
                int endAtomIndexExclusive,
                int waypointIndex,
                float stopFrames,
                int passIndex)
            {
                Building = building;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                WaypointIndex = waypointIndex;
                StopFrames = stopFrames;
                PassIndex = passIndex;
            }
        }

        private void BuildTraversalProfile(LineTrackChain chain, Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null)
                return;

            chain.TraversalProfile.Events.Clear();
            chain.TraversalProfile.RunSlices.Clear();
            if (chain.TrackAtoms.Count == 0)
                return;

            bool hasTimeProfile = TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader timeProfile);
            float lineFrames = GetLineLoopFramesEstimate(line, waypoints);
            List<StationPassRange> stationPasses = CollectStationPassRanges(chain, line, waypoints);
            if (stationPasses.Count == 0)
                return;

            Dictionary<int, int> boundaryEventIndexByAtom = new Dictionary<int, int>();
            List<int> boundaries = new List<int> { 0, chain.TrackAtoms.Count };

            for (int passIndex = 0; passIndex < stationPasses.Count; passIndex++)
            {
                StationPassRange pass = stationPasses[passIndex];
                if (!boundaries.Contains(pass.StartAtomIndex))
                    boundaries.Add(pass.StartAtomIndex);
                if (!boundaries.Contains(pass.EndAtomIndexExclusive))
                    boundaries.Add(pass.EndAtomIndexExclusive);

                int approachIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    approachIndex,
                    TraversalEventKind.ApproachSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.StartAtomIndex,
                    0f));
                boundaryEventIndexByAtom[pass.StartAtomIndex] = approachIndex;

                int stationEventIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    stationEventIndex,
                    pass.WaypointIndex >= 0 ? TraversalEventKind.Stop : TraversalEventKind.Pass,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.StartAtomIndex,
                    pass.EndAtomIndexExclusive,
                    pass.StopFrames));

                int departureIndex = chain.TraversalProfile.Events.Count;
                chain.TraversalProfile.Events.Add(new TraversalEvent(
                    departureIndex,
                    TraversalEventKind.DepartureSplitBoundary,
                    pass.Building,
                    pass.WaypointIndex,
                    pass.PassIndex,
                    pass.EndAtomIndexExclusive,
                    pass.EndAtomIndexExclusive,
                    0f));
                boundaryEventIndexByAtom[pass.EndAtomIndexExclusive] = departureIndex;
            }

            boundaries.Sort();
            for (int boundaryIndex = 0; boundaryIndex < boundaries.Count - 1; boundaryIndex++)
            {
                int startAtomIndex = boundaries[boundaryIndex];
                int endAtomIndexExclusive = boundaries[boundaryIndex + 1];
                if (endAtomIndexExclusive <= startAtomIndex)
                    continue;

                int sliceIndex = chain.TraversalProfile.RunSlices.Count;
                boundaryEventIndexByAtom.TryGetValue(startAtomIndex, out int startEventIndex);
                boundaryEventIndexByAtom.TryGetValue(endAtomIndexExclusive, out int endEventIndex);
                chain.TraversalProfile.RunSlices.Add(new TraversalRunSlice(
                    sliceIndex,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    startEventIndex,
                    endEventIndex,
                    CollectTraversalSlicePhysicalLaneKeys(chain, startAtomIndex, endAtomIndexExclusive),
                    EstimateTraversalRunSliceFrames(
                        chain,
                        startAtomIndex,
                        endAtomIndexExclusive,
                        hasTimeProfile,
                        timeProfile,
                        lineFrames)));
            }
        }

        private List<StationPassRange> CollectStationPassRanges(
            LineTrackChain chain,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            List<StationPassRange> stationPasses = new List<StationPassRange>();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return stationPasses;

            bool hasLinePrefabData = TryGetTraversalProfileLineData(line, out Game.Prefabs.TransportLineData prefabLineData);
            Dictionary<Entity, int> passCountByBuilding = new Dictionary<Entity, int>();

            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count;)
            {
                Entity building = ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget);
                if (building == Entity.Null)
                {
                    atomIndex++;
                    continue;
                }

                int startAtomIndex = atomIndex;
                atomIndex++;
                while (atomIndex < chain.TrackAtoms.Count
                    && ResolvePassingStationBuilding(chain.TrackAtoms[atomIndex].SourceTarget) == building)
                {
                    atomIndex++;
                }

                int endAtomIndexExclusive = atomIndex;
                int passIndex = passCountByBuilding.TryGetValue(building, out int existingPassCount)
                    ? existingPassCount
                    : 0;
                passCountByBuilding[building] = passIndex + 1;

                int waypointIndex = -1;
                float stopFrames = 0f;
                if (TryFindTraversalStopWaypointIndex(chain, building, startAtomIndex, endAtomIndexExclusive, out int matchedWaypointIndex))
                {
                    waypointIndex = matchedWaypointIndex;
                    if (hasLinePrefabData)
                        stopFrames = GetProfileWaypointStopFrames(line, waypoints, matchedWaypointIndex, prefabLineData);
                }

                stationPasses.Add(new StationPassRange(
                    building,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    waypointIndex,
                    stopFrames,
                    passIndex));
            }

            return stationPasses;
        }

        private bool TryGetTraversalProfileLineData(Entity line, out Game.Prefabs.TransportLineData prefabLineData)
        {
            prefabLineData = default;
            if (line == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.PrefabRef>(line))
            {
                return false;
            }

            Entity prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<Game.Prefabs.TransportLineData>(prefab))
                return false;

            prefabLineData = EntityManager.GetComponentData<Game.Prefabs.TransportLineData>(prefab);
            return true;
        }

        private bool TryFindTraversalStopWaypointIndex(
            LineTrackChain chain,
            Entity building,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (chain == null || building == Entity.Null)
                return false;

            for (int controlPointIndex = 0; controlPointIndex < chain.ControlPoints.Count; controlPointIndex++)
            {
                ControlPointMarker marker = chain.ControlPoints[controlPointIndex];
                if ((marker.Kind != ControlPointKind.Stop && marker.Kind != ControlPointKind.Bypass)
                    || marker.Building != building
                    || marker.AtomIndex < startAtomIndex
                    || marker.AtomIndex >= endAtomIndexExclusive)
                {
                    continue;
                }

                waypointIndex = marker.WaypointIndex;
                return true;
            }

            return false;
        }

        private static Entity[] CollectTraversalSlicePhysicalLaneKeys(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0 || endAtomIndexExclusive <= startAtomIndex)
                return Array.Empty<Entity>();

            HashSet<Entity> keys = new HashSet<Entity>();
            int start = math.max(0, startAtomIndex);
            int end = math.min(endAtomIndexExclusive, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < end; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                keys.Add(atom.Key.PhysicalLaneKey);
            }

            if (keys.Count == 0)
                return Array.Empty<Entity>();

            Entity[] result = new Entity[keys.Count];
            keys.CopyTo(result);
            return result;
        }

        private float EstimateTraversalRunSliceFrames(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            bool hasTimeProfile,
            LineTimeProfileHeader timeProfile,
            float lineFrames)
        {
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return 0f;
            }

            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);

            if (hasTimeProfile && timeProfile.m_Count == chain.SegmentRanges.Count)
            {
                float runFrames = 0f;
                for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
                {
                    TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                    int overlapStart = math.max(startAtomIndex, range.StartAtomIndex);
                    int overlapEndExclusive = math.min(endAtomIndexExclusive, range.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    int segmentAtomLength = math.max(1, range.EndAtomIndexExclusive - range.StartAtomIndex);
                    int overlapAtomLength = overlapEndExclusive - overlapStart;
                    runFrames += m_LineTimeProfileSegmentFrames[timeProfile.m_Offset + segmentIndex]
                        * (overlapAtomLength / (float)segmentAtomLength);
                }

                if (runFrames > 0f)
                    return runFrames;
            }

            float atomCount = math.max(1f, chain.TrackAtoms.Count);
            return lineFrames > 0f
                ? lineFrames * ((endAtomIndexExclusive - startAtomIndex) / atomCount)
                : 0f;
        }

        private void RebuildSharedTrackIndex()
        {
            m_SharedTrackIndex.Clear();
            m_SharedPhysicalTrackIndex.Clear();

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                    continue;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (!m_SharedTrackIndex.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences))
                    {
                        occurrences = new List<SharedTrackOccurrence>();
                        m_SharedTrackIndex[atom.Key] = occurrences;
                    }

                    int waypointSegmentIndex = ResolveWaypointSegmentIndex(chain, atomIndex);
                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, waypointSegmentIndex));

                    Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                    if (!m_SharedPhysicalTrackIndex.TryGetValue(physicalLaneKey, out List<SharedPhysicalOccurrence> physicalOccurrences))
                    {
                        physicalOccurrences = new List<SharedPhysicalOccurrence>();
                        m_SharedPhysicalTrackIndex[physicalLaneKey] = physicalOccurrences;
                    }

                    physicalOccurrences.Add(new SharedPhysicalOccurrence(
                        line,
                        atomIndex,
                        waypointSegmentIndex,
                        atom.Key.PreviousTarget,
                        atom.Key.NextTarget));
                }
            }

            m_SharedTrackIndexDirty = false;
            m_SharedTrackIndexVersion++;
        }

        private void EnsureSharedTrackIndexCurrent()
        {
            if (!m_SharedTrackIndexDirty)
                return;

            RebuildSharedTrackIndex();
        }

        private static int ResolveWaypointSegmentIndex(LineTrackChain chain, int atomIndex)
        {
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        private void RefreshSharedRuns(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            if (chain.SharedRunsVersion == m_SharedTrackIndexVersion)
                return;

            chain.SharedRuns.Clear();
            chain.SharedRunsByOtherLine.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ControlEdgeSharedSpansReady = false;
            chain.BypassProtectedIntervalsReady = false;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;

            int runStart = -1;
            bool runMirrored = false;
            int runSharedLineCount = 0;
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane
                    || !TryGetSharedPhysicalContext(chain.LineEntity, atom, out int sharedLineCount, out bool mirroredContext))
                {
                    if (runStart >= 0)
                    {
                        chain.SharedRuns.Add(new SharedTrackRun(runStart, atomIndex, runMirrored, runSharedLineCount));
                        runStart = -1;
                        runMirrored = false;
                        runSharedLineCount = 0;
                    }

                    continue;
                }

                if (runStart < 0)
                {
                    runStart = atomIndex;
                    runMirrored = mirroredContext;
                    runSharedLineCount = sharedLineCount;
                    continue;
                }

                runMirrored |= mirroredContext;
                runSharedLineCount = math.max(runSharedLineCount, sharedLineCount);
            }

            if (runStart >= 0)
                chain.SharedRuns.Add(new SharedTrackRun(runStart, chain.TrackAtoms.Count, runMirrored, runSharedLineCount));

            RefreshSharedRunsByOtherLine(chain);
            chain.SharedRunsVersion = m_SharedTrackIndexVersion;
        }

        private void EnsureTrackChainBypassPipelineReady(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            if (chain.BypassPipelineReadyVersion == m_SharedTrackIndexVersion
                && chain.ProtectedIntervalSummariesReady)
            {
                return;
            }

            RefreshSharedRuns(chain);
            RefreshControlEdgeSharedSpans(chain);
            RefreshBypassProtectedIntervals(chain);
            RefreshProtectedSharedIntervals(chain);
            RefreshProtectedIntervalSummaries(chain);

            chain.BypassPipelineReadyVersion =
                chain.SharedRunsVersion == m_SharedTrackIndexVersion
                && chain.ControlEdgeSharedSpansReady
                && chain.BypassProtectedIntervalsReady
                && chain.ProtectedSharedIntervalsReady
                && chain.ProtectedIntervalSummariesReady
                    ? m_SharedTrackIndexVersion
                    : 0;
        }

        private void RefreshSharedRunsByOtherLine(LineTrackChain chain)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return;

            var candidateLines = new HashSet<Entity>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                if (!m_SharedPhysicalTrackIndex.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                    || occurrences == null)
                {
                    continue;
                }

                foreach (SharedPhysicalOccurrence occurrence in occurrences)
                {
                    if (occurrence.LineEntity != chain.LineEntity)
                        candidateLines.Add(occurrence.LineEntity);
                }
            }

            foreach (Entity otherLine in candidateLines)
            {
                int runStart = -1;
                bool runMirrored = false;
                List<SharedTrackRun> runs = null;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane
                        || !TryGetSharedPhysicalContextForLine(chain.LineEntity, atom, otherLine, out bool mirroredContext))
                    {
                        if (runStart >= 0)
                        {
                            runs ??= new List<SharedTrackRun>();
                            runs.Add(new SharedTrackRun(runStart, atomIndex, runMirrored, 1));
                            runStart = -1;
                            runMirrored = false;
                        }

                        continue;
                    }

                    if (runStart < 0)
                    {
                        runStart = atomIndex;
                        runMirrored = mirroredContext;
                        continue;
                    }

                    runMirrored |= mirroredContext;
                }

                if (runStart >= 0)
                {
                    runs ??= new List<SharedTrackRun>();
                    runs.Add(new SharedTrackRun(runStart, chain.TrackAtoms.Count, runMirrored, 1));
                }

                if (runs != null && runs.Count > 0)
                    chain.SharedRunsByOtherLine[otherLine] = runs;
            }
        }

        private readonly struct ConflictCorridor
        {
            public readonly int ProtectedIntervalIndex;
            public readonly int StartAtomIndex;
            public readonly int EndAtomIndexExclusive;
            public readonly int AnchorSharedStartAtomIndex;
            public readonly int AnchorSharedEndAtomIndexExclusive;
            public readonly int SharedSliceCount;
            public readonly int BridgedGapAtoms;

            public ConflictCorridor(
                int protectedIntervalIndex,
                int startAtomIndex,
                int endAtomIndexExclusive,
                int anchorSharedStartAtomIndex,
                int anchorSharedEndAtomIndexExclusive,
                int sharedSliceCount,
                int bridgedGapAtoms)
            {
                ProtectedIntervalIndex = protectedIntervalIndex;
                StartAtomIndex = startAtomIndex;
                EndAtomIndexExclusive = endAtomIndexExclusive;
                AnchorSharedStartAtomIndex = anchorSharedStartAtomIndex;
                AnchorSharedEndAtomIndexExclusive = anchorSharedEndAtomIndexExclusive;
                SharedSliceCount = sharedSliceCount;
                BridgedGapAtoms = bridgedGapAtoms;
            }
        }

        private readonly struct SuspectProgressSample
        {
            public readonly int ProjectedAtomIndex;
            public readonly int BestAtomIndex;
            public readonly float3 VehiclePosition;
            public readonly float ProjectedDistanceMeters;
            public readonly float BestDistanceMeters;

            public SuspectProgressSample(
                int projectedAtomIndex,
                int bestAtomIndex,
                float3 vehiclePosition,
                float projectedDistanceMeters,
                float bestDistanceMeters)
            {
                ProjectedAtomIndex = projectedAtomIndex;
                BestAtomIndex = bestAtomIndex;
                VehiclePosition = vehiclePosition;
                ProjectedDistanceMeters = projectedDistanceMeters;
                BestDistanceMeters = bestDistanceMeters;
            }
        }

        private void RefreshControlEdgeSharedSpans(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdgeSharedSpansReady)
                return;

            chain.ControlEdgeSharedSpans.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.SharedRuns.Count == 0 || chain.ControlEdges.Count == 0)
            {
                chain.ControlEdgeSharedSpansReady = true;
                return;
            }

            for (int controlEdgeIndex = 0; controlEdgeIndex < chain.ControlEdges.Count; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                for (int runIndex = 0; runIndex < chain.SharedRuns.Count; runIndex++)
                {
                    SharedTrackRun run = chain.SharedRuns[runIndex];
                    int overlapStart = math.max(edge.StartAtomIndex, run.StartAtomIndex);
                    int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, run.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    chain.ControlEdgeSharedSpans.Add(new ControlEdgeSharedSpan(
                        controlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        run.HasMirroredContext,
                        run.SharedLineCount));
                }
            }

            chain.ControlEdgeSharedSpansReady = true;
        }

        private void RefreshBypassProtectedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.BypassProtectedIntervalsReady)
                return;

            chain.BypassProtectedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedSharedIntervalsReady = false;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.ControlPoints.Count < 2 || chain.ControlEdges.Count == 0)
            {
                chain.BypassProtectedIntervalsReady = true;
                return;
            }

            for (int startControlPointIndex = 0; startControlPointIndex < chain.ControlPoints.Count - 1; startControlPointIndex++)
            {
                ControlPointMarker start = chain.ControlPoints[startControlPointIndex];
                if (start.Kind != ControlPointKind.Bypass)
                    continue;

                int endControlPointIndex = -1;
                for (int candidateIndex = startControlPointIndex + 1; candidateIndex < chain.ControlPoints.Count; candidateIndex++)
                {
                    if (chain.ControlPoints[candidateIndex].Kind == ControlPointKind.Bypass)
                    {
                        endControlPointIndex = candidateIndex;
                        break;
                    }
                }

                if (endControlPointIndex <= startControlPointIndex)
                    continue;

                int startControlEdgeIndex = startControlPointIndex;
                int endControlEdgeIndexInclusive = endControlPointIndex - 1;
                if (startControlEdgeIndex < 0 || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
                    continue;

                int startAtomIndex = math.max(0, chain.ControlPoints[startControlPointIndex].AtomIndex);
                int endAtomIndexExclusive = math.max(startAtomIndex + 1, chain.ControlPoints[endControlPointIndex].AtomIndex);
                float baseFrames = 0f;
                for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
                    baseFrames += chain.ControlEdges[controlEdgeIndex].BaseFrames;

                chain.BypassProtectedIntervals.Add(new BypassProtectedInterval(
                    startControlPointIndex,
                    endControlPointIndex,
                    startControlEdgeIndex,
                    endControlEdgeIndexInclusive,
                    startAtomIndex,
                    endAtomIndexExclusive,
                    baseFrames));
            }

            chain.BypassProtectedIntervalsReady = true;
        }

        private void RefreshProtectedSharedIntervals(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedSharedIntervalsReady)
                return;

            chain.ProtectedSharedIntervals.Clear();
            chain.BypassPipelineReadyVersion = 0;
            chain.ProtectedIntervalSummariesReady = false;
            if (chain.BypassProtectedIntervals.Count == 0 || chain.ControlEdgeSharedSpans.Count == 0)
            {
                chain.ProtectedSharedIntervalsReady = true;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                BypassProtectedInterval interval = chain.BypassProtectedIntervals[protectedIntervalIndex];
                for (int spanIndex = 0; spanIndex < chain.ControlEdgeSharedSpans.Count; spanIndex++)
                {
                    ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[spanIndex];
                    if (span.ControlEdgeIndex < interval.StartControlEdgeIndex || span.ControlEdgeIndex > interval.EndControlEdgeIndexInclusive)
                        continue;

                    int overlapStart = math.max(interval.StartAtomIndex, span.StartAtomIndex);
                    int overlapEndExclusive = math.min(interval.EndAtomIndexExclusive, span.EndAtomIndexExclusive);
                    if (overlapEndExclusive <= overlapStart)
                        continue;

                    float entryOffsetFrames = EstimateFramesBetweenAtoms(chain, interval.StartControlEdgeIndex, span.ControlEdgeIndex, interval.StartAtomIndex, overlapStart);
                    float clearOffsetFrames = EstimateFramesBetweenAtoms(chain, span.ControlEdgeIndex, interval.EndControlEdgeIndexInclusive, overlapEndExclusive, interval.EndAtomIndexExclusive);
                    chain.ProtectedSharedIntervals.Add(new ProtectedSharedInterval(
                        protectedIntervalIndex,
                        span.ControlEdgeIndex,
                        overlapStart,
                        overlapEndExclusive,
                        span.HasMirroredContext,
                        span.SharedLineCount,
                        entryOffsetFrames,
                        clearOffsetFrames));
                }
            }

            chain.ProtectedSharedIntervalsReady = true;
        }

        private void RefreshProtectedIntervalSummaries(LineTrackChain chain)
        {
            if (chain == null || chain.ProtectedIntervalSummariesReady)
                return;

            chain.ProtectedIntervalSummaries.Clear();
            chain.BypassPipelineReadyVersion = 0;
            if (chain.BypassProtectedIntervals.Count == 0)
            {
                chain.ProtectedIntervalSummariesReady = true;
                chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_SharedTrackIndexVersion ? m_SharedTrackIndexVersion : 0;
                return;
            }

            for (int protectedIntervalIndex = 0; protectedIntervalIndex < chain.BypassProtectedIntervals.Count; protectedIntervalIndex++)
            {
                int sharedSegmentCount = 0;
                int maxSharedLineCount = 0;
                bool hasMirroredContext = false;
                float minEntryOffsetFrames = float.MaxValue;
                float maxClearOffsetFrames = 0f;

                for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
                {
                    ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                    if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                        continue;

                    sharedSegmentCount++;
                    maxSharedLineCount = math.max(maxSharedLineCount, interval.SharedLineCount);
                    hasMirroredContext |= interval.HasMirroredContext;
                    minEntryOffsetFrames = math.min(minEntryOffsetFrames, interval.EntryOffsetFrames);
                    maxClearOffsetFrames = math.max(maxClearOffsetFrames, interval.ClearOffsetFrames);
                }

                if (sharedSegmentCount == 0)
                {
                    minEntryOffsetFrames = 0f;
                    maxClearOffsetFrames = 0f;
                }

                chain.ProtectedIntervalSummaries.Add(new ProtectedIntervalSummary(
                    protectedIntervalIndex,
                    sharedSegmentCount,
                    maxSharedLineCount,
                    hasMirroredContext,
                    minEntryOffsetFrames,
                    maxClearOffsetFrames));
            }

            chain.ProtectedIntervalSummariesReady = true;
            chain.BypassPipelineReadyVersion = chain.SharedRunsVersion == m_SharedTrackIndexVersion ? m_SharedTrackIndexVersion : 0;
        }

        private float EstimateFramesBetweenAtoms(LineTrackChain chain, int startControlEdgeIndex, int endControlEdgeIndexInclusive, int fromAtomIndex, int toAtomIndexExclusive)
        {
            if (toAtomIndexExclusive <= fromAtomIndex
                || startControlEdgeIndex < 0
                || endControlEdgeIndexInclusive < startControlEdgeIndex
                || endControlEdgeIndexInclusive >= chain.ControlEdges.Count)
            {
                return 0f;
            }

            float frames = 0f;
            for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                int overlapStart = math.max(edge.StartAtomIndex, fromAtomIndex);
                int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, toAtomIndexExclusive);
                if (overlapEndExclusive <= overlapStart)
                    continue;

                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                int overlapAtomLength = overlapEndExclusive - overlapStart;
                frames += edge.BaseFrames * (overlapAtomLength / (float)edgeAtomLength);
            }

            return frames;
        }

        private static float EstimateAverageControlEdgeFramesPerAtom(LineTrackChain chain)
        {
            if (chain == null || chain.ControlEdges == null || chain.ControlEdges.Count == 0)
                return 0f;

            float totalFrames = 0f;
            int totalAtoms = 0;
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                totalFrames += edge.BaseFrames;
                totalAtoms += edgeAtomLength;
            }

            return totalAtoms > 0 ? totalFrames / totalAtoms : 0f;
        }

        private bool TryResolveBypassProtectedInterval(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null || waypoints.Length == 0)
                return false;

            if (!TryGetBypassWaypointContext(
                    waypoints,
                    currentWaypointIndex,
                    out Entity currentBypassBuilding,
                    out int nextBypassWaypointIndex,
                    out Entity nextBypassBuilding))
            {
                return false;
            }

            int startControlPointIndex = FindControlPointIndex(chain, currentWaypointIndex, currentBypassBuilding, ControlPointKind.Bypass);
            int endControlPointIndex = FindControlPointIndex(chain, nextBypassWaypointIndex, nextBypassBuilding, ControlPointKind.Bypass);
            if (startControlPointIndex < 0 || endControlPointIndex <= startControlPointIndex)
                return false;

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                if (candidate.StartControlPointIndex == startControlPointIndex
                    && candidate.EndControlPointIndex == endControlPointIndex)
                {
                    protectedIntervalIndex = i;
                    protectedInterval = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveVehicleCurrentProtectedInterval(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null || waypoints.Length == 0)
                return false;

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
                return false;

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            return TryResolveProtectedIntervalByCursor(
                chain,
                currentControlEdgeIndex,
                cursor.AtomCursorIndex,
                out protectedIntervalIndex,
                out protectedInterval);
        }

        private static bool TryResolveProtectedIntervalByCursor(
            LineTrackChain chain,
            int currentControlEdgeIndex,
            int currentAtomIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null)
                return false;

            int bestRelativeScore = int.MinValue;
            int bestEntryDistanceAtoms = int.MaxValue;
            int bestIntervalLengthAtoms = int.MaxValue;
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, currentAtomIndex, candidate);
                if (relative == TrackModelRelativeToProtectedInterval.Unknown
                    || relative == TrackModelRelativeToProtectedInterval.After)
                {
                    continue;
                }

                int relativeScore = relative == TrackModelRelativeToProtectedInterval.Inside ? 2 : 1;
                int entryDistanceAtoms = math.max(0, candidate.StartAtomIndex - currentAtomIndex);
                int intervalLengthAtoms = math.max(1, candidate.EndAtomIndexExclusive - candidate.StartAtomIndex);
                bool better = protectedIntervalIndex < 0;
                if (!better && relativeScore != bestRelativeScore)
                    better = relativeScore > bestRelativeScore;
                if (!better && entryDistanceAtoms != bestEntryDistanceAtoms)
                    better = entryDistanceAtoms < bestEntryDistanceAtoms;
                if (!better && intervalLengthAtoms != bestIntervalLengthAtoms)
                    better = intervalLengthAtoms < bestIntervalLengthAtoms;
                if (!better)
                    continue;

                bestRelativeScore = relativeScore;
                bestEntryDistanceAtoms = entryDistanceAtoms;
                bestIntervalLengthAtoms = intervalLengthAtoms;
                protectedIntervalIndex = i;
                protectedInterval = candidate;
            }

            return protectedIntervalIndex >= 0;
        }

        private static int FindProtectedIntervalIndex(LineTrackChain chain, BypassProtectedInterval protectedInterval)
        {
            if (chain == null)
                return -1;

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                if (candidate.StartControlPointIndex == protectedInterval.StartControlPointIndex
                    && candidate.EndControlPointIndex == protectedInterval.EndControlPointIndex
                    && candidate.StartAtomIndex == protectedInterval.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == protectedInterval.EndAtomIndexExclusive)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out string resolutionSource)
        {
            m_BypassPerfProbeResolveCalls++;
            resolutionSource = "direct";
            if (TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out protectedIntervalIndex, out protectedInterval))
            {
                return true;
            }

            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null
                || localChain == null
                || waypoints.Length == 0
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            if (!TryResolveProtectedIntervalByCursor(
                    chain,
                    currentControlEdgeIndex,
                    cursor.AtomCursorIndex,
                    out protectedIntervalIndex,
                    out protectedInterval))
            {
                return false;
            }

            resolutionSource = "fallback";
            return true;
        }

        private bool TryResolveExpressConflictWindowForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            PhysicalSharedWindowMatch sharedWindowMatch,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out int overlapCount,
            out int orderedRun,
            out string resolutionSource)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            overlapCount = 0;
            orderedRun = 0;
            resolutionSource = "shared-window";

            // Consume the already-selected current shared-window first.
            // This keeps vehicle-level evaluation aligned with the current
            // local scene instead of collapsing back to a coarser
            // local-interval x express-interval matrix.
            if (sharedWindowMatch.Found && !sharedWindowMatch.Ambiguous)
            {
                protectedInterval = sharedWindowMatch.ExpressSharedWindow;
                overlapCount = sharedWindowMatch.OverlapCount;
                orderedRun = sharedWindowMatch.OrderedRun;
                return true;
            }

            if (chain == null
                || chain.BypassProtectedIntervals.Count > 0
                || !sharedWindowMatch.Found
                || sharedWindowMatch.Ambiguous)
            {
                return false;
            }

            protectedInterval = sharedWindowMatch.ExpressSharedWindow;
            overlapCount = sharedWindowMatch.OverlapCount;
            orderedRun = sharedWindowMatch.OrderedRun;
            resolutionSource = "shared-window";
            return true;
        }

        private static int FindControlPointIndex(LineTrackChain chain, int waypointIndex, Entity building, ControlPointKind kind)
        {
            for (int i = 0; i < chain.ControlPoints.Count; i++)
            {
                ControlPointMarker marker = chain.ControlPoints[i];
                if (marker.Kind == kind
                    && marker.WaypointIndex == waypointIndex
                    && marker.Building == building)
                {
                    return i;
                }
            }

            return -1;
        }

        private int CountProtectedSharedIntervals(LineTrackChain chain, int protectedIntervalIndex, out bool hasMirroredContext)
        {
            hasMirroredContext = false;
            if (protectedIntervalIndex < 0)
                return 0;

            int count = 0;
            for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
            {
                ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                if (interval.ProtectedIntervalIndex != protectedIntervalIndex)
                    continue;

                count++;
                hasMirroredContext |= interval.HasMirroredContext;
            }

            return count;
        }

        private static string FormatProtectedIntervalSummary(ProtectedIntervalSummary summary, BypassProtectedInterval interval)
        {
            return "trackModel[p=" + summary.ProtectedIntervalIndex
                + " cp=" + interval.StartControlPointIndex + "->" + interval.EndControlPointIndex
                + " edges=" + interval.StartControlEdgeIndex + ".." + interval.EndControlEdgeIndexInclusive
                + " shared=" + summary.SharedSegmentCount
                + " maxSharedLines=" + summary.MaxSharedLineCount
                + " mirrored=" + (summary.HasMirroredContext ? "1" : "0")
                + " minEntry=" + summary.MinEntryOffsetFrames.ToString("F1")
                + " maxClear=" + summary.MaxClearOffsetFrames.ToString("F1")
                + "]";
        }

        private static string ClassifyProtectedIntervalShadowRisk(ProtectedIntervalSummary summary)
        {
            if (summary.SharedSegmentCount <= 0)
                return "none";

            if (summary.HasMirroredContext)
                return "mirrored-shared";

            return "shared";
        }

        private bool TryProjectVehicleTrackCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain.SegmentRanges.Count == 0)
            {
                return false;
            }

            if (TryResolveTrainCurrentLaneCursor(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    out cursor))
            {
                if (m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor trainHint)
                    && trainHint.LineEntity == line
                    && trainHint.ChainSignature == chain.Signature)
                {
                    bool wrappedForward = trainHint.SegmentIndex >= chain.SegmentRanges.Count - 2 && cursor.SegmentIndex <= 1;
                    bool monotonicForward = cursor.SegmentIndex >= trainHint.SegmentIndex || wrappedForward;
                    if (!monotonicForward)
                    {
                        cursor = new VehicleTrackCursor(
                            cursor.LineEntity,
                            cursor.ChainSignature,
                            cursor.SegmentIndex,
                            cursor.AtomStartIndex,
                            cursor.AtomEndIndexExclusive,
                            cursor.AtomCursorIndex,
                            cursor.AtomPosition01,
                            cursor.Confidence * 0.7f);
                    }
                }

                if (IsVehicleProgressProjectionInvalid(vehicle, line, chain, cursor.SegmentIndex, cursor.AtomCursorIndex))
                    return false;

                m_VehicleTrackCursorHints[vehicle] = cursor;
                return true;
            }

            bool trustedRouteProgress = TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition);
            if (!trustedRouteProgress)
            {
                if (!m_CachedWpIdx.TryGetValue(vehicle, out nextWaypointIndex))
                    return false;
                segmentPosition = 0f;
            }

            bool boarding = false;
            if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                boarding = (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
            }

            if (boarding)
            {
                // Avoid recursive dependency between cursor projection and
                // waypoint anchoring when boarding vehicles lose a reliable
                // TrainCurrentLane projection.
                if (m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                    && cachedWaypointIndex >= 0
                    && cachedWaypointIndex < waypoints.Length)
                {
                    nextWaypointIndex = cachedWaypointIndex;
                    segmentPosition = 0f;
                    trustedRouteProgress = false;
                }
                else if (trustedRouteProgress && TryResolveStationAnchoredProgressFallback(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    nextWaypointIndex,
                    segmentPosition,
                    out int anchoredWaypointIndex))
                {
                    nextWaypointIndex = anchoredWaypointIndex;
                    segmentPosition = 0f;
                    trustedRouteProgress = false;
                }
            }
            else if (trustedRouteProgress && TryResolveStationAnchoredProgressFallback(
                vehicle,
                line,
                waypoints,
                chain,
                nextWaypointIndex,
                segmentPosition,
                out int anchoredWaypointIndex))
            {
                nextWaypointIndex = anchoredWaypointIndex;
                segmentPosition = 0f;
                trustedRouteProgress = false;
            }

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int segmentIndex = nextWaypointIndex == 0
                ? math.max(0, chain.SegmentRanges.Count - 1)
                : nextWaypointIndex - 1;
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            if (segmentRange.EndAtomIndexExclusive <= segmentRange.StartAtomIndex)
                return false;

            int segmentAtomLength = math.max(1, segmentRange.EndAtomIndexExclusive - segmentRange.StartAtomIndex);
            int approximateAtomIndex = segmentRange.StartAtomIndex
                + math.min(segmentAtomLength - 1, (int)math.floor(segmentAtomLength * math.saturate(segmentPosition)));

            float confidence = trustedRouteProgress ? 1f : 0.7f;
            if (m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                if (hint.SegmentIndex == segmentIndex)
                {
                    approximateAtomIndex = math.max(approximateAtomIndex, hint.AtomCursorIndex);
                }
                else
                {
                    bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && segmentIndex <= 1;
                    bool monotonicForward = segmentIndex >= hint.SegmentIndex || wrappedForward;
                    if (!monotonicForward)
                    {
                        confidence *= 0.4f;
                        approximateAtomIndex = math.max(segmentRange.StartAtomIndex, math.min(segmentRange.EndAtomIndexExclusive - 1, hint.AtomCursorIndex));
                    }
                }
            }

            if (IsVehicleProgressProjectionInvalid(vehicle, line, chain, segmentIndex, approximateAtomIndex))
                return false;

            approximateAtomIndex = math.clamp(approximateAtomIndex, segmentRange.StartAtomIndex, segmentRange.EndAtomIndexExclusive - 1);
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                approximateAtomIndex,
                math.saturate(segmentPosition),
                confidence);
            m_VehicleTrackCursorHints[vehicle] = cursor;
            return true;
        }

        private bool TryResolveTrainCurrentLaneCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null
                || chain == null
                || !EntityManager.HasComponent<Game.Vehicles.TrainCurrentLane>(vehicle))
            {
                return false;
            }

            Game.Vehicles.TrainCurrentLane currentLane = EntityManager.GetComponentData<Game.Vehicles.TrainCurrentLane>(vehicle);
            Entity frontLane = currentLane.m_Front.m_Lane;
            Entity rearLane = currentLane.m_Rear.m_Lane;
            float frontCurvePosition = math.saturate(currentLane.m_Front.m_CurvePosition.x);
            float rearCurvePosition = math.saturate(currentLane.m_Rear.m_CurvePosition.x);

            int referenceAtomIndex = m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature
                ? hint.AtomCursorIndex
                : -1;

            int preferredSegmentIndex = m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor segmentHint)
                && segmentHint.LineEntity == line
                && segmentHint.ChainSignature == chain.Signature
                ? segmentHint.SegmentIndex
                : -1;

            bool found = TryResolveSemanticLaneAtomCandidate(
                vehicle,
                line,
                waypoints,
                chain,
                frontLane,
                referenceAtomIndex,
                out int atomIndex);
            float atomPosition01 = frontCurvePosition;
            if (!found)
            {
                found = TryResolveSemanticLaneAtomCandidate(
                    vehicle,
                    line,
                    waypoints,
                    chain,
                    rearLane,
                    referenceAtomIndex,
                    out atomIndex);
                atomPosition01 = rearCurvePosition;
            }

            int searchStartAtomIndex = 0;
            int searchEndAtomIndexExclusive = chain.TrackAtoms.Count;
            if (preferredSegmentIndex >= 0 && preferredSegmentIndex < chain.SegmentRanges.Count)
            {
                int searchStartSegmentIndex = math.max(0, preferredSegmentIndex - 1);
                int searchEndSegmentIndex = math.min(chain.SegmentRanges.Count - 1, preferredSegmentIndex + 1);
                searchStartAtomIndex = chain.SegmentRanges[searchStartSegmentIndex].StartAtomIndex;
                searchEndAtomIndexExclusive = chain.SegmentRanges[searchEndSegmentIndex].EndAtomIndexExclusive;
            }

            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, frontLane, searchStartAtomIndex, searchEndAtomIndexExclusive, referenceAtomIndex, out atomIndex);
                atomPosition01 = frontCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, rearLane, searchStartAtomIndex, searchEndAtomIndexExclusive, referenceAtomIndex, out atomIndex);
                atomPosition01 = rearCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, frontLane, 0, chain.TrackAtoms.Count, referenceAtomIndex, out atomIndex);
                atomPosition01 = frontCurvePosition;
            }
            if (!found)
            {
                found = TryFindClosestAtomIndexForLane(chain, rearLane, 0, chain.TrackAtoms.Count, referenceAtomIndex, out atomIndex);
                atomPosition01 = rearCurvePosition;
            }
            if (!found)
                return false;

            int segmentIndex = ResolveSegmentIndexForAtom(chain, atomIndex);
            if (segmentIndex < 0 || segmentIndex >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
            cursor = new VehicleTrackCursor(
                line,
                chain.Signature,
                segmentIndex,
                segmentRange.StartAtomIndex,
                segmentRange.EndAtomIndexExclusive,
                atomIndex,
                atomPosition01,
                1f);
            return true;
        }

        private bool TryResolveSemanticLaneAtomCandidate(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            Entity lane,
            int referenceAtomIndex,
            out int atomIndex)
        {
            atomIndex = -1;
            if (lane == Entity.Null
                || chain == null
                || chain.SegmentRanges.Count == 0
                || waypoints.Length == 0)
            {
                return false;
            }

            int segmentCount = chain.SegmentRanges.Count;
            List<int> semanticSegments = new List<int>(4);
            void AddSegmentCandidate(int segmentIndex)
            {
                if (segmentIndex < 0)
                    return;

                int normalized = segmentIndex % segmentCount;
                if (normalized < 0)
                    normalized += segmentCount;

                if (!semanticSegments.Contains(normalized))
                    semanticSegments.Add(normalized);
            }

            void AddWaypointAnchor(int waypointIndex)
            {
                if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                    return;

                AddSegmentCandidate(waypointIndex == 0 ? segmentCount - 1 : waypointIndex - 1);
            }

            if (m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                AddSegmentCandidate(hint.SegmentIndex);
            }

            if (m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex))
                AddWaypointAnchor(cachedWaypointIndex);

            if (EntityManager.HasComponent<Target>(vehicle))
            {
                Entity targetWaypoint = EntityManager.GetComponentData<Target>(vehicle).m_Target;
                if (EntityManager.HasComponent<Waypoint>(targetWaypoint))
                    AddWaypointAnchor(EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index);
            }

            if (TryGetRouteProgress(vehicle, out int nextWaypointIndex, out _))
                AddWaypointAnchor(nextWaypointIndex);

            if (semanticSegments.Count == 0)
                return false;

            int[] segmentOffsets = new[] { 0, -1, 1 };
            int bestDistance = int.MaxValue;
            foreach (int baseSegmentIndex in semanticSegments)
            {
                for (int offsetIndex = 0; offsetIndex < segmentOffsets.Length; offsetIndex++)
                {
                    int segmentIndex = baseSegmentIndex + segmentOffsets[offsetIndex];
                    if (segmentIndex < 0)
                        segmentIndex += segmentCount;
                    else if (segmentIndex >= segmentCount)
                        segmentIndex -= segmentCount;

                    TrackSegmentRange segmentRange = chain.SegmentRanges[segmentIndex];
                    if (!TryFindClosestAtomIndexForLane(
                            chain,
                            lane,
                            segmentRange.StartAtomIndex,
                            segmentRange.EndAtomIndexExclusive,
                            referenceAtomIndex,
                            out int candidateAtomIndex))
                    {
                        continue;
                    }

                    if (referenceAtomIndex < 0)
                    {
                        atomIndex = candidateAtomIndex;
                        return true;
                    }

                    int candidateDistance = math.abs(candidateAtomIndex - referenceAtomIndex);
                    if (candidateDistance >= bestDistance)
                        continue;

                    bestDistance = candidateDistance;
                    atomIndex = candidateAtomIndex;
                }
            }

            return atomIndex >= 0;
        }

        private static bool TryFindClosestAtomIndexForLane(
            LineTrackChain chain,
            Entity lane,
            int startAtomIndex,
            int endAtomIndexExclusive,
            int referenceAtomIndex,
            out int atomIndex)
        {
            atomIndex = -1;
            if (chain == null
                || lane == Entity.Null
                || chain.TrackAtoms.Count == 0)
            {
                return false;
            }

            if (!chain.AtomIndicesByLane.TryGetValue(lane, out List<int> candidateAtomIndices)
                || candidateAtomIndices == null
                || candidateAtomIndices.Count == 0)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);
            for (int candidateIndex = 0; candidateIndex < candidateAtomIndices.Count; candidateIndex++)
            {
                int index = candidateAtomIndices[candidateIndex];
                if (index < startAtomIndex || index >= endAtomIndexExclusive)
                    continue;

                int distance = math.abs(index - referenceAtomIndex);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                atomIndex = index;
            }

            return atomIndex >= 0;
        }

        private static int ResolveSegmentIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            if (chain == null || chain.SegmentRanges.Count == 0 || atomIndex < 0)
                return -1;

            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        private bool TryGetVehicleTrackCursorCurrentFrame(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            cursor = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null)
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_VehicleTrackCursorFrameSnapshots.TryGetValue(vehicle, out VehicleTrackCursorFrameSnapshot snapshot)
                && snapshot.Frame == nowFrame
                && snapshot.LineEntity == line
                && snapshot.ChainSignature == chain.Signature)
            {
                cursor = snapshot.Cursor;
                return snapshot.Available;
            }

            bool available = TryProjectVehicleTrackCursor(vehicle, line, waypoints, out cursor);
            if (available)
            {
                m_VehicleTrackCursorFrameSnapshots[vehicle] = new VehicleTrackCursorFrameSnapshot(
                    line,
                    chain.Signature,
                    nowFrame,
                    true,
                    cursor);
            }
            else
            {
                m_VehicleTrackCursorFrameSnapshots.Remove(vehicle);
            }

            return available;
        }

        private bool TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out int currentControlEdgeIndex,
            out float ownLineAtomCoordinate,
            out int phaseEndAtomExclusive)
        {
            cursor = default;
            currentControlEdgeIndex = -1;
            ownLineAtomCoordinate = 0f;
            phaseEndAtomExclusive = -1;

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
                return false;

            currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            ownLineAtomCoordinate = math.max(0f, cursor.AtomCursorIndex + math.saturate(cursor.AtomPosition01));
            TryGetExpressCurrentForwardPhaseWindow(chain, cursor.AtomCursorIndex, out phaseEndAtomExclusive);
            return true;
        }

        private bool TryResolveStationAnchoredProgressFallback(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            int nextWaypointIndex,
            float segmentPosition,
            out int anchoredWaypointIndex)
        {
            anchoredWaypointIndex = -1;
            if (!TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            if (TryResolveWaypointAnchorConflict(
                    vehiclePosition,
                    waypoints,
                    nextWaypointIndex,
                    out int cachedAnchorWaypointIndex)
                && m_CachedWpIdx.TryGetValue(vehicle, out int cachedWpIdx)
                && cachedWpIdx == cachedAnchorWaypointIndex)
            {
                anchoredWaypointIndex = cachedAnchorWaypointIndex;
                return true;
            }

            if (m_VehicleTrackCursorHints.TryGetValue(vehicle, out VehicleTrackCursor hint)
                && hint.LineEntity == line
                && hint.ChainSignature == chain.Signature)
            {
                int hintedWaypointIndex = hint.SegmentIndex >= chain.SegmentRanges.Count - 1
                    ? 0
                    : hint.SegmentIndex + 1;
                if (TryResolveWaypointAnchorConflict(
                        vehiclePosition,
                        waypoints,
                        nextWaypointIndex,
                        out int nearbyWaypointIndex)
                    && nearbyWaypointIndex == hintedWaypointIndex)
                {
                    anchoredWaypointIndex = nearbyWaypointIndex;
                    return true;
                }

                bool wrappedForward = hint.SegmentIndex >= chain.SegmentRanges.Count - 2 && nextWaypointIndex <= 1;
                bool monotonicForward = (nextWaypointIndex == 0 ? chain.SegmentRanges.Count - 1 : nextWaypointIndex - 1) >= hint.SegmentIndex || wrappedForward;
                if (!monotonicForward && math.saturate(segmentPosition) <= 0.15f)
                {
                    anchoredWaypointIndex = hintedWaypointIndex;
                    return true;
                }
            }

            return false;
        }

        private void MarkVehicleProgressSuspect(Entity vehicle, string reason)
        {
            if (vehicle == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            m_SuspectProgressSinceFrame[vehicle] = nowFrame;
            m_SuspectProgressReason[vehicle] = reason ?? "unknown";
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_VehicleTrackCursorFrameSnapshots.Remove(vehicle);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            string summary = vehicle.Index + "|" + reason;
            if (m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) && previous == summary)
                return;

            m_SuspectProgressLogCache[vehicle] = summary;
            log.Info("[ProgressSuspect] 车辆" + vehicle.Index + " reason=" + reason + " sinceFrame=" + nowFrame);
        }

        private void ClearVehicleProgressSuspect(Entity vehicle, string reason = null)
        {
            if (vehicle == Entity.Null)
                return;

            bool hadState = m_SuspectProgressSinceFrame.Remove(vehicle);
            m_SuspectProgressLastValidationFrame.Remove(vehicle);
            m_SuspectProgressProjectionInvalid.Remove(vehicle);
            m_VehicleTrackCursorFrameSnapshots.Remove(vehicle);
            m_SuspectProgressReason.Remove(vehicle);
            m_SuspectProgressLogCache.Remove(vehicle);
            m_SuspectProgressRecoveryWaypoint.Remove(vehicle);
            m_SuspectProgressValidationCount.Remove(vehicle);
            m_SuspectProgressFirstSample.Remove(vehicle);

            if (hadState)
            {
                log.Info("[ProgressSuspectClear] 车辆" + vehicle.Index
                    + (!string.IsNullOrWhiteSpace(reason) ? " reason=" + reason : string.Empty));
            }
        }

        private void NoteVehicleProgressSuspectRecoveryBoarding(Entity vehicle, int waypointIndex)
        {
            if (vehicle == Entity.Null
                || waypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return;
            }

            m_SuspectProgressRecoveryWaypoint[vehicle] = waypointIndex;
        }

        private void TryClearVehicleProgressSuspectOnStableDeparture(Entity vehicle, int departedWaypointIndex)
        {
            if (vehicle == Entity.Null
                || departedWaypointIndex < 0
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle)
                || !m_SuspectProgressRecoveryWaypoint.TryGetValue(vehicle, out int recoveryWaypointIndex)
                || recoveryWaypointIndex != departedWaypointIndex)
            {
                return;
            }

            ClearVehicleProgressSuspect(vehicle, "stable-stop-cycle wp=" + departedWaypointIndex);
        }

        private bool IsVehicleProgressProjectionInvalid(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int segmentIndex,
            int projectedAtomIndex)
        {
            if (vehicle == Entity.Null
                || !m_SuspectProgressSinceFrame.ContainsKey(vehicle))
            {
                return false;
            }

            if (m_SuspectProgressProjectionInvalid.TryGetValue(vehicle, out bool alreadyInvalid) && alreadyInvalid)
                return true;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_SuspectProgressLastValidationFrame.TryGetValue(vehicle, out uint lastValidationFrame)
                && nowFrame - lastValidationFrame < SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES)
            {
                return false;
            }

            m_SuspectProgressLastValidationFrame[vehicle] = nowFrame;
            if (!TryValidateSuspectVehicleProjection(vehicle, line, chain, segmentIndex, projectedAtomIndex, out SuspectProgressSample sample, out string validationSummary, out bool projectionInvalid))
                return false;

            int validationCount = m_SuspectProgressValidationCount.TryGetValue(vehicle, out int previousCount)
                ? previousCount + 1
                : 1;
            m_SuspectProgressValidationCount[vehicle] = validationCount;
            if (!m_SuspectProgressFirstSample.ContainsKey(vehicle))
                m_SuspectProgressFirstSample[vehicle] = sample;

            string logKey = vehicle.Index + "|" + validationSummary;
            if (!m_SuspectProgressLogCache.TryGetValue(vehicle, out string previous) || previous != logKey)
            {
                m_SuspectProgressLogCache[vehicle] = logKey;
                log.Info("[ProgressSuspectCheck] " + validationSummary);
            }

            if (validationCount % 36 == 0
                && m_SuspectProgressFirstSample.TryGetValue(vehicle, out SuspectProgressSample firstSample))
            {
                log.Info("[ProgressSuspectWindow] vehicle=" + vehicle.Index
                    + " scans=" + validationCount
                    + " startAtom=" + firstSample.ProjectedAtomIndex
                    + " startBestAtom=" + firstSample.BestAtomIndex
                    + " startPos=(" + firstSample.VehiclePosition.x.ToString("F1")
                    + "," + firstSample.VehiclePosition.y.ToString("F1")
                    + "," + firstSample.VehiclePosition.z.ToString("F1") + ")"
                    + " startDist=" + firstSample.ProjectedDistanceMeters.ToString("F1")
                    + "/" + firstSample.BestDistanceMeters.ToString("F1")
                    + " currentAtom=" + sample.ProjectedAtomIndex
                    + " currentBestAtom=" + sample.BestAtomIndex
                    + " currentPos=(" + sample.VehiclePosition.x.ToString("F1")
                    + "," + sample.VehiclePosition.y.ToString("F1")
                    + "," + sample.VehiclePosition.z.ToString("F1") + ")"
                    + " currentDist=" + sample.ProjectedDistanceMeters.ToString("F1")
                    + "/" + sample.BestDistanceMeters.ToString("F1")
                    + (projectionInvalid ? " invalid=true" : " invalid=false"));
            }

            if (!projectionInvalid)
                return false;

            m_SuspectProgressProjectionInvalid[vehicle] = true;
            return true;
        }

        private bool TryValidateSuspectVehicleProjection(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int projectedSegmentIndex,
            int projectedAtomIndex,
            out SuspectProgressSample sample,
            out string validationSummary,
            out bool projectionInvalid)
        {
            sample = default;
            validationSummary = string.Empty;
            projectionInvalid = false;

            if (!TryGetVehicleWorldPosition(vehicle, out float3 vehiclePosition))
                return false;

            if (!TryGetTrackAtomWorldPosition(chain, projectedAtomIndex, out float3 projectedAtomPosition))
                return false;

            float projectedDistance = math.distance(vehiclePosition, projectedAtomPosition);
            int candidateStartSegment = math.max(0, projectedSegmentIndex - SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);
            int candidateEndSegment = math.min(chain.SegmentRanges.Count - 1, projectedSegmentIndex + SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS);

            int bestAtomIndex = -1;
            float bestDistance = float.MaxValue;
            for (int seg = candidateStartSegment; seg <= candidateEndSegment; seg++)
            {
                TrackSegmentRange candidateRange = chain.SegmentRanges[seg];
                for (int atomIndex = candidateRange.StartAtomIndex; atomIndex < candidateRange.EndAtomIndexExclusive; atomIndex++)
                {
                    if (!TryGetTrackAtomWorldPosition(chain, atomIndex, out float3 atomPosition))
                        continue;

                    float distance = math.distance(vehiclePosition, atomPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestAtomIndex = atomIndex;
                    }
                }
            }

            if (bestAtomIndex < 0)
                return false;

            int atomDelta = math.abs(bestAtomIndex - projectedAtomIndex);
            projectionInvalid =
                atomDelta >= SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD
                && projectedDistance - bestDistance >= SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS;

            sample = new SuspectProgressSample(
                projectedAtomIndex,
                bestAtomIndex,
                vehiclePosition,
                projectedDistance,
                bestDistance);

            validationSummary = "vehicle=" + vehicle.Index
                + " line=" + line.Index
                + " projectedAtom=" + projectedAtomIndex
                + " bestAtom=" + bestAtomIndex
                + " projectedDist=" + projectedDistance.ToString("F1")
                + "m bestDist=" + bestDistance.ToString("F1")
                + "m delta=" + atomDelta
                + (projectionInvalid ? " invalid=true" : " invalid=false");
            return true;
        }

        private bool TryGetTrackAtomWorldPosition(LineTrackChain chain, int atomIndex, out float3 position)
        {
            position = default;
            if (chain == null || atomIndex < 0 || atomIndex >= chain.TrackAtoms.Count)
                return false;

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            return TryGetEntityWorldPosition(atom.SourceTarget, out position)
                || TryGetEntityWorldPosition(atom.Key.PhysicalLaneKey, out position);
        }

        private bool TryGetEntityWorldPosition(Entity entity, out float3 position)
        {
            position = default;
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return false;

            if (EntityManager.HasComponent<Position>(entity))
            {
                position = EntityManager.GetComponentData<Position>(entity).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                return true;
            }

            return false;
        }

        private bool TryResolveWaypointAnchorConflict(
            float3 vehiclePosition,
            DynamicBuffer<RouteWaypoint> waypoints,
            int routeProgressNextWaypointIndex,
            out int nearbyWaypointIndex)
        {
            nearbyWaypointIndex = -1;
            const float stationAnchorRadiusMeters = 420f;

            float bestDistance = float.MaxValue;
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                if (!TryGetWaypointWorldPosition(waypoints[waypointIndex].m_Waypoint, out float3 waypointPosition))
                    continue;

                float distance = math.distance(vehiclePosition, waypointPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearbyWaypointIndex = waypointIndex;
                }
            }

            if (nearbyWaypointIndex < 0 || bestDistance > stationAnchorRadiusMeters)
                return false;

            int waypointDelta = math.abs(routeProgressNextWaypointIndex - nearbyWaypointIndex);
            bool wrappedNeighbor =
                (routeProgressNextWaypointIndex == 0 && nearbyWaypointIndex == waypoints.Length - 1)
                || (nearbyWaypointIndex == 0 && routeProgressNextWaypointIndex == waypoints.Length - 1);

            return waypointDelta >= 1 && !wrappedNeighbor;
        }

        private bool TryGetVehicleWorldPosition(Entity vehicle, out float3 position)
        {
            position = default;
            if (!EntityManager.Exists(vehicle))
                return false;

            if (EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Position>(vehicle))
            {
                position = EntityManager.GetComponentData<Position>(vehicle).m_Position;
                return true;
            }

            return false;
        }

        private bool TryGetWaypointWorldPosition(Entity waypoint, out float3 position)
        {
            position = default;
            if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                return false;

            if (EntityManager.HasComponent<Position>(waypoint))
            {
                position = EntityManager.GetComponentData<Position>(waypoint).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Objects.Transform>(waypoint))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(waypoint).m_Position;
                return true;
            }

            return false;
        }

        private bool TryProjectTrackModelRuntimePosition(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            BypassProtectedInterval protectedInterval,
            out TrackModelRuntimePosition runtimePosition)
        {
            runtimePosition = default;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, cursor.AtomCursorIndex, protectedInterval);
            float confidence = cursor.Confidence;
            runtimePosition = new TrackModelRuntimePosition(currentControlEdgeIndex, cursor.AtomCursorIndex, cursor.AtomPosition01, relative, confidence);
            return true;
        }

        private static bool TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
            LineRunningVehicleSnapshot runningVehicle,
            BypassProtectedInterval protectedInterval,
            out TrackModelRuntimePosition runtimePosition)
        {
            runtimePosition = default;
            if (!runningVehicle.HasTrackCursor)
                return false;

            runtimePosition = new TrackModelRuntimePosition(
                runningVehicle.CurrentControlEdgeIndex,
                runningVehicle.TrackCursor.AtomCursorIndex,
                runningVehicle.TrackCursor.AtomPosition01,
                ResolveRelativeToProtectedInterval(
                    runningVehicle.CurrentControlEdgeIndex,
                    runningVehicle.TrackCursor.AtomCursorIndex,
                    protectedInterval),
                runningVehicle.TrackCursor.Confidence);
            return true;
        }

        private static float GetProtectedIntervalDisplayLength(BypassProtectedInterval interval)
        {
            return math.max(1f, interval.EndAtomIndexExclusive - interval.StartAtomIndex);
        }

        private static float MapRuntimePositionToOwnProtectedIntervalCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval interval,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return -0.5f;
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return intervalLength + 0.5f;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    float atomOffset = math.clamp(runtimePosition.CurrentAtomIndex - interval.StartAtomIndex, 0, math.max(0, interval.EndAtomIndexExclusive - interval.StartAtomIndex - 1));
                    return math.clamp(atomOffset + math.saturate(runtimePosition.AtomPosition01), 0f, intervalLength);
                }
                default:
                    include = false;
                    return 0f;
            }
        }

        private static float MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval interval,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            float rawCoordinate = (runtimePosition.CurrentAtomIndex - interval.StartAtomIndex) + math.saturate(runtimePosition.AtomPosition01);
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return math.min(-0.5f, rawCoordinate);
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return math.max(intervalLength + 0.5f, rawCoordinate);
                case TrackModelRelativeToProtectedInterval.Inside:
                    return math.clamp(rawCoordinate, 0f, intervalLength);
                default:
                    include = false;
                    return 0f;
            }
        }

        private static float MapAtomIndexToProtectedIntervalCoordinateExact(
            BypassProtectedInterval interval,
            int atomIndex)
        {
            return atomIndex - interval.StartAtomIndex;
        }

        private static float MapRuntimePositionToReferenceProtectedIntervalCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceInterval,
            float referenceLength,
            bool includeApproachers,
            out bool include)
        {
            include = true;
            switch (runtimePosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    include = includeApproachers;
                    return -0.5f;
                case TrackModelRelativeToProtectedInterval.After:
                    include = includeApproachers;
                    return referenceLength + 0.5f;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    float sourceLength = GetProtectedIntervalDisplayLength(sourceInterval);
                    float atomOffset = math.clamp(runtimePosition.CurrentAtomIndex - sourceInterval.StartAtomIndex, 0, math.max(0, sourceInterval.EndAtomIndexExclusive - sourceInterval.StartAtomIndex - 1));
                    float sourceCoordinate = math.clamp(atomOffset + math.saturate(runtimePosition.AtomPosition01), 0f, sourceLength);
                    float progress01 = math.saturate(sourceCoordinate / sourceLength);
                    return progress01 * referenceLength;
                }
                default:
                    include = false;
                    return 0f;
            }
        }

        private static float MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceInterval,
            float referenceLength,
            bool includeApproachers,
            out bool include)
        {
            float sourceLength = GetProtectedIntervalDisplayLength(sourceInterval);
            float sourceCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                runtimePosition,
                sourceInterval,
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            return sourceCoordinate / sourceLength * referenceLength;
        }

        private static float MapRuntimePositionToReferenceWindowCoordinate(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceWindow,
            BypassProtectedInterval referenceWindow,
            BypassProtectedInterval referenceEnvelope,
            bool includeApproachers,
            out bool include)
        {
            float mappedInWindow = MapRuntimePositionToReferenceProtectedIntervalCoordinate(
                runtimePosition,
                sourceWindow,
                GetProtectedIntervalDisplayLength(referenceWindow),
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            float envelopeLength = GetProtectedIntervalDisplayLength(referenceEnvelope);
            float windowOffset = math.clamp(referenceWindow.StartAtomIndex - referenceEnvelope.StartAtomIndex, 0f, envelopeLength);
            return math.clamp(windowOffset + mappedInWindow, -0.5f, envelopeLength + 0.5f);
        }

        private static float MapRuntimePositionToReferenceWindowCoordinateExact(
            TrackModelRuntimePosition runtimePosition,
            BypassProtectedInterval sourceWindow,
            BypassProtectedInterval referenceWindow,
            BypassProtectedInterval referenceEnvelope,
            bool includeApproachers,
            out bool include)
        {
            float mappedInWindow = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                runtimePosition,
                sourceWindow,
                GetProtectedIntervalDisplayLength(referenceWindow),
                includeApproachers,
                out include);
            if (!include)
                return 0f;

            float envelopeLength = GetProtectedIntervalDisplayLength(referenceEnvelope);
            float windowOffset = math.clamp(referenceWindow.StartAtomIndex - referenceEnvelope.StartAtomIndex, 0f, envelopeLength);
            return windowOffset + mappedInWindow;
        }

        private static float MapControlPointToProtectedIntervalCoordinate(LineTrackChain chain, BypassProtectedInterval interval, int controlPointIndex)
        {
            if (chain == null
                || controlPointIndex < 0
                || controlPointIndex >= chain.ControlPoints.Count)
            {
                return 0f;
            }

            float intervalLength = GetProtectedIntervalDisplayLength(interval);
            int atomIndex = chain.ControlPoints[controlPointIndex].AtomIndex;
            return math.clamp(atomIndex - interval.StartAtomIndex, 0f, intervalLength);
        }

        private static int ResolveControlEdgeIndexForAtom(LineTrackChain chain, int atomIndex)
        {
            for (int i = 0; i < chain.ControlEdges.Count; i++)
            {
                ControlEdge edge = chain.ControlEdges[i];
                if (atomIndex >= edge.StartAtomIndex && atomIndex < edge.EndAtomIndexExclusive)
                    return i;
            }

            return -1;
        }

        private bool TryGetForwardStationExitCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out float stationExitCoordinate)
        {
            stationExitCoordinate = -1f;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            // Only treat the contiguous station-owned prefix at the start of the
            // local protected window as the "current bypass station" throat.
            // This makes the release anchor directional: it is tied to the
            // forward exit side of the current waiting station, not any later
            // reappearance of the same station building inside the window.
            int lastForwardStationAtomIndex = -1;
            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                lastForwardStationAtomIndex = atomIndex;
            }

            if (lastForwardStationAtomIndex < localProtectedInterval.StartAtomIndex)
                return false;

            stationExitCoordinate = (lastForwardStationAtomIndex - localProtectedInterval.StartAtomIndex) + 1f;
            return true;
        }

        private bool TryGetForwardStationExitAtomIndex(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            out int stationExitAtomIndex)
        {
            stationExitAtomIndex = -1;
            if (localChain == null || currentBypassBuilding == Entity.Null)
                return false;

            for (int atomIndex = localProtectedInterval.StartAtomIndex; atomIndex < localProtectedInterval.EndAtomIndexExclusive && atomIndex < localChain.TrackAtoms.Count; atomIndex++)
            {
                Entity atomBuilding = ResolvePassingStationBuilding(localChain.TrackAtoms[atomIndex].SourceTarget);
                if (atomBuilding != currentBypassBuilding)
                    break;

                stationExitAtomIndex = atomIndex;
            }

            return stationExitAtomIndex >= localProtectedInterval.StartAtomIndex;
        }

        private float ComputeForwardDepartureReleaseCoordinate(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding)
        {
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(localProtectedInterval);
            float fallback = math.min(intervalDisplayLength, LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
            if (!TryGetForwardStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return fallback;

            return math.min(intervalDisplayLength, stationExitCoordinate + LOCAL_BYPASS_EXIT_RELEASE_ATOMS);
        }

        private static TrackModelRelativeToProtectedInterval ResolveRelativeToProtectedInterval(int currentControlEdgeIndex, int currentAtomIndex, BypassProtectedInterval protectedInterval)
        {
            // Atom bounds are the true physical window. Control-edge bounds are
            // only a coarse fallback for lines whose control graph is sparse.
            // If we prioritize control-edge first, any single-edge line will mark
            // the entire edge as Inside even when the atom lies outside the
            // actual shared/protected atom range.
            if (currentAtomIndex >= 0)
            {
                if (currentAtomIndex < protectedInterval.StartAtomIndex)
                    return TrackModelRelativeToProtectedInterval.Before;
                if (currentAtomIndex >= protectedInterval.EndAtomIndexExclusive)
                    return TrackModelRelativeToProtectedInterval.After;
                return TrackModelRelativeToProtectedInterval.Inside;
            }

            if (currentControlEdgeIndex >= 0)
            {
                if (currentControlEdgeIndex < protectedInterval.StartControlEdgeIndex)
                    return TrackModelRelativeToProtectedInterval.Before;
                if (currentControlEdgeIndex > protectedInterval.EndControlEdgeIndexInclusive)
                    return TrackModelRelativeToProtectedInterval.After;
                return TrackModelRelativeToProtectedInterval.Inside;
            }

            return TrackModelRelativeToProtectedInterval.Unknown;
        }

        private static string FormatRuntimePosition(TrackModelRuntimePosition runtimePosition)
        {
            return "pos[edge="
                + runtimePosition.CurrentControlEdgeIndex
                + " atom="
                + runtimePosition.CurrentAtomIndex
                + " p="
                + runtimePosition.AtomPosition01.ToString("0.00")
                + " rel="
                + runtimePosition.RelativeToProtectedInterval
                + " conf="
                + runtimePosition.Confidence.ToString("0.00")
                + "]";
        }

        private string FormatTrackModelStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                label = "stop" + building.Index;
            }

            return label + "#" + waypointIndex;
        }

        private static string FormatTrackModelVehicleLabel(Entity vehicle, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "(" + state + ")@" + km + "km";
        }

        private string FormatTrackModelDisplayStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    label = !string.IsNullOrWhiteSpace(name)
                        ? name
                        : ("stop" + building.Index);
                }
                catch
                {
                    label = "stop" + building.Index;
                }
            }

            return label + "#" + waypointIndex;
        }

        private string FormatReadableStationLabel(Entity building, int waypointIndex)
        {
            string label = "wp" + waypointIndex;
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    if (!string.IsNullOrWhiteSpace(name))
                        label = name;
                    else
                        label = "stop" + building.Index;
                }
                catch
                {
                    label = "stop" + building.Index;
                }
            }

            return label + "#" + waypointIndex;
        }

        private string FormatSharedMapStationLabel(Entity building)
        {
            if (building != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(building);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "stop";
        }

        private string FormatSharedMapVehicleNameLabel(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                try
                {
                    string name = m_NameSystem.GetRenderedLabelName(vehicle);
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
                catch
                {
                }
            }

            return "vehicle";
        }

        private string ResolveTrackModelLineLabel(Entity line, bool includeEntityFallback)
        {
            if (line == Entity.Null)
                return includeEntityFallback ? "line" : string.Empty;

            try
            {
                if (m_NameSystem.TryGetCustomName(line, out string customName)
                    && !string.IsNullOrWhiteSpace(customName))
                {
                    return customName.Trim();
                }
            }
            catch
            {
            }

            if (EntityManager.Exists(line)
                && EntityManager.HasComponent<RouteNumber>(line))
            {
                RouteNumber routeNumber = EntityManager.GetComponentData<RouteNumber>(line);
                if (routeNumber.m_Number > 0)
                    return "line" + routeNumber.m_Number;
            }

            try
            {
                string rendered = m_NameSystem.GetRenderedLabelName(line);
                if (!string.IsNullOrWhiteSpace(rendered)
                    && !rendered.Contains("Tool")
                    && !rendered.Contains("Tool")
                    && !rendered.Contains("Route Tool")
                    && !rendered.Contains("Route Tool"))
                {
                    return rendered.Trim();
                }
            }
            catch
            {
            }

            return includeEntityFallback ? ("line" + line.Index) : "line";
        }

        private string FormatSharedMapLineLabel(Entity line)
        {
            return ResolveTrackModelLineLabel(line, includeEntityFallback: false);
        }

        private string FormatSharedMapVehicleLabel(Entity vehicle, Entity line, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return FormatSharedMapVehicleNameLabel(vehicle) + "[" + FormatSharedMapLineLabel(line) + "](" + state + ")@" + km + "km";
        }

        private string FormatSharedMapUnknownVehicleLabel(Entity vehicle, Entity line, string state)
        {
            return FormatSharedMapVehicleNameLabel(vehicle) + "[" + FormatSharedMapLineLabel(line) + "](" + state + ")@?";
        }

        private string FormatReadableVehicleLabel(Entity vehicle, Entity line, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "[" + FormatReadableLineLabel(line) + "](" + state + ")@" + km + "km";
        }

        private string FormatReadableUnknownVehicleLabel(Entity vehicle, Entity line, string state)
        {
            return "vehicle" + vehicle.Index + "[" + FormatReadableLineLabel(line) + "](" + state + ")@?";
        }

        private string FormatReadableLineLabel(Entity line)
        {
            if (line == Entity.Null)
                return "-";

            return ResolveTrackModelLineLabel(line, includeEntityFallback: true);
        }

        private static string FormatTrackModelDisplayVehicleLabel(Entity vehicle, string state, float distanceMeters)
        {
            string km = (distanceMeters / 1000f).ToString("0.00");
            return "vehicle" + vehicle.Index + "(" + state + ")@" + km + "km";
        }

        private bool TryBuildTrackModelSequenceSummary(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out string summary)
        {
            summary = string.Empty;
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !EntityManager.HasBuffer<RouteVehicle>(line))
            {
                return false;
            }

            if (!TryGetLineMileageModel(line, waypoints, out LineMileageModel model)
                || model.TotalDistanceMeters <= 0f
                || model.WaypointDistances.Length != waypoints.Length)
            {
                return false;
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(waypoints.Length + routeVehicles.Length);
            for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
            {
                Entity building = GetStationBuildingForWaypoint(waypoints, waypointIndex);
                items.Add(new TrackModelSequenceItem(
                    model.WaypointDistances[waypointIndex],
                    0,
                    FormatReadableStationLabel(building, waypointIndex)));
            }

            for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
            {
                Entity vehicle = routeVehicles[rvIndex].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    continue;

                string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                    ? vehicleState.ToString()
                    : "Unknown";

                if (TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection projection))
                {
                    items.Add(new TrackModelSequenceItem(
                        projection.DistanceMeters,
                        1,
                        FormatReadableVehicleLabel(vehicle, line, state, projection.DistanceMeters)));
                }
                else
                {
                    items.Add(new TrackModelSequenceItem(
                        float.MaxValue,
                        1,
                        "vehicle" + vehicle.Index + "(" + state + ")@?"));
                }
            }

            if (items.Count == 0)
                return false;

            items.Sort((a, b) =>
            {
                int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                if (cmp != 0)
                    return cmp;

                cmp = a.KindOrder.CompareTo(b.KindOrder);
                if (cmp != 0)
                    return cmp;

                return string.CompareOrdinal(a.Label, b.Label);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("seq=");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");
                sb.Append(items[i].Label);
            }

            summary = sb.ToString();
            return true;
        }

        private bool TryBuildBypassDecisionSequenceSummary(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            BypassProtectedInterval localProtectedInterval,
            out string summary)
        {
            summary = string.Empty;
            if (localLine == Entity.Null
                || !EntityManager.Exists(localLine)
                || !TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return false;
            }

            Entity currentBypassBuilding = GetBypassBuildingForWaypoint(localWaypoints, currentWaypointIndex);
            List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(localWaypoints.Length + 8);
            for (int controlPointIndex = localProtectedInterval.StartControlPointIndex; controlPointIndex <= localProtectedInterval.EndControlPointIndex; controlPointIndex++)
            {
                ControlPointMarker marker = localChain.ControlPoints[controlPointIndex];
                items.Add(new TrackModelSequenceItem(
                    MapControlPointToProtectedIntervalCoordinate(localChain, localProtectedInterval, controlPointIndex),
                    0,
                    FormatSharedMapStationLabel(marker.Building)));
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (routeVehicleBuffers.TryGetBuffer(localLine, out DynamicBuffer<RouteVehicle> localVehicles))
            {
                for (int rvIndex = 0; rvIndex < localVehicles.Length; rvIndex++)
                {
                    Entity vehicle = localVehicles[rvIndex].m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                        continue;

                    string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                        ? vehicleState.ToString()
                        : "Unknown";

                    if (!TryProjectTrackModelRuntimePosition(vehicle, localLine, localWaypoints, localProtectedInterval, out TrackModelRuntimePosition runtimePosition))
                        continue;
                    if (runtimePosition.Confidence < 0.6f)
                        continue;

                    float coordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(runtimePosition, localProtectedInterval, includeApproachers: true, out bool include);
                    if (!include)
                        continue;

                    items.Add(new TrackModelSequenceItem(
                        coordinate,
                        1,
                        FormatSharedMapVehicleLabel(vehicle, localLine, state, coordinate)));
                }
            }

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !routeVehicleBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteVehicle> expressVehicles)
                    || !TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                {
                    continue;
                }

                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, localProtectedInterval, currentBypassBuilding, expressChain);
                if (!sharedWindowMatch.Found || sharedWindowMatch.Ambiguous)
                    continue;
                for (int rvIndex = 0; rvIndex < expressVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = expressVehicles[rvIndex].m_Vehicle;
                    if (expressVehicle == Entity.Null || expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                        continue;

                    string state = m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState)
                        ? expressState.ToString()
                        : "Unknown";

                    if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, sharedWindowMatch.ExpressSharedWindow, out TrackModelRuntimePosition expressPosition))
                        continue;
                    if (expressPosition.Confidence < 0.6f)
                        continue;

                    float mappedMeters = MapRuntimePositionToReferenceWindowCoordinate(
                        expressPosition,
                        sharedWindowMatch.ExpressSharedWindow,
                        sharedWindowMatch.LocalSharedWindow,
                        localProtectedInterval,
                        includeApproachers: true,
                        out bool include);
                    if (!include)
                        continue;

                    items.Add(new TrackModelSequenceItem(
                        mappedMeters,
                        1,
                        FormatSharedMapVehicleLabel(expressVehicle, expressLine, state, mappedMeters)));
                }
            }

            if (items.Count == 0)
                return false;

            items.Sort((a, b) =>
            {
                int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                if (cmp != 0)
                    return cmp;

                cmp = a.KindOrder.CompareTo(b.KindOrder);
                if (cmp != 0)
                    return cmp;

                return string.CompareOrdinal(a.Label, b.Label);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("seq=");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");
                sb.Append(items[i].Label);
            }

            summary = sb.ToString();
            return true;
        }

        private static float MapExpressPositionToLocalProtectedInterval(
            TrackModelRuntimePosition expressPosition,
            BypassProtectedInterval expressProtectedInterval,
            float localStartMeters,
            float localEndMeters)
        {
            const float epsilonMeters = 1f;
            switch (expressPosition.RelativeToProtectedInterval)
            {
                case TrackModelRelativeToProtectedInterval.Before:
                    return math.max(0f, localStartMeters - epsilonMeters);
                case TrackModelRelativeToProtectedInterval.After:
                    return localEndMeters + epsilonMeters;
                case TrackModelRelativeToProtectedInterval.Inside:
                {
                    int atomSpan = math.max(1, expressProtectedInterval.EndAtomIndexExclusive - expressProtectedInterval.StartAtomIndex);
                    float progress01 = math.saturate((expressPosition.CurrentAtomIndex - expressProtectedInterval.StartAtomIndex) / (float)atomSpan);
                    return math.lerp(localStartMeters, localEndMeters, progress01);
                }
                default:
                    return localEndMeters + epsilonMeters * 2f;
            }
        }

        private static float ResolveWaypointDistanceForControlPoint(LineMileageModel model, LineTrackChain chain, int controlPointIndex)
        {
            if (model == null
                || chain == null
                || controlPointIndex < 0
                || controlPointIndex >= chain.ControlPoints.Count)
            {
                return 0f;
            }

            int waypointIndex = chain.ControlPoints[controlPointIndex].WaypointIndex;
            if (waypointIndex < 0 || waypointIndex >= model.WaypointDistances.Length)
                return 0f;

            return model.WaypointDistances[waypointIndex];
        }

        public void RequestDumpTrackModelSnapshot()
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                LogIndependentSharedPhysicalCorridorDump(lines);
                LogTrackModelReplayDump(lines);
            }
            finally
            {
                lines.Dispose();
            }
        }

        private bool TryBuildSharedCorridorDumpRow(
            Entity referenceLine,
            DynamicBuffer<RouteWaypoint> referenceWaypoints,
            LineTrackChain referenceChain,
            BypassProtectedInterval referenceInterval,
            out string dedupeKey,
            out string row)
        {
            dedupeKey = string.Empty;
            row = string.Empty;
            if (referenceChain == null)
                return false;

            Entity startBuilding = referenceChain.ControlPoints[referenceInterval.StartControlPointIndex].Building;
            Entity endBuilding = referenceChain.ControlPoints[referenceInterval.EndControlPointIndex].Building;
            if (startBuilding == Entity.Null || endBuilding == Entity.Null)
                return false;

            List<Entity> corridorLines = new List<Entity> { referenceLine };
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var allLines = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < allLines.Length; i++)
                {
                    Entity otherLine = allLines[i];
                    if (otherLine == Entity.Null || otherLine == referenceLine || !EntityManager.Exists(otherLine))
                        continue;
                    if (!routeWaypointBuffers.TryGetBuffer(otherLine, out DynamicBuffer<RouteWaypoint> otherWaypoints))
                        continue;
                    if (!TryGetLineTrackChain(otherLine, otherWaypoints, out LineTrackChain otherChain))
                        continue;

                    RefreshBypassProtectedIntervals(otherChain);
                    ProtectedIntervalMatch otherMatch = FindBestMatchingProtectedInterval(referenceChain, referenceInterval, otherChain);
                    if (otherMatch.Found)
                        corridorLines.Add(otherLine);
                }

                corridorLines.Sort((a, b) =>
                {
                    int cmp = string.CompareOrdinal(FormatSharedMapLineLabel(a), FormatSharedMapLineLabel(b));
                    if (cmp != 0)
                        return cmp;
                    return a.Index.CompareTo(b.Index);
                });
                StringBuilder keyBuilder = new StringBuilder();
                ulong intervalSignature = ComputeProtectedIntervalAtomSignature(referenceChain, referenceInterval);
                keyBuilder.Append(intervalSignature).Append("|");
                for (int i = 0; i < corridorLines.Count; i++)
                {
                    if (i > 0)
                        keyBuilder.Append(",");
                    keyBuilder.Append(corridorLines[i].Index);
                }
                dedupeKey = keyBuilder.ToString();

                float intervalDisplayLength = GetProtectedIntervalDisplayLength(referenceInterval);

                List<TrackModelSequenceItem> items = new List<TrackModelSequenceItem>(referenceWaypoints.Length + 16);
                for (int controlPointIndex = referenceInterval.StartControlPointIndex; controlPointIndex <= referenceInterval.EndControlPointIndex; controlPointIndex++)
                {
                    ControlPointMarker marker = referenceChain.ControlPoints[controlPointIndex];

                    items.Add(new TrackModelSequenceItem(
                        MapControlPointToProtectedIntervalCoordinate(referenceChain, referenceInterval, controlPointIndex),
                        0,
                        FormatSharedMapStationLabel(marker.Building)));
                }

                for (int lineIndex = 0; lineIndex < corridorLines.Count; lineIndex++)
                {
                    Entity corridorLine = corridorLines[lineIndex];
                    if (!routeWaypointBuffers.TryGetBuffer(corridorLine, out DynamicBuffer<RouteWaypoint> corridorWaypoints))
                        continue;
                    if (!routeVehicleBuffers.TryGetBuffer(corridorLine, out DynamicBuffer<RouteVehicle> corridorVehicles))
                        continue;

                    if (corridorLine == referenceLine)
                    {
                        for (int rvIndex = 0; rvIndex < corridorVehicles.Length; rvIndex++)
                        {
                            Entity vehicle = corridorVehicles[rvIndex].m_Vehicle;
                            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                                continue;
                            string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                                ? vehicleState.ToString()
                                : "Unknown";
                            if (!TryProjectTrackModelRuntimePosition(vehicle, corridorLine, corridorWaypoints, referenceInterval, out TrackModelRuntimePosition runtimePosition))
                            {
                                items.Add(new TrackModelSequenceItem(
                                    float.MaxValue,
                                    2,
                                    FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)));
                                continue;
                            }

                            float coordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(runtimePosition, referenceInterval, includeApproachers: true, out bool include);
                            if (!include)
                            {
                                items.Add(new TrackModelSequenceItem(
                                    float.MaxValue,
                                    2,
                                    FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)));
                                continue;
                            }

                            string label = runtimePosition.Confidence < 0.6f
                                ? FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)
                                : FormatSharedMapVehicleLabel(vehicle, corridorLine, state, coordinate);
                            items.Add(new TrackModelSequenceItem(
                                runtimePosition.Confidence < 0.6f ? float.MaxValue : coordinate,
                                runtimePosition.Confidence < 0.6f ? 2 : 1,
                                label));
                        }

                        continue;
                    }

                    if (!TryGetLineTrackChain(corridorLine, corridorWaypoints, out LineTrackChain corridorChain))
                        continue;
                    RefreshBypassProtectedIntervals(corridorChain);
                    ProtectedIntervalMatch corridorMatch = FindBestMatchingProtectedInterval(referenceChain, referenceInterval, corridorChain);
                    if (!corridorMatch.Found)
                        continue;

                    int corridorProtectedIntervalIndex = corridorMatch.ProtectedIntervalIndex;
                    if (corridorProtectedIntervalIndex < 0 || corridorProtectedIntervalIndex >= corridorChain.BypassProtectedIntervals.Count)
                        continue;

                    BypassProtectedInterval corridorInterval = corridorChain.BypassProtectedIntervals[corridorProtectedIntervalIndex];
                    for (int rvIndex = 0; rvIndex < corridorVehicles.Length; rvIndex++)
                    {
                        Entity vehicle = corridorVehicles[rvIndex].m_Vehicle;
                        if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                            continue;

                        string state = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                            ? vehicleState.ToString()
                            : "Unknown";
                        if (!TryProjectTrackModelRuntimePosition(vehicle, corridorLine, corridorWaypoints, corridorInterval, out TrackModelRuntimePosition runtimePosition))
                        {
                            items.Add(new TrackModelSequenceItem(
                                float.MaxValue,
                                2,
                                FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)));
                            continue;
                        }

                        float mappedMeters = MapRuntimePositionToReferenceProtectedIntervalCoordinate(
                            runtimePosition,
                            corridorInterval,
                            intervalDisplayLength,
                            includeApproachers: true,
                            out bool include);
                        if (!include)
                        {
                            items.Add(new TrackModelSequenceItem(
                                float.MaxValue,
                                2,
                                FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)));
                            continue;
                        }

                        string label = runtimePosition.Confidence < 0.6f
                            ? FormatSharedMapUnknownVehicleLabel(vehicle, corridorLine, state)
                            : FormatSharedMapVehicleLabel(vehicle, corridorLine, state, mappedMeters);
                        items.Add(new TrackModelSequenceItem(
                            runtimePosition.Confidence < 0.6f ? float.MaxValue : mappedMeters,
                            runtimePosition.Confidence < 0.6f ? 2 : 1,
                            label));
                    }
                }

                items.Sort((a, b) =>
                {
                    int cmp = a.DistanceMeters.CompareTo(b.DistanceMeters);
                    if (cmp != 0)
                        return cmp;
                    cmp = a.KindOrder.CompareTo(b.KindOrder);
                    if (cmp != 0)
                        return cmp;
                    return string.CompareOrdinal(a.Label, b.Label);
                });

                StringBuilder seqBuilder = new StringBuilder();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        seqBuilder.Append(" -> ");
                    seqBuilder.Append(items[i].Label);
                }

                StringBuilder lineBuilder = new StringBuilder();
                for (int i = 0; i < corridorLines.Count; i++)
                {
                    if (i > 0)
                        lineBuilder.Append(" / ");
                    lineBuilder.Append(FormatSharedMapLineLabel(corridorLines[i]));
                }

                row = FormatSharedMapStationLabel(startBuilding)
                    + " -> "
                    + FormatSharedMapStationLabel(endBuilding)
                    + " | "
                    + lineBuilder
                    + " | "
                    + seqBuilder;
                return true;
            }
            finally
            {
                allLines.Dispose();
            }
        }

        private bool TryEvaluateBypassTrackModelShadow(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out int protectedIntervalIndex,
            out string risk,
            out string summary)
        {
            protectedIntervalIndex = -1;
            risk = string.Empty;
            summary = string.Empty;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                return false;

            EnsureTrackChainBypassPipelineReady(chain);

            if (!TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out BypassProtectedInterval protectedInterval))
                return false;

            if (protectedIntervalIndex < 0 || protectedIntervalIndex >= chain.ProtectedIntervalSummaries.Count)
                return false;

            ProtectedIntervalSummary intervalSummary = chain.ProtectedIntervalSummaries[protectedIntervalIndex];
            risk = ClassifyProtectedIntervalShadowRisk(intervalSummary);
            summary = FormatProtectedIntervalSummary(intervalSummary, protectedInterval);
            return true;
        }

        private bool TryGetExpressFirstSharedAtomAfterCurrentBypassStation(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            PhysicalSharedWindowMatch sharedWindowMatch,
            out int expressFirstSharedAtomIndex)
        {
            expressFirstSharedAtomIndex = -1;
            if (localChain == null
                || localChain == null
                || expressChain == null
                || !TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int localStationExitAtomIndex))
            {
                return false;
            }

            GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
            if (snapshot == null || snapshot.Segments.Count == 0)
                return false;

            int bestLocalSharedAtomIndex = int.MaxValue;
            for (int i = 0; i < snapshot.Segments.Count; i++)
            {
                GlobalSharedTrunkSegment candidate = snapshot.Segments[i];
                if (candidate.HasMirroredContext || candidate.TraversalRelation != SharedTraversalRelation.SameDirection)
                    continue;

                int localStart = math.max(candidate.LocalCorridorStartAtomIndex, sharedWindowMatch.LocalSharedWindow.StartAtomIndex);
                int localEndExclusive = math.min(candidate.LocalCorridorEndAtomIndexExclusive, sharedWindowMatch.LocalSharedWindow.EndAtomIndexExclusive);
                if (localEndExclusive <= localStart)
                    continue;

                int firstSharedAfterStation = math.max(localStart, localStationExitAtomIndex + 1);
                if (firstSharedAfterStation >= localEndExclusive)
                    continue;

                int localOffset = firstSharedAfterStation - candidate.LocalCorridorStartAtomIndex;
                if (localOffset < 0)
                    continue;

                int candidateExpressAtomIndex = candidate.ExpressCorridorStartAtomIndex + localOffset;
                if (candidateExpressAtomIndex < candidate.ExpressCorridorStartAtomIndex
                    || candidateExpressAtomIndex >= candidate.ExpressCorridorEndAtomIndexExclusive)
                {
                    continue;
                }

                if (firstSharedAfterStation >= bestLocalSharedAtomIndex)
                    continue;

                bestLocalSharedAtomIndex = firstSharedAfterStation;
                expressFirstSharedAtomIndex = candidateExpressAtomIndex;
            }

            return expressFirstSharedAtomIndex >= 0;
        }

        private static bool IsVehicleClearlyPastExpressAtom(
            LineRunningVehicleSnapshot runningVehicle,
            int expressAtomIndex)
        {
            if (!runningVehicle.HasTrackCursor || expressAtomIndex < 0)
            {
                return false;
            }

            return runningVehicle.TrackCursor.AtomCursorIndex > expressAtomIndex;
        }

        private bool IsTrackCursorWithinBypassStationPhysicalContext(
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            Entity bypassBuilding)
        {
            if (chain == null
                || bypassBuilding == Entity.Null
                || cursor.AtomCursorIndex < 0
                || cursor.AtomCursorIndex >= chain.TrackAtoms.Count)
            {
                return false;
            }

            Entity atomBuilding = ResolvePassingStationBuilding(chain.TrackAtoms[cursor.AtomCursorIndex].SourceTarget);
            return atomBuilding == bypassBuilding;
        }

        private bool IsRuntimePositionWithinBypassStationPhysicalContext(
            LineTrackChain chain,
            TrackModelRuntimePosition runtimePosition,
            Entity bypassBuilding)
        {
            if (chain == null
                || bypassBuilding == Entity.Null
                || runtimePosition.CurrentAtomIndex < 0
                || runtimePosition.CurrentAtomIndex >= chain.TrackAtoms.Count)
            {
                return false;
            }

            Entity atomBuilding = ResolvePassingStationBuilding(chain.TrackAtoms[runtimePosition.CurrentAtomIndex].SourceTarget);
            return atomBuilding == bypassBuilding;
        }

        private bool TryFindSameStationSameDirectionDepartureBlocker(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            float intervalDisplayLength,
            uint nowFrame,
            out Entity blockerVehicle)
        {
            blockerVehicle = Entity.Null;
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || localWaypoints.Length == 0
                || localChain == null
                || currentBypassBuilding == Entity.Null)
            {
                return false;
            }

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            bool found = false;
            float bestMappedCoordinate = float.MinValue;

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                {
                    continue;
                }

                EnsureTrackChainBypassPipelineReady(expressChain);
                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(
                    localChain,
                    localProtectedInterval,
                    currentBypassBuilding,
                    expressChain);
                if (!sharedWindowMatch.Found || sharedWindowMatch.Ambiguous)
                    continue;

                if (!TryGetLineRunningVehicleFrameSnapshot(expressLine, expressWaypoints, nowFrame, out LineRunningVehicleFrameSnapshot runningSnapshot))
                    continue;

                for (int rvIndex = 0; rvIndex < runningSnapshot.Vehicles.Count; rvIndex++)
                {
                    LineRunningVehicleSnapshot runningVehicle = runningSnapshot.Vehicles[rvIndex];
                    Entity expressVehicle = runningVehicle.Vehicle;
                    if (expressVehicle == Entity.Null
                        || expressVehicle == localVehicle
                        || !EntityManager.Exists(expressVehicle)
                        || !runningVehicle.HasTrackCursor
                        || !IsTrackCursorWithinBypassStationPhysicalContext(expressChain, runningVehicle.TrackCursor, currentBypassBuilding))
                    {
                        continue;
                    }

                    if (!TryResolveExpressConflictWindowForLocalConflict(
                            expressVehicle,
                            expressLine,
                            expressWaypoints,
                            expressChain,
                            localChain,
                            localProtectedIntervalIndex,
                            localProtectedInterval,
                            sharedWindowMatch,
                            out _,
                            out BypassProtectedInterval expressProtectedInterval,
                            out int overlapCount,
                            out int orderedRun,
                            out _)
                        || overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                        || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN
                        || !TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                            localChain,
                            localProtectedInterval,
                            currentBypassBuilding,
                            expressChain,
                            expressProtectedInterval,
                            runningVehicle.TrackCursor.AtomCursorIndex,
                            out _)
                        || !TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
                            runningVehicle,
                            expressProtectedInterval,
                            out TrackModelRuntimePosition expressPosition))
                    {
                        continue;
                    }

                    float mappedCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                        expressPosition,
                        expressProtectedInterval,
                        intervalDisplayLength,
                        includeApproachers: true,
                        out bool includeExpress);
                    if (!includeExpress)
                        continue;

                    if (found && mappedCoordinate <= bestMappedCoordinate)
                        continue;

                    found = true;
                    bestMappedCoordinate = mappedCoordinate;
                    blockerVehicle = expressVehicle;
                }
            }

            return found;
        }

        private bool TryEvaluateBypassTrackModelShadowDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out BypassTrackModelShadowDecision shadowDecision)
        {
            m_BypassPerfProbeTrackDecisionCalls++;
            shadowDecision = default;
            if (!TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-chain-missing", -1, false, Entity.Null, false);
                return false;
            }

            EnsureTrackChainBypassPipelineReady(localChain);

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out int protectedIntervalIndex, out BypassProtectedInterval protectedInterval)
                || protectedIntervalIndex < 0
                || protectedIntervalIndex >= localChain.ProtectedIntervalSummaries.Count)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "protected-interval-missing", -1, false, Entity.Null, false);
                return false;
            }

            ProtectedIntervalSummary localSummary = localChain.ProtectedIntervalSummaries[protectedIntervalIndex];
            TrackModelRuntimePosition localPosition = default;
            bool hasLocalPosition = TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out localPosition);
            Entity currentBypassBuilding = GetBypassBuildingForWaypoint(localWaypoints, currentWaypointIndex);

            if (localSummary.SharedSegmentCount <= 0)
            {
                shadowDecision = new BypassTrackModelShadowDecision(true, false, "no-shared-protected-interval", protectedIntervalIndex, hasLocalPosition, Entity.Null, false);
                return true;
            }

            if (!hasLocalPosition)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-runtime-position-unknown", protectedIntervalIndex, false, Entity.Null, false);
                return false;
            }
            if (localPosition.Confidence < 0.6f)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-runtime-position-low-confidence", protectedIntervalIndex, false, Entity.Null, false);
                return false;
            }
            float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(localChain, protectedInterval, currentBypassBuilding);
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(protectedInterval);
            float localCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(localPosition, protectedInterval, includeApproachers: true, out bool includeLocalCoordinate);
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            bool releaseClearedUsedFallbackResolution = false;
            bool sawReleaseClearedExpress = false;
            bool foundBestBlocker = false;
            float bestBlockerEntryFrames = float.MaxValue;
            string bestConflictReason = string.Empty;
            string bestExpressPositionText = string.Empty;
            Entity bestExpressVehicle = Entity.Null;
            Entity bestExpressLine = Entity.Null;
            int bestExpressProtectedIntervalIndex = -1;
            string bestIntervalResolutionSource = string.Empty;
            int bestOverlapCount = 0;
            int bestOrderedRun = 0;
            int bestExpressAtomCursorIndex = -1;
            int bestExpressPhaseEndAtomExclusive = -1;
            bool bestUsedFallbackResolution = false;
            bool localStillWithinCurrentBypassStation = IsRuntimePositionWithinBypassStationPhysicalContext(
                localChain,
                localPosition,
                currentBypassBuilding);

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                {
                    continue;
                }

                if (!TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                    continue;

                EnsureTrackChainBypassPipelineReady(expressChain);
                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, currentBypassBuilding, expressChain);
                if (!sharedWindowMatch.Found)
                    continue;
                if (sharedWindowMatch.Ambiguous)
                {
                    shadowDecision = new BypassTrackModelShadowDecision(false, false, "shared-window-match-ambiguous", protectedIntervalIndex, true, Entity.Null, false);
                    return false;
                }

                bool hasExpressFirstSharedAtomAfterCurrentBypassStation = TryGetExpressFirstSharedAtomAfterCurrentBypassStation(
                    localChain,
                    protectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    sharedWindowMatch,
                    out int expressFirstSharedAtomAfterCurrentBypassStation);

                if (!TryGetLineRunningVehicleFrameSnapshot(expressLine, expressWaypoints, nowFrame, out LineRunningVehicleFrameSnapshot runningSnapshot))
                    continue;

                for (int rvIndex = 0; rvIndex < runningSnapshot.Vehicles.Count; rvIndex++)
                {
                    LineRunningVehicleSnapshot runningVehicle = runningSnapshot.Vehicles[rvIndex];
                    Entity expressVehicle = runningVehicle.Vehicle;
                    if (expressVehicle == localVehicle || expressVehicle == Entity.Null || !EntityManager.Exists(expressVehicle))
                        continue;
                    if (hasExpressFirstSharedAtomAfterCurrentBypassStation
                        && IsVehicleClearlyPastExpressAtom(
                            runningVehicle,
                            expressFirstSharedAtomAfterCurrentBypassStation))
                    {
                        LogSharedWindowFinalReject(
                            localVehicle,
                            localLine,
                            localWaypoints,
                            localChain,
                            currentWaypointIndex,
                            currentBypassBuilding,
                            protectedIntervalIndex,
                            protectedInterval,
                            expressVehicle,
                            "past-current-shared-entry atom=" + expressFirstSharedAtomAfterCurrentBypassStation);
                        continue;
                    }

                    if (!TryResolveExpressConflictWindowForLocalConflict(
                            expressVehicle,
                            expressLine,
                            expressWaypoints,
                            expressChain,
                            localChain,
                            protectedIntervalIndex,
                            protectedInterval,
                            sharedWindowMatch,
                            out int expressProtectedIntervalIndex,
                            out BypassProtectedInterval expressProtectedInterval,
                            out int overlapCount,
                            out int orderedRun,
                            out string intervalResolutionSource))
                    {
                        continue;
                    }

                    if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                        || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
                    {
                        if (intervalResolutionSource == "shared-window")
                        {
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                "weak-physical-overlap overlap=" + overlapCount + " run=" + orderedRun);
                        }
                        continue;
                    }

                    if (!runningVehicle.HasTrackCursor)
                    {
                        if (intervalResolutionSource == "shared-window")
                        {
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                "cursor-fail");
                        }
                        continue;
                    }

                    VehicleTrackCursor expressCursor = runningVehicle.TrackCursor;

                    if (intervalResolutionSource == "shared-window")
                    {
                        TryLogTrainLaneSourceDisagreement(
                            expressVehicle,
                            expressLine,
                            expressWaypoints,
                            expressChain,
                            expressCursor,
                            "shared-window-candidate");
                    }

                    if (!TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                            localChain,
                            protectedInterval,
                            currentBypassBuilding,
                            expressChain,
                            expressProtectedInterval,
                            expressCursor.AtomCursorIndex,
                            out GlobalSharedTrunkSegment selectedTrunkSegment))
                    {
                        if (intervalResolutionSource == "shared-window")
                        {
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                "static-opposite-direction");
                        }
                        continue;
                    }

                    if (!TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(runningVehicle, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
                    {
                        if (intervalResolutionSource == "shared-window")
                        {
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                "proj-fail");
                        }
                        continue;
                    }
                    if (expressPosition.Confidence < 0.6f)
                    {
                        if (intervalResolutionSource == "shared-window")
                        {
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                "low-conf(" + expressPosition.Confidence.ToString("0.00") + ")");
                        }
                        continue;
                    }

                    float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                        expressPosition,
                        expressProtectedInterval,
                        intervalDisplayLength,
                        includeApproachers: true,
                        out bool includeExpressCoordinate);

                    if (TryDescribeExpressReleaseWindowClear(
                            departureReleaseCoordinate,
                            expressPosition,
                            expressCoordinate,
                            includeExpressCoordinate,
                            out _,
                            out _))
                    {
                        sawReleaseClearedExpress = true;
                        releaseClearedUsedFallbackResolution = intervalResolutionSource == "fallback";
                        if (intervalResolutionSource == "shared-window")
                        {
                            string releaseReason = expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After
                                ? "express-cleared-bypass-release-window release=" + departureReleaseCoordinate.ToString("0.00") + " mapped=after"
                                : "express-cleared-bypass-release-window release=" + departureReleaseCoordinate.ToString("0.00")
                                    + " mapped=" + expressCoordinate.ToString("0.00");
                            LogSharedWindowFinalReject(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                localChain,
                                currentWaypointIndex,
                                currentBypassBuilding,
                                protectedIntervalIndex,
                                protectedInterval,
                                expressVehicle,
                                releaseReason);
                        }
                        continue;
                    }

                    if (TryEvaluateSameDirectionProtectedIntervalConflict(
                            localVehicle,
                            localLine,
                            localWaypoints,
                            currentWaypointIndex,
                            localChain,
                            protectedInterval,
                            currentBypassBuilding,
                            departureReleaseCoordinate,
                              localPosition,
                              expressChain,
                              expressProtectedInterval,
                              overlapCount,
                              orderedRun,
                              intervalDisplayLength,
                            localCoordinate,
                            includeLocalCoordinate,
                            selectedTrunkSegment,
                            expressCoordinate,
                            includeExpressCoordinate,
                            expressPosition,
                            out string conflictReason,
                            out string expressPositionText,
                            out string rejectReason,
                            out float blockerEntryFrames))
                    {
                        if (!foundBestBlocker || blockerEntryFrames < bestBlockerEntryFrames)
                        {
                            foundBestBlocker = true;
                            bestBlockerEntryFrames = blockerEntryFrames;
                            bestConflictReason = conflictReason;
                            bestExpressPositionText = expressPositionText;
                            bestExpressVehicle = expressVehicle;
                            bestExpressLine = expressLine;
                            bestExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
                            bestIntervalResolutionSource = intervalResolutionSource;
                            bestOverlapCount = overlapCount;
                            bestOrderedRun = orderedRun;
                            bestExpressAtomCursorIndex = expressCursor.AtomCursorIndex;
                            bestExpressPhaseEndAtomExclusive = runningVehicle.PhaseEndAtomExclusive;
                            bestUsedFallbackResolution = intervalResolutionSource == "fallback";
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(rejectReason)
                        && intervalResolutionSource == "shared-window")
                    {
                        LogSharedWindowFinalReject(
                            localVehicle,
                            localLine,
                            localWaypoints,
                            localChain,
                            currentWaypointIndex,
                            currentBypassBuilding,
                            protectedIntervalIndex,
                            protectedInterval,
                            expressVehicle,
                            rejectReason);
                    }
                }
            }

            if (foundBestBlocker)
            {
                LogBypassSelectedBlockerDetailOnce(
                    localVehicle,
                    localLine,
                    protectedIntervalIndex,
                    currentBypassBuilding,
                    bestExpressVehicle,
                    bestExpressLine,
                    bestExpressProtectedIntervalIndex,
                    bestIntervalResolutionSource,
                    bestOverlapCount,
                    bestOrderedRun,
                    bestExpressAtomCursorIndex,
                    bestExpressPhaseEndAtomExclusive,
                    bestExpressPositionText);
                m_SharedWindowAuditPairStateCache[new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, bestExpressVehicle)] = "blocker";
                shadowDecision = new BypassTrackModelShadowDecision(true, true, bestConflictReason, protectedIntervalIndex, true, bestExpressVehicle, bestUsedFallbackResolution);
                return true;
            }

            if (localStillWithinCurrentBypassStation
                && TryFindSameStationSameDirectionDepartureBlocker(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    localChain,
                    protectedInterval,
                    protectedIntervalIndex,
                    currentBypassBuilding,
                    intervalDisplayLength,
                    nowFrame,
                    out Entity sameStationBlocker))
            {
                shadowDecision = new BypassTrackModelShadowDecision(
                    true,
                    true,
                    "same-station-same-direction-express-departing",
                    protectedIntervalIndex,
                    true,
                    sameStationBlocker,
                    false);
                return true;
            }

            if (sawReleaseClearedExpress)
            {
                shadowDecision = new BypassTrackModelShadowDecision(true, false, "express-cleared-bypass-release-window", protectedIntervalIndex, true, Entity.Null, releaseClearedUsedFallbackResolution);
                return true;
            }

            TryLogSharedWindowAuditForNoExpress(
                localVehicle,
                localLine,
                localWaypoints,
                localChain,
                currentWaypointIndex,
                currentBypassBuilding,
                protectedIntervalIndex,
                protectedInterval);
            shadowDecision = new BypassTrackModelShadowDecision(true, false, "no-express-in-shared-window", protectedIntervalIndex, true, Entity.Null, false);
            return true;
        }

        private void LogSharedWindowFinalReject(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity expressVehicle,
            string rejectReason)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || string.IsNullOrWhiteSpace(rejectReason))
                return;

            string state = "finalReject|" + rejectReason;
            var pairKey = new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, expressVehicle);
            if (m_SharedWindowAuditPairStateCache.TryGetValue(pairKey, out string previousState)
                && previousState == state)
            {
                return;
            }
            m_SharedWindowAuditPairStateCache[pairKey] = state;
        }

        private void TryLogSharedWindowAuditForNoExpress(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || localChain == null)
            {
                return;
            }

            if (!ENABLE_TRACKMODEL_DIAGNOSTIC_LOGS)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = "no-express-in-shared-window|"
                + localLine.Index
                + "|"
                + protectedIntervalIndex
                + "|"
                + currentBypassBuilding.Index;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_SharedWindowAuditThrottleCache,
                    m_SharedWindowAuditLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            string auditSummary = BuildSharedWindowAuditSummary(
                localVehicle,
                localLine,
                localWaypoints,
                localChain,
                currentWaypointIndex,
                currentBypassBuilding,
                protectedInterval);
            LogSharedWindowAuditOnce(
                localVehicle,
                localLine,
                protectedIntervalIndex,
                auditSummary);
        }

        private bool TryGetSharedWindowPairState(
            Entity localVehicle,
            int protectedIntervalIndex,
            Entity expressVehicle,
            out string state)
        {
            state = string.Empty;
            if (localVehicle == Entity.Null || expressVehicle == Entity.Null)
                return false;

            return m_SharedWindowAuditPairStateCache.TryGetValue(
                new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, expressVehicle),
                out state)
                && !string.IsNullOrWhiteSpace(state);
        }

        private void TryLogTrainLaneSourceDisagreement(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            string stage)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Game.Vehicles.TrainCurrentLane>(vehicle))
            {
                return;
            }

            if (!ENABLE_TRACKMODEL_DIAGNOSTIC_LOGS)
                return;

            Entity pathTarget = Entity.Null;
            int pathElementIndex = -1;
            if (EntityManager.HasComponent<Game.Pathfind.PathOwner>(vehicle)
                && EntityManager.HasBuffer<Game.Pathfind.PathElement>(vehicle))
            {
                Game.Pathfind.PathOwner pathOwner = EntityManager.GetComponentData<Game.Pathfind.PathOwner>(vehicle);
                DynamicBuffer<Game.Pathfind.PathElement> path = EntityManager.GetBuffer<Game.Pathfind.PathElement>(vehicle, true);
                if (pathOwner.m_ElementIndex >= 0 && pathOwner.m_ElementIndex < path.Length)
                {
                    pathElementIndex = pathOwner.m_ElementIndex;
                    pathTarget = path[pathElementIndex].m_Target;
                }
            }

            Game.Vehicles.TrainCurrentLane currentLane = EntityManager.GetComponentData<Game.Vehicles.TrainCurrentLane>(vehicle);
            Entity frontLane = currentLane.m_Front.m_Lane;
            Entity rearLane = currentLane.m_Rear.m_Lane;
            Entity navigationLane = Entity.Null;
            if (EntityManager.HasBuffer<Game.Vehicles.TrainNavigationLane>(vehicle))
            {
                DynamicBuffer<Game.Vehicles.TrainNavigationLane> navigationLanes = EntityManager.GetBuffer<Game.Vehicles.TrainNavigationLane>(vehicle, true);
                if (navigationLanes.Length > 0)
                    navigationLane = navigationLanes[navigationLanes.Length - 1].m_Lane;
            }

            Entity cursorLane = Entity.Null;
            if (cursor.AtomCursorIndex >= 0 && cursor.AtomCursorIndex < chain.TrackAtoms.Count)
                cursorLane = chain.TrackAtoms[cursor.AtomCursorIndex].Key.PhysicalLaneKey;

            int distinctLaneCount = 0;
            HashSet<Entity> uniqueLanes = new HashSet<Entity>();
            if (pathTarget != Entity.Null && uniqueLanes.Add(pathTarget))
                distinctLaneCount++;
            if (frontLane != Entity.Null && uniqueLanes.Add(frontLane))
                distinctLaneCount++;
            if (rearLane != Entity.Null && uniqueLanes.Add(rearLane))
                distinctLaneCount++;
            if (navigationLane != Entity.Null && uniqueLanes.Add(navigationLane))
                distinctLaneCount++;
            if (cursorLane != Entity.Null && uniqueLanes.Add(cursorLane))
                distinctLaneCount++;

            if (distinctLaneCount <= 1)
                return;

            string key = stage
                + "|line=" + line.Index
                + "|pathIdx=" + pathElementIndex
                + "|path=" + pathTarget.Index
                + "|front=" + frontLane.Index
                + "|rear=" + rearLane.Index
                + "|nav=" + navigationLane.Index
                + "|cursorLane=" + cursorLane.Index
                + "|cursorAtom=" + cursor.AtomCursorIndex
                + "|seg=" + cursor.SegmentIndex;

            string message = "[TrainLaneSource] vehicle=" + vehicle.Index
                + " line=" + line.Index
                + " stage=" + stage
                + " pathIdx=" + pathElementIndex
                + " pathTarget=" + pathTarget.Index
                + " frontLane=" + frontLane.Index
                + " rearLane=" + rearLane.Index
                + " navLast=" + navigationLane.Index
                + " cursorLane=" + cursorLane.Index
                + " cursorAtom=" + cursor.AtomCursorIndex
                + " seg=" + cursor.SegmentIndex
                + " conf=" + cursor.Confidence.ToString("0.00");

            LogVehicleStateOnce(m_TrainLaneSourceDiagnosticLogCache, vehicle, key, message);
        }

        private void LogBypassSelectedBlockerDetailOnce(
            Entity localVehicle,
            Entity localLine,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            Entity expressVehicle,
            Entity expressLine,
            int expressProtectedIntervalIndex,
            string intervalResolutionSource,
            int overlapCount,
            int orderedRun,
            int expressCurrentAtomIndex,
            int expressPhaseEndAtomExclusive,
            string expressPositionText)
        {
            if (!ENABLE_TRACKMODEL_DIAGNOSTIC_LOGS)
                return;

            string key = localProtectedIntervalIndex.ToString()
                + "|b=" + currentBypassBuilding.Index
                + "|x=" + expressVehicle.Index
                + "|xp=" + expressProtectedIntervalIndex
                + "|src=" + intervalResolutionSource
                + "|ov=" + overlapCount
                + "|run=" + orderedRun
                + "|atom=" + expressCurrentAtomIndex
                + "|phaseEnd=" + expressPhaseEndAtomExclusive
                + "|pos=" + expressPositionText;

            string lineTag = localLine != Entity.Null ? "线路" + localLine.Index : "线路?";
            string expressLineTag = expressLine != Entity.Null ? "线路" + expressLine.Index : "线路?";
            string message = "[待避blocker明细] " + lineTag + " 车辆" + localVehicle.Index
                + " localInterval=" + localProtectedIntervalIndex
                + " current=" + currentBypassBuilding.Index
                + " blocker=" + expressVehicle.Index
                + " expressLine=" + expressLineTag
                + " expressInterval=" + expressProtectedIntervalIndex
                + " src=" + intervalResolutionSource
                + " overlap=" + overlapCount
                + " run=" + orderedRun
                + " expressAtom=" + expressCurrentAtomIndex
                + (expressPhaseEndAtomExclusive >= 0 ? " phaseEnd=" + expressPhaseEndAtomExclusive : string.Empty)
                + " " + expressPositionText;

            LogVehicleStateOnce(m_BypassSelectedBlockerDetailLogCache, localVehicle, key, message);
        }

        private bool TryEvaluateSameDirectionProtectedIntervalConflict(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            float departureReleaseCoordinate,
            TrackModelRuntimePosition localPosition,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            int overlapCount,
            int orderedRun,
            float intervalDisplayLength,
            float localCoordinate,
            bool includeLocal,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            float expressCoordinate,
            bool includeExpress,
            TrackModelRuntimePosition expressPosition,
            out string reason,
            out string expressPositionText,
            out string rejectReason,
            out float blockerEntryFrames)
        {
            reason = string.Empty;
            expressPositionText = FormatRuntimePosition(expressPosition);
            rejectReason = string.Empty;
            blockerEntryFrames = float.MaxValue;

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After)
            {
                rejectReason = "express-after-window";
                return false;
            }

            if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
            {
                rejectReason = "weak-physical-overlap overlap=" + overlapCount + " run=" + orderedRun;
                return false;
            }

            if (!includeLocal || !includeExpress)
            {
                rejectReason = "window-map-failed local=" + (includeLocal ? "1" : "0") + " express=" + (includeExpress ? "1" : "0");
                return false;
            }

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressCoordinate > departureReleaseCoordinate)
            {
                rejectReason = "express-cleared-release-window release=" + departureReleaseCoordinate.ToString("0.00")
                    + " mapped=" + expressCoordinate.ToString("0.00");
                return false;
            }

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressCoordinate >= intervalDisplayLength - PROTECTED_INTERVAL_TAIL_CLEARANCE_ATOMS)
            {
                rejectReason = "express-tail-cleared mapped=" + expressCoordinate.ToString("0.00");
                return false;
            }

            if (localPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressCoordinate > localCoordinate + SAME_DIRECTION_AHEAD_MARGIN_ATOMS)
            {
                rejectReason = "express-already-ahead mapped=" + expressCoordinate.ToString("0.00")
                    + " local=" + localCoordinate.ToString("0.00");
                return false;
            }

            if (!TryGetActiveConflictCorridorCurrent(
                    localChain,
                    localProtectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    expressPosition,
                    true,
                    selectedTrunkSegment,
                    out ConflictCorridor localCorridor,
                    out ConflictCorridor expressCorridor,
                    out GlobalSharedTrunkSegment trunkSegment))
            {
                rejectReason = "no-active-conflict-corridor";
                return false;
            }

            if (trunkSegment.TraversalRelation != SharedTraversalRelation.SameDirection)
            {
                rejectReason = "trunk-not-same-direction";
                return false;
            }

            bool hasLocalTraversalTiming = TryEstimateTraversalTimingWithinCorridor(localChain, localCorridor, localPosition, out TraversalTimingEstimate localTraversalTiming);
            float localClearFrames = hasLocalTraversalTiming
                ? localTraversalTiming.TotalFrames
                : EstimateRuntimeFramesToAtomBoundary(localChain, localPosition, localCorridor.EndAtomIndexExclusive);
            float localBoardingFrames = 0f;
            if (TryEstimateRemainingBoardingDwellFrames(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    currentBypassBuilding,
                    m_SimulationSystem.frameIndex,
                    out float estimatedBoardingFrames))
            {
                localBoardingFrames = estimatedBoardingFrames;
                if (localClearFrames != float.MaxValue)
                    localClearFrames += estimatedBoardingFrames;
            }
            float expressEntryFrames = EstimateRuntimeFramesToAtomBoundary(expressChain, expressPosition, expressCorridor.StartAtomIndex);
            blockerEntryFrames = expressEntryFrames;
            bool hasExpressTraversalTiming = TryEstimateTraversalTimingWithinCorridor(expressChain, expressCorridor, expressPosition, out TraversalTimingEstimate expressTraversalTiming);
            float expressClearFrames = hasExpressTraversalTiming
                ? (expressPosition.CurrentAtomIndex < expressCorridor.StartAtomIndex
                    ? expressEntryFrames + expressTraversalTiming.TotalFrames
                    : expressTraversalTiming.TotalFrames)
                : EstimateRuntimeFramesToAtomBoundary(expressChain, expressPosition, expressCorridor.EndAtomIndexExclusive);
            float safetyGapFrames = TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            string etaWindowText = " trunk local=a" + trunkSegment.LocalAnchorStartAtomIndex + ".." + trunkSegment.LocalAnchorEndAtomIndexExclusive
                + " express=a" + trunkSegment.ExpressAnchorStartAtomIndex + ".." + trunkSegment.ExpressAnchorEndAtomIndexExclusive
                + " overlap=" + trunkSegment.PhysicalOverlap
                + " run=" + trunkSegment.OrderedRun;
            etaWindowText += " " + FormatConflictCorridor(localCorridor)
                + " " + FormatConflictCorridor(expressCorridor);
            expressPositionText += " etaEntry=" + FormatEtaFrames(expressEntryFrames)
                + " expressClear=" + FormatEtaFrames(expressClearFrames)
                + " localClear=" + FormatEtaFrames(localClearFrames)
                + " localRun=" + FormatEtaFrames(hasLocalTraversalTiming ? localTraversalTiming.RunFrames : float.MaxValue)
                + " localStop=" + FormatEtaFrames(hasLocalTraversalTiming ? localTraversalTiming.StopFrames : 0f)
                + " localBoarding=" + FormatEtaFrames(localBoardingFrames)
                + " expressRun=" + FormatEtaFrames(hasExpressTraversalTiming ? expressTraversalTiming.RunFrames : float.MaxValue)
                + " expressStop=" + FormatEtaFrames(hasExpressTraversalTiming ? expressTraversalTiming.StopFrames : 0f)
                + " gap=" + FormatEtaFrames(safetyGapFrames)
                + etaWindowText;

            if (expressEntryFrames == float.MaxValue || expressClearFrames == float.MaxValue || localClearFrames == float.MaxValue)
            {
                rejectReason = "eta-unknown" + etaWindowText;
                return false;
            }

            if (expressEntryFrames >= localClearFrames - safetyGapFrames)
            {
                rejectReason = "eta-safe entry=" + FormatEtaFrames(expressEntryFrames)
                    + " clear=" + FormatEtaFrames(localClearFrames)
                    + " gap=" + FormatEtaFrames(safetyGapFrames)
                    + etaWindowText;
                return false;
            }

            if (!TryEvaluateLinearCatchRiskCurrentScene(
                    localProtectedInterval,
                    localCorridor,
                    localCoordinate,
                    expressPosition,
                    expressCoordinate,
                    localClearFrames,
                    expressEntryFrames,
                    expressClearFrames,
                    safetyGapFrames,
                    out string catchText,
                    out string catchRejectReason))
            {
                rejectReason = catchRejectReason + etaWindowText;
                return false;
            }

            expressPositionText += catchText;
            reason = "same-direction-shared-express-approaching";
            expressPositionText += " overlap=" + overlapCount + " run=" + orderedRun
                + " release=" + departureReleaseCoordinate.ToString("0.00")
                + " mapped=" + expressCoordinate.ToString("0.00")
                + " local=" + localCoordinate.ToString("0.00");
            return true;
        }

        private bool TryEstimateTraversalTimingWithinCorridor(
            LineTrackChain chain,
            ConflictCorridor corridor,
            TrackModelRuntimePosition runtimePosition,
            out TraversalTimingEstimate estimate)
        {
            estimate = default;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || corridor.EndAtomIndexExclusive <= corridor.StartAtomIndex)
            {
                return false;
            }

            float corridorStartCoordinate = corridor.StartAtomIndex;
            float corridorEndCoordinate = corridor.EndAtomIndexExclusive;
            float fromCoordinate = runtimePosition.CurrentAtomIndex + math.saturate(runtimePosition.AtomPosition01);
            if (runtimePosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Before
                || fromCoordinate < corridorStartCoordinate)
            {
                fromCoordinate = corridorStartCoordinate;
            }

            if (runtimePosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After
                || fromCoordinate >= corridorEndCoordinate)
            {
                estimate = new TraversalTimingEstimate(0f, 0f);
                return true;
            }

            float runFrames = 0f;
            for (int sliceIndex = 0; sliceIndex < chain.TraversalProfile.RunSlices.Count; sliceIndex++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[sliceIndex];
                if (!TryGetEffectiveTraversalRunSliceFrames(chain.LineEntity, slice, out float effectiveRunFrames)
                    || !(effectiveRunFrames > 0f))
                    continue;

                float overlapStart = math.max(fromCoordinate, slice.StartAtomIndex);
                float overlapEnd = math.min(corridorEndCoordinate, slice.EndAtomIndexExclusive);
                if (overlapEnd <= overlapStart)
                    continue;

                float sliceLength = math.max(1f, slice.EndAtomIndexExclusive - slice.StartAtomIndex);
                runFrames += effectiveRunFrames * ((overlapEnd - overlapStart) / sliceLength);
            }

            float stopFrames = 0f;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.Kind != TraversalEventKind.Stop
                    || !(traversalEvent.StopFrames > 0f))
                {
                    continue;
                }

                float eventCoordinate = traversalEvent.StartAtomIndex;
                if (eventCoordinate <= fromCoordinate + 0.001f
                    || eventCoordinate < corridorStartCoordinate
                    || eventCoordinate >= corridorEndCoordinate)
                {
                    continue;
                }

                stopFrames += traversalEvent.StopFrames;
            }

            estimate = new TraversalTimingEstimate(runFrames, stopFrames);
            return true;
        }

        private bool TryEvaluateLinearCatchRiskCurrentScene(
            BypassProtectedInterval localProtectedInterval,
            ConflictCorridor localCorridor,
            float localCoordinate,
            TrackModelRuntimePosition expressPosition,
            float expressCoordinate,
            float localClearFrames,
            float expressEntryFrames,
            float expressClearFrames,
            float safetyGapFrames,
            out string catchText,
            out string rejectReason)
        {
            catchText = string.Empty;
            rejectReason = string.Empty;

            float corridorStartCoordinate = MapAtomIndexToProtectedIntervalCoordinateExact(localProtectedInterval, localCorridor.StartAtomIndex);
            float corridorEndCoordinate = MapAtomIndexToProtectedIntervalCoordinateExact(localProtectedInterval, localCorridor.EndAtomIndexExclusive);
            if (!(corridorEndCoordinate > corridorStartCoordinate)
                || !(localClearFrames > 0f)
                || !(expressClearFrames > 0f))
            {
                rejectReason = "linear-catch-unknown";
                return false;
            }

            float localDistanceToClear = math.max(0f, corridorEndCoordinate - localCoordinate);
            float localSpeed = localDistanceToClear / math.max(1f, localClearFrames);
            if (!(localSpeed > 0f))
            {
                rejectReason = "linear-catch-safe local-speed"
                    + " localCoord=" + localCoordinate.ToString("0.00")
                    + " corridorEnd=" + corridorEndCoordinate.ToString("0.00")
                    + " localDist=" + localDistanceToClear.ToString("0.00")
                    + " localClear=" + FormatEtaFrames(localClearFrames);
                return false;
            }

            float modelStartFrames;
            float expressBaseCoordinate;
            float localBaseCoordinate;
            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Before
                && expressEntryFrames > 0f)
            {
                modelStartFrames = expressEntryFrames;
                expressBaseCoordinate = corridorStartCoordinate;
                localBaseCoordinate = math.min(corridorEndCoordinate, localCoordinate + localSpeed * expressEntryFrames);
            }
            else
            {
                modelStartFrames = 0f;
                expressBaseCoordinate = math.max(expressCoordinate, corridorStartCoordinate);
                localBaseCoordinate = localCoordinate;
            }

            float expressDistanceAfterBase = math.max(0f, corridorEndCoordinate - expressBaseCoordinate);
            float expressFramesAfterBase = expressClearFrames - modelStartFrames;
            if (!(expressFramesAfterBase > 0f))
            {
                rejectReason = "linear-catch-safe express-window"
                    + " modelStart=" + FormatEtaFrames(modelStartFrames)
                    + " expressClear=" + FormatEtaFrames(expressClearFrames)
                    + " expressBase=" + expressBaseCoordinate.ToString("0.00")
                    + " localBase=" + localBaseCoordinate.ToString("0.00")
                    + " expressDist=" + expressDistanceAfterBase.ToString("0.00");
                return false;
            }

            float expressSpeed = expressDistanceAfterBase / math.max(1f, expressFramesAfterBase);
            catchText = " catch[vL=" + localSpeed.ToString("0.000")
                + " vE=" + expressSpeed.ToString("0.000");

            if (!(expressSpeed > localSpeed))
            {
                rejectReason = "linear-catch-safe speed local=" + localSpeed.ToString("0.000")
                    + " express=" + expressSpeed.ToString("0.000")
                    + " localCoord=" + localCoordinate.ToString("0.00")
                    + " expressCoord=" + expressCoordinate.ToString("0.00")
                    + " corridor=" + corridorStartCoordinate.ToString("0.00") + ".." + corridorEndCoordinate.ToString("0.00")
                    + " modelStart=" + FormatEtaFrames(modelStartFrames)
                    + " localBase=" + localBaseCoordinate.ToString("0.00")
                    + " expressBase=" + expressBaseCoordinate.ToString("0.00")
                    + " localDist=" + localDistanceToClear.ToString("0.00")
                    + " expressDist=" + expressDistanceAfterBase.ToString("0.00")
                    + " localClear=" + FormatEtaFrames(localClearFrames)
                    + " expressEntry=" + FormatEtaFrames(expressEntryFrames)
                    + " expressClear=" + FormatEtaFrames(expressClearFrames)
                    + " expressAfterBase=" + FormatEtaFrames(expressFramesAfterBase)
                    + "]";
                catchText += "]";
                return false;
            }

            float deltaAtModelStart = localBaseCoordinate - expressBaseCoordinate;
            if (deltaAtModelStart <= SAME_DIRECTION_AHEAD_MARGIN_ATOMS)
            {
                catchText += " catch=" + FormatEtaFrames(modelStartFrames) + "]";
                return modelStartFrames < localClearFrames - safetyGapFrames;
            }

            float catchAfterBaseFrames = deltaAtModelStart / math.max(0.0001f, expressSpeed - localSpeed);
            float catchFrames = modelStartFrames + catchAfterBaseFrames;
            catchText += " catch=" + FormatEtaFrames(catchFrames) + "]";

            if (catchFrames >= localClearFrames - safetyGapFrames
                || catchFrames > expressClearFrames + safetyGapFrames)
            {
                rejectReason = "linear-catch-safe catch=" + FormatEtaFrames(catchFrames)
                    + " localClear=" + FormatEtaFrames(localClearFrames)
                    + " expressClear=" + FormatEtaFrames(expressClearFrames)
                    + " modelStart=" + FormatEtaFrames(modelStartFrames)
                    + " localBase=" + localBaseCoordinate.ToString("0.00")
                    + " expressBase=" + expressBaseCoordinate.ToString("0.00")
                    + " delta=" + deltaAtModelStart.ToString("0.00")
                    + " localSpeed=" + localSpeed.ToString("0.000")
                    + " expressSpeed=" + expressSpeed.ToString("0.000");
                return false;
            }

            return true;
        }

        private string BuildBypassDecisionSequenceSummaryForLogging(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            int protectedIntervalIndex)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || !TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return string.Empty;
            }

            EnsureTrackChainBypassPipelineReady(localChain);
            if (protectedIntervalIndex < 0 || protectedIntervalIndex >= localChain.BypassProtectedIntervals.Count)
                return string.Empty;

            return TryBuildBypassDecisionSequenceSummary(
                localVehicle,
                localLine,
                localWaypoints,
                currentWaypointIndex,
                localChain.BypassProtectedIntervals[protectedIntervalIndex],
                out string sequenceSummary)
                ? sequenceSummary
                : string.Empty;
        }

        private bool TryBuildProtectedIntervalConflictCandidate(
            Entity localVehicle,
            Entity localLine,
            LineTrackChain localChain,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            float departureReleaseCoordinate,
            float intervalDisplayLength,
            Entity expressVehicle,
            Entity expressLine,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            LineTrackChain expressChain,
            StringBuilder lineAudit,
            ref bool sawRunningVehicle)
        {
            if (expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                return false;
            if (!m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState) || expressState != VehicleState.Running)
                return false;
            sawRunningVehicle = true;
            int localProtectedIntervalIndex = FindProtectedIntervalIndex(localChain, protectedInterval);
            if (localProtectedIntervalIndex < 0)
                return false;

            PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, currentBypassBuilding, expressChain);
            if (!sharedWindowMatch.Found)
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":no-shared-window");
                return false;
            }

            if (!TryResolveExpressConflictWindowForLocalConflict(
                    expressVehicle,
                    expressLine,
                    expressWaypoints,
                    expressChain,
                    localChain,
                    localProtectedIntervalIndex,
                    protectedInterval,
                    sharedWindowMatch,
                    out int expressProtectedIntervalIndex,
                    out BypassProtectedInterval expressProtectedInterval,
                    out int overlapCount,
                    out int orderedRun,
                    out string intervalResolutionSource))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":protected-interval-missing");
                return false;
            }

            string resolutionSuffix = intervalResolutionSource == "fallback"
                ? " src=fallback"
                : intervalResolutionSource == "shared-window"
                    ? " src=shared-window"
                    : string.Empty;

            if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
            {
                if (lineAudit != null)
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index)
                        .Append(":weak-physical-overlap overlap=").Append(overlapCount)
                        .Append(" run=").Append(orderedRun)
                        .Append(resolutionSuffix);
                }
                return false;
            }

            if (!TryGetVehicleTrackCursorCurrentFrame(expressVehicle, expressLine, expressWaypoints, expressChain, out VehicleTrackCursor expressCursor))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":cursor-fail").Append(resolutionSuffix);
                return false;
            }

            if (!IsProtectedIntervalPairStaticallySameDirection(
                    localChain,
                    protectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    expressCursor.AtomCursorIndex))
            {
                if (lineAudit != null)
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":static-opposite-direction").Append(resolutionSuffix);
                return false;
            }

              if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
              {
                  if (lineAudit != null)
                      lineAudit.Append(" #").Append(expressVehicle.Index).Append(":proj-fail");
                  return false;
            }
              if (expressPosition.Confidence < 0.6f)
              {
                  if (lineAudit != null)
                      lineAudit.Append(" #").Append(expressVehicle.Index).Append(":low-conf(").Append(expressPosition.Confidence.ToString("0.00")).Append(")");
                  return false;
              }

              float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                  expressPosition,
                  expressProtectedInterval,
                  intervalDisplayLength,
                  includeApproachers: true,
                  out bool includeExpress);

              if (TryDescribeExpressReleaseWindowClear(
                      departureReleaseCoordinate,
                      expressPosition,
                      expressCoordinate,
                      includeExpress,
                      out string releaseReason,
                      out string releasePositionText))
              {
                  if (lineAudit != null)
                  {
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(releaseReason)
                        .Append("(").Append(releasePositionText).Append(resolutionSuffix).Append(")");
                }
                return false;
            }

              if (!TryGetActiveConflictCorridorCurrent(
                      localChain,
                      protectedInterval,
                      currentBypassBuilding,
                      expressChain,
                      expressProtectedInterval,
                      expressPosition,
                      out _,
                      out _,
                      out _))
              {
                  if (lineAudit != null)
                      lineAudit.Append(" #").Append(expressVehicle.Index).Append(":no-active-conflict-corridor").Append(resolutionSuffix);
                  return false;
              }
              if (!includeExpress)
              {
                  if (lineAudit != null)
                      lineAudit.Append(" #").Append(expressVehicle.Index).Append(":window-map-failed express=0").Append(resolutionSuffix);
                  return false;
            }

            if (lineAudit != null)
            {
                if (TryGetSharedWindowPairState(localVehicle, localProtectedIntervalIndex, expressVehicle, out string pairState))
                {
                    string stateText = pairState.StartsWith("finalReject|", StringComparison.Ordinal)
                        ? pairState.Substring("finalReject|".Length)
                        : pairState;
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(stateText).Append(resolutionSuffix);
                }
                else
                {
                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":candidate").Append(resolutionSuffix);
                }
            }
            return true;
        }

        private bool TryDescribeExpressReleaseWindowClear(
            float departureReleaseCoordinate,
            TrackModelRuntimePosition expressPosition,
            float expressCoordinate,
            bool includeExpress,
            out string reason,
            out string expressPositionText)
        {
            reason = string.Empty;
            expressPositionText = string.Empty;

            if (!includeExpress)
                return false;

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After)
            {
                reason = "express-cleared-bypass-release-window";
                expressPositionText = FormatRuntimePosition(expressPosition)
                    + " release=" + departureReleaseCoordinate.ToString("0.00")
                    + " mapped=after";
                return true;
            }

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressCoordinate > departureReleaseCoordinate)
            {
                reason = "express-cleared-bypass-release-window";
                expressPositionText = FormatRuntimePosition(expressPosition)
                    + " release=" + departureReleaseCoordinate.ToString("0.00")
                    + " mapped=" + expressCoordinate.ToString("0.00");
                return true;
            }

            return false;
        }

        private bool TryFindBestProtectedSharedInterval(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            out ProtectedSharedInterval localSharedInterval,
            out ProtectedSharedInterval expressSharedInterval)
        {
            localSharedInterval = default;
            expressSharedInterval = default;

            bool hasAnchor = TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
            int localAnchorMaxStartAtomIndex = hasAnchor
                ? stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS
                : int.MaxValue;
            int bestAnchorDistance = int.MaxValue;
            int bestPhysicalOverlap = 0;
            int bestLocalWindowOverlap = 0;
            int bestExpressWindowOverlap = 0;
            bool found = false;
            for (int localIndex = 0; localIndex < localChain.ProtectedSharedIntervals.Count; localIndex++)
            {
                ProtectedSharedInterval localCandidate = localChain.ProtectedSharedIntervals[localIndex];
                int clippedLocalStart = math.max(localCandidate.StartAtomIndex, localProtectedInterval.StartAtomIndex);
                int clippedLocalEndExclusive = math.min(localCandidate.EndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                if (clippedLocalEndExclusive <= clippedLocalStart)
                    continue;
                if (clippedLocalStart > localAnchorMaxStartAtomIndex)
                    continue;

                int localWindowOverlap = CountAtomIntervalOverlap(
                    localCandidate.StartAtomIndex,
                    localCandidate.EndAtomIndexExclusive,
                    localProtectedInterval.StartAtomIndex,
                    localProtectedInterval.EndAtomIndexExclusive);
                if (localWindowOverlap <= 0)
                    continue;

                int anchorDistance = clippedLocalStart - localProtectedInterval.StartAtomIndex;

                for (int expressIndex = 0; expressIndex < expressChain.ProtectedSharedIntervals.Count; expressIndex++)
                {
                    ProtectedSharedInterval expressCandidate = expressChain.ProtectedSharedIntervals[expressIndex];
                    int expressWindowOverlap = CountAtomIntervalOverlap(
                        expressCandidate.StartAtomIndex,
                        expressCandidate.EndAtomIndexExclusive,
                        expressProtectedInterval.StartAtomIndex,
                        expressProtectedInterval.EndAtomIndexExclusive);
                    if (expressWindowOverlap <= 0)
                        continue;

                    int physicalOverlap = CountSharedPhysicalOverlap(
                        localChain,
                        localCandidate.StartAtomIndex,
                        localCandidate.EndAtomIndexExclusive,
                        expressChain,
                        expressCandidate.StartAtomIndex,
                        expressCandidate.EndAtomIndexExclusive);
                    if (physicalOverlap <= 0)
                        continue;

                    bool better = !found;
                    if (!better && anchorDistance != bestAnchorDistance)
                        better = anchorDistance < bestAnchorDistance;
                    if (!better && physicalOverlap != bestPhysicalOverlap)
                        better = physicalOverlap > bestPhysicalOverlap;
                    if (!better && localWindowOverlap != bestLocalWindowOverlap)
                        better = localWindowOverlap > bestLocalWindowOverlap;
                    if (!better && expressWindowOverlap != bestExpressWindowOverlap)
                        better = expressWindowOverlap > bestExpressWindowOverlap;
                    if (!better)
                        continue;

                    bestAnchorDistance = anchorDistance;
                    bestPhysicalOverlap = physicalOverlap;
                    bestLocalWindowOverlap = localWindowOverlap;
                    bestExpressWindowOverlap = expressWindowOverlap;
                    localSharedInterval = localCandidate;
                    expressSharedInterval = expressCandidate;
                    found = true;
                }
            }

            return found;
        }

        private bool TryBuildConflictCorridor(
            LineTrackChain chain,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            ProtectedSharedInterval anchorSharedInterval,
            out ConflictCorridor corridor)
        {
            corridor = default;
            if (chain == null
                || anchorSharedInterval.EndAtomIndexExclusive <= anchorSharedInterval.StartAtomIndex)
            {
                return false;
            }

            List<ProtectedSharedInterval> intervals = new List<ProtectedSharedInterval>();
            for (int i = 0; i < chain.ProtectedSharedIntervals.Count; i++)
            {
                ProtectedSharedInterval candidate = chain.ProtectedSharedIntervals[i];
                if (candidate.ProtectedIntervalIndex == anchorSharedInterval.ProtectedIntervalIndex)
                    intervals.Add(candidate);
            }

            if (intervals.Count == 0)
                return false;

            intervals.Sort((a, b) => a.StartAtomIndex.CompareTo(b.StartAtomIndex));

            int anchorIndex = -1;
            for (int i = 0; i < intervals.Count; i++)
            {
                ProtectedSharedInterval candidate = intervals[i];
                if (candidate.StartAtomIndex == anchorSharedInterval.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == anchorSharedInterval.EndAtomIndexExclusive
                    && candidate.ControlEdgeIndex == anchorSharedInterval.ControlEdgeIndex)
                {
                    anchorIndex = i;
                    break;
                }
            }

            if (anchorIndex < 0)
                return false;

            int mergedStart = intervals[anchorIndex].StartAtomIndex;
            int mergedEndExclusive = intervals[anchorIndex].EndAtomIndexExclusive;
            int sharedSliceCount = 1;
            int bridgedGapAtoms = 0;

            for (int i = anchorIndex + 1; i < intervals.Count; i++)
            {
                ProtectedSharedInterval candidate = intervals[i];
                int gapAtoms = math.max(0, candidate.StartAtomIndex - mergedEndExclusive);
                if (gapAtoms > MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
                    break;

                mergedEndExclusive = candidate.EndAtomIndexExclusive;
                bridgedGapAtoms += gapAtoms;
                sharedSliceCount++;
            }

            int corridorStart = mergedStart;
            int prefixGapAtoms = math.max(0, mergedStart - protectedInterval.StartAtomIndex);
            if (currentBypassBuilding != Entity.Null
                && prefixGapAtoms <= MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
            {
                corridorStart = protectedInterval.StartAtomIndex;
                bridgedGapAtoms += prefixGapAtoms;
            }

            int corridorEndExclusive = mergedEndExclusive;
            int suffixGapAtoms = math.max(0, protectedInterval.EndAtomIndexExclusive - mergedEndExclusive);
            if (suffixGapAtoms <= MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
            {
                corridorEndExclusive = protectedInterval.EndAtomIndexExclusive;
                bridgedGapAtoms += suffixGapAtoms;
            }

            if (corridorEndExclusive <= corridorStart)
                return false;

            corridor = new ConflictCorridor(
                anchorSharedInterval.ProtectedIntervalIndex,
                corridorStart,
                corridorEndExclusive,
                anchorSharedInterval.StartAtomIndex,
                anchorSharedInterval.EndAtomIndexExclusive,
                sharedSliceCount,
                bridgedGapAtoms);
            return true;
        }

        private static string FormatConflictCorridor(ConflictCorridor corridor)
        {
            return "corridor[p=" + corridor.ProtectedIntervalIndex
                + " a" + corridor.StartAtomIndex + ".." + corridor.EndAtomIndexExclusive
                + " anchor=" + corridor.AnchorSharedStartAtomIndex + ".." + corridor.AnchorSharedEndAtomIndexExclusive
                + " slices=" + corridor.SharedSliceCount
                + " gap=" + corridor.BridgedGapAtoms
                + "]";
        }

        private static int CountAtomIntervalOverlap(int startA, int endAExclusive, int startB, int endBExclusive)
        {
            int overlapStart = math.max(startA, startB);
            int overlapEndExclusive = math.min(endAExclusive, endBExclusive);
            return math.max(0, overlapEndExclusive - overlapStart);
        }

        private static int ResolveProtectedIntervalIndex(LineTrackChain chain, BypassProtectedInterval interval)
        {
            if (chain == null)
                return -1;

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                if (candidate.StartAtomIndex == interval.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == interval.EndAtomIndexExclusive
                    && candidate.StartControlEdgeIndex == interval.StartControlEdgeIndex
                    && candidate.EndControlEdgeIndexInclusive == interval.EndControlEdgeIndexInclusive)
                {
                    return i;
                }
            }

            return -1;
        }

        private static TrackTraversalDir GetPrimaryTraversalDirNear(LineTrackChain chain, int atomIndex, int step)
        {
            if (chain == null || chain.TrackAtoms.Count == 0 || step == 0)
                return TrackTraversalDir.Unknown;

            for (int index = atomIndex; index >= 0 && index < chain.TrackAtoms.Count; index += step)
            {
                TrackAtom atom = chain.TrackAtoms[index];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                if (atom.TraversalDir != TrackTraversalDir.Unknown)
                    return atom.TraversalDir;
            }

            return TrackTraversalDir.Unknown;
        }

        private static bool ShouldSplitSharedBandAtGap(LineTrackChain chain, int mergedEndExclusive, SharedTrackRun candidate)
        {
            if (chain == null || candidate.EndAtomIndexExclusive <= candidate.StartAtomIndex)
                return false;

            TrackTraversalDir previousDir = GetPrimaryTraversalDirNear(chain, math.max(0, mergedEndExclusive - 1), -1);
            TrackTraversalDir nextDir = GetPrimaryTraversalDirNear(chain, candidate.StartAtomIndex, 1);
            return previousDir != TrackTraversalDir.Unknown
                && nextDir != TrackTraversalDir.Unknown
                && previousDir != nextDir;
        }

        private static void BuildSharedRunBands(LineTrackChain chain, List<SharedTrackRun> sourceRuns, List<SharedRunBand> bands)
        {
            bands.Clear();
            if (sourceRuns == null || sourceRuns.Count == 0)
                return;

            int mergedStart = sourceRuns[0].StartAtomIndex;
            int mergedEndExclusive = sourceRuns[0].EndAtomIndexExclusive;
            int sharedSliceCount = 1;
            int bridgedGapAtoms = 0;
            bool hasMirroredContext = sourceRuns[0].HasMirroredContext;
            int maxSharedLineCount = sourceRuns[0].SharedLineCount;

            for (int i = 1; i < sourceRuns.Count; i++)
            {
                SharedTrackRun candidate = sourceRuns[i];
                int gapAtoms = math.max(0, candidate.StartAtomIndex - mergedEndExclusive);
                if (gapAtoms > MAX_CONFLICT_CORRIDOR_GAP_ATOMS
                    || ShouldSplitSharedBandAtGap(chain, mergedEndExclusive, candidate))
                {
                    bands.Add(new SharedRunBand(
                        mergedStart,
                        mergedEndExclusive,
                        sharedSliceCount,
                        bridgedGapAtoms,
                        hasMirroredContext,
                        maxSharedLineCount));

                    mergedStart = candidate.StartAtomIndex;
                    mergedEndExclusive = candidate.EndAtomIndexExclusive;
                    sharedSliceCount = 1;
                    bridgedGapAtoms = 0;
                    hasMirroredContext = candidate.HasMirroredContext;
                    maxSharedLineCount = candidate.SharedLineCount;
                    continue;
                }

                mergedEndExclusive = math.max(mergedEndExclusive, candidate.EndAtomIndexExclusive);
                sharedSliceCount++;
                bridgedGapAtoms += gapAtoms;
                hasMirroredContext |= candidate.HasMirroredContext;
                maxSharedLineCount = math.max(maxSharedLineCount, candidate.SharedLineCount);
            }

            bands.Add(new SharedRunBand(
                mergedStart,
                mergedEndExclusive,
                sharedSliceCount,
                bridgedGapAtoms,
                hasMirroredContext,
                maxSharedLineCount));
        }

        private static void CollectPhysicalLaneKeySet(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive, HashSet<Entity> keys)
        {
            keys.Clear();
            if (chain == null || chain.TrackAtoms.Count == 0)
                return;

            int start = math.max(0, startAtomIndex);
            int endExclusive = math.min(endAtomIndexExclusive, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < endExclusive; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass == TrackAtomClass.PrimaryLane)
                    keys.Add(atom.Key.PhysicalLaneKey);
            }
        }

        private static void CollectDistinctSharedPhysicalKeyOrder(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive, HashSet<Entity> sharedKeys, List<Entity> orderedKeys)
        {
            orderedKeys.Clear();
            if (chain == null || chain.TrackAtoms.Count == 0 || sharedKeys == null || sharedKeys.Count == 0)
                return;

            var seen = new HashSet<Entity>();
            int start = math.max(0, startAtomIndex);
            int endExclusive = math.min(endAtomIndexExclusive, chain.TrackAtoms.Count);
            for (int atomIndex = start; atomIndex < endExclusive; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                if (!sharedKeys.Contains(physicalLaneKey) || !seen.Add(physicalLaneKey))
                    continue;

                orderedKeys.Add(physicalLaneKey);
            }
        }

        private static int ComputeOrderedPhysicalKeyLcsLength(List<Entity> leftKeys, List<Entity> rightKeys)
        {
            if (leftKeys == null || rightKeys == null || leftKeys.Count == 0 || rightKeys.Count == 0)
                return 0;

            int[] previous = new int[rightKeys.Count + 1];
            int[] current = new int[rightKeys.Count + 1];
            for (int leftIndex = 1; leftIndex <= leftKeys.Count; leftIndex++)
            {
                Entity leftKey = leftKeys[leftIndex - 1];
                for (int rightIndex = 1; rightIndex <= rightKeys.Count; rightIndex++)
                {
                    if (leftKey == rightKeys[rightIndex - 1])
                    {
                        current[rightIndex] = previous[rightIndex - 1] + 1;
                    }
                    else
                    {
                        current[rightIndex] = math.max(previous[rightIndex], current[rightIndex - 1]);
                    }
                }

                int[] swap = previous;
                previous = current;
                current = swap;
                Array.Clear(current, 0, current.Length);
            }

            return previous[rightKeys.Count];
        }

        private static SharedTraversalRelation ResolveSharedTraversalRelation(
            LineTrackChain localChain,
            SharedRunBand localBand,
            LineTrackChain expressChain,
            SharedRunBand expressBand)
        {
            if (localChain == null || expressChain == null)
                return SharedTraversalRelation.Unknown;

            var localKeys = new HashSet<Entity>();
            var expressKeys = new HashSet<Entity>();
            CollectPhysicalLaneKeySet(localChain, localBand.StartAtomIndex, localBand.EndAtomIndexExclusive, localKeys);
            CollectPhysicalLaneKeySet(expressChain, expressBand.StartAtomIndex, expressBand.EndAtomIndexExclusive, expressKeys);
            localKeys.IntersectWith(expressKeys);
            if (localKeys.Count == 0)
                return SharedTraversalRelation.Unknown;

            var localOrder = new List<Entity>();
            var expressOrder = new List<Entity>();
            CollectDistinctSharedPhysicalKeyOrder(localChain, localBand.StartAtomIndex, localBand.EndAtomIndexExclusive, localKeys, localOrder);
            CollectDistinctSharedPhysicalKeyOrder(expressChain, expressBand.StartAtomIndex, expressBand.EndAtomIndexExclusive, localKeys, expressOrder);
            if (localOrder.Count == 0 || expressOrder.Count == 0)
                return SharedTraversalRelation.Unknown;

            int forwardScore = ComputeOrderedPhysicalKeyLcsLength(localOrder, expressOrder);
            var reverseExpressOrder = new List<Entity>(expressOrder.Count);
            for (int i = expressOrder.Count - 1; i >= 0; i--)
                reverseExpressOrder.Add(expressOrder[i]);
            int reverseScore = ComputeOrderedPhysicalKeyLcsLength(localOrder, reverseExpressOrder);

            if (forwardScore >= 2 || reverseScore >= 2)
            {
                if (forwardScore > reverseScore)
                    return SharedTraversalRelation.SameDirection;
                if (reverseScore > forwardScore)
                    return SharedTraversalRelation.OppositeDirection;
            }

            if (localBand.HasMirroredContext || expressBand.HasMirroredContext)
                return SharedTraversalRelation.OppositeDirection;

            return SharedTraversalRelation.Unknown;
        }

        private static SharedTraversalRelation ResolveSharedTraversalRelation(
            LineTrackChain localChain,
            AtomWindowSlice localSlice,
            LineTrackChain expressChain,
            AtomWindowSlice expressSlice)
        {
            return ResolveSharedTraversalRelation(
                localChain,
                new SharedRunBand(localSlice.StartAtomIndex, localSlice.EndAtomIndexExclusive, localSlice.SharedSliceCount, localSlice.BridgedGapAtoms, localSlice.HasMirroredContext, localSlice.MaxSharedLineCount),
                expressChain,
                new SharedRunBand(expressSlice.StartAtomIndex, expressSlice.EndAtomIndexExclusive, expressSlice.SharedSliceCount, expressSlice.BridgedGapAtoms, expressSlice.HasMirroredContext, expressSlice.MaxSharedLineCount));
        }

        private static void CollectControlEdgeSlices(LineTrackChain chain, SharedRunBand band, List<AtomWindowSlice> slices)
        {
            slices.Clear();
            if (chain == null || chain.ControlEdges == null || chain.ControlEdges.Count == 0)
                return;

            for (int edgeIndex = 0; edgeIndex < chain.ControlEdges.Count; edgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[edgeIndex];
                int startAtomIndex = math.max(edge.StartAtomIndex, band.StartAtomIndex);
                int endAtomIndexExclusive = math.min(edge.EndAtomIndexExclusive, band.EndAtomIndexExclusive);
                if (endAtomIndexExclusive <= startAtomIndex)
                    continue;

                slices.Add(new AtomWindowSlice(
                    startAtomIndex,
                    endAtomIndexExclusive,
                    1,
                    0,
                    band.HasMirroredContext,
                    band.MaxSharedLineCount));
            }

            if (slices.Count == 0)
            {
                slices.Add(new AtomWindowSlice(
                    band.StartAtomIndex,
                    band.EndAtomIndexExclusive,
                    band.SharedSliceCount,
                    band.BridgedGapAtoms,
                    band.HasMirroredContext,
                    band.MaxSharedLineCount));
            }
        }

        private void BuildDirectedSharedPairSegments(
            LineTrackChain localChain,
            List<SharedRunBand> localBands,
            LineTrackChain expressChain,
            List<SharedRunBand> expressBands,
            List<DirectedSharedPairSegment> pairSegments)
        {
            pairSegments.Clear();
            if (localChain == null
                || expressChain == null
                || localBands == null
                || expressBands == null
                || localBands.Count == 0
                || expressBands.Count == 0)
            {
                return;
            }

            for (int localIndex = 0; localIndex < localBands.Count; localIndex++)
            {
                SharedRunBand localBand = localBands[localIndex];
                var localSlices = new List<AtomWindowSlice>();
                CollectControlEdgeSlices(localChain, localBand, localSlices);

                for (int expressIndex = 0; expressIndex < expressBands.Count; expressIndex++)
                {
                    SharedRunBand expressBand = expressBands[expressIndex];
                    var expressSlices = new List<AtomWindowSlice>();
                    CollectControlEdgeSlices(expressChain, expressBand, expressSlices);

                    for (int localSliceIndex = 0; localSliceIndex < localSlices.Count; localSliceIndex++)
                    {
                        AtomWindowSlice localSlice = localSlices[localSliceIndex];
                        BypassProtectedInterval localWindow = BuildAtomWindowInterval(localChain, localSlice.StartAtomIndex, localSlice.EndAtomIndexExclusive);
                        if (localWindow.EndAtomIndexExclusive <= localWindow.StartAtomIndex)
                            continue;

                        for (int expressSliceIndex = 0; expressSliceIndex < expressSlices.Count; expressSliceIndex++)
                        {
                            AtomWindowSlice expressSlice = expressSlices[expressSliceIndex];
                            BypassProtectedInterval expressWindow = BuildAtomWindowInterval(expressChain, expressSlice.StartAtomIndex, expressSlice.EndAtomIndexExclusive);
                            if (expressWindow.EndAtomIndexExclusive <= expressWindow.StartAtomIndex)
                                continue;

                            int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                            if (overlapCount <= 0)
                                continue;

                            int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localWindow, expressChain, expressWindow);
                            if (orderedRun <= 0)
                                continue;

                            int pairLocalStartAtomIndex = localSlice.StartAtomIndex;
                            int pairLocalEndAtomIndexExclusive = localSlice.EndAtomIndexExclusive;
                            int pairExpressStartAtomIndex = expressSlice.StartAtomIndex;
                            int pairExpressEndAtomIndexExclusive = expressSlice.EndAtomIndexExclusive;
                            if (TryFindProtectedIntervalOrderedRunSpan(
                                    localChain,
                                    localWindow,
                                    expressChain,
                                    expressWindow,
                                    out int orderedLocalStartAtomIndex,
                                    out int orderedLocalEndAtomIndexExclusive,
                                    out int orderedExpressStartAtomIndex,
                                    out int orderedExpressEndAtomIndexExclusive,
                                    out int orderedRunSpanLength)
                                && orderedRunSpanLength > 0)
                            {
                                pairLocalStartAtomIndex = orderedLocalStartAtomIndex;
                                pairLocalEndAtomIndexExclusive = orderedLocalEndAtomIndexExclusive;
                                pairExpressStartAtomIndex = orderedExpressStartAtomIndex;
                                pairExpressEndAtomIndexExclusive = orderedExpressEndAtomIndexExclusive;
                                orderedRun = orderedRunSpanLength;
                            }

                            SharedTraversalRelation traversalRelation = ResolveSharedTraversalRelation(localChain, localSlice, expressChain, expressSlice);
                            pairSegments.Add(new DirectedSharedPairSegment(
                                pairLocalStartAtomIndex,
                                pairLocalEndAtomIndexExclusive,
                                pairExpressStartAtomIndex,
                                pairExpressEndAtomIndexExclusive,
                                localSlice.SharedSliceCount,
                                expressSlice.SharedSliceCount,
                                localSlice.BridgedGapAtoms,
                                expressSlice.BridgedGapAtoms,
                                overlapCount,
                                orderedRun,
                                localSlice.HasMirroredContext || expressSlice.HasMirroredContext,
                                math.max(localSlice.MaxSharedLineCount, expressSlice.MaxSharedLineCount),
                                traversalRelation));
                        }
                    }
                }
            }
        }

        private GlobalSharedTrunkSnapshot BuildGlobalSharedTrunkSnapshot(LineTrackChain localChain, LineTrackChain expressChain)
        {
            var snapshot = new GlobalSharedTrunkSnapshot
            {
                SharedTrackVersion = m_SharedTrackIndexVersion,
                LocalChainSignature = localChain?.Signature ?? 0UL,
                ExpressChainSignature = expressChain?.Signature ?? 0UL
            };

            if (localChain == null
                || expressChain == null
                || !localChain.SharedRunsByOtherLine.TryGetValue(expressChain.LineEntity, out List<SharedTrackRun> localSharedRuns)
                || localSharedRuns == null
                || localSharedRuns.Count == 0
                || !expressChain.SharedRunsByOtherLine.TryGetValue(localChain.LineEntity, out List<SharedTrackRun> expressSharedRuns)
                || expressSharedRuns == null
                || expressSharedRuns.Count == 0)
            {
                return snapshot;
            }

            var localBands = new List<SharedRunBand>();
            var expressBands = new List<SharedRunBand>();
            BuildSharedRunBands(localChain, localSharedRuns, localBands);
            BuildSharedRunBands(expressChain, expressSharedRuns, expressBands);
            var pairSegments = new List<DirectedSharedPairSegment>();
            BuildDirectedSharedPairSegments(localChain, localBands, expressChain, expressBands, pairSegments);
            for (int i = 0; i < pairSegments.Count; i++)
            {
                DirectedSharedPairSegment pair = pairSegments[i];
                snapshot.Segments.Add(new GlobalSharedTrunkSegment(
                    pair.LocalStartAtomIndex,
                    pair.LocalEndAtomIndexExclusive,
                    pair.ExpressStartAtomIndex,
                    pair.ExpressEndAtomIndexExclusive,
                    pair.LocalStartAtomIndex,
                    pair.LocalEndAtomIndexExclusive,
                    pair.ExpressStartAtomIndex,
                    pair.ExpressEndAtomIndexExclusive,
                    pair.LocalSharedSliceCount,
                    pair.ExpressSharedSliceCount,
                    pair.LocalBridgedGapAtoms,
                    pair.ExpressBridgedGapAtoms,
                    pair.PhysicalOverlap,
                    pair.OrderedRun,
                    pair.HasMirroredContext,
                    pair.MaxSharedLineCount,
                    pair.TraversalRelation));
            }

            return snapshot;
        }

        private GlobalSharedTrunkSnapshot GetGlobalSharedTrunkSnapshotCurrent(LineTrackChain localChain, LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return null;

            EnsureSharedTrackIndexCurrent();
            RefreshSharedRuns(localChain);
            RefreshSharedRuns(expressChain);

            var key = new GlobalSharedTrunkCacheKey(localChain.LineEntity, expressChain.LineEntity);
            if (m_GlobalSharedTrunkSnapshots.TryGetValue(key, out GlobalSharedTrunkSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot;
            }

            snapshot = BuildGlobalSharedTrunkSnapshot(localChain, expressChain);
            m_GlobalSharedTrunkSnapshots[key] = snapshot;
            return snapshot;
        }

        private ProtectedIntervalPairMetricsSnapshot BuildProtectedIntervalPairMetricsSnapshot(LineTrackChain localChain, LineTrackChain expressChain)
        {
            var snapshot = new ProtectedIntervalPairMetricsSnapshot
            {
                SharedTrackVersion = m_SharedTrackIndexVersion,
                LocalChainSignature = localChain?.Signature ?? 0UL,
                ExpressChainSignature = expressChain?.Signature ?? 0UL,
                LocalIntervalCount = localChain?.BypassProtectedIntervals.Count ?? 0,
                ExpressIntervalCount = expressChain?.BypassProtectedIntervals.Count ?? 0
            };

            if (localChain == null
                || expressChain == null
                || snapshot.LocalIntervalCount <= 0
                || snapshot.ExpressIntervalCount <= 0)
            {
                return snapshot;
            }

            snapshot.Metrics = new ProtectedIntervalPairMetrics[snapshot.LocalIntervalCount * snapshot.ExpressIntervalCount];
            for (int localIndex = 0; localIndex < snapshot.LocalIntervalCount; localIndex++)
            {
                BypassProtectedInterval localInterval = localChain.BypassProtectedIntervals[localIndex];
                for (int expressIndex = 0; expressIndex < snapshot.ExpressIntervalCount; expressIndex++)
                {
                    BypassProtectedInterval expressInterval = expressChain.BypassProtectedIntervals[expressIndex];
                    int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localInterval, expressChain, expressInterval);
                    int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localInterval, expressChain, expressInterval);
                    snapshot.Metrics[(localIndex * snapshot.ExpressIntervalCount) + expressIndex] = new ProtectedIntervalPairMetrics(overlapCount, orderedRun);
                }
            }

            return snapshot;
        }

        private ProtectedIntervalPairMetricsSnapshot GetProtectedIntervalPairMetricsSnapshotCurrent(LineTrackChain localChain, LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return null;

            EnsureSharedTrackIndexCurrent();
            RefreshSharedRuns(localChain);
            RefreshSharedRuns(expressChain);
            EnsureTrackChainBypassPipelineReady(localChain);
            EnsureTrackChainBypassPipelineReady(expressChain);

            var key = new ProtectedIntervalPairMetricsCacheKey(localChain.LineEntity, expressChain.LineEntity);
            if (m_ProtectedIntervalPairMetricsSnapshots.TryGetValue(key, out ProtectedIntervalPairMetricsSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature
                && snapshot.LocalIntervalCount == localChain.BypassProtectedIntervals.Count
                && snapshot.ExpressIntervalCount == expressChain.BypassProtectedIntervals.Count)
            {
                return snapshot;
            }

            snapshot = BuildProtectedIntervalPairMetricsSnapshot(localChain, expressChain);
            m_ProtectedIntervalPairMetricsSnapshots[key] = snapshot;
            return snapshot;
        }

        private bool TryGetProtectedIntervalPairMetricsCurrent(
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            LineTrackChain expressChain,
            int expressProtectedIntervalIndex,
            out int overlapCount,
            out int orderedRun)
        {
            overlapCount = 0;
            orderedRun = 0;
            ProtectedIntervalPairMetricsSnapshot snapshot = GetProtectedIntervalPairMetricsSnapshotCurrent(localChain, expressChain);
            if (snapshot == null
                || localProtectedIntervalIndex < 0
                || localProtectedIntervalIndex >= snapshot.LocalIntervalCount
                || expressProtectedIntervalIndex < 0
                || expressProtectedIntervalIndex >= snapshot.ExpressIntervalCount
                || snapshot.Metrics == null
                || snapshot.Metrics.Length != snapshot.LocalIntervalCount * snapshot.ExpressIntervalCount)
            {
                return false;
            }

            ProtectedIntervalPairMetrics metrics = snapshot.Metrics[(localProtectedIntervalIndex * snapshot.ExpressIntervalCount) + expressProtectedIntervalIndex];
            overlapCount = metrics.OverlapCount;
            orderedRun = metrics.OrderedRun;
            return true;
        }

        private bool IsProtectedIntervalPairStaticallySameDirection(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            int expressCurrentAtomIndex)
        {
            return TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                expressCurrentAtomIndex,
                out _);
        }

        private bool TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            int expressCurrentAtomIndex,
            out GlobalSharedTrunkSegment segment)
        {
            segment = default;
            GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
            if (snapshot == null || snapshot.Segments.Count == 0)
                return false;
            TryGetExpressCurrentForwardPhaseWindow(expressChain, expressCurrentAtomIndex, out int expressPhaseEndAtomExclusive);

            bool hasAnchor = TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
            int localAnchorMaxStartAtomIndex = hasAnchor
                ? stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS
                : int.MaxValue;
            int firstForwardSceneDistance = int.MaxValue;
            bool foundForwardSceneSegment = false;
            for (int i = 0; i < snapshot.Segments.Count; i++)
            {
                GlobalSharedTrunkSegment candidate = snapshot.Segments[i];
                if (candidate.HasMirroredContext)
                    continue;

                int candidateLocalStart = math.max(candidate.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
                int candidateLocalEndExclusive = math.min(candidate.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                int localOverlap = CountAtomIntervalOverlap(
                    candidateLocalStart,
                    candidateLocalEndExclusive,
                    localProtectedInterval.StartAtomIndex,
                    localProtectedInterval.EndAtomIndexExclusive);
                if (localOverlap <= 0)
                    continue;
                if (candidateLocalEndExclusive <= candidateLocalStart)
                    continue;
                if (candidateLocalStart > localAnchorMaxStartAtomIndex)
                    continue;

                int expressOverlap = CountAtomIntervalOverlap(
                    candidate.ExpressCorridorStartAtomIndex,
                    candidate.ExpressCorridorEndAtomIndexExclusive,
                    expressProtectedInterval.StartAtomIndex,
                    expressProtectedInterval.EndAtomIndexExclusive);
                if (expressOverlap <= 0)
                    continue;
                int candidateExpressStart = math.max(candidate.ExpressCorridorStartAtomIndex, expressProtectedInterval.StartAtomIndex);
                int candidateExpressEndExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, expressProtectedInterval.EndAtomIndexExclusive);
                candidateExpressEndExclusive = math.min(candidateExpressEndExclusive, expressPhaseEndAtomExclusive);
                if (candidateExpressEndExclusive <= candidateExpressStart)
                    continue;
                if (expressCurrentAtomIndex >= candidateExpressEndExclusive)
                    continue;

                int expressApproachDistance = math.max(0, candidateExpressStart - expressCurrentAtomIndex);
                if (expressApproachDistance < firstForwardSceneDistance)
                {
                    firstForwardSceneDistance = expressApproachDistance;
                    foundForwardSceneSegment = true;
                }
            }

            if (!foundForwardSceneSegment)
                return false;

            int bestAnchorDistance = int.MaxValue;
            int bestOrderedRun = int.MinValue;
            int bestPhysicalOverlap = int.MinValue;
            int bestLocalOverlap = int.MinValue;
            int bestExpressOverlap = int.MinValue;
            int bestExpressApproachDistance = int.MaxValue;
            int bestTraversalRank = int.MinValue;
            bool found = false;
            for (int i = 0; i < snapshot.Segments.Count; i++)
            {
                GlobalSharedTrunkSegment candidate = snapshot.Segments[i];
                if (candidate.HasMirroredContext)
                    continue;
                if (candidate.TraversalRelation != SharedTraversalRelation.SameDirection)
                    continue;

                int candidateLocalStart = math.max(candidate.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
                int candidateLocalEndExclusive = math.min(candidate.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                int localOverlap = CountAtomIntervalOverlap(
                    candidateLocalStart,
                    candidateLocalEndExclusive,
                    localProtectedInterval.StartAtomIndex,
                    localProtectedInterval.EndAtomIndexExclusive);
                if (localOverlap <= 0)
                    continue;
                if (candidateLocalEndExclusive <= candidateLocalStart)
                    continue;
                if (candidateLocalStart > localAnchorMaxStartAtomIndex)
                    continue;

                int expressOverlap = CountAtomIntervalOverlap(
                    candidate.ExpressCorridorStartAtomIndex,
                    candidate.ExpressCorridorEndAtomIndexExclusive,
                    expressProtectedInterval.StartAtomIndex,
                    expressProtectedInterval.EndAtomIndexExclusive);
                if (expressOverlap <= 0)
                    continue;
                int candidateExpressStart = math.max(candidate.ExpressCorridorStartAtomIndex, expressProtectedInterval.StartAtomIndex);
                int candidateExpressEndExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, expressProtectedInterval.EndAtomIndexExclusive);
                candidateExpressEndExclusive = math.min(candidateExpressEndExclusive, expressPhaseEndAtomExclusive);
                if (candidateExpressEndExclusive <= candidateExpressStart)
                    continue;
                if (expressCurrentAtomIndex >= candidateExpressEndExclusive)
                    continue;

                int expressApproachDistance = math.max(0, candidateExpressStart - expressCurrentAtomIndex);
                if (expressApproachDistance != firstForwardSceneDistance)
                    continue;
                int traversalRank = 2;

                int anchorDistance = candidateLocalStart - localProtectedInterval.StartAtomIndex;
                bool better = !found;
                if (!better && traversalRank != bestTraversalRank)
                    better = traversalRank > bestTraversalRank;
                if (!better && anchorDistance != bestAnchorDistance)
                    better = anchorDistance < bestAnchorDistance;
                if (!better && expressApproachDistance != bestExpressApproachDistance)
                    better = expressApproachDistance < bestExpressApproachDistance;
                if (!better && candidate.OrderedRun != bestOrderedRun)
                    better = candidate.OrderedRun > bestOrderedRun;
                if (!better && candidate.PhysicalOverlap != bestPhysicalOverlap)
                    better = candidate.PhysicalOverlap > bestPhysicalOverlap;
                if (!better && localOverlap != bestLocalOverlap)
                    better = localOverlap > bestLocalOverlap;
                if (!better && expressOverlap != bestExpressOverlap)
                    better = expressOverlap > bestExpressOverlap;
                if (!better)
                    continue;

                bestTraversalRank = traversalRank;
                bestAnchorDistance = anchorDistance;
                bestExpressApproachDistance = expressApproachDistance;
                bestOrderedRun = candidate.OrderedRun;
                bestPhysicalOverlap = candidate.PhysicalOverlap;
                bestLocalOverlap = localOverlap;
                bestExpressOverlap = expressOverlap;
                segment = candidate;
                found = true;
            }

            return found;
        }

        private bool TryFindBestGlobalSharedTrunkSegment(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            TrackModelRuntimePosition expressPosition,
            out GlobalSharedTrunkSegment segment)
        {
            return TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                expressPosition.CurrentAtomIndex,
                out segment);
        }

        private bool TryGetExpressCurrentForwardPhaseWindow(
            LineTrackChain expressChain,
            int expressCurrentAtomIndex,
            out int expressPhaseEndAtomExclusive)
        {
            expressPhaseEndAtomExclusive = int.MaxValue;
            if (expressChain == null)
                return false;

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(expressChain, expressCurrentAtomIndex);
            if (currentControlEdgeIndex < 0 || currentControlEdgeIndex >= expressChain.ControlEdges.Count)
                return false;

            expressPhaseEndAtomExclusive = expressChain.ControlEdges[currentControlEdgeIndex].EndAtomIndexExclusive;
            return true;
        }

        private bool TryGetActiveConflictCorridorCurrent(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            TrackModelRuntimePosition expressPosition,
            bool hasPreselectedTrunkSegment,
            GlobalSharedTrunkSegment preselectedTrunkSegment,
            out ConflictCorridor localCorridor,
            out ConflictCorridor expressCorridor,
            out GlobalSharedTrunkSegment trunkSegment)
        {
            m_BypassPerfProbeActiveCorridorCalls++;
            localCorridor = default;
            expressCorridor = default;
            trunkSegment = default;
            if (localChain == null || expressChain == null)
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_ActiveConflictCorridorSnapshotFrame != nowFrame)
            {
                m_ActiveConflictCorridorSnapshots.Clear();
                m_ActiveConflictCorridorSnapshotFrame = nowFrame;
            }

            var key = new ActiveConflictCorridorCacheKey(
                localChain.LineEntity,
                expressChain.LineEntity,
                currentBypassBuilding,
                localProtectedInterval.StartControlPointIndex,
                localProtectedInterval.EndControlPointIndex,
                expressProtectedInterval.StartControlPointIndex,
                expressProtectedInterval.EndControlPointIndex,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive,
                expressProtectedInterval.StartAtomIndex,
                expressProtectedInterval.EndAtomIndexExclusive,
                expressPosition.CurrentAtomIndex);

            if (m_ActiveConflictCorridorSnapshots.TryGetValue(key, out ActiveConflictCorridorSnapshot snapshot)
                && snapshot.Frame == nowFrame
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                if (!snapshot.Available)
                    return false;

                localCorridor = snapshot.LocalCorridor;
                expressCorridor = snapshot.ExpressCorridor;
                trunkSegment = snapshot.TrunkSegment;
                return true;
            }

            if (hasPreselectedTrunkSegment)
            {
                trunkSegment = preselectedTrunkSegment;
            }
            else if (!TryFindBestGlobalSharedTrunkSegment(localChain, localProtectedInterval, currentBypassBuilding, expressChain, expressProtectedInterval, expressPosition, out trunkSegment))
            {
                m_ActiveConflictCorridorSnapshots[key] = new ActiveConflictCorridorSnapshot(
                    nowFrame,
                    m_SharedTrackIndexVersion,
                    localChain.Signature,
                    expressChain.Signature,
                    false,
                    default,
                    default,
                    default);
                return false;
            }

            int localStart = math.max(trunkSegment.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
            int localEndExclusive = math.min(trunkSegment.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
            if (currentBypassBuilding != Entity.Null)
            {
                int prefixGapAtoms = math.max(0, localStart - localProtectedInterval.StartAtomIndex);
                if (prefixGapAtoms <= MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
                    localStart = localProtectedInterval.StartAtomIndex;
            }

            int expressStart = math.max(trunkSegment.ExpressCorridorStartAtomIndex, expressProtectedInterval.StartAtomIndex);
            int expressEndExclusive = math.min(trunkSegment.ExpressCorridorEndAtomIndexExclusive, expressProtectedInterval.EndAtomIndexExclusive);
            if (localEndExclusive <= localStart || expressEndExclusive <= expressStart)
            {
                m_ActiveConflictCorridorSnapshots[key] = new ActiveConflictCorridorSnapshot(
                    nowFrame,
                    m_SharedTrackIndexVersion,
                    localChain.Signature,
                    expressChain.Signature,
                    false,
                    default,
                    default,
                    default);
                return false;
            }

            int localAnchorStart = math.clamp(trunkSegment.LocalAnchorStartAtomIndex, localStart, localEndExclusive - 1);
            int localAnchorEndExclusive = math.clamp(trunkSegment.LocalAnchorEndAtomIndexExclusive, localAnchorStart + 1, localEndExclusive);
            int expressAnchorStart = math.clamp(trunkSegment.ExpressAnchorStartAtomIndex, expressStart, expressEndExclusive - 1);
            int expressAnchorEndExclusive = math.clamp(trunkSegment.ExpressAnchorEndAtomIndexExclusive, expressAnchorStart + 1, expressEndExclusive);
            int localProtectedIntervalIndex = ResolveProtectedIntervalIndex(localChain, localProtectedInterval);
            int expressProtectedIntervalIndex = ResolveProtectedIntervalIndex(expressChain, expressProtectedInterval);

            localCorridor = new ConflictCorridor(
                localProtectedIntervalIndex,
                localStart,
                localEndExclusive,
                localAnchorStart,
                localAnchorEndExclusive,
                trunkSegment.LocalSharedSliceCount,
                trunkSegment.LocalBridgedGapAtoms);
            expressCorridor = new ConflictCorridor(
                expressProtectedIntervalIndex,
                expressStart,
                expressEndExclusive,
                expressAnchorStart,
                expressAnchorEndExclusive,
                trunkSegment.ExpressSharedSliceCount,
                trunkSegment.ExpressBridgedGapAtoms);

            m_ActiveConflictCorridorSnapshots[key] = new ActiveConflictCorridorSnapshot(
                nowFrame,
                m_SharedTrackIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                true,
                localCorridor,
                expressCorridor,
                trunkSegment);
            return true;
        }

        private bool TryGetActiveConflictCorridorCurrent(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            TrackModelRuntimePosition expressPosition,
            out ConflictCorridor localCorridor,
            out ConflictCorridor expressCorridor,
            out GlobalSharedTrunkSegment trunkSegment)
        {
            return TryGetActiveConflictCorridorCurrent(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                expressPosition,
                false,
                default,
                out localCorridor,
                out expressCorridor,
                out trunkSegment);
        }

        private static BypassProtectedInterval BuildAtomWindowInterval(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return default;

            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);

            int startControlEdgeIndex = -1;
            int endControlEdgeIndexInclusive = -1;
            float baseFrames = 0f;
            for (int controlEdgeIndex = 0; controlEdgeIndex < chain.ControlEdges.Count; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                int overlapStart = math.max(edge.StartAtomIndex, startAtomIndex);
                int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, endAtomIndexExclusive);
                if (overlapEndExclusive <= overlapStart)
                    continue;

                if (startControlEdgeIndex < 0)
                    startControlEdgeIndex = controlEdgeIndex;
                endControlEdgeIndexInclusive = controlEdgeIndex;

                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                int overlapAtomLength = overlapEndExclusive - overlapStart;
                baseFrames += edge.BaseFrames * (overlapAtomLength / (float)edgeAtomLength);
            }

            if (startControlEdgeIndex < 0 && chain.ControlEdges.Count > 0)
            {
                if (endAtomIndexExclusive <= chain.ControlEdges[0].StartAtomIndex)
                {
                    startControlEdgeIndex = 0;
                    endControlEdgeIndexInclusive = 0;
                }
                else
                {
                    int lastControlEdgeIndex = chain.ControlEdges.Count - 1;
                    ControlEdge lastEdge = chain.ControlEdges[lastControlEdgeIndex];
                    if (startAtomIndex >= lastEdge.EndAtomIndexExclusive)
                    {
                        startControlEdgeIndex = lastControlEdgeIndex;
                        endControlEdgeIndexInclusive = lastControlEdgeIndex;
                    }
                }
            }

            if (!(baseFrames > 0f))
            {
                float averageFramesPerAtom = EstimateAverageControlEdgeFramesPerAtom(chain);
                if (averageFramesPerAtom > 0f)
                    baseFrames = (endAtomIndexExclusive - startAtomIndex) * averageFramesPerAtom;
            }

            return new BypassProtectedInterval(
                -1,
                -1,
                startControlEdgeIndex,
                endControlEdgeIndexInclusive,
                startAtomIndex,
                endAtomIndexExclusive,
                baseFrames);
        }

        private PhysicalSharedWindowMatch FindBestPhysicalSharedWindow(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return default;

            if (!localChain.SharedRunsByOtherLine.TryGetValue(expressChain.LineEntity, out List<SharedTrackRun> localSharedRuns)
                || localSharedRuns == null
                || localSharedRuns.Count == 0
                || !expressChain.SharedRunsByOtherLine.TryGetValue(localChain.LineEntity, out List<SharedTrackRun> expressSharedRuns)
                || expressSharedRuns == null
                || expressSharedRuns.Count == 0)
            {
                return default;
            }

            int bestOverlap = 0;
            int bestOrderedRun = 0;
            bool ambiguous = false;
            BypassProtectedInterval bestLocalWindow = default;
            BypassProtectedInterval bestExpressWindow = default;
            bool hasAnchor = TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
            int localAnchorMaxStartAtomIndex = hasAnchor
                ? stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS
                : int.MaxValue;

            for (int localIndex = 0; localIndex < localSharedRuns.Count;)
            {
                SharedTrackRun localCandidate = localSharedRuns[localIndex];
                int clippedLocalStart = math.max(localProtectedInterval.StartAtomIndex, localCandidate.StartAtomIndex);
                int clippedLocalEndExclusive = math.min(localProtectedInterval.EndAtomIndexExclusive, localCandidate.EndAtomIndexExclusive);
                if (clippedLocalEndExclusive <= clippedLocalStart)
                {
                    localIndex++;
                    continue;
                }

                if (clippedLocalStart > localAnchorMaxStartAtomIndex)
                {
                    localIndex++;
                    continue;
                }

                int mergedLocalStart = clippedLocalStart;
                int mergedLocalEndExclusive = clippedLocalEndExclusive;
                int nextLocalIndex = localIndex + 1;
                while (nextLocalIndex < localSharedRuns.Count)
                {
                    SharedTrackRun nextCandidate = localSharedRuns[nextLocalIndex];
                    int clippedNextStart = math.max(localProtectedInterval.StartAtomIndex, nextCandidate.StartAtomIndex);
                    int clippedNextEndExclusive = math.min(localProtectedInterval.EndAtomIndexExclusive, nextCandidate.EndAtomIndexExclusive);
                    if (clippedNextEndExclusive <= clippedNextStart)
                    {
                        nextLocalIndex++;
                        continue;
                    }

                    if (clippedNextStart > localAnchorMaxStartAtomIndex)
                        break;

                    if (clippedNextStart > mergedLocalEndExclusive)
                        break;

                    mergedLocalEndExclusive = math.max(mergedLocalEndExclusive, clippedNextEndExclusive);
                    nextLocalIndex++;
                }

                BypassProtectedInterval localWindow = BuildAtomWindowInterval(localChain, mergedLocalStart, mergedLocalEndExclusive);
                if (localWindow.EndAtomIndexExclusive <= localWindow.StartAtomIndex)
                {
                    localIndex = nextLocalIndex;
                    continue;
                }

                for (int expressIndex = 0; expressIndex < expressSharedRuns.Count; expressIndex++)
                {
                    SharedTrackRun expressRun = expressSharedRuns[expressIndex];
                    BypassProtectedInterval expressWindow = BuildAtomWindowInterval(expressChain, expressRun.StartAtomIndex, expressRun.EndAtomIndexExclusive);
                    if (expressWindow.EndAtomIndexExclusive <= expressWindow.StartAtomIndex)
                        continue;

                    int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                    if (overlapCount <= 0)
                        continue;

                    int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localWindow, expressChain, expressWindow);
                    if (orderedRun <= 0)
                        continue;

                    if (orderedRun > bestOrderedRun
                        || (orderedRun == bestOrderedRun && overlapCount > bestOverlap))
                    {
                        bestOverlap = overlapCount;
                        bestOrderedRun = orderedRun;
                        bestLocalWindow = localWindow;
                        bestExpressWindow = expressWindow;
                        ambiguous = false;
                        continue;
                    }

                    if (orderedRun == bestOrderedRun && overlapCount == bestOverlap)
                        ambiguous = true;
                }

                localIndex = nextLocalIndex;
            }

            if (bestOverlap <= 0 || bestOrderedRun <= 0)
                return default;

            return new PhysicalSharedWindowMatch(true, ambiguous, bestLocalWindow, bestExpressWindow, bestOverlap, bestOrderedRun);
        }

        private PhysicalSharedWindowMatch GetPhysicalSharedWindowMatchCurrentFrame(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return default;

            EnsureSharedTrackIndexCurrent();
            RefreshSharedRuns(localChain);
            RefreshSharedRuns(expressChain);

            var key = new SharedWindowMatchCacheKey(
                localChain.LineEntity,
                expressChain.LineEntity,
                currentBypassBuilding,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive);
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_SharedWindowMatchSnapshots.TryGetValue(key, out SharedWindowMatchSnapshot snapshot)
                && snapshot.Frame == nowFrame
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot.Match;
            }

            PhysicalSharedWindowMatch match = FindBestPhysicalSharedWindow(localChain, localProtectedInterval, currentBypassBuilding, expressChain);
            m_SharedWindowMatchSnapshots[key] = new SharedWindowMatchSnapshot(
                nowFrame,
                m_SharedTrackIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                match);
            return match;
        }

        private bool IsExpressApproachingCurrentBypassStation(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            TrackModelRuntimePosition expressPosition,
            float expressCoordinate,
            bool includeExpress)
        {
            if (localChain == null
                || currentBypassBuilding == Entity.Null
                || !includeExpress
                || expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After)
            {
                return false;
            }

            if (!TryGetForwardStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return false;

            return expressCoordinate <= stationExitCoordinate;
        }

        private static int CountSharedPhysicalOverlap(
            LineTrackChain sourceChain,
            int sourceStartAtomIndex,
            int sourceEndAtomIndexExclusive,
            LineTrackChain candidateChain,
            int candidateStartAtomIndex,
            int candidateEndAtomIndexExclusive)
        {
            if (sourceChain == null
                || candidateChain == null
                || sourceEndAtomIndexExclusive <= sourceStartAtomIndex
                || candidateEndAtomIndexExclusive <= candidateStartAtomIndex)
            {
                return 0;
            }

            HashSet<Entity> sourceKeys = new HashSet<Entity>();
            for (int atomIndex = math.max(0, sourceStartAtomIndex); atomIndex < math.min(sourceEndAtomIndexExclusive, sourceChain.TrackAtoms.Count); atomIndex++)
                sourceKeys.Add(sourceChain.TrackAtoms[atomIndex].Key.PhysicalLaneKey);

            int overlapCount = 0;
            HashSet<Entity> matchedKeys = new HashSet<Entity>();
            for (int atomIndex = math.max(0, candidateStartAtomIndex); atomIndex < math.min(candidateEndAtomIndexExclusive, candidateChain.TrackAtoms.Count); atomIndex++)
            {
                Entity physicalLaneKey = candidateChain.TrackAtoms[atomIndex].Key.PhysicalLaneKey;
                if (sourceKeys.Contains(physicalLaneKey) && matchedKeys.Add(physicalLaneKey))
                    overlapCount++;
            }

            return overlapCount;
        }

        private float EstimateRuntimeFramesToAtomBoundary(
            LineTrackChain chain,
            TrackModelRuntimePosition runtimePosition,
            int targetAtomIndexExclusive)
        {
            if (chain == null || chain.ControlEdges.Count == 0 || targetAtomIndexExclusive <= 0)
                return float.MaxValue;

            if (runtimePosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After
                || runtimePosition.CurrentAtomIndex >= targetAtomIndexExclusive)
            {
                return 0f;
            }

            int currentControlEdgeIndex = runtimePosition.CurrentControlEdgeIndex >= 0
                ? runtimePosition.CurrentControlEdgeIndex
                : ResolveControlEdgeIndexForAtom(chain, runtimePosition.CurrentAtomIndex);
            int fromAtomIndex = math.clamp(runtimePosition.CurrentAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int toAtomIndexExclusive = math.clamp(targetAtomIndexExclusive, fromAtomIndex + 1, chain.TrackAtoms.Count);
            float averageFramesPerAtom = EstimateAverageControlEdgeFramesPerAtom(chain);
            if (currentControlEdgeIndex < 0 || currentControlEdgeIndex >= chain.ControlEdges.Count)
            {
                if (!(averageFramesPerAtom > 0f))
                    return float.MaxValue;

                float rawAtomDistance = (toAtomIndexExclusive - fromAtomIndex) - math.saturate(runtimePosition.AtomPosition01);
                return math.max(0f, rawAtomDistance * averageFramesPerAtom);
            }

            float frames = EstimateFramesBetweenAtoms(chain, currentControlEdgeIndex, chain.ControlEdges.Count - 1, fromAtomIndex, toAtomIndexExclusive);
            ControlEdge currentEdge = chain.ControlEdges[currentControlEdgeIndex];
            int edgeAtomLength = math.max(1, currentEdge.EndAtomIndexExclusive - currentEdge.StartAtomIndex);
            float consumedFrames = (currentEdge.BaseFrames / edgeAtomLength) * math.saturate(runtimePosition.AtomPosition01);
            frames = math.max(0f, frames - consumedFrames);

            ControlEdge lastEdge = chain.ControlEdges[chain.ControlEdges.Count - 1];
            if (averageFramesPerAtom > 0f && toAtomIndexExclusive > lastEdge.EndAtomIndexExclusive)
            {
                int uncoveredStartAtomIndex = math.max(fromAtomIndex, lastEdge.EndAtomIndexExclusive);
                if (toAtomIndexExclusive > uncoveredStartAtomIndex)
                    frames += (toAtomIndexExclusive - uncoveredStartAtomIndex) * averageFramesPerAtom;
            }

            return frames;
        }

        private static string FormatEtaFrames(float frames)
        {
            if (frames == float.MaxValue)
                return "?";

            return (frames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "m";
        }

        private bool TryGetBypassProtectedSharedContext(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out int protectedSharedCount,
            out bool hasMirroredContext)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            protectedSharedCount = 0;
            hasMirroredContext = false;

            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                return false;

            EnsureTrackChainBypassPipelineReady(chain);

            if (!TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out protectedInterval))
                return false;

            protectedSharedCount = CountProtectedSharedIntervals(chain, protectedIntervalIndex, out hasMirroredContext);
            return true;
        }

        private bool TryBuildBypassTrackModelShadowSummary(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out string summary)
        {
            summary = string.Empty;
            if (!TryEvaluateBypassTrackModelShadow(line, waypoints, currentWaypointIndex, out _, out _, out summary))
                return false;
            return true;
        }

        private void LogSharedWindowAuditOnce(
            Entity localVehicle,
            Entity localLine,
            int protectedIntervalIndex,
            string auditSummary)
        {
            if (!ENABLE_TRACKMODEL_DIAGNOSTIC_LOGS)
                return;

            if (localVehicle == Entity.Null || string.IsNullOrWhiteSpace(auditSummary))
                return;

            string key = localLine.Index + "|" + protectedIntervalIndex + "|" + auditSummary;
            if (m_SharedWindowAuditSummaryLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_SharedWindowAuditSummaryLogCache[localVehicle] = key;
            log.Info("[SharedWindowAudit] localVehicle=" + localVehicle.Index
                + " localLine=" + localLine.Index
                + " protectedInterval=" + protectedIntervalIndex
                + " " + auditSummary);
        }

        private string BuildSharedWindowNoMatchDebugSummary(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return string.Empty;

            StringBuilder debug = new StringBuilder();
            debug.Append(" localP=").Append(localProtectedInterval.StartAtomIndex)
                .Append("..").Append(localProtectedInterval.EndAtomIndexExclusive);

            if (TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex))
            {
                debug.Append(" exit=").Append(stationExitAtomIndex)
                    .Append(" gate<=").Append(stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS);
            }
            else
            {
                debug.Append(" exit=none");
            }

            if (localChain.SharedRunsByOtherLine.TryGetValue(expressChain.LineEntity, out List<SharedTrackRun> localSharedRuns)
                && localSharedRuns != null
                && localSharedRuns.Count > 0)
            {
                debug.Append(" localRuns=");
                int written = 0;
                for (int i = 0; i < localSharedRuns.Count && written < 4; i++)
                {
                    SharedTrackRun run = localSharedRuns[i];
                    int start = math.max(run.StartAtomIndex, localProtectedInterval.StartAtomIndex);
                    int endExclusive = math.min(run.EndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                    if (endExclusive <= start)
                        continue;

                    if (written > 0)
                        debug.Append(",");

                    debug.Append(start).Append("..").Append(endExclusive);
                    written++;
                }

                if (written == 0)
                    debug.Append("none-in-local");
            }
            else
            {
                debug.Append(" localRuns=none");
            }

            if (expressChain.SharedRunsByOtherLine.TryGetValue(localChain.LineEntity, out List<SharedTrackRun> expressSharedRuns)
                && expressSharedRuns != null
                && expressSharedRuns.Count > 0)
            {
                debug.Append(" expressRuns=");
                int written = 0;
                for (int i = 0; i < expressSharedRuns.Count && written < 4; i++)
                {
                    SharedTrackRun run = expressSharedRuns[i];
                    if (written > 0)
                        debug.Append(",");

                    debug.Append(run.StartAtomIndex).Append("..").Append(run.EndAtomIndexExclusive);
                    written++;
                }
            }
            else
            {
                debug.Append(" expressRuns=none");
            }

            GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
            if (snapshot == null || snapshot.Segments.Count == 0)
            {
                debug.Append(" pairSegs=none");
                return debug.Append(BuildSharedWindowNoMatchPairAttemptSummary(
                    localChain,
                    localProtectedInterval,
                    expressChain,
                    localSharedRuns,
                    expressSharedRuns)).ToString();
            }

            debug.Append(" pairSegs=");
            int segmentWritten = 0;
            for (int i = 0; i < snapshot.Segments.Count && segmentWritten < 4; i++)
            {
                GlobalSharedTrunkSegment segment = snapshot.Segments[i];
                int localStart = math.max(segment.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
                int localEndExclusive = math.min(segment.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                if (localEndExclusive <= localStart)
                    continue;

                if (segmentWritten > 0)
                    debug.Append(",");

                debug.Append("L").Append(localStart).Append("..").Append(localEndExclusive)
                    .Append("/E").Append(segment.ExpressCorridorStartAtomIndex).Append("..").Append(segment.ExpressCorridorEndAtomIndexExclusive)
                    .Append("/")
                    .Append(segment.TraversalRelation == SharedTraversalRelation.SameDirection
                        ? "same"
                        : segment.TraversalRelation == SharedTraversalRelation.OppositeDirection
                            ? "opp"
                            : "unk");
                segmentWritten++;
            }

            if (segmentWritten == 0)
                debug.Append("none-in-local");

            return debug.Append(BuildSharedWindowNoMatchPairAttemptSummary(
                localChain,
                localProtectedInterval,
                expressChain,
                localSharedRuns,
                expressSharedRuns)).ToString();
        }

        private string BuildSharedWindowNoMatchPairAttemptSummary(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            LineTrackChain expressChain,
            List<SharedTrackRun> localSharedRuns,
            List<SharedTrackRun> expressSharedRuns)
        {
            if (localChain == null
                || expressChain == null
                || localSharedRuns == null
                || localSharedRuns.Count == 0
                || expressSharedRuns == null
                || expressSharedRuns.Count == 0)
            {
                return string.Empty;
            }

            var localBands = new List<SharedRunBand>();
            var expressBands = new List<SharedRunBand>();
            BuildSharedRunBands(localChain, localSharedRuns, localBands);
            BuildSharedRunBands(expressChain, expressSharedRuns, expressBands);

            StringBuilder debug = new StringBuilder();
            debug.Append(" pairTry=");
            int attemptCount = 0;
            for (int localIndex = 0; localIndex < localBands.Count && attemptCount < 4; localIndex++)
            {
                SharedRunBand localBand = localBands[localIndex];
                int clippedLocalStart = math.max(localBand.StartAtomIndex, localProtectedInterval.StartAtomIndex);
                int clippedLocalEndExclusive = math.min(localBand.EndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                if (clippedLocalEndExclusive <= clippedLocalStart)
                    continue;

                BypassProtectedInterval localWindow = BuildAtomWindowInterval(localChain, clippedLocalStart, clippedLocalEndExclusive);
                if (localWindow.EndAtomIndexExclusive <= localWindow.StartAtomIndex)
                    continue;

                for (int expressIndex = 0; expressIndex < expressBands.Count && attemptCount < 4; expressIndex++)
                {
                    SharedRunBand expressBand = expressBands[expressIndex];
                    BypassProtectedInterval expressWindow = BuildAtomWindowInterval(expressChain, expressBand.StartAtomIndex, expressBand.EndAtomIndexExclusive);
                    if (expressWindow.EndAtomIndexExclusive <= expressWindow.StartAtomIndex)
                        continue;

                    if (attemptCount > 0)
                        debug.Append(",");

                    debug.Append("L").Append(clippedLocalStart).Append("..").Append(clippedLocalEndExclusive)
                        .Append("/E").Append(expressBand.StartAtomIndex).Append("..").Append(expressBand.EndAtomIndexExclusive);

                    int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                    if (overlapCount <= 0)
                    {
                        debug.Append(":overlap0");
                        attemptCount++;
                        continue;
                    }

                    int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localWindow, expressChain, expressWindow);
                    SharedTraversalRelation relation = ResolveSharedTraversalRelation(localChain, localBand, expressChain, expressBand);
                    if (orderedRun <= 0)
                    {
                        debug.Append(":run0/")
                            .Append(relation == SharedTraversalRelation.SameDirection
                                ? "same"
                                : relation == SharedTraversalRelation.OppositeDirection
                                    ? "opp"
                                    : "unk");
                        attemptCount++;
                        continue;
                    }

                    debug.Append(":ok")
                        .Append("/o=").Append(overlapCount)
                        .Append("/r=").Append(orderedRun)
                        .Append("/")
                        .Append(relation == SharedTraversalRelation.SameDirection
                            ? "same"
                            : relation == SharedTraversalRelation.OppositeDirection
                                ? "opp"
                                : "unk");
                    attemptCount++;
                }
            }

            if (attemptCount == 0)
                debug.Append("none");

            return debug.ToString();
        }

        private string BuildSharedWindowAuditSummary(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            BypassProtectedInterval protectedInterval)
        {
            if (!TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                return "local=proj-fail";
            if (localPosition.Confidence < 0.6f)
                return "local=low-conf(" + localPosition.Confidence.ToString("0.00") + ")";

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out _))
                return "local=protected-interval-missing";

            StringBuilder sharedWindowAudit = new StringBuilder();
            sharedWindowAudit.Append("local=rel=").Append(localPosition.RelativeToProtectedInterval)
                .Append(" conf=")
                .Append(localPosition.Confidence >= 0.9f ? "high" : "ok");
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(localChain, protectedInterval, currentBypassBuilding);
            float intervalDisplayLength = GetProtectedIntervalDisplayLength(protectedInterval);

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity expressLine = entry.Value.LineEntity;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                    || !routeVehicleBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteVehicle> routeVehicles))
                {
                    continue;
                }

                if (!TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
                {
                    sharedWindowAudit.Append(" | line=").Append(expressLine.Index).Append(" chain-missing");
                    continue;
                }

                EnsureTrackChainBypassPipelineReady(expressChain);
                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, currentBypassBuilding, expressChain);
                if (!sharedWindowMatch.Found)
                {
                    int expressSharedRunCountForLocal = expressChain.SharedRunsByOtherLine.TryGetValue(localLine, out List<SharedTrackRun> expressRunsForLocal)
                        && expressRunsForLocal != null
                        ? expressRunsForLocal.Count
                        : 0;
                    sharedWindowAudit.Append(" | line=").Append(expressLine.Index)
                        .Append(" match=none runs=").Append(expressSharedRunCountForLocal)
                        .Append(BuildSharedWindowNoMatchDebugSummary(
                            localChain,
                            protectedInterval,
                            currentBypassBuilding,
                            expressChain));
                    continue;
                }

                StringBuilder lineAudit = new StringBuilder();
                lineAudit.Append("line=").Append(expressLine.Index);

                if (sharedWindowMatch.Ambiguous)
                {
                    lineAudit.Append(" match=ambiguous overlap=").Append(sharedWindowMatch.OverlapCount)
                        .Append(" run=").Append(sharedWindowMatch.OrderedRun);
                    sharedWindowAudit.Append(" | ").Append(lineAudit);
                    continue;
                }

                lineAudit.Append(" match=found overlap=").Append(sharedWindowMatch.OverlapCount)
                    .Append(" run=").Append(sharedWindowMatch.OrderedRun)
                    .Append(" window=").Append(sharedWindowMatch.ExpressSharedWindow.StartAtomIndex)
                    .Append("..").Append(sharedWindowMatch.ExpressSharedWindow.EndAtomIndexExclusive);

                bool sawRunningVehicle = false;
                for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = routeVehicles[rvIndex].m_Vehicle;
                    TryBuildProtectedIntervalConflictCandidate(
                        localVehicle,
                        localLine,
                        localChain,
                        protectedInterval,
                        currentBypassBuilding,
                        departureReleaseCoordinate,
                        intervalDisplayLength,
                        expressVehicle,
                        expressLine,
                        expressWaypoints,
                        expressChain,
                        lineAudit,
                        ref sawRunningVehicle);
                }

                if (!sawRunningVehicle)
                    lineAudit.Append(" vehicles=none-running");

                sharedWindowAudit.Append(" | ").Append(lineAudit);
            }

            return sharedWindowAudit.ToString();
        }

        private static bool ShouldIncludeBypassSequenceForShadowLog(bool hasDecision, BypassTrackModelShadowDecision decision)
        {
            if (!hasDecision)
                return false;
            if (!decision.ShouldYield || decision.BlockerVehicle == Entity.Null)
                return true;
            if (decision.ReasonCode == "shared-window-match-ambiguous"
                || decision.ReasonCode == "local-runtime-position-unknown"
                || decision.ReasonCode == "local-runtime-position-low-confidence")
            {
                return true;
            }

            return decision.UsedFallbackResolution;
        }

        private static bool ShouldIncludeBypassSequenceForCompareLog(string alignment, BypassTrackModelShadowDecision decision)
        {
            return alignment != "match" || ShouldIncludeBypassSequenceForShadowLog(true, decision);
        }

        private bool CanClearBypassYieldAfterStationExit(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || !TryGetBypassWaypointContext(localWaypoints, currentWaypointIndex, out Entity currentBypassBuilding, out _, out _)
                || currentBypassBuilding == Entity.Null)
            {
                return true;
            }

            if (!TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
                return false;

            EnsureTrackChainBypassPipelineReady(localChain);

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out BypassProtectedInterval protectedInterval))
                return false;

            if (!TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                return false;

            if (localPosition.Confidence < 0.6f)
                return false;

            if (!TryGetForwardStationExitCoordinate(localChain, protectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return false;

            float localCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(localPosition, protectedInterval, includeApproachers: true, out bool includeLocal);
            if (!includeLocal)
                return false;

            return localCoordinate > stationExitCoordinate + LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS;
        }

        private bool IsVehicleWithinCurrentBypassStation(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex)
        {
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || currentWaypointIndex < 0
                || !TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return false;
            }

            int liveWaypointIndex = ComputeWpIndex(localVehicle, localWaypoints);
            if (liveWaypointIndex == currentWaypointIndex)
                return true;

            if (!TryGetBypassWaypointContext(localWaypoints, currentWaypointIndex, out Entity currentBypassBuilding, out _, out _)
                || currentBypassBuilding == Entity.Null)
            {
                return false;
            }

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out BypassProtectedInterval protectedInterval))
                return false;

            if (!TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition)
                || localPosition.Confidence < 0.6f)
            {
                return false;
            }

            if (!TryGetForwardStationExitCoordinate(localChain, protectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return false;

            float localCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(localPosition, protectedInterval, includeApproachers: true, out bool includeLocal);
            if (!includeLocal)
                return false;

            return localCoordinate <= stationExitCoordinate;
        }

        private bool IsVehicleWithinBypassStationPhysicalContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity bypassBuilding)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || bypassBuilding == Entity.Null
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || !TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out _, out BypassProtectedInterval protectedInterval)
                || !TryProjectTrackModelRuntimePosition(vehicle, line, waypoints, protectedInterval, out TrackModelRuntimePosition runtimePosition)
                || runtimePosition.Confidence < 0.6f
                || runtimePosition.CurrentAtomIndex < protectedInterval.StartAtomIndex
                || runtimePosition.CurrentAtomIndex >= protectedInterval.EndAtomIndexExclusive
                || runtimePosition.CurrentAtomIndex < 0
                || runtimePosition.CurrentAtomIndex >= chain.TrackAtoms.Count)
            {
                return false;
            }

            Entity atomBuilding = ResolvePassingStationBuilding(chain.TrackAtoms[runtimePosition.CurrentAtomIndex].SourceTarget);
            return atomBuilding == bypassBuilding;
        }

        private void LogBypassTrackModelShadowOnce(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding)
        {
            if (localVehicle == Entity.Null || localLine == Entity.Null)
                return;

            EnsureBypassTrackModelShadowSnapshotCurrent(
                localVehicle,
                localLine,
                localWaypoints,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding,
                out BypassTrackModelShadowDecision shadowDecision);

            bool hasDecision = shadowDecision.Available;
            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = (hasDecision ? (shadowDecision.ShouldYield ? "Y" : "N") : "U")
                + "|"
                + (hasDecision ? shadowDecision.ReasonCode : "decision-unavailable")
                + "|"
                + (hasDecision ? shadowDecision.ProtectedIntervalIndex : -1)
                + "|"
                + currentBypassBuilding.Index
                + "|"
                + nextBypassBuilding.Index
                + "|"
                + (hasDecision ? shadowDecision.BlockerVehicle.Index : -1);
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_BypassTrackModelShadowThrottleCache,
                    m_BypassTrackModelShadowLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            string summary = "trackModel[unavailable]";
            string shadowRisk = "unavailable";
            if (TryEvaluateBypassTrackModelShadow(localLine, localWaypoints, currentWaypointIndex, out _, out string risk, out string builtSummary))
            {
                summary = builtSummary;
                shadowRisk = risk;
            }

            string localPositionText = "pos[unknown]";
            string blockerPositionText = string.Empty;
            if (TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain)
                && TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out BypassProtectedInterval protectedInterval))
            {
                int localProtectedIntervalIndex = FindProtectedIntervalIndex(localChain, protectedInterval);
                if (TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                    localPositionText = FormatRuntimePosition(localPosition);

                if (shadowDecision.BlockerVehicle != Entity.Null
                    && ResolveVehicleLine(shadowDecision.BlockerVehicle) is Entity blockerLine
                    && blockerLine != Entity.Null
                    && GetBufferLookup<RouteWaypoint>(true).TryGetBuffer(blockerLine, out DynamicBuffer<RouteWaypoint> blockerWaypoints)
                    && TryGetLineTrackChain(blockerLine, blockerWaypoints, out LineTrackChain blockerChain)
                    && localProtectedIntervalIndex >= 0
                    && TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
                        shadowDecision.BlockerVehicle,
                        blockerLine,
                        blockerWaypoints,
                        blockerChain,
                        localChain,
                        localProtectedIntervalIndex,
                        protectedInterval,
                        out _,
                        out BypassProtectedInterval blockerProtectedInterval,
                        out string intervalResolutionSource)
                    && TryProjectTrackModelRuntimePosition(shadowDecision.BlockerVehicle, blockerLine, blockerWaypoints, blockerProtectedInterval, out TrackModelRuntimePosition blockerPosition))
                {
                    blockerPositionText = FormatRuntimePosition(blockerPosition);
                    if (intervalResolutionSource == "fallback")
                        blockerPositionText += " src=fallback";
                }
            }

            bool includeDecisionSequence = hasDecision && ShouldIncludeBypassSequenceForShadowLog(hasDecision, shadowDecision);
            string decisionSequence = includeDecisionSequence
                ? BuildBypassDecisionSequenceSummaryForLogging(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    shadowDecision.ProtectedIntervalIndex)
                : string.Empty;
            string key = summary
                + "|"
                + shadowRisk
                + "|"
                + (hasDecision ? shadowDecision.ProtectedIntervalIndex : -1)
                + "|"
                + (hasDecision ? (shadowDecision.ShouldYield ? "Y" : "N") + "|" + shadowDecision.ReasonCode : "decision-unavailable")
                + "|"
                + currentBypassBuilding.Index
                + "|"
                + nextBypassBuilding.Index
                + "|"
                + (hasDecision ? shadowDecision.BlockerVehicle.Index : -1)
                + "|"
                + localPositionText
                + "|"
                + blockerPositionText
                + "|"
                + decisionSequence;
            if (m_BypassTrackModelShadowLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelShadowLogCache[localVehicle] = key;

            log.Info("[BypassTrackModelShadow] vehicle=" + localVehicle.Index
                + " line=" + localLine.Index
                + " current=" + currentBypassBuilding.Index
                + " next=" + nextBypassBuilding.Index
                + " risk=" + shadowRisk
                + " shadow=" + (hasDecision ? (shadowDecision.ShouldYield ? "yield" : "pass") : "unavailable")
                + " reason=" + (hasDecision ? shadowDecision.ReasonCode : "decision-unavailable")
                + " local=" + localPositionText
                + " blocker=" + blockerPositionText
                + (!string.IsNullOrEmpty(decisionSequence) ? " " + decisionSequence : string.Empty)
                + " " + summary);
        }

        private void EnsureBypassTrackModelShadowSnapshotCurrent(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out BypassTrackModelShadowDecision shadowDecision)
        {
            shadowDecision = new BypassTrackModelShadowDecision(false, false, "decision-unavailable", -1, false, Entity.Null, false);

            if (localVehicle == Entity.Null || localLine == Entity.Null)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_BypassTrackModelShadowSnapshots.TryGetValue(localVehicle, out BypassTrackModelShadowSnapshot snapshot)
                && snapshot.Frame == nowFrame
                && snapshot.Line == localLine
                && snapshot.CurrentWaypointIndex == currentWaypointIndex
                && snapshot.CurrentBypassBuilding == currentBypassBuilding
                && snapshot.NextBypassBuilding == nextBypassBuilding)
            {
                shadowDecision = snapshot.Decision;
                return;
            }

            TryEvaluateBypassTrackModelShadowDecision(localVehicle, localLine, localWaypoints, currentWaypointIndex, nowFrame, out shadowDecision);

            m_BypassTrackModelShadowSnapshots[localVehicle] = new BypassTrackModelShadowSnapshot(
                nowFrame,
                localLine,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding,
                shadowDecision);
        }

        private string GetBypassTrackModelDecisionShadowSuffix(Entity localVehicle, bool shouldYield)
        {
            if (!TryGetLatestBypassTrackModelShadowSnapshot(localVehicle, out BypassTrackModelShadowSnapshot snapshot))
            {
                return string.Empty;
            }

            BypassTrackModelShadowDecision decision = snapshot.Decision;
            string shadowDecision = decision.ShouldYield ? "yield" : "pass";
            string alignment = shadowDecision == (shouldYield ? "yield" : "pass") ? "match" : "diff";
            return " shadow=" + shadowDecision + "/" + decision.ReasonCode + "/" + alignment;
        }

        private void LogBypassTrackModelDecisionComparison(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            bool shouldYield)
        {
            if (!TryGetLatestBypassTrackModelShadowSnapshot(localVehicle, out BypassTrackModelShadowSnapshot snapshot))
            {
                return;
            }

            BypassTrackModelShadowDecision decision = snapshot.Decision;
            string summary = "trackModel[unavailable]";
            string risk = "unavailable";
            if (TryEvaluateBypassTrackModelShadow(localLine, localWaypoints, currentWaypointIndex, out _, out string builtRisk, out string builtSummary))
            {
                summary = builtSummary;
                risk = builtRisk;
            }
            string localPositionText = decision.HasReliableLocalPosition ? "pos[known]" : "pos[unknown]";
            string blockerPositionText = decision.BlockerVehicle != Entity.Null
                ? (decision.UsedFallbackResolution ? "blocker src=fallback" : "blocker")
                : string.Empty;
            string liveDecision = shouldYield ? "yield" : "pass";
            string shadowDecision = decision.ShouldYield ? "yield" : "pass";
            string alignment = shadowDecision == liveDecision ? "match" : "diff";
            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = liveDecision
                + "|"
                + shadowDecision
                + "|"
                + alignment
                + "|"
                + decision.ReasonCode
                + "|"
                + decision.ProtectedIntervalIndex
                + "|"
                + decision.BlockerVehicle.Index;
            if (!ShouldEmitVehicleLogWithCooldown(
                    m_BypassTrackModelCompareThrottleCache,
                    m_BypassTrackModelCompareLastLogFrame,
                    localVehicle,
                    coarseKey,
                    nowFrame,
                    BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES))
            {
                return;
            }

            bool includeDecisionSequence = ShouldIncludeBypassSequenceForCompareLog(alignment, decision);
            string decisionSequence = includeDecisionSequence
                ? BuildBypassDecisionSequenceSummaryForLogging(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    decision.ProtectedIntervalIndex)
                : string.Empty;
            string key = liveDecision
                + "|"
                + shadowDecision
                + "|"
                + decision.ReasonCode
                + "|"
                + decision.ProtectedIntervalIndex
                + "|"
                + decision.BlockerVehicle.Index
                + "|"
                + localPositionText
                + "|"
                + blockerPositionText
                + "|"
                + decisionSequence;
            if (m_BypassTrackModelCompareLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelCompareLogCache[localVehicle] = key;
            log.Info("[BypassTrackModelCompare] vehicle=" + localVehicle.Index
                + " live=" + liveDecision
                + " shadow=" + shadowDecision
                + " reason=" + decision.ReasonCode
                + " risk=" + risk
                + " compare=" + alignment
                + " protectedInterval=" + decision.ProtectedIntervalIndex
                + " local=" + localPositionText
                + " blocker=" + blockerPositionText
                + (!string.IsNullOrEmpty(decisionSequence) ? " " + decisionSequence : string.Empty)
                + " " + summary);
        }

        private bool TryGetTrackModelLiveBypassDecision(
            Entity localVehicle,
            out bool shouldYield,
            out string reason,
            out Entity blockerVehicle)
        {
            shouldYield = false;
            reason = "track-model-decision-unavailable";
            blockerVehicle = Entity.Null;
            if (!TryGetLatestBypassTrackModelShadowSnapshot(localVehicle, out BypassTrackModelShadowSnapshot snapshot)
                || !snapshot.Decision.Available)
            {
                return false;
            }

            BypassTrackModelShadowDecision decision = snapshot.Decision;
            shouldYield = decision.ShouldYield;
            reason = "track-model-" + decision.ReasonCode;
            blockerVehicle = decision.BlockerVehicle;
            return true;
        }

        private string GetTrackModelLiveDecisionLogSuffix(Entity localVehicle)
        {
            if (!TryGetLatestBypassTrackModelShadowSnapshot(localVehicle, out BypassTrackModelShadowSnapshot snapshot)
                || !snapshot.Decision.Available)
            {
                return string.Empty;
            }
            return string.Empty;
        }

        private bool ShouldShadowVetoLiveBypassYield(Entity localVehicle, out string shadowReason)
        {
            shadowReason = string.Empty;
            if (!TryGetLatestBypassTrackModelShadowSnapshot(localVehicle, out BypassTrackModelShadowSnapshot snapshot)
                || !snapshot.Decision.Available
                || snapshot.Decision.ShouldYield
                || !snapshot.Decision.HasReliableLocalPosition)
            {
                return false;
            }

            BypassTrackModelShadowDecision decision = snapshot.Decision;
            switch (decision.ReasonCode)
            {
                case "no-shared-protected-interval":
                case "local-cleared-protected-interval":
                case "no-express-in-protected-interval":
                case "no-express-in-shared-window":
                    shadowReason = decision.ReasonCode;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetLatestBypassTrackModelShadowSnapshot(Entity localVehicle, out BypassTrackModelShadowSnapshot snapshot)
        {
            snapshot = default;
            return localVehicle != Entity.Null
                && m_BypassTrackModelShadowSnapshots.TryGetValue(localVehicle, out snapshot);
        }

        private static bool ShouldIncludeIntervalAtom(TrackAtom atom)
        {
            return atom.AtomClass == TrackAtomClass.PrimaryLane;
        }

        private static void CollectProtectedIntervalAtomKeys(LineTrackChain chain, BypassProtectedInterval interval, List<TrackAtomKey> keys)
        {
            keys.Clear();
            if (chain == null)
                return;

            for (int atomIndex = interval.StartAtomIndex; atomIndex < interval.EndAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                keys.Add(atom.Key);
            }
        }

        private static void CollectProtectedIntervalPhysicalLaneKeys(LineTrackChain chain, BypassProtectedInterval interval, List<Entity> keys)
        {
            keys.Clear();
            if (chain == null)
                return;

            for (int atomIndex = interval.StartAtomIndex; atomIndex < interval.EndAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                keys.Add(atom.Key.PhysicalLaneKey);
            }
        }

        private int CountProtectedIntervalPhysicalOverlap(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain,
            BypassProtectedInterval candidateInterval)
        {
            if (sourceChain == null || candidateChain == null)
                return 0;

            m_ProtectedIntervalOverlapSourceKeys.Clear();
            for (int atomIndex = sourceInterval.StartAtomIndex; atomIndex < sourceInterval.EndAtomIndexExclusive && atomIndex < sourceChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = sourceChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                m_ProtectedIntervalOverlapSourceKeys.Add(atom.Key.PhysicalLaneKey);
            }

            if (m_ProtectedIntervalOverlapSourceKeys.Count == 0)
                return 0;

            int overlapCount = 0;
            m_ProtectedIntervalOverlapMatchedKeys.Clear();
            for (int atomIndex = candidateInterval.StartAtomIndex; atomIndex < candidateInterval.EndAtomIndexExclusive && atomIndex < candidateChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = candidateChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                if (m_ProtectedIntervalOverlapSourceKeys.Contains(physicalLaneKey) && m_ProtectedIntervalOverlapMatchedKeys.Add(physicalLaneKey))
                    overlapCount++;
            }

            return overlapCount;
        }

        private int ComputeProtectedIntervalLongestPhysicalOrderedRun(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain,
            BypassProtectedInterval candidateInterval)
        {
            CollectProtectedIntervalPhysicalLaneKeys(sourceChain, sourceInterval, m_ProtectedIntervalOrderedSourceKeys);
            CollectProtectedIntervalPhysicalLaneKeys(candidateChain, candidateInterval, m_ProtectedIntervalOrderedCandidateKeys);
            if (m_ProtectedIntervalOrderedSourceKeys.Count == 0 || m_ProtectedIntervalOrderedCandidateKeys.Count == 0)
                return 0;

            int bestRun = 0;
            for (int sourceIndex = 0; sourceIndex < m_ProtectedIntervalOrderedSourceKeys.Count; sourceIndex++)
            {
                for (int candidateIndex = 0; candidateIndex < m_ProtectedIntervalOrderedCandidateKeys.Count; candidateIndex++)
                {
                    int run = 0;
                    while (sourceIndex + run < m_ProtectedIntervalOrderedSourceKeys.Count
                        && candidateIndex + run < m_ProtectedIntervalOrderedCandidateKeys.Count
                        && m_ProtectedIntervalOrderedSourceKeys[sourceIndex + run] == m_ProtectedIntervalOrderedCandidateKeys[candidateIndex + run])
                    {
                        run++;
                    }

                    if (run > bestRun)
                        bestRun = run;
                }
            }

            return bestRun;
        }

        private bool TryFindProtectedIntervalOrderedRunSpan(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain,
            BypassProtectedInterval candidateInterval,
            out int sourceStartAtomIndex,
            out int sourceEndAtomIndexExclusive,
            out int candidateStartAtomIndex,
            out int candidateEndAtomIndexExclusive,
            out int orderedRunLength)
        {
            sourceStartAtomIndex = -1;
            sourceEndAtomIndexExclusive = -1;
            candidateStartAtomIndex = -1;
            candidateEndAtomIndexExclusive = -1;
            orderedRunLength = 0;

            if (sourceChain == null || candidateChain == null)
                return false;

            m_ProtectedIntervalOrderedSourceKeys.Clear();
            m_ProtectedIntervalOrderedSourceAtomIndices.Clear();
            for (int atomIndex = sourceInterval.StartAtomIndex; atomIndex < sourceInterval.EndAtomIndexExclusive && atomIndex < sourceChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = sourceChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;

                m_ProtectedIntervalOrderedSourceKeys.Add(atom.Key.PhysicalLaneKey);
                m_ProtectedIntervalOrderedSourceAtomIndices.Add(atomIndex);
            }

            m_ProtectedIntervalOrderedCandidateKeys.Clear();
            m_ProtectedIntervalOrderedCandidateAtomIndices.Clear();
            for (int atomIndex = candidateInterval.StartAtomIndex; atomIndex < candidateInterval.EndAtomIndexExclusive && atomIndex < candidateChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = candidateChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;

                m_ProtectedIntervalOrderedCandidateKeys.Add(atom.Key.PhysicalLaneKey);
                m_ProtectedIntervalOrderedCandidateAtomIndices.Add(atomIndex);
            }

            if (m_ProtectedIntervalOrderedSourceKeys.Count == 0 || m_ProtectedIntervalOrderedCandidateKeys.Count == 0)
                return false;

            int bestSourceIndex = -1;
            int bestCandidateIndex = -1;
            for (int sourceIndex = 0; sourceIndex < m_ProtectedIntervalOrderedSourceKeys.Count; sourceIndex++)
            {
                for (int candidateIndex = 0; candidateIndex < m_ProtectedIntervalOrderedCandidateKeys.Count; candidateIndex++)
                {
                    int run = 0;
                    while (sourceIndex + run < m_ProtectedIntervalOrderedSourceKeys.Count
                        && candidateIndex + run < m_ProtectedIntervalOrderedCandidateKeys.Count
                        && m_ProtectedIntervalOrderedSourceKeys[sourceIndex + run] == m_ProtectedIntervalOrderedCandidateKeys[candidateIndex + run])
                    {
                        run++;
                    }

                    if (run <= orderedRunLength)
                        continue;

                    orderedRunLength = run;
                    bestSourceIndex = sourceIndex;
                    bestCandidateIndex = candidateIndex;
                }
            }

            if (orderedRunLength <= 0 || bestSourceIndex < 0 || bestCandidateIndex < 0)
                return false;

            sourceStartAtomIndex = m_ProtectedIntervalOrderedSourceAtomIndices[bestSourceIndex];
            sourceEndAtomIndexExclusive = m_ProtectedIntervalOrderedSourceAtomIndices[bestSourceIndex + orderedRunLength - 1] + 1;
            candidateStartAtomIndex = m_ProtectedIntervalOrderedCandidateAtomIndices[bestCandidateIndex];
            candidateEndAtomIndexExclusive = m_ProtectedIntervalOrderedCandidateAtomIndices[bestCandidateIndex + orderedRunLength - 1] + 1;
            return sourceEndAtomIndexExclusive > sourceStartAtomIndex
                && candidateEndAtomIndexExclusive > candidateStartAtomIndex;
        }

        private ProtectedIntervalMatch FindBestMatchingProtectedInterval(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain)
        {
            if (sourceChain == null
                || candidateChain == null
                || candidateChain.BypassProtectedIntervals.Count == 0)
            {
                return default;
            }

            int bestIndex = -1;
            int bestOverlap = 0;
            int bestOrderedRun = 0;
            bool ambiguous = false;

            for (int i = 0; i < candidateChain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidateInterval = candidateChain.BypassProtectedIntervals[i];
                int overlapCount = CountProtectedIntervalPhysicalOverlap(sourceChain, sourceInterval, candidateChain, candidateInterval);
                if (overlapCount <= 0)
                    continue;
                int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(sourceChain, sourceInterval, candidateChain, candidateInterval);

                if (orderedRun > bestOrderedRun
                    || (orderedRun == bestOrderedRun && overlapCount > bestOverlap))
                {
                    bestIndex = i;
                    bestOverlap = overlapCount;
                    bestOrderedRun = orderedRun;
                    ambiguous = false;
                    continue;
                }

                if (orderedRun == bestOrderedRun && overlapCount == bestOverlap)
                    ambiguous = true;
            }

            if (bestIndex < 0)
                return default;

            return new ProtectedIntervalMatch(true, ambiguous, bestIndex, bestOverlap);
        }

        private static ulong ComputeProtectedIntervalAtomSignature(LineTrackChain chain, BypassProtectedInterval interval)
        {
            ulong hash = 1469598103934665603UL;
            int atomCount = 0;

            for (int atomIndex = interval.StartAtomIndex; atomIndex < interval.EndAtomIndexExclusive && atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;

                atomCount++;
                hash = MixLineSignature(hash, atom.Key.PhysicalLaneKey.Index);
                hash = MixLineSignature(hash, atom.Key.PreviousTarget.Index);
                hash = MixLineSignature(hash, atom.Key.NextTarget.Index);
            }

            hash = MixLineSignature(hash, atomCount);
            return hash;
        }

        private bool TryGetSharedAtomContext(Entity line, TrackAtomKey key, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;
            if (!m_SharedTrackIndex.TryGetValue(key, out List<SharedTrackOccurrence> occurrences)
                || occurrences == null
                || occurrences.Count == 0)
            {
                return false;
            }

            var sharedLines = new HashSet<Entity>();
            foreach (SharedTrackOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity != line)
                    sharedLines.Add(occurrence.LineEntity);
            }

            sharedLineCount = sharedLines.Count;
            if (sharedLineCount == 0)
                return false;

            TrackAtomKey mirroredKey = new TrackAtomKey(key.PhysicalLaneKey, key.NextTarget, key.PreviousTarget);
            if (m_SharedTrackIndex.TryGetValue(mirroredKey, out List<SharedTrackOccurrence> mirroredOccurrences)
                && mirroredOccurrences != null)
            {
                foreach (SharedTrackOccurrence occurrence in mirroredOccurrences)
                {
                    if (occurrence.LineEntity != line)
                    {
                        mirroredContext = true;
                        break;
                    }
                }
            }

            return true;
        }

        private bool TryGetSharedPhysicalContext(Entity line, TrackAtom atom, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;

            if (!m_SharedPhysicalTrackIndex.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                || occurrences == null
                || occurrences.Count == 0)
            {
                return false;
            }

            var sharedLines = new HashSet<Entity>();
            foreach (SharedPhysicalOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity == line)
                    continue;

                sharedLines.Add(occurrence.LineEntity);
                if (occurrence.PreviousTarget == atom.Key.NextTarget
                    && occurrence.NextTarget == atom.Key.PreviousTarget)
                {
                    mirroredContext = true;
                }
            }

            sharedLineCount = sharedLines.Count;
            return sharedLineCount > 0;
        }

        private bool TryGetSharedPhysicalContextForLine(Entity line, TrackAtom atom, Entity otherLine, out bool mirroredContext)
        {
            mirroredContext = false;
            if (otherLine == Entity.Null
                || !m_SharedPhysicalTrackIndex.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
                || occurrences == null)
            {
                return false;
            }

            bool found = false;
            foreach (SharedPhysicalOccurrence occurrence in occurrences)
            {
                if (occurrence.LineEntity != otherLine)
                    continue;

                found = true;
                if (occurrence.PreviousTarget == atom.Key.NextTarget
                    && occurrence.NextTarget == atom.Key.PreviousTarget)
                {
                    mirroredContext = true;
                }
            }

            return found;
        }

        private void LogLineTrackChainDiagnostics(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line) || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (EntityManager.HasBuffer<RouteSegment>(line))
            {
                DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
                int rawLimit = math.min(segments.Length, 4);
                for (int waypointIndex = 0; waypointIndex < rawLimit; waypointIndex++)
                    LogRouteSegmentPathElementDiagnostics(line, waypointIndex, segments[waypointIndex].m_Segment);
            }

            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                log.Info("[TrackModel] line=" + line.Index + " chain=unavailable");
                return;
            }

            EnsureTrackChainBypassPipelineReady(chain);

            StringBuilder sb = new StringBuilder();
            sb.Append("[TrackModel] line=").Append(line.Index)
              .Append(" atoms=").Append(chain.TrackAtoms.Count)
              .Append(" controlPoints=").Append(chain.ControlPoints.Count)
              .Append(" controlEdges=").Append(chain.ControlEdges.Count)
              .Append(" sharedRuns=").Append(chain.SharedRuns.Count)
              .Append(" edgeSharedSpans=").Append(chain.ControlEdgeSharedSpans.Count)
              .Append(" protectedIntervals=").Append(chain.BypassProtectedIntervals.Count)
              .Append(" protectedShared=").Append(chain.ProtectedSharedIntervals.Count)
              .Append(" signature=").Append(chain.Signature)
              .Append(" firstAtoms=");

            int limit = math.min(chain.TrackAtoms.Count, 12);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");

                TrackAtom atom = chain.TrackAtoms[i];
                sb.Append(atom.Key.PhysicalLaneKey.Index)
                  .Append(":")
                  .Append(atom.Key.PreviousTarget == Entity.Null ? "null" : atom.Key.PreviousTarget.Index.ToString())
                  .Append(">")
                  .Append(atom.Key.NextTarget == Entity.Null ? "null" : atom.Key.NextTarget.Index.ToString())
                  .Append(":")
                  .Append(atom.AtomClass);
            }

            log.Info(sb.ToString());

            if (chain.SharedRuns.Count > 0)
            {
                StringBuilder runSb = new StringBuilder();
                runSb.Append("[TrackModelRuns] line=").Append(line.Index);
                int runLimit = math.min(chain.SharedRuns.Count, 8);
                for (int i = 0; i < runLimit; i++)
                {
                    SharedTrackRun run = chain.SharedRuns[i];
                    runSb.Append(" | run").Append(i)
                      .Append("=").Append(run.StartAtomIndex)
                      .Append("..").Append(run.EndAtomIndexExclusive)
                      .Append(" sharedLines=").Append(run.SharedLineCount)
                      .Append(" mirrored=").Append(run.HasMirroredContext ? "1" : "0");
                }

                log.Info(runSb.ToString());
            }

            if (chain.ControlEdgeSharedSpans.Count > 0)
            {
                StringBuilder edgeSb = new StringBuilder();
                edgeSb.Append("[TrackModelEdges] line=").Append(line.Index);
                int edgeLimit = math.min(chain.ControlEdgeSharedSpans.Count, 8);
                for (int i = 0; i < edgeLimit; i++)
                {
                    ControlEdgeSharedSpan span = chain.ControlEdgeSharedSpans[i];
                    edgeSb.Append(" | edge").Append(span.ControlEdgeIndex)
                        .Append("=").Append(span.StartAtomIndex)
                        .Append("..").Append(span.EndAtomIndexExclusive)
                        .Append(" sharedLines=").Append(span.SharedLineCount)
                        .Append(" mirrored=").Append(span.HasMirroredContext ? "1" : "0");
                }

                log.Info(edgeSb.ToString());
            }

            if (chain.BypassProtectedIntervals.Count > 0)
            {
                StringBuilder protectedSb = new StringBuilder();
                protectedSb.Append("[TrackModelProtected] line=").Append(line.Index);
                int protectedLimit = math.min(chain.BypassProtectedIntervals.Count, 6);
                for (int i = 0; i < protectedLimit; i++)
                {
                    BypassProtectedInterval interval = chain.BypassProtectedIntervals[i];
                    protectedSb.Append(" | p").Append(i)
                        .Append(" cp=").Append(interval.StartControlPointIndex).Append("->").Append(interval.EndControlPointIndex)
                        .Append(" edges=").Append(interval.StartControlEdgeIndex).Append("..").Append(interval.EndControlEdgeIndexInclusive)
                        .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                        .Append(" baseFrames=").Append(interval.BaseFrames.ToString("F1"));
                }

                log.Info(protectedSb.ToString());
            }

            if (chain.ProtectedSharedIntervals.Count > 0)
            {
                StringBuilder overlapSb = new StringBuilder();
                overlapSb.Append("[TrackModelProtectedShared] line=").Append(line.Index);
                int overlapLimit = math.min(chain.ProtectedSharedIntervals.Count, 8);
                for (int i = 0; i < overlapLimit; i++)
                {
                    ProtectedSharedInterval interval = chain.ProtectedSharedIntervals[i];
                    overlapSb.Append(" | ps").Append(i)
                        .Append(" p=").Append(interval.ProtectedIntervalIndex)
                        .Append(" edge=").Append(interval.ControlEdgeIndex)
                        .Append(" atoms=").Append(interval.StartAtomIndex).Append("..").Append(interval.EndAtomIndexExclusive)
                        .Append(" sharedLines=").Append(interval.SharedLineCount)
                        .Append(" mirrored=").Append(interval.HasMirroredContext ? "1" : "0")
                        .Append(" entry=").Append(interval.EntryOffsetFrames.ToString("F1"))
                        .Append(" clear=").Append(interval.ClearOffsetFrames.ToString("F1"));
                }

                log.Info(overlapSb.ToString());
            }

            if (chain.BypassProtectedIntervals.Count > 0)
            {
                StringBuilder summarySb = new StringBuilder();
                summarySb.Append("[TrackModelProtectedSummary] line=").Append(line.Index);
                int summaryLimit = math.min(chain.ProtectedIntervalSummaries.Count, 6);
                for (int i = 0; i < summaryLimit; i++)
                {
                    ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[i];
                    summarySb.Append(" | p").Append(i)
                        .Append(" sharedSegments=").Append(summary.SharedSegmentCount)
                        .Append(" maxSharedLines=").Append(summary.MaxSharedLineCount)
                        .Append(" mirrored=").Append(summary.HasMirroredContext ? "1" : "0")
                        .Append(" minEntry=").Append(summary.MinEntryOffsetFrames.ToString("F1"))
                        .Append(" maxClear=").Append(summary.MaxClearOffsetFrames.ToString("F1"));
                }

                log.Info(summarySb.ToString());
            }

            LogSharedTrackIndexSummary();
        }
    }
}
