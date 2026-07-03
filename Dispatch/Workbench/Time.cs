using System;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class Time
    {
        internal static int Parse(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 5 || value[2] != ':')
                return -1;

            if (!int.TryParse(value.Substring(0, 2), out int hour))
                return -1;
            if (!int.TryParse(value.Substring(3, 2), out int minute))
                return -1;
            if (hour == 24 && minute == 0)
                return 1440;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
                return -1;

            return hour * 60 + minute;
        }

        internal static string Slot(int minute)
        {
            int normalized = ((minute % 1440) + 1440) % 1440;
            return (normalized / 60).ToString("00") + ":" + (normalized % 60).ToString("00");
        }

        internal static string Format(int totalMinutes)
        {
            return Slot(totalMinutes);
        }
    }
}
