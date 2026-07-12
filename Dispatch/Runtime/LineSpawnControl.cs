using System.Collections.Generic;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class LineSpawnControl
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public LineSpawnControl(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public void Tick(int nowMin)
        {
            PuppetMasterControl(nowMin);
        }

        public void ApplyCleanupTargetReduction(Dictionary<Entity, int> removedCountByLine)
        {
            if (removedCountByLine == null || removedCountByLine.Count == 0)
                return;

            BufferLookup<RouteVehicle> cleanupRouteVehicleBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteModifier> cleanupModifierBuffers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            BufferLookup<RouteWaypoint> cleanupWaypointBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            foreach (KeyValuePair<Entity, int> removedEntry in removedCountByLine)
            {
                ApplyCleanupTargetReductionForLine(
                    removedEntry.Key,
                    removedEntry.Value,
                    cleanupRouteVehicleBuffers,
                    cleanupModifierBuffers,
                    cleanupWaypointBuffers);
            }
        }

        public void CaptureRetireTarget(
            Entity line,
            out int preActive,
            out bool hadSpawnTarget,
            out int oldSpawnTarget)
        {
            preActive = 0;
            hadSpawnTarget = false;
            oldSpawnTarget = 0;
            if (line == Entity.Null || !EntityManager.Exists(line))
                return;

            BufferLookup<RouteVehicle> routeVehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            preActive = m_Runtime.m_LineVehicles.Count(line, routeVehicles);
            hadSpawnTarget = m_Runtime.m_SpawningLines.TryGetValue(line, out oldSpawnTarget);
        }

        public void ApplyRetireTarget(
            Entity line,
            int preActive,
            bool hadSpawnTarget,
            int oldSpawnTarget)
        {
            if (line == Entity.Null || !EntityManager.Exists(line))
                return;

            BufferLookup<RouteVehicle> routeVehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteModifier> modifiers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            BufferLookup<RouteWaypoint> waypoints = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            int postActive = m_Runtime.m_LineVehicles.Count(line, routeVehicles);
            if (hadSpawnTarget && m_Runtime.m_SpawningLines.ContainsKey(line))
            {
                int plannedAdditional = math.max(0, oldSpawnTarget - preActive);
                m_Runtime.m_SpawningLines[line] = postActive + plannedAdditional;
            }

            ApplyPuppetMasterControlForLine(line, routeVehicles, modifiers, waypoints);
        }

        private void PuppetMasterControl(int nowMin)
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            BufferLookup<RouteModifier> modBuffers = m_Runtime.GetBufferLookup<RouteModifier>(false);
            BufferLookup<RouteWaypoint> wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);

            try
            {
                NativeArray<Entity> spawnKeys = m_Runtime.m_SpawningLines.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < spawnKeys.Length; i++)
                {
                    if (!EntityManager.Exists(spawnKeys[i]))
                    {
                        if (RtLog.VerboseEnabled)
                            log.Info("[PuppetMaster] 清理失效产车记录 线路" + spawnKeys[i].Index);
                        m_Runtime.m_SpawningLines.Remove(spawnKeys[i]);
                        m_Runtime.m_LineSpawnRequestFrame.Remove(spawnKeys[i]);
                        m_Runtime.m_LastSpawnBlockedLogFrame.Remove(spawnKeys[i]);
                    }
                }
                spawnKeys.Dispose();

                NativeArray<Entity> lineStableKeys = m_Runtime.m_LineProfile.StabilityKeys(Allocator.Temp);
                for (int i = 0; i < lineStableKeys.Length; i++)
                {
                    if (EntityManager.Exists(lineStableKeys[i])) continue;
                    m_Runtime.m_LineProfile.RemoveStability(lineStableKeys[i]);
                    m_Runtime.m_LineInitialAdopted.Remove(lineStableKeys[i]);
                    m_Runtime.TrackModel.InvalidateLine(lineStableKeys[i]);
                    m_Runtime.m_LineTimes.Clear();
                }
                lineStableKeys.Dispose();

                foreach (Entity line in lines)
                    ApplyPuppetMasterControlForLine(line, rvBuffers, modBuffers, wpBuffers);
            }
            finally { lines.Dispose(); }
        }

        private void ApplyPuppetMasterControlForLine(
            Entity line,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            if (!EntityManager.Exists(line)) return;
            if (!wpBuffers.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> wps) || wps.Length < 2) return;
            if (!m_Runtime.m_LineProfile.IsStable(line, wps)) return;
            if (!m_Runtime.m_LineView.Managed(line, m_Runtime.m_Features.Dispatch())) return;
            if (!EntityManager.HasComponent<PrefabRef>(line)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return;
            if (!modBuffers.TryGetBuffer(line, out DynamicBuffer<RouteModifier> mods)) return;

            float iDefault = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultVehicleInterval;
            float lineDuration = m_Runtime.m_LineTimes.Duration(line);
            if (lineDuration <= 0f) lineDuration = iDefault;
            if (lineDuration <= 0f) return;

            int actualCount = m_Runtime.m_LineVehicles.Count(line, rvBuffers);
            int targetCount = actualCount;

            if (m_Runtime.m_SpawningLines.TryGetValue(line, out int spawnTarget))
            {
                if (actualCount >= spawnTarget)
                {
                    m_Runtime.m_SpawningLines.Remove(line);
                    m_Runtime.m_LineSpawnRequestFrame.Remove(line);
                    if (RtLog.VerboseEnabled)
                        log.Info("[PuppetMaster] 线路" + line.Index + " 产车完成 actualCount=" + actualCount);
                }
                else
                {
                    targetCount = spawnTarget;
                }
            }

            float targetInterval = targetCount <= 0
                ? math.max(iDefault, lineDuration) * 64f
                : lineDuration / targetCount;

            InjectModifier(mods, targetInterval - iDefault);
        }

        private void ApplyCleanupTargetReductionForLine(
            Entity line,
            int removedCount,
            BufferLookup<RouteVehicle> rvBuffers,
            BufferLookup<RouteModifier> modBuffers,
            BufferLookup<RouteWaypoint> wpBuffers)
        {
            if (line == Entity.Null || !EntityManager.Exists(line) || removedCount <= 0)
                return;

            int actualCount = m_Runtime.m_LineVehicles.Count(line, rvBuffers);
            if (m_Runtime.m_SpawningLines.TryGetValue(line, out int spawnTarget))
            {
                int newSpawnTarget = math.max(0, spawnTarget - removedCount);
                if (newSpawnTarget <= actualCount)
                {
                    m_Runtime.m_SpawningLines.Remove(line);
                    m_Runtime.m_LineSpawnRequestFrame.Remove(line);
                    if (RtLog.VerboseEnabled)
                    {
                        log.Info("[CleanupTargetAdjust] 线路" + line.Index
                            + " 清理" + removedCount + "辆"
                            + " spawnTarget=" + spawnTarget + " -> -"
                            + " actualCount=" + actualCount);
                    }
                }
                else if (newSpawnTarget != spawnTarget)
                {
                    m_Runtime.m_SpawningLines[line] = newSpawnTarget;
                    if (RtLog.VerboseEnabled)
                    {
                        log.Info("[CleanupTargetAdjust] 线路" + line.Index
                            + " 清理" + removedCount + "辆"
                            + " spawnTarget=" + spawnTarget + " -> " + newSpawnTarget
                            + " actualCount=" + actualCount);
                    }
                }
            }

            ApplyPuppetMasterControlForLine(line, rvBuffers, modBuffers, wpBuffers);
        }

        private static void InjectModifier(DynamicBuffer<RouteModifier> mods, float delta)
        {
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(new RouteModifier { m_Delta = float2.zero });
            RouteModifier modifier = mods[idx];
            modifier.m_Delta = new float2(delta, 0f);
            mods[idx] = modifier;
        }
    }
}
