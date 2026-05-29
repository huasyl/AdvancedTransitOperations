using Game.Routes;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal interface IBypassDecisionRuntime
    {
        bool FeatureEnabled();

        bool TryScope(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            out BypassControlScope scope,
            out string failureReason);

        bool IsLocalLine(Entity line);

        bool Exists(Entity entity);

        bool CanClear(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);

        bool BlockerAtStation(Entity blocker, Entity station);

        bool LatchedBeforeRelease(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, BypassConflictEpisode episode, Entity blocker, out bool beforeRelease);

        bool ReleaseForQueuedLocal(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker);

        bool Baseline(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out bool shouldYield,
            out string reason,
            out Entity blocker,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection);

        bool Finalize(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, bool shouldYield, string reason, Entity blocker);

        Entity ResolveLine(Entity vehicle);

        uint HeldReevaluateFrames();

        uint EpisodeRecheckFrames();

        uint UnlatchedReevaluateFrames();

        void CountCadenceCall();

        void CountCadenceMiss();

        void CountEpisodeReuse();

        bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker);
    }

    internal enum BypassEntryKind : byte
    {
        Scope = 1,
        Cadence = 2,
        Episode = 3,
    }

    internal readonly struct BypassDecisionResult
    {
        public readonly bool Evaluated;
        public readonly bool HadLatchedYield;
        public readonly bool HasLatchedYield;
        public readonly Entity LatchedBlocker;
        public readonly bool ShouldHold;
        public readonly Entity Blocker;
        public readonly bool CanClearAfterExit;
        public readonly string ReleaseReason;

        public BypassDecisionResult(
            bool evaluated,
            bool hadLatchedYield,
            bool hasLatchedYield,
            Entity latchedBlocker,
            bool shouldHold,
            Entity blocker,
            bool canClearAfterExit,
            string releaseReason = null)
        {
            Evaluated = evaluated;
            HadLatchedYield = hadLatchedYield;
            HasLatchedYield = hasLatchedYield;
            LatchedBlocker = latchedBlocker;
            ShouldHold = shouldHold;
            Blocker = blocker;
            CanClearAfterExit = canClearAfterExit;
            ReleaseReason = releaseReason;
        }
    }

    internal sealed class BypassDecision : IDisposable
    {
        private readonly IBypassDecisionRuntime m_Runtime;
        private NativeHashMap<Entity, Entity> m_Blockers;
        private readonly Dictionary<Entity, BypassControlScopeCacheEntry> m_Scope = new Dictionary<Entity, BypassControlScopeCacheEntry>();
        private readonly Dictionary<Entity, BypassHoldCadenceSnapshot> m_Cadence = new Dictionary<Entity, BypassHoldCadenceSnapshot>();
        private readonly Dictionary<Entity, BypassConflictEpisode> m_Conflict = new Dictionary<Entity, BypassConflictEpisode>();

        public NativeHashMap<Entity, Entity> Blockers => m_Blockers;
        public Dictionary<Entity, BypassControlScopeCacheEntry> Scope => m_Scope;
        public Dictionary<Entity, BypassHoldCadenceSnapshot> Cadence => m_Cadence;
        public Dictionary<Entity, BypassConflictEpisode> Conflict => m_Conflict;

        public BypassDecision(IBypassDecisionRuntime runtime)
        {
            m_Runtime = runtime;
            m_Blockers = new NativeHashMap<Entity, Entity>(256, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (m_Blockers.IsCreated)
                m_Blockers.Dispose();
        }

        public BypassDecisionResult Evaluate(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            uint nowFrame)
        {
            m_Runtime.CountCadenceCall();
            bool hadLatchedYield = TryGetLatchedBlocker(vehicle, out Entity initialLatchedBlocker);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex <= 0)
            {
                return new BypassDecisionResult(
                    false,
                    hadLatchedYield,
                    hadLatchedYield,
                    initialLatchedBlocker,
                    false,
                    Entity.Null,
                    true);
            }

            if (!m_Runtime.FeatureEnabled())
            {
                Remove(vehicle, BypassEntryKind.Cadence);
                Remove(vehicle, BypassEntryKind.Episode);
                return BuildResult(vehicle, true, hadLatchedYield, false, Entity.Null, true, "feature-disabled");
            }

            if (!m_Runtime.TryScope(
                vehicle,
                line,
                waypoints,
                waypointIndex,
                out BypassControlScope scope,
                out _))
            {
                Remove(vehicle, BypassEntryKind.Cadence);
                Remove(vehicle, BypassEntryKind.Episode);
                return BuildResult(vehicle, true, hadLatchedYield, false, Entity.Null, true);
            }

            if (!m_Runtime.IsLocalLine(scope.Line))
            {
                return BuildResult(vehicle, true, hadLatchedYield, false, Entity.Null, true, "line-no-longer-local");
            }

            if (hadLatchedYield
                && ReuseEpisode(scope, waypoints, nowFrame, out bool shouldHold, out Entity blocker, out bool canClearAfterExit))
            {
                return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit);
            }

            if (ReuseCadence(scope, waypoints, hadLatchedYield, nowFrame, out shouldHold, out blocker, out canClearAfterExit))
            {
                return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit);
            }

            m_Runtime.CountCadenceMiss();
            shouldHold = FindBlocker(scope, waypoints, nowFrame, out blocker, out string decisionReason, out bool hasLatchedBlockerProjection, out BypassLatchedBlockerProjection latchedBlockerProjection);
            canClearAfterExit = (hadLatchedYield || shouldHold)
                && m_Runtime.CanClear(scope.Vehicle, scope.Line, waypoints, scope.WaypointIndex);
            BypassConflictMode conflictMode = InferConflictMode(decisionReason);
            Entity expressLine = blocker != Entity.Null ? m_Runtime.ResolveLine(blocker) : Entity.Null;
            bool sameStationRequired = string.Equals(decisionReason, "track-model-same-station-same-direction-express-departing", StringComparison.Ordinal);
            if (shouldHold && blocker != Entity.Null)
                StoreEpisode(scope, blocker, expressLine, conflictMode, nowFrame, canClearAfterExit, sameStationRequired, hasLatchedBlockerProjection, latchedBlockerProjection);
            else
                Remove(scope.Vehicle, BypassEntryKind.Episode);

            StoreCadence(scope, hadLatchedYield, nowFrame, shouldHold, canClearAfterExit, conflictMode, blocker);
            return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit);
        }

        private BypassDecisionResult BuildResult(
            Entity vehicle,
            bool evaluated,
            bool hadLatchedYield,
            bool shouldHold,
            Entity blocker,
            bool canClearAfterExit,
            string releaseReason = null)
        {
            bool hasLatchedYield = TryGetLatchedBlocker(vehicle, out Entity latchedBlocker);
            return new BypassDecisionResult(
                evaluated,
                hadLatchedYield,
                hasLatchedYield,
                latchedBlocker,
                shouldHold,
                blocker,
                canClearAfterExit,
                releaseReason);
        }

        public bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker)
        {
            blocker = Entity.Null;
            return vehicle != Entity.Null
                && m_Blockers.IsCreated
                && m_Blockers.TryGetValue(vehicle, out blocker);
        }

        public void SetBlocker(Entity vehicle, Entity blocker)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null || !m_Blockers.IsCreated)
                return;

            m_Blockers[vehicle] = blocker;
        }

        public void ClearBlocker(Entity vehicle)
        {
            if (vehicle != Entity.Null && m_Blockers.IsCreated)
                m_Blockers.Remove(vehicle);
        }

        public bool Get(Entity vehicle, out BypassControlScopeCacheEntry scope)
        {
            return m_Scope.TryGetValue(vehicle, out scope);
        }

        public bool Get(Entity vehicle, out BypassHoldCadenceSnapshot cadence)
        {
            return m_Cadence.TryGetValue(vehicle, out cadence);
        }

        public bool Get(Entity vehicle, out BypassConflictEpisode episode)
        {
            return m_Conflict.TryGetValue(vehicle, out episode);
        }

        public void Put(Entity vehicle, BypassControlScopeCacheEntry scope)
        {
            if (vehicle != Entity.Null)
                m_Scope[vehicle] = scope;
        }

        public void Put(Entity vehicle, BypassHoldCadenceSnapshot cadence)
        {
            if (vehicle != Entity.Null)
                m_Cadence[vehicle] = cadence;
        }

        public void Put(Entity vehicle, BypassConflictEpisode episode)
        {
            if (vehicle != Entity.Null)
                m_Conflict[vehicle] = episode;
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            ClearBlocker(vehicle);
            m_Scope.Remove(vehicle);
            m_Cadence.Remove(vehicle);
            m_Conflict.Remove(vehicle);
        }

        public void Remove(Entity vehicle, BypassEntryKind kind)
        {
            if (vehicle == Entity.Null)
                return;

            switch (kind)
            {
                case BypassEntryKind.Scope:
                    m_Scope.Remove(vehicle);
                    break;
                case BypassEntryKind.Cadence:
                    m_Cadence.Remove(vehicle);
                    break;
                case BypassEntryKind.Episode:
                    m_Conflict.Remove(vehicle);
                    break;
            }
        }

        public void Clear()
        {
            if (m_Blockers.IsCreated)
                m_Blockers.Clear();

            m_Scope.Clear();
            m_Cadence.Clear();
            m_Conflict.Clear();
        }

        public bool CanRelease(BypassDecisionResult result)
        {
            return result.Evaluated
                && (result.HasLatchedYield || !string.IsNullOrWhiteSpace(result.ReleaseReason))
                && !result.ShouldHold;
        }

        public Entity FindBlocker(BypassDecisionResult result)
        {
            return result.Blocker != Entity.Null
                ? result.Blocker
                : result.LatchedBlocker;
        }

        private bool ReuseEpisode(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out bool shouldHold,
            out Entity blocker,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blocker = Entity.Null;
            canClearAfterExit = true;

            if (!Get(scope.Vehicle, out BypassConflictEpisode episode)
                || !episode.SceneKey.Equals(scope.SceneKey)
                || !SustainUntilRelease(episode.Mode)
                || episode.BlockerVehicle == Entity.Null
                || !m_Runtime.Exists(episode.BlockerVehicle))
            {
                return false;
            }

            blocker = episode.BlockerVehicle;
            if (episode.CanClearAfterExit
                && m_Runtime.CanClear(scope.Vehicle, scope.Line, waypoints, scope.WaypointIndex))
            {
                Remove(scope.Vehicle, BypassEntryKind.Episode);
                Remove(scope.Vehicle, BypassEntryKind.Cadence);
                return false;
            }

            if (episode.SameStationRequired
                && !m_Runtime.BlockerAtStation(blocker, scope.CurrentBypassBuilding))
            {
                return false;
            }

            if (!m_Runtime.LatchedBeforeRelease(scope, waypoints, episode, blocker, out bool blockerStillBeforeRelease)
                || !blockerStillBeforeRelease)
            {
                return false;
            }

            if (!episode.SameStationRequired)
            {
                bool shouldRecheck = nowFrame <= episode.LastQueuedLocalReleaseCheckFrame
                    || (nowFrame - episode.LastQueuedLocalReleaseCheckFrame) >= m_Runtime.EpisodeRecheckFrames();
                if (shouldRecheck)
                {
                    if (m_Runtime.ReleaseForQueuedLocal(scope, waypoints, blocker))
                    {
                        Remove(scope.Vehicle, BypassEntryKind.Episode);
                        Remove(scope.Vehicle, BypassEntryKind.Cadence);
                        return false;
                    }

                    Put(scope.Vehicle, new BypassConflictEpisode(
                        episode.LocalVehicle,
                        episode.SceneKey,
                        episode.ExpressLine,
                        episode.BlockerVehicle,
                        episode.Mode,
                        episode.AcquiredFrame,
                        nowFrame,
                        episode.CanClearAfterExit,
                        episode.SameStationRequired,
                        episode.HasLatchedBlockerProjection,
                        episode.LatchedBlockerProjection));
                }
            }

            shouldHold = true;
            canClearAfterExit = episode.CanClearAfterExit;
            m_Runtime.CountEpisodeReuse();
            return true;
        }

        private bool ReuseCadence(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool hasLatchedYield,
            uint nowFrame,
            out bool shouldHold,
            out Entity blocker,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blocker = Entity.Null;
            canClearAfterExit = true;

            if (Get(scope.Vehicle, out BypassHoldCadenceSnapshot snapshot)
                && snapshot.SceneKey.Equals(scope.SceneKey)
                && snapshot.WaypointIndex == scope.WaypointIndex)
            {
                if (snapshot.ShouldHold
                    && (snapshot.Blocker == Entity.Null
                        || !m_Runtime.Exists(snapshot.Blocker)
                        || SustainUntilRelease(snapshot.ConflictMode)))
                {
                    return false;
                }

                if (snapshot.EvaluatedFrame == nowFrame
                    || (nowFrame < snapshot.ReevaluateAfterFrame
                        && (hasLatchedYield || !snapshot.ShouldHold)))
                {
                    if (hasLatchedYield
                        && snapshot.ShouldHold
                        && snapshot.CanClearAfterExit
                        && m_Runtime.CanClear(scope.Vehicle, scope.Line, waypoints, scope.WaypointIndex))
                    {
                        Remove(scope.Vehicle, BypassEntryKind.Cadence);
                        return false;
                    }

                    shouldHold = snapshot.ShouldHold;
                    blocker = snapshot.Blocker;
                    canClearAfterExit = snapshot.CanClearAfterExit;
                    return true;
                }
            }

            return false;
        }

        private bool FindBlocker(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out Entity blocker,
            out string reason,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            blocker = Entity.Null;
            reason = string.Empty;
            hasLatchedBlockerProjection = false;
            latchedBlockerProjection = default;
            if (!m_Runtime.Baseline(scope, waypoints, nowFrame, out bool shouldYield, out string baselineReason, out Entity baselineBlocker, out hasLatchedBlockerProjection, out latchedBlockerProjection))
            {
                reason = "track-model-decision-unavailable";
                return m_Runtime.Finalize(scope, waypoints, false, reason, Entity.Null);
            }

            if (shouldYield
                && baselineBlocker != Entity.Null
                && baselineReason == "track-model-same-direction-shared-express-approaching"
                && m_Runtime.ReleaseForQueuedLocal(scope, waypoints, baselineBlocker))
            {
                blocker = Entity.Null;
                reason = "express-behind-nearest-queued-local";
                return m_Runtime.Finalize(scope, waypoints, false, reason, Entity.Null);
            }

            blocker = baselineBlocker;
            reason = baselineReason ?? string.Empty;
            return m_Runtime.Finalize(scope, waypoints, shouldYield, reason, baselineBlocker);
        }

        private void StoreEpisode(
            BypassControlScope scope,
            Entity blocker,
            Entity expressLine,
            BypassConflictMode mode,
            uint nowFrame,
            bool canClearAfterExit,
            bool sameStationRequired,
            bool hasLatchedBlockerProjection,
            BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            if (scope.Vehicle == Entity.Null
                || blocker == Entity.Null
                || !SustainUntilRelease(mode))
            {
                Remove(scope.Vehicle, BypassEntryKind.Episode);
                return;
            }

            Put(scope.Vehicle, new BypassConflictEpisode(
                scope.Vehicle,
                scope.SceneKey,
                expressLine,
                blocker,
                mode,
                nowFrame,
                nowFrame,
                canClearAfterExit,
                sameStationRequired,
                hasLatchedBlockerProjection,
                latchedBlockerProjection));
        }

        private void StoreCadence(
            BypassControlScope scope,
            bool hasLatchedYield,
            uint nowFrame,
            bool shouldHold,
            bool canClearAfterExit,
            BypassConflictMode mode,
            Entity blocker)
        {
            uint reevaluateAfterFrame = nowFrame + 1;
            if (hasLatchedYield && shouldHold)
                reevaluateAfterFrame = nowFrame + m_Runtime.HeldReevaluateFrames();
            else if (!hasLatchedYield && !shouldHold)
                reevaluateAfterFrame = nowFrame + m_Runtime.UnlatchedReevaluateFrames();

            Put(scope.Vehicle, new BypassHoldCadenceSnapshot(
                scope.SceneKey,
                scope.WaypointIndex,
                nowFrame,
                reevaluateAfterFrame,
                shouldHold,
                canClearAfterExit,
                mode,
                blocker));
        }

        private static BypassConflictMode InferConflictMode(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return BypassConflictMode.Unknown;
            if (string.Equals(reason, "track-model-same-direction-shared-express-approaching", StringComparison.Ordinal)
                || string.Equals(reason, "track-model-same-station-same-direction-express-departing", StringComparison.Ordinal))
            {
                return BypassConflictMode.Block;
            }

            return BypassConflictMode.EtaRefresh;
        }

        private static bool SustainUntilRelease(BypassConflictMode mode)
        {
            return mode == BypassConflictMode.Block;
        }
    }

    public partial class DispatchRuntimeSystem : IBypassDecisionRuntime
    {
        private NativeHashMap<Entity, Entity> m_BypassYieldBlocker => m_BypassDecision.Blockers;
        private Dictionary<Entity, BypassControlScopeCacheEntry> m_BypassControlScopeCache => m_BypassDecision.Scope;
        private Dictionary<Entity, BypassHoldCadenceSnapshot> m_BypassHoldCadenceSnapshots => m_BypassDecision.Cadence;
        private Dictionary<Entity, BypassConflictEpisode> m_BypassConflictEpisodes => m_BypassDecision.Conflict;

        bool IBypassDecisionRuntime.FeatureEnabled()
        {
            return IsBypassRuntimeFeatureEnabled();
        }

        bool IBypassDecisionRuntime.TryScope(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int waypointIndex,
            out BypassControlScope scope,
            out string failureReason)
        {
            return TryGetBypassControlScope(vehicle, line, waypoints, waypointIndex, out scope, out failureReason);
        }

        bool IBypassDecisionRuntime.IsLocalLine(Entity line)
        {
            return line != Entity.Null
                && IsDispatchRuntimeManagedLine(line)
                && IsAppliedWorkbenchLocalLine(line);
        }

        bool IBypassDecisionRuntime.Exists(Entity entity)
        {
            return entity != Entity.Null && EntityManager.Exists(entity);
        }

        bool IBypassDecisionRuntime.CanClear(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            return CanClearBypassYieldAfterStationExit(vehicle, line, waypoints, waypointIndex);
        }

        bool IBypassDecisionRuntime.BlockerAtStation(Entity blocker, Entity station)
        {
            return IsExpressBlockerStillWithinBypassStation(blocker, station);
        }

        bool IBypassDecisionRuntime.LatchedBeforeRelease(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            BypassConflictEpisode episode,
            Entity blocker,
            out bool beforeRelease)
        {
            return TryIsLatchedBypassBlockerBeforeRelease(scope, waypoints, episode, blocker, out beforeRelease);
        }

        bool IBypassDecisionRuntime.ReleaseForQueuedLocal(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker)
        {
            if (!TryShouldReleaseForQueuedLocalAhead(
                    scope,
                    waypoints,
                    blocker,
                    out float expressSceneCoordinate,
                    out float localSceneCoordinate,
                    out float queuedLocalMeters))
            {
                return false;
            }

            LogQueuedLocalBypassOverrideOnce(
                scope.Vehicle,
                scope.Line,
                blocker,
                "release",
                "express-behind-nearest-queued-local",
                expressSceneCoordinate,
                localSceneCoordinate,
                queuedLocalMeters);
            return true;
        }

        bool IBypassDecisionRuntime.Baseline(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out bool shouldYield,
            out string reason,
            out Entity blocker,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            return TryGetTrackModelBypassBaseline(
                scope,
                waypoints,
                nowFrame,
                out shouldYield,
                out reason,
                out blocker,
                out hasLatchedBlockerProjection,
                out latchedBlockerProjection);
        }

        bool IBypassDecisionRuntime.Finalize(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool shouldYield,
            string reason,
            Entity blocker)
        {
            return FinalizeBypassDecision(
                scope.Vehicle,
                scope.Line,
                waypoints,
                scope.WaypointIndex,
                scope.CurrentBypassBuilding,
                scope.NextBypassBuilding,
                shouldYield,
                reason,
                blocker);
        }

        Entity IBypassDecisionRuntime.ResolveLine(Entity vehicle)
        {
            return ResolveVehicleLine(vehicle);
        }

        uint IBypassDecisionRuntime.HeldReevaluateFrames()
        {
            return BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES;
        }

        uint IBypassDecisionRuntime.EpisodeRecheckFrames()
        {
            return BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES;
        }

        uint IBypassDecisionRuntime.UnlatchedReevaluateFrames()
        {
            return BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES;
        }

        void IBypassDecisionRuntime.CountCadenceCall()
        {
            m_BypassPerfProbeCadenceCalls++;
        }

        void IBypassDecisionRuntime.CountCadenceMiss()
        {
            m_BypassPerfProbeCadenceMisses++;
        }

        void IBypassDecisionRuntime.CountEpisodeReuse()
        {
            m_BypassPerfProbeEpisodeReuses++;
        }

        bool IBypassDecisionRuntime.TryGetLatchedBlocker(Entity vehicle, out Entity blocker)
        {
            return m_BypassDecision.TryGetLatchedBlocker(vehicle, out blocker);
        }
    }
}
