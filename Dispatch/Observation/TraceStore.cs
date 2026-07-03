using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class TraceStore
    {
        internal Session Session { get; set; }

        internal Dictionary<string, List<Trip>> BySlot { get; } =
            new Dictionary<string, List<Trip>>(System.StringComparer.Ordinal);

        internal Dictionary<Entity, List<Trip>> ByVehicle { get; } =
            new Dictionary<Entity, List<Trip>>();

        internal Dictionary<Entity, BypassEvent> ActiveBypass { get; } =
            new Dictionary<Entity, BypassEvent>();

        internal Dictionary<Entity, VehicleTrace> Vehicles { get; } =
            new Dictionary<Entity, VehicleTrace>();

        internal void Clear()
        {
            Session = null;
            ClearIndexes();
            ClearTraces();
        }

        internal void ClearIndexes()
        {
            BySlot.Clear();
            ByVehicle.Clear();
            ActiveBypass.Clear();
        }

        internal void ClearTraces()
        {
            Vehicles.Clear();
        }
    }
}
