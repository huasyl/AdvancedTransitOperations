using System;
using Game.Routes;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class TrackProjectionPort : ITrackProjectionRuntimeContext
    {
        private readonly EntityManager m_EntityManager;
        private readonly TimedLogger m_Log;
        private readonly Func<uint> m_Frame;
        private readonly Func<NativeHashMap<Entity, int>> m_CachedWaypointIndex;
        private readonly TrackModelService m_TrackModel;
        private readonly TrackModelContext.IBuffers m_Buffers;
        private readonly RouteProgress m_RouteProgress;
        private readonly VehicleView m_VehicleView;
        private readonly LineMileage m_LineMileage;
        private readonly Func<Entity, bool> m_IsVehicleBoarding;
        private readonly RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe m_HotPathProbe;

        internal TrackProjectionPort(
            EntityManager entityManager,
            TimedLogger log,
            Func<uint> frame,
            Func<NativeHashMap<Entity, int>> cachedWaypointIndex,
            TrackModelService trackModel,
            TrackModelContext.IBuffers buffers,
            RouteProgress routeProgress,
            VehicleView vehicleView,
            LineMileage lineMileage,
            Func<Entity, bool> isVehicleBoarding,
            RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe hotPathProbe)
        {
            m_EntityManager = entityManager;
            m_Log = log;
            m_Frame = frame;
            m_CachedWaypointIndex = cachedWaypointIndex;
            m_TrackModel = trackModel;
            m_Buffers = buffers;
            m_RouteProgress = routeProgress;
            m_VehicleView = vehicleView;
            m_LineMileage = lineMileage;
            m_IsVehicleBoarding = isVehicleBoarding;
            m_HotPathProbe = hotPathProbe;
        }

        EntityManager ITrackProjectionRuntimeContext.EntityManager => m_EntityManager;
        TimedLogger ITrackProjectionRuntimeContext.Log => m_Log;
        uint ITrackProjectionRuntimeContext.Frame => m_Frame();
        NativeHashMap<Entity, int> ITrackProjectionRuntimeContext.CachedWaypointIndex => m_CachedWaypointIndex();
        TrackModelService ITrackProjectionRuntimeContext.TrackModel => m_TrackModel;

        void ITrackProjectionRuntimeContext.CountNavigationDetailRead() => m_HotPathProbe.CountNavigationDetailRead();

        BufferLookup<T> ITrackProjectionRuntimeContext.GetBufferLookup<T>(bool isReadOnly)
        {
            return m_Buffers.Get<T>(isReadOnly);
        }

        bool ITrackProjectionRuntimeContext.TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            return m_RouteProgress.Try(vehicle, out nextWaypointIndex, out segmentPosition);
        }

        bool ITrackProjectionRuntimeContext.TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state)
        {
            return m_VehicleView.TryGetState(vehicle, out state);
        }

        bool ITrackProjectionRuntimeContext.TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out float distanceMeters)
        {
            distanceMeters = 0f;
            if (!m_LineMileage.Project(vehicle, line, waypoints, out LineDistanceProjection projection))
                return false;

            distanceMeters = projection.DistanceMeters;
            return true;
        }

        bool ITrackProjectionRuntimeContext.IsVehicleBoarding(Entity vehicle) => m_IsVehicleBoarding(vehicle);
    }
}
