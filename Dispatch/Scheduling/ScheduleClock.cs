using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal static class ScheduleClock
    {
        public static int NextSlot(int nowMin)
        {
            return ((nowMin / DispatchRuntimeSystem.SLOT_INTERVAL) + 1) * DispatchRuntimeSystem.SLOT_INTERVAL % 1440;
        }

        public static int MinutesUntil(int nowMin, int targetMin)
        {
            int diff = targetMin - nowMin;
            if (diff <= 0)
                diff += 1440;
            return diff;
        }

        public static bool Reached(int nowMin, int targetMin)
        {
            return ((nowMin - targetMin + 1440) % 1440) <= DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public static int PreviousSlot(int nowMin)
        {
            return ((nowMin / DispatchRuntimeSystem.SLOT_INTERVAL) * DispatchRuntimeSystem.SLOT_INTERVAL) % 1440;
        }

        public static bool CurrentOrRecent(int nowMin, int targetMin)
        {
            return Reached(nowMin, targetMin) || CanLate(nowMin, targetMin);
        }

        public static int Overdue(int nowMin, int targetMin)
        {
            return (nowMin - targetMin + 1440) % 1440;
        }

        public static bool CanLate(int nowMin, int targetMin)
        {
            if (!LateEnabled())
                return false;

            int overdue = Overdue(nowMin, targetMin);
            int lateWindow = LateWindow();
            return overdue > DispatchRuntimeSystem.SLOT_GRACE_MIN && overdue <= lateWindow;
        }

        public static bool SoftExpired(int nowMin, int targetMin)
        {
            int overdue = Overdue(nowMin, targetMin);
            int releaseAfter = math.max(DispatchRuntimeSystem.SLOT_GRACE_MIN, LateWindow());
            return overdue > releaseAfter && overdue <= DispatchRuntimeSystem.SLOT_INTERVAL;
        }

        public static bool HardExpired(int nowMin, int targetMin)
        {
            int overdue = Overdue(nowMin, targetMin);
            return overdue > DispatchRuntimeSystem.SLOT_INTERVAL
                && overdue <= DispatchRuntimeSystem.SPAWN_LEAD_MIN + DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public static bool Expired(int nowMin, int targetMin)
        {
            int overdue = Overdue(nowMin, targetMin);
            return overdue > DispatchRuntimeSystem.SLOT_GRACE_MIN
                && overdue <= DispatchRuntimeSystem.SPAWN_LEAD_MIN + DispatchRuntimeSystem.SLOT_GRACE_MIN;
        }

        public static int Lead(int nowMin, int targetMin)
        {
            int overdue = Overdue(nowMin, targetMin);
            if (overdue <= DispatchRuntimeSystem.SLOT_GRACE_MIN)
                return 0;

            return MinutesUntil(nowMin, targetMin);
        }

        public static float ReachFrames(int nowMin, int targetMin)
        {
            return Lead(nowMin, targetMin) * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
        }

        private static int LateWindow()
        {
            return math.clamp(DispatchRuntimeSystem.LATE_DISPATCH_WINDOW_MINUTES, 0, DispatchRuntimeSystem.SLOT_INTERVAL);
        }

        private static bool LateEnabled()
        {
            return LateWindow() > 0;
        }
    }
}
