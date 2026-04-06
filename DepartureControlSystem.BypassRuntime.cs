using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void SetBypassYieldState(Entity vehicle, Entity blocker, string lineTag, string stateTag)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            if (m_BypassYieldBlocker.TryGetValue(vehicle, out Entity previousBlocker) && previousBlocker == blocker)
                return;

            m_BypassYieldBlocker[vehicle] = blocker;
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            log.Info("[待避] " + lineTag + " 车辆" + vehicle.Index
                + " state=" + stateTag
                + " 等待快车" + blocker.Index + " 先行");
        }

        private void ClearBypassYieldState(Entity vehicle, string releaseReason = null)
        {
            if (vehicle == Entity.Null || !m_BypassYieldBlocker.TryGetValue(vehicle, out Entity blocker))
                return;

            m_BypassYieldBlocker.Remove(vehicle);
            m_BypassHoldCadenceSnapshots.Remove(vehicle);
            m_BypassConflictEpisodes.Remove(vehicle);
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            Entity line = ResolveVehicleLine(vehicle);
            string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
            log.Info("[待避解除] " + lineTag + " 车辆" + vehicle.Index
                + " 解除快车待避"
                + (!string.IsNullOrWhiteSpace(releaseReason) ? " reason=" + releaseReason : string.Empty)
                + (blocker != Entity.Null ? " blocker=" + blocker.Index : string.Empty));
        }

        private void SetBypassRuntimeEnabled(bool enabled)
        {
            if (m_BypassRuntimeEnabled == enabled)
                return;

            m_BypassRuntimeEnabled = enabled;
            ClearBypassRuntimeState();
            log.Info(enabled
                ? "[F5] 已启用快慢车待避"
                : "[F5] 已禁用快慢车待避（仅用于性能排查）");
        }

        private void ClearBypassRuntimeState()
        {
            if (m_BypassYieldBlocker.IsCreated)
                m_BypassYieldBlocker.Clear();

            m_BypassHoldCadenceSnapshots.Clear();
            m_BypassConflictEpisodes.Clear();
            m_BypassControlScopeCache.Clear();
            m_BypassDecisionLogCache.Clear();
            m_BypassQueuedLocalOverrideLogCache.Clear();
            ClearBypassTrackModelRuntimeState();
        }

        private void ClearBypassRuntimeStateForLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            if (m_BypassYieldBlocker.IsCreated)
            {
                var blockerKeys = m_BypassYieldBlocker.GetKeyArray(Allocator.Temp);
                List<Entity> yieldVehiclesToRelease = null;
                for (int i = blockerKeys.Length - 1; i >= 0; i--)
                {
                    Entity vehicle = blockerKeys[i];
                    Entity localLine = ResolveVehicleLine(vehicle);
                    Entity blockerLine = m_BypassYieldBlocker.TryGetValue(vehicle, out Entity blockerVehicle)
                        ? ResolveVehicleLine(blockerVehicle)
                        : Entity.Null;
                    if (localLine != line && blockerLine != line)
                        continue;

                    yieldVehiclesToRelease ??= new List<Entity>();
                    yieldVehiclesToRelease.Add(vehicle);
                }
                blockerKeys.Dispose();

                if (yieldVehiclesToRelease != null)
                {
                    for (int i = 0; i < yieldVehiclesToRelease.Count; i++)
                    {
                        Entity vehicle = yieldVehiclesToRelease[i];
                        ClearBypassYieldState(vehicle, "线路运行态失效");
                        m_BypassDecisionLogCache.Remove(vehicle);
                        m_BypassQueuedLocalOverrideLogCache.Remove(vehicle);
                    }
                }
            }

            List<Entity> cadenceKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassHoldCadenceSnapshot> entry in m_BypassHoldCadenceSnapshots)
            {
                if (entry.Value.SceneKey.Line != line)
                    continue;

                cadenceKeysToRemove ??= new List<Entity>();
                cadenceKeysToRemove.Add(entry.Key);
            }

            if (cadenceKeysToRemove != null)
            {
                for (int i = 0; i < cadenceKeysToRemove.Count; i++)
                {
                    Entity vehicle = cadenceKeysToRemove[i];
                    m_BypassHoldCadenceSnapshots.Remove(vehicle);
                    m_BypassDecisionLogCache.Remove(vehicle);
                    m_BypassQueuedLocalOverrideLogCache.Remove(vehicle);
                }
            }

            List<Entity> scopeKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassControlScopeCacheEntry> entry in m_BypassControlScopeCache)
            {
                if (entry.Value.Line != line)
                    continue;

                scopeKeysToRemove ??= new List<Entity>();
                scopeKeysToRemove.Add(entry.Key);
            }

            if (scopeKeysToRemove != null)
            {
                for (int i = 0; i < scopeKeysToRemove.Count; i++)
                    m_BypassControlScopeCache.Remove(scopeKeysToRemove[i]);
            }

            List<Entity> episodeKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassConflictEpisode> entry in m_BypassConflictEpisodes)
            {
                if (entry.Value.SceneKey.Line != line && entry.Value.ExpressLine != line)
                    continue;

                episodeKeysToRemove ??= new List<Entity>();
                episodeKeysToRemove.Add(entry.Key);
            }

            if (episodeKeysToRemove != null)
            {
                for (int i = 0; i < episodeKeysToRemove.Count; i++)
                    m_BypassConflictEpisodes.Remove(episodeKeysToRemove[i]);
            }

            ClearBypassTrackModelRuntimeStateForLine(line);
        }

        private void InvalidateTrackTimingForLine(Entity line)
        {
            if (line == Entity.Null)
                return;

            ClearLineTimeProfiles();

            List<Entity> cadenceKeysToRemove = null;
            foreach (KeyValuePair<Entity, BypassHoldCadenceSnapshot> entry in m_BypassHoldCadenceSnapshots)
            {
                if (entry.Value.SceneKey.Line != line)
                    continue;

                cadenceKeysToRemove ??= new List<Entity>();
                cadenceKeysToRemove.Add(entry.Key);
            }

            if (cadenceKeysToRemove == null)
                return;

            for (int i = 0; i < cadenceKeysToRemove.Count; i++)
            {
                Entity vehicle = cadenceKeysToRemove[i];
                m_BypassHoldCadenceSnapshots.Remove(vehicle);
                m_BypassDecisionLogCache.Remove(vehicle);
                m_BypassQueuedLocalOverrideLogCache.Remove(vehicle);
            }
        }

        private static BypassConflictMode InferConflictModeFromDecisionReason(string decisionReason)
        {
            if (string.IsNullOrWhiteSpace(decisionReason))
                return BypassConflictMode.Unknown;
            if (string.Equals(decisionReason, "track-model-same-direction-shared-express-approaching", StringComparison.Ordinal)
                || string.Equals(decisionReason, "track-model-same-station-same-direction-express-departing", StringComparison.Ordinal))
            {
                return BypassConflictMode.Block;
            }

            return BypassConflictMode.EtaRefresh;
        }

        private static bool ShouldConflictEpisodeSustainUntilRelease(BypassConflictMode mode)
        {
            return mode == BypassConflictMode.Block;
        }

        private void StoreBypassConflictEpisode(
            BypassControlScope scope,
            Entity blockerVehicle,
            Entity expressLine,
            BypassConflictMode conflictMode,
            uint nowFrame,
            bool canClearAfterExit,
            bool sameStationRequired,
            bool hasLatchedBlockerProjection,
            BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            if (scope.Vehicle == Entity.Null
                || blockerVehicle == Entity.Null
                || !ShouldConflictEpisodeSustainUntilRelease(conflictMode))
            {
                m_BypassConflictEpisodes.Remove(scope.Vehicle);
                return;
            }

            m_BypassConflictEpisodes[scope.Vehicle] = new BypassConflictEpisode(
                scope.Vehicle,
                scope.SceneKey,
                expressLine,
                blockerVehicle,
                conflictMode,
                nowFrame,
                canClearAfterExit,
                sameStationRequired,
                hasLatchedBlockerProjection,
                latchedBlockerProjection);
        }

        private bool TryReuseConflictEpisodeUntilRelease(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            out bool shouldHold,
            out Entity blockerVehicle,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blockerVehicle = Entity.Null;
            canClearAfterExit = true;

            if (!m_BypassConflictEpisodes.TryGetValue(scope.Vehicle, out BypassConflictEpisode episode)
                || !episode.SceneKey.Equals(scope.SceneKey)
                || !ShouldConflictEpisodeSustainUntilRelease(episode.Mode)
                || episode.BlockerVehicle == Entity.Null
                || !EntityManager.Exists(episode.BlockerVehicle))
            {
                return false;
            }

            blockerVehicle = episode.BlockerVehicle;
            if (episode.SameStationRequired
                && !IsExpressBlockerStillWithinBypassStation(blockerVehicle, scope.CurrentBypassBuilding))
            {
                return false;
            }

            if (!TryIsLatchedBypassBlockerBeforeRelease(scope, localWaypoints, episode, blockerVehicle, out bool blockerStillBeforeRelease)
                || !blockerStillBeforeRelease)
            {
                return false;
            }

            if (!episode.SameStationRequired
                && TryShouldReleaseForQueuedLocalAhead(
                    scope,
                    localWaypoints,
                    blockerVehicle,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            shouldHold = true;
            canClearAfterExit = episode.CanClearAfterExit;
            m_BypassPerfProbeEpisodeReuses++;
            return true;
        }

        private bool TryGetCadencedBypassHoldDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out bool shouldHold,
            out Entity blockerVehicle,
            out bool canClearAfterExit)
        {
            m_BypassPerfProbeCadenceCalls++;
            shouldHold = false;
            blockerVehicle = Entity.Null;
            canClearAfterExit = true;

            if (!m_BypassRuntimeEnabled)
            {
                m_BypassHoldCadenceSnapshots.Remove(localVehicle);
                m_BypassConflictEpisodes.Remove(localVehicle);
                return true;
            }

            if (!TryGetBypassControlScope(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out BypassControlScope scope,
                    out _))
            {
                m_BypassHoldCadenceSnapshots.Remove(localVehicle);
                m_BypassConflictEpisodes.Remove(localVehicle);
                return true;
            }

            if (scope.Line == Entity.Null
                || !IsWorkbenchTimetableApplied(scope.Line)
                || !IsAppliedWorkbenchLocalLine(scope.Line))
            {
                ClearBypassYieldState(localVehicle, "line-no-longer-local");
                return true;
            }

            bool hasLatchedYield = m_BypassYieldBlocker.ContainsKey(localVehicle);
            if (hasLatchedYield
                && TryReuseConflictEpisodeUntilRelease(scope, localWaypoints, out shouldHold, out blockerVehicle, out canClearAfterExit))
            {
                return true;
            }

            if (TryReuseBypassHoldCadenceSnapshot(scope, hasLatchedYield, nowFrame, out shouldHold, out blockerVehicle, out canClearAfterExit))
            {
                return true;
            }

            m_BypassPerfProbeCadenceMisses++;
            shouldHold = ShouldHoldLocalVehicleForExpressBypass(
                scope,
                localWaypoints,
                nowFrame,
                out blockerVehicle,
                out string decisionReason,
                out bool hasLatchedBlockerProjection,
                out BypassLatchedBlockerProjection latchedBlockerProjection);
            canClearAfterExit = (hasLatchedYield || shouldHold)
                && CanClearBypassYieldAfterStationExit(scope.Vehicle, scope.Line, localWaypoints, scope.WaypointIndex);
            BypassConflictMode conflictMode = InferConflictModeFromDecisionReason(decisionReason);
            Entity expressLine = blockerVehicle != Entity.Null ? ResolveVehicleLine(blockerVehicle) : Entity.Null;
            bool sameStationRequired = string.Equals(decisionReason, "track-model-same-station-same-direction-express-departing", StringComparison.Ordinal);
            if (shouldHold && blockerVehicle != Entity.Null)
                StoreBypassConflictEpisode(
                    scope,
                    blockerVehicle,
                    expressLine,
                    conflictMode,
                    nowFrame,
                    canClearAfterExit,
                    sameStationRequired,
                    hasLatchedBlockerProjection,
                    latchedBlockerProjection);
            else
                m_BypassConflictEpisodes.Remove(scope.Vehicle);

            StoreBypassHoldCadenceSnapshot(
                scope,
                hasLatchedYield,
                nowFrame,
                shouldHold,
                canClearAfterExit,
                conflictMode,
                blockerVehicle);
            return true;
        }

        private bool TryGetBypassControlScope(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            out BypassControlScope scope,
            out string failureReason)
        {
            scope = default;
            failureReason = null;

            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || currentWaypointIndex < 0)
            {
                m_BypassControlScopeCache.Remove(localVehicle);
                failureReason = "local-line-invalid";
                return false;
            }

            if (m_BypassControlScopeCache.TryGetValue(localVehicle, out BypassControlScopeCacheEntry cachedScope)
                && cachedScope.Line == localLine
                && cachedScope.WaypointIndex == currentWaypointIndex)
            {
                scope = cachedScope.Scope;
                return true;
            }

            if (!TryGetLocalSceneDefinition(
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out _,
                    out SceneDefinition sceneDefinition))
            {
                m_BypassControlScopeCache.Remove(localVehicle);
                failureReason = "scene-definition-missing";
                return false;
            }

            VehicleSceneBinding sceneBinding = new VehicleSceneBinding(
                localVehicle,
                sceneDefinition.Key,
                currentWaypointIndex);
            scope = new BypassControlScope(
                localVehicle,
                sceneBinding,
                sceneDefinition);
            m_BypassControlScopeCache[localVehicle] = new BypassControlScopeCacheEntry(
                localLine,
                currentWaypointIndex,
                scope);
            return true;
        }

        private bool TryReuseBypassHoldCadenceSnapshot(
            BypassControlScope scope,
            bool hasLatchedYield,
            uint nowFrame,
            out bool shouldHold,
            out Entity blockerVehicle,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blockerVehicle = Entity.Null;
            canClearAfterExit = true;

            if (m_BypassHoldCadenceSnapshots.TryGetValue(scope.Vehicle, out BypassHoldCadenceSnapshot snapshot)
                && snapshot.SceneKey.Equals(scope.SceneKey)
                && snapshot.WaypointIndex == scope.WaypointIndex)
            {
                if (snapshot.ShouldHold
                    && (snapshot.Blocker == Entity.Null
                        || !EntityManager.Exists(snapshot.Blocker)
                        || ShouldConflictEpisodeSustainUntilRelease(snapshot.ConflictMode)))
                {
                    return false;
                }

                if (snapshot.EvaluatedFrame == nowFrame
                    || (nowFrame < snapshot.ReevaluateAfterFrame
                        && (hasLatchedYield || !snapshot.ShouldHold)))
                {
                    shouldHold = snapshot.ShouldHold;
                    blockerVehicle = snapshot.Blocker;
                    canClearAfterExit = snapshot.CanClearAfterExit;
                    return true;
                }
            }

            return false;
        }

        private void StoreBypassHoldCadenceSnapshot(
            BypassControlScope scope,
            bool hasLatchedYield,
            uint nowFrame,
            bool shouldHold,
            bool canClearAfterExit,
            BypassConflictMode conflictMode,
            Entity blockerVehicle)
        {
            uint reevaluateAfterFrame = nowFrame + 1;
            if (hasLatchedYield && shouldHold)
                reevaluateAfterFrame = nowFrame + BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES;
            else if (!hasLatchedYield && !shouldHold)
                reevaluateAfterFrame = nowFrame + BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES;

            m_BypassHoldCadenceSnapshots[scope.Vehicle] = new BypassHoldCadenceSnapshot(
                scope.SceneKey,
                scope.WaypointIndex,
                nowFrame,
                reevaluateAfterFrame,
                shouldHold,
                canClearAfterExit,
                conflictMode,
                blockerVehicle);
        }

        private bool TryProjectVehicleToCurrentLocalSceneCoordinate(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            Entity vehicle,
            out float sceneCoordinate)
        {
            sceneCoordinate = 0f;
            if (vehicle == Entity.Null
                || scope.Line == Entity.Null
                || !TryGetLineTrackChain(scope.Line, localWaypoints, out LineTrackChain localChain))
            {
                return false;
            }

            int localProtectedIntervalIndex = scope.Scene.ProtectedIntervalIndex;
            BypassProtectedInterval localProtectedInterval = scope.Scene.ProtectedInterval;

            if (ResolveVehicleLine(vehicle) == scope.Line)
            {
                if (!TryProjectTrackModelRuntimePosition(vehicle, scope.Line, localWaypoints, localProtectedInterval, out TrackModelRuntimePosition localPosition)
                    || localPosition.Confidence < 0.6f)
                {
                    return false;
                }

                sceneCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                    localPosition,
                    localProtectedInterval,
                    includeApproachers: true,
                    out bool includeLocal);
                return includeLocal;
            }

            Entity expressLine = ResolveVehicleLine(vehicle);
            if (expressLine == Entity.Null)
                return false;

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                || !TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain))
            {
                return false;
            }

            EnsureTrackChainBypassPipelineReady(expressChain);
            PhysicalSharedWindowMatch sharedWindowMatch = GetPhysicalSharedWindowMatchCurrentFrame(
                localChain,
                localProtectedInterval,
                scope.CurrentBypassBuilding,
                expressChain);
            if (!sharedWindowMatch.Found || sharedWindowMatch.Ambiguous)
                return false;

            if (!TryResolveExpressConflictWindowForLocalConflict(
                    vehicle,
                    expressLine,
                    expressWaypoints,
                    expressChain,
                    localChain,
                    localProtectedIntervalIndex,
                    localProtectedInterval,
                    sharedWindowMatch,
                    out _,
                    out BypassProtectedInterval expressProtectedInterval,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            if (!TryProjectTrackModelRuntimePosition(vehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition)
                || expressPosition.Confidence < 0.6f)
            {
                return false;
            }

            if (!TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                    localChain,
                    localProtectedInterval,
                    scope.CurrentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    expressPosition.CurrentAtomIndex,
                    out GlobalSharedTrunkSegment selectedTrunkSegment))
            {
                return false;
            }

            RelativeToTrunkState expressTrunkState = BuildRelativeToTrunkStateFromRuntimePosition(
                expressPosition,
                expressChain,
                selectedTrunkSegment,
                useLocalSide: false);
            if (!selectedTrunkSegment.HasCanonicalDirection
                || !IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, selectedTrunkSegment))
            {
                return false;
            }

            sceneCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                expressPosition,
                expressProtectedInterval,
                scope.Scene.IntervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);
            return includeExpress;
        }

        private bool TryProjectLatchedBlockerToCurrentLocalSceneCoordinate(
            BypassControlScope scope,
            Entity blockerVehicle,
            BypassLatchedBlockerProjection latchedProjection,
            out float sceneCoordinate)
        {
            sceneCoordinate = 0f;
            if (!latchedProjection.Available
                || blockerVehicle == Entity.Null
                || scope.Line == Entity.Null
                || latchedProjection.SharedTrackVersion != m_SharedTrackIndexVersion
                || ResolveVehicleLine(blockerVehicle) != latchedProjection.ExpressLine)
            {
                return false;
            }

            Entity expressLine = latchedProjection.ExpressLine;
            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                || !TryGetLineTrackChain(expressLine, expressWaypoints, out LineTrackChain expressChain)
                || expressChain.Signature != latchedProjection.ExpressChainSignature)
            {
                return false;
            }

            if (!TryProjectTrackModelRuntimePosition(
                    blockerVehicle,
                    expressLine,
                    expressWaypoints,
                    latchedProjection.ExpressProtectedInterval,
                    out TrackModelRuntimePosition expressPosition)
                || expressPosition.Confidence < 0.6f)
            {
                return false;
            }

            RelativeToTrunkState expressTrunkState = BuildRelativeToTrunkStateFromRuntimePosition(
                expressPosition,
                expressChain,
                latchedProjection.SelectedTrunkSegment,
                useLocalSide: false);
            if (!latchedProjection.SelectedTrunkSegment.HasCanonicalDirection
                || !IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, latchedProjection.SelectedTrunkSegment))
            {
                return false;
            }

            sceneCoordinate = MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                expressPosition,
                latchedProjection.ExpressProtectedInterval,
                scope.Scene.IntervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);
            return includeExpress;
        }

        private bool TryIsLatchedBypassBlockerBeforeRelease(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            BypassConflictEpisode episode,
            Entity blockerVehicle,
            out bool blockerStillBeforeRelease)
        {
            blockerStillBeforeRelease = false;
            if (blockerVehicle == Entity.Null
                || scope.Line == Entity.Null)
            {
                return false;
            }

            float blockerSceneCoordinate;
            if (episode.HasLatchedBlockerProjection)
            {
                if (!TryProjectLatchedBlockerToCurrentLocalSceneCoordinate(
                        scope,
                        blockerVehicle,
                        episode.LatchedBlockerProjection,
                        out blockerSceneCoordinate))
                {
                    return false;
                }
            }
            else if (!TryProjectVehicleToCurrentLocalSceneCoordinate(scope, localWaypoints, blockerVehicle, out blockerSceneCoordinate))
            {
                return false;
            }

            blockerStillBeforeRelease = blockerSceneCoordinate <= scope.Scene.DepartureReleaseCoordinate;
            return true;
        }

        private static bool IsExpressAheadOfNearestQueuedLocalOnCurrentSceneAxis(
            float expressSceneCoordinate,
            float queuedLocalCoordinate)
        {
            const float orderEpsilon = 0.05f;
            return expressSceneCoordinate > queuedLocalCoordinate + orderEpsilon;
        }

        private bool HasBypassBuildingBetweenDistances(LineMileageModel model, float fromMetersExclusive, float toMetersExclusive)
        {
            if (model == null || model.BypassStopNodeDistances == null || model.BypassStopNodeDistances.Length == 0)
                return false;

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, toMetersExclusive);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            for (int i = 0; i < model.BypassStopNodeDistances.Length; i++)
            {
                float fromToNode = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, model.BypassStopNodeDistances[i]);
                if (fromToNode > 0f && fromToNode < corridorLength)
                    return true;
            }

            return false;
        }

        private bool TryBuildBypassCorridorNodeList(
            LineMileageModel model,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out List<CorridorNode> corridorNodes)
        {
            corridorNodes = null;
            if (model == null
                || !model.BuildingDistances.TryGetValue(currentBypassBuilding, out float startMeters)
                || !model.BuildingDistances.TryGetValue(nextBypassBuilding, out float endMeters))
            {
                return false;
            }

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, startMeters, endMeters);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            corridorNodes = new List<CorridorNode>();
            for (int i = 0; i < model.CorridorNodes.Count; i++)
            {
                CorridorNode node = model.CorridorNodes[i];
                float distanceFromStart = ForwardDistanceOnLoop(model.TotalDistanceMeters, startMeters, node.DistanceMeters);
                if (distanceFromStart <= 0f || distanceFromStart > corridorLength)
                    continue;
                corridorNodes.Add(node);
            }

            return corridorNodes.Count > 0;
        }

        private bool TryFindSharedBypassConflictNode(
            List<CorridorNode> localCorridorNodes,
            LineMileageModel expressModel,
            float expressCurrentMeters,
            out CorridorNode conflictNode,
            out float expressTargetMeters)
        {
            conflictNode = default;
            expressTargetMeters = 0f;
            if (localCorridorNodes == null || expressModel == null)
                return false;

            for (int i = 0; i < localCorridorNodes.Count; i++)
            {
                CorridorNode localNode = localCorridorNodes[i];
                if (localNode.Building == Entity.Null)
                    continue;
                if (!expressModel.BuildingDistances.TryGetValue(localNode.Building, out float candidateExpressMeters))
                    continue;

                float forward = ForwardDistanceOnLoop(expressModel.TotalDistanceMeters, expressCurrentMeters, candidateExpressMeters);
                if (!(forward > 0f) || forward == float.MaxValue)
                    continue;

                conflictNode = localNode;
                expressTargetMeters = candidateExpressMeters;
                return true;
            }

            return false;
        }

        private float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            float loopFrames = ReadLineLapCache(line);
            if (loopFrames > 0f)
                return loopFrames;

            if (TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile) && profile.m_BaseLoopFrames > 0f)
                return profile.m_BaseLoopFrames;

            return 0f;
        }

        private static float EstimateFramesForForwardDistance(float loopFrames, float totalDistanceMeters, float fromMeters, float toMeters)
        {
            if (!(loopFrames > 0f) || !(totalDistanceMeters > 0f))
                return float.MaxValue;

            float forwardDistance = ForwardDistanceOnLoop(totalDistanceMeters, fromMeters, toMeters);
            if (!(forwardDistance >= 0f) || forwardDistance == float.MaxValue)
                return float.MaxValue;

            return loopFrames * (forwardDistance / totalDistanceMeters);
        }

        private bool HasBypassWaypointBetweenDistances(
            DynamicBuffer<RouteWaypoint> waypoints,
            LineMileageModel model,
            float fromMetersExclusive,
            float toMetersExclusive)
        {
            if (model == null
                || model.TotalDistanceMeters <= 0f
                || model.BypassWaypointDistances == null
                || model.BypassWaypointDistances.Length == 0)
            {
                return false;
            }

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, toMetersExclusive);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            for (int waypointIndex = 0; waypointIndex < model.BypassWaypointDistances.Length; waypointIndex++)
            {
                float anchorMeters = model.BypassWaypointDistances[waypointIndex];
                float fromToAnchor = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, anchorMeters);
                if (fromToAnchor > 0f && fromToAnchor < corridorLength)
                    return true;
            }

            return false;
        }

        private bool TryFindNearestLocalVehicleInApproachSegment(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            float currentLocalSceneCoordinate,
            out Entity nearestVehicle,
            out float nearestVehicleMeters)
        {
            nearestVehicle = Entity.Null;
            nearestVehicleMeters = 0f;

            if (scope.Line == Entity.Null || scope.WaypointIndex < 0 || waypoints.Length == 0)
                return false;

            if (!TryGetLineTrackChain(scope.Line, waypoints, out LineTrackChain localChain))
                return false;

            EnsureTrackChainBypassPipelineReady(localChain);
            if (!TryResolveBypassProtectedInterval(localChain, waypoints, scope.WaypointIndex, out _, out BypassProtectedInterval localProtectedInterval))
                return false;

            int currentControlPointIndex = localProtectedInterval.StartControlPointIndex;
            if (currentControlPointIndex < 0 || currentControlPointIndex >= localChain.ControlPoints.Count)
                return false;

            Entity currentBuilding = localChain.ControlPoints[currentControlPointIndex].Building;
            int previousStationControlPointIndex = -1;
            for (int controlPointIndex = currentControlPointIndex - 1; controlPointIndex >= 0; controlPointIndex--)
            {
                ControlPointMarker marker = localChain.ControlPoints[controlPointIndex];
                if ((marker.Kind != ControlPointKind.Stop && marker.Kind != ControlPointKind.Bypass)
                    || marker.Building == Entity.Null
                    || marker.Building == currentBuilding)
                {
                    continue;
                }

                previousStationControlPointIndex = controlPointIndex;
                break;
            }

            if (previousStationControlPointIndex < 0)
                return false;

            float previousStationSceneCoordinate = MapAtomIndexToProtectedIntervalCoordinateExact(
                localProtectedInterval,
                localChain.ControlPoints[previousStationControlPointIndex].AtomIndex);
            float approachUpperBound = math.min(currentLocalSceneCoordinate, 0f);
            if (!(previousStationSceneCoordinate < approachUpperBound))
                return false;

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(scope.Line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            float bestSceneCoordinate = float.MinValue;
            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity otherVehicle = routeVehicles[i].m_Vehicle;
                if (otherVehicle == Entity.Null
                    || otherVehicle == scope.Vehicle
                    || !EntityManager.Exists(otherVehicle)
                    || !m_VehicleState.TryGetValue(otherVehicle, out VehicleState vehicleState)
                    || vehicleState != VehicleState.Running)
                {
                    continue;
                }

                if (!TryProjectTrackModelRuntimePosition(otherVehicle, scope.Line, waypoints, localProtectedInterval, out TrackModelRuntimePosition otherPosition)
                    || otherPosition.Confidence < 0.6f)
                {
                    continue;
                }

                float otherSceneCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                    otherPosition,
                    localProtectedInterval,
                    includeApproachers: true,
                    out bool includeOther);
                if (!includeOther
                    || otherSceneCoordinate < previousStationSceneCoordinate
                    || otherSceneCoordinate >= approachUpperBound)
                {
                    continue;
                }

                if (otherSceneCoordinate <= bestSceneCoordinate)
                    continue;

                bestSceneCoordinate = otherSceneCoordinate;
                nearestVehicle = otherVehicle;
                nearestVehicleMeters = otherSceneCoordinate;
            }

            return nearestVehicle != Entity.Null;
        }

        private bool FinalizeBypassDecision(
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
            if (shouldYield
                && ShouldShadowVetoLiveBypassYield(localVehicle, out string shadowReason))
            {
                shouldYield = false;
                blockerVehicle = Entity.Null;
                reason = "shadow-veto-" + shadowReason;
            }

            LogBypassDecisionOnce(localVehicle, currentBypassBuilding, nextBypassBuilding, shouldYield, reason, blockerVehicle);
            return shouldYield;
        }

        private void LogBypassDecisionOnce(
            Entity localVehicle,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            bool shouldYield,
            string reason,
            Entity blockerVehicle)
        {
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            if (localVehicle == Entity.Null)
                return;

            string decisionKey =
                (shouldYield ? "Y" : "N") + "|"
                + reason + "|"
                + currentBypassBuilding.Index + "|"
                + nextBypassBuilding.Index + "|"
                + blockerVehicle.Index;

            if (m_BypassDecisionLogCache.TryGetValue(localVehicle, out string previous) && previous == decisionKey)
                return;

            m_BypassDecisionLogCache[localVehicle] = decisionKey;
            Entity line = ResolveVehicleLine(localVehicle);
            string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
            log.Info("[待避判定] " + lineTag + " 车辆" + localVehicle.Index
                + " result=" + (shouldYield ? "yield" : "pass")
                + " reason=" + reason
                + (blockerVehicle != Entity.Null ? " blocker=" + blockerVehicle.Index : string.Empty));
        }

        private void LogQueuedLocalBypassOverrideOnce(
            Entity localVehicle,
            Entity localLine,
            Entity blockerVehicle,
            string result,
            string reason,
            float expressMeters = float.NaN,
            float currentLocalMeters = float.NaN,
            float queuedLocalMeters = float.NaN)
        {
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            string key = result + "|" + reason + "|" + blockerVehicle.Index;
            if (!float.IsNaN(expressMeters))
                key += "|e=" + math.round(expressMeters).ToString();
            if (!float.IsNaN(currentLocalMeters))
                key += "|l=" + math.round(currentLocalMeters).ToString();
            if (!float.IsNaN(queuedLocalMeters))
                key += "|q=" + math.round(queuedLocalMeters).ToString();

            string lineTag = localLine != Entity.Null ? "线路" + localLine.Index : "线路?";
            string message = "[待避同线复核] " + lineTag + " 车辆" + localVehicle.Index
                + " result=" + result
                + " reason=" + reason
                + (blockerVehicle != Entity.Null ? " blocker=" + blockerVehicle.Index : string.Empty);
            if (!float.IsNaN(expressMeters))
                message += " expressM=" + expressMeters.ToString("0.0");
            if (!float.IsNaN(currentLocalMeters))
                message += " localM=" + currentLocalMeters.ToString("0.0");
            if (!float.IsNaN(queuedLocalMeters))
                message += " queuedM=" + queuedLocalMeters.ToString("0.0");

            LogVehicleStateOnce(m_BypassQueuedLocalOverrideLogCache, localVehicle, key, message);
        }

        private string FormatBypassNodeLabel(Entity building)
        {
            if (building == Entity.Null)
                return "-";

            try
            {
                string name = m_NameSystem.GetRenderedLabelName(building);
                if (!string.IsNullOrWhiteSpace(name))
                    return name + "#" + building.Index;
            }
            catch
            {
            }

            return "建筑#" + building.Index;
        }

        private bool ShouldHoldLocalVehicleForExpressBypass(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out Entity blockerVehicle,
            out string decisionReason,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            blockerVehicle = Entity.Null;
            decisionReason = string.Empty;
            hasLatchedBlockerProjection = false;
            latchedBlockerProjection = default;
            if (localLine == Entity.Null
                || !IsWorkbenchTimetableApplied(localLine)
                || !IsAppliedWorkbenchLocalLine(localLine))
            {
                decisionReason = "local-line-invalid";
                return FinalizeBypassDecision(localVehicle, localLine, localWaypoints, currentWaypointIndex, Entity.Null, Entity.Null, false, "local-line-invalid", Entity.Null);
            }

            if (!TryGetBypassControlScope(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out BypassControlScope scope,
                    out string failureReason))
            {
                decisionReason = failureReason ?? string.Empty;
                return FinalizeBypassDecision(localVehicle, localLine, localWaypoints, currentWaypointIndex, Entity.Null, Entity.Null, false, failureReason, Entity.Null);
            }

            return ShouldHoldLocalVehicleForExpressBypass(
                scope,
                localWaypoints,
                nowFrame,
                out blockerVehicle,
                out decisionReason,
                out hasLatchedBlockerProjection,
                out latchedBlockerProjection);
        }

        private bool ShouldHoldLocalVehicleForExpressBypass(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            uint nowFrame,
            out Entity blockerVehicle,
            out string decisionReason,
            out bool hasLatchedBlockerProjection,
            out BypassLatchedBlockerProjection latchedBlockerProjection)
        {
            blockerVehicle = Entity.Null;
            decisionReason = string.Empty;
            hasLatchedBlockerProjection = false;
            latchedBlockerProjection = default;
            if (!TryGetTrackModelBypassBaseline(
                    scope,
                    localWaypoints,
                    nowFrame,
                    out bool shouldYield,
                    out string trackModelReason,
                    out Entity trackModelBlocker,
                    out hasLatchedBlockerProjection,
                    out latchedBlockerProjection))
            {
                decisionReason = "track-model-decision-unavailable";
                return FinalizeBypassDecision(scope.Vehicle, scope.Line, localWaypoints, scope.WaypointIndex, scope.CurrentBypassBuilding, scope.NextBypassBuilding, false, "track-model-decision-unavailable", Entity.Null);
            }

            if (shouldYield
                && trackModelBlocker != Entity.Null
                && trackModelReason == "track-model-same-direction-shared-express-approaching")
            {
                if (TryShouldReleaseForQueuedLocalAhead(
                        scope,
                        localWaypoints,
                        trackModelBlocker,
                        out float expressSceneCoordinate,
                        out float localSceneCoordinate,
                        out float queuedLocalMeters))
                {
                    LogQueuedLocalBypassOverrideOnce(
                        scope.Vehicle,
                        scope.Line,
                        trackModelBlocker,
                        "release",
                        "express-behind-nearest-queued-local",
                        expressSceneCoordinate,
                        localSceneCoordinate,
                        queuedLocalMeters);
                    blockerVehicle = Entity.Null;
                    decisionReason = "express-behind-nearest-queued-local";
                    return FinalizeBypassDecision(scope.Vehicle, scope.Line, localWaypoints, scope.WaypointIndex, scope.CurrentBypassBuilding, scope.NextBypassBuilding, false, "express-behind-nearest-queued-local", Entity.Null);
                }
            }

            blockerVehicle = trackModelBlocker;
            decisionReason = trackModelReason ?? string.Empty;
            return FinalizeBypassDecision(scope.Vehicle, scope.Line, localWaypoints, scope.WaypointIndex, scope.CurrentBypassBuilding, scope.NextBypassBuilding, shouldYield, trackModelReason, trackModelBlocker);
        }

        private bool TryShouldReleaseForQueuedLocalAhead(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            Entity blockerVehicle,
            out float expressSceneCoordinate,
            out float localSceneCoordinate,
            out float queuedLocalMeters)
        {
            expressSceneCoordinate = float.NaN;
            localSceneCoordinate = float.NaN;
            queuedLocalMeters = float.NaN;

            if (blockerVehicle == Entity.Null)
                return false;

            if (IsExpressBlockerStillWithinBypassStation(blockerVehicle, scope.CurrentBypassBuilding))
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "blocker-still-in-bypass-station");
                return false;
            }

            if (!TryProjectVehicleToCurrentLocalSceneCoordinate(
                    scope,
                    localWaypoints,
                    scope.Vehicle,
                    out localSceneCoordinate))
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "local-scene-projection-failed");
                return false;
            }

            bool hasQueuedLocalInApproach = TryFindNearestLocalVehicleInApproachSegment(
                scope,
                localWaypoints,
                localSceneCoordinate,
                out _,
                out queuedLocalMeters);
            if (!hasQueuedLocalInApproach)
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "no-queued-local-in-approach");
                return false;
            }

            if (!TryProjectVehicleToCurrentLocalSceneCoordinate(
                    scope,
                    localWaypoints,
                    blockerVehicle,
                    out expressSceneCoordinate))
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "express-scene-projection-failed",
                    queuedLocalMeters: queuedLocalMeters);
                return false;
            }

            if (IsExpressAheadOfNearestQueuedLocalOnCurrentSceneAxis(expressSceneCoordinate, queuedLocalMeters))
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "express-ahead-of-nearest-queued-local",
                    expressSceneCoordinate,
                    localSceneCoordinate,
                    queuedLocalMeters);
                return false;
            }

            return true;
        }

        private bool IsExpressBlockerStillWithinBypassStation(Entity blockerVehicle, Entity localCurrentBypassBuilding)
        {
            if (blockerVehicle == Entity.Null || localCurrentBypassBuilding == Entity.Null)
                return false;

            Entity blockerLine = ResolveVehicleLine(blockerVehicle);
            if (blockerLine == Entity.Null)
                return false;

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(blockerLine, out DynamicBuffer<RouteWaypoint> blockerWaypoints))
                return false;

            return IsVehicleWithinBypassStationPhysicalContext(
                blockerVehicle,
                blockerLine,
                blockerWaypoints,
                localCurrentBypassBuilding);
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
            m_BypassPerfProbeBaselineCalls++;
            shouldYield = false;
            trackModelReason = string.Empty;
            trackModelBlocker = Entity.Null;
            hasLatchedBlockerProjection = false;
            latchedBlockerProjection = default;

            if (!TryEvaluateBypassTrackModelShadowDecision(
                    scope.Vehicle,
                    scope.Line,
                    localWaypoints,
                    scope.WaypointIndex,
                    nowFrame,
                    out BypassTrackModelShadowDecision liveDecision)
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
    }
}
