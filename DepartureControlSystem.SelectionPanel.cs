using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        public struct SelectedPanelSnapshot
        {
            public string Mode;
            public string EntityId;
            public string PrimaryLabelKey;
            public string PrimaryValue;
            public string PrimaryValueKind;
            public string Detail1LabelKey;
            public string Detail1Value;
            public string Detail2LabelKey;
            public string Detail2Value;
            public string Detail3LabelKey;
            public string Detail3Value;
            public string Detail4LabelKey;
            public string Detail4Value;
            public string Detail5LabelKey;
            public string Detail5Value;
            public string Detail6LabelKey;
            public string Detail6Value;
            public string Detail7LabelKey;
            public string Detail7Value;
            public string Detail8LabelKey;
            public string Detail8Value;
            public string AlertText;
            public bool ShowRetireAction;
            public bool ShowForceDepartAction;
            public bool ShowReevaluateAction;
            public bool ShowLineSpawnAction;
            public bool ShowDumpTrackModelAction;
            public bool ShowDumpPlannerInputAction;
            public bool ShowDumpRuntimeObservationAction;
            public bool ShowBypassStationToggle;
            public bool BypassStationChecked;
        }

        private ulong m_PanelDataVersion = 1;
        private uint m_LastPanelVersionBucket;
        private const uint PANEL_VERSION_REFRESH_FRAMES = 30;

        public void FillDebugInfo(Entity entity, InfoList list)
        {
            if (entity == Entity.Null) return;
            if (m_VehicleState.ContainsKey(entity))
            {
                FillVehicleDebugInfo(entity, list);
                return;
            }
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity))
                FillLineDebugInfo(entity, list);
        }

        public bool ShouldDisplaySelectedLineInfo(Entity entity, Entity preferredRoute = default)
        {
            return ResolveSelectedLineEntity(entity, preferredRoute) != Entity.Null;
        }

        public bool ShouldDisplaySelectedVehicleInfo(Entity entity)
        {
            return ResolveSelectedVehicleEntity(entity) != Entity.Null;
        }

        public bool IsManagedVehicle(Entity entity)
        {
            Entity resolvedVehicle = ResolveSelectedVehicleEntity(entity);
            return resolvedVehicle != Entity.Null && m_VehicleState.ContainsKey(resolvedVehicle);
        }

        public bool CanConfigureBypassStation(Entity entity)
        {
            return ResolvePassingStationBuilding(entity) != Entity.Null;
        }

        public bool IsBypassStation(Entity entity)
        {
            Entity building = ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            EnsureBypassStationBuffer();
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BypassStationSettingElement>(city))
                return false;

            DynamicBuffer<BypassStationSettingElement> buf = EntityManager.GetBuffer<BypassStationSettingElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_BuildingEntity == building)
                    return buf[i].m_IsBypassStation != 0;
            }

            return false;
        }

        public bool RequestSetBypassStation(Entity entity, bool enabled)
        {
            Entity building = ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            EnsureBypassStationBuffer();
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BypassStationSettingElement>(city))
                return false;

            DynamicBuffer<BypassStationSettingElement> buf = EntityManager.GetBuffer<BypassStationSettingElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_BuildingEntity != building)
                    continue;

                buf[i] = new BypassStationSettingElement
                {
                    m_BuildingEntity = building,
                    m_IsBypassStation = enabled ? (byte)1 : (byte)0
                };
                InvalidatePanelData();
                return true;
            }

            buf.Add(new BypassStationSettingElement
            {
                m_BuildingEntity = building,
                m_IsBypassStation = enabled ? (byte)1 : (byte)0
            });
            InvalidatePanelData();
            return true;
        }

        public ulong PanelDataVersion => m_PanelDataVersion;

        private void UpdatePanelDataVersionBucket()
        {
            uint bucket = m_SimulationSystem.frameIndex / PANEL_VERSION_REFRESH_FRAMES;
            if (bucket == m_LastPanelVersionBucket)
                return;

            m_LastPanelVersionBucket = bucket;
            m_PanelDataVersion++;
        }

        private void InvalidatePanelData()
        {
            m_PanelDataVersion++;
        }

        private static string DescribeNativeVehicleState(PublicTransportFlags flags)
        {
            if ((flags & PublicTransportFlags.Disabled) != 0)
                return "Disabled";
            if ((flags & PublicTransportFlags.Returning) != 0)
                return "Returning";
            if ((flags & PublicTransportFlags.Arriving) != 0)
                return "Arriving";
            if ((flags & PublicTransportFlags.Boarding) != 0)
                return "Boarding";
            if ((flags & PublicTransportFlags.EnRoute) != 0)
                return "EnRoute";
            if ((flags & PublicTransportFlags.Launched) != 0)
                return "Launched";
            return "Assigned";
        }

        public void FillSelectedLineSummary(Entity line, out string summaryLabel, out string summaryValue)
        {
            if (!IsWorkbenchTimetableApplied(line))
            {
                summaryLabel = LocalizedDispatchLabel();
                summaryValue = LocalizedOfficialDispatchValue();
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            summaryLabel = LocalizedNextSlotLabel();
            int nextTarget = GetNextManagedDispatchTarget(line, nowMin);
            summaryValue = nextTarget >= 0 ? SlotStr(nextTarget) : "-";
        }

        public void FillSelectedVehicleSummary(Entity vehicle, out string summaryLabel, out string summaryValue)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            summaryLabel = "State";
            summaryValue = m_VehicleState.TryGetValue(vehicle, out var vehicleState) ? vehicleState.ToString() : "Unknown";
        }

        public void FillSelectedLineInfo(Entity line, InfoList list)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null)
                return;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int nearingTerminus = 0;
            int targetingNextSlot = 0;
            int occupyingNextSlot = 0;
            int total = 0;
            int spawning = 0;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var routeVehicles))
            {
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity vehicle = routeVehicles[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_NearingTerminus.Contains(vehicle))
                        nearingTerminus++;
                    if (m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) && targetSlot == nextSlot)
                        targetingNextSlot++;
                    if (m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot == nextSlot)
                        occupyingNextSlot++;

                    if (!m_VehicleState.TryGetValue(vehicle, out var state))
                        continue;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            preparing++;
                            break;
                        case VehicleState.Holding:
                            holding++;
                            break;
                        case VehicleState.Running:
                            running++;
                            break;
                        case VehicleState.Idle:
                            idle++;
                            break;
                        case VehicleState.Retiring:
                            retiring++;
                            break;
                    }
                }
            }

            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawning);
            string spawnTarget = isManagedLine ? spawning.ToString() : "-";
            string slotCoverage = isManagedLine
                ? ((targetingNextSlot + occupyingNextSlot) > 0 ? "Occupied" : "Gap")
                : LocalizedOfficialDispatchValue();
            string spawnTriggerSummary = m_LineLastSpawnTriggerSummary.TryGetValue(line, out string spawnTriggerText) ? spawnTriggerText : "-";
            string registerSummary = m_LineLastVehicleRegisterSummary.TryGetValue(line, out string registerText) ? registerText : "-";
            string holdingSummary = m_LineLastHoldingSummary.TryGetValue(line, out string holdingText) ? holdingText : "-";
            string dispatchSampleSummary = m_LineLastDispatchSampleSummary.TryGetValue(line, out string dispatchSampleText) ? dispatchSampleText : "-";
            string anomalies = BuildLineAlertSummary(
                line,
                isManagedLine ? (targetingNextSlot + occupyingNextSlot) : 0,
                nearingTerminus,
                lapCacheFrames,
                dispatchCacheFrames,
                spawning);

            AddDebugItem(list, "线路", "Line", line.Index.ToString());
            AddDebugItem(list, "当前时间", "Time", SlotStr(nowMin));
            if (isManagedLine)
                AddDebugItem(list, LocalizedNextSlotLabel(), "Next Slot", SlotStr(nextSlot));
            else
                AddDebugItem(list, LocalizedDispatchLabel(), "Dispatch", LocalizedOfficialDispatchValue());
            AddDebugItem(list, "车辆概览", "Fleet", total + " total / " + running + " running / " + holding + " holding");
            AddDebugItem(list, "状态分布", "States", "prep " + preparing + " / idle " + idle + " / retire " + retiring);
            if (isManagedLine)
                AddDebugItem(list, LocalizedNextSlotCoverageLabel(), "Next Slot Coverage", slotCoverage + " (" + targetingNextSlot + " target / " + occupyingNextSlot + " active)");
            else
                AddDebugItem(list, LocalizedNextSlotCoverageLabel(), "Next Slot Coverage", slotCoverage);
            AddDebugItem(list, "产车目标", "Spawn Target", spawnTarget);
            AddDebugItem(list, "圈时缓存", "Lap Cache", lapCache);
            AddDebugItem(list, LocalizedDispatchCacheLabel(), "Dispatch Cache", dispatchCache);
            AddDebugItem(list, "真实产车命令", "Spawn Command", spawnTriggerSummary);
            AddDebugItem(list, "新车注册", "Vehicle Register", registerSummary);
            AddDebugItem(list, "到站候车", "Arrival Holding", holdingSummary);
            AddDebugItem(list, "出库用时", "Dispatch Sample", dispatchSampleSummary);
            AddDebugItem(list, "关键异常", "Alerts", anomalies);
        }

        public void FillSelectedLineCard(
            Entity line,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null)
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Line: -";
                meta2 = "Selection is not a transport line";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            int spawnPending = 0;
            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawnPending);
            bool hasWaypointData = EntityManager.HasBuffer<RouteWaypoint>(line);

            summaryLabel = isManagedLine ? LocalizedNextSlotLabel() : LocalizedDispatchLabel();
            summaryValue = isManagedLine ? SlotStr(nextSlot) : LocalizedOfficialDispatchValue();
            meta1 = "Time: " + SlotStr(nowMin);
            meta2 = isManagedLine
                ? "Lap: " + lapCache + " / Managed: " + BoolDebugStr(isManagedLine)
                : "Lap: - / Managed: " + BoolDebugStr(false);
            meta3 = isManagedLine
                ? (hasWaypointData
                    ? (IsChineseLocale() ? "出库：" : "Dispatch: ") + dispatchCache
                    : (IsChineseLocale() ? "出库：- / 路点缺失" : "Dispatch: - / Waypoints missing"))
                : (IsChineseLocale() ? "发车：官方调度" : "Dispatch: official dispatch");
            alertText = BuildLineAlertSummary(
                line,
                isManagedLine && spawnPending > 0 ? 1 : 0,
                0,
                lapCacheFrames,
                dispatchCacheFrames,
                spawnPending);
        }

        public void FillSelectedVehicleInfo(Entity vehicle, InfoList list)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
                return;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            string state = m_VehicleState.TryGetValue(vehicle, out var vehicleState)
                ? GetVehiclePanelStateCode(vehicle, vehicleState)
                : "Unknown";
            Entity line = ResolveVehicleLine(vehicle);
            string lineStr = line != Entity.Null ? line.Index.ToString() : "-";
            string targetStr = m_VehicleTargetMin.TryGetValue(vehicle, out int targetMin) && targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string progress = BuildVehicleProgressSummary(vehicle);
            string eta = EstimateVehicleEtaText(vehicle, line, vehicleState);
            string alerts = BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin);

            AddDebugItem(list, "车辆", "Vehicle", vehicle.Index.ToString());
            AddDebugItem(list, "状态", "State", state);
            AddDebugItem(list, "所属线路", "Line", lineStr);
            AddDebugItem(list, "目标班次", "Target Slot", targetStr);
            AddDebugItem(list, "当前班次", "Current Slot", currentStr);
            AddDebugItem(list, "到始发ETA", "ETA To Origin", eta);
            AddDebugItem(list, "运行进度", "Progress", progress);
            AddDebugItem(list, "关键异常", "Alerts", alerts);
            AddDebugItem(list, "可用控制", "Controls", "Retire and Re-evaluate are wired in backend");
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, out SelectedPanelSnapshot snapshot)
        {
            return TryBuildSelectedLineSnapshot(line, Entity.Null, out snapshot);
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, Entity preferredRoute, out SelectedPanelSnapshot snapshot)
        {
            snapshot = default;
            Entity selectedEntity = line;
            line = ResolveSelectedLineEntity(line, preferredRoute);
            if (line == Entity.Null)
                return false;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            if (isManagedLine)
                LogAppliedWorkbenchLineState(line, nowMin, nextSlot);
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            int total = 0;
            int running = 0;
            int holding = 0;
            int idle = 0;
            int retiring = 0;
            int nextSlotOccupancy = 0;
            int nearingTerminus = 0;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var routeVehicles))
            {
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity vehicle = routeVehicles[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_NearingTerminus.Contains(vehicle))
                        nearingTerminus++;
                    if (isManagedLine && m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) && targetSlot == nextSlot)
                        nextSlotOccupancy++;
                    if (isManagedLine && m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot == nextSlot)
                        nextSlotOccupancy++;

                    if (!m_VehicleState.TryGetValue(vehicle, out var state))
                        continue;

                    switch (state)
                    {
                        case VehicleState.Running:
                            running++;
                            break;
                        case VehicleState.Holding:
                            holding++;
                            break;
                        case VehicleState.Idle:
                            idle++;
                            break;
                        case VehicleState.Retiring:
                            retiring++;
                            break;
                    }
                }
            }

            int spawnPending = 0;
            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawnPending);
            string spawnTriggerSummary = m_LineLastSpawnTriggerSummary.TryGetValue(line, out string spawnTriggerText) ? spawnTriggerText : "-";
            string registerSummary = m_LineLastVehicleRegisterSummary.TryGetValue(line, out string registerText) ? registerText : "-";
            string holdingSummary = m_LineLastHoldingSummary.TryGetValue(line, out string holdingText) ? holdingText : "-";
            string dispatchSampleSummary = m_LineLastDispatchSampleSummary.TryGetValue(line, out string dispatchSampleText) ? dispatchSampleText : "-";
            snapshot.Mode = "line";
            snapshot.EntityId = line.Index.ToString();
            snapshot.PrimaryLabelKey = isManagedLine ? "nextSlot" : "dispatch";
            snapshot.PrimaryValue = isManagedLine ? SlotStr(nextSlot) : LocalizedOfficialDispatchValue();
            snapshot.PrimaryValueKind = isManagedLine ? "slot" : "text";
            snapshot.Detail1LabelKey = IsChineseLocale() ? "线路编号" : "Line ID";
            snapshot.Detail1Value = line.Index.ToString();
            snapshot.Detail2LabelKey = IsChineseLocale() ? "当前时间" : "Time";
            snapshot.Detail2Value = SlotStr(nowMin);
            snapshot.Detail3LabelKey = IsChineseLocale() ? "真实产车命令" : "Spawn Command";
            snapshot.Detail3Value = spawnTriggerSummary;
            snapshot.Detail4LabelKey = IsChineseLocale() ? "新车注册" : "Vehicle Register";
            snapshot.Detail4Value = registerSummary;
            snapshot.Detail5LabelKey = IsChineseLocale() ? "到站候车" : "Arrival Holding";
            snapshot.Detail5Value = holdingSummary;
            snapshot.Detail6LabelKey = IsChineseLocale() ? "出库用时" : "Dispatch Sample";
            snapshot.Detail6Value = dispatchSampleSummary;
            snapshot.Detail7LabelKey = IsChineseLocale() ? "车辆概览" : "Fleet";
            snapshot.Detail7Value = total + " / " + running + " / " + holding;
            snapshot.AlertText = BuildLineAlertSummary(
                line,
                nextSlotOccupancy,
                nearingTerminus,
                lapCacheFrames,
                dispatchCacheFrames,
                spawnPending);
            snapshot.ShowLineSpawnAction = isManagedLine;
            snapshot.ShowDumpTrackModelAction = true;
            snapshot.ShowDumpPlannerInputAction = true;
            snapshot.ShowDumpRuntimeObservationAction = true;
            snapshot.ShowBypassStationToggle = CanConfigureBypassStation(selectedEntity);
            snapshot.BypassStationChecked = snapshot.ShowBypassStationToggle && IsBypassStation(selectedEntity);
            return true;
        }

        public bool TryBuildSelectedVehicleSnapshot(Entity vehicle, out SelectedPanelSnapshot snapshot)
        {
            snapshot = default;
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
                return false;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedVehicle = m_VehicleState.TryGetValue(vehicle, out var vehicleState);
            PublicTransportFlags nativeFlags = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                : 0;
            Entity line = ResolveVehicleLine(vehicle);
            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) ? targetSlot : -1;
            int currentMin = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) ? currentSlot : -1;
            string state = isManagedVehicle ? GetVehiclePanelStateCode(vehicle, vehicleState) : DescribeNativeVehicleState(nativeFlags);
            string alertText = isManagedVehicle
                ? BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin)
                : (line != Entity.Null ? "using-native-fallback" : "vehicle-not-tracked");

            snapshot.Mode = "vehicle";
            snapshot.EntityId = vehicle.Index.ToString();
            snapshot.PrimaryLabelKey = "state";
            snapshot.PrimaryValue = state;
            snapshot.PrimaryValueKind = "state";
            snapshot.Detail1LabelKey = "line";
            snapshot.Detail1Value = line != Entity.Null ? line.Index.ToString() : "-";
            snapshot.Detail2LabelKey = "progress";
            snapshot.Detail2Value = BuildVehicleTraversalProgressValue(vehicle);
            snapshot.Detail3LabelKey = "currentSlot";
            snapshot.Detail3Value = currentMin >= 0 ? SlotStr(currentMin) : "-";
            snapshot.Detail4LabelKey = "targetSlot";
            snapshot.Detail4Value = targetMin >= 0 ? SlotStr(targetMin) : "-";
            snapshot.Detail5LabelKey = "stopDwell";
            snapshot.Detail5Value = BuildVehicleStopDwellValue(vehicle);
            TryGetBroadcastPanelStationContext(
                vehicle,
                line,
                out string currentStationName,
                out string nextStationName,
                out _);
            snapshot.Detail6LabelKey = "currentStation";
            snapshot.Detail6Value = string.IsNullOrEmpty(currentStationName) ? "-" : currentStationName;
            snapshot.Detail7LabelKey = "nextStation";
            snapshot.Detail7Value = string.IsNullOrEmpty(nextStationName) ? "-" : nextStationName;
            snapshot.Detail8LabelKey = "event";
            snapshot.Detail8Value = BuildVehicleBroadcastEventValue(vehicle);
            snapshot.AlertText = alertText;
            snapshot.ShowRetireAction = isManagedVehicle;
            snapshot.ShowForceDepartAction = isManagedVehicle;
            snapshot.ShowReevaluateAction = false;
            snapshot.ShowDumpTrackModelAction = false;
            snapshot.ShowDumpPlannerInputAction = false;
            snapshot.ShowDumpRuntimeObservationAction = false;
            return true;
        }

        public void FillSelectedVehicleCard(
            Entity vehicle,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Vehicle: -";
                meta2 = "Selection is not a public transport vehicle";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedVehicle = m_VehicleState.TryGetValue(vehicle, out var vehicleState);
            PublicTransportFlags nativeFlags = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                : 0;
            string state = isManagedVehicle ? GetVehiclePanelStateCode(vehicle, vehicleState) : DescribeNativeVehicleState(nativeFlags);
            Entity line = ResolveVehicleLine(vehicle);
            string lineStr = line != Entity.Null ? "#" + line.Index : "-";
            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) ? targetSlot : -1;
            string targetStr = targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string stopDwell = BuildVehicleStopDwellValue(vehicle);
            string inboundTime = BuildVehicleInboundTimeValue(vehicle);

            summaryLabel = "State";
            summaryValue = state;
            meta1 = "Line: " + lineStr + " / Managed: " + BoolDebugStr(isManagedVehicle);
            meta2 = isManagedVehicle
                ? "Slot: " + currentStr + " -> " + targetStr
                : "Native: " + DescribeNativeVehicleState(nativeFlags);
            meta3 = IsChineseLocale()
                ? "停站计时：" + stopDwell + " / 入站时间：" + inboundTime
                : "Stop dwell: " + stopDwell + " / Inbound: " + inboundTime;
            alertText = isManagedVehicle
                ? BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin)
                : (line != Entity.Null ? "Using native route fallback" : "Vehicle is not currently tracked by RapidTransit");
        }

        public bool RequestVehicleRetire(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);

            DoRetire(
                vehicle,
                publicTransport,
                target,
                m_EndFrameBarrier.CreateCommandBuffer(),
                "UI请求");
            InvalidatePanelData();
            return true;
        }

        public bool RequestVehicleReevaluate(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;

            m_VehicleTargetMin[vehicle] = -1;
            m_VehiclePreparingStartFrame.Remove(vehicle);
            m_VehicleDispatchRequestStartFrame.Remove(vehicle);
            m_VehicleIdleStartFrame.Remove(vehicle);
            InvalidatePanelData();
            return true;
        }

        public bool RequestVehicleForceDepart(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Entity line = ResolveVehicleLine(vehicle);
            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            Game.Vehicles.PublicTransport pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            if ((pt.m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            Target tgt = EntityManager.GetComponentData<Target>(vehicle);
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            int currentWaypointIndex = ComputeWpIndex(vehicle, wps);
            if (currentWaypointIndex < 0)
                currentWaypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                    ? cachedWaypointIndex
                    : -1;

            int scannedPassengers = 0;
            int readiedPassengers = 0;
            BoardingCloseAssistStats assistStats = default;
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            PrepareVehicleForOriginalBoardingClose(
                vehicle,
                ref pt,
                commandBuffer,
                out scannedPassengers,
                out readiedPassengers,
                out assistStats);
            commandBuffer.SetComponent(vehicle, pt);
            ClearBypassYieldState(vehicle, "UI强制发车");
            SetUILabel(vehicle, "结束上客");
            log.Info("[强制发车协助] 线路" + line.Index + " 车辆" + vehicle.Index
                + " scannedPassengers=" + scannedPassengers
                + " readiedPassengers=" + readiedPassengers
                + " " + FormatBoardingCloseAssistStats(assistStats)
                + " wp=" + currentWaypointIndex);
            InvalidatePanelData();
            return true;
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

        private string BuildVehicleTraversalSamplingBandValue(Entity vehicle, Entity line)
        {
            if (!TryBuildVehicleTraversalSamplingDisplay(vehicle, line, out TraversalSliceSamplingPlan plan, out _, out _, out _))
                return "-";

            if (plan.IsHighSampling)
                return IsChineseLocale() ? "高" : "high";
            if (plan.IsMediumSampling)
                return IsChineseLocale() ? "中" : "medium";
            return IsChineseLocale() ? "低" : "low";
        }

        private string BuildVehicleTraversalSamplingRateValue(Entity vehicle, Entity line)
        {
            if (!TryBuildVehicleTraversalSamplingDisplay(vehicle, line, out TraversalSliceSamplingPlan plan, out _, out _, out _))
                return "-";

            if (plan.SampleIntervalFrames <= 1)
                return IsChineseLocale() ? "每帧" : "every frame";

            return IsChineseLocale()
                ? ("每" + plan.SampleIntervalFrames + "帧")
                : ("every " + plan.SampleIntervalFrames + "f");
        }

        private string BuildVehicleTraversalNextCutPointValue(Entity vehicle, Entity line)
        {
            if (!TryBuildVehicleTraversalSamplingDisplay(vehicle, line, out TraversalSliceSamplingPlan plan, out LineTrackChain chain, out _, out int segmentAtomStart))
                return "-";

            if (!plan.HasUpcomingCutPoint)
                return IsChineseLocale() ? "本段无后续切换点" : "none in segment";

            int cutPointPercent = (int)math.round(plan.UpcomingCutPointProgress * 100f);
            int deltaPercent = (int)math.round(plan.UpcomingCutPointDistance * 100f);
            int segmentLengthAtoms = 1;
            if (chain != null
                && plan.SegmentIndex >= 0
                && plan.SegmentIndex < chain.SegmentRanges.Count)
            {
                TrackSegmentRange segmentRange = chain.SegmentRanges[plan.SegmentIndex];
                segmentLengthAtoms = math.max(1, segmentRange.EndAtomIndexExclusive - segmentRange.StartAtomIndex);
            }
            int cutPointAtom = math.clamp(
                segmentAtomStart + (int)math.round(plan.UpcomingCutPointProgress * segmentLengthAtoms),
                segmentAtomStart,
                segmentAtomStart + math.max(0, segmentLengthAtoms - 1));
            return "seg" + plan.SegmentIndex
                + " / " + cutPointPercent + "%"
                + " / a" + cutPointAtom
                + " / +" + deltaPercent + "%";
        }

        private bool TryBuildVehicleTraversalSamplingDisplay(
            Entity vehicle,
            Entity line,
            out TraversalSliceSamplingPlan plan,
            out LineTrackChain chain,
            out DynamicBuffer<RouteWaypoint> waypoints,
            out int segmentAtomStart)
        {
            plan = default;
            chain = null;
            waypoints = default;
            segmentAtomStart = 0;

            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (vehicle == Entity.Null || line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0
                || !TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, out plan)
                || !m_LineTrackChains.TryGetValue(line, out chain)
                || chain == null
                || plan.SegmentIndex < 0
                || plan.SegmentIndex >= chain.SegmentRanges.Count)
            {
                return false;
            }

            segmentAtomStart = chain.SegmentRanges[plan.SegmentIndex].StartAtomIndex;
            return true;
        }

        public bool RequestSpawnForLine(Entity line)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null || !EntityManager.Exists(line) || !IsWorkbenchTimetableApplied(line))
                return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            int actualCount = CountActiveVehicles(line, rvBuffers);
            int pendingTarget = actualCount;
            if (m_SpawningLines.TryGetValue(line, out int existingTarget))
            {
                pendingTarget = math.max(existingTarget, actualCount);
            }

            int nextTarget = pendingTarget + 1;
            m_SpawningLines[line] = nextTarget;
            m_LineSpawnRequestFrame[line] = m_SimulationSystem.frameIndex;
            m_LineLastSpawnTriggerSummary[line] = SlotStr((int)(m_TimeSystem.normalizedTime * 1440f) % 1440)
                + " 手动发车 -> "
                + nextTarget.ToString();
            log.Info("[面板发车] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ", 目标=" + nextTarget + ")");
            return true;
        }

        private string GetVehiclePanelStateCode(Entity vehicle, VehicleState vehicleState)
        {
            if (m_BypassYieldBlocker.ContainsKey(vehicle)
                && (vehicleState == VehicleState.Holding || vehicleState == VehicleState.Running))
            {
                return "Yielding";
            }

            if (vehicleState == VehicleState.Holding
                && (!m_VehicleTargetMin.TryGetValue(vehicle, out int holdingTarget) || holdingTarget < 0))
            {
                return "Idle";
            }

            return vehicleState.ToString();
        }
    }
}
