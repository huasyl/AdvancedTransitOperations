using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private static int NextSlotMin(int nowMin)
            => ((nowMin / SLOT_INTERVAL) + 1) * SLOT_INTERVAL % 1440;

        private static int MinutesUntil(int nowMin, int targetMin)
        {
            int diff = targetMin - nowMin;
            if (diff <= 0)
                diff += 1440;
            return diff;
        }

        private static bool IsTimeReached(int nowMin, int targetMin)
            => ((nowMin - targetMin + 1440) % 1440) <= SLOT_GRACE_MIN;

        private static int GetEffectiveLateDispatchWindowMinutes()
            => math.clamp(LATE_DISPATCH_WINDOW_MINUTES, 0, SLOT_INTERVAL);

        private static bool LateDispatchEnabled()
            => GetEffectiveLateDispatchWindowMinutes() > 0;

        private static int GetPreviousSlotMin(int nowMin)
            => ((nowMin / SLOT_INTERVAL) * SLOT_INTERVAL) % 1440;

        private static bool IsCurrentOrRecentDispatchableSlot(int nowMin, int targetMin)
            => IsTimeReached(nowMin, targetMin) || CanLateDispatchSlot(nowMin, targetMin);

        private static int GetSlotOverdueMinutes(int nowMin, int targetMin)
            => (nowMin - targetMin + 1440) % 1440;

        private static bool CanLateDispatchSlot(int nowMin, int targetMin)
        {
            if (!LateDispatchEnabled())
                return false;

            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            int lateWindow = GetEffectiveLateDispatchWindowMinutes();
            return overdue > SLOT_GRACE_MIN && overdue <= lateWindow;
        }

        private static bool IsSlotSoftExpired(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            int releaseAfter = math.max(SLOT_GRACE_MIN, GetEffectiveLateDispatchWindowMinutes());
            return overdue > releaseAfter && overdue <= SLOT_INTERVAL;
        }

        private static bool IsSlotHardExpired(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            return overdue > SLOT_INTERVAL && overdue <= SPAWN_LEAD_MIN + SLOT_GRACE_MIN;
        }

        private static bool IsSlotExpired(int nowMin, int targetMin)
        {
            int overdue = (nowMin - targetMin + 1440) % 1440;
            return overdue > SLOT_GRACE_MIN && overdue <= SPAWN_LEAD_MIN + SLOT_GRACE_MIN;
        }

        private static string SlotStr(int min)
        {
            min = ((min % 1440) + 1440) % 1440;
            int h = min / 60 % 24;
            int m = min % 60;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

        private bool ShouldProtectIdleFromYield(Entity line, Entity v, int nowMin, int nextTargetMin = -1)
        {
            if (m_VehicleTargetMin.TryGetValue(v, out int targetMin) && targetMin >= 0)
            {
                if (CanLateDispatchSlot(nowMin, targetMin))
                    return true;
                return MinutesUntil(nowMin, targetMin) <= YIELD_PROTECT_MINUTES;
            }

            if (nextTargetMin >= 0)
                return MinutesUntil(nowMin, nextTargetMin) <= YIELD_PROTECT_MINUTES;

            return MinutesUntil(nowMin, GetFallbackProtectTarget(line, nowMin)) <= YIELD_PROTECT_MINUTES;
        }

        private int GetFallbackProtectTarget(Entity line, int nowMin)
        {
            if (line != Entity.Null && IsWorkbenchTimetableApplied(line))
            {
                int nextManagedTarget = GetNextManagedDispatchTarget(line, nowMin);
                if (nextManagedTarget >= 0)
                    return nextManagedTarget;
            }

            return NextSlotMin(nowMin);
        }

        private bool ShouldRetireWaitingVehicleForFarFutureTarget(Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line) || targetMin < 0)
                return false;
            if (IsCurrentOrRecentDispatchableSlot(nowMin, targetMin))
                return false;

            return MinutesUntil(nowMin, targetMin) > GetWorkbenchOriginHoldLimitMinutes(line);
        }

        private static int GetDispatchLeadMinutes(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            if (overdue <= SLOT_GRACE_MIN)
                return 0;

            return MinutesUntil(nowMin, targetMin);
        }

        private static int GetPreviousScheduledTargetMin(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int previous = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                int target = targets[i];
                if (target <= nowMin)
                    previous = target;
                else
                    break;
            }

            return previous >= 0 ? previous : targets[targets.Count - 1];
        }

        private static int GetNextScheduledTargetMin(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int bestTarget = targets[0];
            int bestDistance = MinutesUntil(nowMin, bestTarget);

            for (int i = 1; i < targets.Count; i++)
            {
                int target = targets[i];
                int distance = MinutesUntil(nowMin, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                }
            }

            return bestTarget;
        }

        private static int GetNextScheduledTargetIndex(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] >= nowMin)
                    return i;
            }

            return 0;
        }

        private static int GetScheduledHeadwayMinutes(IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count <= 1)
                return SLOT_INTERVAL;

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

            return bestGap < 1440 ? bestGap : SLOT_INTERVAL;
        }

        private int GetNextManagedDispatchTarget(Entity line, int nowMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return -1;

            int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(line);
            return GetNextScheduledTargetMin(nowMin, appliedTargets);
        }

        private bool IsDispatchTargetAlreadyOccupied(Entity line, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || targetMin < 0)
                return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == vehicle || !EntityManager.Exists(other))
                    continue;

                if (m_VehicleCurrentSlot.TryGetValue(other, out int currentSlot) && currentSlot == targetMin)
                    return true;

                if (m_VehicleTargetMin.TryGetValue(other, out int targetSlot)
                    && targetSlot == targetMin
                    && m_VehicleState.TryGetValue(other, out var otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Holding || otherState == VehicleState.Idle))
                {
                    return true;
                }
            }

            return false;
        }

        private float EstimateSpawnLeadFrames(Entity line, float lineDurationFrames)
        {
            float cachedFrames = ReadDispatchCache(line);
            if (cachedFrames > 0f)
                return cachedFrames;

            float estimateMinutes = 0f;
            if (lineDurationFrames > 0f)
                estimateMinutes = (lineDurationFrames / (float)SIM_FRAMES_PER_MINUTE) * 0.2f;
            if (estimateMinutes <= 0f)
                estimateMinutes = 6f;

            estimateMinutes = math.clamp(estimateMinutes, DISPATCH_ESTIMATE_MIN_MINUTES, DISPATCH_ESTIMATE_MAX_MINUTES);
            return estimateMinutes * (float)SIM_FRAMES_PER_MINUTE;
        }

        private float GetSpawnTriggerBufferMinutes(float spawnLeadFrames)
        {
            float spawnLeadMinutes = spawnLeadFrames / (float)SIM_FRAMES_PER_MINUTE;
            return spawnLeadMinutes < SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES
                ? SPAWN_TRIGGER_BUFFER_SHORT_MINUTES
                : SPAWN_TRIGGER_BUFFER_LONG_MINUTES;
        }

    }
}
