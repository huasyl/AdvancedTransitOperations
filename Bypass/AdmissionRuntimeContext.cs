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
        IEnumerable<KeyValuePair<string, AppliedWorkbenchLineState>> AppliedWorkbenchLines { get; }
        TrackModelService TrackModel { get; }
        TrackProjectionService TrackProjection { get; }
        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;

        bool IsBypassRuntimeFeatureEnabled();
        bool TryGetBypassControlScope(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, out BypassControlScope scope, out string failureReason);
        bool IsDispatchRuntimeManagedLine(Entity line);
        bool IsAppliedWorkbenchLocalLine(Entity line);
        bool IsAppliedWorkbenchExpressLine(Entity line);
        bool ShouldClearHoldAfterStationExit(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        bool IsExpressBlockerStillWithinBypassStation(Entity blocker, Entity station);
        bool TryEvaluateLatchedBlockerBeforeRelease(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, BypassConflictEpisode episode, Entity blocker, out bool beforeRelease);
        bool ShouldReleaseForQueuedLocalAhead(BypassControlScope scope, DynamicBuffer<RouteWaypoint> waypoints, Entity blocker, out float expressSceneCoordinate, out float localSceneCoordinate, out float queuedLocalMeters);
        void LogQueuedLocalBypassOverrideOnce(Entity vehicle, Entity line, Entity blocker, string action, string reason, float expressSceneCoordinate, float localSceneCoordinate, float queuedLocalMeters);
        Entity ResolveVehicleLine(Entity vehicle);
        bool IsLineOrderedRuntimeLoggingEnabled();
        int ComputeWpIndex(Entity vehicle, DynamicBuffer<RouteWaypoint> waypoints);
        Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        Entity ResolvePassingStationBuilding(Entity entity);
        bool TryEstimateRemainingBoardingDwellFrames(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, Entity currentBypassBuilding, uint nowFrame, out float remainingFrames);
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
