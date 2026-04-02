using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using HarmonyLib;

namespace RapidTransitMod
{
    public sealed class TimedLogger
    {
        private readonly ILog m_Log;

        public TimedLogger(ILog log)
        {
            m_Log = log;
        }

        public void Info(string message)
        {
            m_Log.Info(Mod.PrefixWithGameTime(message));
        }
    }

    public class Mod : IMod
    {
        private static readonly ILog s_RawLog = LogManager.GetLogger(nameof(RapidTransitMod)).SetShowsErrorsInUI(false);
        private Harmony m_Harmony;
        public static TimedLogger log = new TimedLogger(s_RawLog);

        internal static string PrefixWithGameTime(string message)
        {
            string gameTime = DepartureControlSystem.Instance != null
                ? DepartureControlSystem.Instance.GetCurrentGameTimeLabel()
                : string.Empty;
            return gameTime.Length > 0 ? gameTime + " " + message : message;
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));
            try
            {
                m_Harmony = new Harmony("RapidTransitMod.BoardingClosePatch");
                m_Harmony.PatchAll(typeof(Mod).Assembly);
                log.Info("Boarding close patch initialized.");
            }
            catch (System.Exception ex)
            {
                m_Harmony = null;
                log.Info("Boarding close patch disabled: " + ex.GetType().Name + ": " + ex.Message);
            }
            updateSystem.UpdateAt<DepartureControlSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<DepotSourceLockSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<DepotSourceLockSystem, TransportDepotAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<DepotSourceLockSystem, TransportVehicleDispatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<DevSightRaycastCollectorSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateAfter<DevSightRaycastCollectorSystem, ToolRaycastSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateBefore<RapidTransitPanelUISystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<DispatchWorkbenchUISystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<DevSightTooltipSystem>(SystemUpdatePhase.UITooltip);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info("Module path: " + ((AssetData)asset).path);
                string modRootPath = Path.GetDirectoryName(((AssetData)asset).path);
                I18n.LoadAll(Path.Combine(modRootPath, "Locales"));
                DispatchWorkbenchEuisBridge.Initialize(modRootPath);
            }

            log.Info("RapidTransitMod initialized.");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            m_Harmony?.UnpatchAll("RapidTransitMod.BoardingClosePatch");
            m_Harmony = null;
            log.Info("RapidTransitMod disposed.");
        }
    }
}
