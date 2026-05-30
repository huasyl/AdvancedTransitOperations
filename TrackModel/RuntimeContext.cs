using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal interface ITrackModelRuntimeContext
    {
        EntityManager EntityManager { get; }
        TimedLogger Log { get; }
        uint FrameIndex { get; }
        int ManagedVehicleCount { get; }
        IEnumerable<KeyValuePair<string, AppliedWorkbenchLineState>> AppliedWorkbenchLines { get; }

        NativeArray<Entity> GetLineEntities(Allocator allocator);
        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;

        bool IsBypassStation(Entity building);
        bool TryGetRenderedLabelName(Entity entity, out string name);
        bool TryGetCustomLineName(Entity line, out string customName);
        bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile);
        float GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, TransportLineData prefabLineData);
        float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints);
        float ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex);
        Entity ResolveWorkbenchStopEntity(Entity waypoint);
        Entity FindTransportStationFromStop(Entity stop);
        Entity ResolvePassingStationBuilding(Entity entity);
        bool IsAppliedWorkbenchLocalLine(Entity line);
        bool IsAppliedWorkbenchExpressLine(Entity line);
        bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding);
        Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        bool TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex);
        bool TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex);
        Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding);
        bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries);
        bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary);
    }
}
