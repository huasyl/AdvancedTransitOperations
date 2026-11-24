using System;
using System.Collections.Generic;
using Game.Routes;
using Game.UI;
using Game.UI.InGame;
using RapidTransitMod.TrackModel;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class TrackModelContext : ITrackModelRuntimeContext
    {
        internal interface IBuffers
        {
            BufferLookup<T> Get<T>(bool readOnly) where T : unmanaged, IBufferElementData;
            ComponentLookup<T> GetComponent<T>(bool readOnly) where T : unmanaged, IComponentData;
        }

        internal delegate bool StopCustomName(Entity stop, out string name);
        internal delegate Entity WaypointEntity(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        internal delegate void LineTrackChainCandidate(Entity line, LineTrackChain chain);
        internal delegate void LineTrackChainEstablished(Entity line, LineTrackChain chain);
        internal delegate void LineDeleted(TrackLineDeletedFact fact);

        internal sealed class Args
        {
            public Func<EntityManager> EntityMgr;
            public TimedLogger Log;
            public Func<uint> Frame;
            public Func<IEnumerable<KeyValuePair<string, AppliedLine>>> AppliedLines;
            public EntityQuery LineQuery;
            public IBuffers Buffers;
            public NameSystem Name;
            public Func<Entity, Entity> ResolveStop;
            public Func<Entity, Entity> FindStation;
            public Func<Entity, string> StopName;
            public StopCustomName GetStopCustomName;
            public Func<Entity, string> StopKey;
            public Func<Entity, Entity> ResolveStation;
            public WaypointEntity GetStationBuilding;
            public LineTrackChainCandidate NotifyLineTrackChainCandidate;
            public LineTrackChainEstablished NotifyLineTrackChainEstablished;
            public LineDeleted NotifyLineDeleted;
        }

        private readonly Args m_Args;

        internal TrackModelContext(Args args)
        {
            m_Args = args;
        }

        public EntityManager EntityManager => m_Args.EntityMgr();
        public TimedLogger Log => m_Args.Log;
        public uint FrameIndex => m_Args.Frame();
        public IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines => m_Args.AppliedLines();

        public NativeArray<Entity> GetLineEntities(Allocator allocator) => m_Args.LineQuery.ToEntityArray(allocator);
        public BufferLookup<T> GetBufferLookup<T>(bool readOnly) where T : unmanaged, IBufferElementData => m_Args.Buffers.Get<T>(readOnly);
        public ComponentLookup<T> GetComponentLookup<T>(bool readOnly) where T : unmanaged, IComponentData => m_Args.Buffers.GetComponent<T>(readOnly);

        public bool TryGetRenderedLabelName(Entity entity, out string name)
        {
            name = string.Empty;
            if (entity == Entity.Null)
                return false;

            try
            {
                name = m_Args.Name.GetRenderedLabelName(entity);
                return !string.IsNullOrWhiteSpace(name);
            }
            catch
            {
                name = string.Empty;
                return false;
            }
        }

        public bool TryGetCustomLineName(Entity line, out string customName)
        {
            customName = string.Empty;
            if (line == Entity.Null)
                return false;

            try
            {
                return m_Args.Name.TryGetCustomName(line, out customName)
                    && !string.IsNullOrWhiteSpace(customName);
            }
            catch
            {
                customName = string.Empty;
                return false;
            }
        }

        public Entity Stop(Entity waypoint) => m_Args.ResolveStop(waypoint);
        public Entity StationOf(Entity stop) => m_Args.FindStation(stop);
        public string StopName(Entity stop) => m_Args.StopName?.Invoke(stop) ?? string.Empty;
        public bool TryStopCustomName(Entity stop, out string name) => m_Args.GetStopCustomName(stop, out name);
        public string StopKey(Entity stop) => m_Args.StopKey?.Invoke(stop) ?? string.Empty;
        public Entity ResolvePassingStation(Entity entity) => m_Args.ResolveStation(entity);
        public Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Args.GetStationBuilding(waypoints, waypointIndex);
        public void NotifyLineTrackChainCandidate(Entity line, LineTrackChain chain) => m_Args.NotifyLineTrackChainCandidate?.Invoke(line, chain);
        public void NotifyLineTrackChainEstablished(Entity line, LineTrackChain chain) => m_Args.NotifyLineTrackChainEstablished?.Invoke(line, chain);
        public void NotifyLineDeleted(TrackLineDeletedFact fact) => m_Args.NotifyLineDeleted?.Invoke(fact);
    }
}
