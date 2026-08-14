using System;
using RapidTransitMod.Workbenches;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class Api
    {
        private const string LegacyReadonlyMessage = "Legacy EUIS workbench is now read-only. Use the Dispatch Workbench schedule panel to edit and apply timetables.";

        internal static string Load(string requestJson)
        {
            string snapshotJson = ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Load(requestJson) ?? string.Empty;
            return snapshotJson;
        }

        internal static string Overview(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Overview(requestJson) ?? string.Empty;
        }

        internal static string Refresh(string requestJson)
        {
            string snapshotJson = ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Refresh(requestJson) ?? string.Empty;
            return snapshotJson;
        }

        internal static string Meta(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Meta(requestJson) ?? string.Empty;
        }

        internal static string Save(string requestJson)
        {
            string resultJson = ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Save(requestJson) ?? string.Empty;
            return resultJson;
        }

        internal static string HostState(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.SetHostState(requestJson) ?? string.Empty;
        }

        internal static string Start(string requestJson)
        {
            if (RtLog.VerboseEnabled)
                Mod.log.Info($"[WorkbenchSaveOperationBridge] startNativeSaveOperation length={requestJson?.Length ?? 0}");
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Start(requestJson) ?? string.Empty;
        }

        internal static string Status(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                if (RtLog.VerboseEnabled)
                    Mod.log.Info("[WorkbenchSaveOperationBridge] getNativeSaveOperationStatus empty id");
            }

            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Status(operationId) ?? string.Empty;
        }

        internal static string StartRunTime(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.StartRunTime(requestJson) ?? string.Empty;
        }

        internal static string RunTimeStatus(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.RunTimeStatus(requestJson) ?? string.Empty;
        }

        internal static string CancelRunTime(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.CancelRunTime(requestJson) ?? string.Empty;
        }

        internal static string CloseRunTimeEditor(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.CloseRunTimeEditor(requestJson) ?? string.Empty;
        }

        internal static string LoadTimetableLineLayout(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.LoadTimetableLineLayout(requestJson) ?? string.Empty;
        }

        internal static string SaveScheduleBatch(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.SaveScheduleBatch(requestJson) ?? string.Empty;
        }

        internal static string RunChartSections(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.RunChartSections(requestJson) ?? string.Empty;
        }

        internal static string RunChartStations(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.RunChartStations(requestJson) ?? string.Empty;
        }

        internal static string MonitorHeaders(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.MonitorHeaders(requestJson) ?? string.Empty;
        }

        internal static string MonitorDetail(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.MonitorDetail(requestJson) ?? string.Empty;
        }

        internal static string MonitorDetails(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.MonitorDetails(requestJson) ?? string.Empty;
        }

        internal static string MonitorAverageState(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.LoadMonitorAverageState(requestJson) ?? string.Empty;
        }

        internal static string QueryMonitorAverage(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.QueryMonitorAverage(requestJson) ?? string.Empty;
        }

        internal static string SetMonitorSubscription(string requestJson)
        {
            return ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.SetMonitorSubscription(requestJson) ?? string.Empty;
        }

        internal static string Legacy(string requestJson)
        {
            string resultJson = BuildLegacy();
            return resultJson;
        }

        private static string BuildLegacy()
        {
            string snapshotJson = ModRuntimeHostSystem.Instance?.m_WorkbenchBridge?.Refresh("{}") ?? string.Empty;
            DispatchWorkbenchSnapshot snapshot = Json.Read<DispatchWorkbenchSnapshot>(snapshotJson);
            DispatchWorkbenchSaveResult result = new DispatchWorkbenchSaveResult
            {
                success = false,
                errors = new[] { LegacyReadonlyMessage },
                warnings = Array.Empty<string>(),
                version = snapshot?.version ?? string.Empty,
                snapshot = snapshot,
                cleanupInfo = snapshot?.cleanupInfo
            };
            return Json.Write(result);
        }
    }
}
