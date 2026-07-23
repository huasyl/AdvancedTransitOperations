#if RT_DEBUG_TOOLS
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RapidTransitMod.RailEtaHost
{
    public static class RailEtaHotDebugApi
    {
        private static RailEtaHotRuntime Runtime => ModRuntimeHostSystem.Instance?.m_RailEtaHotRuntime;

        public static Task<bool> ReloadRailEta(string dllPath)
            => Runtime?.ReloadAsync(dllPath) ?? Task.FromResult(false);

        public static Task<uint> InvokeRailEtaSmokeAsync()
            => Runtime?.SmokeAsync() ?? Task.FromResult(0u);

        public static bool EtaWorkerLost => RailEtaBridgeService.Current?.WorkerLost ?? false;
        public static bool WorkerLost => EtaWorkerLost;
        public static bool Available => Runtime != null;

        public static void RequestReloadLatest()
        {
            RailEtaHotRuntime runtime = Runtime;
            if (runtime == null || String.IsNullOrWhiteSpace(Mod.RootPath)) return;
            _ = runtime.ReloadLatestAsync(Path.Combine(Mod.RootPath, "Hot"));
        }

        public static void RequestSmoke()
        {
            RailEtaHotRuntime runtime = Runtime;
            if (runtime != null) _ = runtime.SmokeAsync();
        }

        public static bool RollbackRailEta() => Runtime?.Rollback() ?? false;

        public static Task<string> ExportRailEtaDebugAsync(RailEtaPublicTicket ticket, string filePath = null)
            => Task.FromException<string>(new InvalidOperationException("Detailed ETA diagnostics are owned by the hot module."));

        public static string StatusJson()
        {
            RailEtaHotRuntime runtime = Runtime;
            if (runtime == null) return string.Empty;
            RailEtaHotRuntime.StatusSnapshot status = runtime.Status;
            bool workerLost = EtaWorkerLost;
            string effectiveStatus = workerLost && String.Equals(status.LastAction, "rollback", StringComparison.Ordinal)
                && String.Equals(status.Status, "completed", StringComparison.Ordinal)
                ? "rolled-back-eta-worker-lost" : status.Status;
            StringBuilder sb = new StringBuilder(288);
            sb.Append('{');
            Append(sb, "busy", status.Busy ? "true" : "false", false);
            Append(sb, "currentSource", String.IsNullOrEmpty(status.CurrentBuildId) ? "unavailable" : "hot", true);
            Append(sb, "currentBuildId", status.CurrentBuildId, true);
            Append(sb, "generation", status.Generation.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "lastAction", status.LastAction, true);
            Append(sb, "status", effectiveStatus, true);
            Append(sb, "lastSmokeValue", status.LastSmokeValue.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "lastSmokeSummary", status.LastSmokeSummary, true);
            Append(sb, "loadedAssemblies", status.LoadedAssemblies.ToString(CultureInfo.InvariantCulture), false);
            Append(sb, "lastError", status.LastError, true);
            Append(sb, "hotBackendWorkerLost", "false", false);
            Append(sb, "etaWorkerLost", workerLost ? "true" : "false", false);
            Append(sb, "workerLost", workerLost ? "true" : "false", false);
            if (sb[sb.Length - 1] == ',') sb.Length--;
            return sb.Append('}').ToString();
        }

        private static void Append(StringBuilder sb, string name, string value, bool quote)
        {
            sb.Append('"').Append(name).Append("\":");
            if (quote) { sb.Append('"'); Escape(sb, value); sb.Append('"'); } else sb.Append(value);
            sb.Append(',');
        }

        private static void Escape(StringBuilder sb, string value)
        {
            for (int i = 0; i < (value ?? string.Empty).Length; i++)
            {
                char ch = value[i];
                if (ch == '\\' || ch == '"') sb.Append('\\').Append(ch);
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\n') sb.Append("\\n");
                else sb.Append(ch);
            }
        }
    }
}
#endif
