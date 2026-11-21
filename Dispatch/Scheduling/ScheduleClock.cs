using System;
using RapidTransitMod.Core;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal static class ScheduleClock
    {
        public static int NextSlot(int nowMinute)
        {
            return ((nowMinute / ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) + 1) * ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES % 1440;
        }

        public static int MinutesUntil(int nowMinute, int targetMinute)
        {
            int diff = targetMinute - nowMinute;
            if (diff <= 0)
                diff += 1440;
            return diff;
        }

        public static int Overdue(int nowMinute, int targetMinute)
        {
            return (nowMinute - targetMinute + 1440) % 1440;
        }

        public static SlotKey Occurrence(ClockSnapshot clock, int minute, int lookback)
        {
            double firstMinute = clock.NowMinuteExact - lookback;
            int dayOffset = (int)Math.Ceiling((firstMinute - minute) / 1440d);
            DateTime date = OffsetDate(clock.NowDate.Date, dayOffset, clock.DaysPerYear);
            return new SlotKey(date, minute, clock.DaysPerYear);
        }

        public static long GameDay(DateTime date, int daysPerYear)
        {
            return (date.Year - 1L) * daysPerYear + date.DayOfYear - 1;
        }

        public static DateTime OffsetDate(DateTime date, int offset, int daysPerYear)
        {
            long day = GameDay(date, daysPerYear) + offset;
            int year = (int)(day / daysPerYear) + 1;
            int dayOfYear = (int)(day % daysPerYear);
            return new DateTime(year, 1, 1).AddDays(dayOfYear);
        }

        public static double RelativeMinutes(ClockSnapshot clock, DateTime date, double minute)
        {
            return (GameDay(date, clock.DaysPerYear) - GameDay(clock.NowDate, clock.DaysPerYear))
                * 1440d + minute - clock.NowMinuteExact;
        }

        public static int DateKey(DateTime date)
        {
            return date.Year * 10000 + date.Month * 100 + date.Day;
        }

    }
}
