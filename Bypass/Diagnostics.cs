using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal sealed partial class RuntimeFacade
    {
        internal void RemoveDiagnostics(Entity vehicle)
        {
            RemoveVehicleLogs(vehicle);
        }
    }
}
