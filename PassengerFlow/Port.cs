using Game.Routes;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Runtime;
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Port
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private Func<Entity, bool?> m_ServiceStatus;

        internal Port(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal uint Frame()
            => m_Runtime.m_SimulationSystem != null ? m_Runtime.m_SimulationSystem.frameIndex : 0u;

        internal bool IsReady => m_Runtime.m_SystemReady;

        internal ulong PublishedTraversalVersion => m_Runtime.m_TrackModel.PublishedTraversalVersion;

        internal int CopyTraversalChanges(ulong afterVersion, int budget,
            List<PublishedTraversalSnapshot> output, out bool historyGap)
            => m_Runtime.m_TrackModel.CopyPublishedTraversalChanges(afterVersion, budget, output, out historyGap);

        internal void InvalidateWaitingDirectory(Entity line)
            => SamplingSystem.CurrentState?.Anchors.InvalidateLine(line);

        internal IReadOnlyDictionary<string, AppliedLine> AppliedLines => m_Runtime.AppliedLines;

        internal TramStationGroup[] ReadStationGroups(FlowQueryDto request)
        {
            Entity line = Entity.Null;
            string[] affected = null;
            if (!string.IsNullOrEmpty(request.lineId))
            {
                if (!AppliedLines.TryGetValue(request.lineId, out AppliedLine applied))
                    return Array.Empty<TramStationGroup>();
                line = applied.LineEntity;
                if ((request.includeLineStops ?? false) && m_Runtime.m_TrackModel != null)
                    affected = m_Runtime.m_TrackModel.RefreshTramStationNames(line);
            }
            return m_Runtime.m_TrackModel != null
                ? m_Runtime.m_TrackModel.ReadTramStationGroups(line, request.stationGroupId ?? request.stationId, affected)
                : Array.Empty<TramStationGroup>();
        }

        internal ClockSnapshot Clock()
            => m_Runtime.m_SimClock.Snapshot;

        internal int NowMinute()
            => m_Runtime.m_SimClock.Snapshot.NowMinute;

        internal uint ToFramesCeil(double gameMinutes)
            => m_Runtime.m_SimClock.Snapshot.ToFramesCeil(gameMinutes);

        internal long ClockEpoch()
            => m_Runtime.m_SimClock.Snapshot.ClockEpoch;

        internal double FramesPerMinute()
            => m_Runtime.m_SimClock.Snapshot.FramesPerMinute;

        internal void SubscribeClockChanged(Action<ClockSnapshot, ClockSnapshot> handler)
            => m_Runtime.m_SimClock.ClockChanged += handler;

        internal bool TryState(Entity vehicle, out VehicleState state)
            => m_Runtime.TryGetRuntimeVehicleState(vehicle, out state);

        internal bool TryLine(Entity vehicle, out Entity line)
            => m_Runtime.m_VehicleView.TryGetLine(vehicle, out line);

        internal NativeArray<Entity> VehicleViews(Allocator allocator)
            => m_Runtime.m_VehicleView.Keys(allocator);

        internal void RegisterVehicle(Entity vehicle, Entity line, VehicleState vehicleState, uint frame)
            => Observer.RegisterVehicle(this, SamplingSystem.CurrentState, vehicle, line, vehicleState, frame);

        internal void EndService(Entity vehicle, Entity line, uint frame)
            => Observer.EndService(this, SamplingSystem.CurrentState, vehicle, line, frame);

        internal void BindServiceStatus(Func<Entity, bool?> readStatus)
            => m_ServiceStatus = readStatus;

        internal bool? ServiceActive(Entity vehicle)
            => m_ServiceStatus?.Invoke(vehicle);

        internal void OpenStop(Entity vehicle, Entity line, int waypointIndex, uint frame)
            => Observer.OpenStop(this, SamplingSystem.CurrentState, vehicle, line, waypointIndex, frame);

        internal void RestoreStop(Entity vehicle, Entity line, int waypointIndex, uint frame)
            => Observer.RestoreStop(this, SamplingSystem.CurrentState, vehicle, line, waypointIndex, frame);

        internal void ConfirmDeparture(Entity vehicle, uint frame)
            => Observer.ConfirmDeparture(this, SamplingSystem.CurrentState, vehicle, frame);

        internal void LaunchOrigin(Entity vehicle, uint frame)
            => Observer.LaunchOrigin(this, SamplingSystem.CurrentState, vehicle, frame);

        internal void CancelStop(Entity vehicle)
        {
            State state = SamplingSystem.CurrentState;
            if (state != null && state.OpenStops.TryGetValue(vehicle, out OpenStop stop))
                SamplingSystem.CancelRepresentatives(vehicle, stop.OpenFrame);
            Observer.CancelStop(state, vehicle);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            SamplingSystem.CancelRepresentatives(vehicle, null);
            Observer.RemoveVehicle(this, SamplingSystem.CurrentState, vehicle);
        }

        internal void RebindVehicle(Entity vehicle, Entity line, VehicleState vehicleState, uint frame)
        {
            SamplingSystem.CancelRepresentatives(vehicle, null);
            Observer.RemoveVehicle(this, SamplingSystem.CurrentState, vehicle, true, frame);
            Observer.RegisterVehicle(this, SamplingSystem.CurrentState, vehicle, line, vehicleState, frame);
        }

        internal void InvalidateAnchors(Entity line)
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return;
            state.Anchors.InvalidateLine(line);
            state.Sections.InvalidateLine(line);
            SamplingSystem.CloseWaitingLine(state, line, Frame(), Clock());
        }

        internal bool HasWaypoints(Entity line)
            => line != Entity.Null && m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line);

        internal DynamicBuffer<RouteWaypoint> Waypoints(Entity line)
            => m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);

        internal bool TryWaypoint(Entity line, int waypointIndex, out Entity waypoint)
        {
            waypoint = Entity.Null;
            if (line == Entity.Null
                || waypointIndex < 0
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length)
                return false;

            waypoint = waypoints[waypointIndex].m_Waypoint;
            return waypoint != Entity.Null;
        }

        internal bool IsBoardingWaypoint(Entity line, int waypointIndex)
        {
            if (!TryWaypoint(line, waypointIndex, out Entity waypoint)
                || !m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
            {
                return false;
            }

            Entity stop = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return stop != Entity.Null
                && m_Runtime.EntityManager.HasComponent<BoardingVehicle>(stop);
        }

        internal Entity ConnectedStop(Entity waypoint)
        {
            if (!m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
                return Entity.Null;
            Entity stop = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return stop != Entity.Null && m_Runtime.EntityManager.HasComponent<BoardingVehicle>(stop)
                ? stop : Entity.Null;
        }

        internal bool TryDwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            anchor = default;
            return m_Runtime.m_Observation != null && m_Runtime.m_Observation.DwellAnchor(line, waypointIndex, out anchor);
        }

        internal bool TryTrackChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
        {
            chain = null;
            return m_Runtime.m_TrackModel != null && m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out chain);
        }

        internal TransitMode LineMode(Entity line) => TransportModeResolver.Resolve(m_Runtime.EntityManager, line);

        internal bool LineExists(Entity line)
            => line != Entity.Null && m_Runtime.EntityManager.Exists(line);

        internal bool TryLineMetadata(Entity line, out TransitMode mode, out string lineId)
        {
            mode = TransitMode.Unknown;
            lineId = string.Empty;
            if (!LineExists(line))
                return false;

            mode = TransportModeResolver.Resolve(m_Runtime.EntityManager, line);
            if (mode == TransitMode.Unknown)
                return false;

            LineAnchorCatalog catalog = m_Runtime.m_LineAnchorCatalog;
            if (catalog != null)
            {
                LineKey stableKey = catalog.StableKey(line);
                if (!stableKey.IsEmpty)
                {
                    lineId = LineIdentityService.GetId(stableKey);
                    return true;
                }
            }
            return false;
        }


        internal string Name(Entity entity)
            => entity != Entity.Null ? m_Runtime.EntityName(entity) : string.Empty;

        internal string StationName(Entity stopEntity)
            => stopEntity != Entity.Null && m_Runtime.m_Resolve != null
                ? m_Runtime.m_Resolve.StationName(stopEntity)
                : string.Empty;

        internal bool IsTransportStop(Entity entity)
            => entity != Entity.Null
                && m_Runtime.EntityManager.HasComponent<TransportStop>(entity);

        internal string EnsureSak(Entity anchor)
            => m_Runtime.m_Resolve != null ? m_Runtime.m_Resolve.EnsureSak(anchor) : string.Empty;

        internal bool TryRoutePlan(Entity line, out RoutePlan plan)
        {
            plan = null;
            return RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle)
                && m_Runtime.m_RoutePlans.TryGet(line, lifecycle, out plan);
        }

        internal Entity AnchorFromStop(Entity stop)
            => m_Runtime.m_Resolve.AnchorFromStop(stop);

        internal Entity RuntimeVehicle(Entity vehicle)
            => m_Runtime.m_Resolve != null ? m_Runtime.m_Resolve.RuntimeVehicle(vehicle) : vehicle;

        internal void Log(string message)
        {
            if (!string.IsNullOrEmpty(message))
                m_Runtime.log.Info(message);
        }
    }
}
