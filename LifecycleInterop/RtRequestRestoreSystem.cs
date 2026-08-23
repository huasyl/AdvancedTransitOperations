using Game;
using Game.Serialization;

namespace RapidTransitMod
{
    internal sealed partial class RtRequestRestoreSystem : GameSystemBase
    {
        private RtManagedVehicleRequestSystem m_RequestSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_RequestSystem = World.GetOrCreateSystemManaged<RtManagedVehicleRequestSystem>();
        }

        protected override void OnUpdate()
        {
            m_RequestSystem.RestoreAfterSave();
        }
    }
}
