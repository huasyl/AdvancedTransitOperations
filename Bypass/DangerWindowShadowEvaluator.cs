using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Bypass
{
    // FROZEN 2026-06-11: danger-window shadow compare is retained for work-card context only.
    // Runtime compare and selection-panel hooks are intentionally disconnected because live logs
    // showed direction and blocker choice misalignment.
    internal sealed class DangerWindowShadowEvaluator
    {
        private const uint LOG_INTERVAL_FRAMES = 3600;
        private const float TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES = 1f;
        private const int MAX_CONFLICT_CORRIDOR_GAP_ATOMS = 6;
        private const int MAX_MISMATCH_SAMPLES = 5;

        private readonly IBypassAdmissionRuntimeContext m_Runtime;
        private readonly AdmissionService m_Admission;
        private readonly SceneStaticIndex m_SceneIndex;
        private readonly Dictionary<SceneStaticIndexKey, SceneState> m_SceneStates = new Dictionary<SceneStaticIndexKey, SceneState>();
        private readonly Dictionary<Entity, DangerWindowShadowStatus> m_LastStatusByVehicle = new Dictionary<Entity, DangerWindowShadowStatus>();
        private readonly Dictionary<string, ulong> m_UnavailableByReason = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, ulong> m_MissByReason = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly List<string> m_MismatchSamples = new List<string>(MAX_MISMATCH_SAMPLES);
        private readonly List<RelationPhaseScope> m_ScratchPhaseScopes = new List<RelationPhaseScope>(8);
        private readonly List<RelationPhaseWindow> m_ScratchWindows = new List<RelationPhaseWindow>(16);
        private readonly List<PhaseWindowBucket> m_ScratchPhaseBuckets = new List<PhaseWindowBucket>(8);

        private uint m_LastFlushFrame;
        private ulong m_Calls;
        private ulong m_ComparableCalls;
        private ulong m_AvailableCalls;
        private ulong m_UnavailableCalls;
        private ulong m_OldHoldInScope;
        private ulong m_ShadowHit;
        private ulong m_ShadowNoHit;
        private ulong m_HitMatch;
        private ulong m_HitMismatch;
        private ulong m_BlockerMatch;
        private ulong m_BlockerMismatch;
        private ulong m_WindowsBuilt;
        private ulong m_WindowsWithHits;
        private ulong m_CursorEntriesScanned;
        private ulong m_EtaIndexBuilds;
        private ulong m_EtaIndexHits;

        private sealed class SceneState
        {
            public uint SharedTrackVersion;
            public ulong LocalChainSignature;
            public uint LocalSceneVersion;
            public readonly Dictionary<Entity, LineTraversalEtaIndex> EtaIndicesByLine = new Dictionary<Entity, LineTraversalEtaIndex>();
            public readonly Dictionary<Entity, ShadowLineCursorFrame> CursorFramesByLine = new Dictionary<Entity, ShadowLineCursorFrame>();
        }

        private readonly struct CompareContext
        {
            public readonly BypassControlScope Scope;
            public readonly DynamicBuffer<RouteWaypoint> LocalWaypoints;
            public readonly LineTrackChain LocalChain;
            public readonly LocalBypassSceneStaticSnapshot LocalScene;
            public readonly TrackModelRuntimePosition LocalPosition;
            public readonly LineTraversalEtaIndex LocalEtaIndex;
            public readonly SceneStaticIndexEntry StaticEntry;
            public readonly SceneState SceneState;
            public readonly Entity OldBlocker;
            public readonly Entity OldExpressLine;
            public readonly int OldRelationIndex;
            public readonly uint NowFrame;
            public readonly float CurrentLocalAtomCoordinate;
            public readonly float RemainingBoardingFrames;

            public CompareContext(
                BypassControlScope scope,
                DynamicBuffer<RouteWaypoint> localWaypoints,
                LineTrackChain localChain,
                LocalBypassSceneStaticSnapshot localScene,
                TrackModelRuntimePosition localPosition,
                LineTraversalEtaIndex localEtaIndex,
                SceneStaticIndexEntry staticEntry,
                SceneState sceneState,
                Entity oldBlocker,
                Entity oldExpressLine,
                int oldRelationIndex,
                uint nowFrame,
                float currentLocalAtomCoordinate,
                float remainingBoardingFrames)
            {
                Scope = scope;
                LocalWaypoints = localWaypoints;
                LocalChain = localChain;
                LocalScene = localScene;
                LocalPosition = localPosition;
                LocalEtaIndex = localEtaIndex;
                StaticEntry = staticEntry;
                SceneState = sceneState;
                OldBlocker = oldBlocker;
                OldExpressLine = oldExpressLine;
                OldRelationIndex = oldRelationIndex;
                NowFrame = nowFrame;
                CurrentLocalAtomCoordinate = currentLocalAtomCoordinate;
                RemainingBoardingFrames = remainingBoardingFrames;
            }
        }

        private readonly struct RelationEvaluation
        {
            public readonly bool Available;
            public readonly bool HasBlocker;
            public readonly Entity BlockerVehicle;
            public readonly Entity ExpressLine;
            public readonly string UnavailableReason;
            public readonly string MissReason;
            public readonly int RelationIndex;
            public readonly int TrunkIndex;
            public readonly int DangerStartAtomIndex;
            public readonly int DangerEndAtomIndexExclusive;
            public readonly float LocalClearFrames;
            public readonly float SafeEntryDeadlineFrames;
            public readonly bool HitOnTrunk;
            public readonly float EntryEtaFrames;
            public readonly int EntryDistanceAtoms;
            public readonly int RelevantSharedEntryAtomIndex;
            public readonly int CursorAtomIndex;

            public RelationEvaluation(
                bool available,
                bool hasBlocker,
                Entity blockerVehicle,
                Entity expressLine,
                string unavailableReason,
                string missReason,
                int relationIndex,
                int trunkIndex,
                int dangerStartAtomIndex,
                int dangerEndAtomIndexExclusive,
                float localClearFrames,
                float safeEntryDeadlineFrames,
                bool hitOnTrunk = false,
                float entryEtaFrames = float.MaxValue,
                int entryDistanceAtoms = int.MaxValue,
                int relevantSharedEntryAtomIndex = int.MaxValue,
                int cursorAtomIndex = int.MaxValue)
            {
                Available = available;
                HasBlocker = hasBlocker;
                BlockerVehicle = blockerVehicle;
                ExpressLine = expressLine;
                UnavailableReason = unavailableReason ?? string.Empty;
                MissReason = missReason ?? string.Empty;
                RelationIndex = relationIndex;
                TrunkIndex = trunkIndex;
                DangerStartAtomIndex = dangerStartAtomIndex;
                DangerEndAtomIndexExclusive = dangerEndAtomIndexExclusive;
                LocalClearFrames = localClearFrames;
                SafeEntryDeadlineFrames = safeEntryDeadlineFrames;
                HitOnTrunk = hitOnTrunk;
                EntryEtaFrames = entryEtaFrames;
                EntryDistanceAtoms = entryDistanceAtoms;
                RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
                CursorAtomIndex = cursorAtomIndex;
            }
        }

        private readonly struct HitCandidate
        {
            public readonly bool Available;
            public readonly float EntryEtaFrames;
            public readonly bool OnTrunk;
            public readonly int EntryDistanceAtoms;
            public readonly int RelevantSharedEntryAtomIndex;
            public readonly int CursorAtomIndex;
            public readonly Entity Vehicle;
            public readonly Entity ExpressLine;
            public readonly int RelationIndex;
            public readonly int TrunkIndex;
            public readonly int DangerStartAtomIndex;
            public readonly int DangerEndAtomIndexExclusive;
            public readonly float LocalClearFrames;
            public readonly float SafeEntryDeadlineFrames;

            public HitCandidate(
                bool available,
                float entryEtaFrames,
                bool onTrunk,
                int entryDistanceAtoms,
                int relevantSharedEntryAtomIndex,
                int cursorAtomIndex,
                Entity vehicle,
                Entity expressLine,
                int relationIndex,
                int trunkIndex,
                int dangerStartAtomIndex,
                int dangerEndAtomIndexExclusive,
                float localClearFrames,
                float safeEntryDeadlineFrames)
            {
                Available = available;
                EntryEtaFrames = entryEtaFrames;
                OnTrunk = onTrunk;
                EntryDistanceAtoms = entryDistanceAtoms;
                RelevantSharedEntryAtomIndex = relevantSharedEntryAtomIndex;
                CursorAtomIndex = cursorAtomIndex;
                Vehicle = vehicle;
                ExpressLine = expressLine;
                RelationIndex = relationIndex;
                TrunkIndex = trunkIndex;
                DangerStartAtomIndex = dangerStartAtomIndex;
                DangerEndAtomIndexExclusive = dangerEndAtomIndexExclusive;
                LocalClearFrames = localClearFrames;
                SafeEntryDeadlineFrames = safeEntryDeadlineFrames;
            }
        }

        private readonly struct RelationPhaseScope
        {
            public readonly int TraversalPhaseIndex;
            public readonly int PhaseStartAtomIndex;
            public readonly int PhaseEndAtomIndexExclusive;

            public RelationPhaseScope(int traversalPhaseIndex, int phaseStartAtomIndex, int phaseEndAtomIndexExclusive)
            {
                TraversalPhaseIndex = traversalPhaseIndex;
                PhaseStartAtomIndex = phaseStartAtomIndex;
                PhaseEndAtomIndexExclusive = phaseEndAtomIndexExclusive;
            }
        }

        private readonly struct RelationPhaseWindow
        {
            public readonly int RelationIndex;
            public readonly int TrunkIndex;
            public readonly int TraversalPhaseIndex;
            public readonly int PhaseStartAtomIndex;
            public readonly int PhaseEndAtomIndexExclusive;
            public readonly GlobalSharedTrunkSegment Segment;
            public readonly ConflictCorridor LocalCorridor;
            public readonly ConflictCorridor ExpressCorridor;
            public readonly int CandidateExpressStartAtomIndex;
            public readonly int CandidateExpressEndAtomIndexExclusive;
            public readonly int LocalOverlap;
            public readonly int ExpressOverlap;
            public readonly float LocalClearFrames;
            public readonly float SafeEntryDeadlineFrames;
            public readonly int DangerStartAtomIndex;
            public readonly int DangerEndAtomIndexExclusive;
            public readonly bool DeadlineNonpositive;
            public readonly bool WindowEmpty;
            public readonly bool HasDangerWindow;

            public RelationPhaseWindow(
                int relationIndex,
                int trunkIndex,
                int traversalPhaseIndex,
                int phaseStartAtomIndex,
                int phaseEndAtomIndexExclusive,
                GlobalSharedTrunkSegment segment,
                ConflictCorridor localCorridor,
                ConflictCorridor expressCorridor,
                int candidateExpressStartAtomIndex,
                int candidateExpressEndAtomIndexExclusive,
                int localOverlap,
                int expressOverlap,
                float localClearFrames,
                float safeEntryDeadlineFrames,
                int dangerStartAtomIndex,
                int dangerEndAtomIndexExclusive,
                bool deadlineNonpositive,
                bool windowEmpty,
                bool hasDangerWindow)
            {
                RelationIndex = relationIndex;
                TrunkIndex = trunkIndex;
                TraversalPhaseIndex = traversalPhaseIndex;
                PhaseStartAtomIndex = phaseStartAtomIndex;
                PhaseEndAtomIndexExclusive = phaseEndAtomIndexExclusive;
                Segment = segment;
                LocalCorridor = localCorridor;
                ExpressCorridor = expressCorridor;
                CandidateExpressStartAtomIndex = candidateExpressStartAtomIndex;
                CandidateExpressEndAtomIndexExclusive = candidateExpressEndAtomIndexExclusive;
                LocalOverlap = localOverlap;
                ExpressOverlap = expressOverlap;
                LocalClearFrames = localClearFrames;
                SafeEntryDeadlineFrames = safeEntryDeadlineFrames;
                DangerStartAtomIndex = dangerStartAtomIndex;
                DangerEndAtomIndexExclusive = dangerEndAtomIndexExclusive;
                DeadlineNonpositive = deadlineNonpositive;
                WindowEmpty = windowEmpty;
                HasDangerWindow = hasDangerWindow;
            }
        }

        private readonly struct PhaseWindowBucket
        {
            public readonly int TraversalPhaseIndex;
            public readonly int PhaseStartAtomIndex;
            public readonly int PhaseEndAtomIndexExclusive;
            public readonly int StartWindowIndex;
            public readonly int EndWindowIndexExclusive;

            public PhaseWindowBucket(
                int traversalPhaseIndex,
                int phaseStartAtomIndex,
                int phaseEndAtomIndexExclusive,
                int startWindowIndex,
                int endWindowIndexExclusive)
            {
                TraversalPhaseIndex = traversalPhaseIndex;
                PhaseStartAtomIndex = phaseStartAtomIndex;
                PhaseEndAtomIndexExclusive = phaseEndAtomIndexExclusive;
                StartWindowIndex = startWindowIndex;
                EndWindowIndexExclusive = endWindowIndexExclusive;
            }
        }

        internal DangerWindowShadowEvaluator(
            IBypassAdmissionRuntimeContext runtime,
            AdmissionService admission,
            SceneStaticIndex sceneIndex)
        {
            m_Runtime = runtime;
            m_Admission = admission;
            m_SceneIndex = sceneIndex;
        }

        private static bool IsDangerWindowShadowLoggingEnabled() => true;

        internal void Clear()
        {
            m_SceneStates.Clear();
            m_LastStatusByVehicle.Clear();
            m_UnavailableByReason.Clear();
            m_MissByReason.Clear();
            m_MismatchSamples.Clear();
            m_LastFlushFrame = 0;
            m_Calls = 0;
            m_ComparableCalls = 0;
            m_AvailableCalls = 0;
            m_UnavailableCalls = 0;
            m_OldHoldInScope = 0;
            m_ShadowHit = 0;
            m_ShadowNoHit = 0;
            m_HitMatch = 0;
            m_HitMismatch = 0;
            m_BlockerMatch = 0;
            m_BlockerMismatch = 0;
            m_WindowsBuilt = 0;
            m_WindowsWithHits = 0;
            m_CursorEntriesScanned = 0;
            m_EtaIndexBuilds = 0;
            m_EtaIndexHits = 0;
        }

        internal void Invalidate()
        {
            m_SceneStates.Clear();
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_LastStatusByVehicle.Remove(vehicle);
        }

        internal bool TryGetStatus(Entity vehicle, out DangerWindowShadowStatus status)
        {
            return m_LastStatusByVehicle.TryGetValue(vehicle, out status);
        }

        internal void Compare(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            uint nowFrame,
            BypassTrackModelDecision oldDecision)
        {
            m_Calls++;

            if (oldDecision.ReasonCode == "same-station-same-direction-express-departing")
            {
                SetStatus(scope.Vehicle, new DangerWindowShadowStatus(
                    false,
                    false,
                    false,
                    Entity.Null,
                    "old-reason-out-of-scope:same-station"));
                RecordUnavailable("unavailable:old-reason-out-of-scope:same-station");
                return;
            }

            if (!string.Equals(oldDecision.ReasonCode, "same-direction-shared-express-approaching", StringComparison.Ordinal))
            {
                SetStatus(scope.Vehicle, new DangerWindowShadowStatus(
                    false,
                    false,
                    false,
                    Entity.Null,
                    "old-reason-out-of-scope:other"));
                RecordUnavailable("unavailable:old-reason-out-of-scope:other");
                return;
            }

            m_OldHoldInScope++;

            if (!TryBuildCompareContext(scope, localWaypoints, nowFrame, oldDecision, out CompareContext context, out string unavailableReason))
            {
                SetStatus(scope.Vehicle, new DangerWindowShadowStatus(
                    true,
                    false,
                    false,
                    Entity.Null,
                    TrimUnavailablePrefix(unavailableReason)));
                RecordUnavailable(unavailableReason);
                return;
            }

            m_ComparableCalls++;

            bool anyAvailable = false;
            bool anyHit = false;
            RelationEvaluation oldRelationEvaluation = default;
            HitCandidate bestHit = default;

            for (int relationIndex = 0; relationIndex < context.StaticEntry.ExpressRelations.Count; relationIndex++)
            {
                RelationEvaluation evaluation = EvaluateRelation(context, relationIndex);
                if (relationIndex == context.OldRelationIndex)
                    oldRelationEvaluation = evaluation;

                if (!evaluation.Available)
                    continue;

                anyAvailable = true;
                if (!evaluation.HasBlocker)
                    continue;

                anyHit = true;
                var candidate = new HitCandidate(
                    true,
                    evaluation.EntryEtaFrames,
                    evaluation.HitOnTrunk,
                    evaluation.EntryDistanceAtoms,
                    evaluation.RelevantSharedEntryAtomIndex,
                    evaluation.CursorAtomIndex,
                    evaluation.BlockerVehicle,
                    evaluation.ExpressLine,
                    evaluation.RelationIndex,
                    evaluation.TrunkIndex,
                    evaluation.DangerStartAtomIndex,
                    evaluation.DangerEndAtomIndexExclusive,
                    evaluation.LocalClearFrames,
                    evaluation.SafeEntryDeadlineFrames);
                if (!bestHit.Available || CompareHitCandidate(candidate, bestHit) < 0)
                    bestHit = candidate;
            }

            if (!anyAvailable)
            {
                string miss = string.IsNullOrWhiteSpace(oldRelationEvaluation.UnavailableReason)
                    ? "unavailable:internal-error"
                    : oldRelationEvaluation.UnavailableReason;
                SetStatus(context.Scope.Vehicle, new DangerWindowShadowStatus(
                    true,
                    false,
                    false,
                    Entity.Null,
                    TrimUnavailablePrefix(miss)));
                RecordUnavailable(miss);
                return;
            }

            m_AvailableCalls++;
            if (anyHit)
            {
                SetStatus(context.Scope.Vehicle, new DangerWindowShadowStatus(
                    true,
                    true,
                    true,
                    bestHit.Vehicle,
                    string.Empty));
                m_ShadowHit++;
                m_HitMatch++;
                if (bestHit.Vehicle == context.OldBlocker)
                {
                    m_BlockerMatch++;
                }
                else
                {
                    m_BlockerMismatch++;
                    RecordMismatch(context, oldDecision, new DangerWindowShadowDecision(
                        true,
                        true,
                        bestHit.Vehicle,
                        bestHit.ExpressLine,
                        string.Empty,
                        context.LocalScene.ProtectedIntervalIndex,
                        bestHit.RelationIndex,
                        bestHit.TrunkIndex,
                        bestHit.DangerStartAtomIndex,
                        bestHit.DangerEndAtomIndexExclusive,
                        bestHit.LocalClearFrames,
                        bestHit.SafeEntryDeadlineFrames));
                }

                return;
            }

            m_ShadowNoHit++;
            m_HitMismatch++;
            string missReason = string.IsNullOrWhiteSpace(oldRelationEvaluation.MissReason)
                ? "window-no-hit"
                : oldRelationEvaluation.MissReason;
            SetStatus(context.Scope.Vehicle, new DangerWindowShadowStatus(
                true,
                true,
                false,
                Entity.Null,
                missReason));
            RecordMiss(missReason);
            RecordMismatch(context, oldDecision, new DangerWindowShadowDecision(
                true,
                false,
                Entity.Null,
                context.OldExpressLine,
                missReason,
                context.LocalScene.ProtectedIntervalIndex,
                oldRelationEvaluation.RelationIndex,
                oldRelationEvaluation.TrunkIndex,
                oldRelationEvaluation.DangerStartAtomIndex,
                oldRelationEvaluation.DangerEndAtomIndexExclusive,
                oldRelationEvaluation.LocalClearFrames,
                oldRelationEvaluation.SafeEntryDeadlineFrames));
        }

        internal void FlushIfDue(uint nowFrame)
        {
            if (!IsDangerWindowShadowLoggingEnabled())
                return;

            if (m_LastFlushFrame == 0)
            {
                m_LastFlushFrame = nowFrame;
                return;
            }

            uint elapsed = nowFrame - m_LastFlushFrame;
            if (elapsed < LOG_INTERVAL_FRAMES)
                return;

            m_LastFlushFrame = nowFrame;
            if (m_Calls > 0
                || m_AvailableCalls > 0
                || m_UnavailableCalls > 0
                || m_MismatchSamples.Count > 0)
            {
                m_Runtime.Log.Info(
                    "[BypassDangerWindowShadow] frames=" + elapsed
                    + " calls=" + m_Calls
                    + " comparable=" + m_ComparableCalls
                    + " available=" + m_AvailableCalls
                    + " unavailable=" + m_UnavailableCalls
                    + " oldHoldInScope=" + m_OldHoldInScope
                    + " shadowHit=" + m_ShadowHit
                    + " shadowNoHit=" + m_ShadowNoHit
                    + " hitMatch=" + m_HitMatch
                    + " hitMismatch=" + m_HitMismatch
                    + " blockerMatch=" + m_BlockerMatch
                    + " blockerMismatch=" + m_BlockerMismatch
                    + " windows=" + m_WindowsBuilt
                    + " hitWindows=" + m_WindowsWithHits
                    + " scanned=" + m_CursorEntriesScanned
                    + " etaBuild=" + m_EtaIndexBuilds
                    + " etaHit=" + m_EtaIndexHits
                    + " unavailableBuckets=" + FormatBuckets(m_UnavailableByReason)
                    + " miss=" + FormatBuckets(m_MissByReason));
            }

            for (int i = 0; i < m_MismatchSamples.Count; i++)
                m_Runtime.Log.Info(m_MismatchSamples[i]);

            m_Calls = 0;
            m_ComparableCalls = 0;
            m_AvailableCalls = 0;
            m_UnavailableCalls = 0;
            m_OldHoldInScope = 0;
            m_ShadowHit = 0;
            m_ShadowNoHit = 0;
            m_HitMatch = 0;
            m_HitMismatch = 0;
            m_BlockerMatch = 0;
            m_BlockerMismatch = 0;
            m_WindowsBuilt = 0;
            m_WindowsWithHits = 0;
            m_CursorEntriesScanned = 0;
            m_EtaIndexBuilds = 0;
            m_EtaIndexHits = 0;
            m_UnavailableByReason.Clear();
            m_MissByReason.Clear();
            m_MismatchSamples.Clear();
        }

        private bool TryBuildCompareContext(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            uint nowFrame,
            BypassTrackModelDecision oldDecision,
            out CompareContext context,
            out string unavailableReason)
        {
            context = default;
            unavailableReason = "unavailable:internal-error";

            if (!m_Runtime.TrackModel.TryGetLocalSceneSnapshot(
                    scope.Line,
                    localWaypoints,
                    scope.WaypointIndex,
                    out LineTrackChain localChain,
                    out LocalBypassSceneStaticSnapshot localScene))
            {
                unavailableReason = "unavailable:local-scene-miss";
                return false;
            }

            if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(
                    scope.Vehicle,
                    scope.Line,
                    localWaypoints,
                    localScene.ProtectedInterval,
                    out TrackModelRuntimePosition localPosition))
            {
                unavailableReason = "unavailable:local-projection-miss";
                return false;
            }

            if (localPosition.Confidence < 0.6f)
            {
                unavailableReason = "unavailable:local-projection-low-confidence";
                return false;
            }

            if (!m_SceneIndex.TryGetEntry(localChain, localScene.CurrentBypassBuilding, localScene.ProtectedIntervalIndex, out SceneStaticIndexEntry staticEntry)
                || staticEntry == null)
            {
                unavailableReason = "unavailable:local-scene-miss";
                return false;
            }

            if (oldDecision.BlockerVehicle == Entity.Null)
            {
                unavailableReason = "unavailable:internal-error";
                return false;
            }

            Entity oldExpressLine = m_Runtime.ResolveLine(oldDecision.BlockerVehicle);
            if (oldExpressLine == Entity.Null)
            {
                unavailableReason = "unavailable:express-cursor-miss";
                return false;
            }

            int oldRelationIndex = -1;
            for (int i = 0; i < staticEntry.ExpressRelations.Count; i++)
            {
                if (staticEntry.ExpressRelations[i].ExpressLine == oldExpressLine)
                {
                    oldRelationIndex = i;
                    break;
                }
            }

            if (oldRelationIndex < 0)
            {
                unavailableReason = "unavailable:internal-error";
                return false;
            }

            SceneStaticIndexKey sceneKey = new SceneStaticIndexKey(scope.Line, localScene.CurrentBypassBuilding, localScene.ProtectedIntervalIndex);
            SceneState sceneState = GetSceneState(sceneKey, localChain);
            if (!TryGetEtaIndex(sceneState, localChain, out LineTraversalEtaIndex localEtaIndex))
            {
                unavailableReason = "unavailable:eta-index-miss";
                return false;
            }

            float currentLocalAtomCoordinate = localPosition.CurrentAtomIndex + math.saturate(localPosition.AtomPosition01);
            float remainingBoardingFrames = 0f;
            if (m_Runtime.TryEstimateRemainingBoardingTime(
                    scope.Vehicle,
                    scope.Line,
                    scope.WaypointIndex,
                    nowFrame,
                    out float estimatedBoardingFrames))
            {
                remainingBoardingFrames = estimatedBoardingFrames;
            }

            context = new CompareContext(
                scope,
                localWaypoints,
                localChain,
                localScene,
                localPosition,
                localEtaIndex,
                staticEntry,
                sceneState,
                oldDecision.BlockerVehicle,
                oldExpressLine,
                oldRelationIndex,
                nowFrame,
                currentLocalAtomCoordinate,
                remainingBoardingFrames);
            return true;
        }

        private RelationEvaluation EvaluateRelation(CompareContext context, int relationIndex)
        {
            SceneExpressRelation relation = context.StaticEntry.ExpressRelations[relationIndex];
            if (relation.Ambiguous)
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:ambiguous-relation", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (!string.Equals(relation.ResolutionSource, "shared-window", StringComparison.Ordinal))
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:old-reason-out-of-scope:other", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (relation.TrunkCandidates == null || relation.TrunkCandidates.Segments.Count == 0)
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:trunk-direction-mismatch", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            BufferLookup<RouteWaypoint> routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(relation.ExpressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:express-cursor-miss", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (!TryGetEtaIndex(context.SceneState, relation.ExpressChain, out LineTraversalEtaIndex expressEtaIndex))
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:eta-index-miss", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (!TryGetCursorFrame(context.SceneState, relation.ExpressLine, expressWaypoints, relation.ExpressChain, context.NowFrame, out ShadowLineCursorFrame cursorFrame))
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:express-cursor-miss", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (!TryBuildRelationWindows(
                    context,
                    relation,
                    relationIndex,
                    cursorFrame,
                    expressEtaIndex,
                    out string buildUnavailableReason,
                    out bool buildSawDeadlineNonpositive,
                    out bool buildSawWindowEmpty,
                    out bool buildSawPhaseMismatch,
                    out bool buildSawDirectionMismatch))
            {
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, buildUnavailableReason, string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);
            }

            bool sawLowConfidence = false;
            bool sawCursor = false;
            bool sawCursorOutsideWindow = false;
            bool sawDeadlineNonpositive = buildSawDeadlineNonpositive;
            bool sawWindowEmpty = buildSawWindowEmpty;
            RelationEvaluation bestHit = default;
            HitCandidate bestHitCandidate = default;
            string fallbackUnavailableReason = buildSawDirectionMismatch
                ? "unavailable:trunk-direction-mismatch"
                : "unavailable:phase-mismatch";
            int currentPhase = int.MinValue;
            int currentPhaseStart = int.MinValue;
            int currentPhaseEnd = int.MinValue;
            bool hasActiveBucket = false;
            PhaseWindowBucket activeBucket = default;

            for (int entryIndex = 0; entryIndex < cursorFrame.Entries.Count; entryIndex++)
            {
                ShadowLineCursorEntry entry = cursorFrame.Entries[entryIndex];
                m_CursorEntriesScanned++;
                if (!entry.VehicleTrackCursor.Available)
                    continue;

                sawCursor = true;
                if (entry.VehicleTrackCursor.Confidence < 0.6f)
                {
                    sawLowConfidence = true;
                    continue;
                }

                if (entry.TraversalPhaseIndex != currentPhase
                    || entry.TraversalPhaseStartAtomIndex != currentPhaseStart
                    || entry.TraversalPhaseEndAtomExclusive != currentPhaseEnd)
                {
                    currentPhase = entry.TraversalPhaseIndex;
                    currentPhaseStart = entry.TraversalPhaseStartAtomIndex;
                    currentPhaseEnd = entry.TraversalPhaseEndAtomExclusive;
                    hasActiveBucket = TryGetPhaseBucket(
                        currentPhase,
                        currentPhaseStart,
                        currentPhaseEnd,
                        out activeBucket);
                }

                if (!hasActiveBucket)
                {
                    fallbackUnavailableReason = "unavailable:phase-mismatch";
                    continue;
                }

                if (!TrySelectBestWindowForCursor(
                        entry,
                        activeBucket,
                        out RelationPhaseWindow selectedWindow,
                        out bool phaseWindowExists))
                {
                    if (!phaseWindowExists)
                        fallbackUnavailableReason = "unavailable:phase-mismatch";
                    else
                        sawCursorOutsideWindow = true;
                    continue;
                }

                RelativeToTrunkState expressTrunkState = ResolveShadowEntryTrunkTravelState(entry, selectedWindow);
                if (!AdmissionService.IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                    || !AdmissionService.IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, selectedWindow.Segment))
                {
                    sawCursorOutsideWindow = true;
                    continue;
                }

                if (selectedWindow.DeadlineNonpositive)
                {
                    sawDeadlineNonpositive = true;
                    continue;
                }

                if (selectedWindow.WindowEmpty || !selectedWindow.HasDangerWindow)
                {
                    sawWindowEmpty = true;
                    continue;
                }

                bool onTrunk = expressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                    || expressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical;
                bool inDangerWindow = onTrunk
                    ? entry.OwnLineAtomCoordinate >= selectedWindow.ExpressCorridor.StartAtomIndex
                        && entry.OwnLineAtomCoordinate < selectedWindow.DangerEndAtomIndexExclusive
                    : entry.OwnLineAtomCoordinate >= selectedWindow.DangerStartAtomIndex
                        && entry.OwnLineAtomCoordinate < selectedWindow.ExpressCorridor.StartAtomIndex;
                if (!inDangerWindow)
                {
                    sawCursorOutsideWindow = true;
                    continue;
                }

                m_WindowsWithHits++;
                int relevantSharedEntryAtomIndex = ResolveRelevantSharedEntryAtomIndex(relation, selectedWindow.Segment);
                int entryDistanceAtoms = ComputeEntryDistanceAtoms(entry, selectedWindow.Segment, expressTrunkState, relation);
                float entryEtaFrames = ComputeEntryEtaFrames(expressEtaIndex, entry, selectedWindow);
                RelationEvaluation hit = new RelationEvaluation(
                    true,
                    true,
                    entry.Vehicle,
                    relation.ExpressLine,
                    string.Empty,
                    string.Empty,
                    relationIndex,
                    selectedWindow.TrunkIndex,
                    selectedWindow.DangerStartAtomIndex,
                    selectedWindow.DangerEndAtomIndexExclusive,
                    selectedWindow.LocalClearFrames,
                    selectedWindow.SafeEntryDeadlineFrames,
                    onTrunk,
                    entryEtaFrames,
                    entryDistanceAtoms,
                    relevantSharedEntryAtomIndex,
                    entry.VehicleTrackCursor.AtomCursorIndex);
                HitCandidate hitCandidate = new HitCandidate(
                    true,
                    entryEtaFrames,
                    onTrunk,
                    entryDistanceAtoms,
                    relevantSharedEntryAtomIndex,
                    entry.VehicleTrackCursor.AtomCursorIndex,
                    entry.Vehicle,
                    relation.ExpressLine,
                    relationIndex,
                    selectedWindow.TrunkIndex,
                    selectedWindow.DangerStartAtomIndex,
                    selectedWindow.DangerEndAtomIndexExclusive,
                    selectedWindow.LocalClearFrames,
                    selectedWindow.SafeEntryDeadlineFrames);
                if (!bestHit.HasBlocker
                    || CompareHitCandidate(hitCandidate, bestHitCandidate) < 0)
                {
                    bestHit = hit;
                    bestHitCandidate = hitCandidate;
                }
            }

            if (bestHit.HasBlocker)
                return bestHit;

            if (!sawCursor)
                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:express-cursor-miss", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            if (m_ScratchWindows.Count == 0)
            {
                if (sawLowConfidence)
                    return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, "unavailable:express-cursor-low-confidence", string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

                return new RelationEvaluation(false, false, Entity.Null, relation.ExpressLine, fallbackUnavailableReason, string.Empty, relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);
            }

            if (sawDeadlineNonpositive)
                return new RelationEvaluation(true, false, Entity.Null, relation.ExpressLine, string.Empty, "safe-entry-deadline-nonpositive", relationIndex, -1, -1, -1, float.MaxValue, 0f);
            if (sawWindowEmpty)
                return new RelationEvaluation(true, false, Entity.Null, relation.ExpressLine, string.Empty, "relation-window-built-but-empty", relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);
            if (sawCursorOutsideWindow)
                return new RelationEvaluation(true, false, Entity.Null, relation.ExpressLine, string.Empty, "cursor-outside-window", relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);

            return new RelationEvaluation(true, false, Entity.Null, relation.ExpressLine, string.Empty, "window-no-hit", relationIndex, -1, -1, -1, float.MaxValue, float.MaxValue);
        }

        private bool TryBuildRelationWindows(
            CompareContext context,
            SceneExpressRelation relation,
            int relationIndex,
            ShadowLineCursorFrame cursorFrame,
            LineTraversalEtaIndex expressEtaIndex,
            out string unavailableReason,
            out bool sawDeadlineNonpositive,
            out bool sawWindowEmpty,
            out bool sawPhaseMismatch,
            out bool sawDirectionMismatch)
        {
            m_ScratchPhaseScopes.Clear();
            m_ScratchWindows.Clear();
            m_ScratchPhaseBuckets.Clear();
            unavailableReason = string.Empty;
            sawDeadlineNonpositive = false;
            sawWindowEmpty = false;
            sawPhaseMismatch = false;
            sawDirectionMismatch = false;

            CollectPhaseScopes(relation, cursorFrame, m_ScratchPhaseScopes);
            if (m_ScratchPhaseScopes.Count == 0)
                return true;

            for (int phaseScopeIndex = 0; phaseScopeIndex < m_ScratchPhaseScopes.Count; phaseScopeIndex++)
            {
                RelationPhaseScope phaseScope = m_ScratchPhaseScopes[phaseScopeIndex];
                int startWindowIndex = m_ScratchWindows.Count;
                for (int trunkIndex = 0; trunkIndex < relation.TrunkCandidates.Segments.Count; trunkIndex++)
                {
                    GlobalSharedTrunkSegment candidate = relation.TrunkCandidates.Segments[trunkIndex];
                    if (!TryBuildRelationWindow(
                            context,
                            relation,
                            relationIndex,
                            expressEtaIndex,
                            phaseScope,
                            candidate,
                            trunkIndex,
                            out RelationPhaseWindow window,
                            out string buildResult))
                    {
                        if (buildResult == "unavailable:eta-index-miss")
                        {
                            unavailableReason = buildResult;
                            return false;
                        }

                        if (buildResult == "unavailable:phase-mismatch")
                            sawPhaseMismatch = true;
                        else if (buildResult == "unavailable:trunk-direction-mismatch")
                            sawDirectionMismatch = true;
                        else if (buildResult == "safe-entry-deadline-nonpositive")
                            sawDeadlineNonpositive = true;
                        else if (buildResult == "relation-window-built-but-empty")
                            sawWindowEmpty = true;

                        continue;
                    }

                    m_ScratchWindows.Add(window);
                    m_WindowsBuilt++;
                    sawDeadlineNonpositive |= window.DeadlineNonpositive;
                    sawWindowEmpty |= window.WindowEmpty;
                }

                if (m_ScratchWindows.Count > startWindowIndex)
                {
                    m_ScratchPhaseBuckets.Add(new PhaseWindowBucket(
                        phaseScope.TraversalPhaseIndex,
                        phaseScope.PhaseStartAtomIndex,
                        phaseScope.PhaseEndAtomIndexExclusive,
                        startWindowIndex,
                        m_ScratchWindows.Count));
                }
            }

            return true;
        }

        private bool TryBuildRelationWindow(
            CompareContext context,
            SceneExpressRelation relation,
            int relationIndex,
            LineTraversalEtaIndex expressEtaIndex,
            RelationPhaseScope phaseScope,
            GlobalSharedTrunkSegment candidate,
            int trunkIndex,
            out RelationPhaseWindow window,
            out string buildResult)
        {
            window = default;
            buildResult = string.Empty;

            if (candidate.PhaseAlignment.Available)
            {
                if (context.LocalPosition.TraversalPhaseIndex >= 0
                    && candidate.PhaseAlignment.LocalTraversalPhaseIndex != context.LocalPosition.TraversalPhaseIndex)
                {
                    buildResult = "unavailable:phase-mismatch";
                    return false;
                }

                if (candidate.PhaseAlignment.ExpressTraversalPhaseIndex != phaseScope.TraversalPhaseIndex)
                {
                    buildResult = "unavailable:phase-mismatch";
                    return false;
                }
            }

            if (candidate.TraversalRelation != SharedTraversalRelation.SameDirection
                || !candidate.HasCanonicalDirection)
            {
                buildResult = "unavailable:trunk-direction-mismatch";
                return false;
            }

            RelativeToTrunkState localTrunkState = m_Admission.ResolveVehicleTrunkTravelState(context.LocalPosition, candidate, useLocalSide: true);
            if (!IsRelativeToTrunkStateDirectionCompatibleWithCanonicalSide(localTrunkState, candidate.LocalAlongCanonical))
            {
                buildResult = "unavailable:trunk-direction-mismatch";
                return false;
            }

            if (!TryProjectConflictCorridorsFromTrunkSkeleton(
                    context.LocalChain,
                    context.LocalScene.ProtectedInterval,
                    context.LocalScene.CurrentBypassBuilding,
                    relation.ExpressChain,
                    relation.ExpressProtectedInterval,
                    BuildTrunkSkeleton(candidate),
                    out ConflictCorridor localCorridor,
                    out ConflictCorridor expressCorridor))
            {
                buildResult = "relation-window-built-but-empty";
                return false;
            }

            float localClearFrames = ComputeLocalClearFrames(context, localCorridor);
            if (localClearFrames == float.MaxValue)
            {
                buildResult = "unavailable:eta-index-miss";
                return false;
            }

            int candidateExpressStartAtomIndex = math.max(candidate.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
            int candidateExpressEndAtomIndexExclusive = math.min(candidate.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
            candidateExpressEndAtomIndexExclusive = math.min(candidateExpressEndAtomIndexExclusive, phaseScope.PhaseEndAtomIndexExclusive);
            if (candidateExpressEndAtomIndexExclusive <= candidateExpressStartAtomIndex)
            {
                buildResult = "relation-window-built-but-empty";
                return false;
            }

            int candidateLocalStart = math.max(candidate.LocalCorridorStartAtomIndex, context.LocalScene.ProtectedInterval.StartAtomIndex);
            int candidateLocalEndAtomIndexExclusive = math.min(candidate.LocalCorridorEndAtomIndexExclusive, context.LocalScene.ProtectedInterval.EndAtomIndexExclusive);
            int localOverlap = CountAtomIntervalOverlap(
                candidateLocalStart,
                candidateLocalEndAtomIndexExclusive,
                context.LocalScene.ProtectedInterval.StartAtomIndex,
                context.LocalScene.ProtectedInterval.EndAtomIndexExclusive);
            int expressOverlap = CountAtomIntervalOverlap(
                candidate.ExpressCorridorStartAtomIndex,
                candidate.ExpressCorridorEndAtomIndexExclusive,
                relation.ExpressProtectedInterval.StartAtomIndex,
                relation.ExpressProtectedInterval.EndAtomIndexExclusive);

            float safeEntryDeadlineFrames = localClearFrames
                - m_Runtime.ClockSnapshot.ToFramesCeil(TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES);
            int dangerEndAtomIndexExclusive = math.min(expressCorridor.EndAtomIndexExclusive, phaseScope.PhaseEndAtomIndexExclusive);
            if (dangerEndAtomIndexExclusive <= expressCorridor.StartAtomIndex)
            {
                window = new RelationPhaseWindow(
                    relationIndex,
                    trunkIndex,
                    phaseScope.TraversalPhaseIndex,
                    phaseScope.PhaseStartAtomIndex,
                    phaseScope.PhaseEndAtomIndexExclusive,
                    candidate,
                    localCorridor,
                    expressCorridor,
                    candidateExpressStartAtomIndex,
                    candidateExpressEndAtomIndexExclusive,
                    localOverlap,
                    expressOverlap,
                    localClearFrames,
                    safeEntryDeadlineFrames,
                    -1,
                    dangerEndAtomIndexExclusive,
                    false,
                    true,
                    false);
                return true;
            }

            if (!(safeEntryDeadlineFrames > 0f))
            {
                window = new RelationPhaseWindow(
                    relationIndex,
                    trunkIndex,
                    phaseScope.TraversalPhaseIndex,
                    phaseScope.PhaseStartAtomIndex,
                    phaseScope.PhaseEndAtomIndexExclusive,
                    candidate,
                    localCorridor,
                    expressCorridor,
                    candidateExpressStartAtomIndex,
                    candidateExpressEndAtomIndexExclusive,
                    localOverlap,
                    expressOverlap,
                    localClearFrames,
                    safeEntryDeadlineFrames,
                    -1,
                    dangerEndAtomIndexExclusive,
                    true,
                    false,
                    false);
                return true;
            }

            if (!expressEtaIndex.TryFindEarliestAtomReachingTargetWithinFrames(
                    phaseScope.PhaseStartAtomIndex,
                    expressCorridor.StartAtomIndex,
                    safeEntryDeadlineFrames,
                    out int dangerStartAtomIndex))
            {
                buildResult = "unavailable:eta-index-miss";
                return false;
            }

            bool hasDangerWindow = dangerEndAtomIndexExclusive > dangerStartAtomIndex;
            window = new RelationPhaseWindow(
                relationIndex,
                trunkIndex,
                phaseScope.TraversalPhaseIndex,
                phaseScope.PhaseStartAtomIndex,
                phaseScope.PhaseEndAtomIndexExclusive,
                candidate,
                localCorridor,
                expressCorridor,
                candidateExpressStartAtomIndex,
                candidateExpressEndAtomIndexExclusive,
                localOverlap,
                expressOverlap,
                localClearFrames,
                safeEntryDeadlineFrames,
                dangerStartAtomIndex,
                dangerEndAtomIndexExclusive,
                false,
                !hasDangerWindow,
                hasDangerWindow);
            return true;
        }

        private static void CollectPhaseScopes(
            SceneExpressRelation relation,
            ShadowLineCursorFrame cursorFrame,
            List<RelationPhaseScope> scopes)
        {
            if (cursorFrame == null)
                return;

            for (int i = 0; i < cursorFrame.Entries.Count; i++)
            {
                ShadowLineCursorEntry entry = cursorFrame.Entries[i];
                if (!ShouldCollectPhaseScopeForEntry(relation, entry))
                    continue;

                if (ContainsPhaseScope(scopes, entry.TraversalPhaseIndex, entry.TraversalPhaseStartAtomIndex, entry.TraversalPhaseEndAtomExclusive))
                    continue;

                scopes.Add(new RelationPhaseScope(
                    entry.TraversalPhaseIndex,
                    entry.TraversalPhaseStartAtomIndex,
                    entry.TraversalPhaseEndAtomExclusive));
            }
        }

        private static bool ShouldCollectPhaseScopeForEntry(SceneExpressRelation relation, ShadowLineCursorEntry entry)
        {
            if (!entry.VehicleTrackCursor.Available
                || entry.VehicleTrackCursor.Confidence < 0.6f)
            {
                return false;
            }

            if (entry.VehicleTrackCursor.AtomCursorIndex >= relation.ExpressProtectedInterval.EndAtomIndexExclusive)
                return false;

            if (entry.TraversalPhaseEndAtomExclusive <= relation.ExpressProtectedInterval.StartAtomIndex)
                return false;

            return true;
        }

        private static bool ContainsPhaseScope(
            List<RelationPhaseScope> scopes,
            int traversalPhaseIndex,
            int phaseStartAtomIndex,
            int phaseEndAtomIndexExclusive)
        {
            for (int i = 0; i < scopes.Count; i++)
            {
                RelationPhaseScope scope = scopes[i];
                if (scope.TraversalPhaseIndex == traversalPhaseIndex
                    && scope.PhaseStartAtomIndex == phaseStartAtomIndex
                    && scope.PhaseEndAtomIndexExclusive == phaseEndAtomIndexExclusive)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetPhaseBucket(
            int traversalPhaseIndex,
            int phaseStartAtomIndex,
            int phaseEndAtomIndexExclusive,
            out PhaseWindowBucket bucket)
        {
            bucket = default;
            for (int bucketIndex = 0; bucketIndex < m_ScratchPhaseBuckets.Count; bucketIndex++)
            {
                PhaseWindowBucket candidate = m_ScratchPhaseBuckets[bucketIndex];
                if (candidate.TraversalPhaseIndex != traversalPhaseIndex
                    || candidate.PhaseStartAtomIndex != phaseStartAtomIndex
                    || candidate.PhaseEndAtomIndexExclusive != phaseEndAtomIndexExclusive)
                {
                    continue;
                }

                bucket = candidate;
                return true;
            }

            return false;
        }

        private bool TrySelectBestWindowForCursor(
            ShadowLineCursorEntry entry,
            PhaseWindowBucket bucket,
            out RelationPhaseWindow selectedWindow,
            out bool phaseWindowExists)
        {
            selectedWindow = default;
            phaseWindowExists = false;
            if (bucket.EndWindowIndexExclusive <= bucket.StartWindowIndex)
                return false;

            int bestAnchorDistance = int.MaxValue;
            int bestOrderedRun = int.MinValue;
            int bestPhysicalOverlap = int.MinValue;
            int bestLocalOverlap = int.MinValue;
            int bestExpressOverlap = int.MinValue;
            int bestExpressApproachDistance = int.MaxValue;
            bool found = false;
            for (int i = bucket.StartWindowIndex; i < bucket.EndWindowIndexExclusive; i++)
            {
                RelationPhaseWindow candidate = m_ScratchWindows[i];
                if (candidate.PhaseEndAtomIndexExclusive != entry.TraversalPhaseEndAtomExclusive)
                {
                    continue;
                }

                phaseWindowExists = true;
                if (entry.VehicleTrackCursor.AtomCursorIndex >= candidate.CandidateExpressEndAtomIndexExclusive)
                    continue;

                int expressApproachDistance = math.max(0, candidate.CandidateExpressStartAtomIndex - entry.VehicleTrackCursor.AtomCursorIndex);
                int anchorDistance = math.max(0, candidate.Segment.ExpressAnchorStartAtomIndex - entry.VehicleTrackCursor.AtomCursorIndex);
                bool better = !found;
                if (!better && expressApproachDistance != bestExpressApproachDistance)
                    better = expressApproachDistance < bestExpressApproachDistance;
                if (!better && anchorDistance != bestAnchorDistance)
                    better = anchorDistance < bestAnchorDistance;
                if (!better && candidate.Segment.OrderedRun != bestOrderedRun)
                    better = candidate.Segment.OrderedRun > bestOrderedRun;
                if (!better && candidate.Segment.PhysicalOverlap != bestPhysicalOverlap)
                    better = candidate.Segment.PhysicalOverlap > bestPhysicalOverlap;
                if (!better && candidate.LocalOverlap != bestLocalOverlap)
                    better = candidate.LocalOverlap > bestLocalOverlap;
                if (!better && candidate.ExpressOverlap != bestExpressOverlap)
                    better = candidate.ExpressOverlap > bestExpressOverlap;
                if (!better)
                    continue;

                bestExpressApproachDistance = expressApproachDistance;
                bestAnchorDistance = anchorDistance;
                bestOrderedRun = candidate.Segment.OrderedRun;
                bestPhysicalOverlap = candidate.Segment.PhysicalOverlap;
                bestLocalOverlap = candidate.LocalOverlap;
                bestExpressOverlap = candidate.ExpressOverlap;
                selectedWindow = candidate;
                found = true;
            }

            return found;
        }

        private SceneState GetSceneState(SceneStaticIndexKey sceneKey, LineTrackChain localChain)
        {
            if (!m_SceneStates.TryGetValue(sceneKey, out SceneState state))
            {
                state = new SceneState();
                m_SceneStates[sceneKey] = state;
            }

            if (state.SharedTrackVersion != m_Runtime.TrackModel.SharedIndexVersion
                || state.LocalChainSignature != localChain.Signature
                || state.LocalSceneVersion != localChain.LocalBypassWaypointScenesVersion)
            {
                state.SharedTrackVersion = m_Runtime.TrackModel.SharedIndexVersion;
                state.LocalChainSignature = localChain.Signature;
                state.LocalSceneVersion = localChain.LocalBypassWaypointScenesVersion;
                state.EtaIndicesByLine.Clear();
                state.CursorFramesByLine.Clear();
            }

            return state;
        }

        private bool TryGetEtaIndex(SceneState sceneState, LineTrackChain chain, out LineTraversalEtaIndex etaIndex)
        {
            etaIndex = null;
            if (sceneState == null || chain == null)
                return false;

            if (sceneState.EtaIndicesByLine.TryGetValue(chain.LineEntity, out etaIndex)
                && etaIndex != null
                && etaIndex.Matches(chain))
            {
                m_EtaIndexHits++;
                return true;
            }

            if (!LineTraversalEtaIndex.TryBuild(m_Runtime, chain, out etaIndex))
                return false;

            m_EtaIndexBuilds++;
            sceneState.EtaIndicesByLine[chain.LineEntity] = etaIndex;
            return true;
        }

        private bool TryGetCursorFrame(
            SceneState sceneState,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            uint nowFrame,
            out ShadowLineCursorFrame cursorFrame)
        {
            cursorFrame = null;
            if (sceneState == null
                || line == Entity.Null
                || chain == null
                || waypoints.Length == 0)
            {
                return false;
            }

            if (!sceneState.CursorFramesByLine.TryGetValue(line, out cursorFrame) || cursorFrame == null)
            {
                cursorFrame = new ShadowLineCursorFrame();
                sceneState.CursorFramesByLine[line] = cursorFrame;
            }

            if (cursorFrame.Frame == nowFrame
                && cursorFrame.Line == line
                && cursorFrame.ChainSignature == chain.Signature)
            {
                return true;
            }

            BufferLookup<RouteVehicle> routeVehicleBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            cursorFrame.Frame = nowFrame;
            cursorFrame.Line = line;
            cursorFrame.ChainSignature = chain.Signature;
            cursorFrame.Entries.Clear();

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = routeVehicles[i].m_Vehicle;
                if (vehicle == Entity.Null
                    || !m_Runtime.EntityManager.Exists(vehicle)
                    || !m_Runtime.TryGetVehicleRuntimeState(vehicle, out VehicleState vehicleState)
                    || vehicleState != VehicleState.Running)
                {
                    continue;
                }

                if (!m_Runtime.TrackProjection.TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                        vehicle,
                        line,
                        waypoints,
                        chain,
                        out VehicleTrackCursor cursor,
                        out _,
                        out float ownLineAtomCoordinate,
                        out _,
                        out int traversalPhaseIndex,
                        out int traversalPhaseStartAtomIndex,
                        out int traversalPhaseEndAtomExclusive,
                        out int nextTurnbackBoundaryAtomIndex))
                {
                    continue;
                }

                cursorFrame.Entries.Add(new ShadowLineCursorEntry(
                    vehicle,
                    cursor,
                    ownLineAtomCoordinate,
                    traversalPhaseIndex,
                    traversalPhaseStartAtomIndex,
                    traversalPhaseEndAtomExclusive,
                    nextTurnbackBoundaryAtomIndex,
                    false));
            }

            cursorFrame.Entries.Sort(CompareShadowCursorEntry);
            return true;
        }

        private float ComputeLocalClearFrames(CompareContext context, ConflictCorridor localCorridor)
        {
            float fromCoordinate = context.CurrentLocalAtomCoordinate;
            if (context.LocalPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Before
                || fromCoordinate < localCorridor.StartAtomIndex)
            {
                fromCoordinate = localCorridor.StartAtomIndex;
            }

            if (context.LocalPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.After
                || fromCoordinate >= localCorridor.EndAtomIndexExclusive)
            {
                return 0f;
            }

            float frames = context.LocalEtaIndex.FramesBetween(fromCoordinate, localCorridor.EndAtomIndexExclusive);
            if (frames == float.MaxValue)
                return float.MaxValue;

            return frames + context.RemainingBoardingFrames;
        }

        private static RelativeToTrunkState ResolveShadowEntryTrunkTravelState(
            ShadowLineCursorEntry entry,
            RelationPhaseWindow window)
        {
            if (!window.Segment.HasCanonicalDirection)
                return RelativeToTrunkState.Unknown;

            if (entry.TraversalPhaseIndex != window.TraversalPhaseIndex
                || window.ExpressCorridor.EndAtomIndexExclusive <= window.ExpressCorridor.StartAtomIndex)
            {
                return RelativeToTrunkState.Unknown;
            }

            return ClassifyVehicleRelativeToTrunk(
                entry.VehicleTrackCursor.AtomCursorIndex,
                entry.NextTurnbackBoundaryAtomIndex,
                window.ExpressCorridor.StartAtomIndex,
                window.ExpressCorridor.EndAtomIndexExclusive,
                window.Segment.ExpressAlongCanonical);
        }

        private static RelativeToTrunkState ClassifyVehicleRelativeToTrunk(
            int currentAtomIndex,
            int nextTurnbackBoundaryAtomIndex,
            int corridorStartAtomIndex,
            int corridorEndAtomIndexExclusive,
            bool alongCanonical)
        {
            if (currentAtomIndex >= corridorStartAtomIndex && currentAtomIndex < corridorEndAtomIndexExclusive)
                return alongCanonical ? RelativeToTrunkState.OnTrunkAlongCanonical : RelativeToTrunkState.OnTrunkAgainstCanonical;

            if (currentAtomIndex < corridorStartAtomIndex)
            {
                bool returnsBeforeTurnback = nextTurnbackBoundaryAtomIndex < 0 || corridorStartAtomIndex < nextTurnbackBoundaryAtomIndex;
                if (!returnsBeforeTurnback)
                    return RelativeToTrunkState.FutureReturnOnly;

                return alongCanonical ? RelativeToTrunkState.ApproachingTrunkAlongCanonical : RelativeToTrunkState.ApproachingTrunkAgainstCanonical;
            }

            return RelativeToTrunkState.DepartingFromTrunk;
        }

        private static bool IsRelativeToTrunkStateDirectionCompatibleWithCanonicalSide(
            RelativeToTrunkState state,
            bool alongCanonical)
        {
            return alongCanonical
                ? state == RelativeToTrunkState.OnTrunkAlongCanonical || state == RelativeToTrunkState.ApproachingTrunkAlongCanonical
                : state == RelativeToTrunkState.OnTrunkAgainstCanonical || state == RelativeToTrunkState.ApproachingTrunkAgainstCanonical;
        }

        private static int ComputeEntryDistanceAtoms(
            ShadowLineCursorEntry entry,
            GlobalSharedTrunkSegment selectedTrunkSegment,
            RelativeToTrunkState expressTrunkState,
            SceneExpressRelation relation)
        {
            if (expressTrunkState == RelativeToTrunkState.OnTrunkAlongCanonical
                || expressTrunkState == RelativeToTrunkState.OnTrunkAgainstCanonical)
            {
                return 0;
            }

            int cursorAtomIndex = entry.VehicleTrackCursor.AtomCursorIndex;
            if (cursorAtomIndex >= selectedTrunkSegment.ExpressCorridorStartAtomIndex
                && cursorAtomIndex < selectedTrunkSegment.ExpressCorridorEndAtomIndexExclusive)
            {
                return 0;
            }

            int targetAtomIndex = relation.HasRelevantSharedEntryAtomIndex
                ? relation.RelevantSharedEntryAtomIndex
                : selectedTrunkSegment.ExpressCorridorStartAtomIndex;
            return math.max(0, targetAtomIndex - cursorAtomIndex);
        }

        private static int ResolveRelevantSharedEntryAtomIndex(
            SceneExpressRelation relation,
            GlobalSharedTrunkSegment selectedTrunkSegment)
        {
            return relation.HasRelevantSharedEntryAtomIndex
                ? relation.RelevantSharedEntryAtomIndex
                : selectedTrunkSegment.ExpressCorridorStartAtomIndex;
        }

        private static float ComputeEntryEtaFrames(
            LineTraversalEtaIndex expressEtaIndex,
            ShadowLineCursorEntry entry,
            RelationPhaseWindow selectedWindow)
        {
            if (expressEtaIndex == null)
                return float.MaxValue;

            if (entry.OwnLineAtomCoordinate >= selectedWindow.ExpressCorridor.StartAtomIndex)
                return 0f;

            return expressEtaIndex.FramesBetween(entry.OwnLineAtomCoordinate, selectedWindow.ExpressCorridor.StartAtomIndex);
        }

        private static int CompareHitCandidate(HitCandidate left, HitCandidate right)
        {
            if (left.EntryEtaFrames != right.EntryEtaFrames)
                return left.EntryEtaFrames.CompareTo(right.EntryEtaFrames);
            if (left.EntryDistanceAtoms != right.EntryDistanceAtoms)
                return left.EntryDistanceAtoms.CompareTo(right.EntryDistanceAtoms);
            if (left.OnTrunk != right.OnTrunk)
                return left.OnTrunk ? -1 : 1;
            if (left.RelevantSharedEntryAtomIndex != right.RelevantSharedEntryAtomIndex)
                return left.RelevantSharedEntryAtomIndex.CompareTo(right.RelevantSharedEntryAtomIndex);
            if (left.CursorAtomIndex != right.CursorAtomIndex)
                return left.CursorAtomIndex.CompareTo(right.CursorAtomIndex);
            if (left.ExpressLine != right.ExpressLine)
                return left.ExpressLine.Index.CompareTo(right.ExpressLine.Index);
            return left.Vehicle.Index.CompareTo(right.Vehicle.Index);
        }

        private static int CompareShadowCursorEntry(ShadowLineCursorEntry left, ShadowLineCursorEntry right)
        {
            if (left.TraversalPhaseIndex != right.TraversalPhaseIndex)
                return left.TraversalPhaseIndex.CompareTo(right.TraversalPhaseIndex);
            int coordinateCompare = left.OwnLineAtomCoordinate.CompareTo(right.OwnLineAtomCoordinate);
            if (coordinateCompare != 0)
                return coordinateCompare;
            return left.Vehicle.Index.CompareTo(right.Vehicle.Index);
        }

        private bool TryFindBestCurrentSceneRelationTrunkSegment(
            SceneExpressRelation relation,
            BypassProtectedInterval localProtectedInterval,
            int localTraversalPhaseIndex,
            int expressCurrentAtomIndex,
            int expressTraversalPhaseIndex,
            int expressPhaseEndAtomExclusive,
            out GlobalSharedTrunkSegment segment,
            out int segmentIndex)
        {
            segment = default;
            segmentIndex = -1;
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
                int anchorDistance = math.max(0, candidate.ExpressAnchorStartAtomIndex - expressCurrentAtomIndex);
                bool better = !found;
                if (!better && expressApproachDistance != bestExpressApproachDistance)
                    better = expressApproachDistance < bestExpressApproachDistance;
                if (!better && anchorDistance != bestAnchorDistance)
                    better = anchorDistance < bestAnchorDistance;
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

                bestExpressApproachDistance = expressApproachDistance;
                bestAnchorDistance = anchorDistance;
                bestOrderedRun = candidate.OrderedRun;
                bestPhysicalOverlap = candidate.PhysicalOverlap;
                bestLocalOverlap = localOverlap;
                bestExpressOverlap = expressOverlap;
                segment = candidate;
                segmentIndex = i;
                found = true;
            }

            return found;
        }

        private static bool TryProjectConflictCorridorsFromTrunkSkeleton(
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

        private static int CountAtomIntervalOverlap(int startA, int endAExclusive, int startB, int endBExclusive)
        {
            int overlapStart = math.max(startA, startB);
            int overlapEndExclusive = math.min(endAExclusive, endBExclusive);
            return math.max(0, overlapEndExclusive - overlapStart);
        }

        private void RecordUnavailable(string reason)
        {
            m_UnavailableCalls++;
            IncrementBucket(m_UnavailableByReason, string.IsNullOrWhiteSpace(reason) ? "unavailable:internal-error" : reason);
        }

        private void SetStatus(Entity vehicle, DangerWindowShadowStatus status)
        {
            if (vehicle != Entity.Null)
                m_LastStatusByVehicle[vehicle] = status;
        }

        private static string TrimUnavailablePrefix(string reason)
        {
            const string prefix = "unavailable:";
            if (string.IsNullOrWhiteSpace(reason))
                return "internal-error";

            return reason.StartsWith(prefix, StringComparison.Ordinal)
                ? reason.Substring(prefix.Length)
                : reason;
        }

        private void RecordMiss(string reason)
        {
            IncrementBucket(m_MissByReason, string.IsNullOrWhiteSpace(reason) ? "window-no-hit" : reason);
        }

        private static void IncrementBucket(Dictionary<string, ulong> buckets, string reason)
        {
            if (!buckets.TryGetValue(reason, out ulong count))
                count = 0;
            buckets[reason] = count + 1;
        }

        private void RecordMismatch(
            CompareContext context,
            BypassTrackModelDecision oldDecision,
            DangerWindowShadowDecision shadowDecision)
        {
            if (m_MismatchSamples.Count >= MAX_MISMATCH_SAMPLES)
                return;

            string sceneLabel = "line=" + context.Scope.Line.Index
                + "/building=" + context.Scope.CurrentBypassBuilding.Index
                + "/p=" + context.Scope.SceneKey.ProtectedIntervalIndex;
            m_MismatchSamples.Add(
                "[BypassDangerWindowMismatch] local=" + context.Scope.Vehicle.Index
                + " line=" + context.Scope.Line.Index
                + " wp=" + context.Scope.WaypointIndex
                + " scene=" + sceneLabel
                + " oldBlocker=" + oldDecision.BlockerVehicle.Index
                + " oldReason=" + oldDecision.ReasonCode
                + " shadowHasBlocker=" + (shadowDecision.HasBlocker ? "1" : "0")
                + " shadowBlocker=" + (shadowDecision.BlockerVehicle == Entity.Null ? "-" : shadowDecision.BlockerVehicle.Index.ToString())
                + " missReason=" + (string.IsNullOrWhiteSpace(shadowDecision.MissReason) ? "-" : shadowDecision.MissReason)
                + " relation=" + shadowDecision.MatchedRelationIndex
                + " trunk=" + shadowDecision.MatchedTrunkIndex
                + " window=a" + shadowDecision.DangerWindowStartAtomIndex + ".." + shadowDecision.DangerWindowEndAtomIndexExclusive
                + " phase=" + context.LocalPosition.TraversalPhaseIndex
                + " localClear=" + FormatEtaFrames(shadowDecision.LocalClearFrames)
                + " safeEntryDeadline=" + FormatEtaFrames(shadowDecision.SafeEntryDeadlineFrames));
        }

        private static string FormatBuckets(Dictionary<string, ulong> buckets)
        {
            if (buckets.Count == 0)
                return "{}";

            var parts = new List<string>(buckets.Count);
            foreach (KeyValuePair<string, ulong> entry in buckets)
                parts.Add(entry.Key + "=" + entry.Value);
            return "{" + string.Join(",", parts) + "}";
        }

        private string FormatEtaFrames(float frames)
        {
            if (frames == float.MaxValue)
                return "?";

            return m_Runtime.ClockSnapshot.ToMinutes(frames).ToString("0.0") + "m";
        }
    }
}
