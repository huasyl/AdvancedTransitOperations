using System;
using System.Collections.Generic;
using Game.Pathfind;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using Unity.Mathematics;
namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineIntervalImpact
    {
        private readonly bool[] m_OldAffected;
        private readonly bool[] m_NewAffected;
        private readonly int[] m_OldToNew;
        private readonly int[] m_NewToOld;
        internal bool IsValid { get; }
        internal string FailureReason { get; }
        internal bool LayoutChanged { get; }
        internal bool StopSigChanged { get; }
        internal int AffectedOldCount { get; }
        internal int AffectedNewCount { get; }
        internal int RetainedCount { get; }
        internal int OldIntervalCount => m_OldAffected.Length;
        internal int NewIntervalCount => m_NewAffected.Length;
        private LineIntervalImpact(
            bool isValid,
            string failureReason,
            bool layoutChanged,
            bool stopSigChanged,
            bool[] oldAffected,
            bool[] newAffected,
            int[] oldToNew,
            int[] newToOld,
            int affectedOldCount,
            int affectedNewCount,
            int retainedCount)
        {
            IsValid = isValid;
            FailureReason = failureReason ?? string.Empty;
            LayoutChanged = layoutChanged;
            StopSigChanged = stopSigChanged;
            m_OldAffected = oldAffected ?? Array.Empty<bool>();
            m_NewAffected = newAffected ?? Array.Empty<bool>();
            m_OldToNew = oldToNew ?? Array.Empty<int>();
            m_NewToOld = newToOld ?? Array.Empty<int>();
            AffectedOldCount = affectedOldCount;
            AffectedNewCount = affectedNewCount;
            RetainedCount = retainedCount;
        }
        internal bool IsOldAffected(int index)
        {
            return index >= 0 && index < m_OldAffected.Length && m_OldAffected[index];
        }
        internal bool IsNewAffected(int index)
        {
            return index >= 0 && index < m_NewAffected.Length && m_NewAffected[index];
        }
        internal int OldToNew(int index)
        {
            return index >= 0 && index < m_OldToNew.Length ? m_OldToNew[index] : -1;
        }
        internal int NewToOld(int index)
        {
            return index >= 0 && index < m_NewToOld.Length ? m_NewToOld[index] : -1;
        }
        internal static LineIntervalImpact Compute(
            LineStopLayout oldLayout,
            LineStopLayout newLayout,
            LineTrackChain oldChain,
            LineTrackChain newChain,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            if (oldLayout == null || newLayout == null || oldChain == null || newChain == null)
                return Invalid("missing-input");
            if (equivalentTarget == null)
                return Invalid("missing-comparer");
            if (!ValidChain(oldLayout, oldChain) || !ValidChain(newLayout, newChain))
                return Invalid("chain-incomplete");
            bool stopSigChanged = !string.Equals(
                oldLayout.StopSig,
                newLayout.StopSig,
                StringComparison.Ordinal);
            bool layoutChanged = stopSigChanged
                || oldLayout.WaypointCount != newLayout.WaypointCount
                || !SameStops(oldLayout, newLayout);
            int oldIntervalCount = IntervalCount(oldLayout.StopCount);
            int newIntervalCount = IntervalCount(newLayout.StopCount);
            bool[] oldAffected = new bool[oldIntervalCount];
            bool[] newAffected = new bool[newIntervalCount];
            int[] oldToNew = NewMap(oldIntervalCount);
            int[] newToOld = NewMap(newIntervalCount);
            if (oldIntervalCount == 0 && newIntervalCount == 0)
            {
                return Valid(
                    layoutChanged,
                    stopSigChanged,
                    oldAffected,
                    newAffected,
                    oldToNew,
                    newToOld,
                    0,
                    0,
                    0);
            }
            Dictionary<DirectedStopPair, int> oldCounts = new Dictionary<DirectedStopPair, int>();
            Dictionary<DirectedStopPair, int> newCounts = new Dictionary<DirectedStopPair, int>();
            Dictionary<DirectedStopPair, int> newPositions = new Dictionary<DirectedStopPair, int>();
            BuildCounts(oldLayout, oldCounts);
            BuildPairs(newLayout, newCounts, newPositions);
            int retainedCount = 0;
            for (int oldIndex = 0; oldIndex < oldIntervalCount; oldIndex++)
            {
                DirectedStopPair pair = PairAt(oldLayout, oldIndex);
                if (!oldCounts.TryGetValue(pair, out int oldCount) || oldCount != 1
                    || !newCounts.TryGetValue(pair, out int newCount) || newCount != 1
                    || !newPositions.TryGetValue(pair, out int newIndex)
                    || newIndex < 0)
                {
                    oldAffected[oldIndex] = true;
                    if (newPositions.TryGetValue(pair, out int duplicateIndex)
                        && duplicateIndex >= 0)
                    {
                        newAffected[duplicateIndex] = true;
                    }
                    continue;
                }
                oldToNew[oldIndex] = newIndex;
                newToOld[newIndex] = oldIndex;
                if (!SameInterval(
                    oldLayout,
                    newLayout,
                    oldChain,
                    newChain,
                    oldIndex,
                    newIndex,
                    equivalentTarget))
                {
                    oldAffected[oldIndex] = true;
                    newAffected[newIndex] = true;
                }
                else
                {
                    retainedCount++;
                }
            }
            for (int newIndex = 0; newIndex < newIntervalCount; newIndex++)
            {
                if (newToOld[newIndex] < 0)
                    newAffected[newIndex] = true;
            }
            retainedCount += RecoverJointShifts(oldLayout, newLayout, oldChain, newChain,
                oldAffected, newAffected, oldToNew, newToOld, equivalentTarget);
            int affectedOldCount = CountAffected(oldAffected);
            int affectedNewCount = CountAffected(newAffected);
            return Valid(
                layoutChanged,
                stopSigChanged,
                oldAffected,
                newAffected,
                oldToNew,
                newToOld,
                affectedOldCount,
                affectedNewCount,
                retainedCount);
        }
        private static int RecoverJointShifts(
            LineStopLayout oldLayout, LineStopLayout newLayout,
            LineTrackChain oldChain, LineTrackChain newChain,
            bool[] oldAffected, bool[] newAffected,
            int[] oldToNew, int[] newToOld,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            int oldCount = oldAffected.Length;
            int newCount = newAffected.Length;
            if (oldCount < 2 || newCount < 2)
                return 0;
            int recovered = 0;
            for (int middle = 0; middle < oldLayout.StopCount; middle++)
            {
                int oldIncoming = (middle + oldCount - 1) % oldCount, oldOutgoing = middle;
                int newIncoming = oldToNew[oldIncoming], newOutgoing = oldToNew[oldOutgoing];
                if (!oldAffected[oldIncoming] || !oldAffected[oldOutgoing]
                    || newIncoming < 0 || newIncoming >= newCount
                    || newOutgoing < 0 || newOutgoing >= newCount
                    || !newAffected[newIncoming] || !newAffected[newOutgoing])
                    continue;
                if (newToOld[newIncoming] != oldIncoming
                    || newToOld[newOutgoing] != oldOutgoing
                    || newOutgoing != (newIncoming + 1) % newCount
                    || !string.Equals(oldLayout[middle].StopKey,
                        newLayout[newOutgoing].StopKey, StringComparison.Ordinal))
                    continue;
                if (!TryJointShift(oldLayout, newLayout, oldChain, newChain,
                    oldIncoming, oldOutgoing, newIncoming, newOutgoing, equivalentTarget))
                    continue;
                oldAffected[oldIncoming] = false;
                oldAffected[oldOutgoing] = false;
                newAffected[newIncoming] = false;
                newAffected[newOutgoing] = false;
                recovered += 2;
            }
            return recovered;
        }
        private static LineIntervalImpact Invalid(string reason)
        {
            return new LineIntervalImpact(
                false,
                reason,
                true,
                true,
                Array.Empty<bool>(),
                Array.Empty<bool>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                0,
                0,
                0);
        }
        private static LineIntervalImpact Valid(
            bool layoutChanged,
            bool stopSigChanged,
            bool[] oldAffected,
            bool[] newAffected,
            int[] oldToNew,
            int[] newToOld,
            int affectedOldCount,
            int affectedNewCount,
            int retainedCount)
        {
            return new LineIntervalImpact(
                true,
                string.Empty,
                layoutChanged,
                stopSigChanged,
                oldAffected,
                newAffected,
                oldToNew,
                newToOld,
                affectedOldCount,
                affectedNewCount,
                retainedCount);
        }
        private static int[] NewMap(int count)
        {
            int[] map = new int[count];
            for (int i = 0; i < map.Length; i++)
                map[i] = -1;
            return map;
        }
        private static int IntervalCount(int stopCount)
        {
            return stopCount >= 2 ? stopCount : 0;
        }
        private static bool ValidChain(LineStopLayout layout, LineTrackChain chain)
        {
            if (!chain.ChainComplete
                || chain.TrackAtoms == null
                || chain.TrackAtoms.Count == 0
                || chain.SegmentRanges == null
                || chain.SegmentRanges.Count != layout.WaypointCount)
            {
                return false;
            }
            int expectedStart = 0;
            for (int i = 0; i < chain.SegmentRanges.Count; i++)
            {
                TrackSegmentRange range = chain.SegmentRanges[i];
                if (range.StartAtomIndex < 0
                    || range.StartAtomIndex != expectedStart
                    || range.EndAtomIndexExclusive <= range.StartAtomIndex
                    || range.EndAtomIndexExclusive > chain.TrackAtoms.Count)
                {
                    return false;
                }
                expectedStart = range.EndAtomIndexExclusive;
            }
            return expectedStart == chain.TrackAtoms.Count;
        }
        private static bool SameStops(LineStopLayout oldLayout, LineStopLayout newLayout)
        {
            if (oldLayout.StopCount != newLayout.StopCount)
                return false;
            for (int i = 0; i < oldLayout.StopCount; i++)
            {
                LineStop oldStop = oldLayout[i];
                LineStop newStop = newLayout[i];
                if (!string.Equals(oldStop.StopKey, newStop.StopKey, StringComparison.Ordinal)
                    || oldStop.WaypointIndex != newStop.WaypointIndex)
                {
                    return false;
                }
            }
            return true;
        }
        private static void BuildPairs(
            LineStopLayout layout,
            Dictionary<DirectedStopPair, int> counts,
            Dictionary<DirectedStopPair, int> positions)
        {
            int intervalCount = IntervalCount(layout.StopCount);
            for (int i = 0; i < intervalCount; i++)
            {
                DirectedStopPair pair = PairAt(layout, i);
                counts.TryGetValue(pair, out int count);
                counts[pair] = count + 1;
                if (!positions.ContainsKey(pair))
                    positions[pair] = i;
                else
                    positions[pair] = -1;
            }
        }
        private static void BuildCounts(
            LineStopLayout layout,
            Dictionary<DirectedStopPair, int> counts)
        {
            int intervalCount = IntervalCount(layout.StopCount);
            for (int i = 0; i < intervalCount; i++)
            {
                DirectedStopPair pair = PairAt(layout, i);
                counts.TryGetValue(pair, out int count);
                counts[pair] = count + 1;
            }
        }
        private static DirectedStopPair PairAt(LineStopLayout layout, int index)
        {
            int next = index + 1;
            if (next >= layout.StopCount)
                next = 0;
            return new DirectedStopPair(layout[index].StopKey, layout[next].StopKey);
        }
        private static bool SameInterval(
            LineStopLayout oldLayout,
            LineStopLayout newLayout,
            LineTrackChain oldChain,
            LineTrackChain newChain,
            int oldIndex,
            int newIndex,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            LineStop oldStart = oldLayout[oldIndex];
            LineStop oldEnd = oldLayout[(oldIndex + 1) % oldLayout.StopCount];
            LineStop newStart = newLayout[newIndex];
            LineStop newEnd = newLayout[(newIndex + 1) % newLayout.StopCount];
            if (!TryCreateCursor(oldChain, oldStart.WaypointIndex, oldEnd.WaypointIndex, out AtomCursor oldCursor)
                || !TryCreateCursor(newChain, newStart.WaypointIndex, newEnd.WaypointIndex, out AtomCursor newCursor))
            {
                return false;
            }
            int oldCount = CountAtoms(oldCursor);
            int newCount = CountAtoms(newCursor);
            if (oldCount < 0 || newCount < 0 || oldCount != newCount)
                return false;
            oldCursor = CreateCursor(oldChain, oldStart.WaypointIndex, oldEnd.WaypointIndex);
            newCursor = CreateCursor(newChain, newStart.WaypointIndex, newEnd.WaypointIndex);
            for (int i = 0; i < oldCount; i++)
            {
                if (!oldCursor.MoveNext(out TrackAtom oldAtom)
                    || !newCursor.MoveNext(out TrackAtom newAtom)
                    || !SameAtom(
                        oldAtom,
                        newAtom,
                        equivalentTarget))
                {
                    return false;
                }
            }
            return !oldCursor.MoveNext(out _) && !newCursor.MoveNext(out _);
        }
        private static bool TryJointShift(
            LineStopLayout oldLayout, LineStopLayout newLayout,
            LineTrackChain oldChain, LineTrackChain newChain,
            int oldIncoming, int oldOutgoing,
            int newIncoming, int newOutgoing,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            int oldStart = oldLayout[oldIncoming].WaypointIndex, oldBoundary = oldLayout[oldOutgoing].WaypointIndex;
            int oldEnd = oldLayout[(oldOutgoing + 1) % oldLayout.StopCount].WaypointIndex;
            int newStart = newLayout[newIncoming].WaypointIndex, newBoundary = newLayout[newOutgoing].WaypointIndex;
            int newEnd = newLayout[(newOutgoing + 1) % newLayout.StopCount].WaypointIndex;
            if (!TryJointRange(oldChain, oldStart, oldBoundary, oldEnd,
                    out int oldLeft, out int oldRight, out bool oldZeroLeft)
                || !TryJointRange(newChain, newStart, newBoundary, newEnd,
                    out int newLeft, out int newRight, out bool newZeroLeft))
                return false;
            int oldAtomStart = oldChain.SegmentRanges[oldStart].StartAtomIndex;
            int oldAtomEnd = oldChain.SegmentRanges[oldEnd].StartAtomIndex;
            int newAtomStart = newChain.SegmentRanges[newStart].StartAtomIndex;
            int newAtomEnd = newChain.SegmentRanges[newEnd].StartAtomIndex;
            TrackAtom oldLeftAtom = oldChain.TrackAtoms[oldLeft], oldRightAtom = oldChain.TrackAtoms[oldRight];
            TrackAtom newLeftAtom = newChain.TrackAtoms[newLeft], newRightAtom = newChain.TrackAtoms[newRight];
            if (!SameJointAtom(oldLeftAtom, newLeftAtom, equivalentTarget)
                || !SameJointAtom(oldRightAtom, newRightAtom, equivalentTarget)
                || !SameAtomRange(oldChain, newChain, oldAtomStart, oldLeft, newAtomStart, newLeft, equivalentTarget)
                || !SameAtomRange(oldChain, newChain, oldRight + 1, oldAtomEnd, newRight + 1, newAtomEnd, equivalentTarget))
                return false;
            if (!TryWaypointSide(oldChain, oldBoundary, oldLeftAtom, oldRightAtom, out bool oldSide)
                || !TryWaypointSide(newChain, newBoundary, newLeftAtom, newRightAtom, out bool newSide))
                return false;
            if (oldChain.WaypointInputs[oldBoundary].Waypoint.Index
                != newChain.WaypointInputs[newBoundary].Waypoint.Index)
                return false;
            return oldSide != newSide && oldZeroLeft == oldSide
                && newZeroLeft == newSide;
        }
        private static bool TryJointRange(
            LineTrackChain chain, int startWaypoint,
            int boundaryWaypoint, int endWaypoint,
            out int left, out int right, out bool zeroOnLeft)
        {
            left = right = -1;
            zeroOnLeft = false;
            if (startWaypoint < 0 || boundaryWaypoint < 0 || endWaypoint < 0
                || startWaypoint >= chain.SegmentRanges.Count
                || boundaryWaypoint >= chain.SegmentRanges.Count
                || endWaypoint >= chain.SegmentRanges.Count)
                return false;
            int start = chain.SegmentRanges[startWaypoint].StartAtomIndex;
            int boundary = chain.SegmentRanges[boundaryWaypoint].StartAtomIndex;
            int end = chain.SegmentRanges[endWaypoint].StartAtomIndex;
            if (start < 0 || start >= boundary || boundary >= end || end > chain.TrackAtoms.Count)
                return false;
            left = boundary - 1;
            while (left >= start && math.asint(chain.TrackAtoms[left].TargetDelta.x) == math.asint(chain.TrackAtoms[left].TargetDelta.y))
                left--;
            right = boundary;
            while (right < end && math.asint(chain.TrackAtoms[right].TargetDelta.x) == math.asint(chain.TrackAtoms[right].TargetDelta.y))
                right++;
            if (left < start || right >= end || right - left - 1 != 1)
                return false;
            TrackAtom zero = chain.TrackAtoms[left + 1];
            bool attachesLeft = zero.Key.PhysicalLaneKey == chain.TrackAtoms[left].Key.PhysicalLaneKey;
            bool attachesRight = zero.Key.PhysicalLaneKey == chain.TrackAtoms[right].Key.PhysicalLaneKey;
            if (zero.SourceTarget != zero.Key.PhysicalLaneKey
                || (zero.SourceFlags & ~PathElementFlags.PathStart) != 0
                || attachesLeft == attachesRight)
                return false;
            TrackAtom attached = attachesLeft ? chain.TrackAtoms[left] : chain.TrackAtoms[right];
            if (zero.AtomClass != attached.AtomClass || zero.TraversalDir != attached.TraversalDir
                || math.asint(zero.TargetDelta.x) != math.asint(
                    attachesLeft ? attached.TargetDelta.y : attached.TargetDelta.x))
                return false;
            zeroOnLeft = attachesLeft;
            return true;
        }
        private static bool SameAtomRange(
            LineTrackChain oldChain, LineTrackChain newChain,
            int oldStart, int oldEnd, int newStart, int newEnd,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            if (oldEnd - oldStart != newEnd - newStart)
                return false;
            for (int i = 0; i < oldEnd - oldStart; i++)
            {
                if (!SameAtom(oldChain.TrackAtoms[oldStart + i], newChain.TrackAtoms[newStart + i], equivalentTarget))
                    return false;
            }
            return true;
        }
        private static bool SameJointAtom(
            TrackAtom oldAtom, TrackAtom newAtom,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            if (math.asint(oldAtom.TargetDelta.x) != math.asint(newAtom.TargetDelta.x)
                || math.asint(oldAtom.TargetDelta.y) != math.asint(newAtom.TargetDelta.y)
                || oldAtom.AtomClass != newAtom.AtomClass
                || oldAtom.TraversalDir != newAtom.TraversalDir
                || !SameTarget(oldAtom.Key.PhysicalLaneKey, newAtom.Key.PhysicalLaneKey, equivalentTarget))
                return false;
            bool oldSourceIsLane = oldAtom.SourceTarget == oldAtom.Key.PhysicalLaneKey;
            bool newSourceIsLane = newAtom.SourceTarget == newAtom.Key.PhysicalLaneKey;
            return (oldSourceIsLane && newSourceIsLane
                    || SameTarget(oldAtom.SourceTarget, newAtom.SourceTarget, equivalentTarget))
                && (oldAtom.SourceFlags & ~PathElementFlags.PathStart)
                    == (newAtom.SourceFlags & ~PathElementFlags.PathStart);
        }
        private static bool TryWaypointSide(
            LineTrackChain chain, int waypointIndex,
            TrackAtom left, TrackAtom right,
            out bool leftSide)
        {
            leftSide = false;
            if (chain.WaypointInputs == null
                || waypointIndex < 0 || waypointIndex >= chain.WaypointInputs.Length)
                return false;
            TrackWaypointInputBaseline input = chain.WaypointInputs[waypointIndex];
            if (input.Waypoint == Entity.Null
                || !input.HasRouteLane
                || input.StartLaneIndex != input.EndLaneIndex
                || input.StartCurveScaled != input.EndCurveScaled)
                return false;
            int curveLeft = (int)math.round(left.TargetDelta.y * 1000f), curveRight = (int)math.round(right.TargetDelta.x * 1000f);
            bool matchesLeft = input.StartLaneIndex == left.Key.PhysicalLaneKey.Index
                && input.StartCurveScaled == curveLeft;
            bool matchesRight = input.StartLaneIndex == right.Key.PhysicalLaneKey.Index
                && input.StartCurveScaled == curveRight;
            if (matchesLeft == matchesRight)
                return false;
            leftSide = matchesLeft;
            return true;
        }
        private static int CountAtoms(AtomCursor cursor)
        {
            int count = 0;
            while (cursor.MoveNext(out _))
                count++;
            return count;
        }
        private static bool SameAtom(
            TrackAtom oldAtom,
            TrackAtom newAtom,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            if (math.asint(oldAtom.TargetDelta.x) != math.asint(newAtom.TargetDelta.x)
                || math.asint(oldAtom.TargetDelta.y) != math.asint(newAtom.TargetDelta.y)
                || oldAtom.SourceFlags != newAtom.SourceFlags
                || oldAtom.AtomClass != newAtom.AtomClass
                || oldAtom.TraversalDir != newAtom.TraversalDir)
            {
                return false;
            }
            if (!SameTarget(oldAtom.Key.PhysicalLaneKey, newAtom.Key.PhysicalLaneKey, equivalentTarget)
                || !SameTarget(oldAtom.Key.PreviousTarget, newAtom.Key.PreviousTarget, equivalentTarget)
                || !SameTarget(oldAtom.Key.NextTarget, newAtom.Key.NextTarget, equivalentTarget))
            {
                return false;
            }
            bool oldSourceIsLane = oldAtom.SourceTarget == oldAtom.Key.PhysicalLaneKey;
            bool newSourceIsLane = newAtom.SourceTarget == newAtom.Key.PhysicalLaneKey;
            return oldSourceIsLane && newSourceIsLane
                || SameTarget(oldAtom.SourceTarget, newAtom.SourceTarget, equivalentTarget);
        }
        private static bool SameTarget(
            Entity oldTarget,
            Entity newTarget,
            Func<Entity, Entity, bool> equivalentTarget)
        {
            return oldTarget == newTarget || equivalentTarget(oldTarget, newTarget);
        }
        private static bool TryCreateCursor(
            LineTrackChain chain,
            int startSegment,
            int endSegment,
            out AtomCursor cursor)
        {
            cursor = default;
            if (chain == null
                || chain.SegmentRanges == null
                || chain.SegmentRanges.Count == 0
                || startSegment < 0
                || startSegment >= chain.SegmentRanges.Count
                || endSegment < 0
                || endSegment >= chain.SegmentRanges.Count
                || startSegment == endSegment)
            {
                return false;
            }
            cursor = CreateCursor(chain, startSegment, endSegment);
            return true;
        }
        private static AtomCursor CreateCursor(LineTrackChain chain, int startSegment, int endSegment)
        {
            int segmentCount = chain.SegmentRanges.Count;
            int segmentSteps = endSegment > startSegment
                ? endSegment - startSegment
                : segmentCount - startSegment + endSegment;
            TrackSegmentRange range = chain.SegmentRanges[startSegment];
            return new AtomCursor(
                chain,
                startSegment,
                segmentSteps,
                range.StartAtomIndex,
                range.EndAtomIndexExclusive);
        }
        private static int CountAffected(bool[] affected)
        {
            int count = 0;
            for (int i = 0; i < affected.Length; i++)
                if (affected[i])
                    count++;
            return count;
        }
        private readonly struct DirectedStopPair : IEquatable<DirectedStopPair>
        {
            private readonly string m_Source;
            private readonly string m_Target;
            internal DirectedStopPair(string source, string target)
            {
                m_Source = source ?? string.Empty;
                m_Target = target ?? string.Empty;
            }
            public bool Equals(DirectedStopPair other)
            {
                return string.Equals(m_Source, other.m_Source, StringComparison.Ordinal)
                    && string.Equals(m_Target, other.m_Target, StringComparison.Ordinal);
            }
            public override bool Equals(object obj)
            {
                return obj is DirectedStopPair other && Equals(other);
            }
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((m_Source != null ? StringComparer.Ordinal.GetHashCode(m_Source) : 0) * 397)
                        ^ (m_Target != null ? StringComparer.Ordinal.GetHashCode(m_Target) : 0);
                }
            }
        }
        private struct AtomCursor
        {
            private readonly LineTrackChain m_Chain;
            private int m_SegmentIndex;
            private int m_SegmentsRemaining;
            private int m_AtomIndex;
            private int m_SegmentEnd;
            internal AtomCursor(
                LineTrackChain chain,
                int segmentIndex,
                int segmentsRemaining,
                int atomIndex,
                int segmentEnd)
            {
                m_Chain = chain;
                m_SegmentIndex = segmentIndex;
                m_SegmentsRemaining = segmentsRemaining;
                m_AtomIndex = atomIndex;
                m_SegmentEnd = segmentEnd;
            }
            internal bool MoveNext(out TrackAtom atom)
            {
                return MoveNext(out atom, out _);
            }
            internal bool MoveNext(out TrackAtom atom, out int atomIndex)
            {
                while (m_SegmentsRemaining > 0)
                {
                    if (m_AtomIndex < m_SegmentEnd)
                    {
                        atomIndex = m_AtomIndex;
                        atom = m_Chain.TrackAtoms[m_AtomIndex++];
                        return true;
                    }
                    m_SegmentsRemaining--;
                    if (m_SegmentsRemaining == 0)
                        break;
                    m_SegmentIndex++;
                    if (m_SegmentIndex >= m_Chain.SegmentRanges.Count)
                        m_SegmentIndex = 0;
                    TrackSegmentRange range = m_Chain.SegmentRanges[m_SegmentIndex];
                    m_AtomIndex = range.StartAtomIndex;
                    m_SegmentEnd = range.EndAtomIndexExclusive;
                }
                atom = default;
                atomIndex = -1;
                return false;
            }
        }
    }
}
