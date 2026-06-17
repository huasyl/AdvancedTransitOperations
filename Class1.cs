using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.UI;
using Colossal;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using Unity.Entities;

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
            try
            {
                m_Log?.Info(Mod.PrefixWithGameTime(message));
            }
            catch
            {
            }
        }
    }

    public class Mod : IMod
    {
        public const string Id = "RapidTransitMod";
        private const int CohtmlDebuggerPort = 9444;
        private static readonly ILog s_RawLog = LogManager.GetLogger(nameof(RapidTransitMod)).SetShowsErrorsInUI(false);
        public static TimedLogger log = new TimedLogger(s_RawLog);

        internal static string PrefixWithGameTime(string message)
        {
            string gameTime = string.Empty;
            try
            {
                DispatchRuntimeSystem runtime = DispatchRuntimeSystem.Instance;
                if (runtime != null && runtime.m_SelectPanel != null)
                    gameTime = runtime.m_SelectPanel.CurrentGameTimeLabel();
            }
            catch
            {
                gameTime = string.Empty;
            }
            return gameTime.Length > 0 ? gameTime + " " + message : message;
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));
#if RT_DEBUG_TOOLS
            TryEnableCohtmlDebugger();
#endif
            updateSystem.UpdateAt<DispatchRuntimeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<Dispatch.Runtime.BoardingFirstFrameGuardSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Dispatch.Runtime.BoardingFirstFrameGuardSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<PassengerFlow.SamplingSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PassengerFlow.SamplingSystem, DispatchRuntimeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<PreSerialize<PassengerFlow.SamplingSystem>>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateAt<RtManagedVehicleRequestSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RtManagedVehicleRequestSystem, TransportLineSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RtManagedVehicleRequestSystem, DepotSourceLockSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RetireHandoffDispatchGuardSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<RetireHandoffDispatchGuardSystem, DispatchRuntimeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<RetireHandoffDispatchGuardSystem, TrainNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RetireHandoffDispatchGuardSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<OriginArrivingStallRepairSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<OriginArrivingStallRepairSystem, TrainNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<OriginArrivingStallRepairSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<DepotSourceLockSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<DepotSourceLockSystem, TransportDepotAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<DepotSourceLockSystem, TransportVehicleDispatchSystem>(SystemUpdatePhase.GameSimulation);
#if RT_DEBUG_TOOLS
            updateSystem.UpdateAt<DevSightRaycastCollectorSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateAfter<DevSightRaycastCollectorSystem, ToolRaycastSystem>(SystemUpdatePhase.Raycast);
#endif
            updateSystem.UpdateBefore<RapidTransitPanelUISystem>(SystemUpdatePhase.Rendering);
#if RT_DEBUG_TOOLS
            updateSystem.UpdateAt<DevSightTooltipSystem>(SystemUpdatePhase.UITooltip);
#endif

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info("Module path: " + ((AssetData)asset).path);
                string modRootPath = Path.GetDirectoryName(((AssetData)asset).path);
                I18n.LoadAll(Path.Combine(modRootPath, "Locales"));
                Workbenches.ApiHost.Init(modRootPath);
            }

            World.DefaultGameObjectInjectionWorld
                .GetOrCreateSystemManaged<GamePanelUISystem>()
                .SetDefaultArgs(new DispatchWorkbenchNativePanel());
            log.Info("Registered dispatch workbench panel: " + typeof(DispatchWorkbenchNativePanel).FullName);

            log.Info("RapidTransitMod initialized.");
        }

#if RT_DEBUG_TOOLS
        private static void TryEnableCohtmlDebugger()
        {
            try
            {
                UIManager manager = UIManager.instance;
                if (manager == null || manager.settings == null)
                {
                    log.Info("[CohtmlDebugger] UIManager settings unavailable.");
                    return;
                }

                bool previousEnabled = manager.settings.enableDebugger;
                int previousPort = manager.settings.debuggerPort;
                manager.settings.enableDebugger = true;
                manager.settings.debuggerPort = CohtmlDebuggerPort;
                log.Info("[CohtmlDebugger] requested enableDebugger=true debuggerPort=" + CohtmlDebuggerPort
                    + " previousEnabled=" + previousEnabled
                    + " previousPort=" + previousPort
                    + " currentEnabled=" + manager.settings.enableDebugger
                    + " currentPort=" + manager.settings.debuggerPort);
            }
            catch (System.Exception ex)
            {
                log.Info("[CohtmlDebugger] configure failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
#endif

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            Workbenches.ApiHost.Dispose();
            log.Info("RapidTransitMod disposed.");
        }
    }
}
