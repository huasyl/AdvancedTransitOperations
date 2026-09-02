using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal readonly struct ManagedSourceVehicle
    {
        internal readonly Entity Vehicle;
        internal readonly Entity Line;
        internal readonly VehicleState State;

        internal ManagedSourceVehicle(
            Entity vehicle,
            Entity line,
            VehicleState state)
        {
            Vehicle = vehicle;
            Line = line;
            State = state;
        }
    }
}
