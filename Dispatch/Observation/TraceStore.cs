using System;
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

        internal Dictionary<Entity, VehicleTrace> Vehicles { get; } =
            new Dictionary<Entity, VehicleTrace>();

        internal Dictionary<Entity, MonitorTrip> ActiveTrips { get; } =
            new Dictionary<Entity, MonitorTrip>();

        internal Dictionary<int, MonitorDateSlot> DateSlots { get; } =
            new Dictionary<int, MonitorDateSlot>();

        internal int MonitorCurrentDateKey { get; set; }

        internal string MonitorOverflowReason { get; set; } = string.Empty;

        internal string MonitorIssueCode { get; set; } = string.Empty;

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
            ActiveTrips.Clear();
            DateSlots.Clear();
            MonitorCurrentDateKey = 0;
            MonitorOverflowReason = string.Empty;
            MonitorIssueCode = string.Empty;
        }

        internal void ClearTraces()
        {
            Vehicles.Clear();
        }
    }

}
