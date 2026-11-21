using System.Collections.Generic;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleCleanup
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly LineSpawnControl m_LineSpawnControl;
        private readonly List<Entity> m_DeadVehicles = new List<Entity>();

        public RuntimeVehicleCleanup(
            ModRuntimeHostSystem runtime,
            LineSpawnControl lineSpawnControl)
        {
            m_Runtime = runtime;
            m_LineSpawnControl = lineSpawnControl;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public void Tick()
        {
            m_DeadVehicles.Clear();
            foreach (var kv in m_Runtime.m_VehicleStateStore.State)
            {
                if (!EntityManager.Exists(kv.Key)) m_DeadVehicles.Add(kv.Key);
            }
            HashSet<Entity> removedLines = null;
            bool removedRail = false;
            foreach (Entity dead in m_DeadVehicles)
            {
                if (m_Runtime.m_VehicleView.TryGetState(dead, out VehicleState removedState)
                    && removedState == VehicleState.Preparing)
                {
                    Entity mappedLine = m_Runtime.m_VehicleView.TryGetLine(dead, out Entity removedLine)
                        ? removedLine
                        : Entity.Null;
                    int removedTargetMin = m_Runtime.m_VehicleView.TryGetTarget(dead, out int removedTarget)
                        ? removedTarget
                        : -1;
                    int removedCachedWp = m_Runtime.m_CachedWpIdx.TryGetValue(dead, out int removedWp)
                        ? removedWp
                        : -1;
                    uint removedPrepAge = m_Runtime.m_VehicleView.TryGetPreparingStartFrame(dead, out uint removedPrepStart)
                        ? m_Runtime.m_SimulationSystem.frameIndex - removedPrepStart
                        : 0;
                    if (RtLog.VerboseEnabled)
                    {
                        log.Info("[PreparingRemoved] vehicle=" + dead.Index
                            + " line=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                            + " targetMin=" + (removedTargetMin >= 0 ? ModRuntimeHostSystem.SlotStr(removedTargetMin) : "-")
                            + " cachedWp=" + removedCachedWp
                            + " prepAgeFrames=" + removedPrepAge);
                    }
                }
                ExitContext context = m_Runtime.m_VehicleExit.Release(dead, ExitMode.Dead, "entity-removed");
                removedRail |= context.Lifecycle == LifecycleKind.Rail;
                if (context.Line != Entity.Null && context.State != VehicleState.Retiring)
                {
                    removedLines ??= new HashSet<Entity>();
                    removedLines.Add(context.Line);
                }
                log.Info("[VehicleCleanup] vehicle=" + dead.Index + " entity disappeared");
            }
            if (removedLines != null && removedLines.Count > 0)
            {
                m_LineSpawnControl.Recompute(removedLines);
            }
            if (removedRail)
                m_Runtime.m_WaypointIndex.Clear();
            if (m_DeadVehicles.Count > 0)
                m_Runtime.m_RouteProgress.Clear();
            if (removedRail)
                m_Runtime.TrackProjection.ClearLineRunningVehicleSnapshots();
            if (m_DeadVehicles.Count > 0 && RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                log.Info("[VehicleCleanupSummary] deadVehicles=" + m_DeadVehicles.Count
                    + " affectedLines=" + (removedLines != null ? removedLines.Count : 0)
                    + " clearedWaypointIndex=1"
                    + " clearedRouteProgress=1"
                    + " clearedLineRunningSnapshots=1");
            }
            m_DeadVehicles.Clear();
        }
    }
}
