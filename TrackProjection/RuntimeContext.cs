using Game.Routes;
using RapidTransitMod.TrackModel;
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

        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;
        bool TryRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);
        bool TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state);
        bool TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out float distanceMeters);
        bool IsVehicleBoarding(Entity vehicle);
    }
}
