using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Observation;
using System;
using System.Collections.Generic;
using RapidTransitMod.Core;
using Unity.Entities;

namespace RapidTransitMod
{
    internal struct LineSelectData
    {
        public Entity SelectedEntity;
        public Entity Line;
        public int NowMinute;
        public bool IsManagedLine;
        public bool IsChineseLocale;
        public int NextSlotMinute;
        public float RouteDurationFrames;
        public float LapCacheFrames;
        public float DispatchCacheFrames;
        public int Preparing;
        public int Holding;
        public int Running;
        public int Idle;
        public int Retiring;
        public int NearingTerminus;
        public int TargetingNextSlot;
        public int OccupyingNextSlot;
        public int Total;
        public int SpawnPending;
        public string SpawnTriggerSummary;
        public string RegisterSummary;
        public string HoldingSummary;
        public string DispatchSampleSummary;
        public string DispatchLabel;
        public string NextSlotLabel;
        public string NextSlotCoverageLabel;
        public string DispatchCacheLabel;
        public string OfficialDispatchValue;
        public string NowText;
        public string NextSlotText;
        public string LineDisplayName;
        public string RouteDurationText;
        public string LapCacheText;
        public string DispatchCacheText;
        public string ManagedText;
        public string AlertText;
        public string CardAlertText;
        public bool HasWaypointData;
        public bool ShowBypassStationToggle;
        public bool BypassStationChecked;
    }

    internal struct VehicleSelectData
    {
        public Entity Vehicle;
        public Entity Line;
        public int NowMinute;
        public bool IsManagedVehicle;
        public bool IsChineseLocale;
        public PublicTransportFlags NativeFlags;
        public string NativeStateText;
        public string ManagedText;
        public string StateText;
        public int TargetMinute;
        public int CurrentMinute;
        public string TargetText;
        public string CurrentText;
        public string ProgressValue;
        public string EtaValue;
        public string StopDwellValue;
        public string InboundTimeValue;
        public string CurrentStationName;
        public string NextPhysicalStationName;
        public string NextStopStationName;
        public string AlertText;
    }

    internal sealed class SelectQuery
    {
        private readonly EntityManager m_EntityManager;
        private readonly Game.Simulation.SimulationSystem m_SimulationSystem;
        private readonly VehicleView m_VehicleView;
        private readonly Query m_ObsQuery;
        private readonly Unity.Collections.NativeHashMap<Entity, int> m_SpawningLines;
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary;
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary;
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary;
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary;
        private readonly Func<Entity, Entity, Entity> m_ResolveSelectedLine;
        private readonly Func<Entity, Entity> m_ResolveSelectedVehicle;
        private readonly Func<Entity, Entity> m_ResolveVehicleLine;
        private readonly Func<Entity, string> m_GetLineDisplayName;
        private readonly Func<Entity, bool> m_IsManagedLine;
        private readonly Func<Entity, int, int> m_GetNextSlot;
        private readonly Action<Entity, int, int> m_LogLineState;
        private readonly Func<Entity, float> m_ReadLineDuration;
        private readonly Func<Entity, float> m_ReadLineLapCache;
        private readonly Func<Entity, float> m_ReadDispatchCache;
        private readonly Func<Entity, bool> m_CanConfigureBypass;
        private readonly Func<Entity, bool> m_IsBypassStation;
        private readonly Func<Entity, VehicleState, string> m_GetManagedVehicleStateText;
        private readonly Func<Entity, string> m_BuildTraversalProgress;
        private readonly Func<Entity, Entity, VehicleState, string> m_BuildEta;
        private readonly Func<Entity, Entity, (string CurrentStationName, string NextPhysicalStationName, string NextStopStationName)> m_GetStationContext;
        private readonly Func<Entity, Entity, int, int, string> m_BuildVehicleAlert;
        private readonly Func<Entity, int, int, float, float, int, string> m_BuildLineAlert;
        private readonly Func<int, string> m_SlotText;
        private readonly Func<bool, string> m_BoolText;
        private readonly Func<string> m_DispatchLabel;
        private readonly Func<string> m_NextSlotLabel;
        private readonly Func<string> m_NextSlotCoverageLabel;
        private readonly Func<string> m_DispatchCacheLabel;
        private readonly Func<string> m_OfficialDispatchValue;
        private readonly Func<bool> m_IsChineseLocale;
        private readonly Func<ClockSnapshot> m_ClockSnapshot;

        public SelectQuery(
            EntityManager entityManager,
            SimulationSystem simulationSystem,
            Func<ClockSnapshot> clockSnapshot,
            VehicleView vehicleView,
            Query obsQuery,
            Unity.Collections.NativeHashMap<Entity, int> spawningLines,
            Dictionary<Entity, string> lineLastSpawnTriggerSummary,
            Dictionary<Entity, string> lineLastVehicleRegisterSummary,
            Dictionary<Entity, string> lineLastHoldingSummary,
            Dictionary<Entity, string> lineLastDispatchSampleSummary,
            Func<Entity, Entity, Entity> resolveSelectedLine,
            Func<Entity, Entity> resolveSelectedVehicle,
            Func<Entity, Entity> resolveVehicleLine,
            Func<Entity, string> getLineDisplayName,
            Func<Entity, bool> isManagedLine,
            Func<Entity, int, int> getNextSlot,
            Action<Entity, int, int> logLineState,
            Func<Entity, float> readLineDuration,
            Func<Entity, float> readLineLapCache,
            Func<Entity, float> readDispatchCache,
            Func<Entity, bool> canConfigureBypass,
            Func<Entity, bool> isBypassStation,
            Func<Entity, VehicleState, string> getManagedVehicleStateText,
            Func<Entity, string> buildTraversalProgress,
            Func<Entity, Entity, VehicleState, string> buildEta,
            Func<Entity, Entity, (string CurrentStationName, string NextPhysicalStationName, string NextStopStationName)> getStationContext,
            Func<Entity, Entity, int, int, string> buildVehicleAlert,
            Func<Entity, int, int, float, float, int, string> buildLineAlert,
            Func<int, string> slotText,
            Func<bool, string> boolText,
            Func<string> dispatchLabel,
            Func<string> nextSlotLabel,
            Func<string> nextSlotCoverageLabel,
            Func<string> dispatchCacheLabel,
            Func<string> officialDispatchValue,
            Func<bool> isChineseLocale)
        {
            m_EntityManager = entityManager;
            m_SimulationSystem = simulationSystem;
            m_ClockSnapshot = clockSnapshot;
            m_VehicleView = vehicleView;
            m_ObsQuery = obsQuery;
            m_SpawningLines = spawningLines;
            m_LineLastSpawnTriggerSummary = lineLastSpawnTriggerSummary;
            m_LineLastVehicleRegisterSummary = lineLastVehicleRegisterSummary;
            m_LineLastHoldingSummary = lineLastHoldingSummary;
            m_LineLastDispatchSampleSummary = lineLastDispatchSampleSummary;
            m_ResolveSelectedLine = resolveSelectedLine;
            m_ResolveSelectedVehicle = resolveSelectedVehicle;
            m_ResolveVehicleLine = resolveVehicleLine;
            m_GetLineDisplayName = getLineDisplayName;
            m_IsManagedLine = isManagedLine;
            m_GetNextSlot = getNextSlot;
            m_LogLineState = logLineState;
            m_ReadLineDuration = readLineDuration;
            m_ReadLineLapCache = readLineLapCache;
            m_ReadDispatchCache = readDispatchCache;
            m_CanConfigureBypass = canConfigureBypass;
            m_IsBypassStation = isBypassStation;
            m_GetManagedVehicleStateText = getManagedVehicleStateText;
            m_BuildTraversalProgress = buildTraversalProgress;
            m_BuildEta = buildEta;
            m_GetStationContext = getStationContext;
            m_BuildVehicleAlert = buildVehicleAlert;
            m_BuildLineAlert = buildLineAlert;
            m_SlotText = slotText;
            m_BoolText = boolText;
            m_DispatchLabel = dispatchLabel;
            m_NextSlotLabel = nextSlotLabel;
            m_NextSlotCoverageLabel = nextSlotCoverageLabel;
            m_DispatchCacheLabel = dispatchCacheLabel;
            m_OfficialDispatchValue = officialDispatchValue;
            m_IsChineseLocale = isChineseLocale;
        }

        public bool TryLine(Entity selectedEntity, Entity preferredRoute, out LineSelectData data)
        {
            data = default;
            Entity line = m_ResolveSelectedLine(selectedEntity, preferredRoute);
            if (line == Entity.Null)
                return false;

            int nowMinute = GetNowMinute();
            bool isManagedLine = m_IsManagedLine(line);
            bool isChineseLocale = m_IsChineseLocale();
            int nextSlotMinute = isManagedLine ? m_GetNextSlot(line, nowMinute) : -1;
            if (isManagedLine)
                m_LogLineState(line, nowMinute, nextSlotMinute);

            float routeDurationFrames = isManagedLine ? m_ReadLineDuration(line) : 0f;
            float lapCacheFrames = isManagedLine ? m_ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? m_ReadDispatchCache(line) : 0f;
            int spawnPending = isManagedLine && m_SpawningLines.IsCreated && m_SpawningLines.TryGetValue(line, out int pending)
                ? pending
                : 0;
            string officialDispatchValue = m_OfficialDispatchValue();

            data = new LineSelectData
            {
                SelectedEntity = selectedEntity,
                Line = line,
                NowMinute = nowMinute,
                IsManagedLine = isManagedLine,
                IsChineseLocale = isChineseLocale,
                NextSlotMinute = nextSlotMinute,
                RouteDurationFrames = routeDurationFrames,
                LapCacheFrames = lapCacheFrames,
                DispatchCacheFrames = dispatchCacheFrames,
                SpawnPending = spawnPending,
                SpawnTriggerSummary = LookupSummary(m_LineLastSpawnTriggerSummary, line),
                RegisterSummary = LookupSummary(m_LineLastVehicleRegisterSummary, line),
                HoldingSummary = LookupSummary(m_LineLastHoldingSummary, line),
                DispatchSampleSummary = LookupSummary(m_LineLastDispatchSampleSummary, line),
                DispatchLabel = m_DispatchLabel(),
                NextSlotLabel = m_NextSlotLabel(),
                NextSlotCoverageLabel = m_NextSlotCoverageLabel(),
                DispatchCacheLabel = m_DispatchCacheLabel(),
                OfficialDispatchValue = officialDispatchValue,
                NowText = m_SlotText(nowMinute),
                NextSlotText = isManagedLine ? m_SlotText(nextSlotMinute) : officialDispatchValue,
                LineDisplayName = m_GetLineDisplayName(line),
                RouteDurationText = FormatMinutes(routeDurationFrames),
                LapCacheText = FormatMinutes(lapCacheFrames),
                DispatchCacheText = FormatMinutes(dispatchCacheFrames),
                ManagedText = m_BoolText(isManagedLine),
                HasWaypointData = m_EntityManager.HasBuffer<RouteWaypoint>(line),
                ShowBypassStationToggle = m_CanConfigureBypass(selectedEntity),
                BypassStationChecked = m_CanConfigureBypass(selectedEntity) && m_IsBypassStation(selectedEntity)
            };

            if (m_EntityManager.HasBuffer<RouteVehicle>(line))
            {
                DynamicBuffer<RouteVehicle> routeVehicles = m_EntityManager.GetBuffer<RouteVehicle>(line, true);
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity vehicle = routeVehicles[i].m_Vehicle;
                    if (!m_EntityManager.Exists(vehicle))
                        continue;

                    data.Total++;
                    if (m_VehicleView.IsInbound(vehicle))
                        data.NearingTerminus++;
                    if (isManagedLine && m_VehicleView.TryGetTarget(vehicle, out int targetSlotMinute) && targetSlotMinute == nextSlotMinute)
                        data.TargetingNextSlot++;
                    if (isManagedLine && m_VehicleView.TryGetSlot(vehicle, out int currentSlotMinute) && currentSlotMinute == nextSlotMinute)
                        data.OccupyingNextSlot++;

                    if (!m_VehicleView.TryGetState(vehicle, out VehicleState state))
                        continue;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            data.Preparing++;
                            break;
                        case VehicleState.Holding:
                            data.Holding++;
                            break;
                        case VehicleState.Running:
                            data.Running++;
                            break;
                        case VehicleState.Idle:
                            data.Idle++;
                            break;
                        case VehicleState.Retiring:
                            data.Retiring++;
                            break;
                    }
                }
            }

            int nextSlotOccupancy = data.TargetingNextSlot + data.OccupyingNextSlot;
            data.AlertText = m_BuildLineAlert(
                line,
                isManagedLine ? nextSlotOccupancy : 0,
                data.NearingTerminus,
                routeDurationFrames,
                dispatchCacheFrames,
                spawnPending);
            data.CardAlertText = m_BuildLineAlert(
                line,
                isManagedLine && spawnPending > 0 ? 1 : 0,
                0,
                routeDurationFrames,
                dispatchCacheFrames,
                spawnPending);

            return true;
        }

        public bool TryVehicle(Entity selectedEntity, out VehicleSelectData data)
        {
            data = default;
            Entity vehicle = m_ResolveSelectedVehicle(selectedEntity);
            if (vehicle == Entity.Null)
                return false;

            int nowMinute = GetNowMinute();
            bool isChineseLocale = m_IsChineseLocale();
            VehicleState vehicleState = default;
            bool isManagedVehicle = m_VehicleView.TryGetState(vehicle, out vehicleState);
            PublicTransportFlags nativeFlags = m_EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? m_EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                : 0;
            string nativeStateText = SelectPanel.NativeState(nativeFlags);
            Entity line = m_ResolveVehicleLine(vehicle);
            int targetMinute = m_VehicleView.TryGetTarget(vehicle, out int targetSlotMinute) ? targetSlotMinute : -1;
            int currentMinute = m_VehicleView.TryGetSlot(vehicle, out int currentSlotMinute) ? currentSlotMinute : -1;
            (string currentStationName, string nextPhysicalStationName, string nextStopStationName) = m_GetStationContext(vehicle, line);

            data = new VehicleSelectData
            {
                Vehicle = vehicle,
                Line = line,
                NowMinute = nowMinute,
                IsManagedVehicle = isManagedVehicle,
                IsChineseLocale = isChineseLocale,
                NativeFlags = nativeFlags,
                NativeStateText = nativeStateText,
                ManagedText = m_BoolText(isManagedVehicle),
                StateText = isManagedVehicle
                    ? m_GetManagedVehicleStateText(vehicle, vehicleState)
                    : nativeStateText,
                TargetMinute = targetMinute,
                CurrentMinute = currentMinute,
                TargetText = targetMinute >= 0 ? m_SlotText(targetMinute) : "-",
                CurrentText = currentMinute >= 0 ? m_SlotText(currentMinute) : "-",
                ProgressValue = m_BuildTraversalProgress(vehicle),
                EtaValue = m_BuildEta(vehicle, line, vehicleState),
                StopDwellValue = BuildStopDwellValue(vehicle),
                InboundTimeValue = BuildInboundTimeValue(vehicle),
                CurrentStationName = currentStationName ?? string.Empty,
                NextPhysicalStationName = nextPhysicalStationName ?? string.Empty,
                NextStopStationName = nextStopStationName ?? string.Empty,
                AlertText = isManagedVehicle
                    ? m_BuildVehicleAlert(vehicle, line, nowMinute, targetMinute)
                    : (line != Entity.Null ? "using-native-fallback" : "vehicle-not-tracked")
            };
            return true;
        }

        private int GetNowMinute()
        {
            return m_ClockSnapshot().NowMinute;
        }

        private string FormatMinutes(float frames)
        {
            return frames > 0f ? m_ClockSnapshot().ToMinutes(frames).ToString("F1") + " min" : "-";
        }

        private string BuildStopDwellValue(Entity vehicle)
        {
            if (!m_ObsQuery.TryDwellStart(vehicle, out uint dwellSinceFrame))
                return "-";

            uint nowFrame = m_SimulationSystem.frameIndex;
            uint elapsedFrames = nowFrame > dwellSinceFrame
                ? nowFrame - dwellSinceFrame
                : 0u;
            return m_ClockSnapshot().ToMinutes(elapsedFrames).ToString("F1") + " min";
        }

        private string BuildInboundTimeValue(Entity vehicle)
        {
            if (m_VehicleView.TryGetPreparing(vehicle, out uint prepStartFrame))
                return BuildStartMinuteText(prepStartFrame);

            if (m_VehicleView.TryGetOrigin(vehicle, out uint originSinceFrame))
                return BuildStartMinuteText(originSinceFrame);

            return "-";
        }

        private string BuildStartMinuteText(uint startFrame)
        {
            uint currentFrame = m_SimulationSystem.frameIndex;
            uint elapsedFrames = unchecked(currentFrame - startFrame);
            if (elapsedFrames >= 0x80000000u)
                elapsedFrames = 0u;
            ClockSnapshot clockSnapshot = m_ClockSnapshot();
            int startMinute = (int)Math.Floor(clockSnapshot.NowMinute - clockSnapshot.ToMinutes(elapsedFrames));
            return m_SlotText(((startMinute % 1440) + 1440) % 1440);
        }

        private static string LookupSummary(Dictionary<Entity, string> summaries, Entity line)
        {
            return summaries.TryGetValue(line, out string text) ? text : "-";
        }
    }
}
