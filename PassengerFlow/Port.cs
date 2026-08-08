using Game.Routes;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using RapidTransitMod.Core;
using System;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Port
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        internal Port(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal uint Frame()
            => m_Runtime.m_SimulationSystem != null ? m_Runtime.m_SimulationSystem.frameIndex : 0u;

        internal ClockSnapshot Clock()
            => m_Runtime.m_SimClock.Snapshot;

        internal int NowMinute()
            => m_Runtime.m_SimClock.Snapshot.NowMinute;

        internal DateTime NowDate()
            => m_Runtime.m_SimClock.Snapshot.NowDate;

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

        internal void OpenStop(Entity vehicle, Entity line, int waypointIndex, uint frame)
            => Observer.OpenStop(this, SamplingSystem.CurrentState, vehicle, line, waypointIndex, frame);

        internal void RestoreStop(Entity vehicle, Entity line, int waypointIndex, uint frame)
            => Observer.RestoreStop(this, SamplingSystem.CurrentState, vehicle, line, waypointIndex, frame);

        internal void ConfirmDeparture(Entity vehicle, uint frame)
            => Observer.ConfirmDeparture(this, SamplingSystem.CurrentState, vehicle, frame);

        internal void LaunchOrigin(Entity vehicle, uint frame)
            => Observer.LaunchOrigin(this, SamplingSystem.CurrentState, vehicle, frame);

        internal void CancelStop(Entity vehicle)
            => Observer.CancelStop(SamplingSystem.CurrentState, vehicle);

        internal void RemoveVehicle(Entity vehicle)
            => Observer.RemoveVehicle(this, SamplingSystem.CurrentState, vehicle);

        internal void InvalidateAnchors(Entity line)
            => SamplingSystem.CurrentState?.Anchors.InvalidateLine(line);

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

        internal Entity RuntimeVehicle(Entity vehicle)
            => m_Runtime.m_Resolve != null ? m_Runtime.m_Resolve.RuntimeVehicle(vehicle) : vehicle;

        internal void Log(string message)
        {
            if (!string.IsNullOrEmpty(message))
                m_Runtime.log.Info(message);
        }
    }
}
