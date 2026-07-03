using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal static class Trips
    {
        internal static bool Done(Trip trip)
        {
            return trip != null
                && (trip.LaunchFrame > 0
                    || trip.ActualMin >= 0
                    || string.Equals(trip.State, "departed", System.StringComparison.Ordinal));
        }

        internal static string SlotKey(Entity line, int targetMin)
        {
            return line.Index.ToString() + ":" + targetMin.ToString();
        }

        internal static void Trim<T>(List<T> list, int max)
        {
            if (list == null || max <= 0)
                return;

            while (list.Count > max)
            {
                list.RemoveAt(0);
            }
        }
    }
}
