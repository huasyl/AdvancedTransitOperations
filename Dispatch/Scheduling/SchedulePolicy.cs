using System;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class SchedulePolicy
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Func<Entity, bool> m_Managed;
        private readonly Func<Entity, int[]> m_Times;
        private readonly Func<Entity, int> m_Hold;
        private readonly Func<Entity, float> m_ReadDispatchCache;

        public SchedulePolicy(
            ModRuntimeHostSystem runtime,
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

        public bool ShouldRetire(Entity line, int nowMinute, int targetMinute)
        {
            if (line == Entity.Null || !m_Managed(line) || targetMinute < 0)
                return false;
            if (ScheduleClock.CurrentOrRecent(nowMinute, targetMinute))
                return false;

            return ScheduleClock.MinutesUntil(nowMinute, targetMinute) > m_Hold(line);
        }

        public bool ShouldProtect(Entity line, Entity vehicle, int nowMinute, int nextTargetMinute)
        {
            if (m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute) && targetMinute >= 0)
            {
                if (ScheduleClock.CanLate(nowMinute, targetMinute))
                    return true;
                return ScheduleClock.MinutesUntil(nowMinute, targetMinute) <= ModRuntimeHostSystem.YIELD_PROTECT_MINUTES;
            }

            if (nextTargetMinute >= 0)
                return ScheduleClock.MinutesUntil(nowMinute, nextTargetMinute) <= ModRuntimeHostSystem.YIELD_PROTECT_MINUTES;

            return ScheduleClock.MinutesUntil(nowMinute, Fallback(line, nowMinute)) <= ModRuntimeHostSystem.YIELD_PROTECT_MINUTES;
        }

        public int Fallback(Entity line, int nowMinute)
        {
            if (line != Entity.Null && m_Managed(line))
            {
                int[] appliedTargets = m_Times(line);
                int nextManagedTargetMinute = ScheduleTargets.Next(nowMinute, appliedTargets);
                if (nextManagedTargetMinute >= 0)
                    return nextManagedTargetMinute;
            }

            return ScheduleClock.NextSlot(nowMinute);
        }

        public bool IsOccupied(Entity line, Entity vehicle, int targetMinute)
        {
            if (line == Entity.Null || targetMinute < 0)
                return false;

            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;

                if (m_Runtime.m_VehicleView.TryGetSlot(other, out int currentSlotMinute) && currentSlotMinute == targetMinute)
                    return true;

                if (m_Runtime.m_VehicleView.TryGetTarget(other, out int targetSlotMinute)
                    && targetSlotMinute == targetMinute
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
            if (m_Runtime.m_SpawnLeadTheory != null && m_Runtime.m_SpawnLeadTheory.TryRead(line, out float theoryFrames))
                return ClampSpawnLeadFrames(theoryFrames);
            float cachedFrames = m_ReadDispatchCache(line);
            if (cachedFrames > 0f)
                return ClampSpawnLeadFrames(cachedFrames);

            float estimateFrames = lineDurationFrames > 0f
                ? lineDurationFrames * 0.2f
                : ModRuntimeHostSystem.DISPATCH_ESTIMATE_DEFAULT_FRAMES;
            return ClampSpawnLeadFrames(estimateFrames);
        }

        public string SpawnLeadSource(Entity line)
        {
            if (m_Runtime.m_SpawnLeadTheory != null && m_Runtime.m_SpawnLeadTheory.TryRead(line, out _))
                return "rail-eta-theory";
            return m_ReadDispatchCache(line) > 0f ? "legacy-dispatch-cache" : "lap-duration-fallback";
        }

        public float SpawnBuffer(float spawnLeadFrames)
        {
            return spawnLeadFrames < ModRuntimeHostSystem.SPAWN_TRIGGER_BUFFER_THRESHOLD_FRAMES
                ? ModRuntimeHostSystem.SPAWN_TRIGGER_BUFFER_SHORT_FRAMES
                : ModRuntimeHostSystem.SPAWN_TRIGGER_BUFFER_LONG_FRAMES;
        }

        private static float ClampSpawnLeadFrames(float spawnLeadFrames)
        {
            return math.clamp(
                spawnLeadFrames,
                ModRuntimeHostSystem.DISPATCH_ESTIMATE_MIN_FRAMES,
                ModRuntimeHostSystem.DISPATCH_ESTIMATE_MAX_FRAMES);
        }
    }
}
