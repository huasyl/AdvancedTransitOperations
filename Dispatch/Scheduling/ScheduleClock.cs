using Unity.Mathematics;
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

        public static bool Reached(int nowMinute, int targetMinute)
        {
            return ((nowMinute - targetMinute + 1440) % 1440) <= ModRuntimeHostSystem.SLOT_GRACE_MINUTES;
        }

        public static int PreviousSlot(int nowMinute)
        {
            return ((nowMinute / ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) * ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES) % 1440;
        }

        public static bool CurrentOrRecent(int nowMinute, int targetMinute)
        {
            return Reached(nowMinute, targetMinute) || CanLate(nowMinute, targetMinute);
        }

        public static int Overdue(int nowMinute, int targetMinute)
        {
            return (nowMinute - targetMinute + 1440) % 1440;
        }

        public static bool CanLate(int nowMinute, int targetMinute)
        {
            if (!LateEnabled())
                return false;

            int overdueMinutes = Overdue(nowMinute, targetMinute);
            int lateWindow = LateWindow();
            return overdueMinutes > ModRuntimeHostSystem.SLOT_GRACE_MINUTES && overdueMinutes <= lateWindow;
        }

        public static bool SoftExpired(int nowMinute, int targetMinute)
        {
            int overdueMinutes = Overdue(nowMinute, targetMinute);
            int releaseAfter = math.max(ModRuntimeHostSystem.SLOT_GRACE_MINUTES, LateWindow());
            return overdueMinutes > releaseAfter && overdueMinutes <= ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES;
        }

        public static bool HardExpired(int nowMinute, int targetMinute)
        {
            int overdueMinutes = Overdue(nowMinute, targetMinute);
            return overdueMinutes > ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES
                && overdueMinutes <= ModRuntimeHostSystem.SPAWN_LEAD_MINUTES + ModRuntimeHostSystem.SLOT_GRACE_MINUTES;
        }

        public static bool Expired(int nowMinute, int targetMinute)
        {
            int overdueMinutes = Overdue(nowMinute, targetMinute);
            return overdueMinutes > ModRuntimeHostSystem.SLOT_GRACE_MINUTES
                && overdueMinutes <= ModRuntimeHostSystem.SPAWN_LEAD_MINUTES + ModRuntimeHostSystem.SLOT_GRACE_MINUTES;
        }

        public static int Lead(int nowMinute, int targetMinute)
        {
            int overdueMinutes = Overdue(nowMinute, targetMinute);
            if (overdueMinutes <= ModRuntimeHostSystem.SLOT_GRACE_MINUTES)
                return 0;

            return MinutesUntil(nowMinute, targetMinute);
        }

        public static uint ReachFrames(ClockSnapshot clockSnapshot, int targetMinute)
        {
            return clockSnapshot.ToFramesCeil(Lead(clockSnapshot.NowMinute, targetMinute));
        }

        private static int LateWindow()
        {
            return math.clamp(ModRuntimeHostSystem.LATE_DISPATCH_WINDOW_MINUTES, 0, ModRuntimeHostSystem.SLOT_INTERVAL_MINUTES);
        }

        private static bool LateEnabled()
        {
            return LateWindow() > 0;
        }
    }
}
