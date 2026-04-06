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
        private readonly Dictionary<Entity, LineTrackChain> m_LineTrackChains = new Dictionary<Entity, LineTrackChain>();
        private readonly Dictionary<Entity, List<DevSightLaneOccurrence>> m_DevSightLaneIndex = new Dictionary<Entity, List<DevSightLaneOccurrence>>();
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
        private static bool IsTrackModelDiagnosticLoggingEnabled() => false;
        private readonly Dictionary<Entity, string> m_BypassSelectedBlockerDetailLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassSelectedBlockerDetailLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_TrainLaneSourceDiagnosticLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SameStationMissLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelCompareLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditSummaryLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_SharedWindowAuditLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<SharedWindowPairStateKey, string> m_SharedWindowAuditPairStateCache = new Dictionary<SharedWindowPairStateKey, string>();
        private readonly Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> m_SharedWindowMatchSnapshots = new Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot>();
        private readonly Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot> m_LocalBypassSceneStaticSnapshots = new Dictionary<LocalBypassSceneStaticKey, LocalBypassSceneStaticSnapshot>();
        private readonly Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> m_GlobalSharedTrunkSnapshots = new Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot>();
        private readonly Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> m_ProtectedIntervalPairMetricsSnapshots = new Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot>();
        private readonly Dictionary<LocalSceneExpressStaticMatchCacheKey, LocalSceneExpressStaticMatchSnapshot> m_LocalSceneExpressStaticMatchSnapshots = new Dictionary<LocalSceneExpressStaticMatchCacheKey, LocalSceneExpressStaticMatchSnapshot>();
        private readonly Dictionary<LocalSceneCandidateExpressLinesCacheKey, LocalSceneCandidateExpressLinesSnapshot> m_LocalSceneCandidateExpressLinesSnapshots = new Dictionary<LocalSceneCandidateExpressLinesCacheKey, LocalSceneCandidateExpressLinesSnapshot>();
        private readonly Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot> m_ActiveConflictCorridorSnapshots = new Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot>();
        private uint m_ActiveConflictCorridorSnapshotFrame;
        private readonly Dictionary<Entity, string> m_TrackModelSequenceLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, BypassTrackModelShadowSnapshot> m_BypassTrackModelShadowSnapshots = new Dictionary<Entity, BypassTrackModelShadowSnapshot>();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapSourceKeys = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProtectedIntervalOverlapMatchedKeys = new HashSet<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedSourceKeys = new List<Entity>();
        private readonly List<Entity> m_ProtectedIntervalOrderedCandidateKeys = new List<Entity>();
        private readonly List<int> m_ProtectedIntervalOrderedSourceAtomIndices = new List<int>();
        private readonly List<int> m_ProtectedIntervalOrderedCandidateAtomIndices = new List<int>();
        private uint m_SharedTrackIndexVersion = 1;
        private readonly HashSet<Entity> m_DirtyTrackLines = new HashSet<Entity>();
        private bool m_SharedTrackIndexDirty = true;

        public string BuildDevSightLaneTooltipSummary(Entity laneEntity)
        {
            if (laneEntity == Entity.Null)
                return "target  null";

            bool hasIndex = m_DevSightLaneIndex.TryGetValue(laneEntity, out List<DevSightLaneOccurrence> occurrences);

            if (!hasIndex)
            {
                foreach (var kvp in m_DevSightLaneIndex)
                {
                    if (kvp.Key.Index == laneEntity.Index)
                    {
                        occurrences = kvp.Value;
                        hasIndex = true;
                        break;
                    }
                }
            }

            if (!hasIndex || occurrences == null || occurrences.Count == 0)
            {
                return "target  " + FormatEntityRef(laneEntity) + "\ntrack model  no-chain-hit";
            }
            StringBuilder result = new StringBuilder(256);
            result.Append("target  ").Append(FormatEntityRef(laneEntity));

            for (int i = 0; i < occurrences.Count; i++)
            {
                result.Append('\n').Append(FormatDevSightOccurrence(occurrences[i]));
            }

            return result.ToString();
        }

        private string FormatDevSightOccurrence(DevSightLaneOccurrence occurrence)
        {
            string lineLabel = ResolveTrackModelLineLabel(occurrence.LineEntity, includeEntityFallback: false);
            return lineLabel + " atoms=" + FormatDevSightAtomIndices(occurrence.AtomIndices);
        }

        private string FormatDevSightAtomIndices(List<int> atomIndices)
        {
            if (atomIndices == null || atomIndices.Count == 0)
                return "[]";

            if (atomIndices.Count <= 3)
                return "[" + string.Join(",", atomIndices) + "]";

            return "[" + atomIndices[0] + "," + atomIndices[1] + ".." + atomIndices[atomIndices.Count - 1] + " x" + atomIndices.Count + "]";
        }

        private static string FormatEntityRef(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index + ":" + entity.Version;
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
        private bool TryResolveVehicleCurrentProtectedInterval(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null || waypoints.Length == 0)
                return false;

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
                return false;

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            return TryResolveProtectedIntervalByCursor(
                chain,
                currentControlEdgeIndex,
                cursor.AtomCursorIndex,
                out protectedIntervalIndex,
                out protectedInterval);
        }

        private static bool TryResolveProtectedIntervalByCursor(
            LineTrackChain chain,
            int currentControlEdgeIndex,
            int currentAtomIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null)
                return false;

            int bestRelativeScore = int.MinValue;
            int bestEntryDistanceAtoms = int.MaxValue;
            int bestIntervalLengthAtoms = int.MaxValue;
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, currentAtomIndex, candidate);
                if (relative == TrackModelRelativeToProtectedInterval.Unknown
                    || relative == TrackModelRelativeToProtectedInterval.After)
                {
                    continue;
                }

                int relativeScore = relative == TrackModelRelativeToProtectedInterval.Inside ? 2 : 1;
                int entryDistanceAtoms = math.max(0, candidate.StartAtomIndex - currentAtomIndex);
                int intervalLengthAtoms = math.max(1, candidate.EndAtomIndexExclusive - candidate.StartAtomIndex);
                bool better = protectedIntervalIndex < 0;
                if (!better && relativeScore != bestRelativeScore)
                    better = relativeScore > bestRelativeScore;
                if (!better && entryDistanceAtoms != bestEntryDistanceAtoms)
                    better = entryDistanceAtoms < bestEntryDistanceAtoms;
                if (!better && intervalLengthAtoms != bestIntervalLengthAtoms)
                    better = intervalLengthAtoms < bestIntervalLengthAtoms;
                if (!better)
                    continue;

                bestRelativeScore = relativeScore;
                bestEntryDistanceAtoms = entryDistanceAtoms;
                bestIntervalLengthAtoms = intervalLengthAtoms;
                protectedIntervalIndex = i;
                protectedInterval = candidate;
            }

            return protectedIntervalIndex >= 0;
        }

        private static int FindProtectedIntervalIndex(LineTrackChain chain, BypassProtectedInterval protectedInterval)
        {
            if (chain == null)
                return -1;

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                if (candidate.StartControlPointIndex == protectedInterval.StartControlPointIndex
                    && candidate.EndControlPointIndex == protectedInterval.EndControlPointIndex
                    && candidate.StartAtomIndex == protectedInterval.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == protectedInterval.EndAtomIndexExclusive)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out string resolutionSource)
        {
            m_BypassPerfProbeResolveCalls++;
            resolutionSource = "direct";
            if (TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out protectedIntervalIndex, out protectedInterval))
            {
                return true;
            }

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
            if (!TryResolveProtectedIntervalByCursor(
                    chain,
                    currentControlEdgeIndex,
                    cursor.AtomCursorIndex,
                    out protectedIntervalIndex,
                    out protectedInterval))
            {
                return false;
            }

            resolutionSource = "fallback";
            return true;
        }

        private bool TryResolveExpressConflictWindowForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            PhysicalSharedWindowMatch sharedWindowMatch,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out int overlapCount,
            out int orderedRun,
            out string resolutionSource)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            overlapCount = 0;
            orderedRun = 0;
            resolutionSource = "shared-window";

            // Consume the already-selected current shared-window first.
            // This keeps vehicle-level evaluation aligned with the current
            // local scene instead of collapsing back to a coarser
            // local-interval x express-interval matrix.
            if (sharedWindowMatch.Found && !sharedWindowMatch.Ambiguous)
            {
                protectedInterval = sharedWindowMatch.ExpressSharedWindow;
                overlapCount = sharedWindowMatch.OverlapCount;
                orderedRun = sharedWindowMatch.OrderedRun;
                return true;
            }

            if (chain == null
                || chain.BypassProtectedIntervals.Count > 0
                || !sharedWindowMatch.Found
                || sharedWindowMatch.Ambiguous)
            {
                return false;
            }

            protectedInterval = sharedWindowMatch.ExpressSharedWindow;
            overlapCount = sharedWindowMatch.OverlapCount;
            orderedRun = sharedWindowMatch.OrderedRun;
            resolutionSource = "shared-window";
            return true;
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

    }
}
