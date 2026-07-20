using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.UI.InGame;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class SelectPanel
    {
        private readonly SelectPort m_Port;
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary = new Dictionary<Entity, string>();

        public SelectPanel(SelectPort port)
        {
            m_Port = port;
        }

        private EntityManager EntityManager => m_Port.EntityManager;
        private TimedLogger log => m_Port.Log;

        public string CurrentGameTimeLabel()
        {
            if (m_Port.ClockSnapshot == null)
                return string.Empty;

            return "[游戏时间 " + DispatchRuntimeSystem.SlotStr(m_Port.ClockSnapshot().NowMinute) + "]";
        }

        public void ClearDebugSummaries()
        {
            m_LineLastSpawnTriggerSummary.Clear();
            m_LineLastVehicleRegisterSummary.Clear();
            m_LineLastHoldingSummary.Clear();
            m_LineLastDispatchSampleSummary.Clear();
        }

        public void RecordLineSpawnTriggerSummary(Entity line, int nowMinute, int slot, int actualCount)
        {
            if (line == Entity.Null)
                return;

            m_LineLastSpawnTriggerSummary[line] = DispatchRuntimeSystem.SlotStr(nowMinute)
                + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                + " 真实产车命令 当前=" + actualCount;
        }

        public void RecordLineVehicleRegisterSummary(Entity line, int nowMinute, Entity vehicle, VehicleState finalState)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            string depotSummary = DescribeVehicleOwnerDepot(vehicle);
            m_LineLastVehicleRegisterSummary[line] = DispatchRuntimeSystem.SlotStr(nowMinute)
                + " 车辆" + vehicle.Index
                + " 注册 -> " + finalState
                + " depot=" + depotSummary;
        }

        public void RecordLineHoldingSummary(Entity line, int nowMinute, Entity vehicle, int targetMinute)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            m_LineLastHoldingSummary[line] = DispatchRuntimeSystem.SlotStr(nowMinute)
                + " 车辆" + vehicle.Index
                + " 到站/Holding"
                + (targetMinute >= 0 ? " " + DispatchRuntimeSystem.SlotStr(targetMinute) : " 等待调度");
        }

        public void RecordLineDispatchSampleSummary(Entity line, int nowMinute, Entity vehicle, float sampleMinutes)
        {
            if (line == Entity.Null || vehicle == Entity.Null || sampleMinutes <= 0f)
                return;

            m_LineLastDispatchSampleSummary[line] = DispatchRuntimeSystem.SlotStr(nowMinute)
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

            string name = m_Port.Names.GetRenderedLabelName(depot);
            return string.IsNullOrEmpty(name)
                ? "#" + depot.Index
                : ("#" + depot.Index + "[" + name + "]");
        }

        public struct Snapshot
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
            public bool ShowDumpObservationAction;
            public bool ShowDumpStationAnchorObservationAction;
            public bool ShowBypassStationToggle;
            public bool BypassStationChecked;
        }

        private ulong m_PanelDataVersion = 1;
        private uint m_LastPanelVersionBucket;
        private SelectQuery m_Query;
        private SelectView m_View;
        private const uint PANEL_VERSION_REFRESH_FRAMES = 30;

        public void FillDebugInfo(Entity entity, InfoList list)
        {
            if (entity == Entity.Null) return;
            if (m_Port.Vehicles.Contains(entity))
            {
                FillVehicleDebugInfo(entity, list);
                return;
            }
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity))
                FillLineDebugInfo(entity, list);
        }

        public bool CanShowLine(Entity entity, Entity preferredRoute = default)
        {
            return m_Port.ResolveLine(entity, preferredRoute) != Entity.Null;
        }

        public bool CanShowVehicle(Entity entity)
        {
            return m_Port.ResolveVehicle(entity) != Entity.Null;
        }

        public bool IsManagedVehicle(Entity entity)
        {
            Entity resolvedVehicle = m_Port.ResolveVehicle(entity);
            return resolvedVehicle != Entity.Null && m_Port.Vehicles.Contains(resolvedVehicle);
        }

        public bool CanConfigureBypassStation(Entity entity)
        {
            return m_Port.ResolveBypassBuilding(entity) != Entity.Null;
        }

        public bool IsBypassStation(Entity entity)
        {
            Entity building = m_Port.ResolveBypassBuilding(entity);
            if (building == Entity.Null)
                return false;

            m_Port.EnsureBypassBuffer();
            Entity city = m_Port.City.City;
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

        public bool SetBypass(Entity entity, bool enabled)
        {
            Entity building = m_Port.ResolveBypassBuilding(entity);
            if (building == Entity.Null)
                return false;

            m_Port.EnsureBypassBuffer();
            Entity city = m_Port.City.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BypassStationSettingElement>(city))
                return false;

            DynamicBuffer<BypassStationSettingElement> buf = EntityManager.GetBuffer<BypassStationSettingElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_BuildingEntity != building)
                    continue;

                bool previousEnabled = false;
                if (RtLog.CacheInvalidationDiagnosticsEnabled)
                    previousEnabled = buf[i].m_IsBypassStation != 0;
                buf[i] = new BypassStationSettingElement
                {
                    m_BuildingEntity = building,
                    m_IsBypassStation = enabled ? (byte)1 : (byte)0
                };
                if (RtLog.CacheInvalidationDiagnosticsEnabled)
                {
                    log.Info("[BypassStationToggle] building=" + building.Index
                        + " entity=" + entity.Index
                        + " old=" + (previousEnabled ? 1 : 0)
                        + " new=" + (enabled ? 1 : 0)
                        + " mode=update"
                        + " bufferIndex=" + i
                        + " invalidateBypassModel=1");
                }
                m_Port.InvalidateBypassModel?.Invoke();
                Invalidate();
                return true;
            }

            buf.Add(new BypassStationSettingElement
            {
                m_BuildingEntity = building,
                m_IsBypassStation = enabled ? (byte)1 : (byte)0
            });
            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                log.Info("[BypassStationToggle] building=" + building.Index
                    + " entity=" + entity.Index
                    + " old=-1"
                    + " new=" + (enabled ? 1 : 0)
                    + " mode=add"
                    + " bufferIndex=" + (buf.Length - 1)
                    + " invalidateBypassModel=1");
            }
            m_Port.InvalidateBypassModel?.Invoke();
            Invalidate();
            return true;
        }

        private static string FormatEntity(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
        }

        public ulong PanelDataVersion => m_PanelDataVersion;

        public void UpdateVersionBucket()
        {
            uint bucket = m_Port.Sim.frameIndex / PANEL_VERSION_REFRESH_FRAMES;
            if (bucket == m_LastPanelVersionBucket)
                return;

            m_LastPanelVersionBucket = bucket;
            m_PanelDataVersion++;
        }

        public void Invalidate()
        {
            m_PanelDataVersion++;
        }

        private SelectQuery Query()
        {
            if (m_Query == null)
            {
                m_Query = new SelectQuery(
                    EntityManager,
                    m_Port.Sim,
                    m_Port.ClockSnapshot,
                    m_Port.Vehicles,
                    m_Port.Obs,
                    m_Port.Spawns,
                    m_LineLastSpawnTriggerSummary,
                    m_LineLastVehicleRegisterSummary,
                    m_LineLastHoldingSummary,
                    m_LineLastDispatchSampleSummary,
                    m_Port.ResolveLine,
                    m_Port.ResolveVehicle,
                    m_Port.ResolveVehicleLine,
                    m_Port.ResolveLineDisplayName,
                    m_Port.Lines.Applied,
                    m_Port.Scheduler.NextManagedTarget,
                    m_Port.Lines.Log,
                    m_Port.ReadLineDuration,
                    m_Port.ReadLap,
                    m_Port.ReadDispatch,
                    CanConfigureBypassStation,
                    IsBypassStation,
                    GetVehiclePanelStateCode,
                    BuildVehicleTraversalProgressValue,
                    EstimateVehicleEtaText,
                    (vehicle, line) =>
                    {
                        m_Port.Stations(
                            vehicle,
                            line,
                            out string currentStationName,
                            out string nextPhysicalStationName,
                            out string nextStopStationName,
                            out bool nextPhysicalIsPass);
                        return (
                            currentStationName,
                            FormatNextPhysicalStationName(nextPhysicalStationName, nextPhysicalIsPass),
                            nextStopStationName);
                    },
                    BuildVehicleAlertSummary,
                    BuildLineAlertSummary,
                    DispatchRuntimeSystem.SlotStr,
                    BoolDebugStr,
                    LocalizedDispatchLabel,
                    LocalizedNextSlotLabel,
                    LocalizedNextSlotCoverageLabel,
                    LocalizedDispatchCacheLabel,
                    LocalizedOfficialDispatchValue,
                    IsChineseLocale);
            }

            return m_Query;
        }

        private static string FormatNextPhysicalStationName(string stationName, bool isPass)
        {
            if (string.IsNullOrWhiteSpace(stationName) || !isPass)
                return stationName ?? string.Empty;

            return IsChineseLocale()
                ? stationName + "（通过）"
                : stationName + " (pass)";
        }

        private SelectView View()
        {
            if (m_View == null)
                m_View = new SelectView();

            return m_View;
        }

        internal static string NativeState(PublicTransportFlags flags)
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
            if (!Query().TryLine(line, Entity.Null, out LineSelectData data))
            {
                summaryLabel = LocalizedDispatchLabel();
                summaryValue = LocalizedOfficialDispatchValue();
                return;
            }

            View().FillLineSummary(data, out summaryLabel, out summaryValue);
        }

        public void FillSelectedVehicleSummary(Entity vehicle, out string summaryLabel, out string summaryValue)
        {
            if (!Query().TryVehicle(vehicle, out VehicleSelectData data))
            {
                summaryLabel = "State";
                summaryValue = "Unknown";
                return;
            }

            View().FillVehicleSummary(data, out summaryLabel, out summaryValue);
        }

        public void FillSelectedLineInfo(Entity line, InfoList list)
        {
            if (!Query().TryLine(line, Entity.Null, out LineSelectData data))
                return;

            View().FillLineInfo(data, list);
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
            if (!Query().TryLine(line, Entity.Null, out LineSelectData data))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Line: -";
                meta2 = "Selection is not a transport line";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            View().FillLineCard(
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
            if (!Query().TryVehicle(vehicle, out VehicleSelectData data))
                return;

            View().FillVehicleInfo(data, list);
        }

        public bool TryLine(Entity line, out Snapshot snapshot)
        {
            return TryLine(line, Entity.Null, out snapshot);
        }

        public bool TryLine(Entity line, Entity preferredRoute, out Snapshot snapshot)
        {
            if (!Query().TryLine(line, preferredRoute, out LineSelectData data))
            {
                snapshot = default;
                return false;
            }

            snapshot = View().BuildLineSnapshot(data);
            return true;
        }

        public bool TryVehicle(Entity vehicle, out Snapshot snapshot)
        {
            if (!Query().TryVehicle(vehicle, out VehicleSelectData data))
            {
                snapshot = default;
                return false;
            }

            snapshot = View().BuildVehicleSnapshot(data);
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
            if (!Query().TryVehicle(vehicle, out VehicleSelectData data))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Vehicle: -";
                meta2 = "Selection is not a public transport vehicle";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            View().FillVehicleCard(
                data,
                out summaryLabel,
                out summaryValue,
                out meta1,
                out meta2,
                out meta3,
                out alertText);
        }

        public bool Retire(Entity vehicle)
        {
            vehicle = m_Port.ResolveVehicle(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);

            m_Port.Commands.Retire(
                vehicle,
                publicTransport,
                target,
                m_Port.Barrier.CreateCommandBuffer(),
                "UI请求");
            Invalidate();
            return true;
        }

        public bool Recheck(Entity vehicle)
        {
            vehicle = m_Port.ResolveVehicle(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;

            m_Port.Runtime.Reevaluate(vehicle);
            Invalidate();
            return true;
        }

        public bool Depart(Entity vehicle)
        {
            vehicle = m_Port.ResolveVehicle(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Entity line = m_Port.ResolveVehicleLine(vehicle);
            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            Game.Vehicles.PublicTransport pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            if ((pt.m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            Target tgt = EntityManager.GetComponentData<Target>(vehicle);
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            int currentWaypointIndex = m_Port.ComputeWp(vehicle, wps);
            if (currentWaypointIndex < 0)
                currentWaypointIndex = m_Port.CachedWp.TryGetValue(vehicle, out int cachedWaypointIndex)
                    ? cachedWaypointIndex
                    : -1;

            EntityCommandBuffer commandBuffer = m_Port.Barrier.CreateCommandBuffer();
            m_Port.Runtime.ForceManualDepart(vehicle, ref pt, m_Port.Sim.frameIndex, commandBuffer);
            m_Port.Labels.Set(vehicle, "结束上客");
            log.Info("[强制发车协助] 线路" + line.Index + " 车辆" + vehicle.Index
                + " wp=" + currentWaypointIndex);
            Invalidate();
            return true;
        }

        public bool Spawn(Entity line)
        {
            line = m_Port.ResolveLine(line, Entity.Null);
            if (line == Entity.Null || !EntityManager.Exists(line) || !m_Port.Lines.Applied(line))
                return false;

            var rvBuffers = m_Port.RouteVehicles(true);
            int actualCount = m_Port.CountVehicles(line, rvBuffers);
            int pendingTarget = actualCount;
            if (m_Port.Spawns.TryGetValue(line, out int existingTarget))
            {
                pendingTarget = math.max(existingTarget, actualCount);
            }

            int nextTarget = pendingTarget + 1;
            m_Port.Spawns[line] = nextTarget;
            m_Port.SpawnFrames[line] = m_Port.Sim.frameIndex;
            m_LineLastSpawnTriggerSummary[line] = DispatchRuntimeSystem.SlotStr((int)(m_Port.Time.normalizedTime * 1440f) % 1440)
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
            float routeDurationFrames,
            float dispatchCacheFrames,
            int spawning)
        {
#if !RT_DEBUG_TOOLS
            return "None";
#else
            if (!m_Port.Lines.Applied(line))
            {
                if (EntityManager.HasComponent<Disabled>(line))
                    return "line-disabled";
                return "official-dispatch";
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
            if (routeDurationFrames <= 0f)
                alerts = AppendAlert(alerts, "no-lap-cache");
            if (dispatchCacheFrames <= 0f)
                alerts = AppendAlert(alerts, "no-dispatch-cache");
            return alerts.Length > 0 ? alerts : "None";
#endif
        }

        private string BuildVehicleAlertSummary(Entity vehicle, Entity line, int nowMinute, int targetMinute)
        {
            if (m_Port.TryBlocker(vehicle, out Entity blockerVehicle) && blockerVehicle != Entity.Null)
                return "yielding-for:" + blockerVehicle.Index;

#if !RT_DEBUG_TOOLS
            return "None";
#else
            if (line == Entity.Null || !m_Port.Lines.Applied(line))
                return "official-dispatch";

            string alerts = string.Empty;
            if (m_Port.Misfires.Contains(vehicle))
                alerts = AppendAlert(alerts, "bv-misfire");
            if (m_Port.Vehicles.IsInbound(vehicle))
                alerts = AppendAlert(alerts, "nearing-terminus");
            if (m_Port.Vehicles.TryGetCooldown(vehicle, out uint cooldownUntil) && m_Port.Sim.frameIndex < cooldownUntil)
                alerts = AppendAlert(alerts, "launch-cooldown");
            if (targetMinute >= 0 && ScheduleClock.Expired(nowMinute, targetMinute))
                alerts = AppendAlert(alerts, "target-expired");
            if (line != Entity.Null
                && m_Port.Vehicles.TryGetState(vehicle, out var state)
                && state == VehicleState.Idle
                && m_Port.Scheduler.Policy.ShouldProtect(line, vehicle, nowMinute, -1))
            {
                alerts = AppendAlert(alerts, "yield-protected");
            }

            return alerts.Length > 0 ? alerts : "None";
#endif
        }

        private string BuildVehicleTraversalProgressValue(Entity vehicle)
        {
            vehicle = m_Port.ResolveVehicle(vehicle);
            if (vehicle == Entity.Null)
                return "-";

            if (!m_Port.TryProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                return IsChineseLocale() ? "未知" : "unknown";

            int progressPercent = (int)math.round(math.saturate(segmentPosition) * 100f);
            return "wp" + nextWaypointIndex + " / " + progressPercent + "%";
        }

        private string GetVehiclePanelStateCode(Entity vehicle, VehicleState vehicleState)
        {
            if (m_Port.TryBlocker(vehicle, out _)
                && (vehicleState == VehicleState.Holding || vehicleState == VehicleState.Running))
            {
                return "Yielding";
            }

            if (vehicleState == VehicleState.Holding
                && (!m_Port.Vehicles.TryGetTarget(vehicle, out int holdingTarget) || holdingTarget < 0))
            {
                return "Idle";
            }

            return vehicleState.ToString();
        }

        private string EstimateVehicleEtaText(Entity vehicle, Entity line, VehicleState vehicleState)
        {
            if (line == Entity.Null || !m_Port.Lines.Applied(line))
                return "-";

            float lineDurationFrames = m_Port.ReadLineDuration(line);
            bool lineHasHistory = lineDurationFrames > 0f;
            uint nowFrame = m_Port.Sim.frameIndex;
            float etaFrames = float.MaxValue;

            if (vehicleState == VehicleState.Preparing)
            {
                var routeWaypoints = m_Port.RouteWaypoints(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = m_Port.PrepEta(vehicle, line, waypoints, nowFrame, lineDurationFrames);
            }
            else if (vehicleState == VehicleState.Running)
            {
                var routeWaypoints = m_Port.RouteWaypoints(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = m_Port.RunEta(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
            }
            else if (vehicleState == VehicleState.Holding)
            {
                etaFrames = 0f;
            }

            if (etaFrames == float.MaxValue)
                return "-";

            double etaMinutes = m_Port.ClockSnapshot().ToMinutes(etaFrames);
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
            string state = m_Port.Vehicles.TryGetState(vehicle, out var st) ? st.ToString() : "Unknown";
            string lineStr = m_Port.Vehicles.TryGetLine(vehicle, out Entity line) ? line.Index.ToString() : "-";
            string targetMinuteText = m_Port.Vehicles.TryGetTarget(vehicle, out int targetMinute) && targetMinute >= 0 ? DispatchRuntimeSystem.SlotStr(targetMinute) : "-";
            string currentMinuteText = m_Port.Vehicles.TryGetSlot(vehicle, out int currentSlotMinute) && currentSlotMinute >= 0 ? DispatchRuntimeSystem.SlotStr(currentSlotMinute) : "-";
            string cachedWp = m_Port.CachedWp.TryGetValue(vehicle, out int wp) ? wp.ToString() : "-";
            string tagged = BoolDebugStr(m_Port.Vehicles.IsInbound(vehicle));
            string cooldown = BoolDebugStr(m_Port.Vehicles.TryGetCooldown(vehicle, out uint cd) && m_Port.Sim.frameIndex < cd);
            string misfire = BoolDebugStr(m_Port.Misfires.Contains(vehicle));
            string lapStartFrame = m_Port.Obs.TryLapStartFrame(vehicle, out uint lsf) ? lsf.ToString() : "-";
            string lapFrames = m_Port.Obs.TryLapFrames(vehicle, out uint lf) ? lf.ToString() : "-";
            string lapDistance = m_Port.Obs.TryLapDistance(vehicle, out float ld) && ld >= 0f ? (ld / 1000f).ToString("F2") + "km" : "-";
            string prepStart = m_Port.Vehicles.TryGetPreparing(vehicle, out uint psf) ? psf.ToString() : "-";
            string idleStart = m_Port.Vehicles.TryGetIdle(vehicle, out uint isf) ? isf.ToString() : "-";

            AddDebugItem(list, "车辆", "Vehicle", vehicle.Index.ToString());
            AddDebugItem(list, "状态", "State", state);
            AddDebugItem(list, "线路", "Line", lineStr);
            AddDebugItem(list, "目标班次", "Target Slot", targetMinuteText);
            AddDebugItem(list, "当前班次", "Current Slot", currentMinuteText);
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
            int nowMinute = m_Port.ClockSnapshot().NowMinute;
            bool isManagedLine = m_Port.Lines.Applied(line);
            int nextSlotMinute = isManagedLine
                ? m_Port.Scheduler.NextManagedTarget(line, nowMinute)
                : m_Port.Scheduler.NextSlotMin(nowMinute);
            float routeDurationFrames = m_Port.ReadLineDuration(line);
            float lapCacheFrames = m_Port.ReadLap(line);
            float dispatchCacheFrames = m_Port.ReadDispatch(line);
            string routeDurationMinutesText = routeDurationFrames > 0f ? m_Port.ClockSnapshot().ToMinutes(routeDurationFrames).ToString("F1") + "min" : "-";
            string lapCacheMinutesText = lapCacheFrames > 0f ? m_Port.ClockSnapshot().ToMinutes(lapCacheFrames).ToString("F1") + "min" : "-";
            string dispatchCacheMinutesText = dispatchCacheFrames > 0f ? m_Port.ClockSnapshot().ToMinutes(dispatchCacheFrames).ToString("F1") + "min" : "-";
            string spawning = m_Port.Spawns.TryGetValue(line, out int spawnTarget) ? spawnTarget.ToString() : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int tagged = 0;
            int total = 0;
            var rvBuffers = m_Port.RouteVehicles(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity vehicle = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_Port.Vehicles.IsInbound(vehicle))
                        tagged++;
                    if (!m_Port.Vehicles.TryGetState(vehicle, out var st))
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
            AddDebugItem(list, isManagedLine ? LocalizedNextSlotLabel() : "下一班次", "Next Slot", DispatchRuntimeSystem.SlotStr(nextSlotMinute));
            AddDebugItem(list, "总车数", "Total Vehicles", total.ToString());
            AddDebugItem(list, "预备数", "Preparing Count", preparing.ToString());
            AddDebugItem(list, "候车数", "Holding Count", holding.ToString());
            AddDebugItem(list, "运行数", "Running Count", running.ToString());
            AddDebugItem(list, "待调度数", "Idle Count", idle.ToString());
            AddDebugItem(list, "回库数", "Retiring Count", retiring.ToString());
            AddDebugItem(list, "回流标签数", "Nearing Terminus Count", tagged.ToString());
            AddDebugItem(list, "产车目标", "Spawn Target", spawning);
            AddDebugItem(list, "全程用时", "Route Duration", routeDurationMinutesText);
            AddDebugItem(list, "圈时缓存", "Lap Cache", lapCacheMinutesText);
            AddDebugItem(list, "出库缓存", "Dispatch Cache", dispatchCacheMinutesText);
        }

    }
}
