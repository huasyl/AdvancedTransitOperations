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
        private struct StartupCandidate
        {
            public Entity Line;
            public Entity Vehicle;
            public LifecycleKind Lifecycle;
        }

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

        internal void ActivateStartup(uint frame)
        {
            long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            var lines = new List<Entity>();
            var candidates = new List<StartupCandidate>();
            CollectStartupIndex(candidates, lines);
            List<StartupCandidate> operational = BuildOperationalStartupCandidates(candidates, lines);
            AdoptStartupCandidates(operational, frame);
            RebaselineStartupSources(operational, frame);
            RestoreStartupStops(operational, frame);
            for (int i = 0; i < lines.Count; i++)
                m_Runtime.m_LineInitialAdopted.Add(lines[i]);
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            LogStartupAdoption(frame, candidates, lines, operational);
            m_Runtime.log.Info("[Startup] First activation: lines=" + lines.Count
                + " vehicles=" + candidates.Count
                + " adopted=" + operational.Count
                + " ms=" + elapsedMs.ToString("F1"));
        }

        private void RebaselineStartupSources(List<StartupCandidate> operational, uint frame)
        {
            var railVehicles = new List<Entity>(operational.Count);
            var roadVehicles = new List<Entity>(operational.Count);
            for (int i = 0; i < operational.Count; i++)
            {
                StartupCandidate candidate = operational[i];
                if (candidate.Lifecycle == LifecycleKind.Rail)
                    railVehicles.Add(candidate.Vehicle);
                else if (candidate.Lifecycle == LifecycleKind.Road)
                    roadVehicles.Add(candidate.Vehicle);
            }

            if (railVehicles.Count > 0)
                m_Runtime.m_RailEventSource.RebaselineStartup(railVehicles, frame);
            if (roadVehicles.Count > 0)
                m_Runtime.m_RoadEventSource.RebaselineStartup(roadVehicles, frame);
        }

        private void RestoreStartupStops(List<StartupCandidate> operational, uint frame)
        {
            for (int i = 0; i < operational.Count; i++)
            {
                StartupCandidate candidate = operational[i];
                bool prepared = candidate.Lifecycle == LifecycleKind.Rail
                    ? m_Runtime.m_RailEventSource.PrepareStartupStopSource(
                        candidate.Vehicle,
                        candidate.Line,
                        frame,
                        out bool boarding,
                        out int waypoint,
                        out _)
                    : m_Runtime.m_RoadEventSource.PrepareStartupStopSource(
                        candidate.Vehicle,
                        candidate.Line,
                        frame,
                        out boarding,
                        out waypoint,
                        out _);
                if (!prepared)
                    continue;

                m_Runtime.m_VehicleRegistry.PublishStartupRestore(
                    candidate.Vehicle,
                    candidate.Line);
                m_Runtime.m_VehicleCache.SeedStartupRunningLapStart(
                    candidate.Vehicle,
                    candidate.Line);
                StopFact restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                    candidate.Vehicle,
                    candidate.Line,
                    boarding,
                    waypoint,
                    frame);
                if (restoredStop.Exists)
                {
                    if (candidate.Lifecycle == LifecycleKind.Rail
                        && m_Runtime.m_VehicleView.TryGetState(candidate.Vehicle, out VehicleState state))
                        SetRegistrationReady(candidate.Vehicle, restoredStop.WaypointIndex, state, frame);
                    m_PublishStopFact(restoredStop);
                }
                else if (candidate.Lifecycle == LifecycleKind.Rail && boarding && waypoint < 0)
                {
                    m_PendingRegistrationConfirmations[candidate.Vehicle] = (candidate.Line, 2);
                }

                if (m_Runtime.m_VehicleView.TryGetState(candidate.Vehicle, out VehicleState finalState))
                {
                    if (finalState == VehicleState.Running)
                        m_Runtime.m_RuntimeEngine.CommitRunning(candidate.Vehicle, candidate.Line);
                    else if (finalState == VehicleState.Holding)
                        m_Runtime.m_Observation.Seed(candidate.Vehicle, candidate.Line, frame);
                }
            }
        }

        private void AdoptStartupCandidates(List<StartupCandidate> operational, uint frame)
        {
            m_Runtime.m_VehicleRegistry.BeginSilentRestore();
            try
            {
                for (int i = 0; i < operational.Count; i++)
                    AdoptStartupVehicle(operational[i], frame);
            }
            finally
            {
                m_Runtime.m_VehicleRegistry.EndSilentRestore();
            }
        }

        private void AdoptStartupVehicle(StartupCandidate candidate, uint frame)
        {
            Entity vehicle = candidate.Vehicle;
            Entity line = candidate.Line;
            VehicleState initialState;
            if (candidate.Lifecycle == LifecycleKind.Road)
            {
                bool waypointKnown = m_Runtime.m_RoadEventSource.TryReadCurrentWaypoint(
                    vehicle,
                    line,
                    out bool boarding,
                    out int waypointIndex);
                PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
                initialState = ResolveRoadInitialState(
                    waypointKnown,
                    waypointIndex,
                    boarding,
                    adoptExistingVehicles: true,
                    publicTransport.m_State);
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, frame, null);
                m_Runtime.m_RoadEventSource.RegisterSource(vehicle, line, waypointKnown ? waypointIndex : -1);
            }
            else
            {
                m_PendingRegistrationConfirmations.Remove(vehicle);
                PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
                bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
                DynamicBuffer<RouteWaypoint> waypoints =
                    m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
                initialState = InferInitialState(
                    vehicle,
                    waypoints,
                    publicTransport,
                    boarding,
                    -1,
                    adoptExistingVehicles: true,
                    out _);
                m_Runtime.m_RuntimeEngine.Adopt(vehicle, line, initialState, frame, null);
            }

            m_Runtime.m_VehicleCache.Restore(vehicle, line, initialState != VehicleState.Holding, out _);
        }

        private void LogStartupAdoption(
            uint frame,
            List<StartupCandidate> candidates,
            List<Entity> lines,
            List<StartupCandidate> operational)
        {
            if (!RtLog.VerboseEnabled)
                return;

            // 只在首次激活时读取一次，保留周期写回车辆缓存前的证据。
            var candidateLines = new Dictionary<Entity, Entity>();
            for (int i = 0; i < candidates.Count; i++)
                candidateLines[candidates[i].Vehicle] = candidates[i].Line;

            Entity city = m_Runtime.m_CitySystem.City;
            bool hasCache = city != Entity.Null
                && m_Runtime.EntityManager.HasBuffer<VehicleStateCacheElement>(city);
            m_Runtime.log.Info("[StartupAdoption] phase=complete frame=" + frame
                + " lines=" + lines.Count
                + " candidates=" + candidates.Count
                + " operationalCandidates=" + operational.Count
                + " registered=" + m_Runtime.m_VehicleView.Count
                + " cacheReady=" + m_Runtime.m_VehicleCacheBufferReady
                + " hasCache=" + hasCache);
            if (hasCache)
            {
                DynamicBuffer<VehicleStateCacheElement> cache =
                    m_Runtime.EntityManager.GetBuffer<VehicleStateCacheElement>(city, true);
                for (int i = 0; i < cache.Length; i++)
                {
                    VehicleStateCacheElement item = cache[i];
                    m_Runtime.log.Info("[StartupVehicleCache] frame=" + frame
                        + " vehicle=" + item.m_VehicleEntity.Index + ":" + item.m_VehicleEntity.Version
                        + " state=" + item.m_State + " targetMin=" + item.m_TargetMin);
                }
            }

            NativeArray<Entity> queriedLines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < queriedLines.Length; i++)
                {
                    Entity line = queriedLines[i];
                    bool hasMembers = m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line);
                    bool collected = lines.Contains(line);
                    bool isOperational = m_Runtime.m_LineServiceState.IsOperational(line);
                    m_Runtime.log.Info("[StartupAdoptionLine] frame=" + frame
                        + " line=" + line.Index + ":" + line.Version
                        + " name=" + m_Runtime.EntityName(line)
                        + " collected=" + collected + " operational=" + isOperational
                        + " hasMembers=" + hasMembers);
                    if (!hasMembers)
                        continue;

                    DynamicBuffer<RouteVehicle> members =
                        m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
                    for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                    {
                        Entity member = members[memberIndex].m_Vehicle;
                        Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(member);
                        bool exists = vehicle != Entity.Null && m_Runtime.EntityManager.Exists(vehicle);
                        bool hasTransport = exists && m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle);
                        bool hasRoute = exists && m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle);
                        bool hasTarget = exists && m_Runtime.EntityManager.HasComponent<Target>(vehicle);
                        bool retireLock = exists && m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle);
                        Entity route = hasRoute
                            ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route : Entity.Null;
                        Entity target = hasTarget
                            ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target : Entity.Null;
                        bool candidate = candidateLines.TryGetValue(vehicle, out Entity candidateLine);
                        bool registered = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state);
                        m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine);
                        string selection = !collected ? "line-not-collected"
                            : vehicle == Entity.Null ? "unresolved-member"
                            : !candidate ? "not-collected"
                            : candidateLine != line ? "collected-on-other-line"
                            : !isOperational ? "line-not-operational"
                            : "operational-candidate";
                        m_Runtime.log.Info("[StartupAdoptionVehicle] frame=" + frame
                            + " line=" + line.Index + ":" + line.Version
                            + " member=" + member.Index + ":" + member.Version
                            + " vehicle=" + vehicle.Index + ":" + vehicle.Version
                            + " selection=" + selection
                            + " candidateLine=" + candidateLine.Index + ":" + candidateLine.Version
                            + " exists=" + exists + " retireLock=" + retireLock
                            + " hasRoute=" + hasRoute + " route=" + route.Index + ":" + route.Version
                            + " pt=" + (hasTransport
                                ? m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State.ToString()
                                : "missing")
                            + " hasTarget=" + hasTarget + " target=" + target.Index + ":" + target.Version
                            + " registered=" + registered
                            + " state=" + (registered ? state.ToString() : "missing")
                            + " registeredLine=" + registeredLine.Index + ":" + registeredLine.Version);
                    }
                }
            }
            finally
            {
                queriedLines.Dispose();
            }
        }

        private void CollectStartupIndex(List<StartupCandidate> candidates, List<Entity> lines)
        {
            var lifecycles = new Dictionary<Entity, LifecycleKind>();
            using NativeArray<Entity> queriedLines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < queriedLines.Length; i++)
            {
                Entity line = queriedLines[i];
                if (!IsStartupLine(line))
                    continue;

                lines.Add(line);
                if (RuntimePorts.TryResolveLineLifecycle(m_Runtime, line, out LifecycleKind lifecycle))
                    lifecycles.Add(line, lifecycle);
            }

            using NativeArray<Entity> vehicles = m_VehicleQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < vehicles.Length; i++)
            {
                Entity vehicle = vehicles[i];
                Entity line = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                if (!lifecycles.TryGetValue(line, out LifecycleKind lifecycle))
                    continue;

                DynamicBuffer<RouteVehicle> members =
                    m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
                if (!IsStartupVehicle(vehicle, members))
                    continue;

                candidates.Add(new StartupCandidate
                {
                    Line = line,
                    Vehicle = vehicle,
                    Lifecycle = lifecycle
                });
            }

            lines.Sort(CompareEntity);
            candidates.Sort(CompareStartupCandidate);
        }

        private bool IsStartupLine(Entity line)
        {
            return m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true).Length >= 2
                && m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch());
        }

        private bool IsStartupVehicle(Entity vehicle, DynamicBuffer<RouteVehicle> members)
        {
            if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                return false;

            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            if ((publicTransport.m_State & (PublicTransportFlags.Returning | PublicTransportFlags.AbandonRoute)) != 0)
                return false;

            for (int i = 0; i < members.Length; i++)
            {
                if (m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle) == vehicle)
                    return true;
            }
            return false;
        }

        private List<StartupCandidate> BuildOperationalStartupCandidates(
            List<StartupCandidate> candidates,
            List<Entity> lines)
        {
            m_Runtime.m_VanillaServiceControl.BaselineStartup(
                lines,
                m_Runtime.m_SimClock.Snapshot);
            var operational = new List<StartupCandidate>();
            for (int i = 0; i < candidates.Count; i++)
            {
                StartupCandidate candidate = candidates[i];
                if (m_Runtime.m_LineServiceState.IsOperational(candidate.Line))
                    operational.Add(candidate);
            }
            return operational;
        }

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
                        out _,
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

        private void SetRegistrationReady(Entity vehicle, int waypoint, VehicleState state, uint frame)
        {
            if (waypoint == 0 && state == VehicleState.Holding)
                m_Runtime.m_RuntimeEngine.SetReady(vehicle, frame, 2d, m_Runtime.m_SimClock.Snapshot);
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

        private static int CompareStartupCandidate(StartupCandidate left, StartupCandidate right)
        {
            int line = CompareEntity(left.Line, right.Line);
            return line != 0 ? line : CompareEntity(left.Vehicle, right.Vehicle);
        }

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
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

        private void AdoptRoadCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles)
        {
            if (!m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle))
                return;

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
            uint? dispatchFrame = null;
            if (!adoptExistingVehicles
                && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
            {
                dispatchFrame = spawnRequestFrame;
                m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            }

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            m_Runtime.m_VehicleRegistry.BeginRestore(vehicle);

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
                if (restoredStop.Exists)
                {
                    m_PublishStopFact(restoredStop);
                }

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

                m_Runtime.m_VehicleRegistry.EndRestore(line);
            }
            catch
            {
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }

            if (m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState finalState))
            {
                if (finalState == VehicleState.Running)
                    m_Runtime.m_RuntimeEngine.CommitRunning(vehicle, line);
                else if (finalState == VehicleState.Holding)
                    m_Runtime.m_Observation.Seed(vehicle, line, nowFrame);
            }

            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(
                    line,
                    m_Runtime.m_RuntimeLifecycleHost.Minute(),
                    vehicle,
                    initialState);
        }

        private static VehicleState ResolveRoadInitialState(
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
            bool atOrigin = waypointIndex == 0;
            VehicleState initialState = InferInitialState(
                vehicle,
                waypoints,
                publicTransport,
                boarding,
                waypointIndex,
                adoptExistingVehicles,
                out string initialReason);
            uint? dispatchFrame = null;
            if (!adoptExistingVehicles
                && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
            {
                dispatchFrame = spawnRequestFrame;
                m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            }

            m_Runtime.m_RailEventSource.RegisterSource(
                vehicle,
                line,
                publicTransport,
                waypointIndex,
                waypoints.Length);
            m_Runtime.m_VehicleRegistry.BeginRestore(vehicle);
            bool restored = false;
            VehicleState finalState = default;
            int finalTarget = -1;
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
            if (restoredStop.Exists)
            {
                m_PublishStopFact(restoredStop);
            }
            m_Runtime.m_RailEventSource.CommitWaypoint(vehicle, waypointIndex);
            m_Runtime.m_UICache.Remove(vehicle);
            m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "register-reset");
            if (initialReason == "boarding-midway")
                m_Runtime.TrackProjection.MarkVehicleProgressSuspect(vehicle, initialReason);

            bool preferOriginHolding = initialState == VehicleState.Holding
                && waypointIndex == 0;
            restored = RestoreLiveCache(
                vehicle,
                line,
                !preferOriginHolding,
                rail: true);
            if (!restored && initialState == VehicleState.Running)
                restored = m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, initialReason);
            finalState = m_Runtime.m_VehicleView.GetState(vehicle);
            finalTarget = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            m_Runtime.m_VehicleRegistry.EndRestore(line);
            m_Runtime.m_RailEventSource.RefreshOwners(vehicle);
            }
            catch
            {
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }
            SetRegistrationReady(vehicle, waypointIndex, finalState, nowFrame);
            if (boarding && waypointIndex < 0)
                m_PendingRegistrationConfirmations[vehicle] = (line, 2);
            if (finalState == VehicleState.Running)
                m_Runtime.m_RuntimeEngine.CommitRunning(vehicle, line);
            if (finalState == VehicleState.Holding)
                m_Runtime.m_Observation.Seed(vehicle, line, nowFrame);

            if (RtLog.VerboseEnabled)
            {
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
                        + " atA0=" + (atOrigin ? "1" : "0")
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
            if (!adoptExistingVehicles)
                m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(line, m_Runtime.m_RuntimeLifecycleHost.Minute(), vehicle, finalState);
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

            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0)
            {
                reason = "returning";
                return VehicleState.Retiring;
            }

            bool hasRouteProgress = m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition);
            // 原版出库即设置 EnRoute；首次停站或路点续接前，路径进度不能证明已经运营。
            if (!boarding
                && (publicTransport.m_State & (PublicTransportFlags.EnRoute | PublicTransportFlags.RouteSource)) == PublicTransportFlags.EnRoute)
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
    }
}
