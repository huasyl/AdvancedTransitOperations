using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal interface IDecisionContext
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

        bool ShouldClearHoldAfterStationExit(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints);

        bool BlockerAtStation(Entity blocker, Entity station);

        bool LatchedBeforeRelease(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, BypassConflictEpisode episode, Entity blocker, out bool beforeRelease);

        bool ReleaseForQueuedLocal(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker, out string releaseReason);

        bool Baseline(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out bool shouldYield,
            out string reason,
            out Entity blocker,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection);

        bool ApplyDecisionVetoes(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, bool shouldYield, string reason, Entity blocker);

        Entity ResolveLine(Entity vehicle);

        uint HeldReevaluateFrames();

        uint EpisodeRecheckFrames();

        uint LatchedReleaseRecheckFrames();

        uint UnlatchedReevaluateFrames();

        void CountCadenceCall();

        void CountCadenceMiss();

        void CountEpisodeReuse();

        bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker);
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

    internal readonly struct LocalLineGateSnapshot
    {
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly bool IsLocal;

        public LocalLineGateSnapshot(Entity line, int waypointIndex, bool isLocal)
        {
            Line = line;
            WaypointIndex = waypointIndex;
            IsLocal = isLocal;
        }
    }

    internal sealed class DecisionEngine : IDisposable
    {
        private readonly IDecisionContext m_Runtime;
        private readonly BypassStateStore m_State;
        private readonly Dictionary<Entity, LocalLineGateSnapshot> m_LocalLineGate = new Dictionary<Entity, LocalLineGateSnapshot>();

        internal DecisionEngine(IDecisionContext runtime)
        {
            m_Runtime = runtime;
            m_State = new BypassStateStore();
        }

        internal BypassStateStore State => m_State;
        internal Unity.Collections.NativeHashMap<Entity, Entity> Blockers => m_State.Blockers;
        internal Dictionary<Entity, BypassControlScopeCacheEntry> Scope => m_State.Scope;
        internal Dictionary<Entity, BypassHoldCadenceSnapshot> Cadence => m_State.Cadence;
        internal Dictionary<Entity, BypassConflictEpisode> Conflict => m_State.Conflict;

        public void Dispose()
        {
            m_State.Dispose();
        }

        internal BypassDecisionResult Evaluate(
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

            bool lineKnownLocal = GetOrCreateLocalLineGate(vehicle, line, waypointIndex);
            if (!hadLatchedYield && !lineKnownLocal)
            {
                Remove(vehicle, BypassEntryKind.Cadence);
                Remove(vehicle, BypassEntryKind.Episode);
                return BuildResult(vehicle, true, hadLatchedYield, false, Entity.Null, true, "line-not-local");
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

            if ((!lineKnownLocal || scope.Line != line) && !m_Runtime.IsLocalLine(scope.Line))
            {
                return BuildResult(vehicle, true, hadLatchedYield, false, Entity.Null, true, "line-no-longer-local");
            }

            string episodeReleaseReason = null;
            if (hadLatchedYield
                && ReuseEpisode(scope, waypoints, nowFrame, out bool shouldHold, out Entity blocker, out bool canClearAfterExit, out episodeReleaseReason))
            {
                return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit);
            }

            if (ReuseCadence(scope, waypoints, hadLatchedYield, nowFrame, out shouldHold, out blocker, out canClearAfterExit))
            {
                return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit, shouldHold ? null : episodeReleaseReason);
            }

            m_Runtime.CountCadenceMiss();
            shouldHold = FindBlocker(scope, waypoints, nowFrame, out blocker, out string decisionReason, out bool hasLatchedBlockerProjection, out BypassLatchedBlockerProjection latchedBlockerProjection);
            canClearAfterExit = (hadLatchedYield || shouldHold)
                && m_Runtime.ShouldClearHoldAfterStationExit(scope, waypoints);
            BypassConflictMode conflictMode = InferConflictMode(decisionReason);
            Entity expressLine = blocker != Entity.Null ? m_Runtime.ResolveLine(blocker) : Entity.Null;
            bool sameStationRequired = string.Equals(decisionReason, "track-model-same-station-same-direction-express-departing", StringComparison.Ordinal);
            if (shouldHold && blocker != Entity.Null)
                StoreEpisode(scope, blocker, expressLine, conflictMode, nowFrame, canClearAfterExit, sameStationRequired, hasLatchedBlockerProjection, latchedBlockerProjection);
            else
                Remove(scope.Vehicle, BypassEntryKind.Episode);

            StoreCadence(scope, hadLatchedYield, nowFrame, shouldHold, canClearAfterExit, conflictMode, blocker);
            return BuildResult(vehicle, true, hadLatchedYield, shouldHold, blocker, canClearAfterExit, shouldHold ? null : (episodeReleaseReason ?? decisionReason));
        }

        internal bool CanRelease(BypassDecisionResult result)
        {
            return result.Evaluated
                && (result.HasLatchedYield || !string.IsNullOrWhiteSpace(result.ReleaseReason))
                && !result.ShouldHold;
        }

        internal Entity FindBlocker(BypassDecisionResult result)
        {
            return result.Blocker != Entity.Null
                ? result.Blocker
                : result.LatchedBlocker;
        }

        internal bool TryGetLatchedBlocker(Entity vehicle, out Entity blocker)
        {
            return m_State.TryGetLatchedBlocker(vehicle, out blocker);
        }

        internal void SetBlocker(Entity vehicle, Entity blocker)
        {
            m_State.SetBlocker(vehicle, blocker);
        }

        internal void ClearBlocker(Entity vehicle)
        {
            m_State.ClearBlocker(vehicle);
        }

        internal bool Get(Entity vehicle, out BypassControlScopeCacheEntry scope)
        {
            return m_State.Get(vehicle, out scope);
        }

        internal bool Get(Entity vehicle, out BypassHoldCadenceSnapshot cadence)
        {
            return m_State.Get(vehicle, out cadence);
        }

        internal bool Get(Entity vehicle, out BypassConflictEpisode episode)
        {
            return m_State.Get(vehicle, out episode);
        }

        internal void Put(Entity vehicle, BypassControlScopeCacheEntry scope)
        {
            m_State.Put(vehicle, scope);
        }

        internal void Put(Entity vehicle, BypassHoldCadenceSnapshot cadence)
        {
            m_State.Put(vehicle, cadence);
        }

        internal void Put(Entity vehicle, BypassConflictEpisode episode)
        {
            m_State.Put(vehicle, episode);
        }

        internal void Remove(Entity vehicle)
        {
            m_LocalLineGate.Remove(vehicle);
            m_State.Remove(vehicle);
        }

        internal void Remove(Entity vehicle, BypassEntryKind kind)
        {
            m_State.Remove(vehicle, kind);
        }

        internal void Clear()
        {
            m_LocalLineGate.Clear();
            m_State.Clear();
        }

        internal void ClearLocalLineGateForLine(Entity line)
        {
            if (line == Entity.Null || m_LocalLineGate.Count == 0)
                return;

            List<Entity> keys = null;
            foreach (KeyValuePair<Entity, LocalLineGateSnapshot> entry in m_LocalLineGate)
            {
                if (entry.Value.Line != line)
                    continue;

                keys ??= new List<Entity>();
                keys.Add(entry.Key);
            }

            if (keys == null)
                return;

            for (int i = 0; i < keys.Count; i++)
                m_LocalLineGate.Remove(keys[i]);
        }

        private bool GetOrCreateLocalLineGate(Entity vehicle, Entity line, int waypointIndex)
        {
            if (m_LocalLineGate.TryGetValue(vehicle, out LocalLineGateSnapshot snapshot)
                && snapshot.Line == line
                && snapshot.WaypointIndex == waypointIndex)
            {
                return snapshot.IsLocal;
            }

            bool isLocal = m_Runtime.IsLocalLine(line);
            if (vehicle != Entity.Null)
                m_LocalLineGate[vehicle] = new LocalLineGateSnapshot(line, waypointIndex, isLocal);
            return isLocal;
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

        private bool ReuseEpisode(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out bool shouldHold,
            out Entity blocker,
            out bool canClearAfterExit,
            out string releaseReason)
        {
            shouldHold = false;
            blocker = Entity.Null;
            canClearAfterExit = true;
            releaseReason = null;

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
                && m_Runtime.ShouldClearHoldAfterStationExit(scope, waypoints))
            {
                Remove(scope.Vehicle, BypassEntryKind.Episode);
                Remove(scope.Vehicle, BypassEntryKind.Cadence);
                releaseReason = "local-cleared-station-exit canClearAfterExit=1";
                return false;
            }

            if (episode.SameStationRequired
                && !m_Runtime.BlockerAtStation(blocker, scope.CurrentBypassBuilding))
            {
                releaseReason = "same-station-blocker-left-station blocker=" + blocker.Index;
                return false;
            }

            bool shouldCheckRelease = nowFrame <= episode.LastReleaseCheckFrame
                || (nowFrame - episode.LastReleaseCheckFrame) >= m_Runtime.LatchedReleaseRecheckFrames();
            bool releaseCheckUpdated = false;
            if (shouldCheckRelease)
            {
                if (!m_Runtime.LatchedBeforeRelease(scope, waypoints, episode, blocker, out bool blockerStillBeforeRelease))
                {
                    releaseReason = "latched-release-check-unavailable blocker=" + blocker.Index;
                    return false;
                }

                if (!blockerStillBeforeRelease)
                {
                    releaseReason = "latched-blocker-past-release-window blocker=" + blocker.Index;
                    return false;
                }

                episode = new BypassConflictEpisode(
                    episode.LocalVehicle,
                    episode.SceneKey,
                    episode.ExpressLine,
                    episode.BlockerVehicle,
                    episode.Mode,
                    episode.AcquiredFrame,
                    episode.LastQueuedLocalReleaseCheckFrame,
                    nowFrame,
                    blockerStillBeforeRelease,
                    episode.CanClearAfterExit,
                    episode.SameStationRequired,
                    episode.HasLatchedBlockerProjection,
                    episode.LatchedBlockerProjection);
                releaseCheckUpdated = true;
            }

            if (!episode.SameStationRequired)
            {
                bool shouldRecheck = nowFrame <= episode.LastQueuedLocalReleaseCheckFrame
                    || (nowFrame - episode.LastQueuedLocalReleaseCheckFrame) >= m_Runtime.EpisodeRecheckFrames();
                if (shouldRecheck)
                {
                    if (m_Runtime.ReleaseForQueuedLocal(scope, waypoints, blocker, out string queuedReleaseReason))
                    {
                        Remove(scope.Vehicle, BypassEntryKind.Episode);
                        Remove(scope.Vehicle, BypassEntryKind.Cadence);
                        releaseReason = queuedReleaseReason;
                        return false;
                    }

                    episode = new BypassConflictEpisode(
                        episode.LocalVehicle,
                        episode.SceneKey,
                        episode.ExpressLine,
                        episode.BlockerVehicle,
                        episode.Mode,
                        episode.AcquiredFrame,
                        nowFrame,
                        episode.LastReleaseCheckFrame,
                        episode.LastReleaseCheckBeforeRelease,
                        episode.CanClearAfterExit,
                        episode.SameStationRequired,
                        episode.HasLatchedBlockerProjection,
                        episode.LatchedBlockerProjection);
                    Put(scope.Vehicle, episode);
                    releaseCheckUpdated = false;
                }
            }

            if (releaseCheckUpdated)
            {
                Put(scope.Vehicle, episode);
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
                        && m_Runtime.ShouldClearHoldAfterStationExit(scope, waypoints))
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
                return m_Runtime.ApplyDecisionVetoes(scope, waypoints, false, reason, Entity.Null);
            }

            if (shouldYield
                && baselineBlocker != Entity.Null
                && baselineReason == "track-model-same-direction-shared-express-approaching"
                && m_Runtime.ReleaseForQueuedLocal(scope, waypoints, baselineBlocker, out string queuedReleaseReason))
            {
                blocker = Entity.Null;
                reason = queuedReleaseReason ?? "express-behind-nearest-queued-local";
                return m_Runtime.ApplyDecisionVetoes(scope, waypoints, false, reason, Entity.Null);
            }

            blocker = baselineBlocker;
            reason = baselineReason ?? string.Empty;
            return m_Runtime.ApplyDecisionVetoes(scope, waypoints, shouldYield, reason, baselineBlocker);
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
                nowFrame,
                true,
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
}
