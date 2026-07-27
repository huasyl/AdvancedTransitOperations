using System;
using Game.Routes;
using Unity.Entities;
using WorkbenchBackendService = RapidTransitMod.Broadcasting.WorkbenchBackend.Workbench;

namespace RapidTransitMod.Broadcasting
{
    internal sealed class Runtime
    {
        private readonly BroadcastAccess m_Access;
        private readonly Config m_Config;
        private readonly Stations m_Stations;
        private readonly Playback m_Playback;
        private readonly Diagnostics m_Diagnostics;
        private readonly Vehicles m_Vehicles;
        private readonly Platforms m_Platforms;

        internal const int BroadcastApproachRemainingAtomThreshold = 4;

        internal Runtime(BroadcastAccess.Host host, WorkbenchBackendService workbench)
        {
            m_Access = new BroadcastAccess(host ?? throw new ArgumentNullException(nameof(host)));
            if (workbench == null)
            {
                throw new ArgumentNullException(nameof(workbench));
            }

            m_Config = new Config(workbench.RuntimeConfig);
            m_Stations = new Stations(m_Access, m_Config);
            m_Playback = new Playback(m_Access, m_Config);
            m_Diagnostics = new Diagnostics(m_Access);
            m_Vehicles = new Vehicles(m_Access, m_Config, m_Stations, m_Playback, m_Diagnostics);
            m_Platforms = new Platforms(m_Access, m_Config, m_Stations, m_Playback, m_Diagnostics);
        }

        internal Stations Stations => m_Stations;

        internal void StopOpened(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex)
        {
            m_Vehicles.StopOpened(vehicle, line, waypoints, currentWaypointIndex);
        }

        internal void ServiceEnded(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int previousWaypointIndex)
        {
            m_Vehicles.ServiceEnded(vehicle, line, waypoints, previousWaypointIndex);
        }

        internal void BypassWaiting(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex)
        {
            m_Vehicles.BypassWaiting(vehicle, line, waypoints, currentWaypointIndex);
        }

        internal void Preparing(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool atOrigin,
            uint nowFrame)
        {
            m_Platforms.Preparing(vehicle, line, waypoints, atOrigin, nowFrame);
        }

        internal void Origin(Entity line, DynamicBuffer<RouteWaypoint> waypoints, bool busy)
        {
            m_Platforms.Origin(line, waypoints, busy);
        }

        internal void StateChanged(
            Entity vehicle,
            VehicleState previousState,
            VehicleState currentState)
        {
            m_Platforms.StateChanged(vehicle, previousState, currentState);
        }

        internal void Running(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            bool boarding)
        {
            if (!m_Config.Enabled)
            {
                return;
            }

            string lineId = m_Access.DraftKey(m_Access.LineId(line));
            Config.LineFlags flags = m_Config.Flags(lineId);
            if (!flags.Any)
            {
                return;
            }

            bool vehicleTracked = flags.HasVehicle && m_Vehicles.ShouldPlay(vehicle);
            bool needsContext = vehicleTracked || flags.HasPlatform;
            FrameContext context = default;
            bool hasContext = needsContext
                && FrameContexts.TryBuild(
                    m_Access,
                    m_Stations,
                    vehicle,
                    line,
                    waypoints,
                    currentWaypointIndex,
                    out context);

            m_Platforms.Running(
                vehicle,
                line,
                waypoints,
                boarding,
                flags,
                hasContext,
                context);
            m_Vehicles.Running(
                vehicle,
                line,
                waypoints,
                boarding,
                vehicleTracked,
                hasContext,
                context);
        }

        internal void Tick(uint nowFrame, bool sourceSweep)
        {
            if (!m_Config.Enabled)
            {
                if (m_Playback.HasActive || m_Platforms.HasState)
                {
                    Clear();
                }
                return;
            }

            m_Platforms.Tick(nowFrame, sourceSweep);
            m_Playback.Tick(nowFrame);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
            {
                return;
            }

            m_Playback.RemoveVehicle(vehicle);
            m_Vehicles.Remove(vehicle);
            m_Platforms.Remove(vehicle);
            m_Stations.RemoveVehicle(vehicle);
            m_Diagnostics.Remove(vehicle);
            m_Access.InvalidatePanel();
        }

        internal void Clear()
        {
            m_Config.ClearFlags();
            m_Playback.Clear();
            m_Vehicles.Clear();
            m_Platforms.Clear();
            m_Stations.Clear();
            m_Diagnostics.Clear();
            m_Access.InvalidatePanel();
        }

        internal void ClearLineChecks()
        {
            m_Platforms.ClearLineChecks();
        }

        internal void RemoveAsset(string assetName)
        {
            m_Playback.RemoveAsset(assetName);
        }

        internal void RemoveAsset(ModeScope scope, string assetName)
        {
            m_Playback.RemoveAsset(scope, assetName);
        }

        internal void RemoveAllAssets()
        {
            m_Playback.RemoveAllAssets();
            m_Platforms.ClearAssetState();
        }

        internal void RemoveAllAssets(ModeScope scope)
        {
            m_Playback.RemoveAllAssets(scope);
            m_Platforms.ClearAssetState(scope);
        }

        internal void ApplyVolume()
        {
            m_Playback.ApplyVolume();
        }

        internal string AssetName(BroadcastWorkbenchRuleNodeDto node, TriggerContext context)
        {
            return m_Playback.AssetName(node, context);
        }

        internal string EventText(Entity vehicle)
        {
            return m_Playback.Text(vehicle);
        }

        internal bool TryPanelContext(
            Entity vehicle,
            Entity line,
            out string currentStationName,
            out string nextStationName,
            out string terminalStationName)
        {
            return m_Stations.TryPanelContext(vehicle, line, out currentStationName, out nextStationName, out terminalStationName);
        }
    }
}
