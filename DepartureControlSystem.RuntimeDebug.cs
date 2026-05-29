using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.SceneFlow;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary = new Dictionary<Entity, string>();

        internal void SetUILabel(Entity v, string msg)
        {
            var fs = new FixedString64Bytes(msg);
            if (!m_UICache.TryGetValue(v, out var cached) || cached != fs)
            {
                m_NameSystem.SetCustomName(v, msg);
                m_UICache[v] = fs;
            }
        }

        public string GetCurrentGameTimeLabel()
        {
            if (m_TimeSystem == null)
                return string.Empty;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            if (nowMin < 0)
                nowMin += 1440;

            return "[游戏时间 " + SlotStr(nowMin) + "]";
        }

        private void ClearLineDispatchDebugSummaries()
        {
            m_LineLastSpawnTriggerSummary.Clear();
            m_LineLastVehicleRegisterSummary.Clear();
            m_LineLastHoldingSummary.Clear();
            m_LineLastDispatchSampleSummary.Clear();
        }

        private void RecordLineSpawnTriggerSummary(Entity line, int nowMin, int slot, int actualCount)
        {
            if (line == Entity.Null)
                return;

            m_LineLastSpawnTriggerSummary[line] = SlotStr(nowMin)
                + " 班次" + SlotStr(slot)
                + " 真实产车命令 当前=" + actualCount;
        }

        private void RecordLineVehicleRegisterSummary(Entity line, int nowMin, Entity vehicle, VehicleState finalState)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            string depotSummary = DescribeVehicleOwnerDepot(vehicle);
            m_LineLastVehicleRegisterSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 注册 -> " + finalState
                + " depot=" + depotSummary;
        }

        private void RecordLineHoldingSummary(Entity line, int nowMin, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            m_LineLastHoldingSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 到站/Holding"
                + (targetMin >= 0 ? " " + SlotStr(targetMin) : " 等待调度");
        }

        private void RecordLineDispatchSampleSummary(Entity line, int nowMin, Entity vehicle, float sampleMinutes)
        {
            if (line == Entity.Null || vehicle == Entity.Null || sampleMinutes <= 0f)
                return;

            m_LineLastDispatchSampleSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 出库用时=" + sampleMinutes.ToString("F1") + "分钟";
        }

        private string DescribeVehicleOwnerDepot(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Owner>(vehicle))
            {
                return "-";
            }

            Entity depot = EntityManager.GetComponentData<Owner>(vehicle).m_Owner;
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return "-";

            string name = m_NameSystem.GetRenderedLabelName(depot);
            return string.IsNullOrEmpty(name)
                ? "#" + depot.Index
                : ("#" + depot.Index + "[" + name + "]");
        }

        private void AddDebugItem(InfoList list, string labelCn, string labelEn, string value)
        {
            list.Add(new InfoList.Item(labelCn + " / " + labelEn + ": " + value));
        }

        private string BuildLineAlertSummary(
            Entity line,
            int nextSlotOccupancy,
            int nearingTerminus,
            float lapCacheFrames,
            float dispatchCacheFrames,
            int spawning)
        {
            if (!IsWorkbenchTimetableApplied(line))
            {
                if (EntityManager.HasComponent<Disabled>(line))
                    return "line-disabled";
                return IsChineseLocale() ? "官方调度" : "Official dispatch";
            }

            string alerts = string.Empty;
            if (EntityManager.HasComponent<Disabled>(line))
                alerts = AppendAlert(alerts, "line-disabled");
            if (nextSlotOccupancy <= 0)
                alerts = AppendAlert(alerts, "next-slot-gap");
            if (spawning > 0)
                alerts = AppendAlert(alerts, "spawn-pending:" + spawning);
            if (nearingTerminus > 0)
                alerts = AppendAlert(alerts, "yield-guard:" + nearingTerminus);
            if (lapCacheFrames <= 0f)
                alerts = AppendAlert(alerts, "no-lap-cache");
            if (dispatchCacheFrames <= 0f)
                alerts = AppendAlert(alerts, "no-dispatch-cache");
            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleAlertSummary(Entity vehicle, Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return IsChineseLocale() ? "官方调度" : "Official dispatch";

            string alerts = string.Empty;
            if (m_BypassYieldBlocker.TryGetValue(vehicle, out Entity blockerVehicle) && blockerVehicle != Entity.Null)
                alerts = AppendAlert(alerts, "yielding-for:" + blockerVehicle.Index);
            if (m_BVMisfire.Contains(vehicle))
                alerts = AppendAlert(alerts, "bv-misfire");
            if (m_VehicleView.IsInbound(vehicle))
                alerts = AppendAlert(alerts, "nearing-terminus");
            if (m_VehicleView.TryGetCooldown(vehicle, out uint cooldownUntil) && m_SimulationSystem.frameIndex < cooldownUntil)
                alerts = AppendAlert(alerts, "launch-cooldown");
            if (targetMin >= 0 && m_DispatchScheduler.IsExpired(nowMin, targetMin))
                alerts = AppendAlert(alerts, "target-expired");
            if (line != Entity.Null
                && m_VehicleView.TryGetState(vehicle, out var state)
                && state == VehicleState.Idle
                && m_DispatchScheduler.ShouldProtectIdle(line, vehicle, nowMin))
            {
                alerts = AppendAlert(alerts, "yield-protected");
            }

            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleProgressSummary(Entity vehicle)
        {
            string cachedWaypoint = m_CachedWpIdx.TryGetValue(vehicle, out int waypointIndex) ? waypointIndex.ToString() : "-";
            string lapDistance = m_LapObservations.TryDistance(vehicle, out float distance) && distance >= 0f
                ? (distance / 1000f).ToString("F2") + " km"
                : "-";
            return "wp " + cachedWaypoint + " / lap " + lapDistance;
        }

        private string BuildVehicleTraversalProgressValue(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (vehicle == Entity.Null)
                return "-";

            if (!TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return IsChineseLocale() ? "未知" : "unknown";

            int progressPercent = (int)math.round(math.saturate(segmentPosition) * 100f);
            return "wp" + nextWaypointIndex + " / " + progressPercent + "%";
        }

        private string GetVehiclePanelStateCode(Entity vehicle, VehicleState vehicleState)
        {
            if (m_BypassDecision.TryGetLatchedBlocker(vehicle, out _)
                && (vehicleState == VehicleState.Holding || vehicleState == VehicleState.Running))
            {
                return "Yielding";
            }

            if (vehicleState == VehicleState.Holding
                && (!m_VehicleView.TryGetTarget(vehicle, out int holdingTarget) || holdingTarget < 0))
            {
                return "Idle";
            }

            return vehicleState.ToString();
        }

        private string EstimateVehicleEtaText(Entity vehicle, Entity line, VehicleState vehicleState)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return "-";

            float lineDurationFrames = ReadLineLapCache(line);
            bool lineHasHistory = lineDurationFrames > 0f;
            uint nowFrame = m_SimulationSystem.frameIndex;
            float etaFrames = float.MaxValue;

            if (vehicleState == VehicleState.Preparing)
            {
                var routeWaypoints = GetBufferLookup<Game.Routes.RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = EstimatePreparingArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames);
            }
            else if (vehicleState == VehicleState.Running)
            {
                var routeWaypoints = GetBufferLookup<Game.Routes.RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = EstimateRunningArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
            }
            else if (vehicleState == VehicleState.Holding)
            {
                etaFrames = 0f;
            }

            if (etaFrames == float.MaxValue)
                return "-";

            float etaMinutes = etaFrames / (float)SIM_FRAMES_PER_MINUTE;
            return etaMinutes.ToString("F1") + " min";
        }

        private string FormatFramesAsMinutes(float frames)
        {
            return frames > 0f ? (frames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
        }

        private string BuildVehicleStopDwellValue(Entity vehicle)
        {
            if (!m_StopDwell.TryStart(vehicle, out uint dwellSinceFrame))
                return "-";

            uint elapsedFrames = m_SimulationSystem.frameIndex > dwellSinceFrame
                ? m_SimulationSystem.frameIndex - dwellSinceFrame
                : 0u;
            return (elapsedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min";
        }

        private string BuildVehicleInboundTimeValue(Entity vehicle)
        {
            if (m_VehicleView.TryGetPreparing(vehicle, out uint prepStartFrame))
                return SlotStr((int)(prepStartFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440);

            if (m_VehicleView.TryGetOrigin(vehicle, out uint originSinceFrame))
                return SlotStr((int)(originSinceFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440);

            return "-";
        }

        private static string AppendAlert(string current, string alert)
        {
            return current.Length == 0 ? alert : current + ", " + alert;
        }

        private static string BoolDebugStr(bool value)
        {
            return value ? "是 / Yes" : "否 / No";
        }

        private static bool IsChineseLocale()
        {
            string locale = GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
            return locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        private static string LocalizedDispatchLabel()
        {
            return IsChineseLocale() ? "发车模式" : "Dispatch";
        }

        private static string LocalizedNextSlotLabel()
        {
            return IsChineseLocale() ? "下一班次" : "Next Slot";
        }

        private static string LocalizedNextSlotCoverageLabel()
        {
            return IsChineseLocale() ? "下一班次占用" : "Next Slot Coverage";
        }

        private static string LocalizedDispatchCacheLabel()
        {
            return IsChineseLocale() ? "出库缓存" : "Dispatch Cache";
        }

        private static string LocalizedOfficialDispatchValue()
        {
            return IsChineseLocale() ? "官方调度" : "Official dispatch";
        }

        private void FillVehicleDebugInfo(Entity v, InfoList list)
        {
            string state = m_VehicleView.TryGetState(v, out var st) ? st.ToString() : "Unknown";
            string lineStr = m_VehicleView.TryGetLine(v, out Entity line) ? line.Index.ToString() : "-";
            string targetStr = m_VehicleView.TryGetTarget(v, out int targetMin) && targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleView.TryGetSlot(v, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string cachedWp = m_CachedWpIdx.TryGetValue(v, out int wp) ? wp.ToString() : "-";
            string tagged = BoolDebugStr(m_VehicleView.IsInbound(v));
            string cooldown = BoolDebugStr(m_VehicleView.TryGetCooldown(v, out uint cd) && m_SimulationSystem.frameIndex < cd);
            string misfire = BoolDebugStr(m_BVMisfire.Contains(v));
            string lapStartFrame = m_LapObservations.TryStartFrame(v, out uint lsf) ? lsf.ToString() : "-";
            string lapFrames = m_LapObservations.TryFrames(v, out uint lf) ? lf.ToString() : "-";
            string lapDistance = m_LapObservations.TryDistance(v, out float ld) && ld >= 0f ? (ld / 1000f).ToString("F2") + "km" : "-";
            string prepStart = m_VehicleView.TryGetPreparing(v, out uint psf) ? psf.ToString() : "-";
            string idleStart = m_VehicleView.TryGetIdle(v, out uint isf) ? isf.ToString() : "-";

            AddDebugItem(list, "车辆", "Vehicle", v.Index.ToString());
            AddDebugItem(list, "状态", "State", state);
            AddDebugItem(list, "线路", "Line", lineStr);
            AddDebugItem(list, "目标班次", "Target Slot", targetStr);
            AddDebugItem(list, "当前班次", "Current Slot", currentStr);
            AddDebugItem(list, "缓存路点", "Cached Waypoint", cachedWp);
            AddDebugItem(list, "回流标签", "Nearing Terminus", tagged);
            AddDebugItem(list, "发车冷却", "Launch Cooldown", cooldown);
            AddDebugItem(list, "BV异常", "BV Misfire", misfire);
            AddDebugItem(list, "圈起点帧", "Lap Start Frame", lapStartFrame);
            AddDebugItem(list, "本圈帧数", "Lap Frames", lapFrames);
            AddDebugItem(list, "本圈距离", "Lap Distance", lapDistance);
            AddDebugItem(list, "出库起始帧", "Preparing Start Frame", prepStart);
            AddDebugItem(list, "闲置起始帧", "Idle Start Frame", idleStart);
        }

        private void FillLineDebugInfo(Entity line, InfoList list)
        {
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine
                ? m_DispatchScheduler.NextManagedTarget(line, nowMin)
                : m_DispatchScheduler.NextSlotMin(nowMin);
            float lapCacheFrames = ReadLineLapCache(line);
            float dispatchCacheFrames = ReadDispatchCache(line);
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string spawning = m_SpawningLines.TryGetValue(line, out int spawnTarget) ? spawnTarget.ToString() : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int tagged = 0;
            int total = 0;
            var rvBuffers = GetBufferLookup<Game.Routes.RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(v))
                        continue;

                    total++;
                    if (m_VehicleView.IsInbound(v))
                        tagged++;
                    if (!m_VehicleView.TryGetState(v, out var st))
                        continue;

                    switch (st)
                    {
                        case VehicleState.Preparing: preparing++; break;
                        case VehicleState.Holding: holding++; break;
                        case VehicleState.Running: running++; break;
                        case VehicleState.Idle: idle++; break;
                        case VehicleState.Retiring: retiring++; break;
                    }
                }
            }

            AddDebugItem(list, "线路", "Line", line.Index.ToString());
            AddDebugItem(list, "时间", "Time", SlotStr(nowMin));
            AddDebugItem(list, isManagedLine ? LocalizedNextSlotLabel() : "下一班次", "Next Slot", SlotStr(nextSlot));
            AddDebugItem(list, "总车数", "Total Vehicles", total.ToString());
            AddDebugItem(list, "预备数", "Preparing Count", preparing.ToString());
            AddDebugItem(list, "候车数", "Holding Count", holding.ToString());
            AddDebugItem(list, "运行数", "Running Count", running.ToString());
            AddDebugItem(list, "待调度数", "Idle Count", idle.ToString());
            AddDebugItem(list, "回库数", "Retiring Count", retiring.ToString());
            AddDebugItem(list, "回流标签数", "Nearing Terminus Count", tagged.ToString());
            AddDebugItem(list, "产车目标", "Spawn Target", spawning);
            AddDebugItem(list, "圈时缓存", "Lap Cache", lapCache);
            AddDebugItem(list, "出库缓存", "Dispatch Cache", dispatchCache);
        }
    }
}
