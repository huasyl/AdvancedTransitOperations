using System;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class SchedulePolicy
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Func<Entity, bool> m_Managed;
        private readonly Func<Entity, int[]> m_Times;
        private readonly Func<Entity, int> m_Hold;
        private readonly Func<Entity, float> m_ReadDispatchCache;

        public SchedulePolicy(
            DispatchRuntimeSystem runtime,
            Func<Entity, bool> managed,
            Func<Entity, int[]> times,
            Func<Entity, int> hold,
            Func<Entity, float> readDispatchCache)
        {
            m_Runtime = runtime;
            m_Managed = managed;
            m_Times = times;
            m_Hold = hold;
            m_ReadDispatchCache = readDispatchCache;
        }

        public bool ShouldRetire(Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !m_Managed(line) || targetMin < 0)
                return false;
            if (ScheduleClock.CurrentOrRecent(nowMin, targetMin))
                return false;

            return ScheduleClock.MinutesUntil(nowMin, targetMin) > m_Hold(line);
        }

        public bool ShouldProtect(Entity line, Entity vehicle, int nowMin, int nextTargetMin)
        {
            if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin) && targetMin >= 0)
            {
                if (ScheduleClock.CanLate(nowMin, targetMin))
                    return true;
                return ScheduleClock.MinutesUntil(nowMin, targetMin) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;
            }

            if (nextTargetMin >= 0)
                return ScheduleClock.MinutesUntil(nowMin, nextTargetMin) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;

            return ScheduleClock.MinutesUntil(nowMin, Fallback(line, nowMin)) <= DispatchRuntimeSystem.YIELD_PROTECT_MINUTES;
        }

        public int Fallback(Entity line, int nowMin)
        {
            if (line != Entity.Null && m_Managed(line))
            {
                int[] appliedTargets = m_Times(line);
                int nextManagedTarget = ScheduleTargets.Next(nowMin, appliedTargets);
                if (nextManagedTarget >= 0)
                    return nextManagedTarget;
            }

            return ScheduleClock.NextSlot(nowMin);
        }

        public bool IsOccupied(Entity line, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || targetMin < 0)
                return false;

            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;

                if (m_Runtime.m_VehicleView.TryGetSlot(other, out int currentSlot) && currentSlot == targetMin)
                    return true;

                if (m_Runtime.m_VehicleView.TryGetTarget(other, out int targetSlot)
                    && targetSlot == targetMin
                    && m_Runtime.m_VehicleView.TryGetState(other, out VehicleState state)
                    && (state == VehicleState.Preparing || state == VehicleState.Holding || state == VehicleState.Idle))
                {
                    return true;
                }
            }

            return false;
        }

        public float SpawnLead(Entity line, float lineDurationFrames)
        {
            float cachedFrames = m_ReadDispatchCache(line);
            if (cachedFrames > 0f)
                return cachedFrames;

            float estimateMinutes = 0f;
            if (lineDurationFrames > 0f)
                estimateMinutes = (lineDurationFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE) * 0.2f;
            if (estimateMinutes <= 0f)
                estimateMinutes = 6f;

            estimateMinutes = math.clamp(
                estimateMinutes,
                DispatchRuntimeSystem.DISPATCH_ESTIMATE_MIN_MINUTES,
                DispatchRuntimeSystem.DISPATCH_ESTIMATE_MAX_MINUTES);
            return estimateMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
        }

        public float SpawnBuffer(float spawnLeadFrames)
        {
            float spawnLeadMinutes = spawnLeadFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            return spawnLeadMinutes < DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES
                ? DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_SHORT_MINUTES
                : DispatchRuntimeSystem.SPAWN_TRIGGER_BUFFER_LONG_MINUTES;
        }
    }
}
