using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal readonly struct ManagedSourceVehicle
    {
        internal readonly Entity Vehicle;
        internal readonly VehicleState State;

        internal ManagedSourceVehicle(Entity vehicle, VehicleState state)
        {
            Vehicle = vehicle;
            State = state;
        }
    }
}
