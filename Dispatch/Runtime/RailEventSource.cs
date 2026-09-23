using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RailEventSource : IDisposable
    {
        [Flags]
        private enum RailChangeMask : byte
        {
            None = 0,
            OfficialBoardingChanged = 1 << 0,
            MovingChanged = 1 << 1
        }

        private struct RailBaseline
        {
            public bool InputValid;
            public bool HasPublicTransport;
            public bool Boarding;
            public Entity Route;
            public bool MovingKnown;
            public bool Moving;
        }

        private struct RailFrameRow
        {
            public Entity Vehicle;
            public Entity RegisteredLine;
            public Entity CurrentRoute;
            public VehicleState RegistryState;
            public RuntimeDemandMask Demands;
            public RailChangeMask Changes;
            public bool InputValid;
            public bool IsCompilable;
            public bool IsSource;
            public uint SourceFrame;
            public bool HasPublicTransport;
            public bool Boarding;
            public bool MovingKnown;
            public bool Moving;
            public int CachedWaypoint;
            public int WaypointCount;
            public bool PathChanged;
            public bool DepartureFrameWritten;
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, RailBaseline> m_Baselines = new Dictionary<Entity, RailBaseline>();
        private readonly Dictionary<Entity, RuntimeDemandMask> m_Demands = new Dictionary<Entity, RuntimeDemandMask>();
        private readonly List<RailFrameRow> m_FrameRows = new List<RailFrameRow>();
        private readonly Dictionary<Entity, int> m_FrameRowIndex = new Dictionary<Entity, int>();
        private readonly List<int> m_RunningNoticeRows = new List<int>();
        private readonly List<int> m_PreparingNoticeRows = new List<int>();
        private readonly Dictionary<Entity, uint> m_PreparingWaypointLiveFrames = new Dictionary<Entity, uint>();
        private readonly List<Entity> m_DemandVehicles = new List<Entity>();
        private readonly List<Entity> m_StaleVehicles = new List<Entity>();
        private uint m_LastCollectedFrame = uint.MaxValue;

        private const float DepartureMovingSpeedSq = 0.01f;

        public RailEventSource(ModRuntimeHostSystem runtime, FrameEvents events)
        {
            m_Runtime = runtime;
        }

        public void BeginFrame()
        {
            m_FrameRows.Clear();
            m_FrameRowIndex.Clear();
            m_RunningNoticeRows.Clear();
            m_PreparingNoticeRows.Clear();
        }

        public void CollectIfDue(uint frame)
        {
            if ((frame & 15u) != 3u)
                return;

            m_LastCollectedFrame = frame;
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    int rowIndex = EnsureSourceRow(vehicle, frame);
                    if (rowIndex < 0)
                        continue;

                    RailFrameRow row = m_FrameRows[rowIndex];
                    m_Runtime.m_RuntimeHotPathProbe.CountSourceRow();
                    if (row.CurrentRoute != row.RegisteredLine)
                        m_Runtime.m_VehicleRegistrar.ObserveRailRoute(vehicle);
                }
            }
            finally
            {
                vehicles.Dispose();
                if ((frame & 1023u) == 3u)
                    PruneBaselines();
            }
        }

        public bool CollectedThisFrame(uint frame) => m_LastCollectedFrame == frame;

        internal int CollectedVehicleCount => m_FrameRows.Count;

        internal bool TryReadProjectionCurrentLane(Entity vehicle, out TrainCurrentLane currentLane)
        {
            currentLane = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return false;

            bool diagnostics = RuntimeHotPathProbe.Enabled();
            long started = diagnostics ? Stopwatch.GetTimestamp() : 0;
            Entity laneVehicle = vehicle;
            if (m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length != 0)
                    laneVehicle = layout[0].m_Vehicle;
            }

            bool hasCurrentLane = laneVehicle != Entity.Null
                && m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(laneVehicle);
            if (hasCurrentLane)
                currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(laneVehicle);
            m_Runtime.m_RuntimeHotPathProbe.CountNavigationDetailRead();
            if (diagnostics)
            {
                m_Runtime.m_RuntimeHotPathProbe.RecordProjectionRead(
                    ProjectionReadKind.CurrentLane,
                    Stopwatch.GetTimestamp() - started);
            }
            return hasCurrentLane;
        }

        internal bool HasProjectionPathWrite(Entity vehicle)
        {
            return TryGetRow(vehicle, out RailFrameRow row)
                && row.PathChanged;
        }

        internal bool TryReadProjectionNavigation(
            Entity vehicle,
            out DynamicBuffer<TrainNavigationLane> navigation)
        {
            navigation = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return false;

            bool diagnostics = RuntimeHotPathProbe.Enabled();
            long started = diagnostics ? Stopwatch.GetTimestamp() : 0;
            bool hasNavigation = m_Runtime.EntityManager.HasBuffer<TrainNavigationLane>(vehicle);
            if (hasNavigation)
                navigation = m_Runtime.EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
            if (diagnostics)
            {
                m_Runtime.m_RuntimeHotPathProbe.RecordProjectionRead(
                    ProjectionReadKind.Navigation,
                    Stopwatch.GetTimestamp() - started);
            }
            if (!hasNavigation)
                return false;

            m_Runtime.m_RuntimeHotPathProbe.CountNavigationDetailRead();
            return true;
        }

        internal bool TryReadProjectionPath(
            Entity vehicle,
            out PathOwner pathOwner,
            out DynamicBuffer<PathElement> pathElements)
        {
            pathOwner = default;
            pathElements = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle)
                || HasProjectionPathWrite(vehicle))
                return false;

            bool diagnostics = RuntimeHotPathProbe.Enabled();
            long started = diagnostics ? Stopwatch.GetTimestamp() : 0;
            bool hasPath = m_Runtime.EntityManager.HasComponent<PathOwner>(vehicle)
                && m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle);
            if (hasPath)
            {
                pathOwner = m_Runtime.EntityManager.GetComponentData<PathOwner>(vehicle);
                pathElements = m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true);
                m_Runtime.m_RuntimeHotPathProbe.CountNavigationDetailRead();
            }
            if (diagnostics)
            {
                m_Runtime.m_RuntimeHotPathProbe.RecordProjectionRead(
                    ProjectionReadKind.Path,
                    Stopwatch.GetTimestamp() - started);
            }
            return hasPath;
        }

        internal bool ReadSignalNavigation(
            Entity vehicle,
            Entity lane0,
            Entity lane1,
            Entity lane2,
            out byte forwardMask)
        {
            forwardMask = 0;
            if (vehicle == Entity.Null
                || !m_Runtime.EntityManager.Exists(vehicle)
                || !m_Runtime.EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
            {
                return false;
            }
            DynamicBuffer<TrainNavigationLane> navigation =
                m_Runtime.EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
            m_Runtime.m_RuntimeHotPathProbe.CountNavigationDetailRead();
            byte expectedMask = 0;
            if (lane0 != Entity.Null) expectedMask |= 1;
            if (lane1 != Entity.Null) expectedMask |= 2;
            if (lane2 != Entity.Null) expectedMask |= 4;
            for (int i = 0; i < navigation.Length; i++)
            {
                Entity lane = navigation[i].m_Lane;
                if (lane0 != Entity.Null && lane == lane0)
                    forwardMask |= 1;
                if (lane1 != Entity.Null && lane == lane1)
                    forwardMask |= 2;
                if (lane2 != Entity.Null && lane == lane2)
                    forwardMask |= 4;
                if (forwardMask == expectedMask)
                    break;
            }
            return true;
        }

        internal bool TryGetCollectedVehicle(
            int index,
            uint frame,
            out ManagedSourceVehicle source)
        {
            source = default;
            if (index < 0 || index >= m_FrameRows.Count)
                return false;
            RailFrameRow row = m_FrameRows[index];
            if (!row.IsSource
                || row.SourceFrame != frame
                || !row.InputValid
                || row.Vehicle == Entity.Null
                || row.RegisteredLine == Entity.Null)
            {
                return false;
            }
            source = new ManagedSourceVehicle(
                row.Vehicle,
                row.RegisteredLine,
                row.RegistryState);
            return true;
        }

        public void CompileSourceRows(RuntimeFramePlan framePlan, uint frame)
        {
            for (int i = 0; i < m_FrameRows.Count; i++)
            {
                RailFrameRow row = m_FrameRows[i];
                if (!row.IsCompilable || !row.IsSource || row.SourceFrame != frame || !row.InputValid)
                    continue;

                Entity line = row.RegisteredLine;
                VehicleState state = row.RegistryState;
                if (line == Entity.Null)
                {
                    continue;
                }

                row.Demands = ReadDemand(row.Vehicle);
                m_FrameRows[i] = row;
                m_Runtime.m_RuntimeHotPathProbe.CountDemand(row.Demands);

                if ((row.Changes & RailChangeMask.OfficialBoardingChanged) != 0 && state != VehicleState.Idle)
                {
                    framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Stop);
                    if (state == VehicleState.Running)
                        framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Dispatch);
                }
                if ((row.Demands & RuntimeDemandMask.DeparturePending) != 0)
                    framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Stop);

                switch (state)
                {
                    case VehicleState.Preparing:
                        framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Dispatch);
                        break;
                    case VehicleState.Running:
                        if ((row.Demands & (RuntimeDemandMask.BypassWatch | RuntimeDemandMask.BypassActive)) != 0)
                            framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Bypass);
                        if ((row.Demands & (RuntimeDemandMask.OriginCandidate | RuntimeDemandMask.InboundWatch)) != 0)
                            framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Dispatch);
                        break;
                    case VehicleState.Holding:
                    case VehicleState.Idle:
                        if ((row.Changes & RailChangeMask.MovingChanged) != 0 && row.Moving)
                            framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Dispatch);
                        break;
                }

                if (state == VehicleState.Running
                    && m_Runtime.m_Observation.IsSourceSliceDue(row.Vehicle, row.RegisteredLine, frame))
                {
                    framePlan.AddStage(row.Vehicle, i, RuntimeStageMask.Slice);
                }

                if (state == VehicleState.Running)
                    m_RunningNoticeRows.Add(i);
                else if (state == VehicleState.Preparing)
                    m_PreparingNoticeRows.Add(i);
            }
        }

        public int RunningNoticeCount => m_RunningNoticeRows.Count;
        public int PreparingNoticeCount => m_PreparingNoticeRows.Count;

        public bool TryGetRunningNotice(
            int noticeIndex,
            out Entity vehicle,
            out Entity line,
            out DynamicBuffer<RouteWaypoint> waypoints,
            out int waypointIndex,
            out bool boarding)
            => TryGetNotice(
                m_RunningNoticeRows,
                noticeIndex,
                out vehicle,
                out line,
                out waypoints,
                out waypointIndex,
                out boarding);

        public bool TryGetPreparingNotice(
            int noticeIndex,
            out Entity vehicle,
            out Entity line,
            out DynamicBuffer<RouteWaypoint> waypoints,
            out int waypointIndex,
            out bool boarding)
        {
            return TryGetNotice(
                m_PreparingNoticeRows,
                noticeIndex,
                out vehicle,
                out line,
                out waypoints,
                out waypointIndex,
                out boarding);
        }

        private bool TryGetNotice(
            List<int> noticeRows,
            int noticeIndex,
            out Entity vehicle,
            out Entity line,
            out DynamicBuffer<RouteWaypoint> waypoints,
            out int waypointIndex,
            out bool boarding)
        {
            vehicle = Entity.Null;
            line = Entity.Null;
            waypoints = default;
            waypointIndex = -1;
            boarding = false;
            if (noticeIndex < 0 || noticeIndex >= noticeRows.Count)
                return false;

            int rowIndex = noticeRows[noticeIndex];
            if (rowIndex < 0 || rowIndex >= m_FrameRows.Count)
                return false;

            RailFrameRow row = m_FrameRows[rowIndex];
            if (!row.IsCompilable || !row.IsSource || !row.InputValid || !TryGetWaypointsForRoute(row.CurrentRoute, out line, out waypoints))
                return false;

            vehicle = row.Vehicle;
            waypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint)
                ? cachedWaypoint
                : row.CachedWaypoint;
            boarding = OfficialBoarding(row);
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
            m_DemandVehicles.Clear();
            foreach (Entity vehicle in m_Demands.Keys)
                m_DemandVehicles.Add(vehicle);
            for (int i = 0; i < m_DemandVehicles.Count; i++)
                SetDemand(m_DemandVehicles[i], demand, false);
            m_DemandVehicles.Clear();
        }

        public bool RebaselineStartup(IReadOnlyList<Entity> vehicles, uint frame)
        {
            if ((frame & 15u) != 3u)
                return false;

            for (int i = 0; i < vehicles.Count; i++)
            {
                int rowIndex = EnsureSourceRow(vehicles[i], frame);
                if (rowIndex < 0)
                    return false;
                RailFrameRow row = m_FrameRows[rowIndex];
                m_Runtime.m_RuntimeHotPathProbe.CountSourceRow();
                if (!row.InputValid || row.CurrentRoute != row.RegisteredLine)
                    return false;
            }
            m_LastCollectedFrame = frame;
            return true;
        }

        public bool TryGetStartupSource(Entity vehicle, Entity line, uint frame, out bool boarding, out int waypoint, out int waypointCount)
        {
            boarding = false;
            waypoint = -1;
            waypointCount = 0;
            if (!TryGetRow(vehicle, out RailFrameRow row) || row.RegisteredLine != line || row.SourceFrame != frame)
                return false;

            row.WaypointCount = TryGetWaypointCount(row.CurrentRoute, out int routeWaypointCount)
                ? routeWaypointCount
                : 0;
            m_FrameRows[m_FrameRowIndex[vehicle]] = row;
            boarding = OfficialBoarding(row);
            waypoint = row.CachedWaypoint;
            waypointCount = row.WaypointCount;
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
            if (!TryGetStartupSource(vehicle, line, frame, out boarding, out waypoint, out waypointCount))
                return false;

            if (!m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
                return false;

            RailFrameRow row = m_FrameRows[rowIndex];
            if (row.RegistryState == VehicleState.Holding)
            {
                waypoint = 0;
                CommitWaypoint(vehicle, waypoint);
                return true;
            }

            if (!boarding || !TryGetWaypointsForRoute(line, out _, out DynamicBuffer<RouteWaypoint> waypoints))
                return true;

            waypoint = m_Runtime.m_WaypointIndex.Compute(vehicle, waypoints);
            waypointCount = waypoints.Length;
            if (waypoint >= 0)
                row.CachedWaypoint = waypoint;
            row.WaypointCount = waypointCount;
            m_FrameRows[rowIndex] = row;
            if (waypoint >= 0)
                CommitWaypoint(vehicle, waypoint);
            return true;
        }

        public void RegisterSource(Entity vehicle, Entity line, PublicTransport publicTransport, int waypoint, int waypointCount)
        {
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
                return;

            RailFrameRow row = m_FrameRows[rowIndex];
            row.RegisteredLine = line;
            row.IsCompilable = true;
            row.Boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            row.HasPublicTransport = true;
            row.CachedWaypoint = waypoint;
            row.WaypointCount = waypointCount;
            m_FrameRows[rowIndex] = row;
            CommitWaypoint(vehicle, waypoint);
            UpdateBaseline(row);
        }

        public void RegisterStopInput(Entity vehicle, Entity line, PublicTransport publicTransport, int waypoint, int waypointCount)
        {
            RegisterSource(vehicle, line, publicTransport, waypoint, waypointCount);
        }

        public void RebindSource(Entity vehicle)
        {
            m_Baselines.Remove(vehicle);
            if (m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
            {
                RailFrameRow row = m_FrameRows[rowIndex];
                row.RegisteredLine = Entity.Null;
                row.CurrentRoute = Entity.Null;
                row.RegistryState = default;
                row.Demands = RuntimeDemandMask.None;
                row.InputValid = false;
                row.HasPublicTransport = false;
                row.Boarding = false;
                row.MovingKnown = false;
                row.Moving = false;
                row.CachedWaypoint = -1;
                row.WaypointCount = 0;
                row.IsCompilable = false;
                row.Changes = RailChangeMask.None;
                m_FrameRows[rowIndex] = row;
            }
        }

        public void CommitWaypoint(Entity vehicle, int waypoint)
        {
            if (vehicle == Entity.Null)
                return;

            bool changed = !m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int previousWaypoint)
                || previousWaypoint != waypoint;
            m_Runtime.m_CachedWpIdx[vehicle] = waypoint;
            if (changed)
                m_Runtime.m_TrackProjection.InvalidateWaypointProjection(vehicle);
            if (!m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
                return;

            RailFrameRow row = m_FrameRows[rowIndex];
            row.CachedWaypoint = waypoint;
            m_FrameRows[rowIndex] = row;
        }

        public void RefreshOwners(Entity vehicle)
        {
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
                return;

            RailFrameRow row = m_FrameRows[rowIndex];
            row.InputValid = m_Runtime.EntityManager.Exists(vehicle);
            row.CurrentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            HydrateOwners(ref row);
            row.IsCompilable = row.InputValid && row.RegisteredLine != Entity.Null;
            m_FrameRows[rowIndex] = row;
            UpdateBaseline(row);
        }

        public void NotePreparingWaypoint(Entity vehicle, uint frame)
        {
            if (vehicle != Entity.Null)
                m_PreparingWaypointLiveFrames[vehicle] = frame;
        }

        public void ClearPreparingWaypoint(Entity vehicle)
        {
            m_PreparingWaypointLiveFrames.Remove(vehicle);
        }

        public void RemoveVehicle(Entity vehicle)
        {
            m_Baselines.Remove(vehicle);
            m_Demands.Remove(vehicle);
            m_FrameRowIndex.Remove(vehicle);
            m_PreparingWaypointLiveFrames.Remove(vehicle);
        }

        internal void InvalidateVehicleForStructure(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Baselines.Remove(vehicle);
            m_Demands.Remove(vehicle);
            m_PreparingWaypointLiveFrames.Remove(vehicle);
            if (!m_FrameRowIndex.TryGetValue(vehicle, out int rowIndex))
                return;

            if (rowIndex >= 0 && rowIndex < m_FrameRows.Count)
            {
                RailFrameRow row = m_FrameRows[rowIndex];
                row.RegisteredLine = Entity.Null;
                row.CurrentRoute = Entity.Null;
                row.CachedWaypoint = -1;
                row.WaypointCount = 0;
                row.Demands = RuntimeDemandMask.None;
                row.InputValid = false;
                row.IsCompilable = false;
                row.IsSource = false;
                m_FrameRows[rowIndex] = row;
            }

            m_FrameRowIndex.Remove(vehicle);
        }

        public void RecordPublicTransportWrite(Entity vehicle, PublicTransport value, uint frame)
        {
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
                return;

            RailFrameRow row = m_FrameRows[rowIndex];
            row.DepartureFrameWritten |= !TryReadDepartureFrame(vehicle, out uint previousDeparture)
                || previousDeparture != value.m_DepartureFrame;
            row.Boarding = (value.m_State & PublicTransportFlags.Boarding) != 0;
            row.HasPublicTransport = true;
            m_FrameRows[rowIndex] = row;
            if ((frame & 15u) != 3u || CollectedThisFrame(frame))
                UpdateBaseline(row);
        }

        public void ConsumeDepartureFrameWrites(Action<Entity, uint> consumer)
        {
            for (int i = 0; i < m_FrameRows.Count; i++)
            {
                RailFrameRow row = m_FrameRows[i];
                if (row.DepartureFrameWritten && TryReadDepartureFrame(row.Vehicle, out uint departureFrame))
                    consumer(row.Vehicle, departureFrame);
            }
        }

        public void RecordPathChange(Entity vehicle)
        {
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
                return;

            RailFrameRow row = m_FrameRows[rowIndex];
            // 新路径尚未由原版处理，本帧不能借旧导航或停站会话消歧。
            row.PathChanged = true;
            m_FrameRows[rowIndex] = row;
            InvalidateProjection(vehicle);
        }

        internal void InvalidateProjection(Entity vehicle)
        {
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_TrackProjection.Cursors.Remove(vehicle, keepCursor: true);
            m_Runtime.m_TrackProjection.ClearFacts(vehicle);
            if (m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line))
                m_Runtime.m_TrackProjection.ClearLineRunningVehicleSnapshots(line);
        }

        public bool TryReadPublicTransport(Entity vehicle, out PublicTransport value)
        {
            value = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle))
                return false;

            value = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            return true;
        }

        public bool TryReadDepartureFrame(Entity vehicle, out uint departureFrame)
        {
            departureFrame = 0u;
            if (!TryReadPublicTransport(vehicle, out PublicTransport publicTransport))
                return false;

            departureFrame = publicTransport.m_DepartureFrame;
            return true;
        }

        public bool TryReadTarget(Entity vehicle, out Target value)
        {
            value = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.HasComponent<Target>(vehicle))
                return false;

            value = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            return true;
        }

        public bool TryGetBypassInput(Entity vehicle, out Entity route, out DynamicBuffer<RouteWaypoint> waypoints, out PublicTransport publicTransport)
        {
            route = Entity.Null;
            waypoints = default;
            publicTransport = default;
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
                return false;
            RailFrameRow row = m_FrameRows[rowIndex];
            if (!row.IsCompilable || !TryReadPublicTransport(vehicle, out publicTransport)
                || !TryGetWaypointsForRoute(row.CurrentRoute, out route, out waypoints))
                return false;
            return true;
        }

        public bool TryGetRouteWaypoints(Entity vehicle, out Entity route, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            int rowIndex = EnsureFrameRow(vehicle, readMoving: false);
            if (rowIndex < 0)
            {
                route = Entity.Null;
                waypoints = default;
                return false;
            }
            return TryGetWaypointsForRoute(m_FrameRows[rowIndex].CurrentRoute, out route, out waypoints);
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
            if (!TryGetRow(entry, out RailFrameRow row) || !row.IsCompilable)
                return false;
            if (framePlan.IsDeadlineDue(
                    row.Vehicle,
                    DeadlineKind.ForcedMidStopBoardingGrace,
                    nowFrame))
            {
                RefreshBoarding(ref row);
                m_FrameRows[m_FrameRowIndex[row.Vehicle]] = row;
                UpdateBaseline(row);
            }
            bool hasWaypoints = TryGetWaypointBuffer(row.CurrentRoute, out DynamicBuffer<RouteWaypoint> waypoints);
            row.WaypointCount = hasWaypoints ? waypoints.Length : 0;
            int previousWaypoint = row.CachedWaypoint;
            int currentWaypoint = previousWaypoint;
            bool boarding = OfficialBoarding(row);
            if (boarding
                && row.RegistryState != VehicleState.Retiring
                && !hasOpenStopSession(row.Vehicle)
                && (row.RegistryState != VehicleState.Idle || hasInvalidatedRecovery(row.Vehicle))
                && row.WaypointCount >= 2)
            {
                currentWaypoint = m_Runtime.m_WaypointIndex.Compute(row.Vehicle, waypoints);
                row.WaypointCount = waypoints.Length;
                m_FrameRows[m_FrameRowIndex[row.Vehicle]] = row;
            }

            int lastStopWaypoint = m_Runtime.m_TrackModel.TryGetWaypointIndexLookup(
                row.RegisteredLine,
                out LineWaypointIndexLookup lookup)
                ? lookup.LastStopWaypointIndex
                : -1;

            bool suppressBoardingGhost = false;
            if (boarding
                && forcedMidStopGraceActive(row.Vehicle, nowFrame)
                && row.WaypointCount >= 2)
            {
                suppressBoardingGhost = SuppressBoardingGhost(row, waypoints);
            }
            m_FrameRows[m_FrameRowIndex[row.Vehicle]] = row;
            input = new StopInput(row.Vehicle, row.RegisteredLine, row.SourceFrame == 0 ? nowFrame : row.SourceFrame,
                row.RegistryState, row.InputValid && row.CurrentRoute == row.RegisteredLine && row.WaypointCount >= 2,
                boarding, m_Runtime.m_VehicleView.TryGetCooldown(row.Vehicle, out uint cooldown) && nowFrame < cooldown,
                previousWaypoint, currentWaypoint, currentWaypoint, row.WaypointCount,
                lastStopWaypoint,
                isDeparturePending(row.Vehicle) && row.MovingKnown,
                isDeparturePending(row.Vehicle) && row.Moving,
                suppressBoardingGhost);
            return true;
        }

        private bool SuppressBoardingGhost(RailFrameRow row, DynamicBuffer<RouteWaypoint> waypoints)
        {
            Entity vehicle = row.Vehicle;
            if (!TryReadTarget(vehicle, out Target target))
                return false;
            if (target.m_Target == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<Waypoint>(target.m_Target))
            {
                return false;
            }

            int targetWaypoint = m_Runtime.EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypoint < 0 || targetWaypoint >= waypoints.Length)
                return false;

            Entity waypoint = waypoints[targetWaypoint].m_Waypoint;
            if (waypoint == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
            {
                return false;
            }

            Entity stop = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            if (stop == Entity.Null
                || !m_Runtime.EntityManager.Exists(stop)
                || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(stop)
                || m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != vehicle
                || !m_Runtime.EntityManager.HasComponent<Transform>(stop)
                || !m_Runtime.EntityManager.HasComponent<Transform>(vehicle))
            {
                return false;
            }

            m_Runtime.m_RuntimeHotPathProbe.CountHeavyDetailRead();
            float3 vehiclePosition = m_Runtime.EntityManager.GetComponentData<Transform>(vehicle).m_Position;
            m_Runtime.m_RuntimeHotPathProbe.CountHeavyDetailRead();
            float3 stopPosition = m_Runtime.EntityManager.GetComponentData<Transform>(stop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > ModRuntimeHostSystem.AT_STOP_MAX_DIST;
        }

        public bool TryBuildDispatchInput(
            FramePlanEntry entry,
            uint nowFrame,
            IReadOnlyDictionary<Entity, StopFrameState> stopStates,
            IReadOnlyDictionary<Entity, RapidTransitMod.Bypass.BypassControlResult> bypassControls,
            out DispatchInput input)
        {
            input = default;
            if (!TryGetRow(entry, out RailFrameRow row) || !row.IsCompilable || row.RegistryState == VehicleState.Retiring)
                return false;

                bool hasWaypoints = TryGetWaypointsForRoute(row.CurrentRoute, out Entity route, out DynamicBuffer<RouteWaypoint> waypoints);
                int waypointCount = hasWaypoints ? waypoints.Length : 0;
                bool hasTargetComponent = TryReadTarget(row.Vehicle, out Target currentTarget);
                int previousWaypoint = row.CachedWaypoint;
                int currentWaypoint = row.CachedWaypoint;
                bool runningWaypointComputed = false;
                bool hasStopState = stopStates.TryGetValue(row.Vehicle, out StopFrameState stop);
                bool boarding = row.RegistryState == VehicleState.Running && hasStopState
                    ? stop.Boarding
                    : OfficialBoarding(row);
                int targetMinute = row.RegistryState == VehicleState.Running
                    && m_Runtime.m_VehicleView.TryGetTarget(row.Vehicle, out int target)
                        ? target
                        : -1;
                bool runningStopSignal = stop.BoardingChanged || stop.Boarding || stop.HadStopSession;
                bool runningOriginDetail = row.RegistryState == VehicleState.Running
                    && ((row.Demands & (RuntimeDemandMask.OriginCandidate | RuntimeDemandMask.InboundWatch)) != 0
                        || runningStopSignal
                        || targetMinute >= 0);
                uint preparingStart = 0;
                bool hasPreparingStart = row.RegistryState == VehicleState.Preparing
                    && m_Runtime.m_VehicleView.TryGetPreparing(row.Vehicle, out preparingStart);
                bool refreshPreparing = row.RegistryState == VehicleState.Preparing
                    && (!m_PreparingWaypointLiveFrames.TryGetValue(row.Vehicle, out uint lastRefresh)
                        || stop.BoardingChanged
                        || (hasPreparingStart && preparingStart > lastRefresh)
                        || nowFrame <= lastRefresh
                        || nowFrame - lastRefresh >= 16u);
                if (row.RegistryState == VehicleState.Preparing
                    && hasWaypoints
                    && waypointCount >= 2
                    && refreshPreparing)
                {
                    currentWaypoint = m_Runtime.m_WaypointIndex.Compute(row.Vehicle, waypoints);
                    if (currentWaypoint >= 0 && currentWaypoint != previousWaypoint)
                    {
                        row.CachedWaypoint = currentWaypoint;
                        CommitWaypoint(row.Vehicle, currentWaypoint);
                    }
                    m_PreparingWaypointLiveFrames[row.Vehicle] = nowFrame;
                }
                else if (row.RegistryState != VehicleState.Preparing)
                {
                    m_PreparingWaypointLiveFrames.Remove(row.Vehicle);
                    if (runningOriginDetail && hasWaypoints && waypointCount >= 2)
                    {
                        runningWaypointComputed = true;
                        currentWaypoint = m_Runtime.m_WaypointIndex.Compute(row.Vehicle, waypoints);
                        if (currentWaypoint >= 0 && currentWaypoint != previousWaypoint)
                        {
                            row.CachedWaypoint = currentWaypoint;
                            CommitWaypoint(row.Vehicle, currentWaypoint);
                        }
                    }
                }

                bool strictOriginRejected = false;
                if (row.RegistryState == VehicleState.Running
                    && runningWaypointComputed
                    && currentWaypoint == 0
                    && !boarding
                    && !stop.HadStopSession
                    && hasWaypoints
                    && waypointCount >= 2)
                {
                    Entity strictLine = m_Runtime.m_Resolve.Line(row.Vehicle);
                    if (strictLine != Entity.Null
                        && m_Runtime.m_TrackModel.TryGetChainForLine(strictLine, waypoints, out LineTrackChain strictChain)
                        && m_Runtime.m_TrackProjection.TrySnapshot(
                            row.Vehicle,
                            strictLine,
                            strictChain.Signature,
                            nowFrame,
                            out var strictCursor)
                        && m_Runtime.m_WaypointIndex.TryRelation(
                            strictChain,
                            0,
                            strictCursor.AtomCursorIndex,
                            out CursorAtomWindowRelation strictRelation,
                            out _,
                            out _))
                    {
                        strictOriginRejected = strictRelation == CursorAtomWindowRelation.Before
                            || strictRelation == CursorAtomWindowRelation.After;
                    }
                }

                bool targetPresent = hasTargetComponent && currentTarget.m_Target != Entity.Null;
                bool preparingAtOrigin = row.RegistryState == VehicleState.Preparing
                    && hasWaypoints
                    && waypointCount >= 2
                    && m_Runtime.m_LineProfile.HasPreparingReachedOrigin(row.Vehicle, waypoints, boarding, currentWaypoint);
                bool atOrigin = row.RegistryState == VehicleState.Preparing
                    ? preparingAtOrigin
                    : row.RegistryState == VehicleState.Running
                        ? currentWaypoint == 0 && !strictOriginRejected
                        : currentWaypoint == 0;
                Entity originStation = hasWaypoints && waypointCount >= 2
                    ? waypoints[0].m_Waypoint
                    : Entity.Null;
                bool targetAtOrigin = targetPresent && currentTarget.m_Target == originStation;
                bool originBusy = row.RegistryState == VehicleState.Idle
                    && hasWaypoints
                    && waypointCount >= 2
                    && m_Runtime.m_LineProfile.HasInboundNearOrigin(
                        route,
                        waypoints,
                        row.Vehicle,
                        ModRuntimeHostSystem.ORIGIN_CONGESTION_RADIUS_METERS,
                        includePreparingVehicles: false);
                bool cooldownActive = runningOriginDetail
                    && m_Runtime.m_VehicleView.TryGetCooldown(row.Vehicle, out uint cooldownUntil)
                    && nowFrame < cooldownUntil;
                bool preparingCooldown = row.RegistryState == VehicleState.Preparing
                    && m_Runtime.m_PreparingFixCooldownUntil.TryGetValue(row.Vehicle, out uint repairCooldown)
                    && nowFrame < repairCooldown;
                bool preparingFresh = row.RegistryState == VehicleState.Preparing
                    && m_Runtime.m_VehicleView.IsFreshPreparing(
                        row.Vehicle,
                        nowFrame,
                        ModRuntimeHostSystem.PREPARING_ROUTE_FIX_GRACE_FRAMES);
                bool preparingDrifted = boarding && currentWaypoint > 0;
                bool preparingWrongTarget = hasTargetComponent && !targetAtOrigin;
                bool preparingRouteNeedsRepair = row.RegistryState == VehicleState.Preparing
                    && hasWaypoints
                    && waypointCount >= 2
                    && hasTargetComponent
                    && !preparingAtOrigin
                    && !preparingCooldown
                    && (preparingDrifted || (preparingWrongTarget && !preparingFresh));
                bool shouldEvaluateOriginSettle = runningOriginDetail
                    && !cooldownActive
                    && hasWaypoints
                    && waypointCount >= 2
                    && (strictOriginRejected || atOrigin || boarding || stop.HadStopSession || targetMinute >= 0
                        || m_Runtime.m_VehicleView.IsInbound(row.Vehicle));
                bool settledAtOrigin = shouldEvaluateOriginSettle
                    && m_Runtime.m_LineProfile.ShouldSettleAtOrigin(
                        row.Vehicle,
                        waypoints,
                        nowFrame,
                        atOrigin,
                        boarding,
                        stop.HadStopSession,
                        targetMinute,
                        strictOriginRejected);
                bool forcedAtOrigin = settledAtOrigin && !atOrigin;
                bool brokenRecoveredRun = false;
                bool runDistanceReady = false;
                float travelledDistance = -1f;
                float observedLapDistance = -1f;
                if (runningOriginDetail && shouldEvaluateOriginSettle && (atOrigin || forcedAtOrigin))
                {
                    bool hasLapStart = m_Runtime.m_ObsQuery.TryLapStart(row.Vehicle, out float lapStart);
                    bool hasLapStartFrame = m_Runtime.m_ObsQuery.TryLapStartFrame(row.Vehicle, out _);
                    bool lapStartValid = hasLapStart && !float.IsNaN(lapStart) && !float.IsInfinity(lapStart) && lapStart >= 0f;
                    if (lapStartValid && m_Runtime.EntityManager.HasComponent<Odometer>(row.Vehicle))
                    {
                        m_Runtime.m_RuntimeHotPathProbe.CountHeavyDetailRead();
                        travelledDistance = m_Runtime.EntityManager.GetComponentData<Odometer>(row.Vehicle).m_Distance - lapStart;
                    }
                    m_Runtime.m_ObsQuery.TryLapDistance(row.Vehicle, out observedLapDistance);
                    brokenRecoveredRun = hasLapStartFrame && !lapStartValid;
                    runDistanceReady = travelledDistance > 500f;
                }

                if (row.RegistryState == VehicleState.Preparing && hasWaypoints && waypointCount >= 2)
                    m_Runtime.m_Observation.TryRequestDispatchEta(row.Vehicle, row.RegisteredLine, waypoints, nowFrame);
                RapidTransitMod.Bypass.BypassControlResult bypass = bypassControls.TryGetValue(row.Vehicle, out RapidTransitMod.Bypass.BypassControlResult control)
                    ? control : new RapidTransitMod.Bypass.BypassControlResult(false, row.Vehicle, route, currentWaypoint, false, false, Entity.Null, true, null);
            input = new DispatchInput(row.Vehicle, row.RegisteredLine, route,
                row.InputValid && row.HasPublicTransport && hasTargetComponent && route == row.RegisteredLine && waypointCount >= 2,
                boarding, previousWaypoint, currentWaypoint, waypointCount,
                atOrigin, targetAtOrigin, preparingAtOrigin, originBusy, preparingRouteNeedsRepair, shouldEvaluateOriginSettle,
                false, false,
                settledAtOrigin, forcedAtOrigin, brokenRecoveredRun, row.Moving,
                runDistanceReady, travelledDistance, observedLapDistance, stop.HadStopSession, stop.BoardingChanged,
                m_Runtime.m_StopRuntime.IsDeparturePending(row.Vehicle),
                m_Runtime.m_StopRuntime.IsOriginDepartureConfirmed(row.Vehicle, row.RegisteredLine), bypass);
            return true;
        }

        public void ResetTracking()
        {
            m_Baselines.Clear();
            m_Demands.Clear();
            m_FrameRows.Clear();
            m_FrameRowIndex.Clear();
            m_PreparingWaypointLiveFrames.Clear();
            m_DemandVehicles.Clear();
            m_StaleVehicles.Clear();
            m_LastCollectedFrame = uint.MaxValue;
        }

        public void Dispose() => ResetTracking();

        private int EnsureFrameRow(
            Entity vehicle,
            bool readMoving)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return -1;
            m_Runtime.m_RuntimeHotPathProbe.CountEnsureFrameRow();
            if (m_FrameRowIndex.TryGetValue(vehicle, out int existing))
                return existing;

            RailFrameRow row = new RailFrameRow
            {
                Vehicle = vehicle,
                InputValid = true,
                CachedWaypoint = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int waypoint) ? waypoint : -1
            };
            if (m_Baselines.TryGetValue(vehicle, out RailBaseline baseline))
                ApplyBaseline(ref row, baseline);
            else
                ReadNarrow(ref row, readMoving);
            HydrateOwners(ref row);
            row.IsCompilable = true;
            m_FrameRowIndex.Add(vehicle, m_FrameRows.Count);
            m_FrameRows.Add(row);
            m_Runtime.m_RuntimeHotPathProbe.CountFrameRow();
            return m_FrameRows.Count - 1;
        }

        private int EnsureSourceRow(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return -1;

            m_Runtime.m_RuntimeHotPathProbe.CountEnsureFrameRow();
            if (m_FrameRowIndex.TryGetValue(vehicle, out int existing))
            {
                RailFrameRow existingRow = m_FrameRows[existing];
                RefreshSourceHeader(ref existingRow, frame);
                m_FrameRows[existing] = existingRow;
                return existing;
            }

            RailFrameRow row = new RailFrameRow
            {
                Vehicle = vehicle,
                CachedWaypoint = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int waypoint) ? waypoint : -1
            };
            RefreshSourceHeader(ref row, frame);
            m_FrameRowIndex.Add(vehicle, m_FrameRows.Count);
            m_FrameRows.Add(row);
            m_Runtime.m_RuntimeHotPathProbe.CountFrameRow();
            return m_FrameRows.Count - 1;
        }

        private void RefreshSourceHeader(ref RailFrameRow row, uint frame)
        {
            RailBaseline previous = m_Baselines.TryGetValue(row.Vehicle, out RailBaseline baseline) ? baseline : default;
            RailFrameRow fresh = new RailFrameRow { Vehicle = row.Vehicle, CachedWaypoint = row.CachedWaypoint };
            fresh.DepartureFrameWritten = row.DepartureFrameWritten;
            fresh.PathChanged = row.PathChanged;
            ReadNarrow(ref fresh, true);
            fresh.RegisteredLine = m_Runtime.m_VehicleView.TryGetLine(row.Vehicle, out Entity line) ? line : Entity.Null;
            fresh.RegistryState = m_Runtime.m_VehicleView.TryGetState(row.Vehicle, out VehicleState state) ? state : default;
            fresh.Demands = ReadDemand(row.Vehicle);
            fresh.IsCompilable = true;
            fresh.IsSource = true;
            fresh.SourceFrame = frame;
            fresh.Changes = (previous.HasPublicTransport && previous.HasPublicTransport == fresh.HasPublicTransport
                && previous.Boarding != fresh.Boarding
                    ? RailChangeMask.OfficialBoardingChanged : RailChangeMask.None)
                | (previous.MovingKnown && previous.Moving != fresh.Moving ? RailChangeMask.MovingChanged : RailChangeMask.None);
            if ((fresh.Changes & RailChangeMask.OfficialBoardingChanged) != 0)
                m_Runtime.m_RuntimeHotPathProbe.CountOfficialBoardingChanged();
            if ((fresh.Changes & RailChangeMask.MovingChanged) != 0)
                m_Runtime.m_RuntimeHotPathProbe.CountMovingChanged();
            row = fresh;
            UpdateBaseline(row);
        }

        private void ReadNarrow(ref RailFrameRow row, bool readMoving)
        {
            Entity vehicle = row.Vehicle;
            row.InputValid = m_Runtime.EntityManager.Exists(vehicle);
            RefreshBoarding(ref row);
            bool hasRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle);
            row.CurrentRoute = hasRoute ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route : Entity.Null;
            if (readMoving)
            {
                row.MovingKnown = m_Runtime.EntityManager.HasComponent<Moving>(vehicle);
                row.Moving = row.MovingKnown && math.lengthsq(m_Runtime.EntityManager.GetComponentData<Moving>(vehicle).m_Velocity) > DepartureMovingSpeedSq;
            }
        }

        private void ApplyBaseline(ref RailFrameRow row, RailBaseline baseline)
        {
            row.InputValid = baseline.InputValid;
            row.HasPublicTransport = baseline.HasPublicTransport;
            row.Boarding = baseline.Boarding;
            row.CurrentRoute = baseline.Route;
            row.MovingKnown = baseline.MovingKnown;
            row.Moving = baseline.Moving;
        }

        private void HydrateOwners(ref RailFrameRow row)
        {
            row.RegisteredLine = m_Runtime.m_VehicleView.TryGetLine(row.Vehicle, out Entity line) ? line : Entity.Null;
            row.RegistryState = m_Runtime.m_VehicleView.TryGetState(row.Vehicle, out VehicleState state) ? state : default;
            row.Demands = ReadDemand(row.Vehicle);
        }

        private void RefreshBoarding(ref RailFrameRow row)
        {
            row.HasPublicTransport = TryReadPublicTransport(row.Vehicle, out PublicTransport publicTransport);
            row.Boarding = row.HasPublicTransport
                && (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
        }

        private void UpdateBaseline(RailFrameRow row)
        {
            m_Baselines[row.Vehicle] = new RailBaseline
            {
                InputValid = row.InputValid,
                HasPublicTransport = row.HasPublicTransport,
                Boarding = row.Boarding,
                Route = row.CurrentRoute,
                MovingKnown = row.MovingKnown,
                Moving = row.Moving
            };
        }

        private bool TryGetRow(Entity vehicle, out RailFrameRow row)
        {
            if (m_FrameRowIndex.TryGetValue(vehicle, out int index))
            {
                row = m_FrameRows[index];
                return true;
            }
            row = default;
            return false;
        }

        internal bool TryReadProjectionRuntimeContext(Entity vehicle, out ProjectionRuntimeContext context)
        {
            context = default;
            context.VehicleStateKnown = m_Runtime.m_VehicleView.TryGetState(vehicle, out context.VehicleState);
            context.PublicTransportKnown = TryReadPublicTransport(vehicle, out PublicTransport publicTransport);
            context.OfficialBoarding = context.PublicTransportKnown
                && (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            return context.VehicleStateKnown || context.PublicTransportKnown;
        }

        private bool TryGetRow(FramePlanEntry entry, out RailFrameRow row)
        {
            if (entry.SourceRowIndex >= 0 && entry.SourceRowIndex < m_FrameRows.Count)
            {
                row = m_FrameRows[entry.SourceRowIndex];
                if (row.Vehicle == entry.Vehicle)
                    return true;
            }
            int index = EnsureFrameRow(entry.Vehicle, readMoving: false);
            if (index >= 0)
            {
                row = m_FrameRows[index];
                return true;
            }
            row = default;
            return false;
        }

        private bool TryGetWaypointsForRoute(Entity sourceRoute, out Entity route, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            route = sourceRoute;
            waypoints = default;
            return TryGetWaypointBuffer(route, out waypoints) && waypoints.Length >= 2;
        }

        private bool TryGetWaypointCount(Entity line, out int count)
        {
            if (TryGetWaypointBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints))
            {
                count = waypoints.Length;
                return true;
            }

            count = 0;
            return false;
        }

        private bool TryGetWaypointBuffer(Entity line, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                waypoints = default;
                return false;
            }

            waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            return true;
        }

        private RuntimeDemandMask ReadDemand(Entity vehicle) => m_Demands.TryGetValue(vehicle, out RuntimeDemandMask demand) ? demand : RuntimeDemandMask.None;
        private static bool OfficialBoarding(RailFrameRow row) => row.HasPublicTransport && row.Boarding;

        private void PruneBaselines()
        {
            m_StaleVehicles.Clear();
            foreach (Entity vehicle in m_Baselines.Keys)
            {
                if (!m_Runtime.EntityManager.Exists(vehicle) || !m_Runtime.m_VehicleView.Contains(vehicle))
                    m_StaleVehicles.Add(vehicle);
            }
            for (int i = 0; i < m_StaleVehicles.Count; i++) RemoveVehicle(m_StaleVehicles[i]);
            m_StaleVehicles.Clear();
        }
    }
}
