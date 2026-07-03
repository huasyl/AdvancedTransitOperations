using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistrar
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly List<Entity> m_DisabledLineLateSpawnRetireQueue = new List<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnRetireQueueSeen = new HashSet<Entity>();
        private readonly HashSet<Entity> m_DisabledLineLateSpawnHandledLines = new HashSet<Entity>();

        public VehicleRegistrar(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal IReadOnlyList<Entity> DisabledLineLateSpawnRetireQueue => m_DisabledLineLateSpawnRetireQueue;

        internal void ClearDisabledLineLateSpawnRetireQueue()
        {
            m_DisabledLineLateSpawnRetireQueue.Clear();
            m_DisabledLineLateSpawnRetireQueueSeen.Clear();
            m_DisabledLineLateSpawnHandledLines.Clear();
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
                    lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
                    foreach (Entity line in lines)
                        RegisterLine(line, fullSweep, rvBuffers, wpBuffers, modBuffers);
                }
                else
                {
                    spawnLines = m_Runtime.m_SpawningLines.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnLines.Length; i++)
                        RegisterLine(spawnLines[i], fullSweep, rvBuffers, wpBuffers, modBuffers);

                    spawnRequestLines = m_Runtime.m_LineSpawnRequestFrame.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < spawnRequestLines.Length; i++)
                        RegisterLine(spawnRequestLines[i], fullSweep, rvBuffers, wpBuffers, modBuffers);
                }
            }
            finally
            {
                if (lines.IsCreated) lines.Dispose();
                if (spawnLines.IsCreated) spawnLines.Dispose();
                if (spawnRequestLines.IsCreated) spawnRequestLines.Dispose();
            }
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

            string lineTag = "线路" + line.Index;
            HashSet<Entity> seenVehicles = new HashSet<Entity>();

            if (!adoptExistingVehicles && !m_Runtime.m_LineProfile.IsDiagnosed(line))
            {
                m_Runtime.m_LineProfile.MarkDiagnosed(line);
                m_Runtime.m_TrackModel.LogLineTrackChainDiagnostics(line);
                string lineName = m_Runtime.EntityName(line);
                if (RtLog.VerboseEnabled)
                    m_Runtime.log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
            }

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = m_Runtime.m_Resolve.RuntimeVehicle(rvs[i].m_Vehicle);
                if (!m_Runtime.EntityManager.Exists(v)) continue;
                if (!seenVehicles.Add(v)) continue;
                if (m_Runtime.m_VehicleView.Contains(v)) continue;

                        Game.Vehicles.PublicTransport pt0 =
                            m_Runtime.EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;
                        if ((pt0.m_State & PublicTransportFlags.Returning) != 0)
                            continue;

                        int initWpIdx = boarding0 ? m_Runtime.m_WaypointIndex.Compute(v, wps) : -1;
                        bool atA0 = initWpIdx == 0;

                        VehicleState initState = InferInitialState(
                            v,
                            wps,
                            pt0,
                            boarding0,
                            initWpIdx,
                            adoptExistingVehicles,
                            out string initReason);
                        uint? dispatchFrame = null;
                        if (!adoptExistingVehicles
                            && m_Runtime.m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
                        {
                            dispatchFrame = spawnRequestFrame;
                            m_Runtime.m_LineSpawnRequestFrame.Remove(line);
                        }

                        m_Runtime.m_RuntimeController.Adopt(v, line, initState, m_Runtime.m_SimulationSystem.frameIndex, dispatchFrame);
                        m_Runtime.m_ObsPersist.SetLapDistance(v, -1f);
                        byte boardingByte = boarding0 ? (byte)1 : (byte)0;
                        m_Runtime.m_LastEffectiveBoardingState[v] = boardingByte;
                        m_Runtime.m_LastOfficialBoardingState[v] = boardingByte;
                        if (boarding0 && initWpIdx >= 0)
                        {
                            m_Runtime.m_StopSessionLine[v] = line;
                            m_Runtime.m_StopSessionWaypointIndex[v] = initWpIdx;
                            m_Runtime.m_StopSessionArrivalFrame[v] = m_Runtime.m_SimulationSystem.frameIndex;
                            m_Runtime.m_StopSessionBoardingChangeCount[v] = 0;
                            m_Runtime.m_DeparturePendingSinceFrame.Remove(v);
                            m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(v);
                        }
                        m_Runtime.m_CachedWpIdx[v] = initWpIdx;
                        m_Runtime.m_UICache.Remove(v);
                        m_Runtime.m_VehicleLabels.Remove(v);
                        m_Runtime.TrackProjection.ClearVehicleProgressSuspect(v, "register-reset");
                        if (initReason == "boarding-midway")
                            m_Runtime.TrackProjection.MarkVehicleProgressSuspect(v, initReason);

                        if (boarding0 && initWpIdx < 0)
                        {
                            m_Runtime.m_RuntimeLog.BvMisfireCandidate(
                                v,
                                "线路" + line.Index,
                                "register",
                                "boarding-without-waypoint",
                                m_Runtime.m_SimulationSystem.frameIndex);
                        }

                        bool preferOriginHolding = initState == VehicleState.Holding
                            && (initReason == "at-origin"
                                || initReason == "boarding-origin-fallback"
                                || initReason.StartsWith("route-progress-origin-fallback"));
                        bool restored = m_Runtime.m_VehicleCache.Restore(v, line, !preferOriginHolding);
                        if (!restored && initState == VehicleState.Running)
                            restored = m_Runtime.m_VehicleCache.RestoreRun(v, line, wps, initReason);
                        VehicleState finalState = m_Runtime.m_VehicleView.GetState(v);
                        int finalTarget = m_Runtime.m_VehicleView.TryGetTarget(v, out int ft) ? ft : -1;
                        if (finalState == VehicleState.Holding)
                            m_Runtime.m_Observation.Seed(v, line, m_Runtime.m_SimulationSystem.frameIndex);

                        if (finalState == VehicleState.Running)
                            m_Runtime.m_VehicleLabels.SetLocalized(v, "Running", "运行中", finalTarget >= 0 ? " " + DispatchRuntimeSystem.SlotStr(finalTarget) : "");
                        else if (finalState == VehicleState.Holding)
                            m_Runtime.m_VehicleLabels.SetLocalized(
                                v,
                                finalTarget >= 0 ? "Holding" : "HoldingWaitingDispatch",
                                finalTarget >= 0 ? "候车" : "候车 等待调度",
                                finalTarget >= 0 ? " " + DispatchRuntimeSystem.SlotStr(finalTarget) : "");
                        else
                            m_Runtime.m_VehicleLabels.SetLocalized(v, atA0 ? "HoldingWaitingDispatch" : "GoingOrigin", atA0 ? "候车 等待调度" : "前往始发站");

                        if (RtLog.VerboseEnabled)
                        {
                            m_Runtime.log.Info("[注册] " + lineTag + " 车辆" + v.Index
                                + " 初始:" + initState + " 最终:" + finalState
                                + (restored ? "(缓存恢复)" : "")
                                + " targetMin=" + finalTarget
                                + " initReason=" + initReason
                                + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(v));
                            m_Runtime.m_RuntimeLog.Once(
                                m_Runtime.m_RuntimeLog.m_RouteVehicleOwnerMismatchLogCache,
                                v,
                                "register-detail|line=" + line.Index
                                    + "|state=" + finalState
                                    + "|target=" + (m_Runtime.EntityManager.HasComponent<Target>(v) ? m_Runtime.EntityManager.GetComponentData<Target>(v).m_Target.Index : -1)
                                    + "|route=" + (m_Runtime.EntityManager.HasComponent<CurrentRoute>(v) ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(v).m_Route.Index : -1),
                                "[RegisterDetail] " + lineTag + " 车辆" + v.Index
                                    + " " + m_Runtime.m_RuntimeLog.VehicleOwnership(line, v, finalState, finalTarget, "register")
                                    + " initReason=" + initReason
                                    + " restored=" + (restored ? "1" : "0")
                                    + " atA0=" + (atA0 ? "1" : "0")
                                    + " initWp=" + initWpIdx);
                            if (!adoptExistingVehicles)
                            {
                                m_Runtime.log.Info("[OfficialSpawnResult] line=" + line.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + finalState
                                    + " targetMin=" + finalTarget
                                    + " initReason=" + initReason
                                    + " depot=" + m_Runtime.m_SelectPanel.DescribeVehicleOwnerDepot(v));
                            }
                        }
                        if (!adoptExistingVehicles)
                            m_Runtime.m_SelectPanel.RecordLineVehicleRegisterSummary(line, m_Runtime.m_RuntimeShell.Minute(), v, finalState);
            }

            if (adoptExistingVehicles)
                m_Runtime.m_LineInitialAdopted.Add(line);
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
                if (m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS))
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
                    && m_Runtime.m_LineProfile.IsWithinOriginDistance(vehicle, waypoints, DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS)
                    && (boarding || arriving))
                {
                    reason = "route-progress-origin-fallback wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                    return VehicleState.Holding;
                }
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return (boarding && nextWaypointIndex == 0) ? VehicleState.Holding : VehicleState.Running;
            }

            float originDistance = m_Runtime.m_LineProfile.DistanceToOrigin(vehicle, waypoints);
            if (originDistance > DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS)
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
