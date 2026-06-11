using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal class BypassAdmissionPort : IBypassAdmissionRuntimeContext
    {
        private readonly EntityManager m_EntityManager;
        private readonly TimedLogger m_Log;
        private readonly Func<uint> m_Frame;
        private readonly Func<IEnumerable<KeyValuePair<string, AppliedLine>>> m_AppliedLines;
        private readonly TrackModelService m_TrackModel;
        private readonly Func<TrackProjectionService> m_TrackProjection;
        private readonly TrackModelContext.IBuffers m_Buffers;
        private readonly Func<bool> m_BypassFeatureEnabled;
        private readonly Func<Entity, bool> m_IsManaged;
        private readonly Func<Entity, bool> m_IsLocal;
        private readonly Func<Entity, bool> m_IsExpress;
        private readonly RuntimeResolve m_Resolve;
        private readonly Func<bool> m_IsLineOrderedRuntimeLoggingEnabled;
        private readonly WaypointIndex m_WaypointIndex;
        private readonly ObservationPort m_Observation;
        private readonly SharedCorridorSupport m_Shared;
        private readonly Action<Dictionary<Entity, string>, Entity, string, string> m_LogVehicleStateOnce;
        private readonly VehicleView m_VehicleView;
        private readonly LineMileage m_LineMileage;
        private readonly LineTimes m_LineTimes;
        private readonly Func<Entity, string> m_EntityName;
        private readonly RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe m_HotPathProbe;

        internal BypassAdmissionPort(
            EntityManager entityManager,
            TimedLogger log,
            Func<uint> frame,
            Func<IEnumerable<KeyValuePair<string, AppliedLine>>> appliedLines,
            TrackModelService trackModel,
            Func<TrackProjectionService> trackProjection,
            TrackModelContext.IBuffers buffers,
            Func<bool> bypassFeatureEnabled,
            Func<Entity, bool> isManaged,
            Func<Entity, bool> isLocal,
            Func<Entity, bool> isExpress,
            RuntimeResolve resolve,
            Func<bool> isLineOrderedRuntimeLoggingEnabled,
            WaypointIndex waypointIndex,
            ObservationPort observation,
            SharedCorridorSupport shared,
            Action<Dictionary<Entity, string>, Entity, string, string> logVehicleStateOnce,
            VehicleView vehicleView,
            LineMileage lineMileage,
            LineTimes lineTimes,
            Func<Entity, string> entityName,
            RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe hotPathProbe)
        {
            m_EntityManager = entityManager;
            m_Log = log;
            m_Frame = frame;
            m_AppliedLines = appliedLines;
            m_TrackModel = trackModel;
            m_TrackProjection = trackProjection;
            m_Buffers = buffers;
            m_BypassFeatureEnabled = bypassFeatureEnabled;
            m_IsManaged = isManaged;
            m_IsLocal = isLocal;
            m_IsExpress = isExpress;
            m_Resolve = resolve;
            m_IsLineOrderedRuntimeLoggingEnabled = isLineOrderedRuntimeLoggingEnabled;
            m_WaypointIndex = waypointIndex;
            m_Observation = observation;
            m_Shared = shared;
            m_LogVehicleStateOnce = logVehicleStateOnce;
            m_VehicleView = vehicleView;
            m_LineMileage = lineMileage;
            m_LineTimes = lineTimes;
            m_EntityName = entityName;
            m_HotPathProbe = hotPathProbe;
        }

        protected Func<uint> FrameGetter => m_Frame;
        protected RuntimeResolve Resolve => m_Resolve;
        protected Action<Dictionary<Entity, string>, Entity, string, string> LogVehicleStateOnceAction => m_LogVehicleStateOnce;

        EntityManager IBypassAdmissionRuntimeContext.EntityManager => m_EntityManager;
        TimedLogger IBypassAdmissionRuntimeContext.Log => m_Log;
        uint IBypassAdmissionRuntimeContext.Frame => m_Frame();
        IEnumerable<KeyValuePair<string, AppliedLine>> IBypassAdmissionRuntimeContext.AppliedLines => m_AppliedLines();
        TrackModelService IBypassAdmissionRuntimeContext.TrackModel => m_TrackModel;
        TrackProjectionService IBypassAdmissionRuntimeContext.TrackProjection => m_TrackProjection();
        RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe IBypassAdmissionRuntimeContext.HotPathProbe => m_HotPathProbe;

        BufferLookup<T> IBypassAdmissionRuntimeContext.GetBufferLookup<T>(bool isReadOnly)
        {
            return m_Buffers.Get<T>(isReadOnly);
        }

        bool IBypassAdmissionRuntimeContext.IsBypassRuntimeFeatureEnabled() => m_BypassFeatureEnabled();
        bool IBypassAdmissionRuntimeContext.IsDispatchRuntimeManagedLine(Entity line) => m_IsManaged(line);
        bool IBypassAdmissionRuntimeContext.IsAppliedLocal(Entity line) => m_IsLocal(line);
        bool IBypassAdmissionRuntimeContext.IsAppliedExpress(Entity line) => m_IsExpress(line);
        Entity IBypassAdmissionRuntimeContext.ResolveLine(Entity vehicle) => m_Resolve.Line(vehicle);
        Entity IBypassAdmissionRuntimeContext.ResolveStopForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => waypointIndex >= 0 && waypointIndex < waypoints.Length ? m_Resolve.Stop(waypoints[waypointIndex].m_Waypoint) : Entity.Null;
        bool IBypassAdmissionRuntimeContext.IsLineOrderedRuntimeLoggingEnabled() => m_IsLineOrderedRuntimeLoggingEnabled();
        int IBypassAdmissionRuntimeContext.ComputeWaypointIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints) => m_WaypointIndex.Compute(vehicle, waypoints);
        Entity IBypassAdmissionRuntimeContext.GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Shared.GetStationBuildingForWaypoint(waypoints, waypointIndex);
        Entity IBypassAdmissionRuntimeContext.ResolvePassingStation(Entity entity) => m_Resolve.PassingStation(entity);
        bool IBypassAdmissionRuntimeContext.TryEstimateRemainingBoardingTime(Entity vehicle, Entity line, int currentWaypointIndex, uint nowFrame, out float remainingFrames) => m_Observation.TryEstimateRemainingBoardingTime(vehicle, line, currentWaypointIndex, nowFrame, out remainingFrames);
        bool IBypassAdmissionRuntimeContext.TryGetEffectiveTraversalRunSliceFrames(Entity line, TraversalRunSlice slice, out float effectiveRunFrames) => m_Observation.EffectiveFrames(line, slice, out effectiveRunFrames);
        bool IBypassAdmissionRuntimeContext.TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => m_Shared.TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        void IBypassAdmissionRuntimeContext.LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message) => m_LogVehicleStateOnce(cache, vehicle, key, message);
        Entity IBypassAdmissionRuntimeContext.ResolveVehicle(Entity vehicle) => m_Resolve.RuntimeVehicle(vehicle);
        bool IBypassAdmissionRuntimeContext.TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state) => m_VehicleView.TryGetState(vehicle, out state);

        bool IBypassAdmissionRuntimeContext.TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out BypassLineDistanceProjection projection)
        {
            projection = default;
            if (!m_LineMileage.Project(vehicle, line, waypoints, out LineDistanceProjection runtimeProjection))
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
            if (!m_LineMileage.Build(line, waypoints, out LineMileageModel runtimeModel) || runtimeModel == null)
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

        bool IBypassAdmissionRuntimeContext.TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => m_LineTimes.Get(line, waypoints, out profile);
        string IBypassAdmissionRuntimeContext.FormatBypassNodeLabel(Entity building) => m_EntityName(building);
    }
}
