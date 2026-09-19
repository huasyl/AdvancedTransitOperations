using System.Collections.Generic;
using Game.Pathfind;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackProjection
{
    internal enum CurrentLaneMatchState : byte
    {
        None = 0,
        Unique = 1,
        Ambiguous = 2,
        ZeroParameterSpan = 3,
    }


    internal static class CurrentLaneMatcher
    {
        private const float EndpointTolerance = 1e-5f;

        internal static CurrentLaneMatchState SelectInitial(
            LineTrackChain chain,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            List<int> candidates,
            bool captureDiagnostic,
            out int atomIndex,
            out CurrentLaneMatchDiagnostic diagnostic)
        {
            atomIndex = -1;
            diagnostic = default;
            if (chain == null
                || chain.TrackAtoms == null
                || chain.TrackAtoms.Count == 0
                || candidates == null)
            {
                return CurrentLaneMatchState.None;
            }

            CollectCandidates(chain, current, overlapLanes, candidates, captureDiagnostic, ref diagnostic);
            if (candidates.Count > 1)
                MergeAdjacentSeams(chain, current.m_Front.m_CurvePosition.y, candidates);
            if (captureDiagnostic)
                diagnostic.InitialCandidates = candidates.Count;
            if (candidates.Count == 0)
                return CurrentLaneMatchState.None;

            if (candidates.Count == 1)
            {
                if (captureDiagnostic)
                {
                    diagnostic.FutureCandidates = 1;
                    diagnostic.Basis = ProjectionMatchBasis.Initial;
                }
                return StateForSingleCandidate(chain, candidates[0], out atomIndex);
            }

            FilterOppositeDirection(chain, current, candidates);
            if (candidates.Count == 0)
                return CurrentLaneMatchState.None;

            if (candidates.Count == 1)
            {
                if (captureDiagnostic)
                {
                    diagnostic.FutureCandidates = 1;
                    diagnostic.Basis = ProjectionMatchBasis.Direction;
                }
                return StateForSingleCandidate(chain, candidates[0], out atomIndex);
            }

            if (captureDiagnostic)
                diagnostic.FutureCandidates = candidates.Count;

            return CurrentLaneMatchState.Ambiguous;
        }

        internal static CurrentLaneMatchState FilterByFuture(
            LineTrackChain chain,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            IList<TrainNavigationLane> navigation,
            IList<PathElement> pathTail,
            bool pathTailComplete,
            List<int> candidates,
            bool captureDiagnostic,
            out int atomIndex,
            out CurrentLaneMatchDiagnostic diagnostic)
        {
            atomIndex = -1;
            diagnostic = default;
            if (chain == null
                || chain.TrackAtoms == null
                || chain.TrackAtoms.Count == 0
                || candidates == null)
            {
                return CurrentLaneMatchState.None;
            }

            if (candidates.Count > 1 && (navigation != null || pathTail != null))
                FilterCandidatesByFuture(
                    chain,
                    current,
                    overlapLanes,
                    navigation,
                    pathTail,
                    pathTailComplete,
                    candidates);

            if (captureDiagnostic)
                diagnostic.FutureCandidates = candidates.Count;
            if (candidates.Count == 0)
                return CurrentLaneMatchState.None;
            if (candidates.Count > 1)
                return CurrentLaneMatchState.Ambiguous;

            return StateForSingleCandidate(chain, candidates[0], out atomIndex);
        }

        internal static CurrentLaneMatchState SelectWaypointCandidates(
            LineTrackChain chain,
            int waypointIndex,
            List<int> candidates,
            bool includeIncomingSegment,
            ProjectionMatchBasis basis,
            bool captureDiagnostic,
            out int atomIndex,
            out CurrentLaneMatchDiagnostic diagnostic)
        {
            atomIndex = -1;
            diagnostic = default;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || waypointIndex < 0
                || candidates == null)
                return CurrentLaneMatchState.Ambiguous;

            if (captureDiagnostic)
                diagnostic.FutureCandidates = candidates.Count;

            int matched = -1;
            int matchCount = 0;
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                int candidate = candidates[candidateIndex];
                if (!IsInWaypointWindow(chain, waypointIndex, candidate)
                    && (!includeIncomingSegment || !IsInIncomingSegment(chain, waypointIndex, candidate)))
                    continue;

                matched = candidate;
                if (++matchCount > 1)
                    return CurrentLaneMatchState.Ambiguous;
            }

            if (matchCount != 1)
                return CurrentLaneMatchState.Ambiguous;

            candidates.Clear();
            candidates.Add(matched);
            if (captureDiagnostic)
            {
                diagnostic.FutureCandidates = 1;
                diagnostic.Basis = basis;
            }
            return StateForSingleCandidate(chain, matched, out atomIndex);
        }

        private static bool IsInWaypointWindow(LineTrackChain chain, int waypointIndex, int atomIndex)
        {
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.WaypointIndex != waypointIndex
                    || (traversalEvent.Kind != TraversalEventKind.Stop
                        && traversalEvent.Kind != TraversalEventKind.Pass))
                {
                    continue;
                }

                int start = traversalEvent.StartAtomIndex;
                int endExclusive = math.max(start + 1, traversalEvent.EndAtomIndexExclusive);
                if (atomIndex >= start && atomIndex < endExclusive)
                    return true;
            }

            return false;
        }

        private static bool IsInIncomingSegment(LineTrackChain chain, int waypointIndex, int atomIndex)
        {
            if (chain.SegmentRanges == null || chain.SegmentRanges.Count == 0)
                return false;

            int incoming = waypointIndex == 0
                ? chain.SegmentRanges.Count - 1
                : waypointIndex - 1;
            if (incoming < 0 || incoming >= chain.SegmentRanges.Count)
                return false;

            TrackSegmentRange range = chain.SegmentRanges[incoming];
            return atomIndex >= range.StartAtomIndex && atomIndex < range.EndAtomIndexExclusive;
        }

        private static CurrentLaneMatchState StateForSingleCandidate(
            LineTrackChain chain,
            int candidate,
            out int atomIndex)
        {
            atomIndex = candidate;
            return HasUsableParameterSpan(chain.TrackAtoms[candidate])
                ? CurrentLaneMatchState.Unique
                : CurrentLaneMatchState.ZeroParameterSpan;
        }

        internal static bool TryGetProgress(LineTrackChain chain, int atomIndex, float curveY, out float progress)
        {
            progress = 0f;
            if (chain == null
                || atomIndex < 0
                || atomIndex >= chain.TrackAtoms.Count
                || !math.isfinite(curveY))
            {
                return false;
            }

            TrackAtom atom = chain.TrackAtoms[atomIndex];
            if (!HasUsableParameterSpan(atom))
                return false;

            float start = atom.TargetDelta.x;
            float end = atom.TargetDelta.y;
            if (!ContainsParameter(start, end, curveY))
                return false;

            float value = (curveY - start) / (end - start);
            if (!math.isfinite(value)
                || value < -EndpointTolerance
                || value > 1f + EndpointTolerance)
            {
                return false;
            }

            progress = math.clamp(value, 0f, 1f);
            return true;
        }

        internal static bool HasPhysicalCandidate(LineTrackChain chain, Entity lane)
        {
            return HasPhysicalAtom(chain, lane);
        }

        private static void CollectCandidates(
            LineTrackChain chain,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            List<int> candidates,
            bool captureDiagnostic,
            ref CurrentLaneMatchDiagnostic diagnostic)
        {
            candidates.Clear();
            Entity lane = current.m_Front.m_Lane;
            float curveY = current.m_Front.m_CurvePosition.y;
            if (lane == Entity.Null
                || !math.isfinite(curveY))
            {
                if (captureDiagnostic)
                    diagnostic.InvalidInput = true;
                return;
            }
            CollectIndexedCandidates(chain, current, lane, candidates, captureDiagnostic, ref diagnostic);
            if (candidates.Count == 0 && overlapLanes != null)
            {
                for (int overlapIndex = 0; overlapIndex < overlapLanes.Count; overlapIndex++)
                {
                    CollectIndexedCandidates(chain, current, overlapLanes[overlapIndex], candidates, captureDiagnostic, ref diagnostic);
                }
            }
        }

        private static void CollectIndexedCandidates(
            LineTrackChain chain,
            TrainCurrentLane current,
            Entity lane,
            List<int> candidates,
            bool captureDiagnostic,
            ref CurrentLaneMatchDiagnostic diagnostic)
        {
            if (!chain.AtomIndicesByLane.TryGetValue(lane, out List<int> indexed))
                return;
            if (captureDiagnostic)
                diagnostic.IndexedCandidates += indexed.Count;
            float curveY = current.m_Front.m_CurvePosition.y;

            for (int i = 0; i < indexed.Count; i++)
            {
                int candidate = indexed[i];
                if (candidate < 0
                    || candidate >= chain.TrackAtoms.Count
                    || Contains(candidates, candidate))
                {
                    continue;
                }

                TrackAtom atom = chain.TrackAtoms[candidate];
                if (!MatchesPhysicalLane(atom, lane))
                {
                    continue;
                }
                if (captureDiagnostic)
                    diagnostic.PhysicalCandidates++;
                if (!ContainsParameter(atom.TargetDelta.x, atom.TargetDelta.y, curveY))
                    continue;
                if (captureDiagnostic)
                {
                    diagnostic.CoordinateCandidates++;
                }

                candidates.Add(candidate);
            }
        }

        private static void MergeAdjacentSeams(LineTrackChain chain, float curveY, List<int> candidates)
        {
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                int index = candidates[i];
                int nextIndex = NextAtomIndex(chain, index);
                if (!Contains(candidates, nextIndex))
                    continue;

                TrackAtom atom = chain.TrackAtoms[index];
                TrackAtom next = chain.TrackAtoms[nextIndex];
                // 只合并实际命中的同一接缝，不把容差内的短片段端点合并。
                if (HasUsableParameterSpan(atom)
                    && HasUsableParameterSpan(next)
                    && AtomLane(atom) == AtomLane(next)
                    && SameDirection(atom.TargetDelta.y - atom.TargetDelta.x, next.TargetDelta.y - next.TargetDelta.x)
                    && atom.TargetDelta.y == next.TargetDelta.x
                    && curveY == atom.TargetDelta.y)
                {
                    candidates.RemoveAt(i);
                }
            }
        }

        private static void FilterOppositeDirection(
            LineTrackChain chain,
            TrainCurrentLane current,
            List<int> candidates)
        {
            float direction = current.m_Front.m_CurvePosition.w - current.m_Front.m_CurvePosition.x;
            if (!math.isfinite(direction) || direction == 0f)
                return;

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                TrackAtom atom = chain.TrackAtoms[candidates[i]];
                float atomDirection = atom.TargetDelta.y - atom.TargetDelta.x;
                if (math.isfinite(atomDirection)
                    && atomDirection != 0f
                    && math.sign(direction) != math.sign(atomDirection))
                {
                    candidates.RemoveAt(i);
                }
            }
        }

        private static void FilterCandidatesByFuture(
            LineTrackChain chain,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            IList<TrainNavigationLane> navigation,
            IList<PathElement> pathTail,
            bool pathTailComplete,
            List<int> candidates)
        {
            if ((current.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) != 0)
            {
                return;
            }

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                int candidateAtomIndex = candidates[i];
                FutureMatch comparison = CompareFuture(
                    chain,
                    candidateAtomIndex,
                    current,
                    overlapLanes,
                    navigation,
                    pathTail,
                    pathTailComplete);
                if (comparison == FutureMatch.Mismatch)
                {
                    candidates.RemoveAt(i);
                }
            }
        }

        private static FutureMatch CompareFuture(
            LineTrackChain chain,
            int atomIndex,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            IList<TrainNavigationLane> navigation,
            IList<PathElement> pathTail,
            bool pathTailComplete)
        {
            int modelIndex = SkipCurrentLaneTail(
                chain,
                atomIndex,
                current,
                overlapLanes,
                out float? modelPosition);
            if (modelIndex < 0)
            {
                return FutureMatch.Indeterminate;
            }

            bool compared = false;
            if (navigation != null)
            {
                for (int i = 0; i < navigation.Count; i++)
                {
                    TrainNavigationLane lane = navigation[i];
                    FutureMatch result = CompareLane(
                        chain,
                        ref modelIndex,
                        ref modelPosition,
                        lane,
                        out bool resumedAtReturn);
                    if (result == FutureMatch.Mismatch)
                        return result;
                    if (result == FutureMatch.Stop)
                        return FutureMatch.Indeterminate;
                    compared |= result == FutureMatch.Match;
                    if ((lane.m_Flags & TrainLaneFlags.EndOfPath) != 0)
                    {
                        return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
                    }
                    if ((lane.m_Flags & TrainLaneFlags.Return) != 0)
                    {
                        if (resumedAtReturn)
                            continue;
                        return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
                    }
                }
            }

            if (pathTail != null)
            {
                for (int i = 0; i < pathTail.Count; i++)
                {
                    PathElement element = pathTail[i];
                    TrainNavigationLane lane = new TrainNavigationLane
                    {
                        m_Lane = element.m_Target,
                        m_CurvePosition = element.m_TargetDelta,
                        m_Flags = pathTailComplete && i == pathTail.Count - 1
                            ? TrainLaneFlags.EndOfPath
                            : (element.m_Flags & PathElementFlags.Return) != 0
                                ? TrainLaneFlags.Return
                                : 0
                    };
                    FutureMatch result = CompareLane(
                        chain,
                        ref modelIndex,
                        ref modelPosition,
                        lane,
                        out bool resumedAtReturn);
                    if (result == FutureMatch.Mismatch)
                        return result;
                    if (result == FutureMatch.Stop)
                        return FutureMatch.Indeterminate;
                    compared |= result == FutureMatch.Match;
                    if ((lane.m_Flags & (TrainLaneFlags.EndOfPath | TrainLaneFlags.Return)) != 0)
                    {
                        if ((lane.m_Flags & TrainLaneFlags.EndOfPath) == 0 && resumedAtReturn)
                            continue;
                        return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
                    }
                }
            }

            return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
        }

        private static int SkipCurrentLaneTail(
            LineTrackChain chain,
            int atomIndex,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            out float? modelPosition)
        {
            modelPosition = null;
            Entity lane = current.m_Front.m_Lane;
            float currentPosition = current.m_Front.m_CurvePosition.y;
            float currentEnd = current.m_Front.m_CurvePosition.w;
            float direction = currentEnd - current.m_Front.m_CurvePosition.x;
            if (lane == Entity.Null
                || !math.isfinite(currentPosition)
                || !math.isfinite(currentEnd)
                || direction == 0f)
            {
                return -1;
            }

            if ((current.m_Front.m_LaneFlags & TrainLaneFlags.Return) != 0)
            {
                if (!TryGetForwardExtensionRange(chain, atomIndex, out TrackExtensionRange extensionRange))
                {
                    return -1;
                }

                TrackAtom returnAtom = chain.TrackAtoms[atomIndex];
                if (!MatchesCurrentLane(returnAtom, lane, overlapLanes)
                    || !SameDirection(returnAtom.TargetDelta.y - returnAtom.TargetDelta.x, direction)
                    || !ContainsParameter(returnAtom.TargetDelta.x, returnAtom.TargetDelta.y, currentPosition)
                    || !ContainsParameter(returnAtom.TargetDelta.x, returnAtom.TargetDelta.y, currentEnd))
                {
                    return -1;
                }

                return extensionRange.ResumeAtomIndex;
            }

            int index = atomIndex;
            for (int count = 0; count < chain.TrackAtoms.Count; count++)
            {
                TrackAtom atom = chain.TrackAtoms[index];
                if (!MatchesCurrentLane(atom, lane, overlapLanes)
                    || !SameDirection(atom.TargetDelta.y - atom.TargetDelta.x, direction)
                    || (count == 0 && !ContainsParameter(atom.TargetDelta.x, atom.TargetDelta.y, currentPosition)))
                {
                    return -1;
                }

                if (ContainsParameter(atom.TargetDelta.x, atom.TargetDelta.y, currentEnd))
                {
                    if (atom.TargetDelta.y == currentEnd)
                        return NextAtomIndex(chain, index);

                    modelPosition = currentEnd;
                    return index;
                }
                if (Passed(currentPosition, atom.TargetDelta.y, currentEnd, direction))
                    return -1;

                currentPosition = atom.TargetDelta.y;
                index = NextAtomIndex(chain, index);
            }

            return -1;
        }

        private static FutureMatch CompareLane(
            LineTrackChain chain,
            ref int modelIndex,
            ref float? modelPosition,
            TrainNavigationLane lane,
            out bool resumedAtReturn)
        {
            resumedAtReturn = false;
            if (lane.m_Lane == Entity.Null)
            {
                return FutureMatch.Indeterminate;
            }

            if (!HasPhysicalAtom(chain, lane.m_Lane))
            {
                // Only a recorded model filter proves that an original
                // navigation element may be skipped.  An unknown rail lane
                // ends this candidate's evidence; it cannot be treated as
                // harmless noise and followed by a later match.
                bool filtered = IsFilteredInput(chain, lane);
                return filtered ? FutureMatch.Indeterminate : FutureMatch.Stop;
            }

            // 当前段停在片段内部时，由后续项决定原轨续接或进入紧邻后继。
            if (modelPosition.HasValue
                && !MatchesPhysicalLane(chain.TrackAtoms[modelIndex], lane.m_Lane))
            {
                modelIndex = NextAtomIndex(chain, modelIndex);
                modelPosition = null;
            }

            if (TryConsumeLaneRange(
                    chain,
                    ref modelIndex,
                    ref modelPosition,
                    lane,
                    out resumedAtReturn))
            {
                return FutureMatch.Match;
            }

            return FutureMatch.Mismatch;
        }

        private static bool TryConsumeLaneRange(
            LineTrackChain chain,
            ref int modelIndex,
            ref float? modelPosition,
            TrainNavigationLane lane,
            out bool resumedAtReturn)
        {
            resumedAtReturn = false;
            if (!math.isfinite(lane.m_CurvePosition.x)
                || !math.isfinite(lane.m_CurvePosition.y)
                || modelIndex < 0)
            {
                return false;
            }

            float expectedStart = lane.m_CurvePosition.x;
            float expectedEnd = lane.m_CurvePosition.y;
            float navigationDirection = expectedEnd - expectedStart;
            int count = 0;
            while (count++ < chain.TrackAtoms.Count)
            {
                TrackAtom atom = chain.TrackAtoms[modelIndex];
                float modelStart = modelPosition ?? atom.TargetDelta.x;
                if (!MatchesPhysicalLane(atom, lane.m_Lane))
                {
                    return false;
                }
                if (!SameDirection(atom.TargetDelta.y - atom.TargetDelta.x, navigationDirection))
                {
                    return false;
                }
                if (!Approximately(modelStart, expectedStart))
                {
                    return false;
                }

                bool endOfPath = (lane.m_Flags & TrainLaneFlags.EndOfPath) != 0;
                bool returns = (lane.m_Flags & TrainLaneFlags.Return) != 0;
                if (endOfPath && ContainsParameter(modelStart, atom.TargetDelta.y, expectedEnd))
                    return true;

                if (returns
                    && TryGetForwardExtensionRange(chain, modelIndex, out TrackExtensionRange matchedRange)
                    && ContainsParameter(modelStart, atom.TargetDelta.y, expectedEnd))
                {
                    modelIndex = matchedRange.ResumeAtomIndex;
                    modelPosition = null;
                    resumedAtReturn = true;
                    return true;
                }

                if (Approximately(atom.TargetDelta.y, expectedEnd))
                {
                    modelIndex = NextAtomIndex(chain, modelIndex);
                    modelPosition = null;
                    return true;
                }

                if (Passed(expectedStart, atom.TargetDelta.y, expectedEnd, navigationDirection))
                {
                    return false;
                }

                expectedStart = atom.TargetDelta.y;
                modelIndex = NextAtomIndex(chain, modelIndex);
                modelPosition = null;
            }

            return false;
        }

        private static Entity AtomLane(TrackAtom atom)
        {
            return atom.Key.PhysicalLaneKey != Entity.Null
                ? atom.Key.PhysicalLaneKey
                : atom.SourceTarget;
        }

        private static bool TryGetForwardExtensionRange(
            LineTrackChain chain,
            int atomIndex,
            out TrackExtensionRange extensionRange)
        {
            extensionRange = default;
            if (chain == null)
                return false;

            for (int rangeIndex = 0; rangeIndex < chain.TrackExtensionRanges.Count; rangeIndex++)
            {
                TrackExtensionRange candidate = chain.TrackExtensionRanges[rangeIndex];
                if (atomIndex < candidate.StartAtomIndex
                    || atomIndex >= candidate.ForwardEndAtomIndexExclusive)
                {
                    continue;
                }

                extensionRange = candidate;
                return true;
            }
            return false;
        }

        private static int NextAtomIndex(LineTrackChain chain, int atomIndex)
        {
            int count = chain.TrackAtoms.Count;
            return count == 0 ? -1 : (atomIndex + 1) % count;
        }

        private static bool HasPhysicalAtom(LineTrackChain chain, Entity lane)
        {
            if (!chain.AtomIndicesByLane.TryGetValue(lane, out List<int> indexed))
                return false;

            for (int i = 0; i < indexed.Count; i++)
            {
                int index = indexed[i];
                if (index >= 0
                    && index < chain.TrackAtoms.Count
                    && MatchesPhysicalLane(chain.TrackAtoms[index], lane))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFilteredInput(LineTrackChain chain, TrainNavigationLane lane)
        {
            if (chain.SegmentInputs == null)
                return false;

            for (int segmentIndex = 0; segmentIndex < chain.SegmentInputs.Length; segmentIndex++)
            {
                TrackPathElementBaseline[] elements = chain.SegmentInputs[segmentIndex].Elements;
                for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                {
                    TrackPathElementBaseline element = elements[elementIndex];
                    if (!element.HasAtomContribution
                        && element.Target == lane.m_Lane
                        && Approximately(math.asfloat(element.TargetDeltaXBits), lane.m_CurvePosition.x)
                        && Approximately(math.asfloat(element.TargetDeltaYBits), lane.m_CurvePosition.y))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool MatchesPhysicalLane(TrackAtom atom, Entity lane)
        {
            return lane != Entity.Null
                && (atom.SourceTarget == lane || atom.Key.PhysicalLaneKey == lane);
        }

        private static bool MatchesCurrentLane(TrackAtom atom, Entity lane, IList<Entity> overlapLanes)
        {
            if (MatchesPhysicalLane(atom, lane))
                return true;
            if (overlapLanes == null)
                return false;
            for (int index = 0; index < overlapLanes.Count; index++)
            {
                if (MatchesPhysicalLane(atom, overlapLanes[index]))
                    return true;
            }
            return false;
        }

        private static bool ContainsParameter(float start, float end, float value)
        {
            if (!math.isfinite(start) || !math.isfinite(end))
                return false;
            if (end == start)
                return math.abs(value - start) <= EndpointTolerance;
            return value >= math.min(start, end) - EndpointTolerance
                && value <= math.max(start, end) + EndpointTolerance;
        }

        private static bool HasUsableParameterSpan(TrackAtom atom)
        {
            return math.isfinite(atom.TargetDelta.x)
                && math.isfinite(atom.TargetDelta.y)
                && atom.TargetDelta.y != atom.TargetDelta.x;
        }

        private static bool SameDirection(float left, float right)
        {
            return left == 0f
                || right == 0f
                || math.sign(left) == math.sign(right);
        }

        private static bool Passed(float start, float current, float end, float direction)
        {
            return direction >= 0f
                ? current > end + EndpointTolerance
                : current < end - EndpointTolerance;
        }

        private static bool Approximately(float left, float right)
        {
            return math.isfinite(left)
                && math.isfinite(right)
                && math.abs(left - right) <= EndpointTolerance;
        }

        private static bool Contains(List<int> values, int value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] == value)
                    return true;
            }

            return false;
        }

        private enum FutureMatch : byte
        {
            Indeterminate = 0,
            Match = 1,
            Mismatch = 2,
            Stop = 3,
        }

    }
}
