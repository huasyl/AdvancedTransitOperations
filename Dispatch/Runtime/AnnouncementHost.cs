using System;
using System.Collections.Generic;
using Colossal.Core;
using Game;
using Game.Audio;
using Game.Rendering;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using RapidTransitMod.Broadcasting;
using RapidTransitMod.Core;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using LineRunningVehicleSnapshot = RapidTransitMod.TrackProjection.LineRunningVehicleSnapshot;
using LineWaypointIndexLookup = RapidTransitMod.TrackModel.LineWaypointIndexLookup;

namespace RapidTransitMod
{
    internal readonly struct AnnouncementServices
    {
        internal readonly Broadcasting.Runtime Runtime;
        internal readonly Broadcasting.WorkbenchBackend.Workbench Workbench;

        internal AnnouncementServices(
            Broadcasting.Runtime runtime,
            Broadcasting.WorkbenchBackend.Workbench workbench)
        {
            Runtime = runtime;
            Workbench = workbench;
        }
    }

    internal sealed class AnnouncementHost
    {
        private readonly ModRuntimeHostSystem m_Host;

        internal AnnouncementHost(ModRuntimeHostSystem host)
        {
            m_Host = host;
        }

        internal AnnouncementServices Create()
        {
            Broadcasting.WorkbenchBackend.Workbench workbench =
                new Broadcasting.WorkbenchBackend.Workbench(
                    new Broadcasting.WorkbenchBackend.WorkbenchAccess(new WorkbenchPort(m_Host)));

            Broadcasting.Runtime runtime = new Broadcasting.Runtime(new RuntimePort(m_Host), workbench);
            workbench.Attach(runtime);
            return new AnnouncementServices(runtime, workbench);
        }

        private sealed class RuntimePort : Broadcasting.BroadcastAccess.Host
        {
            private readonly ModRuntimeHostSystem m_Host;

            internal RuntimePort(ModRuntimeHostSystem host)
            {
                m_Host = host;
            }

            internal override EntityManager EntityManager => m_Host.EntityManager;
            internal override TimedLogger Log => m_Host.log;
            internal override SimulationSystem SimulationSystem => m_Host.m_SimulationSystem;
            internal override CameraUpdateSystem CameraUpdateSystem => m_Host.m_CameraUpdateSystem;
            internal override VehicleView VehicleView => m_Host.m_VehicleView;
            internal override SelectPanel SelectionPanel => m_Host.m_SelectPanel;
            internal override NativeHashMap<Entity, int> CachedWaypointIndex => m_Host.m_CachedWpIdx;
            internal override ClockSnapshot ClockSnapshot => m_Host.m_SimClock.Snapshot;
            internal override void SubscribeClockChanged(Action<ClockSnapshot, ClockSnapshot> handler)
                => m_Host.m_SimClock.ClockChanged += handler;

            internal override bool TryRelation(
                LineTrackChain chain,
                int waypointIndex,
                int cursorAtomIndex,
                out CursorAtomWindowRelation relation,
                out int startAtomIndex,
                out int endAtomIndexExclusive)
            {
                return m_Host.m_WaypointIndex.TryRelation(
                    chain,
                    waypointIndex,
                    cursorAtomIndex,
                    out relation,
                    out startAtomIndex,
                    out endAtomIndexExclusive);
            }

            internal override bool TryChain(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineTrackChain chain)
                => m_Host.TrackModel.TryGetChainForLine(line, waypoints, out chain);

            internal override bool TrySnapshot(
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
                return m_Host.TrackProjection.TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
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

            internal override bool TryCursor(
                Entity vehicle,
                Entity line,
                DynamicBuffer<RouteWaypoint> waypoints,
                LineTrackChain chain,
                out VehicleTrackCursor cursor)
            {
                return m_Host.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor);
            }

            internal override bool TryWindow(
                LineTrackChain chain,
                int waypointIndex,
                int referenceAtomIndex,
                out int startAtomIndex,
                out int endAtomIndexExclusive)
            {
                return m_Host.m_WaypointIndex.TryWindow(
                    chain,
                    waypointIndex,
                    referenceAtomIndex,
                    out startAtomIndex,
                    out endAtomIndexExclusive);
            }

            internal override bool TryPosition(LineTrackChain chain, int atomIndex, out float3 position)
                => m_Host.TrackProjection.TryGetTrackAtomWorldPosition(chain, atomIndex, out position);

            internal override void LogOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
                => m_Host.m_RuntimeLog.Once(cache, vehicle, key, message);

            internal override bool ShouldLog(
                Dictionary<Entity, string> keyCache,
                Dictionary<Entity, uint> lastFrameCache,
                Entity vehicle,
                string key,
                uint nowFrame,
                uint cooldownFrames)
            {
                return m_Host.m_RuntimeLog.Cooldown(keyCache, lastFrameCache, vehicle, key, nowFrame, cooldownFrames);
            }

            internal override Entity Vehicle(Entity vehicle) => m_Host.m_Resolve.RuntimeVehicle(vehicle);
            internal override List<WorkbenchLineRuntime> Lines() => m_Host.Lines();
            internal override Entity Stop(Entity waypoint) => m_Host.m_Resolve.Stop(waypoint);
            internal override Entity Anchor(Entity waypoint) => m_Host.m_Resolve.Anchor(waypoint);
            internal override Entity AnchorFromStop(Entity stopEntity) => m_Host.m_Resolve.AnchorFromStop(stopEntity);
            internal override string EnsureSak(Entity anchor) => m_Host.m_Resolve.EnsureSak(anchor);
            internal override string Sak(Entity anchor) => m_Host.m_Resolve.Sak(anchor);
            internal override string StationId(int order) => m_Host.m_Resolve.StationId(order);
            internal override string StationName(Entity stopEntity) => m_Host.m_Resolve.StationName(stopEntity);
            internal override string Name(Entity entity) => m_Host.EntityName(entity);
            internal override ulong Signature(DynamicBuffer<RouteWaypoint> waypoints) => m_Host.m_LineProfile.ComputeWaypointSignature(waypoints);
            internal override bool TryTurnbacks(LineTrackChain chain, List<TrackTurnbackStationBoundary> stationBoundaries) => Turnbacks.TryCollectTurnbackStationBoundaries(chain, stationBoundaries);
            internal override bool TryWaypointIndex(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineWaypointIndexLookup lookup) => m_Host.m_WaypointIndex.TryLookup(line, waypoints, out lookup);
            internal override string DraftKey(string lineId) => RapidTransitMod.Dispatch.Workbench.Drafts.Key(lineId);
            internal override string LineId(Entity line) => m_Host.LineStableId(line);
            internal override bool TryTurnback(LineTrackChain chain, TurnbackBoundary boundary, out TrackTurnbackStationBoundary stationBoundary) => Turnbacks.TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary);

            internal override float EstimatePreparing(
                Entity vehicle,
                Entity line,
                DynamicBuffer<RouteWaypoint> waypoints,
                uint nowFrame)
            {
                return m_Host.m_LineTimes.Prep(vehicle, line, waypoints, LapFrames(line));
            }

            internal override float LapFrames(Entity line) => m_Host.m_LapCache.Read(line);
            internal override void InvalidatePanel() => m_Host.m_SelectPanel.Invalidate();
        }

        private sealed class WorkbenchPort : Broadcasting.WorkbenchBackend.Host
        {
            private readonly ModRuntimeHostSystem m_Host;

            internal WorkbenchPort(ModRuntimeHostSystem host)
            {
                m_Host = host;
            }

            internal override EntityManager EntityManager => m_Host.EntityManager;
            internal override TimedLogger Log => m_Host.log;
            internal override bool Enabled => m_Host.m_Features.Broadcast();
            internal override ulong Version => m_Host.m_WorkbenchBridge.Version;

            internal override void Next() => m_Host.m_WorkbenchBridge.NextVersion();
            internal override void Load() => m_Host.LoadWorkbench();
            internal override void Save() => m_Host.SaveWorkbench();
            internal override void Run(System.Action action) => MainThreadDispatcher.RunOnMainThread(action);
            internal override List<WorkbenchLineRuntime> Lines() => m_Host.Lines();
            internal override string StationName(Entity stopEntity) => m_Host.m_Resolve.StationName(stopEntity);
            internal override string Name(Entity entity) => m_Host.EntityName(entity);
            internal override string Error(System.Exception ex) => ModRuntimeHostSystem.DescribeError(ex);
        }
    }
}
