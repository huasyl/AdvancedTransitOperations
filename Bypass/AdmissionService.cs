using System;
using System.Collections.Generic;
using System.Text;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Bypass
{
    internal sealed class AdmissionService : IDecisionContext
    {
        private readonly IBypassAdmissionRuntimeContext m_Runtime;
        private readonly DecisionEngine m_Decision;
        private readonly BypassQueue m_Queue;
        private readonly SceneStaticIndex m_SceneIndex;
        internal AdmissionService(IBypassAdmissionRuntimeContext runtime)
        {
            m_Runtime = runtime;
            m_Decision = new DecisionEngine(this);
            m_Queue = new BypassQueue(this);
            m_SceneIndex = new SceneStaticIndex(runtime, new SceneStaticIndexCallbacks(this));
        }

        internal RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe Probe => m_Runtime.HotPathProbe;

        internal const int MIN_STRONG_PROTECTED_INTERVAL_OVERLAP_ATOMS = 3;
        internal const int MIN_STRONG_PROTECTED_INTERVAL_ORDERED_RUN = 2;
        private const float PROTECTED_INTERVAL_TAIL_CLEARANCE_ATOMS = 1.25f;
        private const float SAME_DIRECTION_AHEAD_MARGIN_ATOMS = 0.75f;
        internal const float TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES = 1f;
        internal const float LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS = 8f;
        private const int MAX_CONFLICT_CORRIDOR_GAP_ATOMS = 8;
        private const uint BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES = 3600;
        private const uint PERF_PROBE_SCENE_EXPRESS_LINE_RECENT_WINDOW_FRAMES = 30;
        private const uint BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES = 60;
        private const uint BYPASS_LATCHED_RELEASE_RECHECK_INTERVAL_FRAMES = 6;
        private const uint VANILLA_BLOCKER_RESCUE_STALL_FRAMES = 240;
        private const uint VANILLA_BLOCKER_RESCUE_RECHECK_FRAMES = 60;
        private const uint VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES = 20;
        private const int VANILLA_BLOCKER_CHAIN_MAX_DEPTH = 12;
        private const uint LINE_ORDERED_RUNTIME_FORCE_FULL_SORT_INTERVAL_FRAMES = 360;
        private const uint LINE_ORDERED_PROBE_LOG_INTERVAL_FRAMES = 3600;

        private readonly Dictionary<Entity, string> m_BypassTrackModelDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelDecisionThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelDecisionLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_BypassSelectedBlockerDetailLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassSelectedBlockerDetailLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_TrainLaneSourceDiagnosticLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SameStationMissLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassTrackModelCompareThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BypassTrackModelCompareLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_BypassExitClearLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditSummaryLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_SharedWindowAuditThrottleCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_SharedWindowAuditLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<SharedWindowPairStateKey, string> m_SharedWindowAuditPairStateCache = new Dictionary<SharedWindowPairStateKey, string>();
        private uint m_BypassPerfProbeLastLogFrame;
        private ulong m_BypassPerfProbeCadenceCalls;
        private ulong m_BypassPerfProbeCadenceMisses;
        private ulong m_BypassPerfProbeBaselineCalls;
        private ulong m_BypassPerfProbeTrackDecisionCalls;
        private ulong m_BypassPerfProbeResolveCalls;
        private ulong m_BypassPerfProbeActiveCorridorCalls;
        private ulong m_BypassPerfProbeSceneSamples;
        private ulong m_BypassPerfProbeSceneCandidateVehicles;
        private ulong m_BypassPerfProbeSceneAdmittedCandidates;
        private ulong m_BypassPerfProbeSceneFrontiers;
        private ulong m_BypassPerfProbeSameStationCalls;
        private ulong m_BypassPerfProbeSameStationReusedCandidates;
        private ulong m_BypassPerfProbeDeepCorridorEntries;
        private ulong m_BypassPerfProbeEpisodeReuses;
        private ulong m_PerfProbeSceneExpressLineQueries;
        private ulong m_PerfProbeSceneExpressLineSameFrameRequeries;
        private ulong m_PerfProbeSceneExpressLineConsecutiveFrameRequeries;
        private ulong m_PerfProbeSceneExpressLineRecentFrameRequeries;
        private readonly Dictionary<Entity, uint> m_PerfProbeSceneExpressLineLastQueryFrame = new Dictionary<Entity, uint>();
        private ulong m_LineOrderedProbeExpressLineQueries;
        private ulong m_LineOrderedProbeOrderedAttempts;
        private ulong m_LineOrderedProbeHeadOnlySuccesses;
        private ulong m_LineOrderedProbeFallbacks;
        private ulong m_LineOrderedProbeHeadCandidateBuilds;
        private ulong m_LineOrderedProbeFallbackCandidateBuilds;
        private uint m_LineOrderedProbeLastLogFrame;
        private readonly Dictionary<Entity, LineOrderedRuntimeState> m_LineOrderedRuntimeStates = new Dictionary<Entity, LineOrderedRuntimeState>();
        private readonly Dictionary<Entity, string> m_LineOrderedRuntimeForceRefreshReasons = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineOrderedRuntimeLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> m_SharedWindowMatchSnapshots = new Dictionary<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot>();
        private readonly Dictionary<LocalSceneExpressStaticMatchCacheKey, LocalSceneExpressStaticMatchSnapshot> m_LocalSceneExpressStaticMatchSnapshots = new Dictionary<LocalSceneExpressStaticMatchCacheKey, LocalSceneExpressStaticMatchSnapshot>();
        private readonly Dictionary<LocalSceneCandidateExpressLinesCacheKey, LocalSceneCandidateExpressLinesSnapshot> m_LocalSceneCandidateExpressLinesSnapshots = new Dictionary<LocalSceneCandidateExpressLinesCacheKey, LocalSceneCandidateExpressLinesSnapshot>();
        private readonly Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot> m_ActiveConflictCorridorSnapshots = new Dictionary<ActiveConflictCorridorCacheKey, ActiveConflictCorridorSnapshot>();
        private readonly Dictionary<Entity, BypassLineExecutionModeSnapshot> m_LineBypassExecutionModeSnapshots = new Dictionary<Entity, BypassLineExecutionModeSnapshot>();
        private readonly Dictionary<Entity, string> m_LineBypassExecutionModeLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, VanillaBlockerStall> m_VanillaBlockerStalls = new Dictionary<Entity, VanillaBlockerStall>();
        private readonly Dictionary<Entity, Entity> m_BypassHoldSkipped = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, Entity> m_Watches = new Dictionary<Entity, Entity>();
        private readonly Dictionary<QueuedLocalReleaseFrameCacheKey, bool> m_QueuedLocalReleaseFrameCache = new Dictionary<QueuedLocalReleaseFrameCacheKey, bool>();
        // Version-stamped so boarding precheck avoids scene snapshot rebuilds while stale line scenes cannot skip bypass.
        private readonly Dictionary<Entity, StopSceneEligibilityLineCache> m_StopSceneEligibilityLineCaches = new Dictionary<Entity, StopSceneEligibilityLineCache>();
        private uint m_ActiveConflictCorridorSnapshotFrame;
        private uint m_QueuedLocalReleaseFrameCacheFrame;

        private sealed class StopSceneEligibilityLineCache
        {
            public readonly int WaypointCount;
            public readonly ulong ChainSignature;
            public readonly uint LocalSceneVersion;
            public readonly uint SharedTrackVersion;
            public readonly bool[] EligibleByWaypoint;

            public StopSceneEligibilityLineCache(
                int waypointCount,
                ulong chainSignature,
                uint localSceneVersion,
                uint sharedTrackVersion,
                bool[] eligibleByWaypoint)
            {
                WaypointCount = waypointCount;
                ChainSignature = chainSignature;
                LocalSceneVersion = localSceneVersion;
                SharedTrackVersion = sharedTrackVersion;
                EligibleByWaypoint = eligibleByWaypoint;
            }
        }

        private readonly struct VanillaBlockerStall
        {
            public readonly Entity Blocker;
            public readonly uint FirstSeenFrame;
            public readonly uint LastSeenFrame;
            public readonly uint LastResolvedFrame;

            public VanillaBlockerStall(Entity blocker, uint firstSeenFrame, uint lastSeenFrame, uint lastResolvedFrame)
            {
                Blocker = blocker;
                FirstSeenFrame = firstSeenFrame;
                LastSeenFrame = lastSeenFrame;
                LastResolvedFrame = lastResolvedFrame;
            }
        }

        private readonly struct QueuedLocalReleaseFrameCacheKey : IEquatable<QueuedLocalReleaseFrameCacheKey>
        {
            public readonly Entity LocalVehicle;
            public readonly SceneKey SceneKey;
            public readonly Entity Blocker;

            public QueuedLocalReleaseFrameCacheKey(Entity localVehicle, SceneKey sceneKey, Entity blocker)
            {
                LocalVehicle = localVehicle;
                SceneKey = sceneKey;
                Blocker = blocker;
            }

            public bool Equals(QueuedLocalReleaseFrameCacheKey other)
            {
                return LocalVehicle == other.LocalVehicle
                    && SceneKey.Equals(other.SceneKey)
                    && Blocker == other.Blocker;
            }

            public override bool Equals(object obj)
            {
                return obj is QueuedLocalReleaseFrameCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = LocalVehicle.GetHashCode();
                    hash = (hash * 397) ^ SceneKey.GetHashCode();
                    hash = (hash * 397) ^ Blocker.GetHashCode();
                    return hash;
                }
            }
        }

        private static bool IsTrackModelDiagnosticLoggingEnabled() => false;
        private static bool IsBypassPerfProbeLoggingEnabled() => RtLog.VerboseEnabled;
        private static bool IsLineOrderedRuntimeProbeLoggingEnabled() => RtLog.VerboseEnabled;

        internal void Dispose() => m_Decision.Dispose();
        internal void Clear()
        {
            m_Decision.Clear();
            m_Queue.Clear();
            m_BypassTrackModelDecisionLogCache.Clear();
            m_BypassTrackModelDecisionThrottleCache.Clear();
            m_BypassTrackModelDecisionLastLogFrame.Clear();
            m_BypassSelectedBlockerDetailLogCache.Clear();
            m_BypassSelectedBlockerDetailLastLogFrame.Clear();
            m_TrainLaneSourceDiagnosticLogCache.Clear();
            m_SameStationMissLogCache.Clear();
            m_BypassTrackModelCompareLogCache.Clear();
            m_BypassTrackModelCompareThrottleCache.Clear();
            m_BypassTrackModelCompareLastLogFrame.Clear();
            m_BypassExitClearLogCache.Clear();
            m_SharedWindowAuditSummaryLogCache.Clear();
            m_SharedWindowAuditThrottleCache.Clear();
            m_SharedWindowAuditLastLogFrame.Clear();
            m_SharedWindowAuditPairStateCache.Clear();
            m_LineOrderedRuntimeStates.Clear();
            m_LineOrderedRuntimeForceRefreshReasons.Clear();
            m_LineOrderedRuntimeLogCache.Clear();
            m_SharedWindowMatchSnapshots.Clear();
            m_LocalSceneExpressStaticMatchSnapshots.Clear();
            m_LocalSceneCandidateExpressLinesSnapshots.Clear();
            m_ActiveConflictCorridorSnapshots.Clear();
            m_ActiveConflictCorridorSnapshotFrame = 0;
            m_LineBypassExecutionModeSnapshots.Clear();
            m_LineBypassExecutionModeLogCache.Clear();
            m_SceneIndex.Clear();
            m_VanillaBlockerStalls.Clear();
            m_Runtime.ClearRuntimeDeadlines(DeadlineKind.RescueProbe);
            m_Runtime.ClearRuntimeDeadlines(DeadlineKind.RescueStall);
            m_Runtime.ClearRuntimeDeadlines(DeadlineKind.RescueRecheck);
            m_Runtime.ClearRuntimeBypassActive();
            m_Runtime.ClearRuntimeBypassWatch();
            m_BypassHoldSkipped.Clear();
            m_Watches.Clear();
            m_QueuedLocalReleaseFrameCache.Clear();
            m_StopSceneEligibilityLineCaches.Clear();
            m_QueuedLocalReleaseFrameCacheFrame = 0;
        }
        internal void InvalidateStaticSceneIndex()
        {
            m_SceneIndex.MarkDirty();
            m_StopSceneEligibilityLineCaches.Clear();
        }
        internal void WarmStaticSceneIndex() => m_SceneIndex.WarmAll();
        internal void ClearRescue(Entity vehicle)
        {
            m_VanillaBlockerStalls.Remove(vehicle);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueProbe);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueStall);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueRecheck);
        }
        internal void ClearVehicle(Entity vehicle)
        {
            m_Decision.Remove(vehicle);
            m_VanillaBlockerStalls.Remove(vehicle);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueProbe);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueStall);
            m_Runtime.ClearRuntimeDeadline(vehicle, DeadlineKind.RescueRecheck);
            m_Runtime.SetRuntimeBypassActive(vehicle, false);
            ClearWatch(vehicle);
            m_BypassHoldSkipped.Remove(vehicle);
        }
        internal void ClearInactive(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || TryGetBypassHoldSkipped(vehicle, out _)
                || m_Decision.TryGetLatchedBlocker(vehicle, out _))
            {
                return;
            }

            m_Decision.Remove(vehicle, BypassEntryKind.Cadence);
            m_Decision.Remove(vehicle, BypassEntryKind.Episode);
            m_Runtime.SetRuntimeBypassActive(vehicle, false);
        }
        internal void MarkBypassHoldSkipped(Entity vehicle, Entity blocker)
        {
            if (vehicle != Entity.Null)
                m_BypassHoldSkipped[vehicle] = blocker;
        }
        internal bool TryGetBypassHoldSkipped(Entity vehicle, out Entity blocker)
        {
            if (vehicle != Entity.Null && m_BypassHoldSkipped.TryGetValue(vehicle, out blocker))
                return true;

            blocker = Entity.Null;
            return false;
        }
        internal void ClearBypassHoldSkipped(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_BypassHoldSkipped.Remove(vehicle);
        }
        internal void FlushPerfProbeIfDue(uint nowFrame)
        {
            if (!IsBypassPerfProbeLoggingEnabled())
                return;

            if (m_BypassPerfProbeLastLogFrame == 0)
            {
                m_BypassPerfProbeLastLogFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_BypassPerfProbeLastLogFrame;
            if (elapsedFrames < BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES)
                return;

            if (m_BypassPerfProbeCadenceCalls > 0
                || m_BypassPerfProbeCadenceMisses > 0
                || m_BypassPerfProbeBaselineCalls > 0
                || m_BypassPerfProbeTrackDecisionCalls > 0
                || m_BypassPerfProbeResolveCalls > 0
                || m_BypassPerfProbeActiveCorridorCalls > 0
                || m_BypassPerfProbeSceneSamples > 0
                || m_BypassPerfProbeSceneCandidateVehicles > 0
                || m_BypassPerfProbeSceneAdmittedCandidates > 0
                || m_BypassPerfProbeSceneFrontiers > 0
                || m_BypassPerfProbeSameStationCalls > 0
                || m_BypassPerfProbeSameStationReusedCandidates > 0
                || m_BypassPerfProbeDeepCorridorEntries > 0
                || m_BypassPerfProbeEpisodeReuses > 0
                || m_PerfProbeSceneExpressLineQueries > 0
                || m_PerfProbeSceneExpressLineSameFrameRequeries > 0
                || m_PerfProbeSceneExpressLineConsecutiveFrameRequeries > 0
                || m_PerfProbeSceneExpressLineRecentFrameRequeries > 0)
            {
                m_Runtime.Log.Info("[待避轻量计数] frames=" + elapsedFrames
                    + " cadence=" + m_BypassPerfProbeCadenceCalls
                    + " miss=" + m_BypassPerfProbeCadenceMisses
                    + " baseline=" + m_BypassPerfProbeBaselineCalls
                    + " trackDecision=" + m_BypassPerfProbeTrackDecisionCalls
                    + " resolve=" + m_BypassPerfProbeResolveCalls
                    + " activeCorridor=" + m_BypassPerfProbeActiveCorridorCalls
                    + " scenes=" + m_BypassPerfProbeSceneSamples
                    + " cand=" + m_BypassPerfProbeSceneCandidateVehicles
                    + " admitted=" + m_BypassPerfProbeSceneAdmittedCandidates
                    + " frontiers=" + m_BypassPerfProbeSceneFrontiers
                    + " sameReuse=" + m_BypassPerfProbeSameStationReusedCandidates
                    + " sameCalls=" + m_BypassPerfProbeSameStationCalls
                    + " deepCorridor=" + m_BypassPerfProbeDeepCorridorEntries
                    + " episodeReuse=" + m_BypassPerfProbeEpisodeReuses
                    + " expressLineQ=" + m_PerfProbeSceneExpressLineQueries
                    + " expressLineSameFrame=" + m_PerfProbeSceneExpressLineSameFrameRequeries
                    + " expressLineConsecutive=" + m_PerfProbeSceneExpressLineConsecutiveFrameRequeries
                    + " expressLineRecent=" + m_PerfProbeSceneExpressLineRecentFrameRequeries);
            }

            m_BypassPerfProbeLastLogFrame = nowFrame;
            m_BypassPerfProbeCadenceCalls = 0;
            m_BypassPerfProbeCadenceMisses = 0;
            m_BypassPerfProbeBaselineCalls = 0;
            m_BypassPerfProbeTrackDecisionCalls = 0;
            m_BypassPerfProbeResolveCalls = 0;
            m_BypassPerfProbeActiveCorridorCalls = 0;
            m_BypassPerfProbeSceneSamples = 0;
            m_BypassPerfProbeSceneCandidateVehicles = 0;
            m_BypassPerfProbeSceneAdmittedCandidates = 0;
            m_BypassPerfProbeSceneFrontiers = 0;
            m_BypassPerfProbeSameStationCalls = 0;
            m_BypassPerfProbeSameStationReusedCandidates = 0;
            m_BypassPerfProbeDeepCorridorEntries = 0;
            m_BypassPerfProbeEpisodeReuses = 0;
            m_PerfProbeSceneExpressLineQueries = 0;
            m_PerfProbeSceneExpressLineSameFrameRequeries = 0;
            m_PerfProbeSceneExpressLineConsecutiveFrameRequeries = 0;
            m_PerfProbeSceneExpressLineRecentFrameRequeries = 0;
        }
        internal void FlushLineOrderedProbeIfDue(uint nowFrame)
        {
            if (!IsLineOrderedRuntimeProbeLoggingEnabled())
                return;

            if (m_LineOrderedProbeLastLogFrame == 0)
            {
                m_LineOrderedProbeLastLogFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_LineOrderedProbeLastLogFrame;
            if (elapsedFrames < LINE_ORDERED_PROBE_LOG_INTERVAL_FRAMES)
                return;

            if (m_LineOrderedProbeExpressLineQueries > 0
                || m_LineOrderedProbeOrderedAttempts > 0
                || m_LineOrderedProbeHeadOnlySuccesses > 0
                || m_LineOrderedProbeFallbacks > 0
                || m_LineOrderedProbeHeadCandidateBuilds > 0
                || m_LineOrderedProbeFallbackCandidateBuilds > 0)
            {
                m_Runtime.Log.Info("[LineOrderedProbe] frames=" + elapsedFrames
                    + " expressLineQ=" + m_LineOrderedProbeExpressLineQueries
                    + " orderedAttempts=" + m_LineOrderedProbeOrderedAttempts
                    + " headOnly=" + m_LineOrderedProbeHeadOnlySuccesses
                    + " fallback=" + m_LineOrderedProbeFallbacks
                    + " headBuilds=" + m_LineOrderedProbeHeadCandidateBuilds
                    + " fallbackBuilds=" + m_LineOrderedProbeFallbackCandidateBuilds);
            }

            m_LineOrderedProbeLastLogFrame = nowFrame;
            m_LineOrderedProbeExpressLineQueries = 0;
            m_LineOrderedProbeOrderedAttempts = 0;
            m_LineOrderedProbeHeadOnlySuccesses = 0;
            m_LineOrderedProbeFallbacks = 0;
            m_LineOrderedProbeHeadCandidateBuilds = 0;
            m_LineOrderedProbeFallbackCandidateBuilds = 0;
        }
        internal BypassDecisionResult EvaluateDepartureGate(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, uint nowFrame)
        {
            return m_Decision.Evaluate(vehicle, line, waypoints, waypointIndex, nowFrame);
        }
        internal Entity FindBlocker(BypassDecisionResult result) => m_Decision.FindBlocker(result);
        internal bool CanRelease(BypassDecisionResult result) => m_Decision.CanRelease(result);
        internal bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker) => m_Decision.TryGetLatchedBlocker(vehicle, out blocker);
        internal bool IsStopSceneEligible(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, out bool known)
        {
            known = false;
            if (line == Entity.Null
                || waypointIndex <= 0
                || waypointIndex >= waypoints.Length)
            {
                return true;
            }

            if (m_StopSceneEligibilityLineCaches.TryGetValue(line, out StopSceneEligibilityLineCache cached))
            {
                if (TryValidateStopSceneEligibilityCache(line, waypoints.Length, cached)
                    && waypointIndex < cached.EligibleByWaypoint.Length)
                {
                    known = true;
                    return cached.EligibleByWaypoint[waypointIndex];
                }

                m_StopSceneEligibilityLineCaches.Remove(line);
            }

            if (m_Runtime.TrackModel.TryGetLocalSceneSnapshot(
                    line,
                    waypoints,
                    waypointIndex,
                    out LineTrackChain chain,
                    out _))
            {
                if (TryCacheStopSceneEligibility(line, waypoints.Length, chain, out cached)
                    && waypointIndex < cached.EligibleByWaypoint.Length)
                {
                    known = true;
                    return cached.EligibleByWaypoint[waypointIndex];
                }

                known = true;
                return true;
            }

            if (TryCacheStopSceneEligibility(line, waypoints.Length, chain, out cached)
                && waypointIndex < cached.EligibleByWaypoint.Length)
            {
                known = true;
                return cached.EligibleByWaypoint[waypointIndex];
            }

            return true;
        }

        internal void UpdateWatch(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            bool boarding,
            bool sceneKnown,
            bool sceneEligible)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !boarding || waypointIndex <= 0)
                return;

            if (TryGetBypassHoldSkipped(vehicle, out _))
                return;

            if (sceneKnown && !sceneEligible)
            {
                ClearWatch(vehicle);
                return;
            }

            m_Watches[vehicle] = line;
            m_Runtime.SetRuntimeBypassWatch(vehicle, true);
        }

        internal void ClearWatchLine(Entity line)
        {
            if (line == Entity.Null || m_Watches.Count == 0)
                return;

            List<Entity> vehicles = null;
            foreach (KeyValuePair<Entity, Entity> entry in m_Watches)
            {
                if (entry.Value != line)
                    continue;

                vehicles ??= new List<Entity>();
                vehicles.Add(entry.Key);
            }

            if (vehicles == null)
                return;

            for (int i = 0; i < vehicles.Count; i++)
                ClearWatch(vehicles[i]);
        }

        private void ClearWatch(Entity vehicle)
        {
            m_Watches.Remove(vehicle);
            m_Runtime.SetRuntimeBypassWatch(vehicle, false);
        }

        private bool TryValidateStopSceneEligibilityCache(Entity line, int waypointCount, StopSceneEligibilityLineCache cache)
        {
            if (cache == null
                || cache.WaypointCount != waypointCount
                || cache.SharedTrackVersion != m_Runtime.TrackModel.SharedIndexVersion
                || !m_Runtime.TrackModel.TryChain(line, out LineTrackChain chain)
                || chain == null)
            {
                return false;
            }

            return chain.Signature == cache.ChainSignature
                && chain.LocalBypassWaypointScenesVersion == cache.LocalSceneVersion
                && cache.LocalSceneVersion != 0;
        }

        private bool TryCacheStopSceneEligibility(Entity line, int waypointCount, LineTrackChain chain, out StopSceneEligibilityLineCache cache)
        {
            cache = null;
            if (line == Entity.Null || chain == null)
                return false;

            if (chain.LocalBypassWaypointScenes == null
                || chain.LocalBypassWaypointScenesVersion == 0
                || chain.LocalBypassWaypointScenes.Length != waypointCount)
            {
                return false;
            }

            bool[] eligibleByWaypoint = new bool[waypointCount];
            for (int i = 0; i < chain.LocalBypassWaypointScenes.Length; i++)
                eligibleByWaypoint[i] = chain.LocalBypassWaypointScenes[i].Available;

            cache = new StopSceneEligibilityLineCache(
                waypointCount,
                chain.Signature,
                chain.LocalBypassWaypointScenesVersion,
                m_Runtime.TrackModel.SharedIndexVersion,
                eligibleByWaypoint);
            m_StopSceneEligibilityLineCaches[line] = cache;
            return true;
        }
        internal void SetBlocker(Entity vehicle, Entity blocker)
        {
            m_Decision.SetBlocker(vehicle, blocker);
            m_Runtime.SetRuntimeBypassActive(vehicle, true);
        }

        internal void ClearBlocker(Entity vehicle)
        {
            m_Decision.ClearBlocker(vehicle);
            m_Runtime.SetRuntimeBypassActive(vehicle, false);
        }
        internal void RemoveCadence(Entity vehicle)
        {
            m_Decision.Remove(vehicle, BypassEntryKind.Cadence);
        }
        internal void RemoveEpisode(Entity vehicle) => m_Decision.Remove(vehicle, BypassEntryKind.Episode);
        internal List<Entity> ReleaseLine(Entity line, Func<Entity, Entity> resolveLine)
        {
            if (line == Entity.Null)
                return null;

            m_Decision.ClearLocalLineGateForLine(line);
            m_StopSceneEligibilityLineCaches.Remove(line);

            List<Entity> releasedVehicles = null;
            if (m_Decision.Blockers.IsCreated)
            {
                var blockerKeys = m_Decision.Blockers.GetKeyArray(Allocator.Temp);
                for (int i = blockerKeys.Length - 1; i >= 0; i--)
                {
                    Entity vehicle = blockerKeys[i];
                    Entity localLine = resolveLine != null ? resolveLine(vehicle) : Entity.Null;
                    Entity blockerLine = m_Decision.Blockers.TryGetValue(vehicle, out Entity blockerVehicle) && resolveLine != null
                        ? resolveLine(blockerVehicle)
                        : Entity.Null;
                    if (localLine != line && blockerLine != line)
                        continue;

                    releasedVehicles ??= new List<Entity>();
                    releasedVehicles.Add(vehicle);
                }
                blockerKeys.Dispose();
            }

            List<Entity> cadenceKeys = null;
            foreach (KeyValuePair<Entity, BypassHoldCadenceSnapshot> entry in m_Decision.Cadence)
            {
                if (entry.Value.SceneKey.Line != line)
                    continue;

                cadenceKeys ??= new List<Entity>();
                cadenceKeys.Add(entry.Key);
            }

            if (cadenceKeys != null)
            {
                for (int i = 0; i < cadenceKeys.Count; i++)
                    m_Decision.Remove(cadenceKeys[i], BypassEntryKind.Cadence);
            }

            List<Entity> scopeKeys = null;
            foreach (KeyValuePair<Entity, BypassControlScopeCacheEntry> entry in m_Decision.Scope)
            {
                if (entry.Value.Line != line)
                    continue;

                scopeKeys ??= new List<Entity>();
                scopeKeys.Add(entry.Key);
            }

            if (scopeKeys != null)
            {
                for (int i = 0; i < scopeKeys.Count; i++)
                    m_Decision.Scope.Remove(scopeKeys[i]);
            }

            List<Entity> episodeKeys = null;
            foreach (KeyValuePair<Entity, BypassConflictEpisode> entry in m_Decision.Conflict)
            {
                if (entry.Value.SceneKey.Line != line && entry.Value.ExpressLine != line)
                    continue;

                episodeKeys ??= new List<Entity>();
                episodeKeys.Add(entry.Key);
            }

            if (episodeKeys != null)
            {
                for (int i = 0; i < episodeKeys.Count; i++)
                    m_Decision.Conflict.Remove(episodeKeys[i]);
            }

            return releasedVehicles;
        }

        internal void ClearLineStaticCaches(Entity line)
        {
            if (line == Entity.Null)
                return;

            m_LineOrderedRuntimeStates.Remove(line);
            m_LineOrderedRuntimeForceRefreshReasons.Remove(line);
            m_LineOrderedRuntimeLogCache.Remove(line);
            m_StopSceneEligibilityLineCaches.Remove(line);

            List<SharedWindowMatchCacheKey> sharedWindowKeys = null;
            foreach (KeyValuePair<SharedWindowMatchCacheKey, SharedWindowMatchSnapshot> entry in m_SharedWindowMatchSnapshots)
            {
                if (entry.Key.LocalLine != line && entry.Key.ExpressLine != line)
                    continue;

                sharedWindowKeys ??= new List<SharedWindowMatchCacheKey>();
                sharedWindowKeys.Add(entry.Key);
            }

            if (sharedWindowKeys != null)
            {
                for (int i = 0; i < sharedWindowKeys.Count; i++)
                    m_SharedWindowMatchSnapshots.Remove(sharedWindowKeys[i]);
            }

            List<LocalSceneExpressStaticMatchCacheKey> staticMatchKeys = null;
            foreach (KeyValuePair<LocalSceneExpressStaticMatchCacheKey, LocalSceneExpressStaticMatchSnapshot> entry in m_LocalSceneExpressStaticMatchSnapshots)
            {
                if (entry.Key.LocalLine != line && entry.Key.ExpressLine != line)
                    continue;

                staticMatchKeys ??= new List<LocalSceneExpressStaticMatchCacheKey>();
                staticMatchKeys.Add(entry.Key);
            }

            if (staticMatchKeys != null)
            {
                for (int i = 0; i < staticMatchKeys.Count; i++)
                    m_LocalSceneExpressStaticMatchSnapshots.Remove(staticMatchKeys[i]);
            }

            m_LocalSceneCandidateExpressLinesSnapshots.Clear();
            m_ActiveConflictCorridorSnapshots.Clear();
            m_ActiveConflictCorridorSnapshotFrame = 0;
            m_QueuedLocalReleaseFrameCache.Clear();
            m_QueuedLocalReleaseFrameCacheFrame = 0;
        }
        internal List<Entity> ForgetBlocker(Entity blocker)
        {
            if (blocker == Entity.Null)
                return null;

            List<Entity> episodeKeys = null;
            foreach (KeyValuePair<Entity, BypassConflictEpisode> entry in m_Decision.Conflict)
            {
                if (entry.Value.BlockerVehicle != blocker)
                    continue;

                episodeKeys ??= new List<Entity>();
                episodeKeys.Add(entry.Key);
            }

            if (episodeKeys == null)
                return null;

            for (int i = 0; i < episodeKeys.Count; i++)
                m_Decision.Conflict.Remove(episodeKeys[i]);

            return episodeKeys;
        }
        internal void RequestLineOrderedRuntimeForceRefresh(Entity line, string reason)
        {
            if (line == Entity.Null)
                return;

            m_LineOrderedRuntimeForceRefreshReasons[line] = string.IsNullOrWhiteSpace(reason)
                ? "unspecified"
                : reason;
        }
        internal bool TryFindBypassHeldLocalBlockingExpress(
            Entity expressVehicle,
            Entity expressLine,
            uint nowFrame,
            out Entity localVehicle)
        {
            localVehicle = Entity.Null;
            if (expressVehicle == Entity.Null
                || expressLine == Entity.Null)
            {
                return false;
            }

            if (!ShouldProbeVanillaBlockerRescue(expressVehicle, nowFrame))
                return false;
            m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                nowFrame + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES);

            if (!BypassRun()
                || !Managed(expressLine)
                || !Express(expressLine)
                || !TryReadVanillaBlocker(expressVehicle, out Entity vanillaBlockerSource, out Blocker vanillaBlocker)
                || vanillaBlocker.m_Blocker == Entity.Null)
            {
                m_VanillaBlockerStalls.Remove(expressVehicle);
                m_Runtime.ClearRuntimeDeadline(expressVehicle, DeadlineKind.RescueStall);
                m_Runtime.ClearRuntimeDeadline(expressVehicle, DeadlineKind.RescueRecheck);
                return false;
            }

            Entity firstBlocker = vanillaBlocker.m_Blocker;
            if (!m_VanillaBlockerStalls.TryGetValue(expressVehicle, out VanillaBlockerStall stall)
                || stall.Blocker != firstBlocker
                || nowFrame < stall.LastSeenFrame)
            {
                stall = new VanillaBlockerStall(firstBlocker, nowFrame, nowFrame, 0);
                m_VanillaBlockerStalls[expressVehicle] = stall;
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                    nowFrame + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES);
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueStall,
                    nowFrame + VANILLA_BLOCKER_RESCUE_STALL_FRAMES);
                return false;
            }

            if (nowFrame - stall.FirstSeenFrame < VANILLA_BLOCKER_RESCUE_STALL_FRAMES)
            {
                m_VanillaBlockerStalls[expressVehicle] = new VanillaBlockerStall(firstBlocker, stall.FirstSeenFrame, nowFrame, stall.LastResolvedFrame);
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                    nowFrame + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES);
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueStall,
                    stall.FirstSeenFrame + VANILLA_BLOCKER_RESCUE_STALL_FRAMES);
                return false;
            }

            if (stall.LastResolvedFrame != 0
                && nowFrame - stall.LastResolvedFrame < VANILLA_BLOCKER_RESCUE_RECHECK_FRAMES)
            {
                m_VanillaBlockerStalls[expressVehicle] = new VanillaBlockerStall(firstBlocker, stall.FirstSeenFrame, nowFrame, stall.LastResolvedFrame);
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                    nowFrame + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES);
                m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueRecheck,
                    stall.LastResolvedFrame + VANILLA_BLOCKER_RESCUE_RECHECK_FRAMES);
                return false;
            }

            m_VanillaBlockerStalls[expressVehicle] = new VanillaBlockerStall(firstBlocker, stall.FirstSeenFrame, nowFrame, nowFrame);
            m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                nowFrame + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES);
            m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueRecheck,
                nowFrame + VANILLA_BLOCKER_RESCUE_RECHECK_FRAMES);
            if (!TryResolveVanillaBlockerRoot(firstBlocker, vanillaBlockerSource, out Entity rootBlocker))
                return false;

            Entity localCandidate = ResolveBypassVehicle(rootBlocker);
            Entity rootLine = ResolveLine(localCandidate);
            if (rootLine == Entity.Null
                || !Managed(rootLine)
                || !Local(rootLine)
                || !m_Decision.TryGetLatchedBlocker(localCandidate, out Entity latchedExpress))
            {
                return false;
            }

            Entity latchedExpressLine = ResolveLine(latchedExpress);
            if (latchedExpress != expressVehicle
                && latchedExpressLine != expressLine)
            {
                return false;
            }

            localVehicle = localCandidate;
            return true;
        }

        internal void ArmVanillaBlockerRescue(Entity expressVehicle, Entity expressLine, uint nowFrame)
        {
            if (expressVehicle == Entity.Null
                || expressLine == Entity.Null
                || !BypassRun()
                || !Managed(expressLine)
                || !Express(expressLine))
            {
                return;
            }

            m_Runtime.SetRuntimeDeadline(expressVehicle, DeadlineKind.RescueProbe,
                NextVanillaBlockerRescueProbe(expressVehicle, nowFrame));
        }

        private static bool ShouldProbeVanillaBlockerRescue(Entity vehicle, uint nowFrame)
        {
            uint bucket = (uint)(vehicle.Index & 0x7fffffff) % VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES;
            return nowFrame % VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES == bucket;
        }

        private static uint NextVanillaBlockerRescueProbe(Entity vehicle, uint nowFrame)
        {
            uint bucket = (uint)(vehicle.Index & 0x7fffffff) % VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES;
            uint remainder = nowFrame % VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES;
            uint offset = (bucket + VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES - remainder)
                % VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES;
            if (offset == 0)
                offset = VANILLA_BLOCKER_RESCUE_PROBE_INTERVAL_FRAMES;
            return nowFrame + offset;
        }

        private bool TryReadVanillaBlocker(Entity vehicle, out Entity sourceVehicle, out Blocker blocker)
        {
            sourceVehicle = Entity.Null;
            blocker = default;
            if (vehicle == Entity.Null)
                return false;

            if (m_Runtime.EntityManager.Exists(vehicle)
                && m_Runtime.EntityManager.HasComponent<Blocker>(vehicle))
            {
                sourceVehicle = vehicle;
                blocker = m_Runtime.EntityManager.GetComponentData<Blocker>(vehicle);
                return true;
            }

            Entity runtimeVehicle = m_Runtime.ResolveVehicle(vehicle);
            if (runtimeVehicle == Entity.Null
                || runtimeVehicle == vehicle
                || !m_Runtime.EntityManager.Exists(runtimeVehicle)
                || !m_Runtime.EntityManager.HasComponent<Blocker>(runtimeVehicle))
            {
                return false;
            }

            sourceVehicle = runtimeVehicle;
            blocker = m_Runtime.EntityManager.GetComponentData<Blocker>(runtimeVehicle);
            return true;
        }

        private bool TryResolveVanillaBlockerRoot(Entity firstBlocker, Entity blockedVehicle, out Entity rootBlocker)
        {
            rootBlocker = Entity.Null;
            if (firstBlocker == Entity.Null)
                return false;

            Entity current = firstBlocker;
            Entity previous = blockedVehicle;
            for (int depth = 0; depth < VANILLA_BLOCKER_CHAIN_MAX_DEPTH; depth++)
            {
                if (current == Entity.Null || !m_Runtime.EntityManager.Exists(current))
                    return false;

                Entity normalized = NormalizeVanillaBlockerEntity(current);
                if (normalized != Entity.Null)
                    current = normalized;

                if (!m_Runtime.EntityManager.HasComponent<Blocker>(current))
                {
                    rootBlocker = current;
                    return true;
                }

                Blocker blocker = m_Runtime.EntityManager.GetComponentData<Blocker>(current);
                Entity next = blocker.m_Blocker;
                if (next == Entity.Null)
                {
                    rootBlocker = current;
                    return true;
                }

                if (next == current || next == previous || next == firstBlocker)
                    return false;

                previous = current;
                current = next;
            }

            return false;
        }

        private Entity NormalizeVanillaBlockerEntity(Entity entity)
        {
            if (entity == Entity.Null
                || !m_Runtime.EntityManager.Exists(entity)
                || !m_Runtime.EntityManager.HasComponent<Controller>(entity))
            {
                return entity;
            }

            Entity controller = m_Runtime.EntityManager.GetComponentData<Controller>(entity).m_Controller;
            return controller != Entity.Null && m_Runtime.EntityManager.Exists(controller)
                ? controller
                : entity;
        }

        private Entity ResolveBypassVehicle(Entity entity)
        {
            Entity runtimeVehicle = m_Runtime.ResolveVehicle(entity);
            return runtimeVehicle != Entity.Null && m_Runtime.EntityManager.Exists(runtimeVehicle)
                ? runtimeVehicle
                : entity;
        }
        internal bool Get(Entity vehicle, out BypassControlScopeCacheEntry scope) => m_Decision.Get(vehicle, out scope);
        internal bool Get(Entity vehicle, out BypassHoldCadenceSnapshot cadence) => m_Decision.Get(vehicle, out cadence);
        internal bool Get(Entity vehicle, out BypassConflictEpisode episode) => m_Decision.Get(vehicle, out episode);
        internal void Put(Entity vehicle, BypassControlScopeCacheEntry scope) => m_Decision.Put(vehicle, scope);
        internal void Put(Entity vehicle, BypassHoldCadenceSnapshot cadence) => m_Decision.Put(vehicle, cadence);
        internal void Put(Entity vehicle, BypassConflictEpisode episode) => m_Decision.Put(vehicle, episode);
        internal void Remove(Entity vehicle, BypassEntryKind kind) => m_Decision.Remove(vehicle, kind);
        internal IBypassAdmissionRuntimeContext Runtime => m_Runtime;
        internal NativeHashMap<Entity, Entity> Blockers => m_Decision.Blockers;
        internal Dictionary<Entity, BypassControlScopeCacheEntry> Scope => m_Decision.Scope;
        internal Dictionary<Entity, BypassHoldCadenceSnapshot> Cadence => m_Decision.Cadence;
        internal Dictionary<Entity, BypassConflictEpisode> Conflict => m_Decision.Conflict;

        private bool BypassRun() => m_Runtime.IsBypassRuntimeFeatureEnabled();
        private bool Managed(Entity line) => m_Runtime.IsDispatchRuntimeManagedLine(line);
        private bool Local(Entity line) => m_Runtime.IsAppliedLocal(line);
        private bool Express(Entity line) => m_Runtime.IsAppliedExpress(line);
        private Entity ResolveLine(Entity vehicle) => m_Runtime.ResolveLine(vehicle);
        private bool IsLineOrderedRuntimeLoggingEnabled() => m_Runtime.IsLineOrderedRuntimeLoggingEnabled();

        internal void EnsureLineBypassExecutionModeReady(
            LineTrackChain chain,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (chain == null || waypoints.Length == 0)
                return;

            m_Runtime.TrackModel.EnsureBypassPipelineReady(chain);
            if (chain.LocalBypassWaypointScenes == null
                || chain.LocalBypassWaypointScenes.Length != waypoints.Length
                || chain.LocalBypassWaypointScenesVersion == 0)
            {
                m_Runtime.TrackModel.TryGetLocalSceneSnapshot(
                    chain.LineEntity,
                    waypoints,
                    0,
                    out _,
                    out _);
            }

            if (m_LineBypassExecutionModeSnapshots.TryGetValue(chain.LineEntity, out BypassLineExecutionModeSnapshot cached)
                && cached.LocalSceneVersion == chain.LocalBypassWaypointScenesVersion
                && cached.LocalSceneVersion != 0)
            {
                return;
            }

            int sceneCount = 0;
            int maxExpressLinesPerScene = 0;
            int multiTrunkSceneCount = 0;
            var uniqueScenes = new HashSet<SceneKey>();
            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            LocalBypassWaypointSceneBinding[] bindings = chain.LocalBypassWaypointScenes;
            if (bindings != null)
            {
                for (int waypointIndex = 0; waypointIndex < bindings.Length; waypointIndex++)
                {
                    LocalBypassWaypointSceneBinding binding = bindings[waypointIndex];
                    if (!binding.Available || !uniqueScenes.Add(binding.SceneKey))
                        continue;

                    sceneCount++;
                    List<Entity> candidateExpressLines = GetCandidateExpressLinesForLocalScene(
                        chain,
                        binding.ProtectedInterval,
                        binding.CurrentBypassBuilding);
                    int expressLineCount = candidateExpressLines != null ? candidateExpressLines.Count : 0;
                    if (expressLineCount > maxExpressLinesPerScene)
                        maxExpressLinesPerScene = expressLineCount;

                    bool sceneHasMultiTrunk = false;
                    if (candidateExpressLines != null)
                    {
                        for (int expressIndex = 0; expressIndex < candidateExpressLines.Count; expressIndex++)
                        {
                            Entity expressLine = candidateExpressLines[expressIndex];
                            if (expressLine == Entity.Null
                                || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                                || !TryBuildSceneExpressRelation(
                                    chain,
                                    binding.ProtectedIntervalIndex,
                                    binding.ProtectedInterval,
                                    binding.CurrentBypassBuilding,
                                    expressLine,
                                    expressWaypoints,
                                    out SceneExpressRelation relation))
                            {
                                continue;
                            }

                            if (relation.TrunkCandidates != null && relation.TrunkCandidates.Segments.Count > 1)
                            {
                                sceneHasMultiTrunk = true;
                                break;
                            }
                        }
                    }

                    if (sceneHasMultiTrunk)
                        multiTrunkSceneCount++;
                }
            }

            BypassExecutionMode executionMode =
                sceneCount <= 2
                && maxExpressLinesPerScene <= 1
                && multiTrunkSceneCount == 0
                    ? BypassExecutionMode.SimpleSceneScan
                    : BypassExecutionMode.ComplexLineModel;
            m_LineBypassExecutionModeSnapshots[chain.LineEntity] = new BypassLineExecutionModeSnapshot(
                chain.LocalBypassWaypointScenesVersion,
                sceneCount,
                maxExpressLinesPerScene,
                multiTrunkSceneCount,
                executionMode);

            if (IsLineOrderedRuntimeLoggingEnabled())
            {
                string modeSummary = executionMode
                    + "|scenes=" + sceneCount
                    + "|maxExpressPerScene=" + maxExpressLinesPerScene
                    + "|multiTrunkScenes=" + multiTrunkSceneCount;
                if (!m_LineBypassExecutionModeLogCache.TryGetValue(chain.LineEntity, out string previousSummary)
                    || previousSummary != modeSummary)
                {
                    m_LineBypassExecutionModeLogCache[chain.LineEntity] = modeSummary;
                    m_Runtime.Log.Info("[LineBypassMode] line=" + chain.LineEntity.Index
                        + " mode=" + executionMode
                        + " scenes=" + sceneCount
                        + " maxExpressPerScene=" + maxExpressLinesPerScene
                        + " multiTrunkScenes=" + multiTrunkSceneCount);
                }
            }
        }

        internal BypassExecutionMode ResolveLineBypassExecutionMode(LineTrackChain chain)
        {
            if (chain != null
                && m_LineBypassExecutionModeSnapshots.TryGetValue(chain.LineEntity, out BypassLineExecutionModeSnapshot snapshot)
                && snapshot.LocalSceneVersion == chain.LocalBypassWaypointScenesVersion
                && snapshot.LocalSceneVersion != 0)
            {
                return snapshot.ExecutionMode;
            }

            if (m_SceneIndex.TryGetLineExecutionMode(chain, out BypassExecutionMode indexedMode))
                return indexedMode;

            return BypassExecutionMode.ComplexLineModel;
        }

        private sealed class SceneStaticIndexCallbacks : ISceneStaticIndexBuilder
        {
            private readonly AdmissionService m_Owner;

            internal SceneStaticIndexCallbacks(AdmissionService owner)
            {
                m_Owner = owner;
            }

            public BypassExecutionMode ResolveExecutionMode(LineTrackChain chain)
            {
                return m_Owner.ResolveLineBypassExecutionMode(chain);
            }

            public bool TryBuildRelation(
                LineTrackChain localChain,
                int localProtectedIntervalIndex,
                BypassProtectedInterval localProtectedInterval,
                Entity currentBypassBuilding,
                Entity expressLine,
                DynamicBuffer<RouteWaypoint> expressWaypoints,
                out SceneExpressRelation relation)
            {
                return m_Owner.TryBuildSceneExpressRelation(
                    localChain,
                    localProtectedIntervalIndex,
                    localProtectedInterval,
                    currentBypassBuilding,
                    expressLine,
                    expressWaypoints,
                    out relation);
            }
        }

        bool IDecisionContext.FeatureEnabled() => BypassRun();
        bool IDecisionContext.TryScope(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, out BypassControlScope scope, out string failureReason) => TryGetBypassControlScope(vehicle, line, waypoints, waypointIndex, out scope, out failureReason);
        bool IDecisionContext.IsLocalLine(Entity line) => line != Entity.Null && Managed(line) && Local(line);
        bool IDecisionContext.Exists(Entity entity) => entity != Entity.Null && m_Runtime.EntityManager.Exists(entity);
        bool IDecisionContext.ShouldClearHoldAfterStationExit(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints) => ShouldClearHoldAfterStationExit(scope, waypoints);
        bool IDecisionContext.BlockerAtStation(Entity blocker, Entity station) => IsExpressBlockerStillWithinBypassStation(blocker, station);
        bool IDecisionContext.LatchedBeforeRelease(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, BypassConflictEpisode episode, Entity blocker, out bool beforeRelease) => TryEvaluateLatchedBlockerBeforeRelease(scope, waypoints, episode, blocker, out beforeRelease);
        bool IDecisionContext.ReleaseForQueuedLocal(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker, out string releaseReason)
        {
            releaseReason = null;
            // Temporary same-frame cache: ReuseEpisode can fall through to fresh baseline and ask the same queued-local release question again.
            if (TryGetQueuedLocalReleaseFrameCache(scope, blocker, out bool cachedShouldRelease))
            {
                if (cachedShouldRelease)
                    releaseReason = "express-behind-nearest-queued-local cached=1";
                return cachedShouldRelease;
            }

            bool shouldRelease = ShouldReleaseForQueuedLocalAhead(
                scope,
                waypoints,
                blocker,
                out float expressSceneCoordinate,
                out float localSceneCoordinate,
                out float queuedLocalMeters);
            PutQueuedLocalReleaseFrameCache(scope, blocker, shouldRelease);
            if (!shouldRelease)
                return false;

            LogQueuedLocalBypassOverrideOnce(scope.Vehicle, scope.Line, blocker, "release", "express-behind-nearest-queued-local", expressSceneCoordinate, localSceneCoordinate, queuedLocalMeters);
            MarkBypassHoldSkipped(scope.Vehicle, blocker);
            releaseReason = "express-behind-nearest-queued-local express=" + expressSceneCoordinate.ToString("0.00")
                + " local=" + localSceneCoordinate.ToString("0.00")
                + " queuedLocal=" + queuedLocalMeters.ToString("0.00");
            return true;
        }

        bool IDecisionContext.Baseline(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, uint nowFrame, out bool shouldYield, out string reason, out Entity blocker, out bool hasLatchedBlockerProjection, out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            return TryGetTrackModelBypassBaseline(scope, waypoints, nowFrame, out shouldYield, out reason, out blocker, out hasLatchedBlockerProjection, out latchedBlockerProjection);
        }

        bool IDecisionContext.ApplyDecisionVetoes(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, bool shouldYield, string reason, Entity blocker)
        {
            return ApplyDecisionVetoes(scope.Vehicle, scope.Line, waypoints, scope.WaypointIndex, scope.CurrentBypassBuilding, scope.NextBypassBuilding, shouldYield, reason, blocker);
        }

        Entity IDecisionContext.ResolveLine(Entity vehicle) => ResolveLine(vehicle);
        uint IDecisionContext.EpisodeRecheckFrames() => BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES;
        uint IDecisionContext.LatchedReleaseRecheckFrames() => BYPASS_LATCHED_RELEASE_RECHECK_INTERVAL_FRAMES;
        void IDecisionContext.CountCadenceCall()
        {
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeCadenceCalls++;
        }

        void IDecisionContext.CountCadenceMiss()
        {
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeCadenceMisses++;
        }

        void IDecisionContext.CountEpisodeReuse()
        {
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeEpisodeReuses++;
        }
        bool IDecisionContext.TryGetLatchedBlocker(Entity vehicle, out Entity blocker) => m_Decision.TryGetLatchedBlocker(vehicle, out blocker);

        private bool TryGetQueuedLocalReleaseFrameCache(BypassControlScope scope, Entity blocker, out bool shouldRelease)
        {
            uint frame = m_Runtime.Frame;
            if (m_QueuedLocalReleaseFrameCacheFrame != frame)
            {
                m_QueuedLocalReleaseFrameCache.Clear();
                m_QueuedLocalReleaseFrameCacheFrame = frame;
            }

            return m_QueuedLocalReleaseFrameCache.TryGetValue(
                new QueuedLocalReleaseFrameCacheKey(scope.Vehicle, scope.SceneKey, blocker),
                out shouldRelease);
        }

        private void PutQueuedLocalReleaseFrameCache(BypassControlScope scope, Entity blocker, bool shouldRelease)
        {
            uint frame = m_Runtime.Frame;
            if (m_QueuedLocalReleaseFrameCacheFrame != frame)
            {
                m_QueuedLocalReleaseFrameCache.Clear();
                m_QueuedLocalReleaseFrameCacheFrame = frame;
            }

            m_QueuedLocalReleaseFrameCache[new QueuedLocalReleaseFrameCacheKey(scope.Vehicle, scope.SceneKey, blocker)] = shouldRelease;
        }

        private bool TryGetTrackModelBypassBaseline(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            uint nowFrame,
            out bool shouldYield,
            out string trackModelReason,
            out Entity trackModelBlocker,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeBaselineCalls++;
            shouldYield = false;
            trackModelReason = string.Empty;
            trackModelBlocker = Entity.Null;
            hasLatchedBlockerProjection = false;
            latchedBlockerProjection = default;

            if (!TryEvaluateBypassTrackModelDecision(
                    scope.Vehicle,
                    scope.Line,
                    localWaypoints,
                    scope.WaypointIndex,
                    nowFrame,
                    out BypassTrackModelDecision liveDecision)
                || !liveDecision.Available)
            {
                return false;
            }

            shouldYield = liveDecision.ShouldYield;
            trackModelReason = "track-model-" + liveDecision.ReasonCode;
            trackModelBlocker = liveDecision.BlockerVehicle;
            hasLatchedBlockerProjection = liveDecision.HasLatchedBlockerProjection;
            latchedBlockerProjection = liveDecision.LatchedBlockerProjection;
            return true;
        }

        internal bool TryGetLineOrderedRuntimeState(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineOrderedRuntimeState state)
        {
            state = null;
            if (line == Entity.Null
                || waypoints.Length == 0
                || !m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            if (!TryGetLineRunningVehicleFrameSnapshot(line, waypoints, nowFrame, out LineRunningVehicleFrameSnapshot snapshot))
                return false;

            if (!m_LineOrderedRuntimeStates.TryGetValue(line, out state) || state == null)
            {
                state = new LineOrderedRuntimeState();
                m_LineOrderedRuntimeStates[line] = state;
            }

            RefreshLineOrderedRuntimeState(line, chain, snapshot, nowFrame, state);
            return state.Entries.Count > 0;
        }

        internal bool TryGetLineRunningVehicleFrameSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineRunningVehicleFrameSnapshot snapshot)
        {
            return m_Runtime.TrackProjection.TryGetLineRunningVehicleFrameSnapshot(line, waypoints, nowFrame, out snapshot);
        }

        internal bool TryGetBypassControlScope(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            out BypassControlScope scope,
            out string failureReason)
        {
            bool found = m_Queue.TryGetBypassControlScope(localVehicle, localLine, localWaypoints, currentWaypointIndex, out scope, out failureReason);
            return found;
        }

        internal bool TryEvaluateLatchedBlockerBeforeRelease(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            BypassConflictEpisode episode,
            Entity blockerVehicle,
            out bool blockerStillBeforeRelease)
        {
            return m_Queue.TryEvaluateLatchedBlockerBeforeRelease(scope, localWaypoints, episode, blockerVehicle, out blockerStillBeforeRelease);
        }

        internal void LogQueuedLocalBypassOverrideOnce(
            Entity localVehicle,
            Entity localLine,
            Entity blockerVehicle,
            string result,
            string reason,
            float expressMeters = float.NaN,
            float currentLocalMeters = float.NaN,
            float queuedLocalMeters = float.NaN)
        {
            m_Queue.LogQueuedLocalBypassOverrideOnce(localVehicle, localLine, blockerVehicle, result, reason, expressMeters, currentLocalMeters, queuedLocalMeters);
        }

        internal bool ShouldReleaseForQueuedLocalAhead(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            Entity blockerVehicle,
            out float expressSceneCoordinate,
            out float localSceneCoordinate,
            out float queuedLocalMeters)
        {
            return m_Queue.ShouldReleaseForQueuedLocalAhead(
                scope,
                localWaypoints,
                blockerVehicle,
                out expressSceneCoordinate,
                out localSceneCoordinate,
                out queuedLocalMeters);
        }

        internal bool IsExpressBlockerStillWithinBypassStation(Entity blockerVehicle, Entity localCurrentBypassBuilding)
        {
            return m_Queue.IsExpressBlockerStillWithinBypassStation(blockerVehicle, localCurrentBypassBuilding);
        }

        internal bool ApplyDecisionVetoes(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            bool shouldYield,
            string reason,
            Entity blockerVehicle)
        {
            return m_Queue.ApplyDecisionVetoes(localVehicle, localLine, localWaypoints, currentWaypointIndex, currentBypassBuilding, nextBypassBuilding, shouldYield, reason, blockerVehicle);
        }

        private void RefreshLineOrderedRuntimeState(
            Entity line,
            LineTrackChain chain,
            LineRunningVehicleFrameSnapshot snapshot,
            uint nowFrame,
            LineOrderedRuntimeState state)
        {
            state.ScratchEntriesByVehicle.Clear();
            int unresolvedCount = 0;
            for (int i = 0; i < snapshot.Vehicles.Count; i++)
            {
                LineRunningVehicleSnapshot runningVehicle = snapshot.Vehicles[i];
                if (!TryBuildOrderedLineVehicleEntry(chain, runningVehicle, out OrderedLineVehicleEntry entry))
                {
                    unresolvedCount++;
                    continue;
                }

                state.ScratchEntriesByVehicle[entry.Vehicle] = entry;
            }

            bool requiresFullSort =
                state.Line != line
                || state.ChainSignature != chain.Signature
                || state.LastFullSortFrame == 0
                || nowFrame - state.LastFullSortFrame >= LINE_ORDERED_RUNTIME_FORCE_FULL_SORT_INTERVAL_FRAMES;
            string refreshReason = state.LastFullSortFrame == 0 ? "initial" : string.Empty;

            if (m_LineOrderedRuntimeForceRefreshReasons.TryGetValue(line, out string forcedReason))
            {
                requiresFullSort = true;
                refreshReason = forcedReason;
                m_LineOrderedRuntimeForceRefreshReasons.Remove(line);
            }

            if (!requiresFullSort)
            {
                for (int entryIndex = state.Entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    OrderedLineVehicleEntry previousEntry = state.Entries[entryIndex];
                    if (!state.ScratchEntriesByVehicle.TryGetValue(previousEntry.Vehicle, out OrderedLineVehicleEntry currentEntry))
                    {
                        state.Entries.RemoveAt(entryIndex);
                        continue;
                    }

                    state.Entries[entryIndex] = currentEntry;
                    state.ScratchEntriesByVehicle.Remove(previousEntry.Vehicle);
                    if (currentEntry.TraversalPhaseIndex != previousEntry.TraversalPhaseIndex)
                    {
                        requiresFullSort = true;
                        refreshReason = "phase-shift";
                        break;
                    }
                }

                if (!requiresFullSort && state.ScratchEntriesByVehicle.Count > 0)
                {
                    requiresFullSort = true;
                    refreshReason = "new-running-vehicle";
                }

                if (!requiresFullSort)
                {
                    for (int entryIndex = 1; entryIndex < state.Entries.Count; entryIndex++)
                    {
                        if (CompareOrderedLineVehicleEntry(state.Entries[entryIndex - 1], state.Entries[entryIndex]) > 0)
                        {
                            requiresFullSort = true;
                            refreshReason = "order-inversion";
                            break;
                        }
                    }
                }
            }

            if (requiresFullSort)
            {
                state.Entries.Clear();
                for (int i = 0; i < snapshot.Vehicles.Count; i++)
                {
                    if (TryBuildOrderedLineVehicleEntry(chain, snapshot.Vehicles[i], out OrderedLineVehicleEntry entry))
                        state.Entries.Add(entry);
                }

                state.Entries.Sort(CompareOrderedLineVehicleEntry);
                state.LastFullSortFrame = nowFrame;
                if (IsLineOrderedRuntimeLoggingEnabled())
                {
                    string refreshSummary = "reason=" + (string.IsNullOrWhiteSpace(refreshReason) ? "periodic" : refreshReason)
                        + "|entries=" + state.Entries.Count
                        + "|phases=" + math.max(1, chain.TurnbackBoundaries.Count + 1)
                        + "|unresolved=" + unresolvedCount;
                    if (!m_LineOrderedRuntimeLogCache.TryGetValue(line, out string previousRefreshSummary)
                        || previousRefreshSummary != refreshSummary)
                    {
                        m_LineOrderedRuntimeLogCache[line] = refreshSummary;
                        m_Runtime.Log.Info("[LineOrderedRefresh] line=" + line.Index
                            + " reason=" + (string.IsNullOrWhiteSpace(refreshReason) ? "periodic" : refreshReason)
                            + " entries=" + state.Entries.Count
                            + " phases=" + math.max(1, chain.TurnbackBoundaries.Count + 1)
                            + " unresolved=" + unresolvedCount);
                    }
                }
            }

            RebuildOrderedLinePhaseRanges(state);
            state.Line = line;
            state.ChainSignature = chain.Signature;
            state.LastRefreshFrame = nowFrame;
        }

        private static bool TryBuildOrderedLineVehicleEntry(
            LineTrackChain chain,
            LineRunningVehicleSnapshot runningVehicle,
            out OrderedLineVehicleEntry entry)
        {
            entry = default;
            if (chain == null
                || !runningVehicle.HasTrackCursor
                || runningVehicle.Vehicle == Entity.Null
                || runningVehicle.TraversalPhaseIndex < 0
                || runningVehicle.TraversalPhaseEndAtomExclusive <= runningVehicle.TraversalPhaseStartAtomIndex)
            {
                return false;
            }

            entry = new OrderedLineVehicleEntry(
                runningVehicle.Vehicle,
                runningVehicle,
                runningVehicle.OwnLineAtomCoordinate,
                runningVehicle.TraversalPhaseIndex,
                runningVehicle.TraversalPhaseStartAtomIndex,
                runningVehicle.TraversalPhaseEndAtomExclusive);
            return true;
        }

        private static void RebuildOrderedLinePhaseRanges(LineOrderedRuntimeState state)
        {
            state.PhaseRanges.Clear();
            if (state == null || state.Entries.Count == 0)
                return;

            int currentPhaseIndex = state.Entries[0].TraversalPhaseIndex;
            int currentPhaseStartAtomIndex = state.Entries[0].TraversalPhaseStartAtomIndex;
            int currentPhaseEndAtomExclusive = state.Entries[0].TraversalPhaseEndAtomExclusive;
            int phaseStartEntryIndex = 0;
            for (int entryIndex = 1; entryIndex < state.Entries.Count; entryIndex++)
            {
                OrderedLineVehicleEntry entry = state.Entries[entryIndex];
                if (entry.TraversalPhaseIndex == currentPhaseIndex)
                    continue;

                state.PhaseRanges.Add(new OrderedLinePhaseRange(
                    currentPhaseIndex,
                    currentPhaseStartAtomIndex,
                    currentPhaseEndAtomExclusive,
                    phaseStartEntryIndex,
                    entryIndex));
                currentPhaseIndex = entry.TraversalPhaseIndex;
                currentPhaseStartAtomIndex = entry.TraversalPhaseStartAtomIndex;
                currentPhaseEndAtomExclusive = entry.TraversalPhaseEndAtomExclusive;
                phaseStartEntryIndex = entryIndex;
            }

            state.PhaseRanges.Add(new OrderedLinePhaseRange(
                currentPhaseIndex,
                currentPhaseStartAtomIndex,
                currentPhaseEndAtomExclusive,
                phaseStartEntryIndex,
                state.Entries.Count));
        }

        private static int CompareOrderedLineVehicleEntry(
            OrderedLineVehicleEntry left,
            OrderedLineVehicleEntry right)
        {
            if (left.TraversalPhaseIndex != right.TraversalPhaseIndex)
                return left.TraversalPhaseIndex.CompareTo(right.TraversalPhaseIndex);
            if (left.OwnLineAtomCoordinate != right.OwnLineAtomCoordinate)
                return left.OwnLineAtomCoordinate.CompareTo(right.OwnLineAtomCoordinate);
            return left.Vehicle.Index.CompareTo(right.Vehicle.Index);
        }

        private static bool TryResolveTraversalOrderingPhase(
            LineTrackChain chain,
            int atomCursorIndex,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive,
            out int nextTurnbackBoundaryAtomIndex)
        {
            traversalPhaseIndex = -1;
            traversalPhaseStartAtomIndex = -1;
            traversalPhaseEndAtomExclusive = -1;
            nextTurnbackBoundaryAtomIndex = -1;
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            int atomIndex = math.clamp(atomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            int phaseStartAtomIndex = 0;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                int boundaryAtomIndex = math.clamp(chain.TurnbackBoundaries[boundaryIndex].AtomIndex, 0, chain.TrackAtoms.Count);
                if (atomIndex < boundaryAtomIndex)
                {
                    traversalPhaseIndex = boundaryIndex;
                    traversalPhaseStartAtomIndex = phaseStartAtomIndex;
                    traversalPhaseEndAtomExclusive = boundaryAtomIndex;
                    nextTurnbackBoundaryAtomIndex = boundaryAtomIndex;
                    return true;
                }

                phaseStartAtomIndex = boundaryAtomIndex;
            }

            traversalPhaseIndex = chain.TurnbackBoundaries.Count;
            traversalPhaseStartAtomIndex = phaseStartAtomIndex;
            traversalPhaseEndAtomExclusive = chain.TrackAtoms.Count;
            return true;
        }

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
                || !m_Runtime.TrackModel.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out int localStationExitAtomIndex))
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
            bool hasAnchor = m_Runtime.TrackModel.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
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

                    int overlapCount = m_Runtime.TrackModel.CountIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                    if (overlapCount <= 0)
                        continue;

                    int orderedRun = m_Runtime.TrackModel.ComputeIntervalOrderedRun(localChain, localWindow, expressChain, expressWindow);
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


        internal PhysicalSharedWindowMatch GetPhysicalSharedWindowMatchCurrentFrame(
            LineTrackChain localChain,
            BypassProtectedInterval localProtectedInterval,
            Entity currentBypassBuilding,
            LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return default;

            m_Runtime.TrackModel.EnsureSharedTrackIndexCurrent();
            m_Runtime.TrackModel.RefreshSharedRuns(localChain);
            m_Runtime.TrackModel.RefreshSharedRuns(expressChain);

            var key = new SharedWindowMatchCacheKey(
                localChain.LineEntity,
                expressChain.LineEntity,
                currentBypassBuilding,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive);
            if (m_SharedWindowMatchSnapshots.TryGetValue(key, out SharedWindowMatchSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_Runtime.TrackModel.SharedIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot.Match;
            }

            PhysicalSharedWindowMatch match = FindBestPhysicalSharedWindow(localChain, localProtectedInterval, currentBypassBuilding, expressChain);
            m_SharedWindowMatchSnapshots[key] = new SharedWindowMatchSnapshot(
                m_Runtime.TrackModel.SharedIndexVersion,
                localChain.Signature,
                expressChain.Signature,
                match);
            return match;
        }


        internal List<Entity> GetCandidateExpressLinesForLocalScene(
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
                || snapshot.SharedTrackVersion != m_Runtime.TrackModel.SharedIndexVersion
                || snapshot.LocalChainSignature != localChain.Signature)
            {
                snapshot = new LocalSceneCandidateExpressLinesSnapshot
                {
                    SharedTrackVersion = m_Runtime.TrackModel.SharedIndexVersion,
                    LocalChainSignature = localChain.Signature
                };

                var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
                foreach (KeyValuePair<string, AppliedLine> entry in m_Runtime.AppliedLines)
                {
                    Entity expressLine = entry.Value.LineEntity;
                    if (expressLine == Entity.Null
                        || expressLine == localChain.LineEntity
                        || !m_Runtime.EntityManager.Exists(expressLine)
                        || !m_Runtime.EntityManager.HasComponent<TransportLine>(expressLine)
                        || !Express(expressLine)
                        || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                    {
                        continue;
                    }

                    if (!m_Runtime.TrackModel.TryGetChainForLine(expressLine, expressWaypoints, out LineTrackChain expressChain))
                        continue;

                    m_Runtime.TrackModel.EnsureBypassPipelineReady(expressChain);
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
                || !m_Runtime.TrackModel.TryGetChainForLine(expressLine, expressWaypoints, out expressChain))
            {
                return false;
            }

            m_Runtime.TrackModel.EnsureBypassPipelineReady(expressChain);
            var key = new LocalSceneExpressStaticMatchCacheKey(
                localChain.LineEntity,
                expressLine,
                currentBypassBuilding,
                localProtectedIntervalIndex);
            if (m_LocalSceneExpressStaticMatchSnapshots.TryGetValue(key, out snapshot)
                && snapshot.SharedTrackVersion == m_Runtime.TrackModel.SharedIndexVersion
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
                    m_Runtime.TrackModel.SharedIndexVersion,
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
                    m_Runtime.TrackModel.SharedIndexVersion,
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
                m_Runtime.TrackModel.SharedIndexVersion,
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


        internal bool TryBuildSceneExpressRelation(
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

            if (!m_Runtime.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
                return false;

            int currentControlEdgeIndex = TrackProjectionService.ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
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
                TrackModelRelativeToProtectedInterval relative = TrackProjectionService.ResolveRelativeToProtectedInterval(currentControlEdgeIndex, currentAtomIndex, candidate);
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
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeResolveCalls++;
            resolutionSource = "direct";
            if (TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out protectedIntervalIndex, out protectedInterval))
                return true;

            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null
                || localChain == null
                || waypoints.Length == 0
                || !m_Runtime.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int currentControlEdgeIndex = TrackProjectionService.ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
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

        internal bool TryResolveExpressConflictWindowForLocalConflict(
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

            if (sharedWindowMatch.Found && !sharedWindowMatch.Ambiguous)
            {
                protectedInterval = sharedWindowMatch.ExpressSharedWindow;
                protectedIntervalIndex = FindProtectedIntervalIndex(chain, protectedInterval);
                overlapCount = sharedWindowMatch.OverlapCount;
                orderedRun = sharedWindowMatch.OrderedRun;
                return true;
            }

            return false;
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
            SceneExpressVehicleCandidate candidate,
            float localDepartureReleaseCoordinate,
            float localIntervalDisplayLength)
        {
            float localLength = math.max(1f, localIntervalDisplayLength);
            float expressLength = TrackProjectionService.GetProtectedIntervalDisplayLength(candidate.ExpressProtectedInterval);
            float releaseProgress = math.clamp(localDepartureReleaseCoordinate / localLength, 0f, 1f);
            float expressReleaseCoordinate = releaseProgress * expressLength;
            return new BypassLatchedBlockerProjection(
                candidate.ExpressLine,
                candidate.ExpressProtectedInterval,
                candidate.SelectedTrunkSegment,
                candidate.ExpressChain != null ? candidate.ExpressChain.Signature : 0UL,
                m_Runtime.TrackModel.SharedIndexVersion,
                expressReleaseCoordinate);
        }

        private void RecordSceneExpressLineQueryProbe(Entity expressLine, uint nowFrame)
        {
            if (!IsBypassPerfProbeLoggingEnabled() || expressLine == Entity.Null)
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

        private bool TryBuildOrderedThreatHeadCandidatesFast(
            LineOrderedRuntimeState orderedState,
            SceneExpressRelation relation,
            out bool hasPrimaryThreat,
            out OrderedLineVehicleEntry primaryThreat,
            out OrderedSceneQueryWindow primaryThreatWindow,
            out bool hasSecondaryThreat,
            out OrderedLineVehicleEntry secondaryThreat,
            out OrderedSceneQueryWindow secondaryThreatWindow)
        {
            hasPrimaryThreat = false;
            primaryThreat = default;
            primaryThreatWindow = default;
            hasSecondaryThreat = false;
            secondaryThreat = default;
            secondaryThreatWindow = default;
            int primaryThreatDirectionRank = 0;
            int secondaryThreatDirectionRank = 0;

            if (orderedState == null
                || orderedState.Entries.Count == 0
                || relation.TrunkCandidates == null
                || relation.TrunkCandidates.Segments.Count != 1)
            {
                return false;
            }

            GlobalSharedTrunkSegment segment = relation.TrunkCandidates.Segments[0];
            int candidateStartAtomIndex = math.max(segment.ExpressCorridorStartAtomIndex, relation.ExpressProtectedInterval.StartAtomIndex);
            int candidateEndAtomExclusive = math.min(segment.ExpressCorridorEndAtomIndexExclusive, relation.ExpressProtectedInterval.EndAtomIndexExclusive);
            if (candidateEndAtomExclusive <= candidateStartAtomIndex)
                return false;

            bool hasRelevantEntry = relation.HasRelevantSharedEntryAtomIndex
                && relation.RelevantSharedEntryAtomIndex >= candidateStartAtomIndex
                && relation.RelevantSharedEntryAtomIndex < candidateEndAtomExclusive;
            float anchorAtomIndex = hasRelevantEntry
                ? relation.RelevantSharedEntryAtomIndex
                : candidateStartAtomIndex;

            for (int phaseRangeIndex = 0; phaseRangeIndex < orderedState.PhaseRanges.Count; phaseRangeIndex++)
            {
                OrderedLinePhaseRange phaseRange = orderedState.PhaseRanges[phaseRangeIndex];
                int overlapStartAtomIndex = math.max(candidateStartAtomIndex, phaseRange.StartAtomIndex);
                int overlapEndAtomExclusive = math.min(candidateEndAtomExclusive, phaseRange.EndAtomIndexExclusive);
                if (overlapEndAtomExclusive <= overlapStartAtomIndex)
                    continue;

                var queryWindow = new OrderedSceneQueryWindow(
                    phaseRange.TraversalPhaseIndex,
                    overlapStartAtomIndex,
                    overlapEndAtomExclusive);

                if (hasRelevantEntry)
                {
                    int firstAfterAnchor = FindFirstOrderedEntryAtOrAfter(
                        orderedState.Entries,
                        phaseRange.StartEntryIndex,
                        phaseRange.EndEntryIndexExclusive,
                        anchorAtomIndex + 0.001f);
                    ConsiderOrderedThreatHeadCandidateFast(
                        orderedState,
                        relation,
                        queryWindow,
                        firstAfterAnchor - 1,
                        ref hasPrimaryThreat,
                        ref primaryThreat,
                        ref primaryThreatWindow,
                        ref primaryThreatDirectionRank,
                        ref hasSecondaryThreat,
                        ref secondaryThreat,
                        ref secondaryThreatWindow,
                        ref secondaryThreatDirectionRank);
                    ConsiderOrderedThreatHeadCandidateFast(
                        orderedState,
                        relation,
                        queryWindow,
                        firstAfterAnchor - 2,
                        ref hasPrimaryThreat,
                        ref primaryThreat,
                        ref primaryThreatWindow,
                        ref primaryThreatDirectionRank,
                        ref hasSecondaryThreat,
                        ref secondaryThreat,
                        ref secondaryThreatWindow,
                        ref secondaryThreatDirectionRank);
                    continue;
                }

                int firstInside = FindFirstOrderedEntryAtOrAfter(
                    orderedState.Entries,
                    phaseRange.StartEntryIndex,
                    phaseRange.EndEntryIndexExclusive,
                    overlapStartAtomIndex);
                ConsiderOrderedThreatHeadCandidateFast(
                    orderedState,
                    relation,
                    queryWindow,
                    firstInside,
                    ref hasPrimaryThreat,
                    ref primaryThreat,
                    ref primaryThreatWindow,
                    ref primaryThreatDirectionRank,
                    ref hasSecondaryThreat,
                    ref secondaryThreat,
                    ref secondaryThreatWindow,
                    ref secondaryThreatDirectionRank);
                ConsiderOrderedThreatHeadCandidateFast(
                    orderedState,
                    relation,
                    queryWindow,
                    firstInside + 1,
                    ref hasPrimaryThreat,
                    ref primaryThreat,
                    ref primaryThreatWindow,
                    ref primaryThreatDirectionRank,
                    ref hasSecondaryThreat,
                    ref secondaryThreat,
                    ref secondaryThreatWindow,
                    ref secondaryThreatDirectionRank);
                ConsiderOrderedThreatHeadCandidateFast(
                    orderedState,
                    relation,
                    queryWindow,
                    firstInside - 1,
                    ref hasPrimaryThreat,
                    ref primaryThreat,
                    ref primaryThreatWindow,
                    ref primaryThreatDirectionRank,
                    ref hasSecondaryThreat,
                    ref secondaryThreat,
                    ref secondaryThreatWindow,
                    ref secondaryThreatDirectionRank);
            }

            return hasPrimaryThreat || hasSecondaryThreat;
        }

        private bool ConsiderOrderedThreatHeadCandidateFast(
            LineOrderedRuntimeState orderedState,
            SceneExpressRelation relation,
            OrderedSceneQueryWindow queryWindow,
            int entryIndex,
            ref bool hasPrimaryThreat,
            ref OrderedLineVehicleEntry primaryThreat,
            ref OrderedSceneQueryWindow primaryThreatWindow,
            ref int primaryThreatDirectionRank,
            ref bool hasSecondaryThreat,
            ref OrderedLineVehicleEntry secondaryThreat,
            ref OrderedSceneQueryWindow secondaryThreatWindow,
            ref int secondaryThreatDirectionRank)
        {
            if (orderedState == null
                || entryIndex < 0
                || entryIndex >= orderedState.Entries.Count)
            {
                return false;
            }

            OrderedLineVehicleEntry orderedEntry = orderedState.Entries[entryIndex];
            if (orderedEntry.TraversalPhaseIndex != queryWindow.TraversalPhaseIndex
                || !IsOrderedEntryEligibleForThreatWindow(orderedEntry, queryWindow))
            {
                return false;
            }

            if (relation.HasRelevantSharedEntryAtomIndex
                && orderedEntry.RunningVehicle.HasTrackCursor
                && orderedEntry.RunningVehicle.TrackCursor.AtomCursorIndex > relation.RelevantSharedEntryAtomIndex)
            {
                return false;
            }

            if (!TryGetOrderedThreatDirectionRank(
                    relation,
                    queryWindow,
                    orderedEntry,
                    out int directionRank))
            {
                return false;
            }

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
                return true;
            }

            if ((!hasSecondaryThreat
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
                return true;
            }

            return false;
        }

        private static int FindFirstOrderedEntryAtOrAfter(
            List<OrderedLineVehicleEntry> entries,
            int startIndex,
            int endIndexExclusive,
            float atomCoordinate)
        {
            int lo = math.max(0, startIndex);
            int hi = math.min(entries.Count, endIndexExclusive);
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (entries[mid].OwnLineAtomCoordinate < atomCoordinate)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
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
            bool collectSameStationCandidates,
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

            if (!collectSameStationCandidates
                && TryBuildOrderedThreatHeadCandidatesFast(
                    orderedState,
                    relation,
                    out hasPrimaryThreat,
                    out primaryThreat,
                    out primaryThreatWindow,
                    out hasSecondaryThreat,
                    out secondaryThreat,
                    out secondaryThreatWindow))
            {
                return true;
            }

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
            if (IsBypassPerfProbeLoggingEnabled())
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
                || !m_Runtime.EntityManager.Exists(expressVehicle)
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
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeSceneSamples++;
            if (!m_SceneIndex.TryGetEntry(localChain, currentBypassBuilding, protectedIntervalIndex, out SceneStaticIndexEntry staticEntry)
                || staticEntry.ExpressRelations.Count == 0)
                return true;

            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            for (int candidateIndex = 0; candidateIndex < staticEntry.ExpressRelations.Count; candidateIndex++)
            {
                SceneExpressRelation relation = staticEntry.ExpressRelations[candidateIndex];
                Entity expressLine = relation.ExpressLine;
                if (expressLine == Entity.Null
                    || expressLine == localLine
                    || !m_Runtime.EntityManager.Exists(expressLine)
                    || !m_Runtime.EntityManager.HasComponent<TransportLine>(expressLine)
                    || !Express(expressLine)
                    || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
                {
                    continue;
                }

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
                if (IsLineOrderedRuntimeProbeLoggingEnabled())
                    m_LineOrderedProbeExpressLineQueries++;
                SceneExpressFrontierAccumulator frontier = new SceneExpressFrontierAccumulator(relation);
                LineOrderedRuntimeState orderedState = null;
                BypassExecutionMode executionMode = staticEntry.ExecutionMode;
                bool useOrderedRuntime = executionMode == BypassExecutionMode.ComplexLineModel
                    && TryGetLineOrderedRuntimeState(expressLine, expressWaypoints, nowFrame, out orderedState);
                if (useOrderedRuntime)
                {
                    if (IsLineOrderedRuntimeProbeLoggingEnabled())
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
                            sameStationCandidates != null,
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
                            if (IsBypassPerfProbeLoggingEnabled())
                                m_BypassPerfProbeSceneCandidateVehicles++;
                            if (IsLineOrderedRuntimeProbeLoggingEnabled())
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
                            if (IsBypassPerfProbeLoggingEnabled())
                                m_BypassPerfProbeSceneCandidateVehicles++;
                            if (IsLineOrderedRuntimeProbeLoggingEnabled())
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
                            if (IsBypassPerfProbeLoggingEnabled())
                                m_BypassPerfProbeSceneCandidateVehicles++;
                            if (IsLineOrderedRuntimeProbeLoggingEnabled())
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
                        if (IsLineOrderedRuntimeProbeLoggingEnabled())
                            m_LineOrderedProbeHeadOnlySuccesses++;
                        frontier = threatHeadFrontier;
                        if (sameStationCandidates != null && threatHeadSameStationCandidates != null)
                            sameStationCandidates.AddRange(threatHeadSameStationCandidates);
                        if (frontier.HasPrimaryCandidate)
                            frontiers.Add(BuildSceneExpressFrontier(frontier));
                        continue;
                    }

                    if (IsLineOrderedRuntimeProbeLoggingEnabled())
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
                    if (IsBypassPerfProbeLoggingEnabled())
                        m_BypassPerfProbeSceneCandidateVehicles++;
                    if (useOrderedRuntime)
                    {
                        if (IsLineOrderedRuntimeProbeLoggingEnabled())
                            m_LineOrderedProbeFallbackCandidateBuilds++;
                    }
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
            if (IsBypassPerfProbeLoggingEnabled())
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

            return TryGetAtomStationBuilding(chain, cursor.AtomCursorIndex, out Entity atomBuilding)
                && atomBuilding == bypassBuilding;
        }

        private static bool TryGetAtomStationBuilding(LineTrackChain chain, int atomIndex, out Entity building)
        {
            building = Entity.Null;
            if (chain == null
                || chain.AtomStationBuildings == null
                || atomIndex < 0
                || atomIndex >= chain.AtomStationBuildings.Length)
            {
                return false;
            }

            building = chain.AtomStationBuildings[atomIndex];
            return building != Entity.Null;
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

            return TryGetAtomStationBuilding(chain, runtimePosition.CurrentAtomIndex, out Entity atomBuilding)
                && atomBuilding == bypassBuilding;
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

            if (IsBypassPerfProbeLoggingEnabled())
            {
                m_BypassPerfProbeSameStationCalls++;
                m_BypassPerfProbeSameStationReusedCandidates += (ulong)orderedCandidates.Count;
            }

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
            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            for (int candidateIndex = 0; candidateIndex < orderedCandidates.Count; candidateIndex++)
            {
                SceneExpressVehicleCandidate candidate = orderedCandidates[candidateIndex];
                Entity expressVehicle = candidate.ExpressVehicle;
                LineRunningVehicleSnapshot runningVehicle = candidate.RunningVehicle;
                bool cursorWithinSameStationPresence = runningVehicle.HasTrackCursor
                    && IsTrackCursorWithinBypassStationPhysicalContext(candidate.ExpressChain, runningVehicle.TrackCursor, currentBypassBuilding);
                bool expressWithinSameStationPresence = runningVehicle.HasTrackCursor
                    && cursorWithinSameStationPresence;
                bool expressCurrentWaypointMatchesBypassBuilding = false;
                if (!expressWithinSameStationPresence
                    && runningVehicle.Boarding)
                {
                    expressCurrentWaypointMatchesBypassBuilding = TryExpressCurrentWaypointMatchesBypassBuilding(
                        expressVehicle,
                        candidate.ExpressLine,
                        routeWaypointBuffers,
                        currentBypassBuilding);
                }

                if (!expressWithinSameStationPresence
                    && runningVehicle.Boarding
                    && expressCurrentWaypointMatchesBypassBuilding)
                {
                    expressWithinSameStationPresence = true;
                }

                if (expressVehicle == Entity.Null
                    || expressVehicle == localVehicle
                    || !m_Runtime.EntityManager.Exists(expressVehicle)
                    || !expressWithinSameStationPresence)
                {
                    if (firstMissCandidate == Entity.Null && expressVehicle != Entity.Null && expressVehicle != localVehicle)
                    {
                        firstMissCandidate = expressVehicle;
                        firstMissReason = "presence-miss cursor=" + (cursorWithinSameStationPresence ? "1" : "0")
                            + " boarding=" + (runningVehicle.Boarding ? "1" : "0")
                            + " wpMatch=" + (expressCurrentWaypointMatchesBypassBuilding ? "1" : "0");
                    }
                    continue;
                }

                if (!TrackProjectionService.TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
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

                float expressCoordinate = TrackProjectionService.MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
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

        private bool TryExpressCurrentWaypointMatchesBypassBuilding(
            Entity expressVehicle,
            Entity expressLine,
            BufferLookup<RouteWaypoint> routeWaypointBuffers,
            Entity currentBypassBuilding)
        {
            if (expressVehicle == Entity.Null
                || expressLine == Entity.Null
                || currentBypassBuilding == Entity.Null
                || !routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints))
            {
                return false;
            }

            int expressWaypointIndex = m_Runtime.ComputeWaypointIndex(expressVehicle, expressWaypoints);
            return expressWaypointIndex >= 0
                && expressWaypointIndex < expressWaypoints.Length
                && m_Runtime.GetStationBuildingForWaypoint(expressWaypoints, expressWaypointIndex) == currentBypassBuilding;
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
            expressPositionText = TrackProjectionService.FormatRuntimePosition(expressPosition);
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

            if (IsBypassPerfProbeLoggingEnabled())
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
            if (m_Runtime.TryEstimateRemainingBoardingTime(
                    localVehicle,
                    localLine,
                    currentWaypointIndex,
                    m_Runtime.Frame,
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
            float safetyGapFrames = m_Runtime.ClockSnapshot.ToFramesCeil(
                TRACKMODEL_ENTRY_CLEAR_SAFETY_GAP_MINUTES);
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
                if (!m_Runtime.TryGetEffectiveTraversalRunSliceFrames(chain.LineEntity, slice, out float effectiveRunFrames)
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

            float corridorStartCoordinate = TrackProjectionService.MapAtomIndexToProtectedIntervalCoordinateExact(localProtectedInterval, localCorridor.StartAtomIndex);
            float corridorEndCoordinate = TrackProjectionService.MapAtomIndexToProtectedIntervalCoordinateExact(localProtectedInterval, localCorridor.EndAtomIndexExclusive);
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
                expressPositionText = TrackProjectionService.FormatRuntimePosition(expressPosition)
                    + " release=" + departureReleaseCoordinate.ToString("0.00")
                    + " mapped=after";
                return true;
            }

            if (expressPosition.RelativeToProtectedInterval == TrackModelRelativeToProtectedInterval.Inside
                && expressCoordinate > departureReleaseCoordinate)
            {
                reason = "express-cleared-bypass-release-window";
                expressPositionText = TrackProjectionService.FormatRuntimePosition(expressPosition)
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

            bool hasAnchor = m_Runtime.TrackModel.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
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

                            int overlapCount = m_Runtime.TrackModel.CountIntervalPhysicalOverlap(localChain, localWindow, expressChain, expressWindow);
                            if (overlapCount <= 0)
                                continue;

                            int orderedRun = m_Runtime.TrackModel.ComputeIntervalOrderedRun(localChain, localWindow, expressChain, expressWindow);
                            if (orderedRun <= 0)
                                continue;

                            int pairLocalStartAtomIndex = localSlice.StartAtomIndex;
                            int pairLocalEndAtomIndexExclusive = localSlice.EndAtomIndexExclusive;
                            int pairExpressStartAtomIndex = expressSlice.StartAtomIndex;
                            int pairExpressEndAtomIndexExclusive = expressSlice.EndAtomIndexExclusive;
                            if (m_Runtime.TrackModel.TryFindOrderedRunSpan(
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

        internal bool TryResolveStaticTraversalPhaseWindow(
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
                SharedTrackVersion = m_Runtime.TrackModel.SharedIndexVersion,
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

        internal GlobalSharedTrunkSnapshot GetGlobalSharedTrunkSnapshotCurrent(LineTrackChain localChain, LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return null;

            m_Runtime.TrackModel.EnsureSharedTrackIndexCurrent();
            m_Runtime.TrackModel.RefreshSharedRuns(localChain);
            m_Runtime.TrackModel.RefreshSharedRuns(expressChain);

            var key = new GlobalSharedTrunkCacheKey(localChain.LineEntity, expressChain.LineEntity);
            if (m_Runtime.TrackModel.GlobalSharedTrunkSnapshots.TryGetValue(key, out GlobalSharedTrunkSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_Runtime.TrackModel.SharedIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature)
            {
                return snapshot;
            }

            snapshot = BuildGlobalSharedTrunkSnapshot(localChain, expressChain);
            m_Runtime.TrackModel.GlobalSharedTrunkSnapshots[key] = snapshot;
            return snapshot;
        }

        private ProtectedIntervalPairMetricsSnapshot BuildProtectedIntervalPairMetricsSnapshot(LineTrackChain localChain, LineTrackChain expressChain)
        {
            var snapshot = new ProtectedIntervalPairMetricsSnapshot
            {
                SharedTrackVersion = m_Runtime.TrackModel.SharedIndexVersion,
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
                    int overlapCount = m_Runtime.TrackModel.CountIntervalPhysicalOverlap(localChain, localInterval, expressChain, expressInterval);
                    int orderedRun = m_Runtime.TrackModel.ComputeIntervalOrderedRun(localChain, localInterval, expressChain, expressInterval);
                    snapshot.Metrics[(localIndex * snapshot.ExpressIntervalCount) + expressIndex] = new ProtectedIntervalPairMetrics(overlapCount, orderedRun);
                }
            }

            return snapshot;
        }

        private ProtectedIntervalPairMetricsSnapshot GetProtectedIntervalPairMetricsSnapshotCurrent(LineTrackChain localChain, LineTrackChain expressChain)
        {
            if (localChain == null || expressChain == null)
                return null;

            m_Runtime.TrackModel.EnsureSharedTrackIndexCurrent();
            m_Runtime.TrackModel.RefreshSharedRuns(localChain);
            m_Runtime.TrackModel.RefreshSharedRuns(expressChain);
            m_Runtime.TrackModel.EnsureBypassPipelineReady(localChain);
            m_Runtime.TrackModel.EnsureBypassPipelineReady(expressChain);

            var key = new ProtectedIntervalPairMetricsCacheKey(localChain.LineEntity, expressChain.LineEntity);
            if (m_Runtime.TrackModel.ProtectedIntervalPairMetricsSnapshots.TryGetValue(key, out ProtectedIntervalPairMetricsSnapshot snapshot)
                && snapshot.SharedTrackVersion == m_Runtime.TrackModel.SharedIndexVersion
                && snapshot.LocalChainSignature == localChain.Signature
                && snapshot.ExpressChainSignature == expressChain.Signature
                && snapshot.LocalIntervalCount == localChain.BypassProtectedIntervals.Count
                && snapshot.ExpressIntervalCount == expressChain.BypassProtectedIntervals.Count)
            {
                return snapshot;
            }

            snapshot = BuildProtectedIntervalPairMetricsSnapshot(localChain, expressChain);
            m_Runtime.TrackModel.ProtectedIntervalPairMetricsSnapshots[key] = snapshot;
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

        internal bool TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
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

            bool hasAnchor = m_Runtime.TrackModel.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
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

            bool hasAnchor = m_Runtime.TrackModel.TryGetStationExitAtom(localChain, localProtectedInterval, currentBypassBuilding, out int stationExitAtomIndex);
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

            int currentControlEdgeIndex = TrackProjectionService.ResolveControlEdgeIndexForAtom(expressChain, expressCurrentAtomIndex);
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

        internal RelativeToTrunkState ResolveVehicleTrunkTravelState(
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

        internal RelativeToTrunkState ResolveVehicleTrunkTravelState(
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

        internal static bool IsRelativeToTrunkStateBlockerEligible(RelativeToTrunkState state)
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

        internal static bool IsRelativeToTrunkStateDirectionCompatibleWithLocal(
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
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeActiveCorridorCalls++;
            localCorridor = default;
            expressCorridor = default;
            trunkSegment = default;
            if (localChain == null || expressChain == null)
                return false;

            uint nowFrame = m_Runtime.Frame;
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
                && snapshot.SharedTrackVersion == m_Runtime.TrackModel.SharedIndexVersion
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
                    m_Runtime.TrackModel.SharedIndexVersion,
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
                    m_Runtime.TrackModel.SharedIndexVersion,
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
                m_Runtime.TrackModel.SharedIndexVersion,
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
                float averageFramesPerAtom = TrackModelService.EstimateAverageControlEdgeFramesPerAtom(chain);
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

            if (!m_Runtime.TrackModel.TryGetStationExitCoordinate(localChain, localProtectedInterval, currentBypassBuilding, out float stationExitCoordinate))
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
                : TrackProjectionService.ResolveControlEdgeIndexForAtom(chain, runtimePosition.CurrentAtomIndex);
            int fromAtomIndex = math.clamp(runtimePosition.CurrentAtomIndex, 0, chain.TrackAtoms.Count - 1);
            int toAtomIndexExclusive = math.clamp(targetAtomIndexExclusive, fromAtomIndex + 1, chain.TrackAtoms.Count);
            float averageFramesPerAtom = TrackModelService.EstimateAverageControlEdgeFramesPerAtom(chain);
            if (currentControlEdgeIndex < 0 || currentControlEdgeIndex >= chain.ControlEdges.Count)
            {
                if (!(averageFramesPerAtom > 0f))
                    return float.MaxValue;

                float rawAtomDistance = (toAtomIndexExclusive - fromAtomIndex) - math.saturate(runtimePosition.AtomPosition01);
                return math.max(0f, rawAtomDistance * averageFramesPerAtom);
            }

            float frames = m_Runtime.TrackModel.EstimateFramesBetweenAtoms(chain, currentControlEdgeIndex, chain.ControlEdges.Count - 1, fromAtomIndex, toAtomIndexExclusive);
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

        internal string FormatEtaFrames(float frames)
        {
            if (frames == float.MaxValue)
                return "?";

            return m_Runtime.ClockSnapshot.ToMinutes(frames).ToString("0.0") + "m";
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

            if (!m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
                return false;

            m_Runtime.TrackModel.EnsureBypassPipelineReady(chain);

            if (!m_Runtime.TrackModel.TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out protectedInterval))
                return false;

            protectedSharedCount = m_Runtime.TrackModel.CountProtectedSharedIntervals(chain, protectedIntervalIndex, out hasMirroredContext);
            return true;
        }
        internal bool ShouldClearHoldAfterStationExit(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex)
        {
            if (TryGetBypassControlScope(localVehicle, localLine, localWaypoints, currentWaypointIndex, out BypassControlScope scope, out _))
                return ShouldClearHoldAfterStationExit(scope, localWaypoints);

            return true;
        }

        internal bool ShouldClearHoldAfterStationExit(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints)
        {
            Entity localVehicle = scope.Vehicle;
            Entity localLine = scope.Line;
            int currentWaypointIndex = scope.WaypointIndex;
            Entity currentBypassBuilding = scope.CurrentBypassBuilding;
            BypassProtectedInterval protectedInterval = scope.Scene.ProtectedInterval;
            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || currentBypassBuilding == Entity.Null)
            {
                return true;
            }

            if (!m_Runtime.TrackModel.TryGetChainForLine(localLine, localWaypoints, out LineTrackChain localChain))
                return false;

            if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition))
                return false;

            if (localPosition.Confidence < 0.6f)
                return false;

            if (!m_Runtime.TrackModel.TryGetStationExitCoordinate(localChain, protectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return false;

            float localCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinate(localPosition, protectedInterval, includeApproachers: true, out bool includeLocal);
            if (!includeLocal)
                return false;

            bool canClear = localCoordinate > stationExitCoordinate + LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS;
            if (RtLog.VerboseEnabled
                && canClear
                && m_Decision.TryGetLatchedBlocker(localVehicle, out Entity blocker))
            {
                string lineTag = localLine != Entity.Null ? " line=" + localLine.Index : " line=-";
                m_Runtime.LogVehicleStateOnce(
                    m_BypassExitClearLogCache,
                    localVehicle,
                    "exit-clear|" + localLine.Index + "|" + currentWaypointIndex + "|" + currentBypassBuilding.Index,
                    "[待避出口清除]" + lineTag
                        + " vehicle=" + localVehicle.Index
                        + " blocker=" + blocker.Index
                        + " wp=" + currentWaypointIndex
                        + " station=" + currentBypassBuilding.Index
                        + " coord=" + localCoordinate.ToString("0.0")
                        + " exit=" + stationExitCoordinate.ToString("0.0")
                        + " threshold=" + (stationExitCoordinate + LOCAL_BYPASS_TRAIN_TAIL_CLEAR_ATOMS).ToString("0.0")
                        + " confidence=" + localPosition.Confidence.ToString("0.00"));
            }

            return canClear;
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
                || !m_Runtime.TrackModel.TryGetChainForLine(localLine, localWaypoints, out LineTrackChain localChain))
            {
                return false;
            }

            int liveWaypointIndex = m_Runtime.ComputeWaypointIndex(localVehicle, localWaypoints);
            if (liveWaypointIndex == currentWaypointIndex)
                return true;

            if (!m_Runtime.TryGetBypassWaypointContext(localWaypoints, currentWaypointIndex, out Entity currentBypassBuilding, out _, out _)
                || currentBypassBuilding == Entity.Null)
            {
                return false;
            }

            if (!m_Runtime.TrackModel.TryResolveBypassProtectedInterval(localChain, localWaypoints, currentWaypointIndex, out _, out BypassProtectedInterval protectedInterval))
                return false;

            if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out TrackModelRuntimePosition localPosition)
                || localPosition.Confidence < 0.6f)
            {
                return false;
            }

            if (!m_Runtime.TrackModel.TryGetStationExitCoordinate(localChain, protectedInterval, currentBypassBuilding, out float stationExitCoordinate))
                return false;

            float localCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinate(localPosition, protectedInterval, includeApproachers: true, out bool includeLocal);
            if (!includeLocal)
                return false;

            return localCoordinate <= stationExitCoordinate;
        }

        internal bool IsVehicleWithinBypassStationPhysicalContext(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity bypassBuilding)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || bypassBuilding == Entity.Null
                || !m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || !TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out _, out BypassProtectedInterval protectedInterval)
                || !m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(vehicle, line, waypoints, protectedInterval, out TrackModelRuntimePosition runtimePosition)
                || runtimePosition.Confidence < 0.6f
                || runtimePosition.CurrentAtomIndex < protectedInterval.StartAtomIndex
                || runtimePosition.CurrentAtomIndex >= protectedInterval.EndAtomIndexExclusive
                || runtimePosition.CurrentAtomIndex < 0
                || runtimePosition.CurrentAtomIndex >= chain.TrackAtoms.Count)
            {
                return false;
            }

            return TryGetAtomStationBuilding(chain, runtimePosition.CurrentAtomIndex, out Entity atomBuilding)
                && atomBuilding == bypassBuilding;
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
            if (!m_Runtime.TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain))
                return false;

            m_Runtime.TrackModel.EnsureBypassPipelineReady(chain);

            if (!m_Runtime.TrackModel.TryResolveBypassProtectedInterval(chain, waypoints, currentWaypointIndex, out protectedIntervalIndex, out BypassProtectedInterval protectedInterval))
                return false;

            if (protectedIntervalIndex < 0 || protectedIntervalIndex >= chain.ProtectedIntervalSummaries.Count)
                return false;

            ProtectedIntervalSummary intervalSummary = chain.ProtectedIntervalSummaries[protectedIntervalIndex];
            risk = TrackModelService.ClassifyProtectedIntervalTrackModelRisk(intervalSummary);
            summary = TrackModelService.FormatProtectedIntervalSummary(intervalSummary, protectedInterval);
            return true;
        }

        internal bool TryEvaluateBypassTrackModelDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out BypassTrackModelDecision trackModelDecision)
        {
            if (IsBypassPerfProbeLoggingEnabled())
                m_BypassPerfProbeTrackDecisionCalls++;
            trackModelDecision = default;
            if (!m_Runtime.TrackModel.TryGetLocalSceneSnapshot(
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
            bool hasLocalPosition = m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(localVehicle, localLine, localWaypoints, protectedInterval, out localPosition);
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
            float localCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinateExact(localPosition, protectedInterval, includeApproachers: true, out bool includeLocalCoordinate);
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

                    if (!TrackProjectionService.TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(runningVehicle, expressProtectedInterval, out TrackModelRuntimePosition expressPosition))
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

                    float expressCoordinate = TrackProjectionService.MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
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
                            bestLatchedBlockerProjection = BuildLatchedBlockerProjection(candidate, departureReleaseCoordinate, intervalDisplayLength);
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
                        ? BuildLatchedBlockerProjection(sameStationCandidate, departureReleaseCoordinate, intervalDisplayLength)
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


        private void LogSharedWindowFinalReject(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval,
            Entity expressVehicle,
            string rejectReason)
        {
            if (localVehicle == Entity.Null || expressVehicle == Entity.Null || string.IsNullOrWhiteSpace(rejectReason))
                return;

            m_SharedWindowAuditPairStateCache[new SharedWindowPairStateKey(localVehicle, protectedIntervalIndex, expressVehicle)] =
                "finalReject|" + rejectReason;
        }

        private void TryLogTrainLaneSourceDisagreement(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            string stage)
        {
        }

        private void LogBypassSelectedBlockerDetailOnce(
            Entity localVehicle,
            Entity localLine,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            Entity expressVehicle,
            Entity expressLine,
            int expressProtectedIntervalIndex,
            string intervalResolutionSource,
            int overlapCount,
            int orderedRun,
            int expressCurrentAtomIndex,
            int expressPhaseEndAtomExclusive,
            string expressPositionText)
        {
        }

        private void LogSameStationMissDiagnosticOnce(
            Entity localVehicle,
            Entity localLine,
            int localProtectedIntervalIndex,
            Entity currentBypassBuilding,
            Entity expressVehicle,
            int candidateCount,
            string reason)
        {
        }

        private void TryLogSharedWindowAuditForNoExpress(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            int protectedIntervalIndex,
            BypassProtectedInterval protectedInterval)
        {
        }


    }
}
