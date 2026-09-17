using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TrackProjection
{
    internal interface ITrackProjectionRuntimeContext
    {
        EntityManager EntityManager { get; }
        TimedLogger Log { get; }
        uint Frame { get; }
        NativeHashMap<Entity, int> CachedWaypointIndex { get; }
        TrackModelService TrackModel { get; }
        bool IsLinePending(Entity line);
        bool ProjectionDiagnosticsEnabled { get; }

        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;
        void RecordProjectionCacheAccess(ProjectionRequestSource source, bool cacheHit, bool available, VehicleTrackCursorSource cursorSource, bool exactOnly);
        void RecordProjectionLineSnapshotAccess(ProjectionRequestSource source, bool cacheHit);
        void RecordProjectionOutcome(ProjectionOutcome outcome);
        void RecordProjectionExactFailure(ProjectionOutcome outcome);
        bool TryReadProjectionCurrentLane(Entity vehicle, out TrainCurrentLane currentLane);
        void CollectProjectionOverlapLanes(Entity lane, List<Entity> lanes);
        bool HasProjectionPathWrite(Entity vehicle);
        bool TryReadProjectionNavigation(Entity vehicle, out DynamicBuffer<TrainNavigationLane> navigation);
        bool TryReadProjectionPath(Entity vehicle, out PathOwner pathOwner, out DynamicBuffer<PathElement> pathElements);
        bool TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);
        bool TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state);
        bool IsVehicleBoarding(Entity vehicle);
        bool IsVehicleArriving(Entity vehicle);
        bool HasProjectionStopSession(Entity vehicle);
        bool TryGetDeparturePendingStopWaypoint(Entity vehicle, Entity line, out int waypointIndex);
        bool TryConfirmProjectionBoardingWaypoint(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, TrainCurrentLane currentLane, out int waypointIndex);
        bool TryResolveProjectionTargetWaypoint(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out int waypointIndex);
        bool TryReadProjectionRuntimeContext(Entity vehicle, out ProjectionRuntimeContext context);
    }
}
