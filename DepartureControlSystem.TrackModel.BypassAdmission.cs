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
            if (m_SharedWindowMatchSnapshots.TryGetValue(key, out SharedWindowMatchSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot.Match;
            }

            PhysicalSharedWindowMatch match = FindBestPhysicalSharedWindow(localChain, localProtectedInterval, currentBypassBuilding, expressChain);
            m_SharedWindowMatchSnapshots[key] = new SharedWindowMatchSnapshot(
                m_SharedTrackIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                match);
            return match;
        }


        private List<Entity> GetCandidateExpressLinesForLocalScene(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding)
        {
            if (localChain == null)
                return null;

            var key = new LocalSceneCandidateExpressLinesCacheKey(
                localChain.LineEntity,
                currentBypassBuilding,
                ResolveProtectedIntervalIndex(localChain, localProtectedInterval));
            if (!m_LocalSceneCandidateExpressLinesSnapshots.TryGetValue(key, out LocalSceneCandidateExpressLinesSnapshot snapshot)
                || snapshot == null
                || snapshot.SharedTrackVersion != m_SharedTrackIndexVersion
                || snapshot.LocalChainSignature != localChain.Signature)
            {
                snapshot = new LocalSceneCandidateExpressLinesSnapshot
                {
                    SharedTrackVersion = m_SharedTrackIndexVersion,
                    LocalChainSignature = localChain.Signature
                };

                var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
                foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
                {
                    Entity expressLine = entry.Value.LineEntity;
                    if (expressLine == Entity.Null
                        || expressLine == localChain.LineEntity
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
                    PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(
                        localChain,
                        localProtectedInterval,
                        currentBypassBuilding,
                        expressChain);
                    if (!sharedWindowMatch.Found)
                        continue;

                    snapshot.ExpressLines.Add(expressLine);
                }

                m_LocalSceneCandidateExpressLinesSnapshots[key] = snapshot;
            }

            return snapshot.ExpressLines;
        }


        private bool TryGetLocalSceneExpressStaticMatch(
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            Entity expressLine,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            out LineTrackChain expressChain,
            out LocalSceneExpressStaticMatchSnapshot snapshot)
        {
            expressChain = null;
            snapshot = default;
            if (localChain == null
                || expressLine == Entity.Null
                || !TryGetLineTrackChain(expressLine, expressWaypoints, out expressChain))
            {
                return false;
            }

            EnsureTrackChainBypassPipelineReady(expressChain);
            var key = new LocalSceneExpressStaticMatchCacheKey(
                localChain.LineEntity,
                expressLine,
                currentBypassBuilding,
                localProtectedIntervalIndex);
            if (m_LocalSceneExpressStaticMatchSnapshots.TryGetValue(key, out snapshot)
                && snapshot.SharedTrackVersion == m_SharedTrackIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot.Found;
            }

            PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain);
            if (!sharedWindowMatch.Found)
            {
                snapshot = new LocalSceneExpressStaticMatchSnapshot(
                    m_SharedTrackIndexVersion,
                    localChain.Signature,
                    expressChain.Signature,
                    false,
                    false,
                    default,
                    -1,
                    default,
                    0,
                    0,
                    string.Empty,
                    false,
                    -1,
                    false,
                    default,
                    default,
                    null);
                m_LocalSceneExpressStaticMatchSnapshots[key] = snapshot;
                return false;
            }

            if (sharedWindowMatch.Ambiguous)
            {
                snapshot = new LocalSceneExpressStaticMatchSnapshot(
                    m_SharedTrackIndexVersion,
                    localChain.Signature,
                    expressChain.Signature,
                    true,
                    true,
                    sharedWindowMatch.LocalSharedWindow,
                    -1,
                    default,
                    0,
                    0,
                    string.Empty,
                    false,
                    -1,
                    false,
                    default,
                    default,
                    null);
                m_LocalSceneExpressStaticMatchSnapshots[key] = snapshot;
                return true;
            }

            BypassProtectedInterval expressProtectedInterval = sharedWindowMatch.ExpressSharedWindow;
            int expressProtectedIntervalIndex = FindProtectedIntervalIndex(expressChain, expressProtectedInterval);
            bool hasRelevantSharedEntryAtomIndex = TryGetExpressFirstSharedAtomAfterCurrentBypassStation(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                sharedWindowMatch,
                out int relevantSharedEntryAtomIndex);
            bool hasSelectedTrunkSegment = TryBuildSceneRelationSameDirectionTrunkCandidates(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                hasRelevantSharedEntryAtomIndex,
                relevantSharedEntryAtomIndex,
                out SceneRelationTrunkCandidateSet trunkCandidates,
                out GlobalSharedTrunkSegment selectedTrunkSegment);
            snapshot = new LocalSceneExpressStaticMatchSnapshot(
                m_SharedTrackIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                true,
                false,
                sharedWindowMatch.LocalSharedWindow,
                expressProtectedIntervalIndex,
                expressProtectedInterval,
                sharedWindowMatch.OverlapCount,
                sharedWindowMatch.OrderedRun,
                "shared-window",
                hasRelevantSharedEntryAtomIndex,
                relevantSharedEntryAtomIndex,
                hasSelectedTrunkSegment,
                hasSelectedTrunkSegment ? selectedTrunkSegment : default,
                hasSelectedTrunkSegment ? BuildTrunkSkeleton(selectedTrunkSegment) : default,
                trunkCandidates);
            m_LocalSceneExpressStaticMatchSnapshots[key] = snapshot;
            return true;
        }


        private bool TryBuildSceneExpressRelation(
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            Entity expressLine,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            out SceneExpressRelation relation)
        {
            relation = default;
            if (!TryGetLocalSceneExpressStaticMatch(
                    localChain,
                    localProtectedIntervalIndex,
                    localProtectedInterval,
                    currentBypassBuilding,
                    expressLine,
                    expressWaypoints,
                    out LineTrackChain expressChain,
                    out LocalSceneExpressStaticMatchSnapshot staticMatch))
            {
                return false;
            }

            relation = new SceneExpressRelation(
                expressLine,
                expressChain,
                staticMatch.Ambiguous,
                staticMatch.ExpressProtectedIntervalIndex,
                staticMatch.ExpressProtectedInterval,
                staticMatch.OverlapCount,
                staticMatch.OrderedRun,
                staticMatch.ResolutionSource,
                staticMatch.HasRelevantSharedEntryAtomIndex,
                staticMatch.RelevantSharedEntryAtomIndex,
                staticMatch.HasSelectedTrunkSegment,
                staticMatch.SelectedTrunkSegment,
                staticMatch.TrunkSkeleton,
                staticMatch.TrunkCandidates);
            return true;
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

        private static int ComputeSceneEntryDistanceAtoms(
            LineRunningVehicleSnapshot runningVehicle,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            RelativeToTrunkState expressTrunkState,
            int relevantSharedEntryAtomIndex)
        {
            if (!runningVehicle.HasTrackCursor)
                return int.MaxValue;

            if (expressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                || expressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical)
            {
                return 0;
            }

            int cursorAtomIndex = runningVehicle.TrackCursor.AtomCursorIndex;
            if (cursorAtomIndex >= selectedTrunkSegment.ExpressCorridorStartAtomIndex
                && cursorAtomIndex < selectedTrunkSegment.ExpressCorridorEndAtomIndexExclusive)
            {
                return 0;
            }

            int targetAtomIndex = relevantSharedEntryAtomIndex >= 0
                ? relevantSharedEntryAtomIndex
                : selectedTrunkSegment.ExpressCorridorStartAtomIndex;
            return math.max(0, targetAtomIndex - cursorAtomIndex);
        }

        private static int CompareSceneExpressVehicleCandidate(
            SceneExpressVehicleCandidate left,
            SceneExpressVehicleCandidate right)
        {
            if (left.EntryDistanceAtoms != right.EntryDistanceAtoms)
                return left.EntryDistanceAtoms.CompareTo(right.EntryDistanceAtoms);

            bool leftOnTrunk = left.ExpressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                || left.ExpressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical;
            bool rightOnTrunk = right.ExpressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                || right.ExpressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical;
            if (leftOnTrunk != rightOnTrunk)
                return leftOnTrunk ? -1 : 1;

            if (left.RelevantSharedEntryAtomIndex != right.RelevantSharedEntryAtomIndex)
                return left.RelevantSharedEntryAtomIndex.CompareTo(right.RelevantSharedEntryAtomIndex);

            int leftCursorAtomIndex = left.RunningVehicle.HasTrackCursor ? left.RunningVehicle.TrackCursor.AtomCursorIndex : int.MaxValue;
            int rightCursorAtomIndex = right.RunningVehicle.HasTrackCursor ? right.RunningVehicle.TrackCursor.AtomCursorIndex : int.MaxValue;
            if (leftCursorAtomIndex != rightCursorAtomIndex)
                return leftCursorAtomIndex.CompareTo(rightCursorAtomIndex);

            if (left.ExpressLine != right.ExpressLine)
                return left.ExpressLine.Index.CompareTo(right.ExpressLine.Index);

            return left.ExpressVehicle.Index.CompareTo(right.ExpressVehicle.Index);
        }

        private static void InsertSceneExpressFrontierCandidate(
            SceneExpressFrontierAccumulator frontier,
            SceneExpressVehicleCandidate candidate)
        {
            frontier.AdmittedCandidateCount++;
            if (!frontier.HasPrimaryCandidate)
            {
                frontier.PrimaryCandidate = candidate;
                frontier.HasPrimaryCandidate = true;
                return;
            }

            if (CompareSceneExpressVehicleCandidate(candidate, frontier.PrimaryCandidate) < 0)
            {
                if (!frontier.HasSecondaryCandidate
                    || CompareSceneExpressVehicleCandidate(frontier.PrimaryCandidate, frontier.SecondaryCandidate) < 0)
                {
                    frontier.SecondaryCandidate = frontier.PrimaryCandidate;
                    frontier.HasSecondaryCandidate = true;
                }

                frontier.PrimaryCandidate = candidate;
                return;
            }

            if (!frontier.HasSecondaryCandidate
                || CompareSceneExpressVehicleCandidate(candidate, frontier.SecondaryCandidate) < 0)
            {
                frontier.SecondaryCandidate = candidate;
                frontier.HasSecondaryCandidate = true;
            }
        }

        private static SceneExpressFrontier BuildSceneExpressFrontier(
            SceneExpressFrontierAccumulator frontier)
        {
            return new SceneExpressFrontier(
                frontier.Relation,
                frontier.HasPrimaryCandidate,
                frontier.PrimaryCandidate,
                frontier.HasSecondaryCandidate,
                frontier.SecondaryCandidate,
                frontier.AdmittedCandidateCount);
        }

        private BypassLatchedBlockerProjection BuildLatchedBlockerProjection(
            SceneExpressVehicleCandidate candidate)
        {
            return new BypassLatchedBlockerProjection(
                candidate.ExpressLine,
                candidate.ExpressProtectedInterval,
                candidate.SelectedTrunkSegment,
                candidate.ExpressChain != null ? candidate.ExpressChain.Signature : 0UL,
                m_SharedTrackIndexVersion);
        }

        private void RecordSceneExpressLineQueryProbe(Entity expressLine, uint nowFrame)
        {
            if (expressLine == Entity.Null)
                return;

            m_PerfProbeSceneExpressLineQueries++;
            if (m_PerfProbeSceneExpressLineLastQueryFrame.TryGetValue(expressLine, out uint lastQueryFrame))
            {
                if (lastQueryFrame == nowFrame)
                {
                    m_PerfProbeSceneExpressLineSameFrameRequeries++;
                }
                else
                {
                    if (lastQueryFrame + 1 == nowFrame)
                        m_PerfProbeSceneExpressLineConsecutiveFrameRequeries++;
                    if (nowFrame > lastQueryFrame
                        && nowFrame - lastQueryFrame <= PERF_PROBE_SCENE_EXPRESS_LINE_RECENT_WINDOW_FRAMES)
                    {
                        m_PerfProbeSceneExpressLineRecentFrameRequeries++;
                    }
                }
            }

            m_PerfProbeSceneExpressLineLastQueryFrame[expressLine] = nowFrame;
        }

        private static bool TryGetOrderedLinePhaseRange(
            LineOrderedRuntimeState orderedState,
            int traversalPhaseIndex,
            out OrderedLinePhaseRange phaseRange)
        {
            phaseRange = default;
            if (orderedState == null)
                return false;

            for (int i = 0; i < orderedState.PhaseRanges.Count; i++)
            {
                OrderedLinePhaseRange candidate = orderedState.PhaseRanges[i];
                if (candidate.TraversalPhaseIndex != traversalPhaseIndex)
                    continue;

                phaseRange = candidate;
                return true;
            }

            return false;
        }

        private bool TryBuildOrderedSceneQueryWindows(
            LineOrderedRuntimeState orderedState,
            SceneExpressRelation relation,
            out List<OrderedSceneQueryWindow> queryWindows)
        {
            queryWindows = null;
            if (orderedState == null
                || relation.TrunkCandidates == null
                || relation.TrunkCandidates.Segments.Count == 0)
            {
                return false;
            }

            orderedState.ScratchQueryWindows.Clear();
            for (int segmentIndex = 0; segmentIndex < relation.TrunkCandidates.Segments.Count; segmentIndex++)
            {
                GlobalSharedTrunkSegment segment = relation.TrunkCandidates.Segments[segmentIndex];
                int candidateStartAtomIndex = math.max(segment.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
                int candidateEndAtomExclusive = math.min(segment.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
                if (candidateEndAtomExclusive <= candidateStartAtomIndex)
                    continue;

                for (int phaseRangeIndex = 0; phaseRangeIndex < orderedState.PhaseRanges.Count; phaseRangeIndex++)
                {
                    OrderedLinePhaseRange phaseRange = orderedState.PhaseRanges[phaseRangeIndex];
                    int overlapStartAtomIndex = math.max(candidateStartAtomIndex, phaseRange.StartAtomIndex);
                    int overlapEndAtomExclusive = math.min(candidateEndAtomExclusive, phaseRange.EndAtomIndexExclusive);
                    if (overlapEndAtomExclusive <= overlapStartAtomIndex)
                        continue;

                    bool merged = false;
                    for (int windowIndex = 0; windowIndex < orderedState.ScratchQueryWindows.Count; windowIndex++)
                    {
                        OrderedSceneQueryWindow window = orderedState.ScratchQueryWindows[windowIndex];
                        if (window.TraversalPhaseIndex != phaseRange.TraversalPhaseIndex)
                            continue;

                        orderedState.ScratchQueryWindows[windowIndex] = new OrderedSceneQueryWindow(
                            window.TraversalPhaseIndex,
                            math.min(window.StartAtomIndex, overlapStartAtomIndex),
                            math.max(window.EndAtomIndexExclusive, overlapEndAtomExclusive));
                        merged = true;
                        break;
                    }

                    if (!merged)
                    {
                        orderedState.ScratchQueryWindows.Add(new OrderedSceneQueryWindow(
                            phaseRange.TraversalPhaseIndex,
                            overlapStartAtomIndex,
                            overlapEndAtomExclusive));
                    }
                }
            }

            if (orderedState.ScratchQueryWindows.Count == 0)
                return false;

            queryWindows = orderedState.ScratchQueryWindows;
            return true;
        }

        private static bool IsOrderedEntryEligibleForThreatWindow(
            OrderedLineVehicleEntry orderedEntry,
            OrderedSceneQueryWindow queryWindow)
        {
            return orderedEntry.OwnLineAtomCoordinate < queryWindow.EndAtomIndexExclusive;
        }

        private static int CompareOrderedThreatHeadCandidate(
            OrderedLineVehicleEntry leftEntry,
            OrderedSceneQueryWindow leftWindow,
            OrderedLineVehicleEntry rightEntry,
            OrderedSceneQueryWindow rightWindow,
            bool hasRelevantSharedEntryAtomIndex,
            int relevantSharedEntryAtomIndex)
        {
            float leftAnchor = hasRelevantSharedEntryAtomIndex
                ? relevantSharedEntryAtomIndex
                : leftWindow.StartAtomIndex;
            float rightAnchor = hasRelevantSharedEntryAtomIndex
                ? relevantSharedEntryAtomIndex
                : rightWindow.StartAtomIndex;
            float leftCoordinate = leftEntry.OwnLineAtomCoordinate;
            float rightCoordinate = rightEntry.OwnLineAtomCoordinate;

            if (hasRelevantSharedEntryAtomIndex)
            {
                float leftDistance = math.max(0f, leftAnchor - leftCoordinate);
                float rightDistance = math.max(0f, rightAnchor - rightCoordinate);
                if (leftDistance != rightDistance)
                    return leftDistance.CompareTo(rightDistance);
                if (leftCoordinate != rightCoordinate)
                    return rightCoordinate.CompareTo(leftCoordinate);
                return leftEntry.Vehicle.Index.CompareTo(rightEntry.Vehicle.Index);
            }

            bool leftOnOrInside = leftCoordinate >= leftAnchor;
            bool rightOnOrInside = rightCoordinate >= rightAnchor;
            if (leftOnOrInside != rightOnOrInside)
                return leftOnOrInside ? -1 : 1;

            float leftDistanceToAnchor = math.abs(leftCoordinate - leftAnchor);
            float rightDistanceToAnchor = math.abs(rightCoordinate - rightAnchor);
            if (leftDistanceToAnchor != rightDistanceToAnchor)
                return leftDistanceToAnchor.CompareTo(rightDistanceToAnchor);

            if (leftOnOrInside)
            {
                if (leftCoordinate != rightCoordinate)
                    return leftCoordinate.CompareTo(rightCoordinate);
            }
            else
            {
                if (leftCoordinate != rightCoordinate)
                    return rightCoordinate.CompareTo(leftCoordinate);
            }

            return leftEntry.Vehicle.Index.CompareTo(rightEntry.Vehicle.Index);
        }

        private static int GetOrderedThreatDirectionRank(RelativeToTrunkState expressTrunkState)
        {
            if (expressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                || expressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical)
            {
                return 2;
            }

            if (expressTrunkState == RelativeToTrunkState.ApproachingTrunkAlongCanonical
                || expressTrunkState == RelativeToTrunkState.ApproachingTrunkAgainstCanonical)
            {
                return 1;
            }

            return 0;
        }

        private bool TryGetOrderedThreatDirectionRank(
            SceneExpressRelation relation,
            OrderedSceneQueryWindow queryWindow,
            OrderedLineVehicleEntry orderedEntry,
            out int directionRank)
        {
            directionRank = 0;
            if (relation.ExpressChain == null
                || relation.TrunkCandidates == null
                || relation.TrunkCandidates.Segments.Count == 0
                || !orderedEntry.RunningVehicle.HasTrackCursor)
            {
                return false;
            }

            for (int segmentIndex = 0; segmentIndex < relation.TrunkCandidates.Segments.Count; segmentIndex++)
            {
                GlobalSharedTrunkSegment segment = relation.TrunkCandidates.Segments[segmentIndex];
                int candidateStartAtomIndex = math.max(segment.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
                int candidateEndAtomExclusive = math.min(segment.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
                int overlapStartAtomIndex = math.max(candidateStartAtomIndex, queryWindow.StartAtomIndex);
                int overlapEndAtomExclusive = math.min(candidateEndAtomExclusive, queryWindow.EndAtomIndexExclusive);
                if (overlapEndAtomExclusive <= overlapStartAtomIndex)
                    continue;

                RelativeToTrunkState expressTrunkState = ResolveVehicleTrunkTravelState(
                    orderedEntry.RunningVehicle,
                    segment,
                    useLocalSide: false);
                if (!IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                    || !IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, segment))
                {
                    continue;
                }

                int candidateRank = GetOrderedThreatDirectionRank(expressTrunkState);
                if (candidateRank > directionRank)
                    directionRank = candidateRank;
            }

            return directionRank > 0;
        }

        private bool TryBuildOrderedThreatHeadCandidates(
            LineOrderedRuntimeState orderedState,
            SceneExpressRelation relation,
            Entity currentBypassBuilding,
            out bool hasPrimaryThreat,
            out OrderedLineVehicleEntry primaryThreat,
            out OrderedSceneQueryWindow primaryThreatWindow,
            out bool hasSecondaryThreat,
            out OrderedLineVehicleEntry secondaryThreat,
            out OrderedSceneQueryWindow secondaryThreatWindow,
            out bool hasSameStationThreat,
            out OrderedLineVehicleEntry sameStationThreat,
            out OrderedSceneQueryWindow sameStationThreatWindow)
        {
            hasPrimaryThreat = false;
            primaryThreat = default;
            primaryThreatWindow = default;
            hasSecondaryThreat = false;
            secondaryThreat = default;
            secondaryThreatWindow = default;
            hasSameStationThreat = false;
            sameStationThreat = default;
            sameStationThreatWindow = default;
            int primaryThreatDirectionRank = 0;
            int secondaryThreatDirectionRank = 0;
            int sameStationThreatDirectionRank = 0;

            if (!TryBuildOrderedSceneQueryWindows(orderedState, relation, out List<OrderedSceneQueryWindow> orderedQueryWindows))
                return false;

            for (int windowIndex = 0; windowIndex < orderedQueryWindows.Count; windowIndex++)
            {
                OrderedSceneQueryWindow queryWindow = orderedQueryWindows[windowIndex];
                if (!TryGetOrderedLinePhaseRange(orderedState, queryWindow.TraversalPhaseIndex, out OrderedLinePhaseRange phaseRange))
                    continue;

                for (int entryIndex = phaseRange.StartEntryIndex; entryIndex < phaseRange.EndEntryIndexExclusive; entryIndex++)
                {
                    OrderedLineVehicleEntry orderedEntry = orderedState.Entries[entryIndex];
                    if (!IsOrderedEntryEligibleForThreatWindow(orderedEntry, queryWindow))
                        continue;
                    if (relation.HasRelevantSharedEntryAtomIndex
                        && orderedEntry.RunningVehicle.HasTrackCursor
                        && orderedEntry.RunningVehicle.TrackCursor.AtomCursorIndex > relation.RelevantSharedEntryAtomIndex)
                    {
                        continue;
                    }
                    if (!TryGetOrderedThreatDirectionRank(
                            relation,
                            queryWindow,
                            orderedEntry,
                            out int directionRank))
                    {
                        continue;
                    }

                    bool cursorWithinSameStationPresence = orderedEntry.RunningVehicle.HasTrackCursor
                        && IsTrackCursorWithinBypassStationPhysicalContext(
                            relation.ExpressChain,
                            orderedEntry.RunningVehicle.TrackCursor,
                            currentBypassBuilding);

                    if (!hasPrimaryThreat
                        || directionRank > primaryThreatDirectionRank
                        || (directionRank == primaryThreatDirectionRank
                            && CompareOrderedThreatHeadCandidate(
                                orderedEntry,
                                queryWindow,
                                primaryThreat,
                                primaryThreatWindow,
                                relation.HasRelevantSharedEntryAtomIndex,
                                relation.RelevantSharedEntryAtomIndex) < 0))
                    {
                        secondaryThreat = primaryThreat;
                        secondaryThreatWindow = primaryThreatWindow;
                        secondaryThreatDirectionRank = primaryThreatDirectionRank;
                        hasSecondaryThreat = hasPrimaryThreat;
                        primaryThreat = orderedEntry;
                        primaryThreatWindow = queryWindow;
                        primaryThreatDirectionRank = directionRank;
                        hasPrimaryThreat = true;
                    }
                    else if ((!hasSecondaryThreat
                            || directionRank > secondaryThreatDirectionRank
                            || (directionRank == secondaryThreatDirectionRank
                                && CompareOrderedThreatHeadCandidate(
                                    orderedEntry,
                                    queryWindow,
                                    secondaryThreat,
                                    secondaryThreatWindow,
                                    relation.HasRelevantSharedEntryAtomIndex,
                                    relation.RelevantSharedEntryAtomIndex) < 0))
                        && orderedEntry.Vehicle != primaryThreat.Vehicle)
                    {
                        secondaryThreat = orderedEntry;
                        secondaryThreatWindow = queryWindow;
                        secondaryThreatDirectionRank = directionRank;
                        hasSecondaryThreat = true;
                    }

                    if (cursorWithinSameStationPresence
                        && (!hasSameStationThreat
                            || directionRank > sameStationThreatDirectionRank
                            || (directionRank == sameStationThreatDirectionRank
                                && CompareOrderedThreatHeadCandidate(
                                    orderedEntry,
                                    queryWindow,
                                    sameStationThreat,
                                    sameStationThreatWindow,
                                    relation.HasRelevantSharedEntryAtomIndex,
                                    relation.RelevantSharedEntryAtomIndex) < 0)))
                    {
                        sameStationThreat = orderedEntry;
                        sameStationThreatWindow = queryWindow;
                        sameStationThreatDirectionRank = directionRank;
                        hasSameStationThreat = true;
                    }
                }
            }

            return hasPrimaryThreat || hasSecondaryThreat || hasSameStationThreat;
        }

        private bool TryBuildAndInsertSceneExpressVehicleCandidate(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            LineTrackChain localChain,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            TrackModelRuntimePosition localPosition,
            SceneExpressRelation relation,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            LineRunningVehicleSnapshot runningVehicle,
            SceneExpressFrontierAccumulator frontier,
            List<SceneExpressVehicleCandidate> sameStationCandidates,
            out string diagnosticRejectReason)
        {
            diagnosticRejectReason = string.Empty;
            if (!TryBuildSceneExpressVehicleCandidate(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    localChain,
                    protectedIntervalIndex,
                    protectedInterval,
                    currentBypassBuilding,
                    localPosition,
                    relation,
                    expressWaypoints,
                    runningVehicle,
                    out SceneExpressVehicleCandidate candidate,
                    out diagnosticRejectReason))
            {
                return false;
            }

            InsertSceneExpressFrontierCandidate(frontier, candidate);
            sameStationCandidates?.Add(candidate);
            m_BypassPerfProbeSceneAdmittedCandidates++;
            return true;
        }

        private bool TryBuildSceneExpressVehicleCandidate(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            LineTrackChain localChain,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            TrackModelRuntimePosition localPosition,
            SceneExpressRelation relation,
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            LineRunningVehicleSnapshot runningVehicle,
            out SceneExpressVehicleCandidate candidate,
            out string diagnosticRejectReason)
        {
            candidate = default;
            diagnosticRejectReason = string.Empty;
            Entity expressVehicle = runningVehicle.Vehicle;
            Entity expressLine = relation.ExpressLine;
            LineTrackChain expressChain = relation.ExpressChain;
            if (expressVehicle == localVehicle
                || expressVehicle == Entity.Null
                || !EntityManager.Exists(expressVehicle)
                || expressLine == Entity.Null
                || expressChain == null)
            {
                diagnosticRejectReason = "pre-invalid";
                return false;
            }

            int expressProtectedIntervalIndex = relation.ExpressProtectedIntervalIndex;
            BypassProtectedInterval expressProtectedInterval = relation.ExpressProtectedInterval;
            int overlapCount = relation.OverlapCount;
            int orderedRun = relation.OrderedRun;
            string intervalResolutionSource = relation.ResolutionSource;
            bool hasRelevantSharedEntryAtomIndex = relation.HasRelevantSharedEntryAtomIndex;
            int relevantSharedEntryAtomIndex = relation.RelevantSharedEntryAtomIndex;

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
                diagnosticRejectReason = "cursor-fail";
                return false;
            }

            if (hasRelevantSharedEntryAtomIndex
                && IsVehicleClearlyPastExpressAtom(runningVehicle, relevantSharedEntryAtomIndex))
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
                    "past-current-shared-entry atom=" + relevantSharedEntryAtomIndex);
                diagnosticRejectReason = "past-current-shared-entry";
                return false;
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

            if (!TryFindBestCurrentSceneRelationTrunkSegment(
                    relation,
                    protectedInterval,
                    localPosition.TraversalPhaseIndex,
                    expressCursor.AtomCursorIndex,
                    runningVehicle.TraversalPhaseIndex,
                    runningVehicle.PhaseEndAtomExclusive,
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
                diagnosticRejectReason = "static-opposite-direction";
                return false;
            }

            RelativeToTrunkState localTrunkState = ResolveVehicleTrunkTravelState(
                localPosition,
                selectedTrunkSegment,
                useLocalSide: true);
            RelativeToTrunkState expressTrunkState = ResolveVehicleTrunkTravelState(
                runningVehicle,
                selectedTrunkSegment,
                useLocalSide: false);
            if (!selectedTrunkSegment.HasCanonicalDirection
                || !IsRelativeToTrunkStateDirectionCompatibleWithCanonicalSide(localTrunkState, selectedTrunkSegment.LocalAlongCanonical)
                || !IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, selectedTrunkSegment))
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
                        "trunk-state local=" + FormatRelativeToTrunkState(localTrunkState)
                            + " express=" + FormatRelativeToTrunkState(expressTrunkState)
                            + " localCanon=" + FormatCanonicalSide(selectedTrunkSegment.LocalAlongCanonical)
                            + " expressCanon=" + FormatCanonicalSide(selectedTrunkSegment.ExpressAlongCanonical));
                }
                diagnosticRejectReason = "trunk-state";
                return false;
            }

            int effectiveRelevantSharedEntryAtomIndex = hasRelevantSharedEntryAtomIndex
                ? relevantSharedEntryAtomIndex
                : selectedTrunkSegment.ExpressCorridorStartAtomIndex;
            int entryDistanceAtoms = ComputeSceneEntryDistanceAtoms(
                runningVehicle,
                selectedTrunkSegment,
                expressTrunkState,
                effectiveRelevantSharedEntryAtomIndex);
            int expressWaypointIndex = ComputeWpIndex(expressVehicle, expressWaypoints);
            bool expressCurrentWaypointMatchesBypassBuilding = expressWaypointIndex >= 0
                && expressWaypointIndex < expressWaypoints.Length
                && GetStationBuildingForWaypoint(expressWaypoints, expressWaypointIndex) == currentBypassBuilding;
            candidate = new SceneExpressVehicleCandidate(
                relation,
                expressLine,
                expressVehicle,
                expressChain,
                runningVehicle,
                expressProtectedIntervalIndex,
                expressProtectedInterval,
                overlapCount,
                orderedRun,
                intervalResolutionSource,
                selectedTrunkSegment,
                BuildTrunkSkeleton(selectedTrunkSegment),
                localTrunkState,
                expressTrunkState,
                expressCurrentWaypointMatchesBypassBuilding,
                effectiveRelevantSharedEntryAtomIndex,
                entryDistanceAtoms);
            return true;
        }

        private static string FormatOrderedThreatHeadCase(
            bool available,
            OrderedLineVehicleEntry orderedEntry,
            string result)
        {
            if (!available || orderedEntry.Vehicle == Entity.Null)
                return "none";

            return "v=" + orderedEntry.Vehicle.Index
                + " atom=" + (orderedEntry.RunningVehicle.HasTrackCursor ? orderedEntry.RunningVehicle.TrackCursor.AtomCursorIndex.ToString() : "-")
                + " coord=" + orderedEntry.OwnLineAtomCoordinate.ToString("0.0")
                + " phase=" + orderedEntry.TraversalPhaseIndex
                + " phaseEnd=" + orderedEntry.TraversalPhaseEndAtomExclusive
                + " result=" + (string.IsNullOrWhiteSpace(result) ? "ok" : result);
        }

        private void LogLineOrderedFallbackCase(
            Entity localVehicle,
            Entity localLine,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity expressLine,
            uint nowFrame,
            bool hasPrimaryThreat,
            OrderedLineVehicleEntry primaryThreat,
            string primaryResult,
            bool hasSecondaryThreat,
            OrderedLineVehicleEntry secondaryThreat,
            string secondaryResult,
            bool hasSameStationThreat,
            OrderedLineVehicleEntry sameStationThreat,
            string sameStationResult,
            string fallbackReason)
        {
            return;
        }

        private bool TryCollectSceneExpressFrontiers(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            LineTrackChain localChain,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity currentBypassBuilding,
            TrackModelRuntimePosition localPosition,
            bool collectSameStationCandidates,
            uint nowFrame,
            out List<SceneExpressFrontier> frontiers,
            out List<SceneExpressVehicleCandidate> sameStationCandidates,
            out string fatalReason)
        {
            frontiers = new List<SceneExpressFrontier>();
            sameStationCandidates = collectSameStationCandidates
                ? new List<SceneExpressVehicleCandidate>()
                : null;
            fatalReason = string.Empty;
            m_BypassPerfProbeSceneSamples++;
            EnsureLineBypassExecutionModeReady(localChain, localWaypoints);

            List<Entity> candidateExpressLines = GetCandidateExpressLinesForLocalScene(localChain, protectedInterval, currentBypassBuilding);
            if (candidateExpressLines == null || candidateExpressLines.Count == 0)
                return true;

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            for (int candidateIndex = 0; candidateIndex < candidateExpressLines.Count; candidateIndex++)
            {
                Entity expressLine = candidateExpressLines[candidateIndex];
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !EntityManager.Exists(expressLine)
                    || !EntityManager.HasComponent<TransportLine>(expressLine)
                    || !IsAppliedWorkbenchExpressLine(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                {
                    continue;
                }

                if (!TryBuildSceneExpressRelation(
                        localChain,
                        protectedIntervalIndex,
                        protectedInterval,
                        currentBypassBuilding,
                        expressLine,
                        expressWaypoints,
                        out SceneExpressRelation relation))
                    continue;

                if (relation.Ambiguous)
                {
                    fatalReason = "shared-window-match-ambiguous";
                    return false;
                }

                LineTrackChain expressChain = relation.ExpressChain;
                if (expressChain == null)
                    continue;

                if (!TryGetLineRunningVehicleFrameSnapshot(expressLine, expressWaypoints, nowFrame, out LineRunningVehicleFrameSnapshot runningSnapshot))
                    continue;

                int expressProtectedIntervalIndex = relation.ExpressProtectedIntervalIndex;
                BypassProtectedInterval expressProtectedInterval = relation.ExpressProtectedInterval;
                int overlapCount = relation.OverlapCount;
                int orderedRun = relation.OrderedRun;
                string intervalResolutionSource = relation.ResolutionSource;
                bool hasRelevantSharedEntryAtomIndex = relation.HasRelevantSharedEntryAtomIndex;
                int relevantSharedEntryAtomIndex = relation.RelevantSharedEntryAtomIndex;

                if (overlapCount < MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS
                    || orderedRun < MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN)
                {
                    continue;
                }

                RecordSceneExpressLineQueryProbe(expressLine, nowFrame);
                m_LineOrderedProbeExpressLineQueries++;
                SceneExpressFrontierAccumulator frontier = new SceneExpressFrontierAccumulator(relation);
                LineOrderedRuntimeState orderedState = null;
                bool useOrderedRuntime = localChain.ExecutionMode == BypassExecutionMode.ComplexLineModel
                    && TryGetLineOrderedRuntimeState(expressLine, expressWaypoints, nowFrame, out orderedState);
                if (useOrderedRuntime)
                {
                    m_LineOrderedProbeOrderedAttempts++;
                    bool usedThreatHeadFallback = false;
                    SceneExpressFrontierAccumulator threatHeadFrontier = new SceneExpressFrontierAccumulator(relation);
                    List<SceneExpressVehicleCandidate> threatHeadSameStationCandidates = sameStationCandidates != null
                        ? new List<SceneExpressVehicleCandidate>()
                        : null;
                    string primaryThreatResult = string.Empty;
                    string secondaryThreatResult = string.Empty;
                    string sameStationThreatResult = string.Empty;
                    bool hasPrimaryThreat = false;
                    OrderedLineVehicleEntry primaryThreat = default;
                    bool hasSecondaryThreat = false;
                    OrderedLineVehicleEntry secondaryThreat = default;
                    bool hasSameStationThreat = false;
                    OrderedLineVehicleEntry sameStationThreat = default;
                    if (TryBuildOrderedThreatHeadCandidates(
                            orderedState,
                            relation,
                            currentBypassBuilding,
                            out hasPrimaryThreat,
                            out primaryThreat,
                            out _,
                            out hasSecondaryThreat,
                            out secondaryThreat,
                            out _,
                            out hasSameStationThreat,
                            out sameStationThreat,
                            out _))
                    {
                        if (hasPrimaryThreat)
                        {
                            m_BypassPerfProbeSceneCandidateVehicles++;
                            m_LineOrderedProbeHeadCandidateBuilds++;
                            TryBuildAndInsertSceneExpressVehicleCandidate(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                currentWaypointIndex,
                                localChain,
                                protectedIntervalIndex,
                                protectedInterval,
                                currentBypassBuilding,
                                localPosition,
                                relation,
                                expressWaypoints,
                                primaryThreat.RunningVehicle,
                                threatHeadFrontier,
                                threatHeadSameStationCandidates,
                                out primaryThreatResult);
                        }

                        if (hasSecondaryThreat && secondaryThreat.Vehicle != primaryThreat.Vehicle)
                        {
                            m_BypassPerfProbeSceneCandidateVehicles++;
                            m_LineOrderedProbeHeadCandidateBuilds++;
                            TryBuildAndInsertSceneExpressVehicleCandidate(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                currentWaypointIndex,
                                localChain,
                                protectedIntervalIndex,
                                protectedInterval,
                                currentBypassBuilding,
                                localPosition,
                                relation,
                                expressWaypoints,
                                secondaryThreat.RunningVehicle,
                                threatHeadFrontier,
                                threatHeadSameStationCandidates,
                                out secondaryThreatResult);
                        }

                        if (hasSameStationThreat
                            && sameStationThreat.Vehicle != primaryThreat.Vehicle
                            && (!hasSecondaryThreat || sameStationThreat.Vehicle != secondaryThreat.Vehicle))
                        {
                            m_BypassPerfProbeSceneCandidateVehicles++;
                            m_LineOrderedProbeHeadCandidateBuilds++;
                            TryBuildAndInsertSceneExpressVehicleCandidate(
                                localVehicle,
                                localLine,
                                localWaypoints,
                                currentWaypointIndex,
                                localChain,
                                protectedIntervalIndex,
                                protectedInterval,
                                currentBypassBuilding,
                                localPosition,
                                relation,
                                expressWaypoints,
                                sameStationThreat.RunningVehicle,
                                threatHeadFrontier,
                                threatHeadSameStationCandidates,
                                out sameStationThreatResult);
                        }

                        if (!threatHeadFrontier.HasPrimaryCandidate)
                            usedThreatHeadFallback = true;
                    }
                    else
                    {
                        usedThreatHeadFallback = true;
                    }

                    if (!usedThreatHeadFallback)
                    {
                        m_LineOrderedProbeHeadOnlySuccesses++;
                        frontier = threatHeadFrontier;
                        if (sameStationCandidates != null && threatHeadSameStationCandidates != null)
                            sameStationCandidates.AddRange(threatHeadSameStationCandidates);
                        if (frontier.HasPrimaryCandidate)
                            frontiers.Add(BuildSceneExpressFrontier(frontier));
                        continue;
                    }

                    m_LineOrderedProbeFallbacks++;
                    LogLineOrderedFallbackCase(
                        localVehicle,
                        localLine,
                        currentWaypointIndex,
                        currentBypassBuilding,
                        expressLine,
                        nowFrame,
                        hasPrimaryThreat,
                        primaryThreat,
                        primaryThreatResult,
                        hasSecondaryThreat,
                        secondaryThreat,
                        secondaryThreatResult,
                        hasSameStationThreat,
                        sameStationThreat,
                        sameStationThreatResult,
                        hasPrimaryThreat || hasSecondaryThreat || hasSameStationThreat
                            ? "head-no-primary"
                            : "no-threat-head");
                }

                for (int rvIndex = 0; rvIndex < runningSnapshot.Vehicles.Count; rvIndex++)
                {
                    LineRunningVehicleSnapshot runningVehicle = runningSnapshot.Vehicles[rvIndex];
                    m_BypassPerfProbeSceneCandidateVehicles++;
                    if (useOrderedRuntime)
                        m_LineOrderedProbeFallbackCandidateBuilds++;
                    if (!TryBuildAndInsertSceneExpressVehicleCandidate(
                            localVehicle,
                            localLine,
                            localWaypoints,
                            currentWaypointIndex,
                            localChain,
                            protectedIntervalIndex,
                            protectedInterval,
                            currentBypassBuilding,
                            localPosition,
                            relation,
                            expressWaypoints,
                            runningVehicle,
                            frontier,
                            sameStationCandidates,
                            out _))
                    {
                        continue;
                    }
                }

                if (frontier.HasPrimaryCandidate)
                    frontiers.Add(BuildSceneExpressFrontier(frontier));
            }

            frontiers.Sort(CompareSceneExpressFrontier);
            m_BypassPerfProbeSceneFrontiers += (ulong)frontiers.Count;
            if (sameStationCandidates != null)
                sameStationCandidates.Sort(CompareSceneExpressVehicleCandidate);
            return true;
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

        private static int CompareSceneExpressFrontier(
            SceneExpressFrontier left,
            SceneExpressFrontier right)
        {
            if (left.HasPrimaryCandidate != right.HasPrimaryCandidate)
                return left.HasPrimaryCandidate ? -1 : 1;
            if (!left.HasPrimaryCandidate || !right.HasPrimaryCandidate)
                return 0;
            return CompareSceneExpressVehicleCandidate(left.PrimaryCandidate, right.PrimaryCandidate);
        }

        private static bool IsRelativeToTrunkStateSameStationPresentEligible(RelativeToTrunkState state)
        {
            return state == RelativeToTrunkState.OnTrunkAlongCanonical
                || state == RelativeToTrunkState.OnTrunkAgainstCanonical;
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
            LineTrackChain localChain,
            List<SceneExpressVehicleCandidate> orderedCandidates,
            TrackModelRuntimePosition localPosition,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            float intervalDisplayLength,
            out SceneExpressVehicleCandidate blockerCandidate,
            out Entity blockerVehicle)
        {
            blockerCandidate = default;
            blockerVehicle = Entity.Null;
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || localChain == null
                || orderedCandidates == null
                || orderedCandidates.Count == 0
                || currentBypassBuilding == Entity.Null)
            {
                return false;
            }

            m_BypassPerfProbeSameStationCalls++;
            m_BypassPerfProbeSameStationReusedCandidates += (ulong)orderedCandidates.Count;

            bool found = false;
            Entity bestExpressLine = Entity.Null;
            int bestExpressProtectedIntervalIndex = -1;
            int bestOverlapCount = 0;
            int bestOrderedRun = 0;
            int bestExpressAtomCursorIndex = -1;
            int bestExpressPhaseEndAtomExclusive = -1;
            string bestExpressPositionText = string.Empty;
            Entity firstMissCandidate = Entity.Null;
            string firstMissReason = string.Empty;

            for (int candidateIndex = 0; candidateIndex < orderedCandidates.Count; candidateIndex++)
            {
                SceneExpressVehicleCandidate candidate = orderedCandidates[candidateIndex];
                Entity expressVehicle = candidate.ExpressVehicle;
                LineRunningVehicleSnapshot runningVehicle = candidate.RunningVehicle;
                bool cursorWithinSameStationPresence = runningVehicle.HasTrackCursor
                    && IsTrackCursorWithinBypassStationPhysicalContext(candidate.ExpressChain, runningVehicle.TrackCursor, currentBypassBuilding);
                bool expressWithinSameStationPresence = runningVehicle.HasTrackCursor
                    && cursorWithinSameStationPresence;
                if (!expressWithinSameStationPresence
                    && runningVehicle.Boarding
                    && candidate.ExpressCurrentWaypointMatchesBypassBuilding)
                {
                    expressWithinSameStationPresence = true;
                }

                if (expressVehicle == Entity.Null
                    || expressVehicle == localVehicle
                    || !EntityManager.Exists(expressVehicle)
                    || !expressWithinSameStationPresence)
                {
                    if (firstMissCandidate == Entity.Null && expressVehicle != Entity.Null && expressVehicle != localVehicle)
                    {
                        firstMissCandidate = expressVehicle;
                        firstMissReason = "presence-miss cursor=" + (cursorWithinSameStationPresence ? "1" : "0")
                            + " boarding=" + (runningVehicle.Boarding ? "1" : "0")
                            + " wpMatch=" + (candidate.ExpressCurrentWaypointMatchesBypassBuilding ? "1" : "0");
                    }
                    continue;
                }

                if (!TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
                        runningVehicle,
                        candidate.ExpressProtectedInterval,
                        out TrackModelRuntimePosition expressPosition))
                {
                    if (firstMissCandidate == Entity.Null)
                    {
                        firstMissCandidate = expressVehicle;
                        firstMissReason = "projection-fail";
                    }
                    continue;
                }

                float expressCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                    expressPosition,
                    candidate.ExpressProtectedInterval,
                    intervalDisplayLength,
                    includeApproachers: true,
                    out bool includeExpressCoordinate);
                if (!IsExpressApproachingCurrentBypassStation(
                        localChain,
                        localProtectedInterval,
                        currentBypassBuilding,
                        expressPosition,
                        expressCoordinate,
                        includeExpressCoordinate))
                {
                    if (firstMissCandidate == Entity.Null)
                    {
                        firstMissCandidate = expressVehicle;
                        firstMissReason = "approach-fail rel=" + expressPosition.RelativeToProtectedInterval
                            + " mapped=" + expressCoordinate.ToString("0.00")
                            + " include=" + (includeExpressCoordinate ? "1" : "0");
                    }
                    continue;
                }

                found = true;
                blockerCandidate = candidate;
                blockerVehicle = expressVehicle;
                bestExpressLine = candidate.ExpressLine;
                bestExpressProtectedIntervalIndex = candidate.ExpressProtectedIntervalIndex;
                bestOverlapCount = candidate.OverlapCount;
                bestOrderedRun = candidate.OrderedRun;
                bestExpressAtomCursorIndex = runningVehicle.TrackCursor.AtomCursorIndex;
                bestExpressPhaseEndAtomExclusive = runningVehicle.PhaseEndAtomExclusive;
                bestExpressPositionText = "trunkState[local=" + FormatRelativeToTrunkState(candidate.LocalTrunkState)
                    + " express=" + FormatRelativeToTrunkState(candidate.ExpressTrunkState)
                    + " localCanon=" + FormatCanonicalSide(candidate.SelectedTrunkSegment.LocalAlongCanonical)
                    + " expressCanon=" + FormatCanonicalSide(candidate.SelectedTrunkSegment.ExpressAlongCanonical)
                    + "] station-present";
                break;
            }

            if (found)
            {
                LogBypassSelectedBlockerDetailOnce(
                    localVehicle,
                    localLine,
                    localProtectedIntervalIndex,
                    currentBypassBuilding,
                    blockerVehicle,
                    bestExpressLine,
                    bestExpressProtectedIntervalIndex,
                    "same-station",
                    bestOverlapCount,
                    bestOrderedRun,
                    bestExpressAtomCursorIndex,
                    bestExpressPhaseEndAtomExclusive,
                    bestExpressPositionText);
            }
            else if (firstMissCandidate != Entity.Null)
            {
                LogSameStationMissDiagnosticOnce(
                    localVehicle,
                    localLine,
                    localProtectedIntervalIndex,
                    currentBypassBuilding,
                    firstMissCandidate,
                    orderedCandidates.Count,
                    firstMissReason);
            }

            return found;
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

            m_BypassPerfProbeDeepCorridorEntries++;
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

        private bool TryProjectConflictCorridorsFromTrunkSkeleton(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            TrunkSkeleton trunkSkeleton,
            out ConflictCorridor localCorridor,
            out ConflictCorridor expressCorridor)
        {
            localCorridor = default;
            expressCorridor = default;
            if (localChain == null || expressChain == null)
                return false;

            int localStart = math.max(trunkSkeleton.LocalSharedStartAtomIndex, localProtectedInterval.StartAtomIndex);
            int localEndExclusive = math.min(trunkSkeleton.LocalSharedEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
            if (currentBypassBuilding != Entity.Null)
            {
                int prefixGapAtoms = math.max(0, localStart - localProtectedInterval.StartAtomIndex);
                if (prefixGapAtoms <= MAX_CONFLICT_CORRIDOR_GAP_ATOMS)
                    localStart = localProtectedInterval.StartAtomIndex;
            }

            int expressStart = math.max(trunkSkeleton.ExpressSharedStartAtomIndex, expressProtectedInterval.StartAtomIndex);
            int expressEndExclusive = math.min(trunkSkeleton.ExpressSharedEndAtomIndexExclusive, expressProtectedInterval.EndAtomIndexExclusive);
            if (localEndExclusive <= localStart || expressEndExclusive <= expressStart)
                return false;

            int localAnchorStart = math.clamp(trunkSkeleton.LocalAnchorStartAtomIndex, localStart, localEndExclusive - 1);
            int localAnchorEndExclusive = math.clamp(trunkSkeleton.LocalAnchorEndAtomIndexExclusive, localAnchorStart + 1, localEndExclusive);
            int expressAnchorStart = math.clamp(trunkSkeleton.ExpressAnchorStartAtomIndex, expressStart, expressEndExclusive - 1);
            int expressAnchorEndExclusive = math.clamp(trunkSkeleton.ExpressAnchorEndAtomIndexExclusive, expressAnchorStart + 1, expressEndExclusive);
            int localProtectedIntervalIndex = ResolveProtectedIntervalIndex(localChain, localProtectedInterval);
            int expressProtectedIntervalIndex = ResolveProtectedIntervalIndex(expressChain, expressProtectedInterval);

            localCorridor = new ConflictCorridor(
                localProtectedIntervalIndex,
                localStart,
                localEndExclusive,
                localAnchorStart,
                localAnchorEndExclusive,
                trunkSkeleton.LocalSharedSliceCount,
                trunkSkeleton.LocalBridgedGapAtoms);
            expressCorridor = new ConflictCorridor(
                expressProtectedIntervalIndex,
                expressStart,
                expressEndExclusive,
                expressAnchorStart,
                expressAnchorEndExclusive,
                trunkSkeleton.ExpressSharedSliceCount,
                trunkSkeleton.ExpressBridgedGapAtoms);
            return true;
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

        private static bool TryCollectOrderedSharedPhysicalKeyOrders(
            LineTrackChain localChain,
            int localStartAtomIndex,
            int localEndAtomIndexExclusive,
            LineTrackChain expressChain,
            int expressStartAtomIndex,
            int expressEndAtomIndexExclusive,
            List<Entity> localOrder,
            List<Entity> expressOrder)
        {
            localOrder.Clear();
            expressOrder.Clear();
            if (localChain == null || expressChain == null)
                return false;

            var localKeys = new HashSet<Entity>();
            var expressKeys = new HashSet<Entity>();
            CollectPhysicalLaneKeySet(localChain, localStartAtomIndex, localEndAtomIndexExclusive, localKeys);
            CollectPhysicalLaneKeySet(expressChain, expressStartAtomIndex, expressEndAtomIndexExclusive, expressKeys);
            localKeys.IntersectWith(expressKeys);
            if (localKeys.Count == 0)
                return false;

            CollectDistinctSharedPhysicalKeyOrder(localChain, localStartAtomIndex, localEndAtomIndexExclusive, localKeys, localOrder);
            CollectDistinctSharedPhysicalKeyOrder(expressChain, expressStartAtomIndex, expressEndAtomIndexExclusive, localKeys, expressOrder);
            return localOrder.Count > 0 && expressOrder.Count > 0;
        }

        private static int ComparePhysicalKeyOrderLexicographically(List<Entity> left, List<Entity> right)
        {
            int count = math.min(left?.Count ?? 0, right?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int leftIndex = left[i].Index;
                int rightIndex = right[i].Index;
                if (leftIndex != rightIndex)
                    return leftIndex.CompareTo(rightIndex);
            }

            return (left?.Count ?? 0).CompareTo(right?.Count ?? 0);
        }

        private static bool SequenceEquals(List<Entity> left, List<Entity> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static bool TryResolveCanonicalDirectionAlignment(
            List<Entity> localOrder,
            List<Entity> expressOrder,
            out bool localAlongCanonical,
            out bool expressAlongCanonical)
        {
            localAlongCanonical = false;
            expressAlongCanonical = false;
            if (localOrder == null || expressOrder == null || localOrder.Count == 0 || expressOrder.Count == 0)
                return false;

            var reversedLocalOrder = new List<Entity>(localOrder.Count);
            for (int i = localOrder.Count - 1; i >= 0; i--)
                reversedLocalOrder.Add(localOrder[i]);

            List<Entity> canonicalOrder = ComparePhysicalKeyOrderLexicographically(localOrder, reversedLocalOrder) <= 0
                ? localOrder
                : reversedLocalOrder;
            localAlongCanonical = SequenceEquals(localOrder, canonicalOrder);
            expressAlongCanonical = SequenceEquals(expressOrder, canonicalOrder);
            return true;
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
                            var localOrder = new List<Entity>();
                            var expressOrder = new List<Entity>();
                            bool localAlongCanonical = false;
                            bool expressAlongCanonical = false;
                            bool hasCanonicalDirection = TryCollectOrderedSharedPhysicalKeyOrders(
                                    localChain,
                                    pairLocalStartAtomIndex,
                                    pairLocalEndAtomIndexExclusive,
                                    expressChain,
                                    pairExpressStartAtomIndex,
                                    pairExpressEndAtomIndexExclusive,
                                    localOrder,
                                    expressOrder)
                                && TryResolveCanonicalDirectionAlignment(
                                    localOrder,
                                    expressOrder,
                                    out localAlongCanonical,
                                    out expressAlongCanonical);
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
                                traversalRelation,
                                hasCanonicalDirection,
                                hasCanonicalDirection && localAlongCanonical,
                                hasCanonicalDirection && expressAlongCanonical));
                        }
                    }
                }
            }
        }

        private bool TryResolveStaticTraversalPhaseWindow(
            LineTrackChain chain,
            int startAtomIndex,
            int endAtomIndexExclusive,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive)
        {
            traversalPhaseIndex = -1;
            traversalPhaseStartAtomIndex = -1;
            traversalPhaseEndAtomExclusive = -1;
            if (chain == null
                || chain.TrackAtoms.Count == 0
                || endAtomIndexExclusive <= startAtomIndex)
            {
                return false;
            }

            int startAtom = math.clamp(startAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int endAtom = math.clamp(endAtomIndexExclusive - 1, 0, chain.TrackAtoms.Count - 1);
            if (!TryResolveTraversalOrderingPhase(
                    chain,
                    startAtom,
                    out int startPhaseIndex,
                    out int startPhaseStartAtomIndex,
                    out int startPhaseEndAtomExclusive,
                    out _))
            {
                return false;
            }

            if (!TryResolveTraversalOrderingPhase(
                    chain,
                    endAtom,
                    out int endPhaseIndex,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            if (startPhaseIndex != endPhaseIndex)
                return false;

            traversalPhaseIndex = startPhaseIndex;
            traversalPhaseStartAtomIndex = startPhaseStartAtomIndex;
            traversalPhaseEndAtomExclusive = startPhaseEndAtomExclusive;
            return true;
        }

        private TrunkPhaseAlignment BuildTrunkPhaseAlignment(
            LineTrackChain localChain,
            DirectedSharedPairSegment pair,
            LineTrackChain expressChain)
        {
            bool localAvailable = TryResolveStaticTraversalPhaseWindow(
                localChain,
                pair.LocalStartAtomIndex,
                pair.LocalEndAtomIndexExclusive,
                out int localTraversalPhaseIndex,
                out int localPhaseStartAtomIndex,
                out int localPhaseEndAtomExclusive);
            bool expressAvailable = TryResolveStaticTraversalPhaseWindow(
                expressChain,
                pair.ExpressStartAtomIndex,
                pair.ExpressEndAtomIndexExclusive,
                out int expressTraversalPhaseIndex,
                out int expressPhaseStartAtomIndex,
                out int expressPhaseEndAtomExclusive);
            return new TrunkPhaseAlignment(
                localAvailable && expressAvailable,
                localAvailable ? localTraversalPhaseIndex : -1,
                localAvailable ? localPhaseStartAtomIndex : -1,
                localAvailable ? localPhaseEndAtomExclusive : -1,
                expressAvailable ? expressTraversalPhaseIndex : -1,
                expressAvailable ? expressPhaseStartAtomIndex : -1,
                expressAvailable ? expressPhaseEndAtomExclusive : -1);
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
                TrunkPhaseAlignment phaseAlignment = BuildTrunkPhaseAlignment(localChain, pair, expressChain);
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
                    pair.TraversalRelation,
                    pair.HasCanonicalDirection,
                    pair.LocalAlongCanonical,
                    pair.ExpressAlongCanonical,
                    phaseAlignment));
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
            int localTraversalPhaseIndex = TryResolveStaticTraversalPhaseWindow(
                localChain,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive,
                out int resolvedLocalTraversalPhaseIndex,
                out _,
                out _)
                ? resolvedLocalTraversalPhaseIndex
                : -1;
            return TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                localTraversalPhaseIndex,
                expressCurrentAtomIndex,
                -1,
                out _);
        }

        private bool TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            int localTraversalPhaseIndex,
            int expressCurrentAtomIndex,
            int expressTraversalPhaseIndex,
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
                if (candidate.TraversalRelation != SharedTraversalRelation.SameDirection)
                    continue;
                if (candidate.PhaseAlignment.Available)
                {
                    if (localTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.LocalTraversalPhaseIndex != localTraversalPhaseIndex)
                    {
                        continue;
                    }

                    if (expressTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.ExpressTraversalPhaseIndex != expressTraversalPhaseIndex)
                    {
                        continue;
                    }
                }

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
                if (candidate.PhaseAlignment.Available)
                {
                    if (localTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.LocalTraversalPhaseIndex != localTraversalPhaseIndex)
                    {
                        continue;
                    }

                    if (expressTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.ExpressTraversalPhaseIndex != expressTraversalPhaseIndex)
                    {
                        continue;
                    }
                }

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
            int localTraversalPhaseIndex = TryResolveStaticTraversalPhaseWindow(
                localChain,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive,
                out int resolvedLocalTraversalPhaseIndex,
                out _,
                out _)
                ? resolvedLocalTraversalPhaseIndex
                : -1;
            return TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                localChain,
                localProtectedInterval,
                currentBypassBuilding,
                expressChain,
                expressProtectedInterval,
                localTraversalPhaseIndex,
                expressPosition.CurrentAtomIndex,
                expressPosition.TraversalPhaseIndex,
                out segment);
        }

        private static TrunkSkeleton BuildTrunkSkeleton(GlobalSharedTrunkSegment segment)
        {
            return new TrunkSkeleton(
                segment.LocalCorridorStartAtomIndex,
                segment.LocalCorridorEndAtomIndexExclusive,
                segment.ExpressCorridorStartAtomIndex,
                segment.ExpressCorridorEndAtomIndexExclusive,
                segment.LocalAnchorStartAtomIndex,
                segment.LocalAnchorEndAtomIndexExclusive,
                segment.ExpressAnchorStartAtomIndex,
                segment.ExpressAnchorEndAtomIndexExclusive,
                segment.LocalSharedSliceCount,
                segment.ExpressSharedSliceCount,
                segment.LocalBridgedGapAtoms,
                segment.ExpressBridgedGapAtoms,
                segment.PhysicalOverlap,
                segment.OrderedRun,
                segment.TraversalRelation,
                segment.HasCanonicalDirection,
                segment.LocalAlongCanonical,
                segment.ExpressAlongCanonical);
        }

        private bool TryBuildSceneRelationSameDirectionTrunkCandidates(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain,
            BypassProtectedInterval expressProtectedInterval,
            bool hasRelevantSharedEntryAtomIndex,
            int relevantSharedEntryAtomIndex,
            out SceneRelationTrunkCandidateSet trunkCandidates,
            out GlobalSharedTrunkSegment segment)
        {
            trunkCandidates = null;
            segment = default;
            GlobalSharedTrunkSnapshot snapshot = GetGlobalSharedTrunkSnapshotCurrent(localChain, expressChain);
            if (snapshot == null || snapshot.Segments.Count == 0)
                return false;

            bool hasAnchor = TryGetForwardStationExitAtomIndex(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
            int localAnchorMaxStartAtomIndex = hasAnchor
                ? stationExitAtomIndex + MAX_CONFLICT_CORRIDOR_GAP_ATOMS
                : int.MaxValue;
            int targetExpressEntryAtomIndex = hasRelevantSharedEntryAtomIndex
                ? relevantSharedEntryAtomIndex
                : expressProtectedInterval.StartAtomIndex;
            trunkCandidates = new SceneRelationTrunkCandidateSet();

            int bestForwardSceneDistance = int.MaxValue;
            int bestAnchorDistance = int.MaxValue;
            int bestOrderedRun = int.MinValue;
            int bestPhysicalOverlap = int.MinValue;
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
                if (localOverlap <= 0 || candidateLocalEndExclusive <= candidateLocalStart || candidateLocalStart > localAnchorMaxStartAtomIndex)
                    continue;

                int candidateExpressStart = math.max(candidate.ExpressCorridorStartAtomIndex, expressProtectedInterval.StartAtomIndex);
                int candidateExpressEndExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, expressProtectedInterval.EndAtomIndexExclusive);
                if (candidateExpressEndExclusive <= candidateExpressStart)
                    continue;
                if (targetExpressEntryAtomIndex >= candidateExpressEndExclusive)
                    continue;

                trunkCandidates.Segments.Add(candidate);
                int expressApproachDistance = math.max(0, candidateExpressStart - targetExpressEntryAtomIndex);
                int anchorDistance = candidateLocalStart - localProtectedInterval.StartAtomIndex;
                bool better = !found;
                if (!better && expressApproachDistance != bestForwardSceneDistance)
                    better = expressApproachDistance < bestForwardSceneDistance;
                if (!better && anchorDistance != bestAnchorDistance)
                    better = anchorDistance < bestAnchorDistance;
                if (!better && candidate.OrderedRun != bestOrderedRun)
                    better = candidate.OrderedRun > bestOrderedRun;
                if (!better && candidate.PhysicalOverlap != bestPhysicalOverlap)
                    better = candidate.PhysicalOverlap > bestPhysicalOverlap;
                if (!better)
                    continue;

                bestForwardSceneDistance = expressApproachDistance;
                bestAnchorDistance = anchorDistance;
                bestOrderedRun = candidate.OrderedRun;
                bestPhysicalOverlap = candidate.PhysicalOverlap;
                segment = candidate;
                found = true;
            }

            if (trunkCandidates.Segments.Count == 0)
            {
                trunkCandidates = null;
                return false;
            }

            return found;
        }

        private bool TryFindBestCurrentSceneRelationTrunkSegment(
            SceneExpressRelation relation,
            BypassProtectedInterval localProtectedInterval,
            int localTraversalPhaseIndex,
            int expressCurrentAtomIndex,
            int expressTraversalPhaseIndex,
            int expressPhaseEndAtomExclusive,
            out GlobalSharedTrunkSegment segment)
        {
            segment = default;
            if (relation.TrunkCandidates == null || relation.TrunkCandidates.Segments.Count == 0)
                return false;

            int firstForwardSceneDistance = int.MaxValue;
            bool foundForwardSceneSegment = false;
            for (int i = 0; i < relation.TrunkCandidates.Segments.Count; i++)
            {
                GlobalSharedTrunkSegment candidate = relation.TrunkCandidates.Segments[i];
                if (candidate.PhaseAlignment.Available)
                {
                    if (localTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.LocalTraversalPhaseIndex != localTraversalPhaseIndex)
                    {
                        continue;
                    }

                    if (expressTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.ExpressTraversalPhaseIndex != expressTraversalPhaseIndex)
                    {
                        continue;
                    }
                }

                int candidateExpressStart = math.max(candidate.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
                int candidateExpressEndExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
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
            bool found = false;
            for (int i = 0; i < relation.TrunkCandidates.Segments.Count; i++)
            {
                GlobalSharedTrunkSegment candidate = relation.TrunkCandidates.Segments[i];
                if (candidate.PhaseAlignment.Available)
                {
                    if (localTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.LocalTraversalPhaseIndex != localTraversalPhaseIndex)
                    {
                        continue;
                    }

                    if (expressTraversalPhaseIndex >= 0
                        && candidate.PhaseAlignment.ExpressTraversalPhaseIndex != expressTraversalPhaseIndex)
                    {
                        continue;
                    }
                }

                int candidateExpressStart = math.max(candidate.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
                int candidateExpressEndExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
                candidateExpressEndExclusive = math.min(candidateExpressEndExclusive, expressPhaseEndAtomExclusive);
                if (candidateExpressEndExclusive <= candidateExpressStart)
                    continue;
                if (expressCurrentAtomIndex >= candidateExpressEndExclusive)
                    continue;

                int expressApproachDistance = math.max(0, candidateExpressStart - expressCurrentAtomIndex);
                if (expressApproachDistance != firstForwardSceneDistance)
                    continue;

                int candidateLocalStart = math.max(candidate.LocalCorridorStartAtomIndex, localProtectedInterval.StartAtomIndex);
                int candidateLocalEndExclusive = math.min(candidate.LocalCorridorEndAtomIndexExclusive, localProtectedInterval.EndAtomIndexExclusive);
                int localOverlap = CountAtomIntervalOverlap(
                    candidateLocalStart,
                    candidateLocalEndExclusive,
                    localProtectedInterval.StartAtomIndex,
                    localProtectedInterval.EndAtomIndexExclusive);
                int expressOverlap = CountAtomIntervalOverlap(
                    candidate.ExpressCorridorStartAtomIndex,
                    candidate.ExpressCorridorEndAtomIndexExclusive,
                    relation.ExpressProtectedInterval.StartAtomIndex,
                    relation.ExpressProtectedInterval.EndAtomIndexExclusive);
                int anchorDistance = candidateLocalStart - localProtectedInterval.StartAtomIndex;
                bool better = !found;
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

        private bool TryGetNextTurnbackBoundaryAtomIndex(
            LineTrackChain chain,
            int currentAtomIndex,
            out int turnbackBoundaryAtomIndex)
        {
            turnbackBoundaryAtomIndex = -1;
            if (chain == null
                || chain.TurnbackBoundaries == null
                || chain.TurnbackBoundaries.Count == 0)
            {
                return false;
            }

            int bestAtomIndex = int.MaxValue;
            bool found = false;
            for (int i = 0; i < chain.TurnbackBoundaries.Count; i++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[i];
                if (boundary.AtomIndex <= currentAtomIndex)
                    continue;
                if (boundary.AtomIndex >= bestAtomIndex)
                    continue;

                bestAtomIndex = boundary.AtomIndex;
                found = true;
            }

            if (!found)
                return false;

            turnbackBoundaryAtomIndex = bestAtomIndex;
            return true;
        }

        private RelativeToTrunkState ClassifyVehicleRelativeToTrunk(
            int currentAtomIndex,
            int nextTurnbackBoundaryAtomIndex,
            int corridorStartAtomIndex,
            int corridorEndAtomIndexExclusive,
            bool alongCanonical)
        {
            if (currentAtomIndex < 0 || corridorEndAtomIndexExclusive <= corridorStartAtomIndex)
                return RelativeToTrunkState.Unknown;

            if (currentAtomIndex >= corridorStartAtomIndex && currentAtomIndex < corridorEndAtomIndexExclusive)
            {
                return alongCanonical
                    ? RelativeToTrunkState.OnTrunkAlongCanonical
                    : RelativeToTrunkState.OnTrunkAgainstCanonical;
            }

            if (currentAtomIndex < corridorStartAtomIndex)
            {
                if (nextTurnbackBoundaryAtomIndex >= 0
                    && nextTurnbackBoundaryAtomIndex <= corridorStartAtomIndex)
                    return RelativeToTrunkState.FutureReturnOnly;

                return alongCanonical
                    ? RelativeToTrunkState.ApproachingTrunkAlongCanonical
                    : RelativeToTrunkState.ApproachingTrunkAgainstCanonical;
            }

            if (currentAtomIndex >= corridorEndAtomIndexExclusive)
                return RelativeToTrunkState.DepartingFromTrunk;

            return RelativeToTrunkState.OffTrunk;
        }

        private bool TryResolveVehicleTrunkTravelWindow(
            GlobalSharedTrunkSegment trunkSegment,
            bool useLocalSide,
            int traversalPhaseIndex,
            out int corridorStartAtomIndex,
            out int corridorEndAtomIndexExclusive,
            out bool alongCanonical)
        {
            corridorStartAtomIndex = useLocalSide
                ? trunkSegment.LocalCorridorStartAtomIndex
                : trunkSegment.ExpressCorridorStartAtomIndex;
            corridorEndAtomIndexExclusive = useLocalSide
                ? trunkSegment.LocalCorridorEndAtomIndexExclusive
                : trunkSegment.ExpressCorridorEndAtomIndexExclusive;
            alongCanonical = useLocalSide
                ? trunkSegment.LocalAlongCanonical
                : trunkSegment.ExpressAlongCanonical;
            if (!trunkSegment.HasCanonicalDirection)
                return false;

            if (trunkSegment.PhaseAlignment.Available && traversalPhaseIndex >= 0)
            {
                int phaseIndex = useLocalSide
                    ? trunkSegment.PhaseAlignment.LocalTraversalPhaseIndex
                    : trunkSegment.PhaseAlignment.ExpressTraversalPhaseIndex;
                int phaseStartAtomIndex = useLocalSide
                    ? trunkSegment.PhaseAlignment.LocalPhaseStartAtomIndex
                    : trunkSegment.PhaseAlignment.ExpressPhaseStartAtomIndex;
                int phaseEndAtomIndexExclusive = useLocalSide
                    ? trunkSegment.PhaseAlignment.LocalPhaseEndAtomExclusive
                    : trunkSegment.PhaseAlignment.ExpressPhaseEndAtomExclusive;
                if (phaseIndex != traversalPhaseIndex)
                    return false;

                corridorStartAtomIndex = math.max(corridorStartAtomIndex, phaseStartAtomIndex);
                corridorEndAtomIndexExclusive = math.min(corridorEndAtomIndexExclusive, phaseEndAtomIndexExclusive);
            }

            return corridorEndAtomIndexExclusive > corridorStartAtomIndex;
        }

        private RelativeToTrunkState ResolveVehicleTrunkTravelState(
            LineRunningVehicleSnapshot runningVehicle,
            GlobalSharedTrunkSegment trunkSegment,
            bool useLocalSide)
        {
            if (!runningVehicle.HasTrackCursor
                || !TryResolveVehicleTrunkTravelWindow(
                    trunkSegment,
                    useLocalSide,
                    runningVehicle.TraversalPhaseIndex,
                    out int corridorStartAtomIndex,
                    out int corridorEndAtomIndexExclusive,
                    out bool alongCanonical))
            {
                return RelativeToTrunkState.Unknown;
            }

            return ClassifyVehicleRelativeToTrunk(
                runningVehicle.TrackCursor.AtomCursorIndex,
                runningVehicle.NextTurnbackBoundaryAtomIndex,
                corridorStartAtomIndex,
                corridorEndAtomIndexExclusive,
                alongCanonical);
        }

        private RelativeToTrunkState ResolveVehicleTrunkTravelState(
            TrackModelRuntimePosition runtimePosition,
            GlobalSharedTrunkSegment trunkSegment,
            bool useLocalSide)
        {
            if (!TryResolveVehicleTrunkTravelWindow(
                    trunkSegment,
                    useLocalSide,
                    runtimePosition.TraversalPhaseIndex,
                    out int corridorStartAtomIndex,
                    out int corridorEndAtomIndexExclusive,
                    out bool alongCanonical))
            {
                return RelativeToTrunkState.Unknown;
            }

            return ClassifyVehicleRelativeToTrunk(
                runtimePosition.CurrentAtomIndex,
                runtimePosition.NextTurnbackBoundaryAtomIndex,
                corridorStartAtomIndex,
                corridorEndAtomIndexExclusive,
                alongCanonical);
        }

        private static string FormatRelativeToTrunkState(RelativeToTrunkState state)
        {
            switch (state)
            {
                case RelativeToTrunkState.OffTrunk:
                    return "off-trunk";
                case RelativeToTrunkState.OnTrunkAlongCanonical:
                    return "on-trunk-along";
                case RelativeToTrunkState.OnTrunkAgainstCanonical:
                    return "on-trunk-against";
                case RelativeToTrunkState.ApproachingTrunkAlongCanonical:
                    return "approaching-trunk-along";
                case RelativeToTrunkState.ApproachingTrunkAgainstCanonical:
                    return "approaching-trunk-against";
                case RelativeToTrunkState.DepartingFromTrunk:
                    return "departing-from-trunk";
                case RelativeToTrunkState.FutureReturnOnly:
                    return "future-return-only";
                default:
                    return "unknown";
            }
        }

        private static bool IsRelativeToTrunkStateAlongCanonical(RelativeToTrunkState state)
        {
            return state == RelativeToTrunkState.OnTrunkAlongCanonical
                || state == RelativeToTrunkState.ApproachingTrunkAlongCanonical;
        }

        private static bool IsRelativeToTrunkStateAgainstCanonical(RelativeToTrunkState state)
        {
            return state == RelativeToTrunkState.OnTrunkAgainstCanonical
                || state == RelativeToTrunkState.ApproachingTrunkAgainstCanonical;
        }

        private static bool IsRelativeToTrunkStateBlockerEligible(RelativeToTrunkState state)
        {
            return state == RelativeToTrunkState.OnTrunkAlongCanonical
                || state == RelativeToTrunkState.OnTrunkAgainstCanonical
                || state == RelativeToTrunkState.ApproachingTrunkAlongCanonical
                || state == RelativeToTrunkState.ApproachingTrunkAgainstCanonical;
        }

        private static bool IsRelativeToTrunkStateDirectionCompatibleWithCanonicalSide(
            RelativeToTrunkState state,
            bool alongCanonical)
        {
            return alongCanonical
                ? IsRelativeToTrunkStateAlongCanonical(state)
                : IsRelativeToTrunkStateAgainstCanonical(state);
        }

        private static bool IsRelativeToTrunkStateDirectionCompatibleWithLocal(
            RelativeToTrunkState expressState,
            GlobalSharedTrunkSegment trunkSegment)
        {
            if (!trunkSegment.HasCanonicalDirection)
                return false;

            return IsRelativeToTrunkStateDirectionCompatibleWithCanonicalSide(
                expressState,
                trunkSegment.LocalAlongCanonical);
        }

        private static string FormatCanonicalSide(bool alongCanonical)
        {
            return alongCanonical ? "along" : "against";
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
                hasPreselectedTrunkSegment,
                hasPreselectedTrunkSegment ? preselectedTrunkSegment : default,
                hasPreselectedTrunkSegment ? -1 : expressPosition.CurrentAtomIndex);

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

            if (!TryProjectConflictCorridorsFromTrunkSkeleton(
                    localChain,
                    localProtectedInterval,
                    currentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    BuildTrunkSkeleton(trunkSegment),
                    out localCorridor,
                    out expressCorridor))
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

        private bool TryEvaluateBypassTrackModelSummary(
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
            risk = ClassifyProtectedIntervalTrackModelRisk(intervalSummary);
            summary = FormatProtectedIntervalSummary(intervalSummary, protectedInterval);
            return true;
        }

        private bool TryEvaluateBypassTrackModelDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out BypassTrackModelDecision trackModelDecision)
        {
            m_BypassPerfProbeTrackDecisionCalls++;
            trackModelDecision = default;
            if (!TryGetLocalBypassSceneStaticSnapshot(
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out LineTrackChain localChain,
                    out LocalBypassSceneStaticSnapshot localScene))
            {
                trackModelDecision = new BypassTrackModelDecision(false, false, "local-chain-missing", -1, false, Entity.Null, false);
                return false;
            }

            int protectedIntervalIndex = localScene.ProtectedIntervalIndex;
            BypassProtectedInterval protectedInterval = localScene.ProtectedInterval;
            ProtectedIntervalSummary localSummary = localScene.Summary;
            TrackModelRuntimePosition localPosition = default;
            bool hasLocalPosition = TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out localPosition);
            Entity currentBypassBuilding = localScene.CurrentBypassBuilding;

            if (localSummary.SharedSegmentCount <= 0)
            {
                trackModelDecision = new BypassTrackModelDecision(true, false, "no-shared-protected-interval", protectedIntervalIndex, hasLocalPosition, Entity.Null, false);
                return true;
            }

            if (!hasLocalPosition)
            {
                trackModelDecision = new BypassTrackModelDecision(false, false, "local-runtime-position-unknown", protectedIntervalIndex, false, Entity.Null, false);
                return false;
            }
            if (localPosition.Confidence < 0.6f)
            {
                trackModelDecision = new BypassTrackModelDecision(false, false, "local-runtime-position-low-confidence", protectedIntervalIndex, false, Entity.Null, false);
                return false;
            }
            float departureReleaseCoordinate = localScene.DepartureReleaseCoordinate;
            float intervalDisplayLength = localScene.IntervalDisplayLength;
            float localCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(localPosition, protectedInterval, includeApproachers: true, out bool includeLocalCoordinate);
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
            BypassLatchedBlockerProjection bestLatchedBlockerProjection = default;
            bool localStillWithinCurrentBypassStation = IsRuntimePositionWithinBypassStationPhysicalContext(
                localChain,
                localPosition,
                currentBypassBuilding);

            if (!TryCollectSceneExpressFrontiers(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    localChain,
                    protectedIntervalIndex,
                    protectedInterval,
                    currentBypassBuilding,
                    localPosition,
                    localStillWithinCurrentBypassStation,
                    nowFrame,
                    out List<SceneExpressFrontier> frontiers,
                    out List<SceneExpressVehicleCandidate> sameStationCandidates,
                    out string candidateCollectionFatalReason))
            {
                trackModelDecision = new BypassTrackModelDecision(false, false, candidateCollectionFatalReason, protectedIntervalIndex, true, Entity.Null, false);
                return false;
            }

            if (frontiers.Count == 0)
            {
                trackModelDecision = new BypassTrackModelDecision(true, false, "no-express-in-shared-window", protectedIntervalIndex, true, Entity.Null, false);
                return true;
            }
            for (int frontierIndex = 0; frontierIndex < frontiers.Count; frontierIndex++)
            {
                SceneExpressFrontier frontier = frontiers[frontierIndex];
                for (int frontierCandidateIndex = 0; frontierCandidateIndex < 2; frontierCandidateIndex++)
                {
                    bool hasCandidate = frontierCandidateIndex == 0
                        ? frontier.HasPrimaryCandidate
                        : frontier.HasSecondaryCandidate;
                    if (!hasCandidate)
                        continue;

                    SceneExpressVehicleCandidate candidate = frontierCandidateIndex == 0
                        ? frontier.PrimaryCandidate
                        : frontier.SecondaryCandidate;
                    Entity expressVehicle = candidate.ExpressVehicle;
                    Entity expressLine = candidate.ExpressLine;
                    LineRunningVehicleSnapshot runningVehicle = candidate.RunningVehicle;
                    BypassProtectedInterval expressProtectedInterval = candidate.ExpressProtectedInterval;
                    int expressProtectedIntervalIndex = candidate.ExpressProtectedIntervalIndex;
                    int overlapCount = candidate.OverlapCount;
                    int orderedRun = candidate.OrderedRun;
                    string intervalResolutionSource = candidate.IntervalResolutionSource;
                    GlobalSharedTrunkSegment selectedTrunkSegment = candidate.SelectedTrunkSegment;
                    RelativeToTrunkState localTrunkState = candidate.LocalTrunkState;
                    RelativeToTrunkState expressTrunkState = candidate.ExpressTrunkState;

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
                            candidate.ExpressChain,
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
                        expressPositionText = "trunkState[local=" + FormatRelativeToTrunkState(localTrunkState)
                            + " express=" + FormatRelativeToTrunkState(expressTrunkState)
                            + " localCanon=" + FormatCanonicalSide(selectedTrunkSegment.LocalAlongCanonical)
                            + " expressCanon=" + FormatCanonicalSide(selectedTrunkSegment.ExpressAlongCanonical)
                            + "] " + expressPositionText;
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
                            bestExpressAtomCursorIndex = runningVehicle.TrackCursor.AtomCursorIndex;
                            bestExpressPhaseEndAtomExclusive = runningVehicle.PhaseEndAtomExclusive;
                            bestUsedFallbackResolution = intervalResolutionSource == "fallback";
                            bestLatchedBlockerProjection = BuildLatchedBlockerProjection(candidate);
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

                    if (foundBestBlocker
                        && bestBlockerEntryFrames <= 0f
                        && frontierCandidateIndex == 0
                        && !frontier.HasSecondaryCandidate)
                    {
                        break;
                    }
                }

                if (foundBestBlocker && bestBlockerEntryFrames <= 0f)
                    break;
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
                trackModelDecision = new BypassTrackModelDecision(
                    true,
                    true,
                    bestConflictReason,
                    protectedIntervalIndex,
                    true,
                    bestExpressVehicle,
                    bestUsedFallbackResolution,
                    bestLatchedBlockerProjection.Available,
                    bestLatchedBlockerProjection);
                return true;
            }

            if (localStillWithinCurrentBypassStation
                && TryFindSameStationSameDirectionDepartureBlocker(
                    localVehicle,
                    localLine,
                    localChain,
                    sameStationCandidates,
                    localPosition,
                    protectedIntervalIndex,
                    protectedInterval,
                    currentBypassBuilding,
                    intervalDisplayLength,
                    out SceneExpressVehicleCandidate sameStationCandidate,
                    out Entity sameStationBlocker))
            {
                m_SharedWindowAuditPairStateCache[new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, sameStationBlocker)] = "blocker|same-station";
                trackModelDecision = new BypassTrackModelDecision(
                    true,
                    true,
                    "same-station-same-direction-express-departing",
                    protectedIntervalIndex,
                    true,
                    sameStationBlocker,
                    false,
                    sameStationCandidate.ExpressVehicle != Entity.Null,
                    sameStationCandidate.ExpressVehicle != Entity.Null
                        ? BuildLatchedBlockerProjection(sameStationCandidate)
                        : default);
                return true;
            }

            if (sawReleaseClearedExpress)
            {
                trackModelDecision = new BypassTrackModelDecision(true, false, "express-cleared-bypass-release-window", protectedIntervalIndex, true, Entity.Null, releaseClearedUsedFallbackResolution);
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
            trackModelDecision = new BypassTrackModelDecision(true, false, "no-express-in-shared-window", protectedIntervalIndex, true, Entity.Null, false);
            return true;
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
            if (!TryGetLatestBypassTrackModelDecisionSnapshot(localVehicle, out BypassTrackModelDecisionSnapshot snapshot)
                || !snapshot.Decision.Available)
            {
                return false;
            }

            BypassTrackModelDecision decision = snapshot.Decision;
            shouldYield = decision.ShouldYield;
            reason = "track-model-" + decision.ReasonCode;
            blockerVehicle = decision.BlockerVehicle;
            return true;
        }

        private string GetTrackModelLiveDecisionLogSuffix(Entity localVehicle)
        {
            if (!TryGetLatestBypassTrackModelDecisionSnapshot(localVehicle, out BypassTrackModelDecisionSnapshot snapshot)
                || !snapshot.Decision.Available)
            {
                return string.Empty;
            }
            return string.Empty;
        }

        private bool ShouldTrackModelVetoLiveBypassYield(Entity localVehicle, out string trackModelReason)
        {
            trackModelReason = string.Empty;
            if (!TryGetLatestBypassTrackModelDecisionSnapshot(localVehicle, out BypassTrackModelDecisionSnapshot snapshot)
                || !snapshot.Decision.Available
                || snapshot.Decision.ShouldYield
                || !snapshot.Decision.HasReliableLocalPosition)
            {
                return false;
            }

            BypassTrackModelDecision decision = snapshot.Decision;
            switch (decision.ReasonCode)
            {
                case "no-shared-protected-interval":
                case "local-cleared-protected-interval":
                case "no-express-in-protected-interval":
                case "no-express-in-shared-window":
                    trackModelReason = decision.ReasonCode;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetLatestBypassTrackModelDecisionSnapshot(Entity localVehicle, out BypassTrackModelDecisionSnapshot snapshot)
        {
            snapshot = default;
            return localVehicle != Entity.Null
                && m_BypassTrackModelDecisionSnapshots.TryGetValue(localVehicle, out snapshot);
        }


    }
}
