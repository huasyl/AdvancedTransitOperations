using System;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal readonly struct TraceEvent
    {
        public readonly Entity Line;
        public readonly Entity Vehicle;
        public readonly int Trip;
        public readonly string Event;
        public readonly string StopName;
        public readonly Entity Stop;
        public readonly int Waypoint;
        public readonly string Time;
        public readonly int StopCount;
        public readonly bool Origin;

        public TraceEvent(
            Entity line,
            Entity vehicle,
            int trip,
            string evt,
            string stopName,
            Entity stop,
            int waypoint,
            string time,
            int stopCount,
            bool origin)
        {
            Line = line;
            Vehicle = vehicle;
            Trip = trip;
            Event = evt ?? string.Empty;
            StopName = stopName ?? string.Empty;
            Stop = stop;
            Waypoint = waypoint;
            Time = time ?? string.Empty;
            StopCount = stopCount;
            Origin = origin;
        }
    }

    internal static class TraceLog
    {
        internal static void Write(Action<string> log, TraceEvent evt)
        {
            if (log == null || !RtLog.VerboseEnabled)
                return;

            log(
                "[TripTrace] line="
                + evt.Line.Index
                + " vehicle="
                + evt.Vehicle.Index
                + " trip="
                + evt.Trip
                + " event="
                + evt.Event
                + " stop=\""
                + evt.StopName
                + "\" stopEntity="
                + evt.Stop.Index
                + " wp="
                + evt.Waypoint
                + " time="
                + evt.Time
                + " stopCount="
                + evt.StopCount
                + " origin="
                + (evt.Origin ? "1" : "0"));
        }
    }
}
