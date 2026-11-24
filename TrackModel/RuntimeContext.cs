using System.Collections.Generic;
using Game.Routes;
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

        NativeArray<Entity> GetLineEntities(Allocator allocator);
        BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData;
        ComponentLookup<T> GetComponentLookup<T>(bool isReadOnly) where T : unmanaged, IComponentData;
    }

    internal interface ITrackNames
    {
        bool TryGetRenderedLabelName(Entity entity, out string name);
        bool TryGetCustomLineName(Entity line, out string customName);
    }

    internal interface ITrackStops
    {
        Entity Stop(Entity waypoint);
        Entity StationOf(Entity stop);
        string StopName(Entity stop);
        bool TryStopCustomName(Entity stop, out string name);
        string StopKey(Entity stop);
        Entity ResolvePassingStation(Entity entity);
        Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
    }

    internal interface ITrackLines
    {
        IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines { get; }
    }

    internal interface ITrackModelRuntimeContext :
        ITrackWorld,
        ITrackNames,
        ITrackStops,
        ITrackLines
    {
        void NotifyLineTrackChainCandidate(Entity line, LineTrackChain chain);
        void NotifyLineTrackChainEstablished(Entity line, LineTrackChain chain);
        void NotifyLineDeleted(TrackLineDeletedFact fact);
    }
}
