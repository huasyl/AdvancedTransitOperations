using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.UI.InGame;

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
            updateSystem.UpdateAt<DepartureControlSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RapidTransitPanelUISystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateBefore<DispatchWorkbenchUISystem>(SystemUpdatePhase.Rendering);

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
            log.Info("RapidTransitMod disposed.");
        }
    }
}
