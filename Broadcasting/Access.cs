using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Core;
using Game;
using Game.Audio;
using Game.Common;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Audio;
using LineRunningVehicleSnapshot = RapidTransitMod.TrackProjection.LineRunningVehicleSnapshot;
using LineWaypointIndexLookup = RapidTransitMod.TrackModel.LineWaypointIndexLookup;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class BroadcastAccess
    {
        internal abstract class Host
        {
            internal abstract EntityManager EntityManager { get; }
            internal abstract TimedLogger Log { get; }
            internal abstract SimulationSystem SimulationSystem { get; }
            internal abstract CameraUpdateSystem CameraUpdateSystem { get; }
            internal abstract VehicleView VehicleView { get; }
            internal abstract SelectPanel SelectionPanel { get; }
            internal abstract NativeHashMap<Entity, int> CachedWaypointIndex { get; }
            internal abstract ClockSnapshot ClockSnapshot { get; }
            internal abstract bool TryStopArrivalFrame(Entity vehicle, out uint arrivalFrame);
            internal abstract uint? ReadDepartureFrame(Entity vehicle);
            internal abstract bool IsTimedStop(Entity vehicle);
            internal abstract void AppendOpenStopSessions(List<ActiveStopSession> sessions, Entity line);
            internal abstract int LineDwellMinutes(Entity line);
            internal abstract bool TryViewerPosition(out float3 position);
            internal abstract bool TryEntityPosition(Entity entity, out float3 position);

            internal abstract bool TryRelation(LineTrackChain chain, int waypointIndex, int cursorAtomIndex, out CursorAtomWindowRelation relation, out int startAtomIndex, out int endAtomIndexExclusive);
            internal abstract bool TryChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain);
            internal abstract bool TrySnapshot(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain chain, out VehicleTrackCursor cursor, out int currentControlEdgeIndex, out float ownLineAtomCoordinate, out int phaseEndAtomExclusive, out int traversalPhaseIndex, out int traversalPhaseStartAtomIndex, out int traversalPhaseEndAtomExclusive, out int nextTurnbackBoundaryAtomIndex);
            internal abstract bool TryCursor(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, LineTrackChain chain, out VehicleTrackCursor cursor);
            internal abstract bool TryWindow(LineTrackChain chain, int waypointIndex, int referenceAtomIndex, out int startAtomIndex, out int endAtomIndexExclusive);
            internal abstract bool TryPosition(LineTrackChain chain, int atomIndex, out float3 position);
            internal abstract void LogOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message);
            internal abstract bool ShouldLog(Dictionary<Entity, string> keyCache, Dictionary<Entity, uint> lastFrameCache, Entity vehicle, string key, uint nowFrame, uint cooldownFrames);
            internal abstract Entity Vehicle(Entity vehicle);
            internal abstract bool TryLineEntity(LineKey key, out Entity line);
            internal abstract Entity Stop(Entity waypoint);
            internal abstract Entity Anchor(Entity waypoint);
            internal abstract Entity AnchorFromStop(Entity stopEntity);
            internal abstract string EnsureSak(Entity anchor);
            internal abstract string Sak(Entity anchor);
            internal abstract string StationId(int order);
            internal abstract string StationName(Entity stopEntity);
            internal abstract string Name(Entity entity);
            internal abstract ulong Signature(DynamicBuffer<RouteWaypoint> waypoints);
            internal abstract bool TryWaypointIndex(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup);
            internal abstract string DraftKey(string lineId);
            internal abstract string LineId(Entity line);
            internal abstract bool TryTurnback(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary);
            internal abstract float EstimatePreparing(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, uint nowFrame);
            internal abstract float LapFrames(Entity line);
            internal abstract void InvalidatePanel();
        }

        private readonly Host m_Host;

        internal BroadcastAccess(Host host)
        {
            m_Host = host;
        }

        internal EntityManager EntityManager => m_Host.EntityManager;
        internal TimedLogger Log => m_Host.Log;
        internal SimulationSystem SimulationSystem => m_Host.SimulationSystem;
        internal CameraUpdateSystem CameraUpdateSystem => m_Host.CameraUpdateSystem;
        internal VehicleView VehicleView => m_Host.VehicleView;
        internal SelectPanel SelectionPanel => m_Host.SelectionPanel;
        internal NativeHashMap<Entity, int> CachedWaypointIndex => m_Host.CachedWaypointIndex;
        internal ClockSnapshot ClockSnapshot => m_Host.ClockSnapshot;
        internal bool TryStopArrivalFrame(Entity vehicle, out uint arrivalFrame)
            => m_Host.TryStopArrivalFrame(vehicle, out arrivalFrame);
        internal uint? ReadDepartureFrame(Entity vehicle) => m_Host.ReadDepartureFrame(vehicle);
        internal bool IsTimedStop(Entity vehicle) => m_Host.IsTimedStop(vehicle);
        internal void AppendOpenStopSessions(List<ActiveStopSession> sessions, Entity line)
            => m_Host.AppendOpenStopSessions(sessions, line);

        internal int LineDwellMinutes(Entity line) => m_Host.LineDwellMinutes(line);
        internal bool TryViewerPosition(out float3 position)
            => m_Host.TryViewerPosition(out position);

        internal bool TryEntityPosition(Entity entity, out float3 position)
            => m_Host.TryEntityPosition(entity, out position);

        internal bool TryRelation(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            out CursorAtomWindowRelation relation,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            return m_Host.TryRelation(
                chain,
                waypointIndex,
                cursorAtomIndex,
                out relation,
                out startAtomIndex,
                out endAtomIndexExclusive);
        }

        internal bool TryChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
            => m_Host.TryChain(line, waypoints, out chain);

        internal bool TrySnapshot(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor,
            out int currentControlEdgeIndex,
            out float ownLineAtomCoordinate,
            out int phaseEndAtomExclusive,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive,
            out int nextTurnbackBoundaryAtomIndex)
        {
            return m_Host.TrySnapshot(
                vehicle,
                line,
                waypoints,
                chain,
                out cursor,
                out currentControlEdgeIndex,
                out ownLineAtomCoordinate,
                out phaseEndAtomExclusive,
                out traversalPhaseIndex,
                out traversalPhaseStartAtomIndex,
                out traversalPhaseEndAtomExclusive,
                out nextTurnbackBoundaryAtomIndex);
        }

        internal bool TryCursor(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out VehicleTrackCursor cursor)
        {
            return m_Host.TryCursor(vehicle, line, waypoints, chain, out cursor);
        }

        internal bool TryWindow(
            LineTrackChain chain,
            int waypointIndex,
            int referenceAtomIndex,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            return m_Host.TryWindow(
                chain,
                waypointIndex,
                referenceAtomIndex,
                out startAtomIndex,
                out endAtomIndexExclusive);
        }

        internal bool TryPosition(LineTrackChain chain, int atomIndex, out float3 position)
            => m_Host.TryPosition(chain, atomIndex, out position);

        internal void LogOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
            => m_Host.LogOnce(cache, vehicle, key, message);

        internal bool ShouldLog(
            Dictionary<Entity, string> keyCache,
            Dictionary<Entity, uint> lastFrameCache,
            Entity vehicle,
            string key,
            uint nowFrame,
            uint cooldownFrames)
        {
            return m_Host.ShouldLog(keyCache, lastFrameCache, vehicle, key, nowFrame, cooldownFrames);
        }

        internal Entity Vehicle(Entity vehicle) => m_Host.Vehicle(vehicle);
        internal bool TryLineEntity(LineKey key, out Entity line) => m_Host.TryLineEntity(key, out line);
        internal Entity Stop(Entity waypoint) => m_Host.Stop(waypoint);
        internal Entity Anchor(Entity waypoint) => m_Host.Anchor(waypoint);
        internal Entity AnchorFromStop(Entity stopEntity) => m_Host.AnchorFromStop(stopEntity);
        internal string EnsureSak(Entity anchor) => m_Host.EnsureSak(anchor);
        internal string Sak(Entity anchor) => m_Host.Sak(anchor);
        internal string StationId(int order) => m_Host.StationId(order);
        internal string StationName(Entity stopEntity) => m_Host.StationName(stopEntity);
        internal string Name(Entity entity) => m_Host.Name(entity);
        internal ulong Signature(DynamicBuffer<RouteWaypoint> waypoints) => m_Host.Signature(waypoints);
        internal bool TryWaypointIndex(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup) => m_Host.TryWaypointIndex(line, waypoints, out lookup);
        internal string DraftKey(string lineId) => m_Host.DraftKey(lineId);
        internal string LineId(Entity line) => m_Host.LineId(line);
        internal bool TryTurnback(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => m_Host.TryTurnback(chain, boundary, out stationBoundary);

        internal float EstimatePreparing(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, uint nowFrame)
            => m_Host.EstimatePreparing(vehicle, line, waypoints, nowFrame);

        internal float LapFrames(Entity line) => m_Host.LapFrames(line);

        internal void InvalidatePanel() => m_Host.InvalidatePanel();

        internal AudioMixerGroup WorldMixerGroup(FieldInfo worldGroupField)
        {
            AudioManager audioManager = AudioManager.instance;
            if (audioManager == null || worldGroupField == null)
            {
                return null;
            }

            try
            {
                return worldGroupField.GetValue(audioManager) as AudioMixerGroup;
            }
            catch
            {
                return null;
            }
        }
    }
}
