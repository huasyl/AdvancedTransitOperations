using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using Game.UI;
using Game.UI.InGame;
using RapidTransitMod.Dispatch.Lines;
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
        }

        internal delegate bool LineProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile);
        internal delegate float StopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, TransportLineData prefabLineData);
        internal delegate float DepartFrames(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex);
        internal delegate bool BypassContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding);
        internal delegate Entity WaypointEntity(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        internal delegate bool FindWaypoint(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex);
        internal delegate bool FutureWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex);
        internal delegate Dictionary<Entity, int> CorridorMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding);
        internal delegate bool CollectTurnback(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries);
        internal delegate bool ResolveTurnback(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary);
        internal delegate void LineTrackChainRebuilt(Entity line, ulong oldSignature, ulong newSignature, int oldAtomCount, int newAtomCount);

        internal sealed class Args
        {
            public Func<EntityManager> EntityMgr;
            public TimedLogger Log;
            public Func<uint> Frame;
            public Func<ClockSnapshot> ClockSnapshot;
            public Func<int> VehicleCount;
            public Func<IEnumerable<KeyValuePair<string, AppliedLine>>> AppliedLines;
            public EntityQuery LineQuery;
            public IBuffers Buffers;
            public NameSystem Name;
            public Func<Entity, bool> IsBypassStation;
            public LineProfile GetProfile;
            public StopFrames GetStopFrames;
            public DepartFrames GetDepartFrames;
            public Func<Entity, Entity> ResolveStop;
            public Func<Entity, Entity> FindStation;
            public Func<Entity, Entity> ResolveStation;
            public Func<Entity, bool> IsLocal;
            public Func<Entity, bool> IsExpress;
            public BypassContext GetBypassContext;
            public WaypointEntity GetBypassBuilding;
            public WaypointEntity GetStationBuilding;
            public FindWaypoint FindBypassWaypoint;
            public FutureWaypoint FindSharedWaypoint;
            public CorridorMap BuildCorridorMap;
            public CollectTurnback CollectTurnback;
            public ResolveTurnback ResolveTurnback;
            public LineTrackChainRebuilt NotifyLineTrackChainRebuilt;
        }

        private readonly Args m_Args;

        internal TrackModelContext(Args args)
        {
            m_Args = args;
        }

        public EntityManager EntityManager => m_Args.EntityMgr();
        public TimedLogger Log => m_Args.Log;
        public uint FrameIndex => m_Args.Frame();
        public ClockSnapshot ClockSnapshot => m_Args.ClockSnapshot();
        public int ManagedVehicleCount => m_Args.VehicleCount();
        public IEnumerable<KeyValuePair<string, AppliedLine>> AppliedLines => m_Args.AppliedLines();

        public NativeArray<Entity> GetLineEntities(Allocator allocator) => m_Args.LineQuery.ToEntityArray(allocator);
        public BufferLookup<T> GetBufferLookup<T>(bool readOnly) where T : unmanaged, IBufferElementData => m_Args.Buffers.Get<T>(readOnly);
        public bool IsBypassStation(Entity building) => m_Args.IsBypassStation != null && m_Args.IsBypassStation(building);

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

        public bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTimeProfileHeader profile) => m_Args.GetProfile(line, waypoints, out profile);
        public float GetProfileWaypointStopFrames(Entity line, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex, TransportLineData prefabLineData) => m_Args.GetStopFrames(line, waypoints, waypointIndex, prefabLineData);
        public float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints) => m_Args.GetProfile(line, waypoints, out LineTimeProfileHeader profile) && profile.m_BaseLoopFrames > 0f ? profile.m_BaseLoopFrames : 0f;
        public float ComputeDepartureToWaypointFramesFromProfile(LineTimeProfileHeader profile, int fromWaypointIndex, int targetWaypointIndex) => m_Args.GetDepartFrames(profile, fromWaypointIndex, targetWaypointIndex);
        public Entity Stop(Entity waypoint) => m_Args.ResolveStop(waypoint);
        public Entity StationOf(Entity stop) => m_Args.FindStation(stop);
        public Entity ResolvePassingStation(Entity entity) => m_Args.ResolveStation(entity);
        public bool IsAppliedLocal(Entity line) => m_Args.IsLocal(line);
        public bool IsAppliedExpress(Entity line) => m_Args.IsExpress(line);
        public bool TryGetBypassWaypointContext(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, out Entity currentBypassBuilding, out int nextBypassWaypointIndex, out Entity nextBypassBuilding) => m_Args.GetBypassContext(waypoints, currentWaypointIndex, out currentBypassBuilding, out nextBypassWaypointIndex, out nextBypassBuilding);
        public Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Args.GetBypassBuilding(waypoints, waypointIndex);
        public Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex) => m_Args.GetStationBuilding(waypoints, waypointIndex);
        public bool TryFindWaypointIndexForBypassBuilding(DynamicBuffer<RouteWaypoint> waypoints, Entity building, int startIndexInclusive, out int waypointIndex) => m_Args.FindBypassWaypoint(waypoints, building, startIndexInclusive, out waypointIndex);
        public bool TryFindFutureSharedCorridorWaypoint(DynamicBuffer<RouteWaypoint> expressWaypoints, Dictionary<Entity, int> localCorridorWaypoints, int startIndexInclusive, int endIndexInclusive, out int expressWaypointIndex, out int localWaypointIndex) => m_Args.FindSharedWaypoint(expressWaypoints, localCorridorWaypoints, startIndexInclusive, endIndexInclusive, out expressWaypointIndex, out localWaypointIndex);
        public Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(DynamicBuffer<RouteWaypoint> waypoints, int currentWaypointIndex, int nextBypassWaypointIndex, Entity currentBypassBuilding) => m_Args.BuildCorridorMap(waypoints, currentWaypointIndex, nextBypassWaypointIndex, currentBypassBuilding);
        public bool TryCollectTurnbackStationBoundaries(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries) => m_Args.CollectTurnback(chain, stationBoundaries);
        public bool TryResolveTurnbackStationBoundary(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => m_Args.ResolveTurnback(chain, boundary, out stationBoundary);
        public void NotifyLineTrackChainRebuilt(Entity line, ulong oldSignature, ulong newSignature, int oldAtomCount, int newAtomCount) => m_Args.NotifyLineTrackChainRebuilt?.Invoke(line, oldSignature, newSignature, oldAtomCount, newAtomCount);
    }
}
