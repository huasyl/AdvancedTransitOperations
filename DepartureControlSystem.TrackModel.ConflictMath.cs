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
    public partial class DispatchRuntimeSystem
    {
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


    }
}
