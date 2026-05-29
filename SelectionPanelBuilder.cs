using Game.UI.InGame;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SelectionPanelBuilder
    {
        public void FillLineSummary(SelectionPanelLineData data, out string summaryLabel, out string summaryValue)
        {
            summaryLabel = data.IsManagedLine
                ? data.NextSlotLabel
                : data.DispatchLabel;
            summaryValue = data.IsManagedLine
                ? data.NextSlotText
                : data.OfficialDispatchValue;
        }

        public void FillVehicleSummary(SelectionPanelVehicleData data, out string summaryLabel, out string summaryValue)
        {
            summaryLabel = "State";
            summaryValue = data.StateText;
        }

        public void FillLineInfo(SelectionPanelLineData data, InfoList list)
        {
            string slotCoverage = data.IsManagedLine
                ? ((data.TargetingNextSlot + data.OccupyingNextSlot) > 0 ? "Occupied" : "Gap")
                : data.OfficialDispatchValue;
            string spawnTarget = data.IsManagedLine ? data.SpawnPending.ToString() : "-";

            AddDebugItem(list, "线路", "Line", data.Line.Index.ToString());
            AddDebugItem(list, "当前时间", "Time", data.NowText);
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
            AddDebugItem(list, "圈时缓存", "Lap Cache", data.LapCacheText);
            AddDebugItem(list, data.DispatchCacheLabel, "Dispatch Cache", data.DispatchCacheText);
            AddDebugItem(list, "真实产车命令", "Spawn Command", data.SpawnTriggerSummary);
            AddDebugItem(list, "新车注册", "Vehicle Register", data.RegisterSummary);
            AddDebugItem(list, "到站候车", "Arrival Holding", data.HoldingSummary);
            AddDebugItem(list, "出库用时", "Dispatch Sample", data.DispatchSampleSummary);
            AddDebugItem(list, "关键异常", "Alerts", data.AlertText);
        }

        public void FillLineCard(
            SelectionPanelLineData data,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            FillLineSummary(data, out summaryLabel, out summaryValue);

            meta1 = "Time: " + data.NowText;
            meta2 = data.IsManagedLine
                ? "Lap: " + data.LapCacheText + " / Managed: " + data.ManagedText
                : "Lap: - / Managed: " + data.ManagedText;
            meta3 = data.IsManagedLine
                ? (data.HasWaypointData
                    ? (data.IsChineseLocale ? "出库：" : "Dispatch: ") + data.DispatchCacheText
                    : (data.IsChineseLocale ? "出库：- / 路点缺失" : "Dispatch: - / Waypoints missing"))
                : (data.IsChineseLocale ? "发车：官方调度" : "Dispatch: official dispatch");
            alertText = data.CardAlertText;
        }

        public void FillVehicleInfo(SelectionPanelVehicleData data, InfoList list)
        {
            AddDebugItem(list, "车辆", "Vehicle", data.Vehicle.Index.ToString());
            AddDebugItem(list, "状态", "State", data.StateText);
            AddDebugItem(list, "所属线路", "Line", data.Line != Entity.Null ? data.Line.Index.ToString() : "-");
            AddDebugItem(list, "目标班次", "Target Slot", data.TargetText);
            AddDebugItem(list, "当前班次", "Current Slot", data.CurrentText);
            AddDebugItem(list, "到始发ETA", "ETA To Origin", data.EtaValue);
            AddDebugItem(list, "运行进度", "Progress", data.ProgressValue);
            AddDebugItem(list, "关键异常", "Alerts", data.AlertText);
            AddDebugItem(list, "可用控制", "Controls", "Retire and Re-evaluate are wired in backend");
        }

        public DispatchRuntimeSystem.SelectedPanelSnapshot BuildLineSnapshot(SelectionPanelLineData data)
        {
            return new DispatchRuntimeSystem.SelectedPanelSnapshot
            {
                Mode = "line",
                EntityId = data.Line.Index.ToString(),
                PrimaryLabelKey = data.IsManagedLine ? "nextSlot" : "dispatch",
                PrimaryValue = data.IsManagedLine ? data.NextSlotText : data.OfficialDispatchValue,
                PrimaryValueKind = data.IsManagedLine ? "slot" : "text",
                Detail1LabelKey = data.IsChineseLocale ? "线路编号" : "Line ID",
                Detail1Value = data.Line.Index.ToString(),
                Detail2LabelKey = data.IsChineseLocale ? "当前时间" : "Time",
                Detail2Value = data.NowText,
                Detail3LabelKey = data.IsChineseLocale ? "真实产车命令" : "Spawn Command",
                Detail3Value = data.SpawnTriggerSummary,
                Detail4LabelKey = data.IsChineseLocale ? "新车注册" : "Vehicle Register",
                Detail4Value = data.RegisterSummary,
                Detail5LabelKey = data.IsChineseLocale ? "到站候车" : "Arrival Holding",
                Detail5Value = data.HoldingSummary,
                Detail6LabelKey = data.IsChineseLocale ? "出库用时" : "Dispatch Sample",
                Detail6Value = data.DispatchSampleSummary,
                Detail7LabelKey = data.IsChineseLocale ? "车辆概览" : "Fleet",
                Detail7Value = data.Total + " / " + data.Running + " / " + data.Holding,
                AlertText = data.AlertText,
                ShowLineSpawnAction = data.IsManagedLine,
                ShowDumpTrackModelAction = true,
                ShowDumpPlannerInputAction = true,
                ShowDumpRuntimeObservationAction = true,
                ShowDumpStationAnchorObservationAction = true,
                ShowBypassStationToggle = data.ShowBypassStationToggle,
                BypassStationChecked = data.BypassStationChecked
            };
        }

        public DispatchRuntimeSystem.SelectedPanelSnapshot BuildVehicleSnapshot(SelectionPanelVehicleData data)
        {
            return new DispatchRuntimeSystem.SelectedPanelSnapshot
            {
                Mode = "vehicle",
                EntityId = data.Vehicle.Index.ToString(),
                PrimaryLabelKey = "state",
                PrimaryValue = data.StateText,
                PrimaryValueKind = "state",
                Detail1LabelKey = "line",
                Detail1Value = data.Line != Entity.Null ? data.Line.Index.ToString() : "-",
                Detail2LabelKey = "progress",
                Detail2Value = data.ProgressValue,
                Detail3LabelKey = "currentSlot",
                Detail3Value = data.CurrentText,
                Detail4LabelKey = "targetSlot",
                Detail4Value = data.TargetText,
                Detail5LabelKey = "stopDwell",
                Detail5Value = data.StopDwellValue,
                Detail6LabelKey = "currentStation",
                Detail6Value = string.IsNullOrEmpty(data.CurrentStationName) ? "-" : data.CurrentStationName,
                Detail7LabelKey = "nextStation",
                Detail7Value = string.IsNullOrEmpty(data.NextStationName) ? "-" : data.NextStationName,
                Detail8LabelKey = "event",
                Detail8Value = data.EventValue,
                AlertText = data.AlertText,
                ShowRetireAction = data.IsManagedVehicle,
                ShowForceDepartAction = data.IsManagedVehicle
            };
        }

        public void FillVehicleCard(
            SelectionPanelVehicleData data,
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
                ? data.AlertText
                : (data.Line != Entity.Null ? "Using native route fallback" : "Vehicle is not currently tracked by RapidTransit");
        }

        private static void AddDebugItem(InfoList list, string labelCn, string labelEn, string value)
        {
            list.Add(new InfoList.Item(labelCn + " / " + labelEn + ": " + value));
        }
    }
}
