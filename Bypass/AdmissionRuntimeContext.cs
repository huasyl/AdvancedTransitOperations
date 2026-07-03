using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal interface IBypassAdmissionRuntimeContext
    {
        EntityManager EntityManager { get; }
        TimedLogger Log { get; }
        uint Frame { get; }
        IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines { get; }
        TrackModelService TrackModel { get; }
        TrackProjectionService TrackProjection { get; }
        RapidTransitMod.Dispatch.Diagnostics.RuntimeHotPathProbe HotPathProbe { get; }
        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;

        bool IsBypassRuntimeFeatureEnabled();
        bool IsDispatchRuntimeManagedLine(Entity line);
        bool IsAppliedLocal(Entity line);
        bool IsAppliedExpress(Entity line);
        Entity ResolveLine(Entity vehicle);
        Entity ResolveStopForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        bool IsLineOrderedRuntimeLoggingEnabled();
        int ComputeWaypointIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints);
        Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        Entity ResolvePassingStation(Entity entity);
        bool TryEstimateRemainingBoardingTime(Entity vehicle, Entity line, int currentWaypointIndex, uint nowFrame, out float remainingFrames);
        bool TryGetEffectiveTraversalRunSliceFrames(Entity line, TraversalRunSlice slice, out float effectiveRunFrames);
        bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding);
        void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message);
        Entity ResolveVehicle(Entity vehicle);
        bool TryGetVehicleRuntimeState(Entity vehicle, out VehicleState state);
        bool TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out BypassLineDistanceProjection projection);
        bool TryBuildLineDistanceModel(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out BypassLineDistanceModel model);
        bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile);
        string FormatBypassNodeLabel(Entity building);
    }
}
