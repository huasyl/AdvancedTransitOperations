using Game.UI.InGame;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SelectView
    {
        public void FillLineSummary(LineSelectData data, out string summaryLabel, out string summaryValue)
        {
            summaryLabel = data.IsManagedLine
                ? data.NextSlotLabel
                : data.DispatchLabel;
            summaryValue = data.IsManagedLine
                ? data.NextSlotText
                : data.OfficialDispatchValue;
        }

        public void FillVehicleSummary(VehicleSelectData data, out string summaryLabel, out string summaryValue)
        {
            summaryLabel = "State";
            summaryValue = data.StateText;
        }

        public void FillLineInfo(LineSelectData data, InfoList list)
        {
            string slotCoverage = data.IsManagedLine
                ? ((data.TargetingNextSlot + data.OccupyingNextSlot) > 0 ? "Occupied" : "Gap")
                : data.OfficialDispatchValue;
            string spawnTarget = data.IsManagedLine ? data.SpawnPending.ToString() : "-";
            string alertText = FormatLegacyAlertText(data.AlertText, data.OfficialDispatchValue);

            AddDebugItem(list, "线路", "Line", data.Line.Index.ToString());
            if (data.IsManagedLine)
                AddDebugItem(list, data.NextSlotLabel, "Next Slot", data.NextSlotText);
            else
                AddDebugItem(list, data.DispatchLabel, "Dispatch", data.OfficialDispatchValue);
            AddDebugItem(list, "车辆概览", "Fleet", data.Total + " total / " + data.Running + " running / " + data.Holding + " holding");
            AddDebugItem(list, "状态分布", "States", "prep " + data.Preparing + " / idle " + data.Idle + " / retire " + data.Retiring);
            if (data.IsManagedLine)
                AddDebugItem(list, data.NextSlotCoverageLabel, "Next Slot Coverage", slotCoverage + " (" + data.TargetingNextSlot + " target / " + data.OccupyingNextSlot + " active)");
            else
                AddDebugItem(list, data.NextSlotCoverageLabel, "Next Slot Coverage", slotCoverage);
            AddDebugItem(list, "产车目标", "Spawn Target", spawnTarget);
            AddDebugItem(list, "全程用时", "Route Duration", data.RouteDurationText);
            AddDebugItem(list, "圈时缓存", "Lap Cache", data.LapCacheText);
            AddDebugItem(list, data.DispatchCacheLabel, "Dispatch Cache", data.DispatchCacheText);
            AddDebugItem(list, "真实产车命令", "Spawn Command", data.SpawnTriggerSummary);
            AddDebugItem(list, "新车注册", "Vehicle Register", data.RegisterSummary);
            AddDebugItem(list, "到站候车", "Arrival Holding", data.HoldingSummary);
            AddDebugItem(list, "出库用时", "Dispatch Sample", data.DispatchSampleSummary);
            AddDebugItem(list, "关键异常", "Alerts", alertText);
        }

        public void FillLineCard(
            LineSelectData data,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            FillLineSummary(data, out summaryLabel, out summaryValue);

            meta1 = "Line: #" + data.Line.Index;
            meta2 = data.IsManagedLine
                ? "Route: " + data.RouteDurationText + " / Managed: " + data.ManagedText
                : "Route: - / Managed: " + data.ManagedText;
            meta3 = data.IsManagedLine
                ? (data.HasWaypointData
                    ? (data.IsChineseLocale ? "出库：" : "Dispatch: ") + data.DispatchCacheText
                    : (data.IsChineseLocale ? "出库：- / 路点缺失" : "Dispatch: - / Waypoints missing"))
                : (data.IsChineseLocale ? "发车：官方调度" : "Dispatch: official dispatch");
            alertText = FormatLegacyAlertText(data.CardAlertText, data.OfficialDispatchValue);
        }

        public void FillVehicleInfo(VehicleSelectData data, InfoList list)
        {
            AddDebugItem(list, "车辆", "Vehicle", data.Vehicle.Index.ToString());
            AddDebugItem(list, "状态", "State", data.StateText);
            AddDebugItem(list, "所属线路", "Line", data.Line != Entity.Null ? data.Line.Index.ToString() : "-");
            AddDebugItem(list, "目标班次", "Target Slot", data.TargetText);
            AddDebugItem(list, "当前班次", "Current Slot", data.CurrentText);
            AddDebugItem(list, "到始发ETA", "ETA To Origin", data.EtaValue);
            AddDebugItem(list, "运行进度", "Progress", data.ProgressValue);
            AddDebugItem(list, "关键异常", "Alerts", FormatLegacyAlertText(data.AlertText, data.IsChineseLocale ? "官方调度" : "Official dispatch"));
            AddDebugItem(list, "可用控制", "Controls", "Retire and Re-evaluate are wired in backend");
        }

        public SelectPanel.Snapshot BuildLineSnapshot(LineSelectData data)
        {
            return new SelectPanel.Snapshot
            {
                Mode = "line",
                EntityId = data.Line.Index.ToString(),
                PrimaryLabelKey = data.IsManagedLine ? "nextSlot" : "dispatch",
                PrimaryValue = data.IsManagedLine ? data.NextSlotText : "officialDispatch",
                PrimaryValueKind = data.IsManagedLine ? "slot" : "key",
                Detail1LabelKey = "lineName",
                Detail1Value = string.IsNullOrEmpty(data.LineDisplayName) ? "-" : data.LineDisplayName,
                Detail2LabelKey = "routeDuration",
                Detail2Value = data.RouteDurationText,
                NextPlannedArrivalMinute = -1,
                PlannedArrivalMinute = -1,
                ActualArrivalMinute = -1,
                PlannedDepartureMinute = -1,
                AlertText = data.AlertText,
                ShowLineSpawnAction = data.IsManagedLine && BuildFlavor.DebugTools,
                ShowDumpTrackModelAction = BuildFlavor.DebugTools,
                ShowDumpPlannerInputAction = BuildFlavor.DebugTools,
                ShowDumpObservationAction = BuildFlavor.DebugTools,
                ShowDumpStationAnchorObservationAction = BuildFlavor.DebugTools,
                ShowBypassStationToggle = data.ShowBypassStationToggle,
                BypassStationChecked = data.BypassStationChecked
            };
        }

        public SelectPanel.Snapshot BuildVehicleSnapshot(VehicleSelectData data)
        {
            return new SelectPanel.Snapshot
            {
                Mode = "vehicle",
                EntityId = data.Vehicle.Index.ToString(),
                PrimaryLabelKey = "state",
                PrimaryValue = data.StateText,
                PrimaryValueKind = "state",
                Detail1LabelKey = "control",
                Detail1Value = data.IsManagedVehicle ? "controlActive" : "controlInactive",
                Detail2LabelKey = "currentStation",
                Detail2Value = string.IsNullOrEmpty(data.CurrentStationName) ? "-" : data.CurrentStationName,
                Detail3LabelKey = "nextStopStation",
                Detail3Value = string.IsNullOrEmpty(data.NextStopStationName) ? "-" : data.NextStopStationName,
                Detail4LabelKey = "nextStation",
                Detail4Value = string.IsNullOrEmpty(data.NextPhysicalStationName) ? "-" : data.NextPhysicalStationName,
                Detail5LabelKey = "currentSlot",
                Detail5Value = data.CurrentText,
                Detail6LabelKey = "targetSlot",
                Detail6Value = data.TargetText,
                Detail7LabelKey = "stopDwell",
                Detail7Value = data.StopDwellValue,
                Detail8LabelKey = string.Empty,
                Detail8Value = string.Empty,
                NextPlannedArrivalMinute = data.NextPlannedArrivalMinute,
                PlannedArrivalMinute = data.PlannedArrivalMinute,
                ActualArrivalMinute = data.ActualArrivalMinute,
                PlannedDepartureMinute = data.PlannedDepartureMinute,
                AlertText = data.AlertText,
                ShowRetireAction = data.IsManagedVehicle,
                ShowForceDepartAction = data.IsManagedVehicle,
                ShowReevaluateAction = data.IsManagedVehicle && BuildFlavor.DebugTools
            };
        }

        public SelectPanel.Snapshot BuildVehiclePanelSnapshot(VehicleSelectData data)
        {
            if (!data.IsManagedVehicle)
            {
                return new SelectPanel.Snapshot
                {
                    Mode = "vehicle",
                    EntityId = data.Vehicle.Index.ToString(),
                    PrimaryLabelKey = "state",
                    PrimaryValue = "vanillaControl",
                    PrimaryValueKind = "key",
                    IsManagedVehicle = false
                };
            }

            bool showCurrentStop = data.HasStopSession
                && !string.IsNullOrWhiteSpace(data.CurrentStationName);
            bool showSchedule = data.CurrentMinute >= 0 || data.TargetMinute >= 0;
            return new SelectPanel.Snapshot
            {
                Mode = "vehicle",
                EntityId = data.Vehicle.Index.ToString(),
                PrimaryLabelKey = "state",
                PrimaryValue = data.StateText,
                PrimaryValueKind = "state",
                IsManagedVehicle = true,
                ShowCurrentStop = showCurrentStop,
                CurrentStationName = showCurrentStop ? data.CurrentStationName : string.Empty,
                StopDwellValue = showCurrentStop ? data.StopDwellValue : string.Empty,
                NextPassStationName = data.NextPassStationName ?? string.Empty,
                NextStopStationName = data.NextStopStationName ?? string.Empty,
                NextPlannedArrivalMinute = data.NextPlannedArrivalMinute,
                PlannedArrivalMinute = showCurrentStop ? data.PlannedArrivalMinute : -1,
                ActualArrivalMinute = showCurrentStop ? data.ActualArrivalMinute : -1,
                PlannedDepartureMinute = showCurrentStop ? data.PlannedDepartureMinute : -1,
                ShowSchedule = showSchedule,
                CurrentSlotText = data.CurrentMinute >= 0 ? data.CurrentText : string.Empty,
                TargetSlotText = data.TargetMinute >= 0 ? data.TargetText : string.Empty,
                ShowWaitingForFastTrain = data.WaitingForFastTrain,
                WaitingForFastTrainVehicleId = data.WaitingForFastTrainVehicleId,
                ShowRetireAction = true,
                ShowForceDepartAction = true,
                ShowReevaluateAction = BuildFlavor.DebugTools
            };
        }

        public void FillVehicleCard(
            VehicleSelectData data,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            FillVehicleSummary(data, out summaryLabel, out summaryValue);
            string lineStr = data.Line != Entity.Null ? "#" + data.Line.Index : "-";

            meta1 = "Line: " + lineStr + " / Managed: " + data.ManagedText;
            meta2 = data.IsManagedVehicle
                ? "Slot: " + data.CurrentText + " -> " + data.TargetText
                : "Native: " + data.NativeStateText;
            meta3 = data.IsChineseLocale
                ? "停站计时：" + data.StopDwellValue + " / 入站时间：" + data.InboundTimeValue
                : "Stop dwell: " + data.StopDwellValue + " / Inbound: " + data.InboundTimeValue;
            alertText = data.IsManagedVehicle
                ? FormatLegacyAlertText(data.AlertText, data.IsChineseLocale ? "官方调度" : "Official dispatch")
                : (data.Line != Entity.Null ? "Using native route fallback" : "Vehicle is not currently tracked by RapidTransit");
        }

        private static string FormatLegacyAlertText(string alertText, string officialDispatchValue)
        {
            if (alertText == "official-dispatch")
                return officialDispatchValue;

            return alertText;
        }

        private static void AddDebugItem(InfoList list, string labelCn, string labelEn, string value)
        {
            list.Add(new InfoList.Item(labelCn + " / " + labelEn + ": " + value));
        }
    }
}
