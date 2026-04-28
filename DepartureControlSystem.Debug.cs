using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private void SetUILabel(Entity v, string msg)
        {
            var fs = new FixedString64Bytes(msg);
            if (!m_UICache.TryGetValue(v, out var cached) || cached != fs)
            {
                m_NameSystem.SetCustomName(v, msg);
                m_UICache[v] = fs;
            }
        }

        private void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (vehicle == Entity.Null)
            {
                log.Info(message);
                return;
            }

            if (cache.TryGetValue(vehicle, out string previous) && previous == key)
                return;

            cache[vehicle] = key;
            log.Info(message);
        }

        private bool ShouldEmitVehicleLogWithCooldown(
            Dictionary<Entity, string> keyCache,
            Dictionary<Entity, uint> lastLogFrameCache,
            Entity vehicle,
            string key,
            uint nowFrame,
            uint cooldownFrames)
        {
            if (vehicle == Entity.Null)
                return true;

            bool keyChanged = !keyCache.TryGetValue(vehicle, out string previousKey) || previousKey != key;
            if (keyChanged)
            {
                keyCache[vehicle] = key;
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            if (!lastLogFrameCache.TryGetValue(vehicle, out uint lastLogFrame)
                || nowFrame >= lastLogFrame + cooldownFrames)
            {
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            return false;
        }

        private void ObserveBvMisfireCandidate(
            Entity vehicle,
            string lineTag,
            string phase,
            string detail,
            uint nowFrame)
        {
            LogVehicleStateOnce(
                m_BvMisfireObserveLogCache,
                vehicle,
                phase + "|" + detail,
                "[BVObserve] " + lineTag + " 车辆" + vehicle.Index
                    + " phase=" + phase
                    + " detail=" + detail
                    + " enforcement=" + (IsBvMisfireEnforcementEnabled() ? "on" : "off")
                    + " frame=" + nowFrame);

            if (IsBvMisfireEnforcementEnabled())
            {
                m_BVMisfire.Add(vehicle);
                m_BVMisfireStartFrame[vehicle] = nowFrame;
            }
            else
            {
                m_BVMisfire.Remove(vehicle);
                m_BVMisfireStartFrame.Remove(vehicle);
            }
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
            if (m_NearingTerminus.Contains(vehicle))
                alerts = AppendAlert(alerts, "nearing-terminus");
            if (m_LaunchCooldownUntil.TryGetValue(vehicle, out uint cooldownUntil) && m_SimulationSystem.frameIndex < cooldownUntil)
                alerts = AppendAlert(alerts, "launch-cooldown");
            if (targetMin >= 0 && IsSlotExpired(nowMin, targetMin))
                alerts = AppendAlert(alerts, "target-expired");
            if (line != Entity.Null
                && m_VehicleState.TryGetValue(vehicle, out var state)
                && state == VehicleState.Idle
                && ShouldProtectIdleFromYield(line, vehicle, nowMin))
                alerts = AppendAlert(alerts, "yield-protected");
            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleProgressSummary(Entity vehicle)
        {
            string cachedWaypoint = m_CachedWpIdx.TryGetValue(vehicle, out int waypointIndex) ? waypointIndex.ToString() : "-";
            string lapDistance = m_VehicleLapDistance.TryGetValue(vehicle, out float distance) && distance >= 0f
                ? (distance / 1000f).ToString("F2") + " km"
                : "-";
            return "wp " + cachedWaypoint + " / lap " + lapDistance;
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
                var routeWaypoints = GetBufferLookup<RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = EstimatePreparingArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames);
            }
            else if (vehicleState == VehicleState.Running)
            {
                var routeWaypoints = GetBufferLookup<RouteWaypoint>(true);
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
            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out uint dwellSinceFrame))
                return "-";

            uint elapsedFrames = m_SimulationSystem.frameIndex > dwellSinceFrame
                ? m_SimulationSystem.frameIndex - dwellSinceFrame
                : 0u;
            return (elapsedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min";
        }

        private string BuildVehicleInboundTimeValue(Entity vehicle)
        {
            if (m_VehiclePreparingStartFrame.TryGetValue(vehicle, out uint prepStartFrame))
                return SlotStr((int)(prepStartFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440);

            if (m_OriginArrivalCandidateSinceFrame.TryGetValue(vehicle, out uint originSinceFrame))
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
            string state = m_VehicleState.TryGetValue(v, out var st) ? st.ToString() : "Unknown";
            string lineStr = m_VehicleLine.TryGetValue(v, out Entity line) ? line.Index.ToString() : "-";
            string targetStr = m_VehicleTargetMin.TryGetValue(v, out int targetMin) && targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(v, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string cachedWp = m_CachedWpIdx.TryGetValue(v, out int wp) ? wp.ToString() : "-";
            string tagged = BoolDebugStr(m_NearingTerminus.Contains(v));
            string cooldown = BoolDebugStr(m_LaunchCooldownUntil.TryGetValue(v, out uint cd) && m_SimulationSystem.frameIndex < cd);
            string misfire = BoolDebugStr(m_BVMisfire.Contains(v));
            string lapStartFrame = m_VehicleLapStartFrame.TryGetValue(v, out uint lsf) ? lsf.ToString() : "-";
            string lapFrames = m_VehicleLapFrames.TryGetValue(v, out uint lf) ? lf.ToString() : "-";
            string lapDistance = m_VehicleLapDistance.TryGetValue(v, out float ld) && ld >= 0f ? (ld / 1000f).ToString("F2") + "km" : "-";
            string prepStart = m_VehiclePreparingStartFrame.TryGetValue(v, out uint psf) ? psf.ToString() : "-";
            string idleStart = m_VehicleIdleStartFrame.TryGetValue(v, out uint isf) ? isf.ToString() : "-";

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
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : NextSlotMin(nowMin);
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
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(v)) continue;
                    total++;
                    if (m_NearingTerminus.Contains(v)) tagged++;
                    if (!m_VehicleState.TryGetValue(v, out var st)) continue;
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
