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

        internal Dictionary<Entity, BypassEvent> ActiveBypass { get; } =
            new Dictionary<Entity, BypassEvent>();

        internal Dictionary<Entity, VehicleTrace> Vehicles { get; } =
            new Dictionary<Entity, VehicleTrace>();

        internal Dictionary<Entity, MonitorTrip> ActiveTrips { get; } =
            new Dictionary<Entity, MonitorTrip>();

        internal Dictionary<int, MonitorDateSlot> DateSlots { get; } =
            new Dictionary<int, MonitorDateSlot>();

        internal Dictionary<MonitorSlotKey, MonitorClaim> MonitorClaims { get; } =
            new Dictionary<MonitorSlotKey, MonitorClaim>();

        internal Dictionary<Entity, MonitorSlotKey> VehicleMonitorClaims { get; } =
            new Dictionary<Entity, MonitorSlotKey>();

        internal int MonitorCurrentDateKey { get; set; }

        internal bool MonitorOverflowed { get; set; }

        internal string MonitorOverflowReason { get; set; } = string.Empty;

        internal int MonitorOverflowCount { get; set; }

        internal string MonitorIssueCode { get; set; } = string.Empty;

        internal int MonitorIssueCount { get; set; }

        internal bool MonitorClaimsRestored { get; set; }

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
            ActiveTrips.Clear();
            DateSlots.Clear();
            MonitorClaims.Clear();
            VehicleMonitorClaims.Clear();
            MonitorCurrentDateKey = 0;
            MonitorOverflowed = false;
            MonitorOverflowReason = string.Empty;
            MonitorOverflowCount = 0;
            MonitorIssueCode = string.Empty;
            MonitorIssueCount = 0;
            MonitorClaimsRestored = false;
        }

        internal void ClearTraces()
        {
            Vehicles.Clear();
        }
    }

    internal readonly struct MonitorSlotKey : System.IEquatable<MonitorSlotKey>
    {
        internal readonly Entity Line;
        internal readonly int SlotMinute;
        internal readonly int ServiceDateKey;

        internal MonitorSlotKey(Entity line, int slotMinute, int serviceDateKey)
        {
            Line = line;
            SlotMinute = slotMinute;
            ServiceDateKey = serviceDateKey;
        }

        public bool Equals(MonitorSlotKey other) =>
            Line == other.Line
                && SlotMinute == other.SlotMinute
                && ServiceDateKey == other.ServiceDateKey;

        public override bool Equals(object obj) =>
            obj is MonitorSlotKey other && Equals(other);

        public override int GetHashCode() =>
            unchecked((Line.GetHashCode() * 397 ^ SlotMinute) * 397 ^ ServiceDateKey);
    }

    internal sealed class MonitorClaim
    {
        internal Entity Vehicle;
    }

    internal readonly struct MonitorClaimSeed
    {
        internal readonly Entity Vehicle;
        internal readonly Entity Line;
        internal readonly int SlotMinute;

        internal MonitorClaimSeed(Entity vehicle, Entity line, int slotMinute)
        {
            Vehicle = vehicle;
            Line = line;
            SlotMinute = slotMinute;
        }
    }

}
