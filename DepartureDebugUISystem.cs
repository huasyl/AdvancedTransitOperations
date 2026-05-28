using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Debug;
using Game.UI.InGame;
using Unity.Entities;

namespace RapidTransitMod
{
    public class DepartureDebugUISystem : UISystemBase
    {
        private static bool s_LoggedRegistered = false;
        private static bool s_LoggedLineDisplay = false;
        private static bool s_LoggedVehicleDisplay = false;
        private static bool s_LoggedUpdateInfo = false;

        private SelectedInfoUISystem m_SelectedInfoUISystem = null!;
        private DebugUISystem m_DebugUISystem = null!;
        private InfoList m_DebugInfo = null!;
        private bool m_Registered = false;

        public override GameMode gameMode => GameMode.Game;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_DebugUISystem = World.GetOrCreateSystemManaged<DebugUISystem>();
            m_DebugInfo = new InfoList(ShouldDisplay, UpdateInfo);
            m_DebugInfo.label = "RapidTransit 调试 / Debug";
        }

        protected override void OnUpdate()
        {
            m_DebugUISystem.developerInfoVisible = true;
            if (m_Registered) return;
            m_SelectedInfoUISystem.AddDeveloperInfo(m_DebugInfo);
            m_Registered = true;
            if (!s_LoggedRegistered)
            {
                Mod.log.Info("[DebugUI] developer subsection registered");
                s_LoggedRegistered = true;
            }
        }

        private static bool ShouldDisplay(Entity entity, Entity prefab)
        {
            if (DispatchRuntimeSystem.Instance == null) return false;
            bool display = DispatchRuntimeSystem.Instance.DisplayDebugFor(entity, prefab);
            if (display && !s_LoggedVehicleDisplay && DispatchRuntimeSystem.Instance.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(entity))
            {
                Mod.log.Info("[DebugUI] ShouldDisplay vehicle entity=" + entity.Index + " prefab=" + prefab.Index);
                s_LoggedVehicleDisplay = true;
            }
            if (display && !s_LoggedLineDisplay && DispatchRuntimeSystem.Instance.EntityManager.HasComponent<Game.Routes.TransportLine>(entity))
            {
                Mod.log.Info("[DebugUI] ShouldDisplay line entity=" + entity.Index + " prefab=" + prefab.Index);
                s_LoggedLineDisplay = true;
            }
            return display;
        }

        private static void UpdateInfo(Entity entity, Entity prefab, InfoList list)
        {
            if (DispatchRuntimeSystem.Instance == null) return;
            if (!s_LoggedUpdateInfo)
            {
                Mod.log.Info("[DebugUI] UpdateInfo entity=" + entity.Index + " prefab=" + prefab.Index);
                s_LoggedUpdateInfo = true;
            }
            DispatchRuntimeSystem.Instance.FillDebugInfo(entity, list);
        }
    }
}
