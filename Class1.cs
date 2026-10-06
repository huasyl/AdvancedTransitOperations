using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Modding;
using Game.Pathfind;
using Game.Routes;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using RapidTransitMod.Settings;
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
        private static readonly ILog s_RawLog = LogManager.GetLogger(nameof(RapidTransitMod)).SetShowsErrorsInUI(false);
        public static TimedLogger log = new TimedLogger(s_RawLog);
        public static AtoGameOptions Options { get; private set; } = null!;
        internal static string RootPath { get; private set; } = string.Empty;

        internal static string PrefixWithGameTime(string message)
        {
            string gameTime = string.Empty;
            try
            {
                ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
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
            Options = new AtoGameOptions(this);
#if RT_DEBUG_TOOLS
            updateSystem.UpdateAfter<Dispatch.Diagnostics.RuntimeProbeSystem>(SystemUpdatePhase.MainLoop);
#endif
            updateSystem.UpdateAfter<Dispatch.Runtime.LineChangeSourceSystem, RoutePathReadySystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAfter<LineServiceChangeSourceSystem, ModificationBarrier4>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateAfter<RailTravel.QuerySystem, PathfindSetupSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<Dispatch.Runtime.BoardingFirstFrameGuardSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<ModRuntimeHostSystem, TrainMoveSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PassengerFlow.SamplingSystem, ModRuntimeHostSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<PassengerFlow.SamplingSystem, SaveGameSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateBefore<PreSerialize<ModRuntimeHostSystem>>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateBefore<PreSerialize<PassengerFlow.SamplingSystem>>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateBefore<PreSerialize<RtManagedVehicleRequestSystem>, BeginPrefabSerializationSystem>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateAfter<RtRequestRestoreSystem, WriteSystem>(SystemUpdatePhase.Serialize);
            updateSystem.UpdateBefore<RtManagedVehicleRequestSystem, TransportVehicleDispatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RetireDispatchPreTrainAiQuarantineSystem, TransportTrainAISystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<RetireDispatchPostTrainAiRearmSystem, TransportVehicleDispatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<OriginArrivingStallRepairSystem, TrainNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<DepotSourceLockSystem, RtManagedVehicleRequestSystem>(SystemUpdatePhase.GameSimulation);
#if RT_DEBUG_TOOLS
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
                RootPath = modRootPath ?? string.Empty;
                I18n.LoadAll(Path.Combine(modRootPath, "Locales"));
                Workbenches.ApiHost.Init(modRootPath);
            }

            AssetDatabase.global.LoadSettings("AdvancedTransitOperations.AtoGameOptions", Options, userSetting: true);
            Options.RegisterInOptionsUI();

            World.DefaultGameObjectInjectionWorld
                .GetOrCreateSystemManaged<GamePanelUISystem>()
                .SetDefaultArgs(new DispatchWorkbenchNativePanel());
            log.Info("Registered dispatch workbench panel: " + typeof(DispatchWorkbenchNativePanel).FullName);

            log.Info("RapidTransitMod initialized.");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (Options != null)
            {
                Options.UnregisterInOptionsUI();
                Options = null!;
            }
            Workbenches.ApiHost.Dispose();
            log.Info("RapidTransitMod disposed.");
        }
    }
}
