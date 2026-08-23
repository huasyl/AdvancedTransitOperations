using System;
using RapidTransitMod.Dispatch.Lines;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineStopLayout
    {
        private readonly LineStop[] m_Stops;

        internal string StopSig { get; }
        internal int WaypointCount { get; }
        internal int StopCount => m_Stops.Length;

        private LineStopLayout(string stopSig, int waypointCount, LineStop[] stops)
        {
            StopSig = stopSig ?? string.Empty;
            WaypointCount = waypointCount;
            m_Stops = stops ?? Array.Empty<LineStop>();
        }

        internal LineStop this[int index] => m_Stops[index];

        internal static bool TryCreate(
            RoutePlan plan,
            Func<Unity.Entities.Entity, string> stopName,
            out LineStopLayout layout)
        {
            layout = null;
            if (plan == null
                || string.IsNullOrEmpty(plan.StopSig)
                || plan.Waypoints == null
                || plan.Stops == null
                || plan.Waypoints.Length == 0
                || plan.Stops.Length == 0)
            {
                return false;
            }

            LineStop[] stops = new LineStop[plan.Stops.Length];
            for (int i = 0; i < plan.Stops.Length; i++)
            {
                RouteStopRef stop = plan.Stops[i];
                if (string.IsNullOrEmpty(stop.StopKey)
                    || stop.WaypointIndex < 0
                    || stop.WaypointIndex >= plan.Waypoints.Length)
                {
                    return false;
                }

                stops[i] = new LineStop(
                    stop.StopKey,
                    stopName?.Invoke(stop.Stop),
                    stop.WaypointIndex);
            }

            layout = new LineStopLayout(plan.StopSig, plan.Waypoints.Length, stops);
            return true;
        }
    }

    internal readonly struct LineStop
    {
        internal readonly string StopKey;
        internal readonly string Name;
        internal readonly int WaypointIndex;

        internal LineStop(string stopKey, string name, int waypointIndex)
        {
            StopKey = stopKey ?? string.Empty;
            Name = name ?? string.Empty;
            WaypointIndex = waypointIndex;
        }

        internal LineStop(string stopKey, int waypointIndex)
            : this(stopKey, string.Empty, waypointIndex)
        {
        }
    }
}
