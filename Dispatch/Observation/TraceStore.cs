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

        internal Dictionary<Entity, RailSegmentSession> RailSegmentSessions { get; } =
            new Dictionary<Entity, RailSegmentSession>();

        internal Dictionary<RailSegmentKey, RailSegmentObservation> RailSegments { get; } =
            new Dictionary<RailSegmentKey, RailSegmentObservation>();

        internal Dictionary<Entity, MonitorTrip> ActiveTrips { get; } =
            new Dictionary<Entity, MonitorTrip>();

        internal Dictionary<int, MonitorDateSlot> DateSlots { get; } =
            new Dictionary<int, MonitorDateSlot>();

        internal int MonitorCurrentDateKey { get; set; }

        internal bool MonitorOverflowed { get; set; }

        internal string MonitorOverflowReason { get; set; } = string.Empty;

        internal int MonitorOverflowCount { get; set; }

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
            RailSegmentSessions.Clear();
            ActiveTrips.Clear();
            DateSlots.Clear();
            MonitorCurrentDateKey = 0;
            MonitorOverflowed = false;
            MonitorOverflowReason = string.Empty;
            MonitorOverflowCount = 0;
        }

        internal void ClearTraces()
        {
            Vehicles.Clear();
            RailSegments.Clear();
        }
    }

    internal sealed class RailSegmentSession
    {
        internal Entity Line;
        internal Entity FromWaypoint;
        internal Entity FromStop;
        internal uint StartFrame;
    }

    internal readonly struct RailSegmentKey : System.IEquatable<RailSegmentKey>
    {
        internal readonly Entity Line;
        internal readonly Entity FromWaypoint;
        internal readonly Entity FromStop;
        internal readonly Entity ToWaypoint;
        internal readonly Entity ToStop;

        internal RailSegmentKey(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop)
        {
            Line = line;
            FromWaypoint = fromWaypoint;
            FromStop = fromStop;
            ToWaypoint = toWaypoint;
            ToStop = toStop;
        }

        public bool Equals(RailSegmentKey other)
        {
            return Line == other.Line
                && FromWaypoint == other.FromWaypoint
                && FromStop == other.FromStop
                && ToWaypoint == other.ToWaypoint
                && ToStop == other.ToStop;
        }

        public override bool Equals(object obj) => obj is RailSegmentKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Line.GetHashCode();
                hash = hash * 397 ^ FromWaypoint.GetHashCode();
                hash = hash * 397 ^ FromStop.GetHashCode();
                hash = hash * 397 ^ ToWaypoint.GetHashCode();
                return hash * 397 ^ ToStop.GetHashCode();
            }
        }
    }

    internal sealed class RailSegmentObservation
    {
        internal float AverageFrames;
        internal int SampleCount;
        internal uint LastObservedFrame;
    }
}
