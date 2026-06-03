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
    internal sealed partial class TrackModelService
    {
        private void RebuildSharedTrackIndex()
        {
            RebuildShared();
        }

        private void RebuildShared()
        {
            m_Builder.Track.Clear();
            m_Builder.Physical.Clear();

            foreach (KeyValuePair<string, AppliedLine> entry in m_AppliedLines)
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

                    if (!m_Builder.Track.TryGetValue(atom.Key, out List<SharedTrackOccurrence> occurrences))
                    {
                        occurrences = new List<SharedTrackOccurrence>();
                        m_Builder.Track[atom.Key] = occurrences;
                    }

                    int waypointSegmentIndex = ResolveWaypointSegmentIndex(chain, atomIndex);
                    occurrences.Add(new SharedTrackOccurrence(line, atomIndex, waypointSegmentIndex));

                    Entity physicalLaneKey = atom.Key.PhysicalLaneKey;
                    if (!m_Builder.Physical.TryGetValue(physicalLaneKey, out List<SharedPhysicalOccurrence> physicalOccurrences))
                    {
                        physicalOccurrences = new List<SharedPhysicalOccurrence>();
                        m_Builder.Physical[physicalLaneKey] = physicalOccurrences;
                    }

                    physicalOccurrences.Add(new SharedPhysicalOccurrence(
                        line,
                        atomIndex,
                        waypointSegmentIndex,
                        atom.Key.PreviousTarget,
                        atom.Key.NextTarget));
                }
            }

            m_Builder.ClearDirty();
            m_Builder.Bump();
        }

        internal void EnsureSharedTrackIndexCurrent()
        {
            m_Coordinator.RefreshShared(RebuildShared);
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

        internal void RefreshSharedRuns(LineTrackChain chain)
        {
            if (chain == null)
                return;

            EnsureSharedTrackIndexCurrent();
            if (chain.SharedRunsVersion == SharedIndexVersion)
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
            chain.SharedRunsVersion = SharedIndexVersion;
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

                if (!m_Builder.Physical.TryGetValue(atom.Key.PhysicalLaneKey, out List<SharedPhysicalOccurrence> occurrences)
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

        internal int CountProtectedIntervalPhysicalOverlap(
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

        internal int ComputeProtectedIntervalLongestPhysicalOrderedRun(
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

        internal bool TryFindProtectedIntervalOrderedRunSpan(
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


    }
}
