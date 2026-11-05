using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Tools;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistrar
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly EntityQuery m_VehicleQuery;
        private readonly HashSet<Entity> m_EligibleLines = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> m_ReleasedCountByLine = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, (Entity Line, byte RemainingAttempts)> m_PendingRegistrationConfirmations = new Dictionary<Entity, (Entity, byte)>();
        private readonly List<Entity> m_RegistrationConfirmationVehicles = new List<Entity>();
        private readonly System.Action<StopFact> m_PublishStopFact;

        public VehicleRegistrar(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            m_VehicleQuery = runtime.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] {
                    ComponentType.ReadOnly<PublicTransport>(),
                    ComponentType.ReadOnly<CurrentRoute>()
                },
                None = new ComponentType[] {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<ParkedTrain>(),
                    ComponentType.ReadOnly<ParkedCar>()
                }
            });
            m_PublishStopFact = runtime.PublishStopFact;
        }

        internal void ClearRegistrationConfirmations() => m_PendingRegistrationConfirmations.Clear();

        internal EntityQuery VehicleQuery => m_VehicleQuery;

        internal void AddPendingConfirmation(Entity vehicle, Entity line) => m_PendingRegistrationConfirmations[vehicle] = (line, 2);

        internal void RemovePendingConfirmation(Entity vehicle) => m_PendingRegistrationConfirmations.Remove(vehicle);

        internal void Scan()
        {
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            m_ReleasedCountByLine.Clear();
            CollectEligibleLines(rvBuffers, wpBuffers);
            using (NativeArray<Entity> vehicles = m_VehicleQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (m_Runtime.m_VehicleView.Contains(vehicle)) continue;
                    if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle)) continue;
                    Entity line = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                    if (!m_EligibleLines.Contains(line)) continue;
                    DynamicBuffer<RouteWaypoint> waypoints = wpBuffers[line];
                    AdoptCandidate(line, vehicle, waypoints,
                        adoptExistingVehicles: !m_Runtime.m_LineInitialAdopted.Contains(line));
                }
            }

            using (NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp))
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.EntityManager.Exists(vehicle)) continue;
                    if (m_Runtime.m_VehicleView.GetState(vehicle) == VehicleState.Retiring
                        || m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle)) continue;

                    bool hasRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle);
                    Entity route = hasRoute
                        ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                        : Entity.Null;
                    bool parked = m_Runtime.EntityManager.HasComponent<ParkedTrain>(vehicle)
                        || m_Runtime.EntityManager.HasComponent<ParkedCar>(vehicle);
                    bool lineExists = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line)
                        && m_Runtime.EntityManager.Exists(line)
                        && !m_Runtime.EntityManager.HasComponent<Deleted>(line);
                    if (hasRoute && route == line && lineExists && !parked) continue;

                    string reason = parked ? "scan-parked"
                        : !lineExists ? "scan-line-deleted"
                        : !hasRoute ? "scan-left-line"
                        : "scan-route-changed";
                    ExitContext context = m_Runtime.m_VehicleExit.Release(vehicle, ExitMode.Live, reason);
                    if (context.Line != Entity.Null && context.State != VehicleState.Retiring)
                    {
                        m_ReleasedCountByLine[context.Line] = m_ReleasedCountByLine.TryGetValue(context.Line, out int count)
                            ? count + 1
                            : 1;
                    }
                }
            }

            if (m_ReleasedCountByLine.Count > 0)
                m_Runtime.m_LineSpawnControl.ApplyCleanupTargetReduction(m_ReleasedCountByLine);
            foreach (Entity line in m_EligibleLines)
                m_Runtime.m_LineInitialAdopted.Add(line);
        }

        internal void RetryRegistrationConfirmations()
        {
            if (m_PendingRegistrationConfirmations.Count == 0)
                return;

            m_RegistrationConfirmationVehicles.Clear();
            foreach (Entity vehicle in m_PendingRegistrationConfirmations.Keys)
                m_RegistrationConfirmationVehicles.Add(vehicle);

            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            for (int i = 0; i < m_RegistrationConfirmationVehicles.Count; i++)
            {
                Entity vehicle = m_RegistrationConfirmationVehicles[i];
                (Entity Line, byte RemainingAttempts) pending = m_PendingRegistrationConfirmations[vehicle];
                if (!m_Runtime.m_RailEventSource.TryGetStartupSource(
                        vehicle,
                        pending.Line,
                        frame,
                        out bool boarding,
                        out _)
                    || !boarding
                    || m_Runtime.m_StopRuntime.HasOpenStopSession(vehicle)
                    || m_Runtime.m_StopRuntime.HasInvalidatedRecovery(vehicle)
                    || !m_Runtime.m_RailEventSource.TryGetRouteWaypoints(vehicle, out _, out DynamicBuffer<RouteWaypoint> waypoints)
                    || !m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state == VehicleState.Retiring)
                {
                    m_PendingRegistrationConfirmations.Remove(vehicle);
                    continue;
                }

                bool confirmed = m_Runtime.m_WaypointIndex.TryConfirmBoardingWaypoint(
                    vehicle,
                    pending.Line,
                    waypoints,
                    false,
                    default,
                    out _,
                    out int waypoint);
                if (confirmed)
                {
                    m_PendingRegistrationConfirmations.Remove(vehicle);
                    StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                        vehicle,
                        pending.Line,
                        boarding: true,
                        waypoint,
                        frame);
                    m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, waypoint);
                    SetRegistrationReady(vehicle, waypoint, state, frame);
                    m_PublishStopFact(restoredStop);
                    m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RapidTransitMod.Runtime.RuntimeStageMask.Dispatch);
                    continue;
                }

                byte remaining = (byte)(pending.RemainingAttempts - 1);
                if (remaining == 0)
                    m_PendingRegistrationConfirmations.Remove(vehicle);
                else
                    m_PendingRegistrationConfirmations[vehicle] = (pending.Line, remaining);
            }
            m_RegistrationConfirmationVehicles.Clear();
        }

        internal void SetRegistrationReady(Entity vehicle, int waypoint, VehicleState state, uint frame)
        {
            if (waypoint == 0 && state == VehicleState.Holding)
                m_Runtime.m_RuntimeEngine.SetReady(vehicle, frame, 2d, m_Runtime.m_SimClock.Snapshot);
        }

        internal void CommitFinalState(Entity vehicle, Entity line, uint frame)
        {
            if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                return;

            if (state == VehicleState.Running)
                m_Runtime.m_RuntimeEngine.CommitRunning(vehicle, line);
            else if (state == VehicleState.Holding)
                m_Runtime.m_Observation.Seed(vehicle, line, frame);
        }

        private void CollectEligibleLines(
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            m_EligibleLines.Clear();
            using var lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)) continue;
                if (!rvBuffers.TryGetBuffer(line, out _)) continue;
                if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints) || waypoints.Length < 2) continue;
                if (!m_Runtime.m_LineProfile.IsStable(line, waypoints)) continue;
                if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch())) continue;
                m_Runtime.m_LineServiceState.EnsureObserved(line);
                if (!m_Runtime.m_LineServiceState.IsOperational(line)) continue;
                m_EligibleLines.Add(line);
                DiagnoseLine(line, waypoints);
            }
        }

        private void DiagnoseLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!m_Runtime.m_LineInitialAdopted.Contains(line) || m_Runtime.m_LineProfile.IsDiagnosed(line)) return;
            m_Runtime.m_LineProfile.MarkDiagnosed(line);
            bool isRail = RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail;
            if (isRail)
                m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
            if (RtLog.VerboseEnabled)
                m_Runtime.log.Info("[诊断] 线路" + line.Index + " (" + m_Runtime.EntityName(line) + ") waypoint数=" + waypoints.Length);
        }

        private bool RestoreLiveCache(Entity vehicle, Entity line, bool allowRunningRestore, bool rail)
        {
            if (!m_Runtime.m_VehicleCache.Restore(vehicle, line, allowRunningRestore, out VehicleState restoredState))
                return false;

            if (restoredState == VehicleState.Holding)
                WriteRestoredDeparture(vehicle, line, rail);
            else if (restoredState == VehicleState.Running)
                m_Runtime.m_VehicleCache.RestoreLap(vehicle, line);
            return true;
        }

        private void WriteRestoredDeparture(Entity vehicle, Entity line, bool rail)
        {
            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            uint frame = m_Runtime.m_SimulationSystem.frameIndex;
            publicTransport.m_DepartureFrame = frame + 99999;
            if (rail)
                m_Runtime.m_RailEventSource.RecordPublicTransportWrite(vehicle, publicTransport, frame);
            else
                m_Runtime.m_RoadEventSource.RecordPublicTransportWrite(vehicle, publicTransport, frame);
            m_Runtime.EntityManager.SetComponentData(vehicle, publicTransport);
            if (RtLog.VerboseEnabled)
            {
                int target = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) ? targetMinute : -1;
                m_Runtime.log.Info("[恢复] 线路" + line.Index + " 车辆" + vehicle.Index
                    + " Holding target=" + (target >= 0 ? ModRuntimeHostSystem.SlotStr(target) : "-"));
            }
        }

        private uint? TakeSpawnRequestFrame(Entity line, bool adoptExistingVehicles)
        {
            if (!adoptExistingVehicles
                && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
            {
                m_Runtime.m_LineSpawnRequestFrame.Remove(line);
                return spawnRequestFrame;
            }
            return null;
        }

        private void AdoptRoadCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles)
        {
            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & (PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute)) != 0)
                return;

            bool waypointKnown = m_Runtime.m_RoadEventSource.TryReadCurrentWaypoint(
                vehicle,
                line,
                out bool boarding,
                out int waypointIndex);
            VehicleState initialState = ResolveRoadInitialState(
                waypointKnown,
                waypointIndex,
                boarding,
                adoptExistingVehicles,
                publicTransport.m_State);
            uint? dispatchFrame = TakeSpawnRequestFrame(line, adoptExistingVehicles);

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_VehicleRegistry.BeginRestore();

            try
            {
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, nowFrame, dispatchFrame);
                m_Runtime.m_RoadEventSource.RegisterSource(vehicle, line, waypointKnown ? waypointIndex : -1);
                m_Runtime.m_ObsPersist.SetLapDistance(vehicle, -1f);
                StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                    vehicle,
                    line,
                    boarding,
                    waypointKnown ? waypointIndex : -1,
                    nowFrame);
                m_PublishStopFact(restoredStop);

                m_Runtime.m_RoadEventSource.CommitWaypoint(
                    vehicle,
                    waypointKnown ? waypointIndex : -1);
                m_Runtime.m_UICache.Remove(vehicle);

                bool restored = RestoreLiveCache(
                    vehicle,
                    line,
                    initialState != VehicleState.Holding,
                    rail: false);
                if (!restored && initialState == VehicleState.Running)
                    m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, "road-register");

                m_Runtime.m_VehicleRegistry.EndRestore(vehicle, line);
            }
            catch
            {
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }

            CommitFinalState(vehicle, line, nowFrame);

            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(
                    line,
                    m_Runtime.m_RuntimeLifecycleHost.Minute(),
                    vehicle,
                    initialState);
        }

        internal static VehicleState ResolveRoadInitialState(
            bool waypointKnown,
            int waypointIndex,
            bool boarding,
            bool adoptExistingVehicles,
            PublicTransportFlags transportState)
        {
            if (boarding && waypointKnown && waypointIndex == 0)
                return VehicleState.Holding;
            if (!adoptExistingVehicles)
                return VehicleState.Preparing;
            // 目标是中途路点，不代表尚在入线途中的车辆已经运营。
            if (!boarding
                && (transportState & (PublicTransportFlags.EnRoute | PublicTransportFlags.RouteSource)) == PublicTransportFlags.EnRoute)
                return VehicleState.Preparing;
            if (waypointKnown && waypointIndex > 0)
                return VehicleState.Running;
            return VehicleState.Preparing;
        }

        private void AdoptCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles)
        {
            if (!RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
                return;
            if (lifecycle == LifecycleKind.Road)
            {
                AdoptRoadCandidate(
                    line,
                    vehicle,
                    waypoints,
                    adoptExistingVehicles);
                return;
            }

            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            if ((publicTransport.m_State & (PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute)) != 0) return;

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            int waypointIndex = -1;
            m_PendingRegistrationConfirmations.Remove(vehicle);
            if (boarding)
            {
                m_Runtime.m_WaypointIndex.TryConfirmBoardingWaypoint(
                    vehicle,
                    line,
                    waypoints,
                    false,
                    default,
                    out _,
                    out waypointIndex);
            }
            VehicleState initialState = InferInitialState(
                vehicle,
                waypoints,
                publicTransport,
                boarding,
                waypointIndex,
                adoptExistingVehicles,
                out string initialReason);
            uint? dispatchFrame = TakeSpawnRequestFrame(line, adoptExistingVehicles);

            m_Runtime.m_RailEventSource.RegisterSource(
                vehicle,
                line,
                publicTransport,
                waypointIndex,
                waypoints.Length);
            m_Runtime.m_VehicleRegistry.BeginRestore();
            bool restored = false;
            string spawnIntent = string.Empty;
            try
            {
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, nowFrame, dispatchFrame);
                spawnIntent = dispatchFrame.HasValue
                    ? m_Runtime.m_SpawnIntentTrace.Bind(line, vehicle, dispatchFrame.Value, nowFrame)
                    : string.Empty;
                m_Runtime.m_ObsPersist.SetLapDistance(vehicle, -1f);
                StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                    vehicle,
                    line,
                    boarding,
                    waypointIndex,
                    nowFrame);
                m_PublishStopFact(restoredStop);
                m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, waypointIndex);
                m_Runtime.m_UICache.Remove(vehicle);
                m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "register-reset");
                if (initialReason == "boarding-midway")
                    m_Runtime.TrackProjection.MarkVehicleProgressSuspect(vehicle, initialReason);

                restored = RestoreLiveCache(
                    vehicle,
                    line,
                    initialState != VehicleState.Holding,
                    rail: true);
                if (!restored && initialState == VehicleState.Running)
                    restored = m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, initialReason);
                m_Runtime.m_VehicleRegistry.EndRestore(vehicle, line);
                m_Runtime.m_RailEventSource.RefreshOwners(vehicle);
            }
            catch
            {
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }
            VehicleState finalState = m_Runtime.m_VehicleView.GetState(vehicle);
            SetRegistrationReady(vehicle, waypointIndex, finalState, nowFrame);
            if (boarding && waypointIndex < 0)
                m_PendingRegistrationConfirmations[vehicle] = (line, 2);
            CommitFinalState(vehicle, line, nowFrame);

            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(line, m_Runtime.m_RuntimeLifecycleHost.Minute(), vehicle, finalState);
            LogRailRegistration(line, vehicle, initialState, initialReason, restored, waypointIndex, adoptExistingVehicles, spawnIntent);
        }


        internal VehicleState InferInitialState(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            Game.Vehicles.PublicTransport publicTransport,
            bool boarding,
            int initialWaypointIndex,
            bool adoptExistingVehicles,
            out string reason)
        {
            if (initialWaypointIndex == 0)
            {
                reason = "at-origin";
                return VehicleState.Holding;
            }
            if (boarding && initialWaypointIndex > 0)
            {
                reason = "boarding-midway";
                return VehicleState.Running;
            }
            if (boarding)
            {
                reason = "boarding-unconfirmed";
                return VehicleState.Running;
            }
            if (!adoptExistingVehicles)
            {
                reason = "new-vehicle-default";
                return VehicleState.Preparing;
            }

            bool hasRouteProgress = m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition);
            // 原版出库即设置 EnRoute；首次停站或路点续接前，路径进度不能证明已经运营。
            if ((publicTransport.m_State & (PublicTransportFlags.EnRoute | PublicTransportFlags.RouteSource)) == PublicTransportFlags.EnRoute)
            {
                reason = "initial-route-entry";
                return VehicleState.Preparing;
            }

            if (hasRouteProgress)
            {
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return VehicleState.Running;
            }

            float originDistance = m_Runtime.m_LineProfile.DistanceToOrigin(vehicle, waypoints);
            if (originDistance > ModRuntimeHostSystem.ORIGIN_CONGESTION_RADIUS_METERS)
            {
                reason = "far-from-origin " + originDistance.ToString("F0") + "m";
                return VehicleState.Running;
            }

            if (!m_Runtime.EntityManager.HasComponent<Target>(vehicle))
            {
                reason = "no-target";
                return VehicleState.Preparing;
            }

            Entity target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target;
            if (target == Entity.Null || target == waypoints[0].m_Waypoint)
            {
                reason = "target-origin";
                return VehicleState.Preparing;
            }
            reason = "non-origin-target";
            return VehicleState.Running;
        }

        private void LogRailRegistration(
            Entity line,
            Entity vehicle,
            VehicleState initialState,
            string initialReason,
            bool restored,
            int waypointIndex,
            bool adoptExistingVehicles,
            string spawnIntent)
        {
            if (!RtLog.VerboseEnabled)
                return;

            VehicleState finalState = m_Runtime.m_VehicleView.GetState(vehicle);
            int finalTarget = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            string lineTag = "线路" + line.Index;
            m_Runtime.log.Info("[注册] " + lineTag + " 车辆" + vehicle.Index
                + " 初始:" + initialState + " 最终:" + finalState
                + (restored ? "(缓存恢复)" : "")
                + " targetMin=" + finalTarget
                + " initReason=" + initialReason
                + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(vehicle));
            m_Runtime.m_RuntimeLog.Once(
                m_Runtime.m_RuntimeLog.m_RouteVehicleOwnerMismatchLogCache,
                vehicle,
                "register-detail|line=" + line.Index
                    + "|state=" + finalState
                    + "|target=" + (m_Runtime.EntityManager.HasComponent<Target>(vehicle) ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target.Index : -1)
                    + "|route=" + (m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle) ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route.Index : -1),
                "[RegisterDetail] " + lineTag + " 车辆" + vehicle.Index
                    + " " + m_Runtime.m_RuntimeLog.VehicleOwnership(line, vehicle, finalState, finalTarget, "register")
                    + " initReason=" + initialReason
                    + " restored=" + (restored ? "1" : "0")
                    + " atA0=" + (waypointIndex == 0 ? "1" : "0")
                    + " initWp=" + waypointIndex);
            if (!adoptExistingVehicles)
            {
                m_Runtime.log.Info("[OfficialSpawnResult] line=" + line.Index
                    + " vehicle=" + vehicle.Index
                    + " state=" + finalState
                    + " targetMin=" + finalTarget
                    + " initReason=" + initialReason
                    + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(vehicle)
                    + spawnIntent);
            }
        }
    }
}
