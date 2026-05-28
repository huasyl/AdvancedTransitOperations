using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private const int MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS = 3;
        private const int MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN = 2;
        private const float PROTECTED_INTERVAL_TAIL_CLEARANCE_ATOMS = 1.25f;
        private const float SAME_DIRECTION_AHEAD_MARGIN_ATOMS = 0.75f;
        private const float TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES = 1f;
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;
        private const float LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS = 8f;
        private const int MAX_CONFLICT_CORRIDOR_GAP_ATOMS = 6;
        private const uint SUSPECT_PROGRESS_VALIDATE_INTERVAL_FRAMES = 60;
        private const int SUSPECT_PROGRESS_CANDIDATE_SEGMENT_RADIUS = 1;
        private const int SUSPECT_PROGRESS_ATOM_MISMATCH_THRESHOLD = 12;
        private const float SUSPECT_PROGRESS_POSITION_IMPROVEMENT_METERS = 120f;

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

        internal enum BypassExecutionMode : byte
        {
            SimpleSceneScan = 0,
            ComplexLineModel = 1,
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

        private readonly struct TrackTurnbackStationBoundary
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

        internal sealed class LineTrackChain
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
            public List<TurnbackBoundary> TurnbackBoundaries = new List<TurnbackBoundary>();
            public LocalBypassWaypointSceneBinding[] LocalBypassWaypointScenes = System.Array.Empty<LocalBypassWaypointSceneBinding>();
            public uint LocalBypassWaypointScenesVersion;
            public uint BypassExecutionModeVersion;
            public int LocalBypassSceneCount;
            public int MaxExpressLinesPerScene;
            public int MultiTrunkSceneCount;
            public BypassExecutionMode ExecutionMode;
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

        internal readonly struct LocalBypassWaypointSceneBinding
        {
            public readonly bool Available;
            public readonly SceneKey SceneKey;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly int ProtectedIntervalIndex;
            public readonly BypassProtectedInterval ProtectedInterval;
            public readonly ProtectedIntervalSummary Summary;
            public readonly float DepartureReleaseCoordinate;
            public readonly float IntervalDisplayLength;

            public LocalBypassWaypointSceneBinding(
                bool available,
                SceneKey sceneKey,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                int protectedIntervalIndex,
                BypassProtectedInterval protectedInterval,
                ProtectedIntervalSummary summary,
                float departureReleaseCoordinate,
                float intervalDisplayLength)
            {
                Available = available;
                SceneKey = sceneKey;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                ProtectedIntervalIndex = protectedIntervalIndex;
                ProtectedInterval = protectedInterval;
                Summary = summary;
                DepartureReleaseCoordinate = departureReleaseCoordinate;
                IntervalDisplayLength = intervalDisplayLength;
            }
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
            public readonly int TraversalPhaseIndex;
            public readonly int TraversalPhaseStartAtomIndex;
            public readonly int TraversalPhaseEndAtomExclusive;
            public readonly int NextTurnbackBoundaryAtomIndex;

            public TrackModelRuntimePosition(
                int currentControlEdgeIndex,
                int currentAtomIndex,
                float atomPosition01,
                TrackModelRelativeToProtectedInterval relativeToProtectedInterval,
                float confidence,
                int traversalPhaseIndex,
                int traversalPhaseStartAtomIndex,
                int traversalPhaseEndAtomExclusive,
                int nextTurnbackBoundaryAtomIndex)
            {
                CurrentControlEdgeIndex = currentControlEdgeIndex;
                CurrentAtomIndex = currentAtomIndex;
                AtomPosition01 = atomPosition01;
                RelativeToProtectedInterval = relativeToProtectedInterval;
                Confidence = confidence;
                TraversalPhaseIndex = traversalPhaseIndex;
                TraversalPhaseStartAtomIndex = traversalPhaseStartAtomIndex;
                TraversalPhaseEndAtomExclusive = traversalPhaseEndAtomExclusive;
                NextTurnbackBoundaryAtomIndex = nextTurnbackBoundaryAtomIndex;
            }
        }

        private readonly struct TrunkSkeleton
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

        private readonly struct SceneExpressRelation
        {
            public readonly Entity ExpressLine;
            public readonly LineTrackChain ExpressChain;
            public readonly bool Ambiguous;
            public readonly int ExpressProtectedIntervalIndex;
            public readonly BypassProtectedInterval ExpressProtectedInterval;
            public readonly int OverlapCount;
            public readonly int OrderedRun;
            public readonly string ResolutionSource;
            public readonly bool HasRelevantSharedEntryAtomIndex;
            public readonly int RelevantSharedEntryAtomIndex;
            public readonly bool HasSelectedTrunkSegment;
            public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
            public readonly TrunkSkeleton TrunkSkeleton;
            public readonly SceneRelationTrunkCandidateSet TrunkCandidates;

            public SceneExpressRelation(
                Entity expressLine,
                LineTrackChain expressChain,
                bool ambiguous,
                int expressProtectedIntervalIndex,
                BypassProtectedInterval expressProtectedInterval,
                int overlapCount,
                int orderedRun,
                string resolutionSource,
                bool hasRelevantSharedEntryAtomIndex,
                int relevantSharedEntryAtomIndex,
                bool hasSelectedTrunkSegment,
                GlobalSharedTrunkSegment selectedTrunkSegment,
                TrunkSkeleton trunkSkeleton,
                SceneRelationTrunkCandidateSet trunkCandidates)
            {
                ExpressLine = expressLine;
                ExpressChain = expressChain;
                Ambiguous = ambiguous;
                ExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
                ExpressProtectedInterval = expressProtectedInterval;
                OverlapCount = overlapCount;
                OrderedRun = orderedRun;
                ResolutionSource = resolutionSource;
                HasRelevantSharedEntryAtomIndex = hasRelevantSharedEntryAtomIndex;
                RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
                HasSelectedTrunkSegment = hasSelectedTrunkSegment;
                SelectedTrunkSegment = selectedTrunkSegment;
                TrunkSkeleton = trunkSkeleton;
                TrunkCandidates = trunkCandidates;
            }
        }

        private sealed class SceneRelationTrunkCandidateSet
        {
            public readonly List<GlobalSharedTrunkSegment> Segments = new List<GlobalSharedTrunkSegment>();
        }

        private readonly struct SceneExpressVehicleCandidate
        {
            public readonly SceneExpressRelation Relation;
            public readonly Entity ExpressLine;
            public readonly Entity ExpressVehicle;
            public readonly LineTrackChain ExpressChain;
            public readonly LineRunningVehicleSnapshot RunningVehicle;
            public readonly int ExpressProtectedIntervalIndex;
            public readonly BypassProtectedInterval ExpressProtectedInterval;
            public readonly int OverlapCount;
            public readonly int OrderedRun;
            public readonly string IntervalResolutionSource;
            public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
            public readonly TrunkSkeleton TrunkSkeleton;
            public readonly RelativeToTrunkState LocalTrunkState;
            public readonly RelativeToTrunkState ExpressTrunkState;
            public readonly bool ExpressCurrentWaypointMatchesBypassBuilding;
            public readonly int RelevantSharedEntryAtomIndex;
            public readonly int EntryDistanceAtoms;

            public SceneExpressVehicleCandidate(
                SceneExpressRelation relation,
                Entity expressLine,
                Entity expressVehicle,
                LineTrackChain expressChain,
                LineRunningVehicleSnapshot runningVehicle,
                int expressProtectedIntervalIndex,
                BypassProtectedInterval expressProtectedInterval,
                int overlapCount,
                int orderedRun,
                string intervalResolutionSource,
                GlobalSharedTrunkSegment selectedTrunkSegment,
                TrunkSkeleton trunkSkeleton,
                RelativeToTrunkState localTrunkState,
                RelativeToTrunkState expressTrunkState,
                bool expressCurrentWaypointMatchesBypassBuilding,
                int relevantSharedEntryAtomIndex,
                int entryDistanceAtoms)
            {
                Relation = relation;
                ExpressLine = expressLine;
                ExpressVehicle = expressVehicle;
                ExpressChain = expressChain;
                RunningVehicle = runningVehicle;
                ExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
                ExpressProtectedInterval = expressProtectedInterval;
                OverlapCount = overlapCount;
                OrderedRun = orderedRun;
                IntervalResolutionSource = intervalResolutionSource;
                SelectedTrunkSegment = selectedTrunkSegment;
                TrunkSkeleton = trunkSkeleton;
                LocalTrunkState = localTrunkState;
                ExpressTrunkState = expressTrunkState;
                ExpressCurrentWaypointMatchesBypassBuilding = expressCurrentWaypointMatchesBypassBuilding;
                RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
                EntryDistanceAtoms = entryDistanceAtoms;
            }
        }

        private readonly struct SceneExpressFrontier
        {
            public readonly SceneExpressRelation Relation;
            public readonly bool HasPrimaryCandidate;
            public readonly SceneExpressVehicleCandidate PrimaryCandidate;
            public readonly bool HasSecondaryCandidate;
            public readonly SceneExpressVehicleCandidate SecondaryCandidate;
            public readonly int AdmittedCandidateCount;

            public SceneExpressFrontier(
                SceneExpressRelation relation,
                bool hasPrimaryCandidate,
                SceneExpressVehicleCandidate primaryCandidate,
                bool hasSecondaryCandidate,
                SceneExpressVehicleCandidate secondaryCandidate,
                int admittedCandidateCount)
            {
                Relation = relation;
                HasPrimaryCandidate = hasPrimaryCandidate;
                PrimaryCandidate = primaryCandidate;
                HasSecondaryCandidate = hasSecondaryCandidate;
                SecondaryCandidate = secondaryCandidate;
                AdmittedCandidateCount = admittedCandidateCount;
            }
        }

        private sealed class SceneExpressFrontierAccumulator
        {
            public readonly SceneExpressRelation Relation;
            public bool HasPrimaryCandidate;
            public SceneExpressVehicleCandidate PrimaryCandidate;
            public bool HasSecondaryCandidate;
            public SceneExpressVehicleCandidate SecondaryCandidate;
            public int AdmittedCandidateCount;

            public SceneExpressFrontierAccumulator(SceneExpressRelation relation)
            {
                Relation = relation;
            }
        }

        private readonly struct BypassTrackModelDecision
        {
            public readonly bool Available;
            public readonly bool ShouldYield;
            public readonly string ReasonCode;
            public readonly int ProtectedIntervalIndex;
            public readonly bool HasReliableLocalPosition;
            public readonly Entity BlockerVehicle;
            public readonly bool UsedFallbackResolution;
            public readonly bool HasLatchedBlockerProjection;
            public readonly BypassLatchedBlockerProjection LatchedBlockerProjection;

            public BypassTrackModelDecision(
                bool available,
                bool shouldYield,
                string reasonCode,
                int protectedIntervalIndex,
                bool hasReliableLocalPosition,
                Entity blockerVehicle,
                bool usedFallbackResolution,
                bool hasLatchedBlockerProjection = false,
                BypassLatchedBlockerProjection latchedBlockerProjection = default)
            {
                Available = available;
                ShouldYield = shouldYield;
                ReasonCode = reasonCode;
                ProtectedIntervalIndex = protectedIntervalIndex;
                HasReliableLocalPosition = hasReliableLocalPosition;
                BlockerVehicle = blockerVehicle;
                UsedFallbackResolution = usedFallbackResolution;
                HasLatchedBlockerProjection = hasLatchedBlockerProjection;
                LatchedBlockerProjection = latchedBlockerProjection;
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

        private readonly struct BypassTrackModelDecisionSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Line;
            public readonly int CurrentWaypointIndex;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly BypassTrackModelDecision Decision;

            public BypassTrackModelDecisionSnapshot(
                uint frame,
                Entity line,
                int currentWaypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                BypassTrackModelDecision decision)
            {
                Frame = frame;
                Line = line;
                CurrentWaypointIndex = currentWaypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                Decision = decision;
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

        internal readonly struct VehicleTrackCursor
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

        internal readonly struct VehicleTrackCursorFrameSnapshot
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
            public readonly uint SharedTrackVersion;
            public readonly ulong LocalChainSignature;
            public readonly ulong ExpressChainSignature;
            public readonly PhysicalSharedWindowMatch Match;

            public SharedWindowMatchSnapshot(
                uint sharedTrackVersion,
                ulong localChainSignature,
                ulong expressChainSignature,
                PhysicalSharedWindowMatch match)
            {
                SharedTrackVersion = sharedTrackVersion;
                LocalChainSignature = localChainSignature;
                ExpressChainSignature = expressChainSignature;
                Match = match;
            }
        }

        private readonly struct LocalBypassSceneStaticKey : IEquatable<LocalBypassSceneStaticKey>
        {
            public readonly Entity Line;
            public readonly int WaypointIndex;

            public LocalBypassSceneStaticKey(Entity line, int waypointIndex)
            {
                Line = line;
                WaypointIndex = waypointIndex;
            }

            public bool Equals(LocalBypassSceneStaticKey other)
            {
                return Line == other.Line
                    && WaypointIndex == other.WaypointIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is LocalBypassSceneStaticKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Line.GetHashCode() * 397) ^ WaypointIndex;
                }
            }
        }

        private readonly struct LocalBypassSceneStaticSnapshot
        {
            public readonly SceneKey SceneKey;
            public readonly ulong LineChainSignature;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly int ProtectedIntervalIndex;
            public readonly BypassProtectedInterval ProtectedInterval;
            public readonly ProtectedIntervalSummary Summary;
            public readonly float DepartureReleaseCoordinate;
            public readonly float IntervalDisplayLength;

            public LocalBypassSceneStaticSnapshot(
                SceneKey sceneKey,
                ulong lineChainSignature,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                int protectedIntervalIndex,
                BypassProtectedInterval protectedInterval,
                ProtectedIntervalSummary summary,
                float departureReleaseCoordinate,
                float intervalDisplayLength)
            {
                SceneKey = sceneKey;
                LineChainSignature = lineChainSignature;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                ProtectedIntervalIndex = protectedIntervalIndex;
                ProtectedInterval = protectedInterval;
                Summary = summary;
                DepartureReleaseCoordinate = departureReleaseCoordinate;
                IntervalDisplayLength = intervalDisplayLength;
            }
        }

        private readonly struct LocalSceneCandidateExpressLinesCacheKey : IEquatable<LocalSceneCandidateExpressLinesCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity CurrentBypassBuilding;
            public readonly int LocalProtectedIntervalIndex;

            public LocalSceneCandidateExpressLinesCacheKey(
                Entity localLine,
                Entity currentBypassBuilding,
                int localProtectedIntervalIndex)
            {
                LocalLine = localLine;
                CurrentBypassBuilding = currentBypassBuilding;
                LocalProtectedIntervalIndex = localProtectedIntervalIndex;
            }

            public bool Equals(LocalSceneCandidateExpressLinesCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && CurrentBypassBuilding == other.CurrentBypassBuilding
                    && LocalProtectedIntervalIndex == other.LocalProtectedIntervalIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is LocalSceneCandidateExpressLinesCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalLine.GetHashCode();
                    hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ LocalProtectedIntervalIndex;
                    return hash;
                }
            }
        }

        private sealed class LocalSceneCandidateExpressLinesSnapshot
        {
            public uint SharedTrackVersion;
            public ulong LocalChainSignature;
            public readonly List<Entity> ExpressLines = new List<Entity>();
        }

        private readonly struct LocalSceneExpressStaticMatchCacheKey : IEquatable<LocalSceneExpressStaticMatchCacheKey>
        {
            public readonly Entity LocalLine;
            public readonly Entity ExpressLine;
            public readonly Entity CurrentBypassBuilding;
            public readonly int LocalProtectedIntervalIndex;

            public LocalSceneExpressStaticMatchCacheKey(
                Entity localLine,
                Entity expressLine,
                Entity currentBypassBuilding,
                int localProtectedIntervalIndex)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
                CurrentBypassBuilding = currentBypassBuilding;
                LocalProtectedIntervalIndex = localProtectedIntervalIndex;
            }

            public bool Equals(LocalSceneExpressStaticMatchCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine
                    && CurrentBypassBuilding == other.CurrentBypassBuilding
                    && LocalProtectedIntervalIndex == other.LocalProtectedIntervalIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is LocalSceneExpressStaticMatchCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalLine.GetHashCode();
                    hash = (hash * 397) ^ ExpressLine.GetHashCode();
                    hash = (hash * 397) ^ CurrentBypassBuilding.GetHashCode();
                    hash = (hash * 397) ^ LocalProtectedIntervalIndex;
                    return hash;
                }
            }
        }

        private readonly struct LocalSceneExpressStaticMatchSnapshot
        {
            public readonly uint SharedTrackVersion;
            public readonly ulong LocalChainSignature;
            public readonly ulong ExpressChainSignature;
            public readonly bool Found;
            public readonly bool Ambiguous;
            public readonly BypassProtectedInterval LocalSharedWindow;
            public readonly int ExpressProtectedIntervalIndex;
            public readonly BypassProtectedInterval ExpressProtectedInterval;
            public readonly int OverlapCount;
            public readonly int OrderedRun;
            public readonly string ResolutionSource;
            public readonly bool HasRelevantSharedEntryAtomIndex;
            public readonly int RelevantSharedEntryAtomIndex;
            public readonly bool HasSelectedTrunkSegment;
            public readonly GlobalSharedTrunkSegment SelectedTrunkSegment;
            public readonly TrunkSkeleton TrunkSkeleton;
            public readonly SceneRelationTrunkCandidateSet TrunkCandidates;

            public LocalSceneExpressStaticMatchSnapshot(
                uint sharedTrackVersion,
                ulong localChainSignature,
                ulong expressChainSignature,
                bool found,
                bool ambiguous,
                BypassProtectedInterval localSharedWindow,
                int expressProtectedIntervalIndex,
                BypassProtectedInterval expressProtectedInterval,
                int overlapCount,
                int orderedRun,
                string resolutionSource,
                bool hasRelevantSharedEntryAtomIndex,
                int relevantSharedEntryAtomIndex,
                bool hasSelectedTrunkSegment,
                GlobalSharedTrunkSegment selectedTrunkSegment,
                TrunkSkeleton trunkSkeleton,
                SceneRelationTrunkCandidateSet trunkCandidates)
            {
                SharedTrackVersion = sharedTrackVersion;
                LocalChainSignature = localChainSignature;
                ExpressChainSignature = expressChainSignature;
                Found = found;
                Ambiguous = ambiguous;
                LocalSharedWindow = localSharedWindow;
                ExpressProtectedIntervalIndex = expressProtectedIntervalIndex;
                ExpressProtectedInterval = expressProtectedInterval;
                OverlapCount = overlapCount;
                OrderedRun = orderedRun;
                ResolutionSource = resolutionSource;
                HasRelevantSharedEntryAtomIndex = hasRelevantSharedEntryAtomIndex;
                RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
                HasSelectedTrunkSegment = hasSelectedTrunkSegment;
                SelectedTrunkSegment = selectedTrunkSegment;
                TrunkSkeleton = trunkSkeleton;
                TrunkCandidates = trunkCandidates;
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
            public readonly bool HasPreselectedTrunkSegment;
            public readonly GlobalSharedTrunkSegment PreselectedTrunkSegment;
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
                bool hasPreselectedTrunkSegment,
                GlobalSharedTrunkSegment preselectedTrunkSegment,
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
                HasPreselectedTrunkSegment = hasPreselectedTrunkSegment;
                PreselectedTrunkSegment = preselectedTrunkSegment;
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
                    && HasPreselectedTrunkSegment == other.HasPreselectedTrunkSegment
                    && PreselectedTrunkSegment.Equals(other.PreselectedTrunkSegment)
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
                    hash = (hash * 397) ^ HasPreselectedTrunkSegment.GetHashCode();
                    hash = (hash * 397) ^ PreselectedTrunkSegment.GetHashCode();
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

    }
}
