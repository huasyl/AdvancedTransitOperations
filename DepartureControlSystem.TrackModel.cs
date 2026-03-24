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

            public TrackAtom(
                TrackAtomKey key,
                Entity sourceTarget,
                PathElementFlags sourceFlags,
                TrackAtomClass atomClass)
            {
                Key = key;
                SourceTarget = sourceTarget;
                SourceFlags = sourceFlags;
                AtomClass = atomClass;
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

        private sealed class LineTrackChain
        {
            public Entity LineEntity;
            public ulong Signature;
            public List<TrackAtom> TrackAtoms = new List<TrackAtom>();
            public List<TrackSegmentRange> SegmentRanges = new List<TrackSegmentRange>();
            public List<ControlPointMarker> ControlPoints = new List<ControlPointMarker>();
            public List<ControlEdge> ControlEdges = new List<ControlEdge>();
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
            public readonly string Risk;
            public readonly string Summary;
            public readonly string LocalPosition;
            public readonly Entity BlockerVehicle;
            public readonly string BlockerPosition;
            public readonly string SequenceSummary;

            public BypassTrackModelShadowDecision(
                bool available,
                bool shouldYield,
                string reasonCode,
                int protectedIntervalIndex,
                string risk,
                string summary,
                string localPosition,
                Entity blockerVehicle,
                string blockerPosition,
                string sequenceSummary)
            {
                Available = available;
                ShouldYield = shouldYield;
                ReasonCode = reasonCode;
                ProtectedIntervalIndex = protectedIntervalIndex;
                Risk = risk;
                Summary = summary;
                LocalPosition = localPosition;
                BlockerVehicle = blockerVehicle;
                BlockerPosition = blockerPosition;
                SequenceSummary = sequenceSummary;
            }
        }

        private readonly struct BypassTrackModelShadowEvaluation
        {
            public readonly bool Available;
            public readonly int ProtectedIntervalIndex;
            public readonly string Risk;
            public readonly string Summary;

            public BypassTrackModelShadowEvaluation(bool available, int protectedIntervalIndex, string risk, string summary)
            {
                Available = available;
                ProtectedIntervalIndex = protectedIntervalIndex;
                Risk = risk;
                Summary = summary;
            }
        }

        private readonly struct BypassTrackModelShadowSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Line;
            public readonly int CurrentWaypointIndex;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;
            public readonly BypassTrackModelShadowEvaluation Evaluation;
            public readonly BypassTrackModelShadowDecision Decision;

            public BypassTrackModelShadowSnapshot(
                uint frame,
                Entity line,
                int currentWaypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                BypassTrackModelShadowEvaluation evaluation,
                BypassTrackModelShadowDecision decision)
            {
                Frame = frame;
                Line = line;
                CurrentWaypointIndex = currentWaypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                Evaluation = evaluation;
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
            public readonly int LocalStartAtomIndex;
            public readonly int LocalEndAtomIndexExclusive;

            public SharedWindowMatchCacheKey(
                Entity localLine,
                Entity expressLine,
                int localStartAtomIndex,
                int localEndAtomIndexExclusive)
            {
                LocalLine = localLine;
                ExpressLine = expressLine;
                LocalStartAtomIndex = localStartAtomIndex;
                LocalEndAtomIndexExclusive = localEndAtomIndexExclusive;
            }

            public bool Equals(SharedWindowMatchCacheKey other)
            {
                return LocalLine == other.LocalLine
                    && ExpressLine == other.ExpressLine
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
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelCompareLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> m_SharedWindowMatchSnapshots = new Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot>();
        private readonly Dictionary<Entity, string> m_TrackModelSequenceLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, BypassTrackModelShadowEvaluation> m_BypassTrackModelShadowEvaluations = new Dictionary<Entity, BypassTrackModelShadowEvaluation>();
        private readonly Dictionary<Entity, BypassTrackModelShadowDecision> m_BypassTrackModelShadowDecisions = new Dictionary<Entity, BypassTrackModelShadowDecision>();
        private readonly Dictionary<Entity, BypassTrackModelShadowSnapshot> m_BypassTrackModelShadowSnapshots = new Dictionary<Entity, BypassTrackModelShadowSnapshot>();
        private uint m_SharedTrackIndexVersion = 1;
        private readonly HashSet<Entity> m_DirtyTrackLines = new HashSet<Entity>();
        private bool m_SharedTrackIndexDirty = true;

        private void InvalidateTrackModel(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_DirtyTrackLines.Add(line);
            m_LineTrackChains.Remove(line);
            m_SharedTrackIndexDirty = true;
        }

        private void InvalidateAllTrackModels()
        {
            m_DirtyTrackLines.Clear();
            m_LineTrackChains.Clear();
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
            m_BypassTrackModelShadowLogCache.Clear();
            m_BypassTrackModelShadowThrottleCache.Clear();
            m_BypassTrackModelShadowLastLogFrame.Clear();
            m_BypassTrackModelCompareLogCache.Clear();
            m_BypassTrackModelCompareThrottleCache.Clear();
            m_BypassTrackModelCompareLastLogFrame.Clear();
            m_SharedWindowAuditLogCache.Clear();
            m_SharedWindowMatchSnapshots.Clear();
            m_TrackModelSequenceLogCache.Clear();
            m_BypassTrackModelShadowEvaluations.Clear();
            m_BypassTrackModelShadowDecisions.Clear();
            m_BypassTrackModelShadowSnapshots.Clear();
            m_BypassHoldCadenceSnapshots.Clear();
            m_SharedTrackIndexDirty = true;
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
            if (m_LineTrackChains.TryGetValue(line, out chain)
                && chain != null
                && chain.Signature == signature)
            {
                return chain.TrackAtoms.Count > 0;
            }

            chain = BuildLineTrackChain(line, waypoints, segments, signature);
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            m_LineTrackChains[line] = chain;
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
            atom = new TrackAtom(key, element.m_Target, element.m_Flags, atomClass);
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
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null || chain == null || waypoints.Length == 0)
                return false;

            int currentWaypointIndex = -1;
            if (TryGetRouteProgress(vehicle, out int nextWaypointIndex, out _))
                currentWaypointIndex = nextWaypointIndex;

            if (currentWaypointIndex < 0 && m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex))
                currentWaypointIndex = cachedWaypointIndex;

            if (currentWaypointIndex < 0)
                currentWaypointIndex = ComputeWpIndex(vehicle, waypoints);

            if (currentWaypointIndex < 0)
                return false;

            return TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out protectedInterval);
        }

        private bool TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out string resolutionSource)
        {
            resolutionSource = "direct";
            if (TryResolveVehicleCurrentProtectedInterval(vehicle, waypoints, chain, out protectedIntervalIndex, out protectedInterval))
                return true;

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
            int bestScore = int.MinValue;
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localProtectedInterval, chain, candidate);
                int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localProtectedInterval, chain, candidate);
                if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                    || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
                {
                    continue;
                }

                TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, cursor.AtomCursorIndex, candidate);
                if (relative == TrackModelRelativeToProtectedInterval.Unknown
                    || relative == TrackModelRelativeToProtectedInterval.After)
                {
                    continue;
                }

                int entryDistanceAtoms = math.max(0, candidate.StartAtomIndex - cursor.AtomCursorIndex);
                int relativeScore = relative == TrackModelRelativeToProtectedInterval.Inside ? 2 : 1;
                int score = relativeScore * 1000000
                    + overlapCount * 1000
                    + orderedRun * 100
                    - math.min(999, entryDistanceAtoms);
                if (score <= bestScore)
                    continue;

                bestScore = score;
                protectedIntervalIndex = i;
                protectedInterval = candidate;
            }

            if (protectedIntervalIndex < 0)
                return false;

            resolutionSource = "fallback";
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
                int anchoredWaypointIndex = ComputeWpIndex(vehicle, waypoints);
                if (anchoredWaypointIndex >= 0)
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
            float confidence = currentControlEdgeIndex >= 0 ? cursor.Confidence : cursor.Confidence * 0.5f;
            runtimePosition = new TrackModelRuntimePosition(currentControlEdgeIndex, cursor.AtomCursorIndex, cursor.AtomPosition01, relative, confidence);
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

                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, localProtectedInterval, expressChain);
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
            out BypassTrackModelShadowEvaluation evaluation)
        {
            evaluation = default;
            if (!TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                return false;

            EnsureTrackChainBypassPipelineReady(chain);

            if (!TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out int protectedIntervalIndex, out BypassProtectedInterval protectedInterval))
                return false;

            if (protectedIntervalIndex < 0 || protectedIntervalIndex >= chain.ProtectedIntervalSummaries.Count)
                return false;

            ProtectedIntervalSummary summary = chain.ProtectedIntervalSummaries[protectedIntervalIndex];
            evaluation = new BypassTrackModelShadowEvaluation(
                available: true,
                protectedIntervalIndex: protectedIntervalIndex,
                risk: ClassifyProtectedIntervalShadowRisk(summary),
                summary: FormatProtectedIntervalSummary(summary, protectedInterval));
            return true;
        }

        private bool TryEvaluateBypassTrackModelShadowDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out BypassTrackModelShadowDecision shadowDecision)
        {
            shadowDecision = default;
            if (!TryGetLineTrackChain(localLine, localWaypoints, out LineTrackChain localChain))
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-chain-missing", -1, "unavailable", "trackModel[unavailable]", "pos[unknown]", Entity.Null, string.Empty, string.Empty);
                return false;
            }

            EnsureTrackChainBypassPipelineReady(localChain);

            if (!TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out int protectedIntervalIndex, out BypassProtectedInterval protectedInterval)
                || protectedIntervalIndex < 0
                || protectedIntervalIndex >= localChain.ProtectedIntervalSummaries.Count)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "protected-interval-missing", -1, "unavailable", "trackModel[unavailable]", "pos[unknown]", Entity.Null, string.Empty, string.Empty);
                return false;
            }

            ProtectedIntervalSummary localSummary = localChain.ProtectedIntervalSummaries[protectedIntervalIndex];
            string summary = FormatProtectedIntervalSummary(localSummary, protectedInterval);
            string risk = ClassifyProtectedIntervalShadowRisk(localSummary);
            Entity currentBypassBuilding = GetBypassBuildingForWaypoint(localWaypoints, currentWaypointIndex);
            float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(localChain, protectedInterval, currentBypassBuilding);
            string sequenceSummary = string.Empty;
            TryBuildBypassDecisionSequenceSummary(localVehicle, localLine, localWaypoints, currentWaypointIndex, protectedInterval, out sequenceSummary);
            string localPositionText = "pos[unknown]";
            TrackModelRuntimePosition localPosition = default;
            bool hasLocalPosition = TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out localPosition);
            if (hasLocalPosition)
                localPositionText = FormatRuntimePosition(localPosition);

            if (localSummary.SharedSegmentCount <= 0)
            {
                shadowDecision = new BypassTrackModelShadowDecision(true, false, "no-shared-protected-interval", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
                return true;
            }

            if (localSummary.HasMirroredContext)
            {
                shadowDecision = new BypassTrackModelShadowDecision(true, true, "mirrored-shared-in-protected-interval", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
                return true;
            }

            if (!hasLocalPosition)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-runtime-position-unknown", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
                return false;
            }
            if (localPosition.Confidence < 0.6f)
            {
                shadowDecision = new BypassTrackModelShadowDecision(false, false, "local-runtime-position-low-confidence", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
                return false;
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            bool sawReleaseClearedExpress = false;
            string releaseClearedExpressPosition = string.Empty;
            StringBuilder sharedWindowAudit = new StringBuilder();
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
                    if (sharedWindowAudit.Length > 0)
                        sharedWindowAudit.Append(" | ");
                    sharedWindowAudit.Append("line=").Append(expressLine.Index).Append(" chain-missing");
                    continue;
                }

                EnsureTrackChainBypassPipelineReady(expressChain);

                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, expressChain);
                if (!sharedWindowMatch.Found)
                {
                    int expressSharedRunCountForLocal = expressChain.SharedRunsByOtherLine.TryGetValue(localLine, out List<SharedTrackRun> expressRunsForLocal)
                        && expressRunsForLocal != null
                        ? expressRunsForLocal.Count
                        : 0;
                    if (sharedWindowAudit.Length > 0)
                        sharedWindowAudit.Append(" | ");
                    sharedWindowAudit.Append("line=").Append(expressLine.Index)
                        .Append(" match=none runs=").Append(expressSharedRunCountForLocal);
                    continue;
                }
                if (sharedWindowMatch.Ambiguous)
                {
                    if (sharedWindowAudit.Length > 0)
                        sharedWindowAudit.Append(" | ");
                    sharedWindowAudit.Append("line=").Append(expressLine.Index)
                        .Append(" match=ambiguous overlap=").Append(sharedWindowMatch.OverlapCount)
                        .Append(" run=").Append(sharedWindowMatch.OrderedRun);
                    LogSharedWindowAuditOnce(localVehicle, localLine, protectedIntervalIndex, sharedWindowAudit.ToString());
                    shadowDecision = new BypassTrackModelShadowDecision(false, false, "shared-window-match-ambiguous", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
                    return false;
                }

                StringBuilder lineAudit = new StringBuilder();
                lineAudit.Append("line=").Append(expressLine.Index)
                    .Append(" match=found overlap=").Append(sharedWindowMatch.OverlapCount)
                    .Append(" run=").Append(sharedWindowMatch.OrderedRun)
                    .Append(" window=").Append(sharedWindowMatch.ExpressSharedWindow.StartAtomIndex)
                    .Append("..").Append(sharedWindowMatch.ExpressSharedWindow.EndAtomIndexExclusive);

                bool sawRunningVehicle = false;
                for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = routeVehicles[rvIndex].m_Vehicle;
                    if (expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                        continue;
                    if (!m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState) || expressState != VehicleState.Running)
                        continue;
                    sawRunningVehicle = true;

                    if (!TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
                            expressVehicle,
                            expressLine,
                            expressWaypoints,
                            expressChain,
                            localChain,
                            protectedInterval,
                            out _,
                            out BypassProtectedInterval expressProtectedInterval,
                            out string intervalResolutionSource))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":protected-interval-missing");
                        continue;
                    }

                    if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":proj-fail");
                        continue;
                    }
                    if (expressPosition.Confidence < 0.6f)
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":low-conf(").Append(expressPosition.Confidence.ToString("0.00")).Append(")");
                        continue;
                    }

                    string resolutionSuffix = intervalResolutionSource == "fallback"
                        ? " src=fallback"
                        : string.Empty;

                    if (TryDescribeExpressReleaseWindowClear(
                            protectedInterval,
                            expressProtectedInterval,
                            departureReleaseCoordinate,
                            expressPosition,
                            out string releaseReason,
                            out string releasePositionText))
                    {
                        sawReleaseClearedExpress = true;
                        if (!string.IsNullOrEmpty(releasePositionText))
                            releaseClearedExpressPosition = releasePositionText + resolutionSuffix;
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(releaseReason)
                            .Append("(").Append(releasePositionText).Append(resolutionSuffix).Append(")");
                        continue;
                    }

                    if (TryEvaluateSameDirectionProtectedIntervalConflict(
                            localChain,
                            protectedInterval,
                            departureReleaseCoordinate,
                            localPosition,
                            expressChain,
                            expressProtectedInterval,
                            expressPosition,
                            out string conflictReason,
                            out string expressPositionText,
                            out string rejectReason))
                    {
                        if (!string.IsNullOrEmpty(resolutionSuffix))
                            expressPositionText += resolutionSuffix;
                        shadowDecision = new BypassTrackModelShadowDecision(true, true, conflictReason, protectedIntervalIndex, risk, summary, localPositionText, expressVehicle, expressPositionText, sequenceSummary);
                        return true;
                    }

                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(rejectReason).Append(resolutionSuffix);
                }

                if (!sawRunningVehicle)
                    lineAudit.Append(" vehicles=none-running");

                if (sharedWindowAudit.Length > 0)
                    sharedWindowAudit.Append(" | ");
                sharedWindowAudit.Append(lineAudit);
            }

            if (sawReleaseClearedExpress)
            {
                shadowDecision = new BypassTrackModelShadowDecision(true, false, "express-cleared-bypass-release-window", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, releaseClearedExpressPosition, sequenceSummary);
                return true;
            }

            LogSharedWindowAuditOnce(
                localVehicle,
                localLine,
                protectedIntervalIndex,
                sharedWindowAudit.Length > 0 ? sharedWindowAudit.ToString() : "no-express-lines-considered");
            shadowDecision = new BypassTrackModelShadowDecision(true, false, "no-express-in-shared-window", protectedIntervalIndex, risk, summary, localPositionText, Entity.Null, string.Empty, sequenceSummary);
            return true;
        }

        private bool TryEvaluateSameDirectionProtectedIntervalConflict(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            float departureReleaseCoordinate,
            TrackModelRuntimePosition localPosition,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            TrackModelRuntimePosition expressPosition,
            out string reason,
            out string expressPositionText,
            out string rejectReason)
        {
            reason = string.Empty;
            expressPositionText = FormatRuntimePosition(expressPosition);
            rejectReason = string.Empty;

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After)
            {
                rejectReason = "express-after-window";
                return false;
            }

            int overlapCount = CountProtectedIntervalPhysicalOverlap(localChain, localProtectedInterval, expressChain, expressProtectedInterval);
            int orderedRun = ComputeProtectedIntervalLongestPhysicalOrderedRun(localChain, localProtectedInterval, expressChain, expressProtectedInterval);
            if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
            {
                rejectReason = "weak-physical-overlap overlap=" + overlapCount + " run=" + orderedRun;
                return false;
            }

            float intervalDisplayLength = GetProtectedIntervalDisplayLength(localProtectedInterval);
            float localCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinate(localPosition, localProtectedInterval, includeApproachers: true, out bool includeLocal);
            float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinate(
                expressPosition,
                expressProtectedInterval,
                intervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);
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

            if (!TryFindBestProtectedSharedInterval(localChain, localProtectedInterval, expressChain, expressProtectedInterval, out ProtectedSharedInterval localSharedInterval, out ProtectedSharedInterval expressSharedInterval))
            {
                rejectReason = "no-current-shared-subinterval";
                return false;
            }

            if (!TryBuildConflictCorridor(localChain, localProtectedInterval, localSharedInterval, out ConflictCorridor localCorridor)
                || !TryBuildConflictCorridor(expressChain, expressProtectedInterval, expressSharedInterval, out ConflictCorridor expressCorridor))
            {
                rejectReason = "corridor-build-failed";
                return false;
            }

            float localClearFrames = EstimateRuntimeFramesToAtomBoundary(localChain, localPosition, localCorridor.EndAtomIndexExclusive);
            float expressEntryFrames = EstimateRuntimeFramesToAtomBoundary(expressChain, expressPosition, expressCorridor.StartAtomIndex);
            float safetyGapFrames = TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            string etaWindowText = " localPs=p" + localSharedInterval.ProtectedIntervalIndex
                + "/e" + localSharedInterval.ControlEdgeIndex
                + "/a" + localSharedInterval.StartAtomIndex + ".." + localSharedInterval.EndAtomIndexExclusive
                + " expressPs=p" + expressSharedInterval.ProtectedIntervalIndex
                + "/e" + expressSharedInterval.ControlEdgeIndex
                + "/a" + expressSharedInterval.StartAtomIndex + ".." + expressSharedInterval.EndAtomIndexExclusive;
            etaWindowText += " " + FormatConflictCorridor(localCorridor)
                + " " + FormatConflictCorridor(expressCorridor);
            expressPositionText += " etaEntry=" + FormatEtaFrames(expressEntryFrames)
                + " localClear=" + FormatEtaFrames(localClearFrames)
                + " gap=" + FormatEtaFrames(safetyGapFrames)
                + etaWindowText;

            if (expressEntryFrames == float.MaxValue || localClearFrames == float.MaxValue)
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

            reason = "same-direction-shared-express-approaching";
            expressPositionText += " overlap=" + overlapCount + " run=" + orderedRun
                + " release=" + departureReleaseCoordinate.ToString("0.00")
                + " mapped=" + expressCoordinate.ToString("0.00")
                + " local=" + localCoordinate.ToString("0.00");
            return true;
        }

        private bool TryDescribeExpressReleaseWindowClear(
            BypassProtectedInterval localProtectedInterval,
            BypassProtectedInterval expressProtectedInterval,
            float departureReleaseCoordinate,
            TrackModelRuntimePosition expressPosition,
            out string reason,
            out string expressPositionText)
        {
            reason = string.Empty;
            expressPositionText = string.Empty;

            float intervalDisplayLength = GetProtectedIntervalDisplayLength(localProtectedInterval);
            float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinate(
                expressPosition,
                expressProtectedInterval,
                intervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);
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
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            out ProtectedSharedInterval localSharedInterval,
            out ProtectedSharedInterval expressSharedInterval)
        {
            localSharedInterval = default;
            expressSharedInterval = default;

            int bestScore = 0;
            bool found = false;
            for (int localIndex = 0; localIndex < localChain.ProtectedSharedIntervals.Count; localIndex++)
            {
                ProtectedSharedInterval localCandidate = localChain.ProtectedSharedIntervals[localIndex];
                int localWindowOverlap = CountAtomIntervalOverlap(
                    localCandidate.StartAtomIndex,
                    localCandidate.EndAtomIndexExclusive,
                    localProtectedInterval.StartAtomIndex,
                    localProtectedInterval.EndAtomIndexExclusive);
                if (localWindowOverlap <= 0)
                    continue;

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

                    int score = physicalOverlap * 10000 + localWindowOverlap * 100 + expressWindowOverlap;
                    if (score <= 0 || score < bestScore)
                        continue;

                    bestScore = score;
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

            for (int i = anchorIndex - 1; i >= 0; i--)
            {
                ProtectedSharedInterval candidate = intervals[i];
                int gapAtoms = math.max(0, mergedStart - candidate.EndAtomIndexExclusive);
                if (gapAtoms > MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
                    break;

                mergedStart = candidate.StartAtomIndex;
                bridgedGapAtoms += gapAtoms;
                sharedSliceCount++;
            }

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
            if (prefixGapAtoms <= MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
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

        private static BypassProtectedInterval BuildAtomWindowInterval(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return default;

            startAtomIndex = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            endAtomIndexExclusive = math.clamp(endAtomIndexExclusive, startAtomIndex + 1, chain.TrackAtoms.Count);

            int startControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, startAtomIndex);
            int endControlEdgeIndexInclusive = ResolveControlEdgeIndexForAtom(chain, math.max(startAtomIndex, endAtomIndexExclusive - 1));
            if (startControlEdgeIndex < 0 || endControlEdgeIndexInclusive < startControlEdgeIndex)
                return default;

            float baseFrames = 0f;
            for (int controlEdgeIndex = startControlEdgeIndex; controlEdgeIndex <= endControlEdgeIndexInclusive && controlEdgeIndex < chain.ControlEdges.Count; controlEdgeIndex++)
            {
                ControlEdge edge = chain.ControlEdges[controlEdgeIndex];
                int overlapStart = math.max(edge.StartAtomIndex, startAtomIndex);
                int overlapEndExclusive = math.min(edge.EndAtomIndexExclusive, endAtomIndexExclusive);
                if (overlapEndExclusive <= overlapStart)
                    continue;

                int edgeAtomLength = math.max(1, edge.EndAtomIndexExclusive - edge.StartAtomIndex);
                int overlapAtomLength = overlapEndExclusive - overlapStart;
                baseFrames += edge.BaseFrames * (overlapAtomLength / (float)edgeAtomLength);
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

            PhysicalSharedWindowMatch match = FindBestPhysicalSharedWindow(localChain, localProtectedInterval, expressChain);
            m_SharedWindowMatchSnapshots[key] = new SharedWindowMatchSnapshot(
                nowFrame,
                m_SharedTrackIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                match);
            return match;
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
            if (currentControlEdgeIndex < 0 || currentControlEdgeIndex >= chain.ControlEdges.Count)
                return float.MaxValue;

            int fromAtomIndex = math.clamp(runtimePosition.CurrentAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int toAtomIndexExclusive = math.clamp(targetAtomIndexExclusive, fromAtomIndex + 1, chain.TrackAtoms.Count);
            float frames = EstimateFramesBetweenAtoms(chain, currentControlEdgeIndex, chain.ControlEdges.Count - 1, fromAtomIndex, toAtomIndexExclusive);
            ControlEdge currentEdge = chain.ControlEdges[currentControlEdgeIndex];
            int edgeAtomLength = math.max(1, currentEdge.EndAtomIndexExclusive - currentEdge.StartAtomIndex);
            float consumedFrames = (currentEdge.BaseFrames / edgeAtomLength) * math.saturate(runtimePosition.AtomPosition01);
            return math.max(0f, frames - consumedFrames);
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
            if (!TryEvaluateBypassTrackModelShadow(line, waypoints, currentWaypointIndex, out BypassTrackModelShadowEvaluation evaluation))
                return false;

            summary = evaluation.Summary;
            return true;
        }

        private void LogSharedWindowAuditOnce(
            Entity localVehicle,
            Entity localLine,
            int protectedIntervalIndex,
            string auditSummary)
        {
            if (localVehicle == Entity.Null || string.IsNullOrWhiteSpace(auditSummary))
                return;

            string key = localLine.Index + "|" + protectedIntervalIndex + "|" + auditSummary;
            if (m_SharedWindowAuditLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_SharedWindowAuditLogCache[localVehicle] = key;
            log.Info("[SharedWindowAudit] localVehicle=" + localVehicle.Index
                + " localLine=" + localLine.Index
                + " protectedInterval=" + protectedIntervalIndex
                + " " + auditSummary);
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
            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            StringBuilder sharedWindowAudit = new StringBuilder();
            float departureReleaseCoordinate = ComputeForwardDepartureReleaseCoordinate(localChain, protectedInterval, currentBypassBuilding);
            if (!TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                return "local=proj-fail";
            if (localPosition.Confidence < 0.6f)
                return "local=low-conf(" + localPosition.Confidence.ToString("0.00") + ")";

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
                    if (sharedWindowAudit.Length > 0)
                        sharedWindowAudit.Append(" | ");
                    sharedWindowAudit.Append("line=").Append(expressLine.Index).Append(" chain-missing");
                    continue;
                }

                EnsureTrackChainBypassPipelineReady(expressChain);

                PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(localChain, protectedInterval, expressChain);
                if (!sharedWindowMatch.Found)
                {
                    int expressSharedRunCountForLocal = expressChain.SharedRunsByOtherLine.TryGetValue(localLine, out List<SharedTrackRun> expressRunsForLocal)
                        && expressRunsForLocal != null
                        ? expressRunsForLocal.Count
                        : 0;
                    if (sharedWindowAudit.Length > 0)
                        sharedWindowAudit.Append(" | ");
                    sharedWindowAudit.Append("line=").Append(expressLine.Index)
                        .Append(" match=none runs=").Append(expressSharedRunCountForLocal);
                    continue;
                }

                if (sharedWindowAudit.Length > 0)
                    sharedWindowAudit.Append(" | ");

                if (sharedWindowMatch.Ambiguous)
                {
                    sharedWindowAudit.Append("line=").Append(expressLine.Index)
                        .Append(" match=ambiguous overlap=").Append(sharedWindowMatch.OverlapCount)
                        .Append(" run=").Append(sharedWindowMatch.OrderedRun);
                    continue;
                }

                StringBuilder lineAudit = new StringBuilder();
                lineAudit.Append("line=").Append(expressLine.Index)
                    .Append(" match=found overlap=").Append(sharedWindowMatch.OverlapCount)
                    .Append(" run=").Append(sharedWindowMatch.OrderedRun)
                    .Append(" window=").Append(sharedWindowMatch.ExpressSharedWindow.StartAtomIndex)
                    .Append("..").Append(sharedWindowMatch.ExpressSharedWindow.EndAtomIndexExclusive);

                bool sawRunningVehicle = false;
                for (int rvIndex = 0; rvIndex < routeVehicles.Length; rvIndex++)
                {
                    Entity expressVehicle = routeVehicles[rvIndex].m_Vehicle;
                    if (expressVehicle == localVehicle || !EntityManager.Exists(expressVehicle))
                        continue;
                    if (!m_VehicleState.TryGetValue(expressVehicle, out VehicleState expressState) || expressState != VehicleState.Running)
                        continue;
                    sawRunningVehicle = true;

                    if (!TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
                            expressVehicle,
                            expressLine,
                            expressWaypoints,
                            expressChain,
                            localChain,
                            protectedInterval,
                            out _,
                            out BypassProtectedInterval expressProtectedInterval,
                            out string intervalResolutionSource))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":protected-interval-missing");
                        continue;
                    }

                    if (!TryProjectTrackModelRuntimePosition(expressVehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":proj-fail");
                        continue;
                    }

                    if (expressPosition.Confidence < 0.6f)
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":low-conf(").Append(expressPosition.Confidence.ToString("0.00")).Append(")");
                        continue;
                    }

                    string resolutionSuffix = intervalResolutionSource == "fallback"
                        ? " src=fallback"
                        : string.Empty;

                    if (TryDescribeExpressReleaseWindowClear(
                            protectedInterval,
                            expressProtectedInterval,
                            departureReleaseCoordinate,
                            expressPosition,
                            out string releaseReason,
                            out string releasePositionText))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(releaseReason)
                            .Append("(").Append(releasePositionText).Append(resolutionSuffix).Append(")");
                        continue;
                    }

                    if (TryEvaluateSameDirectionProtectedIntervalConflict(
                            localChain,
                            protectedInterval,
                            departureReleaseCoordinate,
                            localPosition,
                            expressChain,
                            expressProtectedInterval,
                            expressPosition,
                            out string conflictReason,
                            out string expressPositionText,
                            out string rejectReason))
                    {
                        lineAudit.Append(" #").Append(expressVehicle.Index).Append(":block(")
                            .Append(conflictReason);
                        if (!string.IsNullOrEmpty(expressPositionText))
                            lineAudit.Append(" ").Append(expressPositionText);
                        if (!string.IsNullOrEmpty(resolutionSuffix))
                            lineAudit.Append(resolutionSuffix);
                        lineAudit.Append(")");
                        continue;
                    }

                    lineAudit.Append(" #").Append(expressVehicle.Index).Append(":").Append(rejectReason).Append(resolutionSuffix);
                }

                if (!sawRunningVehicle)
                    lineAudit.Append(" vehicles=none-running");

                sharedWindowAudit.Append(lineAudit);
            }

            return sharedWindowAudit.Length > 0 ? sharedWindowAudit.ToString() : "no-express-lines-considered";
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
                out BypassTrackModelShadowEvaluation evaluation,
                out BypassTrackModelShadowDecision shadowDecision);

            bool hasSummary = evaluation.Available;
            string summary = hasSummary ? evaluation.Summary : string.Empty;
            bool hasDecision = shadowDecision.Available;
            string decisionSequence = hasDecision ? shadowDecision.SequenceSummary : string.Empty;
            uint nowFrame = m_SimulationSystem.frameIndex;
            string coarseKey = (hasDecision ? (shadowDecision.ShouldYield ? "Y" : "N") : "U")
                + "|"
                + (hasDecision ? shadowDecision.ReasonCode : "decision-unavailable")
                + "|"
                + (hasSummary ? evaluation.Risk : "unavailable")
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

            string key = (hasSummary ? summary : "trackModel[unavailable]")
                + "|"
                + (hasSummary ? evaluation.Risk : "unavailable")
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
                + decisionSequence;
            if (m_BypassTrackModelShadowLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelShadowLogCache[localVehicle] = key;
            string shadowRisk = hasSummary ? evaluation.Risk : "unavailable";
            m_BypassTrackModelShadowEvaluations[localVehicle] = hasSummary
                ? evaluation
                : new BypassTrackModelShadowEvaluation(false, -1, "unavailable", "trackModel[unavailable]");
            m_BypassTrackModelShadowDecisions[localVehicle] = hasDecision
                ? shadowDecision
                : new BypassTrackModelShadowDecision(false, false, "decision-unavailable", -1, shadowRisk, hasSummary ? summary : "trackModel[unavailable]", "pos[unknown]", Entity.Null, string.Empty, string.Empty);

            log.Info("[BypassTrackModelShadow] vehicle=" + localVehicle.Index
                + " line=" + localLine.Index
                + " current=" + currentBypassBuilding.Index
                + " next=" + nextBypassBuilding.Index
                + " risk=" + shadowRisk
                + " shadow=" + (hasDecision ? (shadowDecision.ShouldYield ? "yield" : "pass") : "unavailable")
                + " reason=" + (hasDecision ? shadowDecision.ReasonCode : "decision-unavailable")
                + " local=" + (hasDecision ? shadowDecision.LocalPosition : "pos[unknown]")
                + " blocker=" + (hasDecision ? shadowDecision.BlockerPosition : string.Empty)
                + (!string.IsNullOrEmpty(decisionSequence) ? " " + decisionSequence : string.Empty)
                + " " + (hasSummary ? summary : "trackModel[unavailable]"));
        }

        private void EnsureBypassTrackModelShadowSnapshotCurrent(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out BypassTrackModelShadowEvaluation evaluation,
            out BypassTrackModelShadowDecision shadowDecision)
        {
            evaluation = new BypassTrackModelShadowEvaluation(false, -1, "unavailable", "trackModel[unavailable]");
            shadowDecision = new BypassTrackModelShadowDecision(false, false, "decision-unavailable", -1, "unavailable", "trackModel[unavailable]", "pos[unknown]", Entity.Null, string.Empty, string.Empty);

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
                evaluation = snapshot.Evaluation;
                shadowDecision = snapshot.Decision;
                return;
            }

            TryEvaluateBypassTrackModelShadow(localLine, localWaypoints, currentWaypointIndex, out evaluation);
            TryEvaluateBypassTrackModelShadowDecision(localVehicle, localLine, localWaypoints, currentWaypointIndex, nowFrame, out shadowDecision);

            m_BypassTrackModelShadowSnapshots[localVehicle] = new BypassTrackModelShadowSnapshot(
                nowFrame,
                localLine,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding,
                evaluation,
                shadowDecision);
            m_BypassTrackModelShadowEvaluations[localVehicle] = evaluation;
            m_BypassTrackModelShadowDecisions[localVehicle] = shadowDecision;
        }

        private string GetBypassTrackModelDecisionShadowSuffix(Entity localVehicle, bool shouldYield)
        {
            if (localVehicle == Entity.Null
                || !m_BypassTrackModelShadowEvaluations.TryGetValue(localVehicle, out BypassTrackModelShadowEvaluation evaluation))
            {
                return string.Empty;
            }

            string shadowDecision = evaluation.Risk == "none" ? "pass" : "yield";
            string alignment = shadowDecision == (shouldYield ? "yield" : "pass") ? "match" : "diff";
            return " shadow=" + shadowDecision + "/" + evaluation.Risk + "/" + alignment;
        }

        private void LogBypassTrackModelDecisionComparison(Entity localVehicle, bool shouldYield)
        {
            if (localVehicle == Entity.Null
                || !m_BypassTrackModelShadowDecisions.TryGetValue(localVehicle, out BypassTrackModelShadowDecision decision))
            {
                return;
            }

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
                + decision.Risk
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

            string key = liveDecision
                + "|"
                + shadowDecision
                + "|"
                + decision.ReasonCode
                + "|"
                + decision.Risk
                + "|"
                + decision.ProtectedIntervalIndex
                + "|"
                + decision.BlockerVehicle.Index
                + "|"
                + decision.LocalPosition
                + "|"
                + decision.BlockerPosition
                + "|"
                + decision.SequenceSummary;
            if (m_BypassTrackModelCompareLogCache.TryGetValue(localVehicle, out string previous) && previous == key)
                return;

            m_BypassTrackModelCompareLogCache[localVehicle] = key;
            log.Info("[BypassTrackModelCompare] vehicle=" + localVehicle.Index
                + " live=" + liveDecision
                + " shadow=" + shadowDecision
                + " reason=" + decision.ReasonCode
                + " risk=" + decision.Risk
                + " compare=" + alignment
                + " protectedInterval=" + decision.ProtectedIntervalIndex
                + " local=" + decision.LocalPosition
                + " blocker=" + decision.BlockerPosition
                + (!string.IsNullOrEmpty(decision.SequenceSummary) ? " " + decision.SequenceSummary : string.Empty)
                + " " + decision.Summary);
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
            if (localVehicle == Entity.Null
                || !m_BypassTrackModelShadowDecisions.TryGetValue(localVehicle, out BypassTrackModelShadowDecision decision)
                || !decision.Available)
            {
                return false;
            }

            shouldYield = decision.ShouldYield;
            reason = "track-model-" + decision.ReasonCode;
            blockerVehicle = decision.BlockerVehicle;
            return true;
        }

        private string GetTrackModelLiveDecisionLogSuffix(Entity localVehicle)
        {
            if (localVehicle == Entity.Null
                || !m_BypassTrackModelShadowDecisions.TryGetValue(localVehicle, out BypassTrackModelShadowDecision decision)
                || !decision.Available)
            {
                return string.Empty;
            }

            return " localEta=" + decision.LocalPosition
                + (!string.IsNullOrEmpty(decision.BlockerPosition) ? " expressEta=" + decision.BlockerPosition : string.Empty)
                + " track=" + decision.Summary;
        }

        private bool ShouldShadowVetoLiveBypassYield(Entity localVehicle, out string shadowReason)
        {
            shadowReason = string.Empty;
            if (localVehicle == Entity.Null
                || !m_BypassTrackModelShadowDecisions.TryGetValue(localVehicle, out BypassTrackModelShadowDecision decision)
                || !decision.Available
                || decision.ShouldYield
                || string.Equals(decision.LocalPosition, "pos[unknown]", StringComparison.Ordinal))
            {
                return false;
            }

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

        private static int CountProtectedIntervalPhysicalOverlap(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain,
            BypassProtectedInterval candidateInterval)
        {
            if (sourceChain == null || candidateChain == null)
                return 0;

            HashSet<Entity> sourceKeys = new HashSet<Entity>();
            for (int atomIndex = sourceInterval.StartAtomIndex; atomIndex < sourceInterval.EndAtomIndexExclusive && atomIndex < sourceChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = sourceChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                sourceKeys.Add(atom.Key.PhysicalLaneKey);
            }

            if (sourceKeys.Count == 0)
                return 0;

            int overlapCount = 0;
            HashSet<Entity> matchedKeys = new HashSet<Entity>();
            for (int atomIndex = candidateInterval.StartAtomIndex; atomIndex < candidateInterval.EndAtomIndexExclusive && atomIndex < candidateChain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = candidateChain.TrackAtoms[atomIndex];
                if (!ShouldIncludeIntervalAtom(atom))
                    continue;
                Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                if (sourceKeys.Contains(physicalLaneKey) && matchedKeys.Add(physicalLaneKey))
                    overlapCount++;
            }

            return overlapCount;
        }

        private static int ComputeProtectedIntervalLongestPhysicalOrderedRun(
            LineTrackChain sourceChain,
            BypassProtectedInterval sourceInterval,
            LineTrackChain candidateChain,
            BypassProtectedInterval candidateInterval)
        {
            List<Entity> sourceKeys = new List<Entity>();
            List<Entity> candidateKeys = new List<Entity>();
            CollectProtectedIntervalPhysicalLaneKeys(sourceChain, sourceInterval, sourceKeys);
            CollectProtectedIntervalPhysicalLaneKeys(candidateChain, candidateInterval, candidateKeys);
            if (sourceKeys.Count == 0 || candidateKeys.Count == 0)
                return 0;

            int bestRun = 0;
            for (int sourceIndex = 0; sourceIndex < sourceKeys.Count; sourceIndex++)
            {
                for (int candidateIndex = 0; candidateIndex < candidateKeys.Count; candidateIndex++)
                {
                    int run = 0;
                    while (sourceIndex + run < sourceKeys.Count
                        && candidateIndex + run < candidateKeys.Count
                        && sourceKeys[sourceIndex + run] == candidateKeys[candidateIndex + run])
                    {
                        run++;
                    }

                    if (run > bestRun)
                        bestRun = run;
                }
            }

            return bestRun;
        }

        private static ProtectedIntervalMatch FindBestMatchingProtectedInterval(
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
