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
        private readonly Dictionary<Entity, string> m_BypassTrackModelDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelDecisionThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelDecisionLastLogFrame = new Dictionary<Entity, uint>();
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
        private readonly Dictionary<Entity, BypassTrackModelDecisionSnapshot> m_BypassTrackModelDecisionSnapshots = new Dictionary<Entity, BypassTrackModelDecisionSnapshot>();
        private readonly Dictionary<Entity, string> m_LineBypassExecutionModeLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineOrderedRuntimeLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineOrderedFallbackCaseLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_LineOrderedFallbackCaseLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_TrackModelTurnbackBuildLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_TrackModelTurnbackSignalLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_TrackModelTurnbackSignalLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_TrackModelTurnbackLearnLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_TrackModelTurnbackLearnLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, List<LearnedTurnbackBoundaryCluster>> m_LearnedTurnbackBoundaryClustersByLine = new Dictionary<Entity, List<LearnedTurnbackBoundaryCluster>>();
        private readonly Dictionary<Entity, TurnbackLearnVehicleSampleState> m_TurnbackLearnVehicleStates = new Dictionary<Entity, TurnbackLearnVehicleSampleState>();
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
