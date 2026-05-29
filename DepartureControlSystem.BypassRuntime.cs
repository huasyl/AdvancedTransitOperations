using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private bool m_BypassRuntimeEnabled = true;

        private readonly struct QueuedLocalReleaseScope
        {
            public readonly LineTrackChain LocalChain;
            public readonly BypassProtectedInterval LocalProtectedInterval;
            public readonly float PreviousStationSceneCoordinate;

            public QueuedLocalReleaseScope(
                LineTrackChain localChain,
                BypassProtectedInterval localProtectedInterval,
                float previousStationSceneCoordinate)
            {
                LocalChain = localChain;
                LocalProtectedInterval = localProtectedInterval;
                PreviousStationSceneCoordinate = previousStationSceneCoordinate;
            }
        }

        private void SetBypassYieldState(Entity vehicle, Entity blocker, string lineTag, string stateTag, Entity holdStation = default, int waypointIndex = -1)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            if (m_BypassDecision.TryGetLatchedBlocker(vehicle, out Entity previousBlocker) && previousBlocker == blocker)
                return;

            m_BypassReleaseDiagLogCache.Remove(vehicle);
            m_BypassDecision.SetBlocker(vehicle, blocker);
            RecordRuntimeObservationBypassHoldStart(vehicle, blocker, holdStation, waypointIndex, m_SimulationSystem.frameIndex, stateTag);
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            log.Info("[待避] " + lineTag + " 车辆" + vehicle.Index
                + " state=" + stateTag
                + " 等待快车" + blocker.Index + " 先行");
        }

        internal void ClearBypassYieldState(Entity vehicle, string releaseReason = null)
        {
            if (vehicle == Entity.Null || !m_BypassDecision.TryGetLatchedBlocker(vehicle, out Entity blocker))
                return;

            m_BypassDecision.ClearBlocker(vehicle);
            m_BypassDecision.Remove(vehicle, BypassEntryKind.Cadence);
            m_BypassDecision.Remove(vehicle, BypassEntryKind.Episode);
            RecordRuntimeObservationBypassHoldRelease(vehicle, blocker, m_SimulationSystem.frameIndex, releaseReason);
            LogVehicleStateOnce(
                m_BypassReleaseDiagLogCache,
                vehicle,
                "release|blocker=" + blocker.Index + "|reason=" + (releaseReason ?? "-"),
                "[待避释放诊断] vehicle=" + vehicle.Index
                    + " blocker=" + blocker.Index
                    + " reason=" + (releaseReason ?? "-")
                    + " frame=" + m_SimulationSystem.frameIndex);
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
            m_BypassDecision.Clear();
            m_BypassDecisionLogCache.Clear();
            m_BypassDepartureGateLogCache.Clear();
            m_BypassHoldFrameLogCache.Clear();
            m_BypassReleaseDiagLogCache.Clear();
            m_BypassExitClearLogCache.Clear();
            m_BypassQueuedLocalOverrideLogCache.Clear();
            m_PerfProbeSceneExpressLineLastQueryFrame.Clear();
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
                    m_BypassDecision.Remove(vehicle, BypassEntryKind.Cadence);
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
                m_BypassDecision.Remove(vehicle, BypassEntryKind.Cadence);
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
                m_BypassDecision.Remove(localVehicle, BypassEntryKind.Scope);
                failureReason = "local-line-invalid";
                return false;
            }

            if (m_BypassDecision.Get(localVehicle, out BypassControlScopeCacheEntry cachedScope)
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
                m_BypassDecision.Remove(localVehicle, BypassEntryKind.Scope);
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
            m_BypassDecision.Put(localVehicle, new BypassControlScopeCacheEntry(
                localLine,
                currentWaypointIndex,
                scope));
            return true;
        }

        private bool TryProjectVehicleToCurrentLocalSceneCoordinate(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            Entity vehicle,
            out float sceneCoordinate)
        {
            if (!TryGetLineTrackChain(scope.Line, localWaypoints, out LineTrackChain localChain))
            {
                sceneCoordinate = 0f;
                return false;
            }

            return TryProjectVehicleToCurrentLocalSceneCoordinate(
                scope,
                localWaypoints,
                localChain,
                scope.Scene.ProtectedIntervalIndex,
                scope.Scene.ProtectedInterval,
                vehicle,
                out sceneCoordinate);
        }

        private bool TryProjectVehicleToCurrentLocalSceneCoordinate(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            Entity vehicle,
            out float sceneCoordinate)
        {
            sceneCoordinate = 0f;
            if (vehicle == Entity.Null
                || scope.Line == Entity.Null
                || localChain == null)
            {
                return false;
            }

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

            int localTraversalPhaseIndex = TryResolveStaticTraversalPhaseWindow(
                localChain,
                localProtectedInterval.StartAtomIndex,
                localProtectedInterval.EndAtomIndexExclusive,
                out int resolvedLocalTraversalPhaseIndex,
                out _,
                out _)
                ? resolvedLocalTraversalPhaseIndex
                : -1;
            if (!TryFindBestCurrentForwardSceneSameDirectionTrunkSegment(
                    localChain,
                    localProtectedInterval,
                    scope.CurrentBypassBuilding,
                    expressChain,
                    expressProtectedInterval,
                    localTraversalPhaseIndex,
                    expressPosition.CurrentAtomIndex,
                    expressPosition.TraversalPhaseIndex,
                    out GlobalSharedTrunkSegment selectedTrunkSegment))
            {
                return false;
            }

            RelativeToTrunkState expressTrunkState = ResolveVehicleTrunkTravelState(
                expressPosition,
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

        private bool TryBuildQueuedLocalReleaseScope(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> waypoints,
            out QueuedLocalReleaseScope releaseScope)
        {
            releaseScope = default;
            if (scope.Line == Entity.Null || scope.WaypointIndex < 0 || waypoints.Length == 0)
                return false;

            if (!TryGetLineTrackChain(scope.Line, waypoints, out LineTrackChain localChain))
                return false;

            EnsureTrackChainBypassPipelineReady(localChain);
            BypassProtectedInterval localProtectedInterval = scope.Scene.ProtectedInterval;
            int currentControlPointIndex = localProtectedInterval.StartControlPointIndex;
            if (currentControlPointIndex < 0 || currentControlPointIndex >= localChain.ControlPoints.Count)
                return false;

            Entity currentBuilding = scope.CurrentBypassBuilding != Entity.Null
                ? scope.CurrentBypassBuilding
                : localChain.ControlPoints[currentControlPointIndex].Building;
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
            releaseScope = new QueuedLocalReleaseScope(
                localChain,
                localProtectedInterval,
                previousStationSceneCoordinate);
            return true;
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

            RelativeToTrunkState expressTrunkState = ResolveVehicleTrunkTravelState(
                expressPosition,
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
            QueuedLocalReleaseScope releaseScope,
            DynamicBuffer<RouteWaypoint> waypoints,
            float currentLocalSceneCoordinate,
            out Entity nearestVehicle,
            out float nearestVehicleMeters)
        {
            nearestVehicle = Entity.Null;
            nearestVehicleMeters = 0f;

            if (scope.Line == Entity.Null || scope.WaypointIndex < 0 || waypoints.Length == 0)
                return false;
            float approachUpperBound = math.min(currentLocalSceneCoordinate, 0f);
            if (!(releaseScope.PreviousStationSceneCoordinate < approachUpperBound))
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            EnsureLineBypassExecutionModeReady(releaseScope.LocalChain, waypoints);
            if (releaseScope.LocalChain.ExecutionMode == BypassExecutionMode.ComplexLineModel
                && TryGetLineOrderedRuntimeState(scope.Line, waypoints, nowFrame, out LineOrderedRuntimeState orderedState)
                && TryFindNearestOrderedLocalVehicleInApproachSegment(
                    scope,
                    releaseScope,
                    orderedState,
                    approachUpperBound,
                    out nearestVehicle,
                    out nearestVehicleMeters))
            {
                return true;
            }

            float bestSceneCoordinate = float.MinValue;
            if (TryGetLineRunningVehicleFrameSnapshot(scope.Line, waypoints, nowFrame, out LineRunningVehicleFrameSnapshot runningSnapshot))
            {
                for (int i = 0; i < runningSnapshot.Vehicles.Count; i++)
                {
                    LineRunningVehicleSnapshot runningVehicle = runningSnapshot.Vehicles[i];
                    Entity otherVehicle = runningVehicle.Vehicle;
                    if (otherVehicle == Entity.Null
                        || otherVehicle == scope.Vehicle
                        || !EntityManager.Exists(otherVehicle))
                    {
                        continue;
                    }

                    if (!TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
                            runningVehicle,
                            releaseScope.LocalProtectedInterval,
                            out TrackModelRuntimePosition otherPosition)
                        || otherPosition.Confidence < 0.6f)
                    {
                        continue;
                    }

                    float otherSceneCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                        otherPosition,
                        releaseScope.LocalProtectedInterval,
                        includeApproachers: true,
                        out bool includeOther);
                    if (!includeOther
                        || otherSceneCoordinate < releaseScope.PreviousStationSceneCoordinate
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

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(scope.Line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity otherVehicle = routeVehicles[i].m_Vehicle;
                if (otherVehicle == Entity.Null
                    || otherVehicle == scope.Vehicle
                    || !EntityManager.Exists(otherVehicle)
                    || !m_VehicleView.TryGetState(otherVehicle, out VehicleState vehicleState)
                    || vehicleState != VehicleState.Running)
                {
                    continue;
                }

                if (!TryProjectTrackModelRuntimePosition(otherVehicle, scope.Line, waypoints, releaseScope.LocalProtectedInterval, out TrackModelRuntimePosition otherPosition)
                    || otherPosition.Confidence < 0.6f)
                {
                    continue;
                }

                float otherSceneCoordinate = MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                    otherPosition,
                    releaseScope.LocalProtectedInterval,
                    includeApproachers: true,
                    out bool includeOther);
                if (!includeOther
                    || otherSceneCoordinate < releaseScope.PreviousStationSceneCoordinate
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

        private bool TryFindNearestOrderedLocalVehicleInApproachSegment(
            BypassControlScope scope,
            QueuedLocalReleaseScope releaseScope,
            LineOrderedRuntimeState orderedState,
            float approachUpperBound,
            out Entity nearestVehicle,
            out float nearestVehicleMeters)
        {
            nearestVehicle = Entity.Null;
            nearestVehicleMeters = 0f;
            if (orderedState == null || orderedState.Entries.Count == 0)
                return false;

            OrderedLineVehicleEntry currentEntry = default;
            bool foundCurrentEntry = false;
            for (int i = 0; i < orderedState.Entries.Count; i++)
            {
                OrderedLineVehicleEntry candidate = orderedState.Entries[i];
                if (candidate.Vehicle != scope.Vehicle)
                    continue;

                currentEntry = candidate;
                foundCurrentEntry = true;
                break;
            }

            if (!foundCurrentEntry)
                return false;

            float absoluteLowerBound = releaseScope.LocalProtectedInterval.StartAtomIndex + releaseScope.PreviousStationSceneCoordinate;
            float absoluteUpperBound = releaseScope.LocalProtectedInterval.StartAtomIndex + approachUpperBound;
            if (!(absoluteLowerBound < absoluteUpperBound))
                return false;

            for (int phaseRangeIndex = 0; phaseRangeIndex < orderedState.PhaseRanges.Count; phaseRangeIndex++)
            {
                OrderedLinePhaseRange phaseRange = orderedState.PhaseRanges[phaseRangeIndex];
                if (phaseRange.TraversalPhaseIndex != currentEntry.TraversalPhaseIndex)
                    continue;

                for (int entryIndex = phaseRange.EndEntryIndexExclusive - 1; entryIndex >= phaseRange.StartEntryIndex; entryIndex--)
                {
                    OrderedLineVehicleEntry candidate = orderedState.Entries[entryIndex];
                    if (candidate.Vehicle == scope.Vehicle)
                        continue;
                    if (candidate.OwnLineAtomCoordinate >= absoluteUpperBound)
                        continue;
                    if (candidate.OwnLineAtomCoordinate < absoluteLowerBound)
                        break;

                    nearestVehicle = candidate.Vehicle;
                    nearestVehicleMeters = candidate.OwnLineAtomCoordinate - releaseScope.LocalProtectedInterval.StartAtomIndex;
                    return true;
                }

                break;
            }

            return false;
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
                && CanClearBypassYieldAfterStationExit(localVehicle, localLine, localWaypoints, currentWaypointIndex))
            {
                shouldYield = false;
                blockerVehicle = Entity.Null;
                reason = "local-left-bypass-station";
            }

            if (shouldYield
                && ShouldTrackModelVetoLiveBypassYield(localVehicle, out string trackModelReason))
            {
                shouldYield = false;
                blockerVehicle = Entity.Null;
                reason = "track-model-veto-" + trackModelReason;
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
            if (!TryBuildQueuedLocalReleaseScope(scope, localWaypoints, out QueuedLocalReleaseScope releaseScope))
            {
                LogQueuedLocalBypassOverrideOnce(
                    scope.Vehicle,
                    scope.Line,
                    blockerVehicle,
                    "skip",
                    "queued-local-release-scope-failed");
                return false;
            }

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
                    releaseScope.LocalChain,
                    scope.Scene.ProtectedIntervalIndex,
                    releaseScope.LocalProtectedInterval,
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
                releaseScope,
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
                    releaseScope.LocalChain,
                    scope.Scene.ProtectedIntervalIndex,
                    releaseScope.LocalProtectedInterval,
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
    }
}
