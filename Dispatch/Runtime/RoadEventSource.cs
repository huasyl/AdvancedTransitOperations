using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RoadEventSource : IDisposable
    {
        [Flags]
        private enum RoadChangeMask : byte
        {
            None = 0,
            OfficialBoardingChanged = 1 << 0,
            MovingChanged = 1 << 1,
            RouteChanged = 1 << 2
        }

        private struct RoadBaseline
        {
            public bool InputValid;
            public bool OfficialBoarding;
            public bool MovingKnown;
            public bool MovingForDeparture;
            public Entity CurrentRoute;
            public bool OriginTargetMatched;
        }

        private struct RoadFrameRow
        {
            public Entity Vehicle;
            public Entity RegisteredLine;
            public Entity CurrentRoute;
            public VehicleState RegistryState;
            public RuntimeDemandMask Demands;
            public RoadChangeMask Changes;
            public bool InputValid;
            public bool IsCompilable;
            public bool IsSource;
            public uint SourceFrame;
            public bool HasPublicTransport;
            public PublicTransport PublicTransport;
            public bool PublicTransportWritten;
            public bool HasTarget;
            public Target Target;
            public bool MovingKnown;
            public bool MovingForDeparture;
            public int CachedWaypoint;
            public bool OriginTargetMatched;
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly List<Entity> m_SourceVehicles = new List<Entity>(512);
        private readonly Dictionary<Entity, int> m_SourceSlots = new Dictionary<Entity, int>(512);
        private readonly Dictionary<Entity, RoadBaseline> m_Baselines = new Dictionary<Entity, RoadBaseline>(512);
        private readonly Dictionary<Entity, RuntimeDemandMask> m_Demands = new Dictionary<Entity, RuntimeDemandMask>(512);
        private readonly List<RoadFrameRow> m_FrameRows = new List<RoadFrameRow>(512);
        private readonly Dictionary<Entity, int> m_FrameRowIndex = new Dictionary<Entity, int>(512);
        private readonly Dictionary<Entity, DynamicBuffer<RouteWaypoint>> m_WaypointBuffers = new Dictionary<Entity, DynamicBuffer<RouteWaypoint>>(64);
        private readonly List<Entity> m_StaleVehicles = new List<Entity>(32);

        private const float DepartureMovingSpeedSq = 0.01f;

        public RoadEventSource(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void BeginFrame()
        {
            m_FrameRows.Clear();
            m_FrameRowIndex.Clear();
            m_WaypointBuffers.Clear();
            m_StaleVehicles.Clear();
        }

        public void Collect(uint frame)
        {
            for (int i = 0; i < m_SourceVehicles.Count; i++)
            {
                Entity vehicle = m_SourceVehicles[i];
                if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                {
                    m_StaleVehicles.Add(vehicle);
                    continue;
                }

                int rowIndex = EnsureSourceRow(vehicle, frame);
                if (rowIndex < 0)
                    continue;

                RoadFrameRow row = m_FrameRows[rowIndex];
                if ((frame & 15u) == 1u && row.RegistryState == VehicleState.Running)
                {
                    ProbeRunningOrigin(ref row);
                    UpdateBaseline(row);
                }
                m_FrameRows[rowIndex] = row;
                if (row.CurrentRoute != row.RegisteredLine)
                    m_Runtime.m_VehicleRegistrar.ObserveRoadRoute(vehicle);
            }

            for (int i = 0; i < m_StaleVehicles.Count; i++)
                RemoveVehicle(m_StaleVehicles[i]);
            m_StaleVehicles.Clear();
        }

        public void CompileSourceRows(RuntimeFramePlan framePlan, uint frame)
        {
            for (int i = 0; i < m_FrameRows.Count; i++)
            {
                RoadFrameRow row = m_FrameRows[i];
                if (!row.IsCompilable || !row.IsSource || row.SourceFrame != frame || !row.InputValid)
                    continue;

                row.Demands = ReadDemand(row.Vehicle);
                m_FrameRows[i] = row;
                m_Runtime.m_RuntimeHotPathProbe.CountDemand(row.Demands);

                if (row.RegistryState == VehicleState.Retiring)
                {
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Retire);
                    continue;
                }

                if ((frame & 15u) == 1u && row.RegistryState == VehicleState.Preparing)
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Dispatch);
                if ((frame & 15u) == 1u
                    && row.RegistryState == VehicleState.Running
                    && row.OriginTargetMatched)
                {
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Dispatch);
                }
                if ((row.Changes & RoadChangeMask.OfficialBoardingChanged) != 0)
                {
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Stop);
                    if (row.RegistryState == VehicleState.Running)
                        framePlan.AddStage(row.Vehicle, RuntimeStageMask.Dispatch);
                }
                if ((row.Changes & RoadChangeMask.RouteChanged) != 0
                    && row.RegistryState != VehicleState.Running)
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Dispatch);
                if ((row.Demands & RuntimeDemandMask.DeparturePending) != 0)
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Stop);
                if (row.RegistryState != VehicleState.Running
                    && (row.Demands & RuntimeDemandMask.OriginCandidate) != 0)
                    framePlan.AddStage(row.Vehicle, RuntimeStageMask.Dispatch);
            }
        }

        public bool TryBuildStopInput(
            RuntimeFramePlan framePlan,
            FramePlanEntry entry,
            uint nowFrame,
            Func<Entity, bool> hasOpenStopSession,
            Func<Entity, bool> hasInvalidatedRecovery,
            Func<Entity, bool> isDeparturePending,
            Func<Entity, uint, bool> forcedMidStopGraceActive,
            out StopInput input)
        {
            input = default;
            if (!TryGetRow(entry.Vehicle, out RoadFrameRow row) || !row.IsCompilable)
                return false;

            bool hasWaypoints = TryGetWaypointsForRoute(
                row.CurrentRoute,
                out _,
                out DynamicBuffer<RouteWaypoint> waypoints);
            int waypointCount = hasWaypoints ? waypoints.Length : 0;
            int previousWaypoint = row.CachedWaypoint;
            int currentWaypoint = -1;
            bool targetResolved = hasWaypoints
                && TryResolveTargetWaypoint(ref row, waypoints, out currentWaypoint);
            int lastStopWaypoint = hasWaypoints
                ? FindLastStopWaypoint(waypoints)
                : -1;
            int rowIndex = m_FrameRowIndex[row.Vehicle];
            m_FrameRows[rowIndex] = row;

            input = new StopInput(
                row.Vehicle,
                row.RegisteredLine,
                row.SourceFrame == 0 ? nowFrame : row.SourceFrame,
                row.RegistryState,
                row.InputValid
                    && row.HasPublicTransport
                    && row.CurrentRoute == row.RegisteredLine
                    && targetResolved
                    && waypointCount >= 2,
                OfficialBoarding(row),
                m_Runtime.m_VehicleView.TryGetCooldown(row.Vehicle, out uint cooldown)
                    && nowFrame < cooldown,
                previousWaypoint,
                targetResolved ? currentWaypoint : -1,
                targetResolved ? currentWaypoint : -1,
                waypointCount,
                lastStopWaypoint,
                isDeparturePending(row.Vehicle) && row.MovingKnown,
                isDeparturePending(row.Vehicle) && row.MovingForDeparture,
                suppressBoardingGhost: false);
            return true;
        }

        public bool TryBuildDispatchInput(
            FramePlanEntry entry,
            uint nowFrame,
            IReadOnlyDictionary<Entity, StopFrameState> stopStates,
            IReadOnlyDictionary<Entity, BypassControlResult> bypassControls,
            out DispatchInput input)
        {
            input = default;
            if (!TryGetRow(entry.Vehicle, out RoadFrameRow row)
                || !row.IsCompilable
                || row.RegistryState == VehicleState.Retiring)
            return false;

            bool hasWaypoints = TryGetWaypointsForRoute(
                row.CurrentRoute,
                out Entity route,
                out DynamicBuffer<RouteWaypoint> waypoints);
            int waypointCount = hasWaypoints ? waypoints.Length : 0;
            int currentWaypoint = -1;
            bool running = row.RegistryState == VehicleState.Running;
            bool targetResolved = running
                ? row.OriginTargetMatched
                : hasWaypoints && TryResolveTargetWaypoint(ref row, waypoints, out currentWaypoint);
            if (running && targetResolved)
                currentWaypoint = 0;
            int previousWaypoint = row.CachedWaypoint;
            bool hasStopState = stopStates.TryGetValue(row.Vehicle, out StopFrameState stop);
            bool boarding = running && hasStopState
                ? stop.Boarding
                : OfficialBoarding(row);
            int targetMinute = running
                && m_Runtime.m_VehicleView.TryGetTarget(row.Vehicle, out int target)
                ? target
                : -1;
            bool targetAtOrigin = running
                ? row.OriginTargetMatched
                : targetResolved && currentWaypoint == 0;
            bool runningOriginDetail = running && targetAtOrigin;
            bool preparingAtOrigin = row.RegistryState == VehicleState.Preparing
                && targetResolved
                && currentWaypoint == 0
                && boarding;
            bool atOrigin = targetAtOrigin && boarding;
            bool hasTarget = running
                ? targetAtOrigin
                : row.HasTarget && row.Target.m_Target != Entity.Null;
            bool originBusy = false;
            bool preparingRouteNeedsRepair = row.RegistryState == VehicleState.Preparing
                && (nowFrame & 15u) == 1u
                && !preparingAtOrigin;
            bool shouldEvaluateOriginSettle = runningOriginDetail;
            bool originSettleReady = atOrigin;
            bool settledAtOrigin = atOrigin;
            bool forcedAtOrigin = false;
            float travelledDistance = -1f;
            float observedLapDistance = -1f;
            BypassControlResult bypass = new BypassControlResult(
                false,
                row.Vehicle,
                route,
                targetResolved ? currentWaypoint : -1,
                false,
                false,
                Entity.Null,
                true,
                null);
            int rowIndex = m_FrameRowIndex[row.Vehicle];
            m_FrameRows[rowIndex] = row;
            input = new DispatchInput(
                row.Vehicle,
                row.RegisteredLine,
                route,
                row.InputValid
                    && row.HasPublicTransport
                    && hasTarget
                    && targetResolved
                    && route == row.RegisteredLine
                    && waypointCount >= 2,
                boarding,
                previousWaypoint,
                targetResolved ? currentWaypoint : -1,
                waypointCount,
                atOrigin,
                targetAtOrigin,
                preparingAtOrigin,
                originBusy,
                preparingRouteNeedsRepair,
                shouldEvaluateOriginSettle,
                runningOriginDetail,
                originSettleReady,
                settledAtOrigin,
                forcedAtOrigin,
                false,
                row.MovingForDeparture,
                false,
                travelledDistance,
                observedLapDistance,
                stop.HadStopSession,
                stop.BoardingChanged,
                bypass);
            return true;
        }

        public void SetDemand(Entity vehicle, RuntimeDemandMask demand, bool active)
        {
            if (vehicle == Entity.Null || demand == RuntimeDemandMask.None)
                return;

            RuntimeDemandMask current = ReadDemand(vehicle);
            current = active ? current | demand : current & ~demand;
            if (current == RuntimeDemandMask.None)
                m_Demands.Remove(vehicle);
            else
                m_Demands[vehicle] = current;
        }

        public void ClearDemands(RuntimeDemandMask demand)
        {
            if (demand == RuntimeDemandMask.None)
                return;

            m_StaleVehicles.Clear();
            foreach (KeyValuePair<Entity, RuntimeDemandMask> pair in m_Demands)
                m_StaleVehicles.Add(pair.Key);
            for (int i = 0; i < m_StaleVehicles.Count; i++)
                SetDemand(m_StaleVehicles[i], demand, false);
            m_StaleVehicles.Clear();
        }

        public void RegisterSource(Entity vehicle, Entity line, int waypoint)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return;

            if (!m_SourceSlots.ContainsKey(vehicle))
            {
                m_SourceSlots.Add(vehicle, m_SourceVehicles.Count);
                m_SourceVehicles.Add(vehicle);
            }

            m_Baselines.Remove(vehicle);
            if (m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
            {
                RoadFrameRow row = m_FrameRows[rowIndex];
                row.RegisteredLine = line;
                row.CachedWaypoint = waypoint;
                row.IsCompilable = line != Entity.Null;
                m_FrameRows[rowIndex] = row;
            }
            CommitWaypoint(vehicle, waypoint);
        }

        public void RebindSource(Entity vehicle)
        {
            m_Baselines.Remove(vehicle);
            if (!m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
                return;

            RoadFrameRow row = m_FrameRows[rowIndex];
            row.RegisteredLine = Entity.Null;
            row.CurrentRoute = Entity.Null;
            row.RegistryState = default;
            row.Demands = RuntimeDemandMask.None;
            row.Changes = RoadChangeMask.None;
            row.InputValid = false;
            row.IsCompilable = false;
            row.HasPublicTransport = false;
            row.PublicTransport = default;
            row.MovingKnown = false;
            row.MovingForDeparture = false;
            row.CachedWaypoint = -1;
            m_FrameRows[rowIndex] = row;
        }

        public void CommitWaypoint(Entity vehicle, int waypoint)
        {
            if (vehicle == Entity.Null)
                return;

            m_Runtime.m_CachedWpIdx[vehicle] = waypoint;
            if (!m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
                return;

            RoadFrameRow row = m_FrameRows[rowIndex];
            row.CachedWaypoint = waypoint;
            m_FrameRows[rowIndex] = row;
        }

        public bool TryReadPublicTransportForWrite(Entity vehicle, out PublicTransport value)
        {
            int rowIndex = EnsureFrameRow(vehicle);
            if (rowIndex >= 0 && m_FrameRows[rowIndex].HasPublicTransport)
            {
                value = m_FrameRows[rowIndex].PublicTransport;
                return true;
            }

            value = default;
            return false;
        }

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport value, uint frame)
        {
            int rowIndex = EnsureFrameRow(vehicle);
            if (rowIndex < 0)
                return;

            RoadFrameRow row = m_FrameRows[rowIndex];
            row.PublicTransport = value;
            row.HasPublicTransport = true;
            row.PublicTransportWritten = true;
            row.SourceFrame = row.SourceFrame == 0 ? frame : row.SourceFrame;
            m_FrameRows[rowIndex] = row;
            UpdateBaseline(row);
        }

        public void AppendPreparingTargetWrite(Entity vehicle, Target value)
        {
            int rowIndex = EnsureFrameRow(vehicle);
            if (rowIndex < 0)
                return;

            RoadFrameRow row = m_FrameRows[rowIndex];
            row.Target = value;
            row.HasTarget = true;
            m_FrameRows[rowIndex] = row;
        }

        public bool RebaselineStartup(IReadOnlyList<Entity> vehicles, uint frame)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                int rowIndex = EnsureSourceRow(vehicles[i], frame);
                if (rowIndex < 0)
                    return false;

                RoadFrameRow row = m_FrameRows[rowIndex];
                if (!row.InputValid || row.CurrentRoute != row.RegisteredLine)
                    return false;
            }

            return true;
        }

        public bool TryReadCurrentWaypoint(
            Entity vehicle,
            Entity line,
            out bool boarding,
            out int waypoint)
        {
            boarding = false;
            waypoint = -1;
            int rowIndex = EnsureFrameRow(vehicle);
            if (rowIndex < 0)
                return false;

            RoadFrameRow row = m_FrameRows[rowIndex];
            if (line == Entity.Null
                || row.CurrentRoute != line
                || !TryGetWaypointsForRoute(row.CurrentRoute, out _, out DynamicBuffer<RouteWaypoint> waypoints)
                || !TryResolveTargetWaypoint(ref row, waypoints, out waypoint))
            {
                m_FrameRows[rowIndex] = row;
                return false;
            }

            boarding = OfficialBoarding(row);
            m_FrameRows[rowIndex] = row;
            return true;
        }

        public bool PrepareStartupStopSource(
            Entity vehicle,
            Entity line,
            uint frame,
            out bool boarding,
            out int waypoint,
            out int waypointCount)
        {
            boarding = false;
            waypoint = -1;
            waypointCount = 0;
            if (!TryGetRow(vehicle, out RoadFrameRow row)
                || row.RegisteredLine != line
                || row.SourceFrame != frame
                || !TryGetWaypointsForRoute(row.CurrentRoute, out _, out DynamicBuffer<RouteWaypoint> waypoints))
            {
                return false;
            }

            boarding = OfficialBoarding(row);
            waypointCount = waypoints.Length;
            if (!TryResolveTargetWaypoint(ref row, waypoints, out waypoint))
                waypoint = row.CachedWaypoint;
            if (row.RegistryState == VehicleState.Holding)
                waypoint = 0;

            row.CachedWaypoint = waypoint;
            m_FrameRows[m_FrameRowIndex[vehicle]] = row;
            CommitWaypoint(vehicle, waypoint);
            return true;
        }

        public void RemoveVehicle(Entity vehicle)
        {
            if (m_SourceSlots.ContainsKey(vehicle)
                || m_Demands.ContainsKey(vehicle)
                || m_FrameRowIndex.ContainsKey(vehicle))
            {
                RemoveRetireSource(vehicle);
            }
            RemoveRetireTerminal(vehicle);
        }

        public void RemoveRetireSource(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            if (m_SourceSlots.TryGetValue(vehicle, out int slot))
            {
                int last = m_SourceVehicles.Count - 1;
                Entity lastVehicle = m_SourceVehicles[last];
                if (slot != last)
                {
                    m_SourceVehicles[slot] = lastVehicle;
                    m_SourceSlots[lastVehicle] = slot;
                }
                m_SourceVehicles.RemoveAt(last);
                m_SourceSlots.Remove(vehicle);
            }

            m_Demands.Remove(vehicle);
            m_FrameRowIndex.Remove(vehicle);
        }

        public void RemoveRetireTerminal(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Baselines.Remove(vehicle);
        }

        public void InvalidateLine(Entity line)
        {
            if (line != Entity.Null)
                m_WaypointBuffers.Remove(line);
        }

        public void Clear()
        {
            m_SourceVehicles.Clear();
            m_SourceSlots.Clear();
            m_Baselines.Clear();
            m_Demands.Clear();
            m_FrameRows.Clear();
            m_FrameRowIndex.Clear();
            m_WaypointBuffers.Clear();
            m_StaleVehicles.Clear();
        }

        public void Dispose() => Clear();

        private int EnsureSourceRow(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return -1;

            if (m_FrameRowIndex.TryGetValue(vehicle, out int existing))
            {
                RoadFrameRow existingRow = m_FrameRows[existing];
                RefreshSourceHeader(ref existingRow, frame);
                m_FrameRows[existing] = existingRow;
                return existing;
            }

            RoadFrameRow row = new RoadFrameRow
            {
                Vehicle = vehicle,
                CachedWaypoint = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int waypoint) ? waypoint : -1
            };
            RefreshSourceHeader(ref row, frame);
            m_FrameRowIndex.Add(vehicle, m_FrameRows.Count);
            m_FrameRows.Add(row);
            return m_FrameRows.Count - 1;
        }

        private int EnsureFrameRow(Entity vehicle)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return -1;

            if (m_FrameRowIndex.TryGetValue(vehicle, out int existing))
                return existing;

            RoadFrameRow row = new RoadFrameRow
            {
                Vehicle = vehicle,
                CachedWaypoint = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int waypoint) ? waypoint : -1
            };
            RefreshSourceHeader(ref row, m_Runtime.m_SimulationSystem.frameIndex);
            m_FrameRowIndex.Add(vehicle, m_FrameRows.Count);
            m_FrameRows.Add(row);
            return m_FrameRows.Count - 1;
        }

        private void RefreshSourceHeader(ref RoadFrameRow row, uint frame)
        {
            RoadBaseline previous = m_Baselines.TryGetValue(row.Vehicle, out RoadBaseline baseline)
                ? baseline
                : default;
            bool publicTransportWritten = row.PublicTransportWritten;
            PublicTransport writtenPublicTransport = row.PublicTransport;
            RoadFrameRow fresh = new RoadFrameRow
            {
                Vehicle = row.Vehicle,
                CachedWaypoint = row.CachedWaypoint
            };
            ReadNarrow(ref fresh);
            fresh.RegisteredLine = m_Runtime.m_VehicleView.TryGetLine(row.Vehicle, out Entity line)
                ? line
                : Entity.Null;
            fresh.RegistryState = m_Runtime.m_VehicleView.TryGetState(row.Vehicle, out VehicleState state)
                ? state
                : default;
            fresh.Demands = ReadDemand(row.Vehicle);
            fresh.IsCompilable = fresh.InputValid && fresh.RegisteredLine != Entity.Null;
            fresh.IsSource = true;
            fresh.SourceFrame = frame;
            fresh.OriginTargetMatched = previous.OriginTargetMatched;
            if (publicTransportWritten)
            {
                fresh.PublicTransport = writtenPublicTransport;
                fresh.HasPublicTransport = true;
                fresh.PublicTransportWritten = true;
            }
            fresh.Changes = (previous.InputValid && previous.OfficialBoarding != OfficialBoarding(fresh)
                ? RoadChangeMask.OfficialBoardingChanged
                : RoadChangeMask.None)
                | (previous.InputValid && previous.MovingKnown && previous.MovingForDeparture != fresh.MovingForDeparture
                    ? RoadChangeMask.MovingChanged
                    : RoadChangeMask.None)
                | (previous.InputValid && previous.CurrentRoute != fresh.CurrentRoute
                    ? RoadChangeMask.RouteChanged
                    : RoadChangeMask.None);
            row = fresh;
            UpdateBaseline(row);
        }

        private void ReadNarrow(ref RoadFrameRow row)
        {
            Entity vehicle = row.Vehicle;
            row.InputValid = m_Runtime.EntityManager.Exists(vehicle);
            row.HasPublicTransport = m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle);
            if (row.HasPublicTransport)
                row.PublicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            row.CurrentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            row.MovingKnown = m_Runtime.EntityManager.HasComponent<Moving>(vehicle);
            row.MovingForDeparture = row.MovingKnown
                && math.lengthsq(m_Runtime.EntityManager.GetComponentData<Moving>(vehicle).m_Velocity) > DepartureMovingSpeedSq;
        }

        private void UpdateBaseline(RoadFrameRow row)
        {
            m_Baselines[row.Vehicle] = new RoadBaseline
            {
                InputValid = row.InputValid,
                OfficialBoarding = OfficialBoarding(row),
                MovingKnown = row.MovingKnown,
                MovingForDeparture = row.MovingForDeparture,
                CurrentRoute = row.CurrentRoute,
                OriginTargetMatched = row.OriginTargetMatched
            };
        }

        private void ProbeRunningOrigin(ref RoadFrameRow row)
        {
            row.OriginTargetMatched = false;
            if (row.RegisteredLine == Entity.Null
                || row.CurrentRoute != row.RegisteredLine
                || !TryGetWaypointsForRoute(
                    row.CurrentRoute,
                    out _,
                    out DynamicBuffer<RouteWaypoint> waypoints)
                || waypoints.Length < 2)
            {
                return;
            }

            int waypoint = -1;
            if (TryResolveTargetWaypoint(ref row, waypoints, out waypoint)
                && waypoint == 0
                && row.Target.m_Target == waypoints[0].m_Waypoint)
            {
                row.OriginTargetMatched = true;
            }
        }

        private bool TryGetRow(Entity vehicle, out RoadFrameRow row)
        {
            if (m_FrameRowIndex.TryGetValue(vehicle, out int index))
            {
                row = m_FrameRows[index];
                return true;
            }

            row = default;
            return false;
        }

        private bool TryGetWaypointsForRoute(
            Entity route,
            out Entity resolvedRoute,
            out DynamicBuffer<RouteWaypoint> waypoints)
        {
            resolvedRoute = route;
            waypoints = default;
            if (route == Entity.Null || !m_Runtime.EntityManager.Exists(route))
                return false;

            if (m_WaypointBuffers.TryGetValue(route, out waypoints))
                return waypoints.Length >= 2;

            if (!m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(route))
                return false;

            waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(route, true);
            m_WaypointBuffers[route] = waypoints;
            return waypoints.Length >= 2;
        }

        private bool TryResolveTargetWaypoint(
            ref RoadFrameRow row,
            DynamicBuffer<RouteWaypoint> waypoints,
            out int waypoint)
        {
            waypoint = -1;
            if (!row.HasTarget)
            {
                Entity vehicle = row.Vehicle;
                row.HasTarget = m_Runtime.EntityManager.HasComponent<Target>(vehicle);
                if (row.HasTarget)
                    row.Target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            }

            if (!row.HasTarget
                || row.Target.m_Target == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<Waypoint>(row.Target.m_Target))
            {
                return false;
            }

            int index = m_Runtime.EntityManager.GetComponentData<Waypoint>(row.Target.m_Target).m_Index;
            if (index < 0 || index >= waypoints.Length || waypoints[index].m_Waypoint != row.Target.m_Target)
                return false;

            waypoint = index;
            return true;
        }

        private int FindLastStopWaypoint(DynamicBuffer<RouteWaypoint> waypoints)
        {
            for (int i = waypoints.Length - 1; i >= 0; i--)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint != Entity.Null && m_Runtime.m_Resolve.Stop(waypoint) != Entity.Null)
                    return i;
            }

            return -1;
        }

        private RuntimeDemandMask ReadDemand(Entity vehicle)
        {
            return m_Demands.TryGetValue(vehicle, out RuntimeDemandMask demand)
                ? demand
                : RuntimeDemandMask.None;
        }

        private static bool OfficialBoarding(RoadFrameRow row)
        {
            return row.HasPublicTransport
                && (row.PublicTransport.m_State & PublicTransportFlags.Boarding) != 0;
        }
    }
}
