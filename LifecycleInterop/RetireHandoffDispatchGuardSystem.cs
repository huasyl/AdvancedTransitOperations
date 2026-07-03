using Game;
using Game.SceneFlow;
using Game.Simulation;
using Unity.Entities;

namespace RapidTransitMod
{
    public sealed partial class RetireHandoffDispatchGuardSystem : GameSystemBase
    {
        private SimulationSystem m_SimulationSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance.gameMode != GameMode.Game)
                return;

            LifecyclePort lifecycle = LifecyclePort.Current;
            RetireGuardPort retireGuard = lifecycle != null ? lifecycle.RetireGuard : null;
            if (retireGuard == null)
                return;

            retireGuard.Guard(m_SimulationSystem.frameIndex);
        }
    }
}
