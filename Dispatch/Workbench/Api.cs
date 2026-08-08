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
