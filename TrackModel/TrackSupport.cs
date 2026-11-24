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
        internal IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines => m_Runtime.AppliedLines;

        internal NativeArray<Entity> GetLineEntities(Allocator allocator) => m_Runtime.GetLineEntities(allocator);
        internal BufferLookup<T> GetBufferLookup<T>(bool isReadOnly) where T : unmanaged, IBufferElementData => m_Runtime.GetBufferLookup<T>(isReadOnly);
        internal ComponentLookup<T> GetComponentLookup<T>(bool isReadOnly) where T : unmanaged, IComponentData => m_Runtime.GetComponentLookup<T>(isReadOnly);
        internal bool TryGetRenderedLabelName(Entity entity, out string name) => m_Runtime.TryGetRenderedLabelName(entity, out name);
        internal bool TryGetCustomLineName(Entity line, out string customName) => m_Runtime.TryGetCustomLineName(line, out customName);

        internal Entity Stop(Entity waypoint) => m_Runtime.Stop(waypoint);
        internal Entity StationOf(Entity stop) => m_Runtime.StationOf(stop);
        internal string StopName(Entity stop) => m_Runtime.StopName(stop);
        internal bool TryStopCustomName(Entity stop, out string name) => m_Runtime.TryStopCustomName(stop, out name);
        internal string StopKey(Entity stop) => m_Runtime.StopKey(stop);
        internal Entity ResolvePassingStationBuilding(Entity entity) => m_Runtime.ResolvePassingStation(entity);

        internal Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
            => m_Runtime.GetStationBuildingForWaypoint(waypoints, waypointIndex);
    }
}
