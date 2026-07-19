using System.Collections.Generic;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal static class ScheduleTargets
    {
        public static int Previous(int nowMinute, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int previous = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                int target = targets[i];
                if (target <= nowMinute)
                    previous = target;
                else
                    break;
            }

            return previous >= 0 ? previous : targets[targets.Count - 1];
        }

        public static int Next(int nowMinute, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int bestTarget = targets[0];
            int bestDistance = ScheduleClock.MinutesUntil(nowMinute, bestTarget);
            for (int i = 1; i < targets.Count; i++)
            {
                int target = targets[i];
                int distance = ScheduleClock.MinutesUntil(nowMinute, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                }
            }

            return bestTarget;
        }

        public static int NextIndex(int nowMinute, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] >= nowMinute)
                    return i;
            }

            return 0;
        }

        public static int Headway(IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count <= 1)
                return DispatchRuntimeSystem.SLOT_INTERVAL_MINUTES;

            int bestGap = 1440;
            for (int i = 0; i < targets.Count; i++)
            {
                int current = targets[i];
                int next = targets[(i + 1) % targets.Count];
                int gap = (next - current + 1440) % 1440;
                if (gap <= 0)
                    continue;
                if (gap < bestGap)
                    bestGap = gap;
            }

            return bestGap < 1440 ? bestGap : DispatchRuntimeSystem.SLOT_INTERVAL_MINUTES;
        }
    }
}
