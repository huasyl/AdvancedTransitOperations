using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem : ITrackProjectionRuntimeContext, IBypassAdmissionRuntimeContext, IRuntimeContext
    {
        internal const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;

        internal TrackModelService m_TrackModel = null!;
        internal TrackProjectionService m_TrackProjection = null!;
        internal RuntimeFacade m_Bypass = null!;

        public string BuildDevSightLaneTooltipSummary(Entity laneEntity)
        {
            return m_TrackModel.BuildDevSightLaneTooltipSummary(laneEntity);
        }

        internal TrackModelService TrackModel => m_TrackModel;
        internal TrackProjectionService TrackProjection => m_TrackProjection;
        internal RuntimeFacade Bypass => m_Bypass;

        EntityManager ITrackProjectionRuntimeContext.EntityManager => EntityManager;
        TimedLogger ITrackProjectionRuntimeContext.Log => log;
        uint ITrackProjectionRuntimeContext.Frame => m_SimulationSystem.frameIndex;
        NativeHashMap<Entity, int> ITrackProjectionRuntimeContext.CachedWaypointIndex => m_CachedWpIdx;
        TrackModelService ITrackProjectionRuntimeContext.TrackModel => m_TrackModel;
        BufferLookup<T> ITrackProjectionRuntimeContext.GetBufferLookup<T>(bool isReadOnly) => GetBufferLookup<T>(isReadOnly);
        bool ITrackProjectionRuntimeContext.TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition) => m_RouteProgress.Try(vehicle, out nextWaypointIndex, out segmentPosition);
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
        IEnumerable<KeyValuePair<string, AppliedLine>> IBypassAdmissionRuntimeContext.AppliedLines => AppliedLines;
        TrackModelService IBypassAdmissionRuntimeContext.TrackModel => m_TrackModel;
        TrackProjectionService IBypassAdmissionRuntimeContext.TrackProjection => m_TrackProjection;
        BufferLookup<T> IBypassAdmissionRuntimeContext.GetBufferLookup<T>(bool isReadOnly) => GetBufferLookup<T>(isReadOnly);
        bool IBypassAdmissionRuntimeContext.IsBypassRuntimeFeatureEnabled() => m_Features.BypassRun();
        bool IBypassAdmissionRuntimeContext.IsDispatchRuntimeManagedLine(Entity line) => m_LineView.Managed(line, m_Features.Dispatch());
        bool IBypassAdmissionRuntimeContext.IsAppliedLocal(Entity line) => m_LineView.Local(line);
        bool IBypassAdmissionRuntimeContext.IsAppliedExpress(Entity line) => m_LineView.Express(line);
        Entity IBypassAdmissionRuntimeContext.ResolveLine(Entity vehicle) => m_Resolve.Line(vehicle);
        bool IBypassAdmissionRuntimeContext.IsLineOrderedRuntimeLoggingEnabled() => IsLineOrderedRuntimeLoggingEnabled();
        int IBypassAdmissionRuntimeContext.ComputeWaypointIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints) => m_WaypointIndex.Compute(vehicle, waypoints);
        Entity IBypassAdmissionRuntimeContext.GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => GetStationBuildingForWaypoint(waypoints, waypointIndex);
        Entity IBypassAdmissionRuntimeContext.ResolvePassingStation(Entity entity) => m_Resolve.PassingStation(entity);
        bool IBypassAdmissionRuntimeContext.TryEstimateRemainingBoardingDwellFrames(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, Entity currentBypassBuilding, uint nowFrame, out float remainingFrames) => m_Observation.TryEstimateRemainingBoardingDwellFrames(vehicle, line, waypoints, currentWaypointIndex, currentBypassBuilding, nowFrame, out remainingFrames);
        bool IBypassAdmissionRuntimeContext.TryGetEffectiveTraversalRunSliceFrames(Entity line, TraversalRunSlice slice, out float effectiveRunFrames) => m_RuntimeObs.EffectiveFrames(line, slice, out effectiveRunFrames);
        bool IBypassAdmissionRuntimeContext.TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        void IBypassAdmissionRuntimeContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnce(cache, vehicle, key, message);
        Entity IBypassAdmissionRuntimeContext.ResolveVehicle(Entity vehicle) => m_Resolve.RuntimeVehicle(vehicle);
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
        string IBypassAdmissionRuntimeContext.FormatBypassNodeLabel(Entity building) => EntityName(building);
        uint IControlContext.Frame => m_SimulationSystem.frameIndex;
        bool IControlContext.IsBypassRuntimeLoggingEnabled() => IsBypassRuntimeLoggingEnabled();
        void IControlContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => LogVehicleStateOnce(cache, vehicle, key, message);
        Entity IControlContext.ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => waypointIndex >= 0 && waypointIndex < waypoints.Length ? m_Resolve.Stop(waypoints[waypointIndex].m_Waypoint) : Entity.Null;
        void IControlContext.RecordHold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex, string stateTag)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            HoldObservation(vehicle, blocker, holdStation, waypointIndex, m_SimulationSystem.frameIndex, stateTag);
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

            ReleaseObservation(vehicle, blocker, m_SimulationSystem.frameIndex, reason);
        }
        void IControlContext.TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Announcements.BypassWaiting(vehicle, route, waypoints, waypointIndex);
        bool IRuntimeContext.RuntimeEnabled() => m_Features.BypassRun();
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
