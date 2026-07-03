using System;
using System.IO;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerPort
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        internal PlannerPort(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal DispatchRuntimeSystem Runtime => m_Runtime;

        internal void Dump(PlannerExport export)
        {
            if (!RtLog.DebugToolsEnabled)
                return;

            try
            {
                string json = Workbenches.Json.Write(export.Load(ModeScope.DefaultWorkbench));
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-planner-input-latest.json");
                File.WriteAllText(filePath, json);
                Mod.log.Info("[PlannerInputDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[PlannerInputDump] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
