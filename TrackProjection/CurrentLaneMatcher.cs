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
            if (captureDiagnostic)
            {
                diagnostic.LastCandidateAtomIndex = -1;
                diagnostic.LastCandidateRange = new float2(float.NaN);
            }
            if (chain == null
                || chain.TrackAtoms == null
                || chain.TrackAtoms.Count == 0
                || candidates == null)
            {
                return CurrentLaneMatchState.None;
            }

            CollectCandidates(chain, current, overlapLanes, candidates, captureDiagnostic, ref diagnostic);
            if (captureDiagnostic)
                diagnostic.InitialCandidates = candidates.Count;
            if (candidates.Count == 0)
                return CurrentLaneMatchState.None;

            if (candidates.Count == 1)
            {
                if (captureDiagnostic)
                {
                    diagnostic.DirectionCandidates = 1;
                    diagnostic.FutureCandidates = 1;
                    diagnostic.Basis = ProjectionMatchBasis.Initial;
                }
                return StateForSingleCandidate(chain, candidates[0], out atomIndex);
            }

            FilterOppositeDirection(chain, current, candidates, captureDiagnostic, ref diagnostic);
            if (captureDiagnostic)
                diagnostic.DirectionCandidates = candidates.Count;
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
            List<int> candidates,
            ProjectionEvidenceStage evidenceStage,
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
                    candidates,
                    evidenceStage,
                    captureDiagnostic,
                    ref diagnostic);

            if (captureDiagnostic)
                diagnostic.FutureCandidates = candidates.Count;
            if (candidates.Count == 0)
                return CurrentLaneMatchState.None;
            if (candidates.Count > 1)
                return CurrentLaneMatchState.Ambiguous;

            if (captureDiagnostic)
            {
                diagnostic.Basis = evidenceStage == ProjectionEvidenceStage.PathTail
                    ? ProjectionMatchBasis.PathTail
                    : ProjectionMatchBasis.Navigation;
            }
            return StateForSingleCandidate(chain, candidates[0], out atomIndex);
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
                    int previousCount = candidates.Count;
                    CollectIndexedCandidates(chain, current, overlapLanes[overlapIndex], candidates, captureDiagnostic, ref diagnostic);
                    if (captureDiagnostic && candidates.Count > previousCount)
                        diagnostic.OverlapLane = overlapLanes[overlapIndex];
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
            float nearestDistance = float.MaxValue;

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
                if (captureDiagnostic)
                {
                    float low = math.min(atom.TargetDelta.x, atom.TargetDelta.y);
                    float high = math.max(atom.TargetDelta.x, atom.TargetDelta.y);
                    float distance = curveY < low ? low - curveY : curveY > high ? curveY - high : 0f;
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        diagnostic.LastCandidateAtomIndex = candidate;
                        diagnostic.LastCandidateRange = atom.TargetDelta;
                    }
                }
                if (!ContainsParameter(atom.TargetDelta.x, atom.TargetDelta.y, curveY))
                    continue;
                if (captureDiagnostic)
                {
                    diagnostic.CoordinateCandidates++;
                    diagnostic.LastCandidateAtomIndex = candidate;
                    diagnostic.LastCandidateRange = atom.TargetDelta;
                }

                candidates.Add(candidate);
            }
        }

        private static void FilterOppositeDirection(
            LineTrackChain chain,
            TrainCurrentLane current,
            List<int> candidates,
            bool captureDiagnostic,
            ref CurrentLaneMatchDiagnostic diagnostic)
        {
            float direction = current.m_Front.m_CurvePosition.w - current.m_Front.m_CurvePosition.x;
            if (!math.isfinite(direction) || math.abs(direction) <= EndpointTolerance)
                return;

            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                TrackAtom atom = chain.TrackAtoms[candidates[i]];
                float atomDirection = atom.TargetDelta.y - atom.TargetDelta.x;
                if (math.isfinite(atomDirection)
                    && math.abs(atomDirection) > EndpointTolerance
                    && math.sign(direction) != math.sign(atomDirection))
                {
                    if (captureDiagnostic && !diagnostic.Evidence.Available)
                    {
                        diagnostic.Evidence = new ProjectionMatchEvidence
                        {
                            Kind = ProjectionEvidenceKind.DirectionExcluded,
                            Reason = ProjectionEvidenceReason.OppositeDirection,
                            Stage = ProjectionEvidenceStage.Direction,
                            CandidateAtomIndex = candidates[i],
                            QueueIndex = -1,
                            ExpectedLane = AtomLane(atom),
                            ExpectedParameters = atom.TargetDelta,
                            ActualLane = current.m_Front.m_Lane,
                            ActualParameters = current.m_Front.m_CurvePosition,
                        };
                    }
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
            List<int> candidates,
            ProjectionEvidenceStage evidenceStage,
            bool captureDiagnostic,
            ref CurrentLaneMatchDiagnostic diagnostic)
        {
            if ((current.m_Front.m_LaneFlags & TrainLaneFlags.EndOfPath) != 0)
            {
                if (captureDiagnostic && candidates.Count > 0)
                {
                    ProjectionMatchEvidence terminalEvidence = MakeCurrentEvidence(
                        chain,
                        candidates[0],
                        current,
                        ProjectionEvidenceStage.None,
                        ProjectionEvidenceKind.NoEvidence,
                        ProjectionEvidenceReason.EndOfPathOrReturn);
                    if (diagnostic.Evidence.ShouldReplaceWith(terminalEvidence))
                        diagnostic.Evidence = terminalEvidence;
                }
                return;
            }

            ExtensionReturnMatch survivingExtension = default;
            ProjectionMatchEvidence ambiguityEvidence = default;
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
                    evidenceStage,
                    captureDiagnostic,
                    out ProjectionMatchEvidence evidence,
                    out ExtensionReturnMatch extensionMatch);
                if (captureDiagnostic
                    && diagnostic.Evidence.ShouldReplaceWith(evidence))
                {
                    diagnostic.Evidence = evidence;
                }
                if (comparison == FutureMatch.Mismatch)
                {
                    candidates.RemoveAt(i);
                    if (captureDiagnostic && candidates.Count == 0 && evidence.Available)
                        diagnostic.Evidence = evidence;
                }
                else
                {
                    if (extensionMatch.Matched)
                    {
                        extensionMatch.CandidateAtomIndex = candidateAtomIndex;
                        survivingExtension = extensionMatch;
                    }
                    if (evidence.Kind == ProjectionEvidenceKind.NoEvidence)
                        ambiguityEvidence = evidence;
                }
            }
            if (captureDiagnostic
                && candidates.Count > 1
                && ambiguityEvidence.Available)
            {
                diagnostic.Evidence = ambiguityEvidence;
            }
            if (captureDiagnostic
                && candidates.Count == 1
                && survivingExtension.Matched
                && survivingExtension.CandidateAtomIndex == candidates[0])
            {
                RecordExtensionReturn(ref diagnostic, survivingExtension);
            }
        }

        private static FutureMatch CompareFuture(
            LineTrackChain chain,
            int atomIndex,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            IList<TrainNavigationLane> navigation,
            IList<PathElement> pathTail,
            ProjectionEvidenceStage evidenceStage,
            bool captureDiagnostic,
            out ProjectionMatchEvidence evidence,
            out ExtensionReturnMatch extensionMatch)
        {
            evidence = default;
            extensionMatch = default;
            int modelIndex = SkipCurrentLaneTail(
                chain,
                atomIndex,
                current,
                overlapLanes,
                out ExtensionReturnMatch currentReturnMatch);
            if (modelIndex < 0)
            {
                if (captureDiagnostic)
                {
                    evidence = MakeCurrentEvidence(
                        chain,
                        atomIndex,
                        current,
                        evidenceStage,
                        ProjectionEvidenceKind.NoEvidence,
                        ProjectionEvidenceReason.CurrentTailUnavailable);
                }
                return FutureMatch.Indeterminate;
            }
            if (currentReturnMatch.Matched)
                extensionMatch = currentReturnMatch;

            bool compared = false;
            if (navigation != null)
            {
                for (int i = 0; i < navigation.Count; i++)
                {
                    TrainNavigationLane lane = navigation[i];
                    FutureMatch result = CompareLane(
                        chain,
                        ref modelIndex,
                        lane,
                        ProjectionEvidenceStage.Navigation,
                        i,
                        captureDiagnostic,
                        out bool resumedAtReturn,
                        out ExtensionReturnMatch laneExtension,
                        out ProjectionMatchEvidence laneEvidence);
                    if (captureDiagnostic && evidence.ShouldReplaceWith(laneEvidence))
                        evidence = laneEvidence;
                    if (result == FutureMatch.Mismatch)
                        return result;
                    if (result == FutureMatch.Stop)
                        return FutureMatch.Indeterminate;
                    compared |= result == FutureMatch.Match;
                    if (laneExtension.Matched)
                        extensionMatch = laneExtension;
                    if ((lane.m_Flags & TrainLaneFlags.EndOfPath) != 0)
                    {
                        if (captureDiagnostic)
                        {
                            ProjectionMatchEvidence terminalEvidence = MakeEvidence(
                                chain,
                                modelIndex,
                                lane,
                                ProjectionEvidenceStage.Navigation,
                                i,
                                ProjectionEvidenceKind.NoEvidence,
                                ProjectionEvidenceReason.EndOfPathOrReturn);
                            if (evidence.ShouldReplaceWith(terminalEvidence))
                                evidence = terminalEvidence;
                        }
                        return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
                    }
                    if ((lane.m_Flags & TrainLaneFlags.Return) != 0)
                    {
                        if (resumedAtReturn)
                            continue;
                        if (captureDiagnostic)
                        {
                            ProjectionMatchEvidence terminalEvidence = MakeEvidence(
                                chain,
                                modelIndex,
                                lane,
                                ProjectionEvidenceStage.Navigation,
                                i,
                                ProjectionEvidenceKind.NoEvidence,
                                ProjectionEvidenceReason.EndOfPathOrReturn);
                            if (evidence.ShouldReplaceWith(terminalEvidence))
                                evidence = terminalEvidence;
                        }
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
                        m_Flags = (element.m_Flags & PathElementFlags.Return) != 0
                            ? TrainLaneFlags.Return
                            : 0
                    };
                    FutureMatch result = CompareLane(
                        chain,
                        ref modelIndex,
                        lane,
                        ProjectionEvidenceStage.PathTail,
                        i,
                        captureDiagnostic,
                        out bool resumedAtReturn,
                        out ExtensionReturnMatch laneExtension,
                        out ProjectionMatchEvidence laneEvidence);
                    if (captureDiagnostic && evidence.ShouldReplaceWith(laneEvidence))
                        evidence = laneEvidence;
                    if (result == FutureMatch.Mismatch)
                        return result;
                    if (result == FutureMatch.Stop)
                        return FutureMatch.Indeterminate;
                    compared |= result == FutureMatch.Match;
                    if (laneExtension.Matched)
                        extensionMatch = laneExtension;
                    if ((lane.m_Flags & TrainLaneFlags.Return) != 0)
                    {
                        if (resumedAtReturn)
                            continue;
                        if (captureDiagnostic)
                        {
                            ProjectionMatchEvidence terminalEvidence = MakeEvidence(
                                chain,
                                modelIndex,
                                lane,
                                ProjectionEvidenceStage.PathTail,
                                i,
                                ProjectionEvidenceKind.NoEvidence,
                                ProjectionEvidenceReason.EndOfPathOrReturn);
                            if (evidence.ShouldReplaceWith(terminalEvidence))
                                evidence = terminalEvidence;
                        }
                        return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
                    }
                }
            }

            if (!compared && captureDiagnostic && !evidence.Available)
            {
                evidence = MakeCurrentEvidence(
                    chain,
                    atomIndex,
                    current,
                    evidenceStage,
                    ProjectionEvidenceKind.NoEvidence,
                    ProjectionEvidenceReason.EmptyInput);
            }
            return compared ? FutureMatch.Match : FutureMatch.Indeterminate;
        }

        private static int SkipCurrentLaneTail(
            LineTrackChain chain,
            int atomIndex,
            TrainCurrentLane current,
            IList<Entity> overlapLanes,
            out ExtensionReturnMatch returnMatch)
        {
            returnMatch = default;
            Entity lane = current.m_Front.m_Lane;
            float currentPosition = current.m_Front.m_CurvePosition.y;
            float currentEnd = current.m_Front.m_CurvePosition.w;
            float direction = currentEnd - current.m_Front.m_CurvePosition.x;
            if (lane == Entity.Null
                || !math.isfinite(currentPosition)
                || !math.isfinite(currentEnd)
                || math.abs(direction) <= EndpointTolerance)
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

                returnMatch = new ExtensionReturnMatch(
                    extensionRange,
                    current.m_Front.m_Lane,
                    current.m_Front.m_CurvePosition.w,
                    ProjectionEvidenceStage.None,
                    -1);
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

                if (Approximately(atom.TargetDelta.y, currentEnd))
                    return NextAtomIndex(chain, index);
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
            TrainNavigationLane lane,
            ProjectionEvidenceStage stage,
            int queueIndex,
            bool captureDiagnostic,
            out bool resumedAtReturn,
            out ExtensionReturnMatch extensionMatch,
            out ProjectionMatchEvidence evidence)
        {
            evidence = default;
            resumedAtReturn = false;
            extensionMatch = default;
            if (lane.m_Lane == Entity.Null)
            {
                if (captureDiagnostic)
                {
                    evidence = MakeEvidence(
                        chain,
                        modelIndex,
                        lane,
                        stage,
                        queueIndex,
                        ProjectionEvidenceKind.NoEvidence,
                        ProjectionEvidenceReason.EmptyInput);
                }
                return FutureMatch.Indeterminate;
            }

            if (!HasPhysicalAtom(chain, lane.m_Lane))
            {
                // Only a recorded model filter proves that an original
                // navigation element may be skipped.  An unknown rail lane
                // ends this candidate's evidence; it cannot be treated as
                // harmless noise and followed by a later match.
                bool filtered = IsFilteredInput(chain, lane);
                if (captureDiagnostic)
                {
                    evidence = MakeEvidence(
                        chain,
                        modelIndex,
                        lane,
                        stage,
                        queueIndex,
                        ProjectionEvidenceKind.NoEvidence,
                        filtered
                            ? ProjectionEvidenceReason.FilteredInput
                            : ProjectionEvidenceReason.UnknownLaneStop);
                }
                return filtered ? FutureMatch.Indeterminate : FutureMatch.Stop;
            }

            if (TryConsumeLaneRange(
                    chain,
                    ref modelIndex,
                    lane,
                    out resumedAtReturn,
                    out TrackExtensionRange extensionRange,
                    out int mismatchIndex,
                    out ProjectionEvidenceReason mismatchReason))
            {
                if (resumedAtReturn)
                {
                    extensionMatch = new ExtensionReturnMatch(
                        extensionRange,
                        lane.m_Lane,
                        lane.m_CurvePosition.y,
                        stage,
                        queueIndex);
                }
                return FutureMatch.Match;
            }

            if (captureDiagnostic)
            {
                evidence = MakeEvidence(
                    chain,
                    mismatchIndex,
                    lane,
                    stage,
                    queueIndex,
                    ProjectionEvidenceKind.Mismatch,
                    mismatchReason);
            }
            return FutureMatch.Mismatch;
        }

        private static bool TryConsumeLaneRange(
            LineTrackChain chain,
            ref int modelIndex,
            TrainNavigationLane lane,
            out bool resumedAtReturn,
            out TrackExtensionRange extensionRange,
            out int mismatchIndex,
            out ProjectionEvidenceReason mismatchReason)
        {
            resumedAtReturn = false;
            extensionRange = default;
            mismatchIndex = modelIndex;
            mismatchReason = ProjectionEvidenceReason.InvalidParameters;
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
                mismatchIndex = modelIndex;
                if (!MatchesPhysicalLane(atom, lane.m_Lane))
                {
                    mismatchReason = ProjectionEvidenceReason.LaneMismatch;
                    return false;
                }
                if (!SameDirection(atom.TargetDelta.y - atom.TargetDelta.x, navigationDirection))
                {
                    mismatchReason = ProjectionEvidenceReason.DirectionMismatch;
                    return false;
                }
                if (!Approximately(atom.TargetDelta.x, expectedStart))
                {
                    mismatchReason = ProjectionEvidenceReason.ParameterStartMismatch;
                    return false;
                }

                bool endOfPath = (lane.m_Flags & TrainLaneFlags.EndOfPath) != 0;
                bool returns = (lane.m_Flags & TrainLaneFlags.Return) != 0;
                if ((endOfPath || returns)
                    && TryGetForwardExtensionRange(chain, modelIndex, out TrackExtensionRange matchedRange)
                    && ContainsParameter(atom.TargetDelta.x, atom.TargetDelta.y, expectedEnd))
                {
                    if (endOfPath)
                        return true;

                    extensionRange = matchedRange;
                    modelIndex = matchedRange.ResumeAtomIndex;
                    resumedAtReturn = true;
                    return true;
                }

                if (Approximately(atom.TargetDelta.y, expectedEnd))
                {
                    modelIndex = NextAtomIndex(chain, modelIndex);
                    return true;
                }

                if (Passed(expectedStart, atom.TargetDelta.y, expectedEnd, navigationDirection))
                {
                    mismatchReason = ProjectionEvidenceReason.ParameterRangeOverrun;
                    return false;
                }

                expectedStart = atom.TargetDelta.y;
                modelIndex = NextAtomIndex(chain, modelIndex);
            }

            mismatchReason = ProjectionEvidenceReason.ModelExhausted;
            return false;
        }

        private static ProjectionMatchEvidence MakeEvidence(
            LineTrackChain chain,
            int atomIndex,
            TrainNavigationLane actual,
            ProjectionEvidenceStage stage,
            int queueIndex,
            ProjectionEvidenceKind kind,
            ProjectionEvidenceReason reason)
        {
            Entity expectedLane = Entity.Null;
            float2 expectedParameters = default;
            if (atomIndex >= 0 && atomIndex < chain.TrackAtoms.Count)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                expectedLane = AtomLane(atom);
                expectedParameters = atom.TargetDelta;
            }

            return new ProjectionMatchEvidence
            {
                Kind = kind,
                Reason = reason,
                Stage = stage,
                CandidateAtomIndex = atomIndex,
                QueueIndex = queueIndex,
                ExpectedLane = expectedLane,
                ExpectedParameters = expectedParameters,
                ActualLane = actual.m_Lane,
                ActualParameters = new float4(
                    actual.m_CurvePosition.x,
                    actual.m_CurvePosition.y,
                    0f,
                    0f),
            };
        }

        private static ProjectionMatchEvidence MakeCurrentEvidence(
            LineTrackChain chain,
            int atomIndex,
            TrainCurrentLane current,
            ProjectionEvidenceStage stage,
            ProjectionEvidenceKind kind,
            ProjectionEvidenceReason reason)
        {
            Entity expectedLane = Entity.Null;
            float2 expectedParameters = default;
            if (atomIndex >= 0 && atomIndex < chain.TrackAtoms.Count)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                expectedLane = AtomLane(atom);
                expectedParameters = atom.TargetDelta;
            }

            return new ProjectionMatchEvidence
            {
                Kind = kind,
                Reason = reason,
                Stage = stage,
                CandidateAtomIndex = atomIndex,
                QueueIndex = -1,
                ExpectedLane = expectedLane,
                ExpectedParameters = expectedParameters,
                ActualLane = current.m_Front.m_Lane,
                ActualParameters = current.m_Front.m_CurvePosition,
            };
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

        private static void RecordExtensionReturn(
            ref CurrentLaneMatchDiagnostic diagnostic,
            ExtensionReturnMatch match)
        {
            diagnostic.ExtensionReturnMatched = true;
            diagnostic.ExtensionStartAtomIndex = match.Range.StartAtomIndex;
            diagnostic.ExtensionForwardEndAtomIndexExclusive = match.Range.ForwardEndAtomIndexExclusive;
            diagnostic.ExtensionResumeAtomIndex = match.Range.ResumeAtomIndex;
            diagnostic.ExtensionReturnLane = match.Lane;
            diagnostic.ExtensionReturnPosition = match.Position;
            diagnostic.ExtensionCandidateAtomIndex = match.CandidateAtomIndex;
            diagnostic.ExtensionStage = match.Stage;
            diagnostic.ExtensionQueueIndex = match.QueueIndex;
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
            if (math.abs(end - start) <= EndpointTolerance)
                return math.abs(value - start) <= EndpointTolerance;
            return value >= math.min(start, end) - EndpointTolerance
                && value <= math.max(start, end) + EndpointTolerance;
        }

        private static bool HasUsableParameterSpan(TrackAtom atom)
        {
            return math.isfinite(atom.TargetDelta.x)
                && math.isfinite(atom.TargetDelta.y)
                && math.abs(atom.TargetDelta.y - atom.TargetDelta.x) > EndpointTolerance;
        }

        private static bool SameDirection(float left, float right)
        {
            return math.abs(left) <= EndpointTolerance
                || math.abs(right) <= EndpointTolerance
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

        private struct ExtensionReturnMatch
        {
            internal TrackExtensionRange Range;
            internal Entity Lane;
            internal float Position;
            internal ProjectionEvidenceStage Stage;
            internal int QueueIndex;
            internal int CandidateAtomIndex;
            internal bool Matched;

            internal ExtensionReturnMatch(
                TrackExtensionRange range,
                Entity lane,
                float position,
                ProjectionEvidenceStage stage,
                int queueIndex)
            {
                Range = range;
                Lane = lane;
                Position = position;
                Stage = stage;
                QueueIndex = queueIndex;
                CandidateAtomIndex = -1;
                Matched = true;
            }
        }
    }
}
