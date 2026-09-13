using System;
using System.Collections.Generic;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

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
        private readonly Func<Entity, bool> m_IsVehicleBoarding;
        private readonly RailEventSource m_RailSource;
        private readonly RuntimeHotPathProbe m_HotPathProbe;
        private readonly Func<Entity, bool> m_IsLinePending;

        internal TrackProjectionPort(
            EntityManager entityManager,
            TimedLogger log,
            Func<uint> frame,
            Func<NativeHashMap<Entity, int>> cachedWaypointIndex,
            TrackModelService trackModel,
            TrackModelContext.IBuffers buffers,
            RouteProgress routeProgress,
            VehicleView vehicleView,
            Func<Entity, bool> isVehicleBoarding,
            RailEventSource railSource,
            RuntimeHotPathProbe hotPathProbe,
            Func<Entity, bool> isLinePending)
        {
            m_EntityManager = entityManager;
            m_Log = log;
            m_Frame = frame;
            m_CachedWaypointIndex = cachedWaypointIndex;
            m_TrackModel = trackModel;
            m_Buffers = buffers;
            m_RouteProgress = routeProgress;
            m_VehicleView = vehicleView;
            m_IsVehicleBoarding = isVehicleBoarding;
            m_RailSource = railSource;
            m_HotPathProbe = hotPathProbe;
            m_IsLinePending = isLinePending;
        }

        EntityManager ITrackProjectionRuntimeContext.EntityManager => m_EntityManager;
        TimedLogger ITrackProjectionRuntimeContext.Log => m_Log;
        uint ITrackProjectionRuntimeContext.Frame => m_Frame();
        NativeHashMap<Entity, int> ITrackProjectionRuntimeContext.CachedWaypointIndex => m_CachedWaypointIndex();
        TrackModelService ITrackProjectionRuntimeContext.TrackModel => m_TrackModel;
        bool ITrackProjectionRuntimeContext.IsLinePending(Entity line) => m_IsLinePending != null && m_IsLinePending(line);
        bool ITrackProjectionRuntimeContext.ProjectionDiagnosticsEnabled => RuntimeHotPathProbe.Enabled();

        void ITrackProjectionRuntimeContext.RecordProjectionCacheAccess(ProjectionRequestSource source, bool cacheHit, bool available, VehicleTrackCursorSource cursorSource, bool exactOnly)
        {
            m_HotPathProbe.RecordProjectionCacheAccess(source, cacheHit, available, cursorSource, exactOnly);
        }

        void ITrackProjectionRuntimeContext.RecordProjectionLineSnapshotAccess(ProjectionRequestSource source, bool cacheHit)
        {
            m_HotPathProbe.RecordProjectionLineSnapshotAccess(source, cacheHit);
        }

        void ITrackProjectionRuntimeContext.RecordProjectionOutcome(ProjectionOutcome outcome)
        {
            m_HotPathProbe.RecordProjectionOutcome(outcome);
        }

        void ITrackProjectionRuntimeContext.RecordProjectionExactFailure(ProjectionOutcome outcome)
        {
            m_HotPathProbe.RecordProjectionExactFailure(outcome);
        }

        bool ITrackProjectionRuntimeContext.TryReadProjectionCurrentLane(Entity vehicle, out TrainCurrentLane currentLane)
        {
            return m_RailSource.TryReadProjectionCurrentLane(vehicle, out currentLane);
        }

        void ITrackProjectionRuntimeContext.CollectProjectionOverlapLanes(Entity lane, List<Entity> lanes)
        {
            if (lanes == null || lane == Entity.Null
                || !m_EntityManager.HasBuffer<LaneOverlap>(lane)
                || !m_EntityManager.HasComponent<Lane>(lane)
                || !m_EntityManager.HasComponent<Curve>(lane))
                return;

            Lane sourceLane = m_EntityManager.GetComponentData<Lane>(lane);
            Curve sourceCurve = m_EntityManager.GetComponentData<Curve>(lane);
            DynamicBuffer<LaneOverlap> overlaps = m_EntityManager.GetBuffer<LaneOverlap>(lane, true);
            for (int index = 0; index < overlaps.Length; index++)
            {
                LaneOverlap overlap = overlaps[index];
                Entity other = overlap.m_Other;
                if (other == Entity.Null
                    || overlap.m_ThisStart != 0 || overlap.m_ThisEnd != byte.MaxValue
                    || overlap.m_OtherStart != 0 || overlap.m_OtherEnd != byte.MaxValue
                    || overlap.m_Parallelism < 128
                    || (overlap.m_Flags & OverlapFlags.Track) == 0
                    || !m_EntityManager.Exists(other)
                    || !m_EntityManager.HasComponent<Lane>(other)
                    || !m_EntityManager.HasComponent<Curve>(other))
                    continue;

                Lane otherLane = m_EntityManager.GetComponentData<Lane>(other);
                Curve otherCurve = m_EntityManager.GetComponentData<Curve>(other);
                if (!sourceLane.m_StartNode.Equals(otherLane.m_StartNode)
                    || !sourceLane.m_EndNode.Equals(otherLane.m_EndNode)
                    || !math.all(sourceCurve.m_Bezier.a == otherCurve.m_Bezier.a)
                    || !math.all(sourceCurve.m_Bezier.b == otherCurve.m_Bezier.b)
                    || !math.all(sourceCurve.m_Bezier.c == otherCurve.m_Bezier.c)
                    || !math.all(sourceCurve.m_Bezier.d == otherCurve.m_Bezier.d))
                    continue;

                lanes.Add(other);
            }
        }

        bool ITrackProjectionRuntimeContext.HasProjectionPathWrite(Entity vehicle)
        {
            return m_RailSource.HasProjectionPathWrite(vehicle);
        }

        bool ITrackProjectionRuntimeContext.TryReadProjectionNavigation(
            Entity vehicle,
            out DynamicBuffer<TrainNavigationLane> navigation)
        {
            return m_RailSource.TryReadProjectionNavigation(vehicle, out navigation);
        }

        bool ITrackProjectionRuntimeContext.TryReadProjectionPath(
            Entity vehicle,
            out PathOwner pathOwner,
            out DynamicBuffer<PathElement> pathElements)
        {
            return m_RailSource.TryReadProjectionPath(vehicle, out pathOwner, out pathElements);
        }

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

        bool ITrackProjectionRuntimeContext.IsVehicleBoarding(Entity vehicle) => m_IsVehicleBoarding(vehicle);

        bool ITrackProjectionRuntimeContext.TryReadProjectionRuntimeContext(Entity vehicle, out ProjectionRuntimeContext context)
        {
            return m_RailSource.TryReadProjectionRuntimeContext(vehicle, out context);
        }
    }
}
