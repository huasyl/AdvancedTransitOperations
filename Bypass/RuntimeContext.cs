using Game.Vehicles;

namespace RapidTransitMod.Bypass
{
    internal interface IRuntimeContext : IBypassAdmissionRuntimeContext, IControlContext
    {
        bool RuntimeEnabled();
        void ClearLineTimeProfiles();
    }
}
