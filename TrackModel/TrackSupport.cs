using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal sealed class TrackSupport
    {
        private readonly ITrackModelRuntimeContext m_Runtime;

        internal TrackSupport(ITrackModelRuntimeContext runtime)
        {
            m_Runtime = runtime;
        }

        internal EntityManager EntityManager => m_Runtime.EntityManager;
        internal TimedLogger Log => m_Runtime.Log;
        internal uint FrameIndex => m_Runtime.FrameIndex;
        internal ClockSnapshot ClockSnapshot => m_Runtime.ClockSnapshot;
        internal int ManagedVehicleCount => m_Runtime.ManagedVehicleCount;
        internal IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines => m_Runtime.AppliedLines;

        internal NativeArray<Entity> GetLineEntities(Allocator allocator) => m_Runtime.GetLineEntities(allocator);
        internal BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData => m_Runtime.GetBufferLookup<T>(isReadOnly);
        internal bool IsBypassStation(Entity building) => m_Runtime.IsBypassStation(building);
        internal bool TryGetRenderedLabelName(Entity entity, out string name) => m_Runtime.TryGetRenderedLabelName(entity, out name);
        internal bool TryGetCustomLineName(Entity line, out string customName) => m_Runtime.TryGetCustomLineName(line, out customName);

        internal bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => m_Runtime.TryGetLineTimeProfile(line, waypoints, out profile);
        internal float GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, Game.Prefabs.TransportLineData prefabLineData) => m_Runtime.GetProfileWaypointStopFrames(line, waypoints, waypointIndex, prefabLineData);
        internal float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints) => m_Runtime.GetLineLoopFramesEstimate(line, waypoints);
        internal float ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex) => m_Runtime.ComputeDepartureToWaypointFramesFromProfile(profile, fromWaypointIndex, targetWaypointIndex);

        internal Entity Stop(Entity waypoint) => m_Runtime.Stop(waypoint);
        internal Entity StationOf(Entity stop) => m_Runtime.StationOf(stop);
        internal string StopName(Entity stop) => m_Runtime.StopName(stop);
        internal string StopKey(Entity stop) => m_Runtime.StopKey(stop);
        internal Entity ResolvePassingStationBuilding(Entity entity) => m_Runtime.ResolvePassingStation(entity);
        internal bool IsAppliedLocal(Entity line) => m_Runtime.IsAppliedLocal(line);
        internal bool IsAppliedExpress(Entity line) => m_Runtime.IsAppliedExpress(line);

        internal bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding)
            => m_Runtime.TryGetBypassWaypointContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);

        internal Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
            => m_Runtime.GetBypassBuildingForWaypoint(waypoints, waypointIndex);

        internal Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
            => m_Runtime.GetStationBuildingForWaypoint(waypoints, waypointIndex);

        internal bool TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex)
            => m_Runtime.TryFindWaypointIndexForBypassBuilding(waypoints, building, startIndexInclusive, out waypointIndex);

        internal bool TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex)
            => m_Runtime.TryFindFutureSharedCorridorWaypoint(expressWaypoints, localCorridorWaypoints, startIndexInclusive, endIndexInclusive, out expressWaypointIndex, out localWaypointIndex);

        internal Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding)
            => m_Runtime.BuildLocalBypassCorridorWaypointMap(waypoints, currentWaypointIndex, nextBypassWaypointIndex, currentBypassBuilding);

        internal bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries)
            => m_Runtime.TryCollectTurnbackStationBoundaries(chain, stationBoundaries);

        internal bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary)
            => m_Runtime.TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary);
    }
}
