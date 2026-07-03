using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal sealed class SharedIndex
    {
        private readonly TrackSupport m_Support;
        private readonly TrackBuild m_Build;
        private readonly TrackModelBuilder m_Index = new TrackModelBuilder();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapSourceKeys = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapMatchedKeys = new HashSet<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedSourceKeys = new List<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedCandidateKeys = new List<Entity>();
        private readonly List<int> m_ProtectedIntervalOrderedSourceAtomIndices = new List<int>();
        private readonly List<int> m_ProtectedIntervalOrderedCandidateAtomIndices = new List<int>();
        private delegate bool SharedPhysicalContextResolver(Entity line, TrackAtom atom, out int sharedLineCount, out bool mirroredContext);
        private delegate bool SharedPhysicalForLineResolver(Entity line, TrackAtom atom, Entity otherLine, out bool mirroredContext);

        internal SharedIndex(TrackSupport support, TrackBuild build)
        {
            m_Support = support;
            m_Build = build;
        }

        private EntityManager EntityManager => m_Support.EntityManager;
        internal uint Version => m_Index.Version();
        internal IReadOnlyDictionary<TrackAtomKey, List<SharedTrackOccurrence>> Track => m_Index.Track;

        internal void Clear() => m_Index.Clear();
        internal void MarkDirty() => m_Index.MarkDirty();
        internal bool TryTrack(TrackAtomKey key, out List<SharedTrackOccurrence> occurrences) => m_Index.TryTrack(key, out occurrences);
        internal bool TryPhysical(Entity physicalLane, out List<SharedPhysicalOccurrence> occurrences) => m_Index.TryPhysical(physicalLane, out occurrences);

        private void RebuildSharedTrackIndex()
        {
            RebuildShared();
        }

        private void RebuildShared()
        {
            m_Index.Track.Clear();
            m_Index.Physical.Clear();
            RebuildSharedInto(m_Index, _ => true);

            m_Index.ClearDirty();
            m_Index.Bump();
        }

        private void RebuildSharedInto(
            TrackModelBuilder target,
            Func<KeyValuePair<string, AppliedLine>, bool> include)
        {
            foreach (KeyValuePair<string, AppliedLine> entry in m_Support.AppliedLines)
            {
                if (include != null && !include(entry))
                    continue;

                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                if (!m_Build.TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
                    continue;

                for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
                {
                    TrackAtom atom = chain.TrackAtoms[atomIndex];
                    if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                        continue;

                    if (!target.Track.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences))
                    {
                        occurrences = new List<SharedTrackOccurrence>();
                        target.Track[atom.Key] = occurrences;
                    }

                    int waypointSegmentIndex = ResolveWaypointSegmentIndex(chain, atomIndex);
                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, waypointSegmentIndex));

                    Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                    if (!target.Physical.TryGetValue(physicalLaneKey, out List<SharedPhysicalOccurrence> physicalOccurrences))
                    {
                        physicalOccurrences = new List<SharedPhysicalOccurrence>();
                        target.Physical[physicalLaneKey] = physicalOccurrences;
                    }

                    physicalOccurrences.Add(new SharedPhysicalOccurrence(
                        line,
                        atomIndex,
                        waypointSegmentIndex,
                        atom.Key.PreviousTarget,
                        atom.Key.NextTarget));
                }
            }
        }

        internal void EnsureSharedTrackIndexCurrent()
        {
            if (m_Index.Dirty())
                RebuildShared();
        }

        internal uint BuildScopedSharedTrackIndex(ModeScope scope, TrackModelBuilder scopedIndex)
        {
            if (scopedIndex == null)
                return 0;

            scopedIndex.Track.Clear();
            scopedIndex.Physical.Clear();
            RebuildSharedInto(scopedIndex, entry => MatchesScope(entry, scope));
            scopedIndex.ClearDirty();
            scopedIndex.Bump();
            return scopedIndex.Version();
        }

        private bool MatchesScope(KeyValuePair<string, AppliedLine> entry, ModeScope scope)
        {
            string lineId = entry.Key ?? string.Empty;
            if (string.IsNullOrWhiteSpace(lineId))
                return false;

            if (LineKey.TryParse(lineId, out LineKey key))
                return key.Mode == scope.Mode;

            Entity line = entry.Value?.LineEntity ?? Entity.Null;
            TransitMode resolvedMode = TransportModeResolver.Resolve(EntityManager, line);
            return resolvedMode != TransitMode.Unknown && resolvedMode == scope.Mode;
        }

        internal static int ResolveWaypointSegmentIndex(LineTrackChain chain, int atomIndex)
        {
            for (int segmentIndex = 0; segmentIndex < chain.SegmentRanges.Count; segmentIndex++)
            {
                TrackSegmentRange range = chain.SegmentRanges[segmentIndex];
                if (atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive)
                    return segmentIndex;
            }

            return -1;
        }

        internal void RefreshSharedRuns(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            RefreshSharedRunsCore(
                chain,
                m_Index,
                Version,
                TryGetSharedPhysicalContext,
                TryGetSharedPhysicalContextForLine);
        }

        internal void RefreshSharedRuns(LineTrackChain chain, TrackModelBuilder scopedIndex, uint scopedVersion)
        {
            if (chain == null || scopedIndex == null || scopedVersion == 0)
                return;

            RefreshSharedRunsCore(
                chain,
                scopedIndex,
                scopedVersion,
                (Entity line, TrackAtom atom, out int sharedLineCount, out bool mirroredContext) =>
                    TryGetSharedPhysicalContext(scopedIndex, line, atom, out sharedLineCount, out mirroredContext),
                (Entity line, TrackAtom atom, Entity otherLine, out bool mirroredContext) =>
                    TryGetSharedPhysicalContextForLine(scopedIndex, line, atom, otherLine, out mirroredContext));
        }

        private void RefreshSharedRunsCore(
            LineTrackChain chain,
            TrackModelBuilder index,
            uint version,
            SharedPhysicalContextResolver sharedContext,
            SharedPhysicalForLineResolver sharedForLine)
        {
            if (chain.SharedRunsVersion == version)
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
                    || !sharedContext(chain.LineEntity, atom, out int sharedLineCount, out bool mirroredContext))
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

            RefreshSharedRunsByOtherLine(chain, index, sharedForLine);
            chain.SharedRunsVersion = version;
        }

        private void RefreshSharedRunsByOtherLine(
            LineTrackChain chain,
            TrackModelBuilder index,
            SharedPhysicalForLineResolver sharedForLine)
        {
            if (chain == null || chain.TrackAtoms.Count == 0)
                return;

            var candidateLines = new HashSet<Entity>();
            for (int atomIndex = 0; atomIndex < chain.TrackAtoms.Count; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                if (atom.AtomClass != TrackAtomClass.PrimaryLane)
                    continue;

                if (!index.Physical.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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
                        || !sharedForLine(chain.LineEntity, atom, otherLine, out bool mirroredContext))
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

        internal int CountIntervalPhysicalOverlap(
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

        internal int ComputeIntervalOrderedRun(
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

        internal bool TryFindOrderedRunSpan(
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

        internal ProtectedIntervalMatch FindBestMatchingProtectedInterval(
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
                int overlapCount = CountIntervalPhysicalOverlap(sourceChain, sourceInterval, candidateChain, candidateInterval);
                if (overlapCount <= 0)
                    continue;
                int orderedRun = ComputeIntervalOrderedRun(sourceChain, sourceInterval, candidateChain, candidateInterval);

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

        internal static ulong ComputeProtectedIntervalAtomSignature(LineTrackChain chain, BypassProtectedInterval interval)
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

        private static ulong MixLineSignature(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }

        private bool TryGetSharedAtomContext(Entity line, TrackAtomKey key, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;
            if (!TryTrack(key, out List<SharedTrackOccurrence> occurrences)
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
            if (TryTrack(mirroredKey, out List<SharedTrackOccurrence> mirroredOccurrences)
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

            if (!TryPhysical(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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

        private bool TryGetSharedPhysicalContext(TrackModelBuilder index, Entity line, TrackAtom atom, out int sharedLineCount, out bool mirroredContext)
        {
            sharedLineCount = 0;
            mirroredContext = false;

            if (index == null
                || !index.Physical.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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
                || !TryPhysical(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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

        private bool TryGetSharedPhysicalContextForLine(TrackModelBuilder index, Entity line, TrackAtom atom, Entity otherLine, out bool mirroredContext)
        {
            mirroredContext = false;
            if (index == null
                || otherLine == Entity.Null
                || !index.Physical.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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


    }
}
