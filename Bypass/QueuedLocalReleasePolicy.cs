using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Bypass
{
    internal sealed partial class AdmissionService
    {
        private readonly Dictionary<Entity, string> m_GateDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_QueuedLocalOverrideLogCache = new Dictionary<Entity, string>();

        private static bool IsBypassAdmissionLoggingEnabled() => false;

        private static float ForwardDistanceOnLoop(float totalDistanceMeters, float fromMeters, float toMeters)
        {
            if (!(totalDistanceMeters > 0f))
                return float.MaxValue;

            float delta = toMeters - fromMeters;
            while (delta < 0f)
                delta += totalDistanceMeters;
            return delta;
        }

        private bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile)
        {
            return m_Runtime.TryGetLineTimeProfile(line, waypoints, out profile);
        }

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

        internal bool TryGetBypassControlScope(
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
                Remove(localVehicle, BypassEntryKind.Scope);
                failureReason = "local-line-invalid";
                return false;
            }

            if (Get(localVehicle, out BypassControlScopeCacheEntry cachedScope)
                && cachedScope.Line == localLine
                && cachedScope.WaypointIndex == currentWaypointIndex)
            {
                scope = cachedScope.Scope;
                return true;
            }

            if (!m_Runtime.TrackModel.TryGetLocalSceneDefinition(
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out _,
                    out SceneDefinition sceneDefinition))
            {
                Remove(localVehicle, BypassEntryKind.Scope);
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
            Put(localVehicle, new BypassControlScopeCacheEntry(
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
            if (!m_Runtime.TrackModel.TryGetChainForLine(scope.Line, localWaypoints, out LineTrackChain localChain))
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

            if (m_Runtime.ResolveLine(vehicle) == scope.Line)
            {
                if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(vehicle, scope.Line, localWaypoints, localProtectedInterval, out TrackModelRuntimePosition localPosition)
                    || localPosition.Confidence < 0.6f)
                {
                    return false;
                }

                sceneCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
                    localPosition,
                    localProtectedInterval,
                    includeApproachers: true,
                    out bool includeLocal);
                return includeLocal;
            }

            Entity expressLine = m_Runtime.ResolveLine(vehicle);
            if (expressLine == Entity.Null)
                return false;

            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                || !m_Runtime.TrackModel.TryGetChainForLine(expressLine, expressWaypoints, out LineTrackChain expressChain))
            {
                return false;
            }

            m_Runtime.TrackModel.EnsureTrackChainBypassPipelineReady(expressChain);
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

            if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(vehicle, expressLine, expressWaypoints, expressProtectedInterval, out TrackModelRuntimePosition expressPosition)
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
                || !AdmissionService.IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !AdmissionService.IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, selectedTrunkSegment))
            {
                return false;
            }

            sceneCoordinate = TrackProjectionService.MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
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

            if (!m_Runtime.TrackModel.TryGetChainForLine(scope.Line, waypoints, out LineTrackChain localChain))
                return false;

            m_Runtime.TrackModel.EnsureTrackChainBypassPipelineReady(localChain);
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

            float previousStationSceneCoordinate = TrackProjectionService.MapAtomIndexToProtectedIntervalCoordinateExact(
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
                || latchedProjection.SharedTrackVersion != m_Runtime.TrackModel.SharedIndexVersion
                || m_Runtime.ResolveLine(blockerVehicle) != latchedProjection.ExpressLine)
            {
                return false;
            }

            Entity expressLine = latchedProjection.ExpressLine;
            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(expressLine, out DynamicBuffer<RouteWaypoint> expressWaypoints)
                || !m_Runtime.TrackModel.TryGetChainForLine(expressLine, expressWaypoints, out LineTrackChain expressChain)
                || expressChain.Signature != latchedProjection.ExpressChainSignature)
            {
                return false;
            }

            if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(
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
                || !AdmissionService.IsRelativeToTrunkStateBlockerEligible(expressTrunkState)
                || !AdmissionService.IsRelativeToTrunkStateDirectionCompatibleWithLocal(expressTrunkState, latchedProjection.SelectedTrunkSegment))
            {
                return false;
            }

            sceneCoordinate = TrackProjectionService.MapRuntimePositionToReferenceProtectedIntervalCoordinateExact(
                expressPosition,
                latchedProjection.ExpressProtectedInterval,
                scope.Scene.IntervalDisplayLength,
                includeApproachers: true,
                out bool includeExpress);
            return includeExpress;
        }

        internal bool TryEvaluateLatchedBlockerBeforeRelease(
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

        private bool HasBypassBuildingBetweenDistances(BypassLineDistanceModel model, float fromMetersExclusive, float toMetersExclusive)
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
            BypassLineDistanceModel model,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out List<BypassCorridorNode> corridorNodes)
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

            corridorNodes = new List<BypassCorridorNode>();
            for (int i = 0; i < model.CorridorNodes.Count; i++)
            {
                BypassCorridorNode node = model.CorridorNodes[i];
                float distanceFromStart = ForwardDistanceOnLoop(model.TotalDistanceMeters, startMeters, node.DistanceMeters);
                if (distanceFromStart <= 0f || distanceFromStart > corridorLength)
                    continue;
                corridorNodes.Add(node);
            }

            return corridorNodes.Count > 0;
        }

        private bool TryFindSharedBypassConflictNode(
            List<BypassCorridorNode> localCorridorNodes,
            BypassLineDistanceModel expressModel,
            float expressCurrentMeters,
            out BypassCorridorNode conflictNode,
            out float expressTargetMeters)
        {
            conflictNode = default;
            expressTargetMeters = 0f;
            if (localCorridorNodes == null || expressModel == null)
                return false;

            for (int i = 0; i < localCorridorNodes.Count; i++)
            {
                BypassCorridorNode localNode = localCorridorNodes[i];
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

        internal float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
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
            BypassLineDistanceModel model,
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

            uint nowFrame = m_Runtime.Frame;
            EnsureLineBypassExecutionModeReady(releaseScope.LocalChain, waypoints);
            if (ResolveLineBypassExecutionMode(releaseScope.LocalChain) == BypassExecutionMode.ComplexLineModel
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
                        || !m_Runtime.EntityManager.Exists(otherVehicle))
                    {
                        continue;
                    }

                    if (!TrackProjectionService.TryBuildTrackModelRuntimePositionFromLineRunningSnapshot(
                            runningVehicle,
                            releaseScope.LocalProtectedInterval,
                            out TrackModelRuntimePosition otherPosition)
                        || otherPosition.Confidence < 0.6f)
                    {
                        continue;
                    }

                    float otherSceneCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
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

            var routeVehicleBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(scope.Line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity otherVehicle = routeVehicles[i].m_Vehicle;
                if (otherVehicle == Entity.Null
                    || otherVehicle == scope.Vehicle
                    || !m_Runtime.EntityManager.Exists(otherVehicle)
                    || !m_Runtime.TryGetVehicleRuntimeState(otherVehicle, out VehicleState vehicleState)
                    || vehicleState != VehicleState.Running)
                {
                    continue;
                }

                if (!m_Runtime.TrackProjection.TryProjectTrackModelRuntimePosition(otherVehicle, scope.Line, waypoints, releaseScope.LocalProtectedInterval, out TrackModelRuntimePosition otherPosition)
                    || otherPosition.Confidence < 0.6f)
                {
                    continue;
                }

                float otherSceneCoordinate = TrackProjectionService.MapRuntimePositionToOwnProtectedIntervalCoordinateExact(
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
            if (shouldYield
                && ShouldClearHoldAfterStationExit(localVehicle, localLine, localWaypoints, currentWaypointIndex))
            {
                shouldYield = false;
                blockerVehicle = Entity.Null;
                reason = "local-left-bypass-station";
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
            if (!IsBypassAdmissionLoggingEnabled())
                return;

            if (localVehicle == Entity.Null)
                return;

            string decisionKey =
                (shouldYield ? "Y" : "N") + "|"
                + reason + "|"
                + currentBypassBuilding.Index + "|"
                + nextBypassBuilding.Index + "|"
                + blockerVehicle.Index;

            if (m_GateDecisionLogCache.TryGetValue(localVehicle, out string previous) && previous == decisionKey)
                return;

            m_GateDecisionLogCache[localVehicle] = decisionKey;
            Entity line = m_Runtime.ResolveLine(localVehicle);
            string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
            m_Runtime.Log.Info("[待避判定] " + lineTag + " 车辆" + localVehicle.Index
                + " result=" + (shouldYield ? "yield" : "pass")
                + " reason=" + reason
                + (blockerVehicle != Entity.Null ? " blocker=" + blockerVehicle.Index : string.Empty));
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
            if (!IsBypassAdmissionLoggingEnabled())
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

            m_Runtime.LogVehicleStateOnce(m_QueuedLocalOverrideLogCache, localVehicle, key, message);
        }

        internal bool ShouldReleaseForQueuedLocalAhead(
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

        internal bool IsExpressBlockerStillWithinBypassStation(Entity blockerVehicle, Entity localCurrentBypassBuilding)
        {
            if (blockerVehicle == Entity.Null || localCurrentBypassBuilding == Entity.Null)
                return false;

            Entity blockerLine = m_Runtime.ResolveLine(blockerVehicle);
            if (blockerLine == Entity.Null)
                return false;

            var routeWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(blockerLine, out DynamicBuffer<RouteWaypoint> blockerWaypoints))
                return false;

            return IsVehicleWithinBypassStationPhysicalContext(
                blockerVehicle,
                blockerLine,
                blockerWaypoints,
                localCurrentBypassBuilding);
        }


    }
}
