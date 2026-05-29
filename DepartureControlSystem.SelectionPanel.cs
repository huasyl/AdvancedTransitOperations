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
    public partial class DispatchRuntimeSystem
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
            if (m_VehicleView.Contains(entity))
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
            return resolvedVehicle != Entity.Null && m_VehicleView.Contains(resolvedVehicle);
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

        private SelectionPanelQuery PanelQuery()
        {
            if (m_SelectionPanelQuery == null)
            {
                m_SelectionPanelQuery = new SelectionPanelQuery(
                    EntityManager,
                    m_TimeSystem,
                    m_SimulationSystem,
                    m_VehicleView,
                    m_StopDwell,
                    m_SpawningLines,
                    m_LineLastSpawnTriggerSummary,
                    m_LineLastVehicleRegisterSummary,
                    m_LineLastHoldingSummary,
                    m_LineLastDispatchSampleSummary,
                    ResolveSelectedLineEntity,
                    ResolveSelectedVehicleEntity,
                    ResolveVehicleLine,
                    IsWorkbenchTimetableApplied,
                    m_DispatchScheduler.NextManagedTarget,
                    LogAppliedWorkbenchLineState,
                    ReadLineLapCache,
                    ReadDispatchCache,
                    CanConfigureBypassStation,
                    IsBypassStation,
                    GetVehiclePanelStateCode,
                    BuildVehicleTraversalProgressValue,
                    EstimateVehicleEtaText,
                    (vehicle, line) =>
                    {
                        TryGetBroadcastPanelStationContext(vehicle, line, out string currentStationName, out string nextStationName, out _);
                        return (currentStationName, nextStationName);
                    },
                    BuildVehicleBroadcastEventValue,
                    BuildVehicleAlertSummary,
                    BuildLineAlertSummary,
                    SlotStr,
                    BoolDebugStr,
                    LocalizedDispatchLabel,
                    LocalizedNextSlotLabel,
                    LocalizedNextSlotCoverageLabel,
                    LocalizedDispatchCacheLabel,
                    LocalizedOfficialDispatchValue,
                    IsChineseLocale,
                    (int)SIM_FRAMES_PER_MINUTE);
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
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);

            m_CommandApplier.Retire(
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

            m_RuntimeController.Reevaluate(vehicle);
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

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            m_CommandApplier.ForceDepart(vehicle, ref pt, m_SimulationSystem.frameIndex, commandBuffer);
            ClearBypassYieldState(vehicle, "UI强制发车");
            SetUILabel(vehicle, "结束上客");
            log.Info("[强制发车协助] 线路" + line.Index + " 车辆" + vehicle.Index
                + " wp=" + currentWaypointIndex);
            InvalidatePanelData();
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

    }
}
