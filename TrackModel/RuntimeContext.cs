using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Bypass;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.TrackModel
{
    internal interface ITrackWorld
    {
        EntityManager EntityManager { get; }
        TimedLogger Log { get; }
        uint FrameIndex { get; }
        ClockSnapshot ClockSnapshot { get; }
        int ManagedVehicleCount { get; }

        NativeArray<Entity> GetLineEntities(Allocator allocator);
        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;
    }

    internal interface ITrackNames
    {
        bool TryGetRenderedLabelName(Entity entity, out string name);
        bool TryGetCustomLineName(Entity line, out string customName);
    }

    internal interface ITrackTimes
    {
        bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile);
        float GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, TransportLineData prefabLineData);
        float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints);
        float ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex);
    }

    internal interface ITrackStops
    {
        Entity Stop(Entity waypoint);
        Entity StationOf(Entity stop);
        string StopName(Entity stop);
        string StopKey(Entity stop);
        Entity ResolvePassingStation(Entity entity);
        Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
    }

    internal interface ITrackLines
    {
        IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines { get; }
        bool IsBypassStation(Entity building);
        bool IsAppliedLocal(Entity line);
        bool IsAppliedExpress(Entity line);
    }

    internal interface ITrackBypass
    {
        bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding);
        bool TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex);
        bool TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex);
        Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding);
    }

    internal interface ITrackTurns
    {
        bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries);
        bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary);
    }

    internal interface ITrackModelRuntimeContext :
        ITrackWorld,
        ITrackNames,
        ITrackTimes,
        ITrackStops,
        ITrackLines,
        ITrackBypass,
        ITrackTurns
    {
        void NotifyLineTrackChainRebuilt(Entity line, ulong oldSignature, ulong newSignature, int oldAtomCount, int newAtomCount);
    }
}
