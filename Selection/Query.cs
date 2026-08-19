using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using System;
using System.Collections.Generic;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
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
        public int NextPlannedArrivalMinute;
        public int PlannedArrivalMinute;
        public int ActualArrivalMinute;
        public int PlannedDepartureMinute;
        public string AlertText;
        public bool HasStopSession;
        public string NextPassStationName;
        public bool WaitingForFastTrain;
        public int WaitingForFastTrainVehicleId;
    }

    internal sealed class SelectQuery
    {
        private readonly EntityManager m_EntityManager;
        private readonly Game.Simulation.SimulationSystem m_SimulationSystem;
        private readonly VehicleView m_VehicleView;
        private readonly SelectPort.Blocker m_TryBlocker;
        private readonly SelectPort.SessionArrival m_TrySessionArrival;
        private readonly SelectPort.StopSession m_TryStopSession;
        private readonly SelectPort.VehicleTimes m_TryVehicleTimes;
        private readonly SelectPort.PanelStations m_TryPanelStations;
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
            SelectPort.Blocker tryBlocker,
            SelectPort.SessionArrival trySessionArrival,
            SelectPort.StopSession tryStopSession,
            SelectPort.VehicleTimes tryVehicleTimes,
            SelectPort.PanelStations tryPanelStations,
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
            m_TryBlocker = tryBlocker;
            m_TrySessionArrival = trySessionArrival;
            m_TryStopSession = tryStopSession;
            m_TryVehicleTimes = tryVehicleTimes;
            m_TryPanelStations = tryPanelStations;
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
            int nextPlannedArrivalMinute = -1;
            int plannedArrivalMinute = -1;
            int actualArrivalMinute = -1;
            int plannedDepartureMinute = -1;
            if (m_TryVehicleTimes != null)
            {
                m_TryVehicleTimes(
                    vehicle,
                    out _,
                    out _,
                    out nextPlannedArrivalMinute,
                    out plannedArrivalMinute,
                    out actualArrivalMinute,
                    out plannedDepartureMinute);
            }

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
                NextPlannedArrivalMinute = nextPlannedArrivalMinute,
                PlannedArrivalMinute = plannedArrivalMinute,
                ActualArrivalMinute = actualArrivalMinute,
                PlannedDepartureMinute = plannedDepartureMinute,
                AlertText = isManagedVehicle
                    ? m_BuildVehicleAlert(vehicle, line, nowMinute, targetMinute)
                    : (line != Entity.Null ? "using-native-fallback" : "vehicle-not-tracked")
            };
            return true;
        }

        public bool TryVehiclePanel(Entity selectedEntity, out VehicleSelectData data)
        {
            data = default;
            Entity vehicle = m_ResolveSelectedVehicle(selectedEntity);
            if (vehicle == Entity.Null)
                return false;

            if (!m_VehicleView.TryGetState(vehicle, out VehicleState vehicleState))
            {
                data = new VehicleSelectData
                {
                    Vehicle = vehicle,
                    IsManagedVehicle = false,
                    StateText = "vanillaControl"
                };
                return true;
            }

            Entity sessionLine = Entity.Null;
            int sessionWaypointIndex = -1;
            uint sessionArrivalFrame = 0u;
            bool hasStopSession = m_TryStopSession != null
                && m_TryStopSession(
                    vehicle,
                    out sessionLine,
                    out sessionWaypointIndex,
                    out sessionArrivalFrame);
            Entity line = hasStopSession ? sessionLine : m_ResolveVehicleLine(vehicle);
            TransportModeProfile modeProfile = line != Entity.Null
                ? TransportModeProfile.GetProfile(TransportModeResolver.Resolve(m_EntityManager, line))
                : default;
            bool includePhysical = modeProfile.Lifecycle == LifecycleKind.Rail;
            bool canBypass = modeProfile.CanBypass;
            VehicleStationContext stationContext = default;
            bool hasStationContext = m_TryPanelStations != null
                && m_TryPanelStations(
                    vehicle,
                    line,
                    hasStopSession ? sessionWaypointIndex : -1,
                    includePhysical,
                    out stationContext);

            int currentWaypointIndex = -1;
            int nextWaypointIndex = -1;
            int nextPlannedArrivalMinute = -1;
            int plannedArrivalMinute = -1;
            int actualArrivalMinute = -1;
            int plannedDepartureMinute = -1;
            if (m_TryVehicleTimes != null)
            {
                m_TryVehicleTimes(
                    vehicle,
                    out currentWaypointIndex,
                    out nextWaypointIndex,
                    out nextPlannedArrivalMinute,
                    out plannedArrivalMinute,
                    out actualArrivalMinute,
                    out plannedDepartureMinute);
            }

            bool hasCurrentStop = hasStopSession
                && hasStationContext
                && stationContext.CurrentStopWaypointIndex == sessionWaypointIndex
                && !string.IsNullOrWhiteSpace(stationContext.CurrentStationName);
            bool hasCurrentTimes = hasCurrentStop && currentWaypointIndex == sessionWaypointIndex;
            bool hasNextTimes = hasStationContext
                && stationContext.NextStopWaypointIndex >= 0
                && nextWaypointIndex == stationContext.NextStopWaypointIndex;
            bool hasNextPass = hasStationContext
                && stationContext.NextPhysicalIsPass
                && !string.IsNullOrWhiteSpace(stationContext.NextPhysicalStationId)
                && !string.IsNullOrWhiteSpace(stationContext.NextStopStationId)
                && !string.Equals(
                    stationContext.NextPhysicalStationId,
                    stationContext.CurrentStationId,
                    StringComparison.Ordinal)
                && !string.Equals(
                    stationContext.NextPhysicalStationId,
                    stationContext.NextStopStationId,
                    StringComparison.Ordinal);
            int currentMinute = m_VehicleView.TryGetSlot(vehicle, out int currentSlotMinute)
                ? currentSlotMinute
                : -1;
            int targetMinute = m_VehicleView.TryGetTarget(vehicle, out int targetSlotMinute)
                ? targetSlotMinute
                : -1;
            string stateText = GetPanelStateCode(vehicle, vehicleState, canBypass, out Entity blockerVehicle);

            data = new VehicleSelectData
            {
                Vehicle = vehicle,
                Line = line,
                IsManagedVehicle = true,
                StateText = stateText,
                CurrentMinute = currentMinute,
                TargetMinute = targetMinute,
                CurrentText = currentMinute >= 0 ? m_SlotText(currentMinute) : string.Empty,
                TargetText = targetMinute >= 0 ? m_SlotText(targetMinute) : string.Empty,
                HasStopSession = hasStopSession,
                StopDwellValue = hasStopSession ? BuildStopDwellValue(sessionArrivalFrame) : string.Empty,
                CurrentStationName = hasCurrentStop
                    ? stationContext.CurrentStationName
                    : string.Empty,
                NextStopStationName = hasStationContext ? stationContext.NextStopStationName : string.Empty,
                NextPassStationName = hasNextPass ? stationContext.NextPhysicalStationName : string.Empty,
                NextPlannedArrivalMinute = hasNextTimes ? nextPlannedArrivalMinute : -1,
                PlannedArrivalMinute = hasCurrentTimes ? plannedArrivalMinute : -1,
                ActualArrivalMinute = hasCurrentTimes ? actualArrivalMinute : -1,
                PlannedDepartureMinute = hasCurrentTimes ? plannedDepartureMinute : -1,
                WaitingForFastTrain = stateText == "Yielding",
                WaitingForFastTrainVehicleId = blockerVehicle != Entity.Null ? blockerVehicle.Index : -1
            };
            return true;
        }

        private int GetNowMinute()
        {
            return m_ClockSnapshot().NowMinute;
        }

        private string GetPanelStateCode(
            Entity vehicle,
            VehicleState vehicleState,
            bool canBypass,
            out Entity blockerVehicle)
        {
            blockerVehicle = Entity.Null;
            if (canBypass
                && m_TryBlocker != null
                && m_TryBlocker(vehicle, out blockerVehicle)
                && blockerVehicle != Entity.Null
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

        private string FormatMinutes(float frames)
        {
            return frames > 0f ? m_ClockSnapshot().ToMinutes(frames).ToString("F1") + " " + MinuteUnit() : "-";
        }

        private string MinuteUnit()
        {
            if (SelectPanel.IsChineseLocale())
                return "分";
            if (SelectPanel.IsJapaneseLocale())
                return "分";
            return "min";
        }

        private string BuildStopDwellValue(Entity vehicle)
        {
            if (!m_TrySessionArrival(vehicle, out uint dwellSinceFrame))
                return "-";

            return BuildStopDwellValue(dwellSinceFrame);
        }

        private string BuildStopDwellValue(uint dwellSinceFrame)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            uint elapsedFrames = unchecked(nowFrame - dwellSinceFrame);
            return m_ClockSnapshot().ToMinutes(elapsedFrames).ToString("F1") + " " + MinuteUnit();
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
