using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistrar
    {
        private struct VehicleCandidate
        {
            public Entity Line;
            public Entity Vehicle;
            public int LineIndex;
            public int VehicleIndex;
        }

        [BurstCompile]
        private struct FilterVehiclesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Lines;
            [ReadOnly] public BufferLookup<RouteVehicle> RouteVehicles;
            [ReadOnly] public ComponentLookup<Controller> Controllers;
            [ReadOnly] public ComponentLookup<Owner> Owners;
            [ReadOnly] public ComponentLookup<PublicTransport> PublicTransports;
            [ReadOnly] public BufferLookup<LayoutElement> Layouts;
            public NativeParallelHashSet<Entity>.ParallelWriter Seen;
            public NativeList<VehicleCandidate>.ParallelWriter Candidates;

            public void Execute(int index)
            {
                Entity line = Lines[index];
                if (!RouteVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) return;
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = Resolve(vehicles[i].m_Vehicle);
                    if (vehicle == Entity.Null) continue;
                    if (!Seen.Add(vehicle)) continue;
                    PublicTransport publicTransport = PublicTransports[vehicle];
                    if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0) continue;
                    Candidates.AddNoResize(new VehicleCandidate
                    {
                        Line = line,
                        Vehicle = vehicle,
                        LineIndex = index,
                        VehicleIndex = i
                    });
                }
            }

            private Entity Resolve(Entity vehicle)
            {
                Entity current = vehicle;
                Entity fallback = Entity.Null;
                for (int i = 0; current != Entity.Null && i < 16; i++)
                {
                    bool hasPublicTransport = PublicTransports.HasComponent(current);
                    if (hasPublicTransport) fallback = current;
                    if (hasPublicTransport && Layouts.HasBuffer(current)) return current;
                    if (Controllers.HasComponent(current))
                    {
                        Entity controller = Controllers[current].m_Controller;
                        if (controller != Entity.Null && controller != current)
                        {
                            current = controller;
                            continue;
                        }
                    }
                    if (!Owners.HasComponent(current)) break;
                    Entity owner = Owners[current].m_Owner;
                    if (owner == Entity.Null || owner == current) break;
                    current = owner;
                }
                return fallback;
            }
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly List<Entity> m_DisabledLineLateSpawnRetireQueue = new List<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnRetireQueueSeen = new HashSet<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnHandledLines = new HashSet<Entity>();
        // 跨来源帧候选：第二步只保留完整 Entity，第三步才由 Register 唯一消费。
        private readonly HashSet<Entity> m_PendingRebindCandidates = new HashSet<Entity>();

        public VehicleRegistrar(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal IReadOnlyList<Entity> DisabledLineLateSpawnRetireQueue => m_DisabledLineLateSpawnRetireQueue;
        internal IReadOnlyCollection<Entity> PendingRebindCandidates => m_PendingRebindCandidates;

        internal void ClearDisabledLineLateSpawnRetireQueue()
        {
            m_DisabledLineLateSpawnRetireQueue.Clear();
            m_DisabledLineLateSpawnRetireQueueSeen.Clear();
            m_DisabledLineLateSpawnHandledLines.Clear();
        }

        internal void ClearPendingRebindCandidates() => m_PendingRebindCandidates.Clear();

        internal void ObserveRailRoute(Entity vehicle)
        {
            if (vehicle != Entity.Null && m_Runtime.m_VehicleView.Contains(vehicle))
                m_PendingRebindCandidates.Add(vehicle);
        }

        public void Register(bool fullSweep)
        {
            NativeArray<Entity> lines = default;
            NativeArray<Entity> spawnLines = default;
            NativeArray<Entity> spawnRequestLines = default;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            BufferLookup<RouteModifier> modBuffers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            ClearDisabledLineLateSpawnRetireQueue();
            try
            {
                if (fullSweep)
                {
                    lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.TempJob);
                    RegisterFullSweep(lines, rvBuffers, wpBuffers, modBuffers);
                }
                else
                {
                    spawnLines = m_Runtime.m_SpawningLines.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnLines.Length; i++)
                        RegisterLine(spawnLines[i], fullSweep, rvBuffers, wpBuffers, modBuffers);

                    spawnRequestLines = m_Runtime.m_LineSpawnRequestFrame.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnRequestLines.Length; i++)
                    {
                        Entity line = spawnRequestLines[i];
                        bool alreadyRegistered = false;
                        for (int j = 0; j < spawnLines.Length; j++)
                        {
                            if (spawnLines[j] != line) continue;
                            alreadyRegistered = true;
                            break;
                        }
                        if (!alreadyRegistered)
                            RegisterLine(line, fullSweep, rvBuffers, wpBuffers, modBuffers);
                    }
                }
            }
            finally
            {
                if (lines.IsCreated) lines.Dispose();
                if (spawnLines.IsCreated) spawnLines.Dispose();
                if (spawnRequestLines.IsCreated) spawnRequestLines.Dispose();
            }

            if (fullSweep || m_Runtime.m_RailEventSource.CollectedThisFrame(m_Runtime.m_SimulationSystem.frameIndex))
                DrainRebindCandidates(rvBuffers, wpBuffers);
        }

        private void RegisterFullSweep(
            NativeArray<Entity> lines,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteWaypoint> wpBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            using var eligible = new NativeList<Entity>(lines.Length, Allocator.TempJob);
            int candidateCapacity = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)) continue;
                bool hasPendingSpawn = m_Runtime.m_SpawningLines.ContainsKey(line)
                    || m_Runtime.m_LineSpawnRequestFrame.ContainsKey(line);
                if (hasPendingSpawn && m_Runtime.EntityManager.HasComponent<Disabled>(line))
                {
                    m_DisabledLineLateSpawnHandledLines.Add(line);
                    HandleDisabledLinePendingSpawn(line, rvBuffers, modBuffers);
                    continue;
                }
                if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles)) continue;
                if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints) || waypoints.Length < 2) continue;
                if (!m_Runtime.m_LineProfile.IsStable(line, waypoints)) continue;
                if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch())) continue;
                eligible.Add(line);
                candidateCapacity += vehicles.Length;
                DiagnoseLine(line, waypoints);
            }

            if (eligible.Length != 0 && candidateCapacity != 0)
            {
                using var candidates = new NativeList<VehicleCandidate>(candidateCapacity, Allocator.TempJob);
                using var jobSeen = new NativeParallelHashSet<Entity>(candidateCapacity, Allocator.TempJob);
                var job = new FilterVehiclesJob
                {
                    Lines = eligible.AsArray(),
                    RouteVehicles = rvBuffers,
                    Controllers = m_Runtime.GetComponentLookup<Controller>(true),
                    Owners = m_Runtime.GetComponentLookup<Owner>(true),
                    PublicTransports = m_Runtime.GetComponentLookup<PublicTransport>(true),
                    Layouts = m_Runtime.GetBufferLookup<LayoutElement>(true),
                    Seen = jobSeen.AsParallelWriter(),
                    Candidates = candidates.AsParallelWriter()
                };
                job.Schedule(eligible.Length, 1).Complete();

                var ordered = new List<VehicleCandidate>(candidates.Length);
                for (int i = 0; i < candidates.Length; i++) ordered.Add(candidates[i]);
                ordered.Sort((left, right) => left.LineIndex != right.LineIndex
                    ? left.LineIndex.CompareTo(right.LineIndex)
                    : left.VehicleIndex.CompareTo(right.VehicleIndex));
                var seen = new HashSet<Entity>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    VehicleCandidate candidate = ordered[i];
                    if (!seen.Add(candidate.Vehicle)) continue;
                    if (!m_Runtime.EntityManager.Exists(candidate.Vehicle)) continue;
                    if (m_Runtime.m_VehicleView.Contains(candidate.Vehicle))
                    {
                        ObserveRebind(candidate.Line, candidate.Vehicle);
                        continue;
                    }
                    if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(candidate.Vehicle)) continue;
                    if (!wpBuffers.TryGetBuffer(candidate.Line, out DynamicBuffer<RouteWaypoint> waypoints)) continue;
                    bool adoptExisting = !m_Runtime.m_LineInitialAdopted.Contains(candidate.Line);
                    AdoptCandidate(candidate.Line, candidate.Vehicle, waypoints, adoptExisting);
                }
            }

            for (int i = 0; i < eligible.Length; i++)
            {
                Entity line = eligible[i];
                if (rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> knownVehicles))
                {
                    for (int vehicleIndex = 0; vehicleIndex < knownVehicles.Length; vehicleIndex++)
                        ObserveRebind(line, knownVehicles[vehicleIndex].m_Vehicle);
                }
                if (!m_Runtime.m_LineInitialAdopted.Contains(line))
                    m_Runtime.m_LineInitialAdopted.Add(line);
            }
        }

        private void DiagnoseLine(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (!m_Runtime.m_LineInitialAdopted.Contains(line) || m_Runtime.m_LineProfile.IsDiagnosed(line)) return;
            m_Runtime.m_LineProfile.MarkDiagnosed(line);
            m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
            if (RtLog.VerboseEnabled)
                m_Runtime.log.Info("[诊断] 线路" + line.Index + " (" + m_Runtime.EntityName(line) + ") waypoint数=" + waypoints.Length);
        }

        private void RegisterLine(
            Entity line,
            bool fullSweep,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteWaypoint> wpBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line)) return;
            if (m_DisabledLineLateSpawnHandledLines.Contains(line)) return;

            bool hasPendingSpawn = m_Runtime.m_SpawningLines.ContainsKey(line)
                || m_Runtime.m_LineSpawnRequestFrame.ContainsKey(line);
            if (hasPendingSpawn && m_Runtime.EntityManager.HasComponent<Disabled>(line))
            {
                m_DisabledLineLateSpawnHandledLines.Add(line);
                HandleDisabledLinePendingSpawn(line, rvBuffers, modBuffers);
                return;
            }

            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs)) return;
            if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2) return;
            if (!m_Runtime.m_LineProfile.IsStable(line, wps)) return;
            if (!m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch())) return;
            bool adoptExistingVehicles = !m_Runtime.m_LineInitialAdopted.Contains(line);
            bool isHotLine = adoptExistingVehicles || m_Runtime.m_SpawningLines.ContainsKey(line);
            if (!fullSweep && !isHotLine) return;

            if (!adoptExistingVehicles && !m_Runtime.m_LineProfile.IsDiagnosed(line))
            {
                m_Runtime.m_LineProfile.MarkDiagnosed(line);
                m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
                if (RtLog.VerboseEnabled)
                {
                    string lineTag = "线路" + line.Index;
                    string lineName = m_Runtime.EntityName(line);
                    m_Runtime.log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                }
            }

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!m_Runtime.EntityManager.Exists(v)) continue;
                if (m_Runtime.m_VehicleView.Contains(v))
                {
                    ObserveRebind(line, v);
                    continue;
                }
                if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(v))
                {
                    continue;
                }

                AdoptCandidate(line, v, wps, adoptExistingVehicles);
            }

            if (adoptExistingVehicles)
                m_Runtime.m_LineInitialAdopted.Add(line);
        }

        private void ObserveRebind(Entity line, Entity vehicle)
        {
            Entity resolved = m_Runtime.m_Resolve.RuntimeVehicle(vehicle);
            if (resolved == Entity.Null || !m_Runtime.EntityManager.Exists(resolved)) return;
            if (!m_Runtime.m_VehicleView.TryGetLine(resolved, out Entity registeredLine) || registeredLine == line) return;
            m_PendingRebindCandidates.Add(resolved);
        }

        private void DrainRebindCandidates(
            BufferLookup<RouteVehicle> routeVehicles,
            BufferLookup<RouteWaypoint> waypointBuffers)
        {
            if (m_PendingRebindCandidates.Count == 0)
                return;

            var candidates = new List<Entity>(m_PendingRebindCandidates);
            candidates.Sort(CompareEntity);
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity vehicle = candidates[i];
                if (vehicle == Entity.Null
                    || !m_Runtime.EntityManager.Exists(vehicle)
                    || !m_Runtime.m_VehicleView.Contains(vehicle)
                    || !m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity oldLine))
                {
                    m_PendingRebindCandidates.Remove(vehicle);
                    continue;
                }

                if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    continue;

                if (!TryGetRebindRoute(vehicle, routeVehicles, waypointBuffers, out Entity newLine, out DynamicBuffer<RouteWaypoint> waypoints))
                    continue;

                if (newLine == oldLine)
                {
                    m_PendingRebindCandidates.Remove(vehicle);
                    continue;
                }

                RebindVehicle(oldLine, newLine, vehicle, waypoints);
                m_PendingRebindCandidates.Remove(vehicle);
            }
        }

        private bool TryGetRebindRoute(
            Entity vehicle,
            BufferLookup<RouteVehicle> routeVehicles,
            BufferLookup<RouteWaypoint> waypointBuffers,
            out Entity line,
            out DynamicBuffer<RouteWaypoint> waypoints)
        {
            line = Entity.Null;
            waypoints = default;
            if (!m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle))
            {
                return false;
            }

            if ((m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Returning) != 0)
                return false;

            line = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || m_Runtime.EntityManager.HasComponent<Disabled>(line)
                || !m_Runtime.EntityManager.HasComponent<TransportLine>(line)
                || !routeVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> members)
                || !waypointBuffers.TryGetBuffer(line, out waypoints)
                || waypoints.Length < 2
                || !m_Runtime.m_LineProfile.IsStable(line, waypoints)
                || !m_Runtime.m_LineView.ManagedRuntime(line, m_Runtime.m_Features.Dispatch()))
            {
                return false;
            }

            for (int i = 0; i < members.Length; i++)
            {
                Entity member = m_Runtime.m_Resolve.RuntimeVehicle(members[i].m_Vehicle);
                if (member == vehicle)
                    return true;
            }

            return false;
        }

        private void RebindVehicle(
            Entity oldLine,
            Entity newLine,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            bool hadStop = m_Runtime.m_StopRuntime.CancelRebind(vehicle);
            if (hadStop)
                PassengerFlow.Runtime.Current?.CancelStop(vehicle);

            m_Runtime.m_Bypass.ClearVehicle(vehicle, "换线");
            ClearRebindRuntime(vehicle);

            ulong sourceGeneration = m_Runtime.m_RailEventSource.RebindSource(vehicle);
            m_Runtime.m_VehicleRegistry.BeginRebind(vehicle, oldLine, sourceGeneration);
            try
            {
                m_Runtime.m_VehicleRegistry.Remove(vehicle);
                AdoptCandidate(
                    newLine,
                    vehicle,
                    waypoints,
                    adoptExistingVehicles: true,
                    completeRestore: false,
                    restoreVehicleCache: false);
                m_Runtime.m_VehicleRegistry.EndRestore(newLine);
            }
            catch
            {
                m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }

            m_Runtime.m_RuntimeWorksets.MarkDirty(oldLine);
            m_Runtime.m_RuntimeWorksets.MarkDirty(newLine);
        }

        private void ClearRebindRuntime(Entity vehicle)
        {
            m_Runtime.m_TrackProjection.ClearVehicle(vehicle);
            m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "route-rebind");
            m_Runtime.m_CachedWpIdx.Remove(vehicle);
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_ObsPersist.ClearLap(vehicle);
            m_Runtime.m_Observation.ClearDwellDeadlineCache(vehicle);
            m_Runtime.m_ObsPersist.ClearDwell(vehicle);
            m_Runtime.m_Observation.ClearVehicleSlices(vehicle);
            m_Runtime.m_Observation.ClearDebug(vehicle);
            m_Runtime.m_Observation.ClearDispatchEta(vehicle);
            m_Runtime.m_Announcements.RemoveVehicle(vehicle);
            m_Runtime.m_StationContextQuery.RemoveVehicle(vehicle);
            m_Runtime.m_UICache.Remove(vehicle);
            m_Runtime.m_VehicleLabels.Remove(vehicle);
            m_Runtime.m_BoardingFirstFrameGuardState.Remove(vehicle);
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.BvMisfire);
            m_Runtime.m_PreparingFixCooldownUntil.Remove(vehicle);
            m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.PreparingCooldown);
            m_Runtime.m_RuntimeEngine.ClearAssistLaunchPending(vehicle);
            m_Runtime.m_SpawnIntentTrace.Remove(vehicle);
            m_Runtime.m_RuntimeLog.ClearVehicle(vehicle);
        }

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }

        private void AdoptCandidate(
            Entity line,
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool adoptExistingVehicles,
            bool completeRestore = true,
            bool restoreVehicleCache = true)
        {
            PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
            bool boarding = (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
            if ((publicTransport.m_State & PublicTransportFlags.Returning) != 0) return;

            int waypointIndex = boarding ? m_Runtime.m_WaypointIndex.Compute(vehicle, waypoints) : -1;
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

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (completeRestore)
                m_Runtime.m_VehicleRegistry.BeginRestore(vehicle, m_Runtime.m_RailEventSource.RegisterSource(vehicle));
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
            var restoredStop = m_Runtime.m_StopRuntime.RestoreRegistration(
                vehicle,
                line,
                boarding,
                waypointIndex,
                nowFrame);
            if (restoredStop.Exists)
            {
                PassengerFlow.Runtime.Current?.RestoreStop(
                    restoredStop.Vehicle,
                    restoredStop.Line,
                    restoredStop.WaypointIndex,
                    restoredStop.Frame);
            }
            m_Runtime.m_CachedWpIdx[vehicle] = waypointIndex;
            m_Runtime.m_UICache.Remove(vehicle);
            m_Runtime.m_VehicleLabels.Remove(vehicle);
            m_Runtime.TrackProjection.ClearVehicleProgressSuspect(vehicle, "register-reset");
            if (initialReason == "boarding-midway")
                m_Runtime.TrackProjection.MarkVehicleProgressSuspect(vehicle, initialReason);

            if (boarding && waypointIndex < 0)
            {
                m_Runtime.m_RuntimeLog.BvMisfireCandidate(
                    vehicle,
                    "线路" + line.Index,
                    "register",
                    "boarding-without-waypoint",
                    nowFrame);
            }

            bool preferOriginHolding = initialState == VehicleState.Holding
                && (initialReason == "at-origin"
                    || initialReason == "boarding-origin-fallback"
                    || initialReason.StartsWith("route-progress-origin-fallback"));
            if (restoreVehicleCache)
            {
                restored = m_Runtime.m_VehicleCache.Restore(vehicle, line, !preferOriginHolding);
                if (!restored && initialState == VehicleState.Running)
                    restored = m_Runtime.m_VehicleCache.RestoreRun(vehicle, line, waypoints, initialReason);
            }
            finalState = m_Runtime.m_VehicleView.GetState(vehicle);
            finalTarget = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
            if (completeRestore)
                m_Runtime.m_VehicleRegistry.EndRestore(line);
            }
            catch
            {
                if (completeRestore)
                    m_Runtime.m_VehicleRegistry.CancelRestore();
                throw;
            }
            if (finalState == VehicleState.Holding)
                m_Runtime.m_Observation.Seed(vehicle, line, nowFrame);
            else if (finalState == VehicleState.Running)
                m_Runtime.m_Bypass.ArmExpressRescue(vehicle, line, nowFrame);

            if (finalState == VehicleState.Running)
                m_Runtime.m_VehicleLabels.SetLocalized(vehicle, "Running", "运行中", finalTarget >= 0 ? " " + ModRuntimeHostSystem.SlotStr(finalTarget) : "");
            else if (finalState == VehicleState.Holding)
                m_Runtime.m_VehicleLabels.SetLocalized(
                    vehicle,
                    finalTarget >= 0 ? "Holding" : "HoldingWaitingDispatch",
                    finalTarget >= 0 ? "候车" : "候车 等待调度",
                    finalTarget >= 0 ? " " + ModRuntimeHostSystem.SlotStr(finalTarget) : "");
            else
                m_Runtime.m_VehicleLabels.SetLocalized(vehicle, atOrigin ? "HoldingWaitingDispatch" : "GoingOrigin", atOrigin ? "候车 等待调度" : "前往始发站");

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

        private void HandleDisabledLinePendingSpawn(
            Entity line,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers)
        {
            m_Runtime.m_SpawningLines.Remove(line);
            m_Runtime.m_LineSpawnRequestFrame.Remove(line);
            RestoreVehicleIntervalModifier(line, modBuffers);

            int queuedRetires = 0;
            if (rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
            {
                HashSet<Entity> seenVehicles = new HashSet<Entity>();
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity vehicle = m_Runtime.m_Resolve.RuntimeVehicle(rvs[i].m_Vehicle);
                    if (!m_Runtime.EntityManager.Exists(vehicle)) continue;
                    if (!seenVehicles.Add(vehicle)) continue;
                    if (m_Runtime.m_VehicleView.Contains(vehicle)) continue;
                    if (m_Runtime.EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    {
                        continue;
                    }
                    if (m_Runtime.EntityManager.HasComponent<Deleted>(vehicle)
                        || m_Runtime.EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        continue;
                    }
                    if (!m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Owner>(vehicle))
                    {
                        continue;
                    }
                    if (!m_DisabledLineLateSpawnRetireQueueSeen.Add(vehicle)) continue;

                    m_DisabledLineLateSpawnRetireQueue.Add(vehicle);
                    queuedRetires++;
                }
            }

            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[DisabledLineLateSpawnCleanup] 线路" + line.Index
                    + " 清理关闭线路残留产车状态 queuedRetires=" + queuedRetires);
            }
        }

        private static void RestoreVehicleIntervalModifier(
            Entity line,
            BufferLookup<RouteModifier> modBuffers)
        {
            if (!modBuffers.TryGetBuffer(line, out DynamicBuffer<RouteModifier> mods))
                return;

            int modifierIndex = (int)RouteModifierType.VehicleInterval;
            if (mods.Length <= modifierIndex)
                return;

            RouteModifier modifier = mods[modifierIndex];
            modifier.m_Delta = float2.zero;
            mods[modifierIndex] = modifier;
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
            bool arriving = (publicTransport.m_State & PublicTransportFlags.Arriving) != 0;

            if (initialWaypointIndex == 0)
            {
                reason = "at-origin";
                return VehicleState.Holding;
            }
            if (boarding)
            {
                if (m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, ModRuntimeHostSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS))
                {
                    if (!m_Runtime.m_RouteProgress.Try(vehicle, out int nearOriginWaypointIndex, out float nearOriginSegmentPosition)
                        || (nearOriginWaypointIndex == 1 && nearOriginSegmentPosition <= 0.10f)
                        || nearOriginWaypointIndex == 0)
                    {
                        reason = "boarding-origin-fallback";
                        return VehicleState.Holding;
                    }
                }
            }
            if (boarding && initialWaypointIndex > 0)
            {
                reason = "boarding-midway";
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

            if (m_Runtime.m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
            {
                bool nearOriginProgress = nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.05f);
                if (nearOriginProgress
                    && m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, ModRuntimeHostSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS)
                    && (boarding || arriving))
                {
                    reason = "route-progress-origin-fallback wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                    return VehicleState.Holding;
                }
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return (boarding && nextWaypointIndex == 0) ? VehicleState.Holding : VehicleState.Running;
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
