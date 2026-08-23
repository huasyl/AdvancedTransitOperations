#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using Game.Routes;
using Game.Pathfind;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal enum TrackChangeDiagnosticResult : byte
    {
        RawSame = 0,
        RawChangedSemanticSame = 1,
        SemanticChanged = 2,
        Unavailable = 3,
        EquivalentRefresh = 4
    }

    internal readonly struct TrackChangeDiagnosticDifference
    {
        public readonly string Field;
        public readonly int SegmentIndex;
        public readonly int ElementIndex;
        public readonly string OldValue;
        public readonly string NewValue;

        internal TrackChangeDiagnosticDifference(
            string field,
            int segmentIndex,
            int elementIndex,
            string oldValue,
            string newValue)
        {
            Field = field ?? string.Empty;
            SegmentIndex = segmentIndex;
            ElementIndex = elementIndex;
            OldValue = oldValue ?? string.Empty;
            NewValue = newValue ?? string.Empty;
        }
    }

    internal readonly struct TrackChangeDiagnosticEvent
    {
        public readonly uint Frame;
        public readonly Entity Line;
        public readonly TrackChangeKind CandidateKind;
        public readonly Entity[] CandidateSegments;
        public readonly int CandidateSegmentTotal;
        public readonly bool CandidateSegmentsExact;
        public readonly bool HasOldCache;
        public readonly bool NewAvailable;
        public readonly string UnavailableReason;
        public readonly TrackChangeDiagnosticResult Result;
        public readonly ulong OldSignature;
        public readonly ulong NewSignature;
        public readonly ulong OldSemanticSignature;
        public readonly ulong NewSemanticSignature;
        public readonly int OldAtomCount;
        public readonly int NewAtomCount;
        public readonly int RawDifferenceCount;
        public readonly int SemanticDifferenceCount;
        public readonly TrackChangeDiagnosticDifference[] RawDifferences;
        public readonly TrackChangeDiagnosticDifference[] SemanticDifferences;

        internal TrackChangeDiagnosticEvent(
            uint frame,
            Entity line,
            TrackChangeCandidate candidate,
            bool hasOldCache,
            bool newAvailable,
            string unavailableReason,
            TrackChangeDiagnosticResult result,
            ulong oldSignature,
            ulong newSignature,
            ulong oldSemanticSignature,
            ulong newSemanticSignature,
            int oldAtomCount,
            int newAtomCount,
            int rawDifferenceCount,
            int semanticDifferenceCount,
            TrackChangeDiagnosticDifference[] rawDifferences,
            TrackChangeDiagnosticDifference[] semanticDifferences)
        {
            Frame = frame;
            Line = line;
            CandidateKind = candidate.Kind;
            CandidateSegments = candidate.SourceSegments ?? Array.Empty<Entity>();
            CandidateSegmentTotal = candidate.SourceSegmentTotal;
            CandidateSegmentsExact = candidate.SourceSegmentsExact;
            HasOldCache = hasOldCache;
            NewAvailable = newAvailable;
            UnavailableReason = unavailableReason ?? string.Empty;
            Result = result;
            OldSignature = oldSignature;
            NewSignature = newSignature;
            OldSemanticSignature = oldSemanticSignature;
            NewSemanticSignature = newSemanticSignature;
            OldAtomCount = oldAtomCount;
            NewAtomCount = newAtomCount;
            RawDifferenceCount = rawDifferenceCount;
            SemanticDifferenceCount = semanticDifferenceCount;
            RawDifferences = rawDifferences ?? Array.Empty<TrackChangeDiagnosticDifference>();
            SemanticDifferences = semanticDifferences ?? Array.Empty<TrackChangeDiagnosticDifference>();
        }
    }

    internal sealed class TrackChangeDiagnostics
    {
        private const int MaxRecent = 32;
        private const int MaxDifferences = 16;

        private readonly Dictionary<Entity, TrackInputSnapshot> m_Baselines =
            new Dictionary<Entity, TrackInputSnapshot>();
        private readonly TrackChangeDiagnosticEvent[] m_RecentBuffer =
            new TrackChangeDiagnosticEvent[MaxRecent];
        private int m_RecentStart;
        private int m_RecentCount;

        // The probe reads this property as m_Recent. The backing storage remains a fixed ring.
        internal TrackChangeDiagnosticEvent[] m_Recent
        {
            get
            {
                TrackChangeDiagnosticEvent[] result = new TrackChangeDiagnosticEvent[m_RecentCount];
                for (int i = 0; i < m_RecentCount; i++)
                    result[i] = m_RecentBuffer[(m_RecentStart + i) % MaxRecent];
                return result;
            }
        }

        internal TrackInputSnapshot Capture(
            Entity line,
            TransitMode mode,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            TrackBuild build,
            ulong signature)
        {
            RawWaypointSnapshot[] waypointSnapshots = new RawWaypointSnapshot[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                RawWaypointSnapshot snapshot = new RawWaypointSnapshot
                {
                    Waypoint = waypoint
                };
                if (waypoint != Entity.Null && build.EntityManager.HasComponent<RouteLane>(waypoint))
                {
                    RouteLane routeLane = build.EntityManager.GetComponentData<RouteLane>(waypoint);
                    snapshot.HasRouteLane = true;
                    snapshot.StartLaneIndex = routeLane.m_StartLane.Index;
                    snapshot.EndLaneIndex = routeLane.m_EndLane.Index;
                    snapshot.StartCurveScaled = (int)math.round(routeLane.m_StartCurvePos * 1000f);
                    snapshot.EndCurveScaled = (int)math.round(routeLane.m_EndCurvePos * 1000f);
                }

                waypointSnapshots[i] = snapshot;
            }

            RawSegmentSnapshot[] segmentSnapshots = new RawSegmentSnapshot[segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                Entity segment = segments[i].m_Segment;
                RawSegmentSnapshot snapshot = new RawSegmentSnapshot
                {
                    Segment = segment,
                    Exists = segment != Entity.Null && build.EntityManager.Exists(segment),
                    Elements = Array.Empty<RawPathElementSnapshot>()
                };
                if (snapshot.Exists)
                {
                    snapshot.HasPathInformation = build.EntityManager.HasComponent<PathInformation>(segment);
                    if (snapshot.HasPathInformation)
                    {
                        PathInformation information = build.EntityManager.GetComponentData<PathInformation>(segment);
                        snapshot.PathAvailable = information.m_Distance >= 0f;
                    }

                    bool hasPathElements = build.EntityManager.HasBuffer<PathElement>(segment);
                    if (hasPathElements
                        && snapshot.HasPathInformation
                        && snapshot.PathAvailable)
                    {
                        DynamicBuffer<PathElement> elements = build.EntityManager.GetBuffer<PathElement>(segment, true);
                        snapshot.PathComplete = elements.Length > 0;
                        if (snapshot.PathComplete)
                        {
                            RawPathElementSnapshot[] elementSnapshots = new RawPathElementSnapshot[elements.Length];
                            for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                            {
                                PathElement element = elements[elementIndex];
                                TrackAtomClass atomClass = build.ClassifyPathElementTarget(element);
                                TrackTraversalDir traversalDir = build.ResolveTraversalDirection(elements, elementIndex);
                                bool hasAtomContribution = element.m_Target != Entity.Null
                                    && atomClass != TrackAtomClass.FilteredNoise;
                                snapshot.HasTrackAtom |= hasAtomContribution;
                                elementSnapshots[elementIndex] = new RawPathElementSnapshot
                                {
                                    Target = element.m_Target,
                                    TargetDeltaXBits = math.asint(element.m_TargetDelta.x),
                                    TargetDeltaYBits = math.asint(element.m_TargetDelta.y),
                                    Flags = (int)element.m_Flags,
                                    HasAtomContribution = hasAtomContribution,
                                    AtomClass = hasAtomContribution ? atomClass : TrackAtomClass.Unknown,
                                    TraversalDir = hasAtomContribution ? traversalDir : TrackTraversalDir.Unknown
                                };
                            }

                            snapshot.Elements = elementSnapshots;
                        }
                    }
                }

                segmentSnapshots[i] = snapshot;
            }

            return new TrackInputSnapshot
            {
                Line = line,
                Mode = mode,
                Signature = signature,
                Waypoints = waypointSnapshots,
                Segments = segmentSnapshots
            };
        }

        internal void RecordBuilt(
            TrackChangeCandidate candidate,
            TrackInputSnapshot currentInput,
            LineTrackChain currentChain,
            bool hasOldCache,
            bool equivalentRefresh = false)
        {
            if (currentChain == null)
                return;

            SemanticSnapshot currentSemantic = SemanticSnapshot.Create(currentChain);
            if (!m_Baselines.TryGetValue(currentInput.Line, out TrackInputSnapshot previousInput)
                || previousInput == null)
            {
                currentInput.Semantic = currentSemantic;
                m_Baselines[currentInput.Line] = currentInput;
                return;
            }

            DifferenceCollector raw = new DifferenceCollector(MaxDifferences);
            CompareRaw(previousInput, currentInput, raw);
            if (raw.Count == 0 && previousInput.Signature == currentInput.Signature)
            {
                currentInput.Semantic = previousInput.Semantic;
                m_Baselines[currentInput.Line] = currentInput;
                return;
            }

            DifferenceCollector semantic = new DifferenceCollector(MaxDifferences);
            CompareSemantic(previousInput.Semantic, currentSemantic, semantic);
            TrackChangeDiagnosticResult result;
            if (equivalentRefresh)
                result = TrackChangeDiagnosticResult.EquivalentRefresh;
            else if (raw.Count == 0 && semantic.Count == 0)
                result = TrackChangeDiagnosticResult.RawSame;
            else if (semantic.Count == 0)
                result = TrackChangeDiagnosticResult.RawChangedSemanticSame;
            else
                result = TrackChangeDiagnosticResult.SemanticChanged;

            currentInput.Semantic = currentSemantic;
            m_Baselines[currentInput.Line] = currentInput;
            // A chunk-level candidate with no real raw change is intentionally silent.
            if (candidate.Kind == TrackChangeKind.None
                || result == TrackChangeDiagnosticResult.RawSame)
                return;

            AddRecent(new TrackChangeDiagnosticEvent(
                candidate.Frame,
                currentInput.Line,
                candidate,
                hasOldCache,
                true,
                string.Empty,
                result,
                previousInput.Signature,
                currentInput.Signature,
                previousInput.Semantic.Signature,
                currentSemantic.Signature,
                previousInput.Semantic.AtomCount,
                currentSemantic.AtomCount,
                raw.Count,
                semantic.Count,
                raw.Items.ToArray(),
                semantic.Items.ToArray()));
        }

        internal void RecordUnavailable(
            TrackChangeCandidate candidate,
            Entity line,
            bool hasOldCache,
            string reason,
            TrackInputSnapshot currentInput)
        {
            if (candidate.Kind == TrackChangeKind.None
                || !m_Baselines.TryGetValue(line, out TrackInputSnapshot previousInput)
                || previousInput == null)
            {
                return;
            }

            DifferenceCollector raw = new DifferenceCollector(MaxDifferences);
            ulong newSignature = 0UL;
            TrackChangeDiagnosticDifference[] rawDifferences = Array.Empty<TrackChangeDiagnosticDifference>();
            string effectiveReason = ResolveUnavailableReason(reason, currentInput);
            if (currentInput != null)
            {
                CompareRaw(previousInput, currentInput, raw);
                newSignature = currentInput.Signature;
                rawDifferences = raw.Items.ToArray();
            }

            AddRecent(new TrackChangeDiagnosticEvent(
                candidate.Frame,
                line,
                candidate,
                hasOldCache,
                false,
                effectiveReason,
                TrackChangeDiagnosticResult.Unavailable,
                previousInput.Signature,
                newSignature,
                previousInput.Semantic.Signature,
                0UL,
                previousInput.Semantic.AtomCount,
                0,
                raw.Count,
                0,
                rawDifferences,
                Array.Empty<TrackChangeDiagnosticDifference>()));
        }

        private static string ResolveUnavailableReason(string reason, TrackInputSnapshot input)
        {
            if (input == null)
                return reason ?? string.Empty;

            bool hasPathUnavailable = false;
            bool hasTrackAtom = false;
            for (int i = 0; i < input.Segments.Length; i++)
            {
                RawSegmentSnapshot segment = input.Segments[i];
                if (!segment.Exists)
                    return "segment-missing";
                if (!segment.PathAvailable || !segment.PathComplete)
                    hasPathUnavailable = true;
                hasTrackAtom |= segment.HasTrackAtom;
            }

            if (hasPathUnavailable)
                return "path-unavailable";
            if (!hasTrackAtom)
                return "no-track-atoms";
            return reason ?? string.Empty;
        }

        internal void RemoveLine(Entity line)
        {
            if (line != Entity.Null)
                m_Baselines.Remove(line);
        }

        internal void Clear()
        {
            m_Baselines.Clear();
            m_RecentStart = 0;
            m_RecentCount = 0;
            Array.Clear(m_RecentBuffer, 0, m_RecentBuffer.Length);
        }

        private void AddRecent(TrackChangeDiagnosticEvent value)
        {
            int index;
            if (m_RecentCount < MaxRecent)
            {
                index = (m_RecentStart + m_RecentCount) % MaxRecent;
                m_RecentCount++;
            }
            else
            {
                index = m_RecentStart;
                m_RecentStart = (m_RecentStart + 1) % MaxRecent;
            }

            m_RecentBuffer[index] = value;
        }

        private static void CompareRaw(
            TrackInputSnapshot previous,
            TrackInputSnapshot current,
            DifferenceCollector differences)
        {
            CompareValue(differences, "line", -1, -1, IndexText(previous.Line), IndexText(current.Line));
            CompareValue(differences, "mode", -1, -1, ((int)previous.Mode).ToString(), ((int)current.Mode).ToString());
            CompareValue(differences, "waypoints.count", -1, -1, previous.Waypoints.Length.ToString(), current.Waypoints.Length.ToString());
            int waypointCount = Math.Min(previous.Waypoints.Length, current.Waypoints.Length);
            for (int i = 0; i < waypointCount; i++)
            {
                RawWaypointSnapshot oldWaypoint = previous.Waypoints[i];
                RawWaypointSnapshot newWaypoint = current.Waypoints[i];
                CompareValue(differences, "waypoints.entity", -1, i, IndexText(oldWaypoint.Waypoint), IndexText(newWaypoint.Waypoint));
                CompareValue(differences, "waypoints.routeLane.present", -1, i, BoolText(oldWaypoint.HasRouteLane), BoolText(newWaypoint.HasRouteLane));
                CompareValue(differences, "waypoints.routeLane.start", -1, i, oldWaypoint.StartLaneIndex.ToString(), newWaypoint.StartLaneIndex.ToString());
                CompareValue(differences, "waypoints.routeLane.end", -1, i, oldWaypoint.EndLaneIndex.ToString(), newWaypoint.EndLaneIndex.ToString());
                CompareValue(differences, "waypoints.routeLane.startCurve", -1, i, oldWaypoint.StartCurveScaled.ToString(), newWaypoint.StartCurveScaled.ToString());
                CompareValue(differences, "waypoints.routeLane.endCurve", -1, i, oldWaypoint.EndCurveScaled.ToString(), newWaypoint.EndCurveScaled.ToString());
            }

            CompareValue(differences, "segments.count", -1, -1, previous.Segments.Length.ToString(), current.Segments.Length.ToString());
            int segmentCount = Math.Min(previous.Segments.Length, current.Segments.Length);
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                RawSegmentSnapshot oldSegment = previous.Segments[segmentIndex];
                RawSegmentSnapshot newSegment = current.Segments[segmentIndex];
                CompareValue(differences, "segments.entity", segmentIndex, -1, EntityText(oldSegment.Segment), EntityText(newSegment.Segment));
                CompareValue(differences, "segments.pathComplete", segmentIndex, -1, BoolText(oldSegment.PathComplete), BoolText(newSegment.PathComplete));
                CompareValue(differences, "segments.hasTrackAtom", segmentIndex, -1, BoolText(oldSegment.HasTrackAtom), BoolText(newSegment.HasTrackAtom));
                CompareValue(differences, "segments.elements.count", segmentIndex, -1, oldSegment.Elements.Length.ToString(), newSegment.Elements.Length.ToString());
                int elementCount = Math.Min(oldSegment.Elements.Length, newSegment.Elements.Length);
                for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
                {
                    RawPathElementSnapshot oldElement = oldSegment.Elements[elementIndex];
                    RawPathElementSnapshot newElement = newSegment.Elements[elementIndex];
                    CompareValue(differences, "path.target", segmentIndex, elementIndex, EntityText(oldElement.Target), EntityText(newElement.Target));
                    CompareValue(differences, "path.targetDelta.x", segmentIndex, elementIndex, BitsText(oldElement.TargetDeltaXBits), BitsText(newElement.TargetDeltaXBits));
                    CompareValue(differences, "path.targetDelta.y", segmentIndex, elementIndex, BitsText(oldElement.TargetDeltaYBits), BitsText(newElement.TargetDeltaYBits));
                    CompareValue(differences, "path.flags", segmentIndex, elementIndex, oldElement.Flags.ToString(), newElement.Flags.ToString());
                    CompareValue(differences, "path.atomContribution", segmentIndex, elementIndex, BoolText(oldElement.HasAtomContribution), BoolText(newElement.HasAtomContribution));
                    if (oldElement.HasAtomContribution && newElement.HasAtomContribution)
                    {
                        CompareValue(differences, "path.atomClass", segmentIndex, elementIndex, ((int)oldElement.AtomClass).ToString(), ((int)newElement.AtomClass).ToString());
                        CompareValue(differences, "path.traversalDir", segmentIndex, elementIndex, ((int)oldElement.TraversalDir).ToString(), ((int)newElement.TraversalDir).ToString());
                    }
                }
            }
        }

        private static void CompareSemantic(
            SemanticSnapshot previous,
            SemanticSnapshot current,
            DifferenceCollector differences)
        {
            CompareValue(differences, "chainComplete", -1, -1, BoolText(previous.ChainComplete), BoolText(current.ChainComplete));
            CompareValue(differences, "atoms.count", -1, -1, previous.Atoms.Length.ToString(), current.Atoms.Length.ToString());
            int atomCount = Math.Min(previous.Atoms.Length, current.Atoms.Length);
            for (int i = 0; i < atomCount; i++)
            {
                TrackAtomSnapshot oldAtom = previous.Atoms[i];
                TrackAtomSnapshot newAtom = current.Atoms[i];
                CompareValue(differences, "atoms.key.physicalLane", -1, i, EntityText(oldAtom.PhysicalLane), EntityText(newAtom.PhysicalLane));
                CompareValue(differences, "atoms.key.previousTarget", -1, i, EntityText(oldAtom.PreviousTarget), EntityText(newAtom.PreviousTarget));
                CompareValue(differences, "atoms.key.nextTarget", -1, i, EntityText(oldAtom.NextTarget), EntityText(newAtom.NextTarget));
                CompareValue(differences, "atoms.sourceTarget", -1, i, EntityText(oldAtom.SourceTarget), EntityText(newAtom.SourceTarget));
                CompareValue(differences, "atoms.targetDelta.x", -1, i, BitsText(oldAtom.TargetDeltaXBits), BitsText(newAtom.TargetDeltaXBits));
                CompareValue(differences, "atoms.targetDelta.y", -1, i, BitsText(oldAtom.TargetDeltaYBits), BitsText(newAtom.TargetDeltaYBits));
                CompareValue(differences, "atoms.sourceFlags", -1, i, oldAtom.SourceFlags.ToString(), newAtom.SourceFlags.ToString());
                CompareValue(differences, "atoms.atomClass", -1, i, ((int)oldAtom.AtomClass).ToString(), ((int)newAtom.AtomClass).ToString());
                CompareValue(differences, "atoms.traversalDir", -1, i, ((int)oldAtom.TraversalDir).ToString(), ((int)newAtom.TraversalDir).ToString());
            }

            CompareValue(differences, "turnbackBoundaries.count", -1, -1, previous.TurnbackBoundaries.Length.ToString(), current.TurnbackBoundaries.Length.ToString());
            int boundaryCount = Math.Min(previous.TurnbackBoundaries.Length, current.TurnbackBoundaries.Length);
            for (int i = 0; i < boundaryCount; i++)
            {
                TurnbackBoundarySnapshot oldBoundary = previous.TurnbackBoundaries[i];
                TurnbackBoundarySnapshot newBoundary = current.TurnbackBoundaries[i];
                CompareValue(differences, "turnbackBoundaries.atom", -1, i, oldBoundary.AtomIndex.ToString(), newBoundary.AtomIndex.ToString());
                CompareValue(differences, "turnbackBoundaries.beforeSlice", -1, i, oldBoundary.BeforeSliceIndex.ToString(), newBoundary.BeforeSliceIndex.ToString());
                CompareValue(differences, "turnbackBoundaries.afterSlice", -1, i, oldBoundary.AfterSliceIndex.ToString(), newBoundary.AfterSliceIndex.ToString());
                CompareValue(differences, "turnbackBoundaries.event", -1, i, oldBoundary.BoundaryEventIndex.ToString(), newBoundary.BoundaryEventIndex.ToString());
                CompareValue(differences, "turnbackBoundaries.learned", -1, i, BoolText(oldBoundary.IsLearned), BoolText(newBoundary.IsLearned));
                CompareValue(differences, "turnbackBoundaries.matchedAtoms", -1, i, oldBoundary.MatchedAtomCount.ToString(), newBoundary.MatchedAtomCount.ToString());
                CompareValue(differences, "turnbackBoundaries.matchedLanes", -1, i, oldBoundary.MatchedUniqueLaneCount.ToString(), newBoundary.MatchedUniqueLaneCount.ToString());
            }

            CompareValue(differences, "turnbackRegions.count", -1, -1, previous.TurnbackRegions.Length.ToString(), current.TurnbackRegions.Length.ToString());
            int regionCount = Math.Min(previous.TurnbackRegions.Length, current.TurnbackRegions.Length);
            for (int i = 0; i < regionCount; i++)
            {
                RunChartTurnbackRegionSnapshot oldRegion = previous.TurnbackRegions[i];
                RunChartTurnbackRegionSnapshot newRegion = current.TurnbackRegions[i];
                CompareValue(differences, "turnbackRegions.boundaryAtom", -1, i, oldRegion.BoundaryAtomIndex.ToString(), newRegion.BoundaryAtomIndex.ToString());
                CompareValue(differences, "turnbackRegions.startAtom", -1, i, oldRegion.StartAtomIndex.ToString(), newRegion.StartAtomIndex.ToString());
                CompareValue(differences, "turnbackRegions.endAtom", -1, i, oldRegion.EndAtomIndexExclusive.ToString(), newRegion.EndAtomIndexExclusive.ToString());
            }
        }

        private static void CompareValue(
            DifferenceCollector differences,
            string field,
            int segmentIndex,
            int elementIndex,
            string oldValue,
            string newValue)
        {
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                differences.Add(field, segmentIndex, elementIndex, oldValue, newValue);
        }

        private static string EntityText(Entity value)
        {
            return value == Entity.Null ? "null" : value.Index + ":" + value.Version;
        }

        private static string IndexText(Entity value)
        {
            return value == Entity.Null ? "null" : value.Index.ToString();
        }

        private static string BoolText(bool value) => value ? "1" : "0";

        private static string BitsText(int value) => "0x" + value.ToString("X8");

        private sealed class DifferenceCollector
        {
            internal readonly List<TrackChangeDiagnosticDifference> Items;
            internal int Count;
            private readonly int m_Limit;

            internal DifferenceCollector(int limit)
            {
                m_Limit = limit;
                Items = new List<TrackChangeDiagnosticDifference>(limit);
            }

            internal void Add(string field, int segmentIndex, int elementIndex, string oldValue, string newValue)
            {
                Count++;
                if (Items.Count < m_Limit)
                    Items.Add(new TrackChangeDiagnosticDifference(field, segmentIndex, elementIndex, oldValue, newValue));
            }
        }
    }

    internal sealed class TrackInputSnapshot
    {
        internal Entity Line;
        internal TransitMode Mode;
        internal ulong Signature;
        internal RawWaypointSnapshot[] Waypoints = Array.Empty<RawWaypointSnapshot>();
        internal RawSegmentSnapshot[] Segments = Array.Empty<RawSegmentSnapshot>();
        internal SemanticSnapshot Semantic;
    }

    internal struct RawWaypointSnapshot
    {
        internal Entity Waypoint;
        internal bool HasRouteLane;
        internal int StartLaneIndex;
        internal int EndLaneIndex;
        internal int StartCurveScaled;
        internal int EndCurveScaled;
    }

    internal struct RawSegmentSnapshot
    {
        internal Entity Segment;
        internal bool Exists;
        internal bool HasPathInformation;
        internal bool PathAvailable;
        internal bool PathComplete;
        internal bool HasTrackAtom;
        internal RawPathElementSnapshot[] Elements;

    }

    internal struct RawPathElementSnapshot
    {
        internal Entity Target;
        internal int TargetDeltaXBits;
        internal int TargetDeltaYBits;
        internal int Flags;
        internal bool HasAtomContribution;
        internal TrackAtomClass AtomClass;
        internal TrackTraversalDir TraversalDir;
    }

    internal sealed class SemanticSnapshot
    {
        internal ulong Signature;
        internal bool ChainComplete;
        internal int AtomCount;
        internal TrackAtomSnapshot[] Atoms = Array.Empty<TrackAtomSnapshot>();
        internal TurnbackBoundarySnapshot[] TurnbackBoundaries = Array.Empty<TurnbackBoundarySnapshot>();
        internal RunChartTurnbackRegionSnapshot[] TurnbackRegions = Array.Empty<RunChartTurnbackRegionSnapshot>();

        internal static SemanticSnapshot Create(LineTrackChain chain)
        {
            TrackAtomSnapshot[] atoms = new TrackAtomSnapshot[chain.TrackAtoms.Count];
            ulong signature = 1469598103934665603UL;
            signature = Mix(signature, chain.ChainComplete ? 1 : 0);
            for (int i = 0; i < chain.TrackAtoms.Count; i++)
            {
                TrackAtom atom = chain.TrackAtoms[i];
                atoms[i] = TrackAtomSnapshot.Create(atom);
                signature = Mix(signature, atoms[i]);
            }

            TurnbackBoundarySnapshot[] boundaries = new TurnbackBoundarySnapshot[chain.TurnbackBoundaries.Count];
            for (int i = 0; i < boundaries.Length; i++)
            {
                boundaries[i] = TurnbackBoundarySnapshot.Create(chain.TurnbackBoundaries[i]);
                signature = Mix(signature, boundaries[i]);
            }

            RunChartTurnbackRegionSnapshot[] regions = new RunChartTurnbackRegionSnapshot[chain.RunChartTurnbackRegions.Count];
            for (int i = 0; i < regions.Length; i++)
            {
                regions[i] = RunChartTurnbackRegionSnapshot.Create(chain.RunChartTurnbackRegions[i]);
                signature = Mix(signature, regions[i]);
            }

            return new SemanticSnapshot
            {
                Signature = signature,
                ChainComplete = chain.ChainComplete,
                AtomCount = atoms.Length,
                Atoms = atoms,
                TurnbackBoundaries = boundaries,
                TurnbackRegions = regions
            };
        }

        private static ulong Mix(ulong hash, TrackAtomSnapshot value)
        {
            hash = Mix(hash, value.PhysicalLane);
            hash = Mix(hash, value.PreviousTarget);
            hash = Mix(hash, value.NextTarget);
            hash = Mix(hash, value.SourceTarget);
            hash = Mix(hash, value.TargetDeltaXBits);
            hash = Mix(hash, value.TargetDeltaYBits);
            hash = Mix(hash, value.SourceFlags);
            hash = Mix(hash, (int)value.AtomClass);
            return Mix(hash, (int)value.TraversalDir);
        }

        private static ulong Mix(ulong hash, TurnbackBoundarySnapshot value)
        {
            hash = Mix(hash, value.AtomIndex);
            hash = Mix(hash, value.BeforeSliceIndex);
            hash = Mix(hash, value.AfterSliceIndex);
            hash = Mix(hash, value.BoundaryEventIndex);
            hash = Mix(hash, value.IsLearned ? 1 : 0);
            hash = Mix(hash, value.MatchedAtomCount);
            return Mix(hash, value.MatchedUniqueLaneCount);
        }

        private static ulong Mix(ulong hash, RunChartTurnbackRegionSnapshot value)
        {
            hash = Mix(hash, value.BoundaryAtomIndex);
            hash = Mix(hash, value.StartAtomIndex);
            return Mix(hash, value.EndAtomIndexExclusive);
        }

        private static ulong Mix(ulong hash, Entity value)
        {
            hash = Mix(hash, value.Index);
            return Mix(hash, value.Version);
        }

        private static ulong Mix(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }

        private static ulong Mix(ulong hash, string value)
        {
            string text = value ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
                hash = Mix(hash, text[i]);
            return hash;
        }
    }

    internal struct TrackAtomSnapshot
    {
        internal Entity PhysicalLane;
        internal Entity PreviousTarget;
        internal Entity NextTarget;
        internal Entity SourceTarget;
        internal int TargetDeltaXBits;
        internal int TargetDeltaYBits;
        internal int SourceFlags;
        internal TrackAtomClass AtomClass;
        internal TrackTraversalDir TraversalDir;

        internal static TrackAtomSnapshot Create(TrackAtom atom)
        {
            return new TrackAtomSnapshot
            {
                PhysicalLane = atom.Key.PhysicalLaneKey,
                PreviousTarget = atom.Key.PreviousTarget,
                NextTarget = atom.Key.NextTarget,
                SourceTarget = atom.SourceTarget,
                TargetDeltaXBits = math.asint(atom.TargetDelta.x),
                TargetDeltaYBits = math.asint(atom.TargetDelta.y),
                SourceFlags = (int)atom.SourceFlags,
                AtomClass = atom.AtomClass,
                TraversalDir = atom.TraversalDir
            };
        }
    }

    internal struct TurnbackBoundarySnapshot
    {
        internal int AtomIndex;
        internal int BeforeSliceIndex;
        internal int AfterSliceIndex;
        internal int BoundaryEventIndex;
        internal bool IsLearned;
        internal int MatchedAtomCount;
        internal int MatchedUniqueLaneCount;

        internal static TurnbackBoundarySnapshot Create(TurnbackBoundary value)
        {
            return new TurnbackBoundarySnapshot
            {
                AtomIndex = value.AtomIndex,
                BeforeSliceIndex = value.BeforeSliceIndex,
                AfterSliceIndex = value.AfterSliceIndex,
                BoundaryEventIndex = value.BoundaryEventIndex,
                IsLearned = value.IsLearned,
                MatchedAtomCount = value.MatchedAtomCount,
                MatchedUniqueLaneCount = value.MatchedUniqueLaneCount
            };
        }
    }

    internal struct RunChartTurnbackRegionSnapshot
    {
        internal int BoundaryAtomIndex;
        internal int StartAtomIndex;
        internal int EndAtomIndexExclusive;

        internal static RunChartTurnbackRegionSnapshot Create(RunChartTurnbackRegion value)
        {
            return new RunChartTurnbackRegionSnapshot
            {
                BoundaryAtomIndex = value.BoundaryAtomIndex,
                StartAtomIndex = value.StartAtomIndex,
                EndAtomIndexExclusive = value.EndAtomIndexExclusive
            };
        }
    }
}
#endif
