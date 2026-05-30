using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleRuntimeRegistrar
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public VehicleRuntimeRegistrar(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Register(bool fullSweep)
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            try
            {
                foreach (Entity line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2) continue;
                    if (!m_Runtime.IsLineStable(line, wps)) continue;
                    if (!m_Runtime.IsDispatchRuntimeManagedLine(line)) continue;
                    bool adoptExistingVehicles = !m_Runtime.m_LineInitialAdopted.Contains(line);
                    bool isHotLine = adoptExistingVehicles || m_Runtime.m_SpawningLines.ContainsKey(line);
                    if (!fullSweep && !isHotLine) continue;

                    string lineTag = "线路" + line.Index;
                    HashSet<Entity> seenVehicles = new HashSet<Entity>();

                    if (!adoptExistingVehicles && !m_Runtime.m_DiagnosedLines.Contains(line))
                    {
                        m_Runtime.m_DiagnosedLines.Add(line);
                        m_Runtime.LogLineTrackChainDiagnostics(line);
                        string lineName = m_Runtime.ResolveWorkbenchEntityName(line);
                        m_Runtime.log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                    }

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = m_Runtime.ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                        if (!m_Runtime.EntityManager.Exists(v)) continue;
                        if (!seenVehicles.Add(v)) continue;
                        if (m_Runtime.m_VehicleView.Contains(v)) continue;

                        Game.Vehicles.PublicTransport pt0 =
                            m_Runtime.EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;
                        if ((pt0.m_State & PublicTransportFlags.Returning) != 0)
                            continue;

                        int initWpIdx = boarding0 ? m_Runtime.ComputeWpIndex(v, wps) : -1;
                        bool atA0 = initWpIdx == 0;

                        VehicleState initState = m_Runtime.InferInitialVehicleState(
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
                        m_Runtime.m_LapObservations.SetDistance(v, -1f);
                        m_Runtime.m_LastBoarding[v] = boarding0;
                        m_Runtime.m_CachedWpIdx[v] = initWpIdx;
                        m_Runtime.m_UICache.Remove(v);
                        m_Runtime.TrackProjection.ClearVehicleProgressSuspect(v, "register-reset");
                        if (initReason == "boarding-midway")
                            m_Runtime.TrackProjection.MarkVehicleProgressSuspect(v, initReason);

                        if (boarding0 && initWpIdx < 0)
                        {
                            m_Runtime.ObserveBvMisfireCandidate(
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
                        bool restored = m_Runtime.TryRestoreVehicleState(v, line, !preferOriginHolding);
                        if (!restored && initState == VehicleState.Running)
                            restored = m_Runtime.RestoreRunningContextFromProgress(v, line, wps, initReason);
                        VehicleState finalState = m_Runtime.m_VehicleView.GetState(v);
                        int finalTarget = m_Runtime.m_VehicleView.TryGetTarget(v, out int ft) ? ft : -1;
                        if (finalState == VehicleState.Holding)
                            m_Runtime.TryRecordPreparingArrivalSample(v, line, m_Runtime.m_SimulationSystem.frameIndex);

                        if (finalState == VehicleState.Running)
                            m_Runtime.m_VehicleLabels.Set(v, "运行中" + (finalTarget >= 0 ? " " + DispatchRuntimeSystem.SlotStr(finalTarget) : ""));
                        else if (finalState == VehicleState.Holding)
                            m_Runtime.m_VehicleLabels.Set(v, finalTarget >= 0 ? "候车 " + DispatchRuntimeSystem.SlotStr(finalTarget) : "候车 等待调度");
                        else
                            m_Runtime.m_VehicleLabels.Set(v, atA0 ? "候车 等待调度" : "前往始发站");

                        m_Runtime.log.Info("[注册] " + lineTag + " 车辆" + v.Index
                            + " 初始:" + initState + " 最终:" + finalState
                            + (restored ? "(缓存恢复)" : "")
                            + " targetMin=" + finalTarget
                            + " initReason=" + initReason
                            + " depot=" + m_Runtime.m_SelectionPanel.DescribeVehicleOwnerDepot(v));
                        m_Runtime.LogVehicleStateOnce(
                            m_Runtime.m_RouteVehicleOwnerMismatchLogCache,
                            v,
                            "register-detail|line=" + line.Index
                                + "|state=" + finalState
                                + "|target=" + (m_Runtime.EntityManager.HasComponent<Target>(v) ? m_Runtime.EntityManager.GetComponentData<Target>(v).m_Target.Index : -1)
                                + "|route=" + (m_Runtime.EntityManager.HasComponent<CurrentRoute>(v) ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(v).m_Route.Index : -1),
                            "[RegisterDetail] " + lineTag + " 车辆" + v.Index
                                + " " + m_Runtime.BuildVehicleOwnershipDiagnostic(line, v, finalState, finalTarget, "register")
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
                                + " depot=" + m_Runtime.m_SelectionPanel.DescribeVehicleOwnerDepot(v));
                        }
                        if (!adoptExistingVehicles)
                            m_Runtime.m_SelectionPanel.RecordLineVehicleRegisterSummary(line, m_Runtime.CurrentGameMinute(), v, finalState);
                    }
                    if (adoptExistingVehicles)
                        m_Runtime.m_LineInitialAdopted.Add(line);
                }
            }
            finally
            {
                if (lines.IsCreated) lines.Dispose();
            }
        }
    }
}
