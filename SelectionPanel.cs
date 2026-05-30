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
    internal sealed class SelectionPanel
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary = new Dictionary<Entity, string>();

        public SelectionPanel(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public string CurrentGameTimeLabel()
        {
            if (m_Runtime.m_TimeSystem == null)
                return string.Empty;

            int nowMin = (int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440;
            if (nowMin < 0)
                nowMin += 1440;

            return "[游戏时间 " + DispatchRuntimeSystem.SlotStr(nowMin) + "]";
        }

        public void ClearDebugSummaries()
        {
            m_LineLastSpawnTriggerSummary.Clear();
            m_LineLastVehicleRegisterSummary.Clear();
            m_LineLastHoldingSummary.Clear();
            m_LineLastDispatchSampleSummary.Clear();
        }

        public void RecordLineSpawnTriggerSummary(Entity line, int nowMin, int slot, int actualCount)
        {
            if (line == Entity.Null)
                return;

            m_LineLastSpawnTriggerSummary[line] = DispatchRuntimeSystem.SlotStr(nowMin)
                + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                + " 真实产车命令 当前=" + actualCount;
        }

        public void RecordLineVehicleRegisterSummary(Entity line, int nowMin, Entity vehicle, VehicleState finalState)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            string depotSummary = DescribeVehicleOwnerDepot(vehicle);
            m_LineLastVehicleRegisterSummary[line] = DispatchRuntimeSystem.SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 注册 -> " + finalState
                + " depot=" + depotSummary;
        }

        public void RecordLineHoldingSummary(Entity line, int nowMin, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            m_LineLastHoldingSummary[line] = DispatchRuntimeSystem.SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 到站/Holding"
                + (targetMin >= 0 ? " " + DispatchRuntimeSystem.SlotStr(targetMin) : " 等待调度");
        }

        public void RecordLineDispatchSampleSummary(Entity line, int nowMin, Entity vehicle, float sampleMinutes)
        {
            if (line == Entity.Null || vehicle == Entity.Null || sampleMinutes <= 0f)
                return;

            m_LineLastDispatchSampleSummary[line] = DispatchRuntimeSystem.SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 出库用时=" + sampleMinutes.ToString("F1") + "分钟";
        }

        public string DescribeVehicleOwnerDepot(Entity vehicle)
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

            string name = m_Runtime.m_NameSystem.GetRenderedLabelName(depot);
            return string.IsNullOrEmpty(name)
                ? "#" + depot.Index
                : ("#" + depot.Index + "[" + name + "]");
        }

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
            public bool ShowDumpStationAnchorObservationAction;
            public bool ShowBypassStationToggle;
            public bool BypassStationChecked;
        }

        private ulong m_PanelDataVersion = 1;
        private uint m_LastPanelVersionBucket;
        private SelectionPanelQuery m_SelectionPanelQuery;
        private SelectionPanelBuilder m_SelectionPanelBuilder;
        private const uint PANEL_VERSION_REFRESH_FRAMES = 30;

        public void FillDebugInfo(Entity entity, InfoList list)
        {
            if (entity == Entity.Null) return;
            if (m_Runtime.m_VehicleView.Contains(entity))
            {
                FillVehicleDebugInfo(entity, list);
                return;
            }
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity))
                FillLineDebugInfo(entity, list);
        }

        public bool ShouldDisplaySelectedLineInfo(Entity entity, Entity preferredRoute = default)
        {
            return m_Runtime.ResolveSelectedLineEntity(entity, preferredRoute) != Entity.Null;
        }

        public bool ShouldDisplaySelectedVehicleInfo(Entity entity)
        {
            return m_Runtime.ResolveSelectedVehicleEntity(entity) != Entity.Null;
        }

        public bool IsManagedVehicle(Entity entity)
        {
            Entity resolvedVehicle = m_Runtime.ResolveSelectedVehicleEntity(entity);
            return resolvedVehicle != Entity.Null && m_Runtime.m_VehicleView.Contains(resolvedVehicle);
        }

        public bool CanConfigureBypassStation(Entity entity)
        {
            return m_Runtime.ResolvePassingStationBuilding(entity) != Entity.Null;
        }

        public bool IsBypassStation(Entity entity)
        {
            Entity building = m_Runtime.ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            m_Runtime.EnsureBypassStationBuffer();
            Entity city = m_Runtime.m_CitySystem.City;
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
            Entity building = m_Runtime.ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            m_Runtime.EnsureBypassStationBuffer();
            Entity city = m_Runtime.m_CitySystem.City;
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
                Invalidate();
                return true;
            }

            buf.Add(new BypassStationSettingElement
            {
                m_BuildingEntity = building,
                m_IsBypassStation = enabled ? (byte)1 : (byte)0
            });
            Invalidate();
            return true;
        }

        public ulong PanelDataVersion => m_PanelDataVersion;

        public void UpdateVersionBucket()
        {
            uint bucket = m_Runtime.m_SimulationSystem.frameIndex / PANEL_VERSION_REFRESH_FRAMES;
            if (bucket == m_LastPanelVersionBucket)
                return;

            m_LastPanelVersionBucket = bucket;
            m_PanelDataVersion++;
        }

        public void Invalidate()
        {
            m_PanelDataVersion++;
        }

        private SelectionPanelQuery PanelQuery()
        {
            if (m_SelectionPanelQuery == null)
            {
                m_SelectionPanelQuery = new SelectionPanelQuery(
                    EntityManager,
                    m_Runtime.m_TimeSystem,
                    m_Runtime.m_SimulationSystem,
                    m_Runtime.m_VehicleView,
                    m_Runtime.m_StopDwell,
                    m_Runtime.m_SpawningLines,
                    m_LineLastSpawnTriggerSummary,
                    m_LineLastVehicleRegisterSummary,
                    m_LineLastHoldingSummary,
                    m_LineLastDispatchSampleSummary,
                    m_Runtime.ResolveSelectedLineEntity,
                    m_Runtime.ResolveSelectedVehicleEntity,
                    m_Runtime.ResolveVehicleLine,
                    m_Runtime.IsWorkbenchTimetableApplied,
                    m_Runtime.m_DispatchScheduler.NextManagedTarget,
                    m_Runtime.LogAppliedWorkbenchLineState,
                    m_Runtime.ReadLineLapCache,
                    m_Runtime.ReadDispatchCache,
                    CanConfigureBypassStation,
                    IsBypassStation,
                    GetVehiclePanelStateCode,
                    BuildVehicleTraversalProgressValue,
                    EstimateVehicleEtaText,
                    (vehicle, line) =>
                    {
                        m_Runtime.m_Announcements.TryPanelContext(vehicle, line, out string currentStationName, out string nextStationName, out _);
                        return (currentStationName, nextStationName);
                    },
                    m_Runtime.m_Announcements.EventText,
                    BuildVehicleAlertSummary,
                    BuildLineAlertSummary,
                    DispatchRuntimeSystem.SlotStr,
                    BoolDebugStr,
                    LocalizedDispatchLabel,
                    LocalizedNextSlotLabel,
                    LocalizedNextSlotCoverageLabel,
                    LocalizedDispatchCacheLabel,
                    LocalizedOfficialDispatchValue,
                    IsChineseLocale,
                    (int)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            }

            return m_SelectionPanelQuery;
        }

        private SelectionPanelBuilder PanelBuilder()
        {
            if (m_SelectionPanelBuilder == null)
                m_SelectionPanelBuilder = new SelectionPanelBuilder();

            return m_SelectionPanelBuilder;
        }

        internal static string DescribeNativeVehicleState(PublicTransportFlags flags)
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
            if (!PanelQuery().TryLine(line, Entity.Null, out SelectionPanelLineData data))
            {
                summaryLabel = LocalizedDispatchLabel();
                summaryValue = LocalizedOfficialDispatchValue();
                return;
            }

            PanelBuilder().FillLineSummary(data, out summaryLabel, out summaryValue);
        }

        public void FillSelectedVehicleSummary(Entity vehicle, out string summaryLabel, out string summaryValue)
        {
            if (!PanelQuery().TryVehicle(vehicle, out SelectionPanelVehicleData data))
            {
                summaryLabel = "State";
                summaryValue = "Unknown";
                return;
            }

            PanelBuilder().FillVehicleSummary(data, out summaryLabel, out summaryValue);
        }

        public void FillSelectedLineInfo(Entity line, InfoList list)
        {
            if (!PanelQuery().TryLine(line, Entity.Null, out SelectionPanelLineData data))
                return;

            PanelBuilder().FillLineInfo(data, list);
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
            if (!PanelQuery().TryLine(line, Entity.Null, out SelectionPanelLineData data))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Line: -";
                meta2 = "Selection is not a transport line";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            PanelBuilder().FillLineCard(
                data,
                out summaryLabel,
                out summaryValue,
                out meta1,
                out meta2,
                out meta3,
                out alertText);
        }

        public void FillSelectedVehicleInfo(Entity vehicle, InfoList list)
        {
            if (!PanelQuery().TryVehicle(vehicle, out SelectionPanelVehicleData data))
                return;

            PanelBuilder().FillVehicleInfo(data, list);
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, out SelectedPanelSnapshot snapshot)
        {
            return TryBuildSelectedLineSnapshot(line, Entity.Null, out snapshot);
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, Entity preferredRoute, out SelectedPanelSnapshot snapshot)
        {
            if (!PanelQuery().TryLine(line, preferredRoute, out SelectionPanelLineData data))
            {
                snapshot = default;
                return false;
            }

            snapshot = PanelBuilder().BuildLineSnapshot(data);
            return true;
        }

        public bool TryBuildSelectedVehicleSnapshot(Entity vehicle, out SelectedPanelSnapshot snapshot)
        {
            if (!PanelQuery().TryVehicle(vehicle, out SelectionPanelVehicleData data))
            {
                snapshot = default;
                return false;
            }

            snapshot = PanelBuilder().BuildVehicleSnapshot(data);
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
            if (!PanelQuery().TryVehicle(vehicle, out SelectionPanelVehicleData data))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Vehicle: -";
                meta2 = "Selection is not a public transport vehicle";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            PanelBuilder().FillVehicleCard(
                data,
                out summaryLabel,
                out summaryValue,
                out meta1,
                out meta2,
                out meta3,
                out alertText);
        }

        public bool RequestVehicleRetire(Entity vehicle)
        {
            vehicle = m_Runtime.ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);

            m_Runtime.m_CommandApplier.Retire(
                vehicle,
                publicTransport,
                target,
                m_Runtime.m_EndFrameBarrier.CreateCommandBuffer(),
                "UI请求");
            Invalidate();
            return true;
        }

        public bool RequestVehicleReevaluate(Entity vehicle)
        {
            vehicle = m_Runtime.ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;

            m_Runtime.m_RuntimeController.Reevaluate(vehicle);
            Invalidate();
            return true;
        }

        public bool RequestVehicleForceDepart(Entity vehicle)
        {
            vehicle = m_Runtime.ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Entity line = m_Runtime.ResolveVehicleLine(vehicle);
            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            Game.Vehicles.PublicTransport pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            if ((pt.m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            Target tgt = EntityManager.GetComponentData<Target>(vehicle);
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            int currentWaypointIndex = m_Runtime.ComputeWpIndex(vehicle, wps);
            if (currentWaypointIndex < 0)
                currentWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                    ? cachedWaypointIndex
                    : -1;

            EntityCommandBuffer commandBuffer = m_Runtime.m_EndFrameBarrier.CreateCommandBuffer();
            m_Runtime.m_CommandApplier.ForceDepart(vehicle, ref pt, m_Runtime.m_SimulationSystem.frameIndex, commandBuffer);
            m_Runtime.Bypass.ClearVehicle(vehicle, "UI强制发车");
            m_Runtime.m_VehicleLabels.Set(vehicle, "结束上客");
            log.Info("[强制发车协助] 线路" + line.Index + " 车辆" + vehicle.Index
                + " wp=" + currentWaypointIndex);
            Invalidate();
            return true;
        }

        public bool RequestSpawnForLine(Entity line)
        {
            line = m_Runtime.ResolveSelectedLineEntity(line);
            if (line == Entity.Null || !EntityManager.Exists(line) || !m_Runtime.IsWorkbenchTimetableApplied(line))
                return false;

            var rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            int actualCount = m_Runtime.CountActiveVehicles(line, rvBuffers);
            int pendingTarget = actualCount;
            if (m_Runtime.m_SpawningLines.TryGetValue(line, out int existingTarget))
            {
                pendingTarget = math.max(existingTarget, actualCount);
            }

            int nextTarget = pendingTarget + 1;
            m_Runtime.m_SpawningLines[line] = nextTarget;
            m_Runtime.m_LineSpawnRequestFrame[line] = m_Runtime.m_SimulationSystem.frameIndex;
            m_LineLastSpawnTriggerSummary[line] = DispatchRuntimeSystem.SlotStr((int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440)
                + " 手动发车 -> "
                + nextTarget.ToString();
            log.Info("[面板发车] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ", 目标=" + nextTarget + ")");
            return true;
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
            if (!m_Runtime.IsWorkbenchTimetableApplied(line))
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
            if (line == Entity.Null || !m_Runtime.IsWorkbenchTimetableApplied(line))
                return IsChineseLocale() ? "官方调度" : "Official dispatch";

            string alerts = string.Empty;
            if (m_Runtime.Bypass.TryGetLatchedBlocker(vehicle, out Entity blockerVehicle) && blockerVehicle != Entity.Null)
                alerts = AppendAlert(alerts, "yielding-for:" + blockerVehicle.Index);
            if (m_Runtime.m_BVMisfire.Contains(vehicle))
                alerts = AppendAlert(alerts, "bv-misfire");
            if (m_Runtime.m_VehicleView.IsInbound(vehicle))
                alerts = AppendAlert(alerts, "nearing-terminus");
            if (m_Runtime.m_VehicleView.TryGetCooldown(vehicle, out uint cooldownUntil) && m_Runtime.m_SimulationSystem.frameIndex < cooldownUntil)
                alerts = AppendAlert(alerts, "launch-cooldown");
            if (targetMin >= 0 && m_Runtime.m_DispatchScheduler.IsExpired(nowMin, targetMin))
                alerts = AppendAlert(alerts, "target-expired");
            if (line != Entity.Null
                && m_Runtime.m_VehicleView.TryGetState(vehicle, out var state)
                && state == VehicleState.Idle
                && m_Runtime.m_DispatchScheduler.ShouldProtectIdle(line, vehicle, nowMin))
            {
                alerts = AppendAlert(alerts, "yield-protected");
            }

            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleTraversalProgressValue(Entity vehicle)
        {
            vehicle = m_Runtime.ResolveSelectedVehicleEntity(vehicle);
            if (vehicle == Entity.Null)
                return "-";

            if (!m_Runtime.TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return IsChineseLocale() ? "未知" : "unknown";

            int progressPercent = (int)math.round(math.saturate(segmentPosition) * 100f);
            return "wp" + nextWaypointIndex + " / " + progressPercent + "%";
        }

        private string GetVehiclePanelStateCode(Entity vehicle, VehicleState vehicleState)
        {
            if (m_Runtime.Bypass.TryGetLatchedBlocker(vehicle, out _)
                && (vehicleState == VehicleState.Holding || vehicleState == VehicleState.Running))
            {
                return "Yielding";
            }

            if (vehicleState == VehicleState.Holding
                && (!m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int holdingTarget) || holdingTarget < 0))
            {
                return "Idle";
            }

            return vehicleState.ToString();
        }

        private string EstimateVehicleEtaText(Entity vehicle, Entity line, VehicleState vehicleState)
        {
            if (line == Entity.Null || !m_Runtime.IsWorkbenchTimetableApplied(line))
                return "-";

            float lineDurationFrames = m_Runtime.ReadLineLapCache(line);
            bool lineHasHistory = lineDurationFrames > 0f;
            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            float etaFrames = float.MaxValue;

            if (vehicleState == VehicleState.Preparing)
            {
                var routeWaypoints = m_Runtime.GetBufferLookup<Game.Routes.RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = m_Runtime.EstimatePreparingArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames);
            }
            else if (vehicleState == VehicleState.Running)
            {
                var routeWaypoints = m_Runtime.GetBufferLookup<Game.Routes.RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = m_Runtime.EstimateRunningArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
            }
            else if (vehicleState == VehicleState.Holding)
            {
                etaFrames = 0f;
            }

            if (etaFrames == float.MaxValue)
                return "-";

            float etaMinutes = etaFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            return etaMinutes.ToString("F1") + " min";
        }

        private static string AppendAlert(string current, string alert)
        {
            return current.Length == 0 ? alert : current + ", " + alert;
        }

        private static string BoolDebugStr(bool value)
        {
            return value ? "是 / Yes" : "否 / No";
        }

        internal static bool IsChineseLocale()
        {
            string locale = Game.SceneFlow.GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
            return locale.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);
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

        private void FillVehicleDebugInfo(Entity vehicle, InfoList list)
        {
            string state = m_Runtime.m_VehicleView.TryGetState(vehicle, out var st) ? st.ToString() : "Unknown";
            string lineStr = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line.Index.ToString() : "-";
            string targetStr = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0 ? DispatchRuntimeSystem.SlotStr(targetMin) : "-";
            string currentStr = m_Runtime.m_VehicleView.TryGetSlot(vehicle, out int currentSlot) && currentSlot >= 0 ? DispatchRuntimeSystem.SlotStr(currentSlot) : "-";
            string cachedWp = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int wp) ? wp.ToString() : "-";
            string tagged = BoolDebugStr(m_Runtime.m_VehicleView.IsInbound(vehicle));
            string cooldown = BoolDebugStr(m_Runtime.m_VehicleView.TryGetCooldown(vehicle, out uint cd) && m_Runtime.m_SimulationSystem.frameIndex < cd);
            string misfire = BoolDebugStr(m_Runtime.m_BVMisfire.Contains(vehicle));
            string lapStartFrame = m_Runtime.m_LapObservations.TryStartFrame(vehicle, out uint lsf) ? lsf.ToString() : "-";
            string lapFrames = m_Runtime.m_LapObservations.TryFrames(vehicle, out uint lf) ? lf.ToString() : "-";
            string lapDistance = m_Runtime.m_LapObservations.TryDistance(vehicle, out float ld) && ld >= 0f ? (ld / 1000f).ToString("F2") + "km" : "-";
            string prepStart = m_Runtime.m_VehicleView.TryGetPreparing(vehicle, out uint psf) ? psf.ToString() : "-";
            string idleStart = m_Runtime.m_VehicleView.TryGetIdle(vehicle, out uint isf) ? isf.ToString() : "-";

            AddDebugItem(list, "车辆", "Vehicle", vehicle.Index.ToString());
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
            int nowMin = (int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = m_Runtime.IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine
                ? m_Runtime.m_DispatchScheduler.NextManagedTarget(line, nowMin)
                : m_Runtime.m_DispatchScheduler.NextSlotMin(nowMin);
            float lapCacheFrames = m_Runtime.ReadLineLapCache(line);
            float dispatchCacheFrames = m_Runtime.ReadDispatchCache(line);
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string spawning = m_Runtime.m_SpawningLines.TryGetValue(line, out int spawnTarget) ? spawnTarget.ToString() : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int tagged = 0;
            int total = 0;
            var rvBuffers = m_Runtime.GetBufferLookup<Game.Routes.RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity vehicle = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_Runtime.m_VehicleView.IsInbound(vehicle))
                        tagged++;
                    if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out var st))
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
            AddDebugItem(list, "时间", "Time", DispatchRuntimeSystem.SlotStr(nowMin));
            AddDebugItem(list, isManagedLine ? LocalizedNextSlotLabel() : "下一班次", "Next Slot", DispatchRuntimeSystem.SlotStr(nextSlot));
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
