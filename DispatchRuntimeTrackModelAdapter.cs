using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem : ITrackModelRuntimeContext, ITrackProjectionRuntimeContext, IBypassAdmissionRuntimeContext, IRuntimeContext
    {
        private const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;

        private TrackModelService m_TrackModel = null!;
        private TrackProjectionService m_TrackProjection = null!;
        private RuntimeFacade m_Bypass = null!;

        public string BuildDevSightLaneTooltipSummary(Entity laneEntity)
        {
            return m_TrackModel.BuildDevSightLaneTooltipSummary(laneEntity);
        }

        internal TrackModelService TrackModel => m_TrackModel;
        internal TrackProjectionService TrackProjection => m_TrackProjection;
        internal RuntimeFacade Bypass => m_Bypass;

        EntityManager ITrackModelRuntimeContext.EntityManager => EntityManager;
        TimedLogger ITrackModelRuntimeContext.Log => log;
        uint ITrackModelRuntimeContext.FrameIndex => m_SimulationSystem.frameIndex;
        int ITrackModelRuntimeContext.ManagedVehicleCount => m_VehicleView.Count;
        IEnumerable<KeyValuePair<string, AppliedWorkbenchLineState>> ITrackModelRuntimeContext.AppliedWorkbenchLines => m_AppliedWorkbenchLines;

        NativeArray<Entity> ITrackModelRuntimeContext.GetLineEntities(Allocator allocator)
        {
            return m_LineQuery.ToEntityArray(allocator);
        }

        BufferLookup<T> ITrackModelRuntimeContext.GetBufferLookup<T>(bool isReadOnly)
        {
            return GetBufferLookup<T>(isReadOnly);
        }

        bool ITrackModelRuntimeContext.IsBypassStation(Entity building)
        {
            return m_SelectionPanel.IsBypassStation(building);
        }

        bool ITrackModelRuntimeContext.TryGetRenderedLabelName(Entity entity, out string name)
        {
            name = string.Empty;
            if (entity == Entity.Null)
                return false;

            try
            {
                name = m_NameSystem.GetRenderedLabelName(entity);
                return !string.IsNullOrWhiteSpace(name);
            }
            catch
            {
                name = string.Empty;
                return false;
            }
        }

        bool ITrackModelRuntimeContext.TryGetCustomLineName(Entity line, out string customName)
        {
            customName = string.Empty;
            if (line == Entity.Null)
                return false;

            try
            {
                return m_NameSystem.TryGetCustomName(line, out customName)
                    && !string.IsNullOrWhiteSpace(customName);
            }
            catch
            {
                customName = string.Empty;
                return false;
            }
        }

        bool ITrackModelRuntimeContext.TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => TryGetLineTimeProfile(line, waypoints, out profile);
        float ITrackModelRuntimeContext.GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, TransportLineData prefabLineData) => GetProfileWaypointStopFrames(line, waypoints, waypointIndex, prefabLineData);
        float ITrackModelRuntimeContext.GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            return TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile) && profile.m_BaseLoopFrames > 0f
                ? profile.m_BaseLoopFrames
                : 0f;
        }
        float ITrackModelRuntimeContext.ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex) => ComputeDepartureToWaypointFramesFromProfile(profile, fromWaypointIndex, targetWaypointIndex);
        Entity ITrackModelRuntimeContext.ResolveWorkbenchStopEntity(Entity waypoint) => ResolveWorkbenchStopEntity(waypoint);
        Entity ITrackModelRuntimeContext.FindTransportStationFromStop(Entity stop) => FindTransportStationFromStop(stop);
        Entity ITrackModelRuntimeContext.ResolvePassingStationBuilding(Entity entity) => ResolvePassingStationBuilding(entity);
        bool ITrackModelRuntimeContext.IsAppliedWorkbenchLocalLine(Entity line) => IsAppliedWorkbenchLocalLine(line);
        bool ITrackModelRuntimeContext.IsAppliedWorkbenchExpressLine(Entity line) => IsAppliedWorkbenchExpressLine(line);
        bool ITrackModelRuntimeContext.TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        Entity ITrackModelRuntimeContext.GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => GetBypassBuildingForWaypoint(waypoints, waypointIndex);
        Entity ITrackModelRuntimeContext.GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => GetStationBuildingForWaypoint(waypoints, waypointIndex);
        bool ITrackModelRuntimeContext.TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex) => TryFindWaypointIndexForBypassBuilding(waypoints, building, startIndexInclusive, out waypointIndex);
        bool ITrackModelRuntimeContext.TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex) => TryFindFutureSharedCorridorWaypoint(expressWaypoints, localCorridorWaypoints, startIndexInclusive, endIndexInclusive, out expressWaypointIndex, out localWaypointIndex);
        Dictionary<Entity, int> ITrackModelRuntimeContext.BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding) => BuildLocalBypassCorridorWaypointMap(waypoints, currentWaypointIndex, nextBypassWaypointIndex, currentBypassBuilding);
        bool ITrackModelRuntimeContext.TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries) => TryCollectTurnbackStationBoundaries(chain, stationBoundaries);
        bool ITrackModelRuntimeContext.TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary);
        EntityManager ITrackProjectionRuntimeContext.EntityManager => EntityManager;
        TimedLogger ITrackProjectionRuntimeContext.Log => log;
        uint ITrackProjectionRuntimeContext.Frame => m_SimulationSystem.frameIndex;
        NativeHashMap<Entity, int> ITrackProjectionRuntimeContext.CachedWaypointIndex => m_CachedWpIdx;
        TrackModelService ITrackProjectionRuntimeContext.TrackModel => m_TrackModel;
        BufferLookup<T> ITrackProjectionRuntimeContext.GetBufferLookup<T>(bool isReadOnly) => GetBufferLookup<T>(isReadOnly);
        bool ITrackProjectionRuntimeContext.TryGetRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition) => TryGetRouteProgress(vehicle, out nextWaypointIndex, out segmentPosition);
        bool ITrackProjectionRuntimeContext.TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state) => m_VehicleView.TryGetState(vehicle, out state);
        bool ITrackProjectionRuntimeContext.TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out float distanceMeters)
        {
            distanceMeters = 0f;
            if (!TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection projection))
                return false;

            distanceMeters = projection.DistanceMeters;
            return true;
        }
        bool ITrackProjectionRuntimeContext.IsVehicleBoarding(Entity vehicle) => IsVehicleBoarding(vehicle);

        EntityManager IBypassAdmissionRuntimeContext.EntityManager => EntityManager;
        TimedLogger IBypassAdmissionRuntimeContext.Log => log;
        uint IBypassAdmissionRuntimeContext.Frame => m_SimulationSystem.frameIndex;
        IEnumerable<KeyValuePair<string, AppliedWorkbenchLineState>> IBypassAdmissionRuntimeContext.AppliedWorkbenchLines => m_AppliedWorkbenchLines;
        TrackModelService IBypassAdmissionRuntimeContext.TrackModel => m_TrackModel;
        TrackProjectionService IBypassAdmissionRuntimeContext.TrackProjection => m_TrackProjection;
        BufferLookup<T> IBypassAdmissionRuntimeContext.GetBufferLookup<T>(bool isReadOnly) => GetBufferLookup<T>(isReadOnly);
        bool IBypassAdmissionRuntimeContext.IsBypassRuntimeFeatureEnabled() => IsBypassRuntimeFeatureEnabled();
        bool IBypassAdmissionRuntimeContext.TryGetBypassControlScope(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, out BypassControlScope scope, out string failureReason) => m_Bypass.TryGetBypassControlScope(vehicle, line, waypoints, waypointIndex, out scope, out failureReason);
        bool IBypassAdmissionRuntimeContext.IsDispatchRuntimeManagedLine(Entity line) => IsDispatchRuntimeManagedLine(line);
        bool IBypassAdmissionRuntimeContext.IsAppliedWorkbenchLocalLine(Entity line) => IsAppliedWorkbenchLocalLine(line);
        bool IBypassAdmissionRuntimeContext.IsAppliedWorkbenchExpressLine(Entity line) => IsAppliedWorkbenchExpressLine(line);
        bool IBypassAdmissionRuntimeContext.ShouldClearHoldAfterStationExit(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Bypass.ShouldClearHoldAfterStationExit(vehicle, line, waypoints, waypointIndex);
        bool IBypassAdmissionRuntimeContext.IsExpressBlockerStillWithinBypassStation(Entity blocker, Entity station) => m_Bypass.IsExpressBlockerStillWithinBypassStation(blocker, station);
        bool IBypassAdmissionRuntimeContext.TryEvaluateLatchedBlockerBeforeRelease(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, BypassConflictEpisode episode, Entity blocker, out bool beforeRelease) => m_Bypass.TryEvaluateLatchedBlockerBeforeRelease(scope, waypoints, episode, blocker, out beforeRelease);
        bool IBypassAdmissionRuntimeContext.ShouldReleaseForQueuedLocalAhead(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker, out float expressSceneCoordinate, out float localSceneCoordinate, out float queuedLocalMeters) => m_Bypass.ShouldReleaseForQueuedLocalAhead(scope, waypoints, blocker, out expressSceneCoordinate, out localSceneCoordinate, out queuedLocalMeters);
        void IBypassAdmissionRuntimeContext.LogQueuedLocalBypassOverrideOnce(Entity vehicle, Entity line, Entity blocker, string action, string reason, float expressSceneCoordinate, float localSceneCoordinate, float queuedLocalMeters) => m_Bypass.LogQueuedLocalBypassOverrideOnce(vehicle, line, blocker, action, reason, expressSceneCoordinate, localSceneCoordinate, queuedLocalMeters);
        Entity IBypassAdmissionRuntimeContext.ResolveVehicleLine(Entity vehicle) => ResolveVehicleLine(vehicle);
        bool IBypassAdmissionRuntimeContext.IsLineOrderedRuntimeLoggingEnabled() => IsLineOrderedRuntimeLoggingEnabled();
        int IBypassAdmissionRuntimeContext.ComputeWpIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints) => ComputeWpIndex(vehicle, waypoints);
        Entity IBypassAdmissionRuntimeContext.GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => GetStationBuildingForWaypoint(waypoints, waypointIndex);
        Entity IBypassAdmissionRuntimeContext.ResolvePassingStationBuilding(Entity entity) => ResolvePassingStationBuilding(entity);
        bool IBypassAdmissionRuntimeContext.TryEstimateRemainingBoardingDwellFrames(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, Entity currentBypassBuilding, uint nowFrame, out float remainingFrames) => TryEstimateRemainingBoardingDwellFrames(vehicle, line, waypoints, currentWaypointIndex, currentBypassBuilding, nowFrame, out remainingFrames);
        bool IBypassAdmissionRuntimeContext.TryGetEffectiveTraversalRunSliceFrames(Entity line, TraversalRunSlice slice, out float effectiveRunFrames) => TryGetEffectiveTraversalRunSliceFrames(line, slice, out effectiveRunFrames);
        bool IBypassAdmissionRuntimeContext.TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        void IBypassAdmissionRuntimeContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnce(cache, vehicle, key, message);
        Entity IBypassAdmissionRuntimeContext.ResolveVehicle(Entity vehicle) => ResolveRuntimeControllerVehicle(vehicle);
        bool IBypassAdmissionRuntimeContext.TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state) => m_VehicleView.TryGetState(vehicle, out state);
        bool IBypassAdmissionRuntimeContext.TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out BypassLineDistanceProjection projection)
        {
            projection = default;
            if (!TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection runtimeProjection))
                return false;

            projection = new BypassLineDistanceProjection
            {
                TotalDistanceMeters = runtimeProjection.TotalDistanceMeters,
                DistanceMeters = runtimeProjection.DistanceMeters,
                Progress01 = runtimeProjection.Progress01,
                NextWaypointIndex = runtimeProjection.NextWaypointIndex,
                SegmentPosition = runtimeProjection.SegmentPosition
            };
            return true;
        }

        bool IBypassAdmissionRuntimeContext.TryBuildLineDistanceModel(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out BypassLineDistanceModel model)
        {
            model = null;
            if (!TryBuildLineDistanceModel(line, waypoints, out LineMileageModel runtimeModel) || runtimeModel == null)
                return false;

            model = new BypassLineDistanceModel
            {
                Signature = runtimeModel.Signature,
                TotalDistanceMeters = runtimeModel.TotalDistanceMeters,
                WaypointDistances = runtimeModel.WaypointDistances,
                BypassWaypointDistances = runtimeModel.BypassWaypointDistances,
                BypassStopNodeDistances = runtimeModel.BypassStopNodeDistances,
                BuildingDistances = runtimeModel.BuildingDistances
            };

            for (int i = 0; i < runtimeModel.CorridorNodes.Count; i++)
            {
                CorridorNode node = runtimeModel.CorridorNodes[i];
                model.CorridorNodes.Add(new BypassCorridorNode
                {
                    Building = node.Building,
                    DistanceMeters = node.DistanceMeters,
                    IsStopNode = node.IsStopNode
                });
            }

            return true;
        }
        bool IBypassAdmissionRuntimeContext.TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => TryGetLineTimeProfile(line, waypoints, out profile);
        string IBypassAdmissionRuntimeContext.FormatBypassNodeLabel(Entity building) => ResolveWorkbenchEntityName(building);
        uint IControlContext.Frame => m_SimulationSystem.frameIndex;
        bool IControlContext.IsBypassRuntimeLoggingEnabled() => IsBypassRuntimeLoggingEnabled();
        void IControlContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnce(cache, vehicle, key, message);
        Entity IControlContext.ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => waypointIndex >= 0 && waypointIndex < waypoints.Length ? ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint) : Entity.Null;
        void IControlContext.RecordHold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex, string stateTag)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            RecordRuntimeObservationBypassHoldStart(vehicle, blocker, holdStation, waypointIndex, m_SimulationSystem.frameIndex, stateTag);
            if (!IsBypassRuntimeLoggingEnabled())
                return;

            log.Info("[待避] " + lineTag + " 车辆" + vehicle.Index
                + " state=" + stateTag
                + " 等待快车" + blocker.Index + " 先行");
        }

        void IControlContext.RecordRelease(Entity vehicle, Entity blocker, string reason)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            RecordRuntimeObservationBypassHoldRelease(vehicle, blocker, m_SimulationSystem.frameIndex, reason);
        }
        void IControlContext.TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Announcements.BypassWaiting(vehicle, route, waypoints, waypointIndex);
        bool IRuntimeContext.RuntimeEnabled() => IsBypassRuntimeFeatureEnabled();
        void IRuntimeContext.ClearLineTimeProfiles() => ClearLineTimeProfiles();

        private Dictionary<GlobalSharedTrunkCacheKey, GlobalSharedTrunkSnapshot> m_GlobalSharedTrunkSnapshots => m_TrackModel.GlobalSharedTrunkSnapshots;
        private Dictionary<ProtectedIntervalPairMetricsCacheKey, ProtectedIntervalPairMetricsSnapshot> m_ProtectedIntervalPairMetricsSnapshots => m_TrackModel.ProtectedIntervalPairMetricsSnapshots;

        internal void LogLineTrackChainDiagnostics(Entity line)
        {
            m_TrackModel.LogLineTrackChainDiagnostics(line);
        }

        public void RequestDumpTrackModelSnapshot()
        {
            m_TrackModel.RequestDumpTrackModelSnapshot();
        }

        internal bool IsVehicleBoarding(Entity vehicle)
        {
            return vehicle != Entity.Null
                && EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & Game.Vehicles.PublicTransportFlags.Boarding) != 0;
        }
    }
}
