using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleCleanup
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly LineSpawnControl m_LineSpawnControl;
        private readonly Action<Entity> m_ClearAssistLaunchPending;
        private readonly Action<StopFact> m_PublishStopFact;
        private readonly Action<Entity, int, StopControlResult> m_ApplyStopControl;
        private readonly List<Entity> m_DeadVehicles = new List<Entity>();

        public RuntimeVehicleCleanup(
            ModRuntimeHostSystem runtime,
            LineSpawnControl lineSpawnControl,
            Action<Entity> clearAssistLaunchPending)
        {
            m_Runtime = runtime;
            m_LineSpawnControl = lineSpawnControl;
            m_ClearAssistLaunchPending = clearAssistLaunchPending;
            m_PublishStopFact = runtime.PublishStopFact;
            m_ApplyStopControl = runtime.ApplyStopControl;
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
            Dictionary<Entity, int> removedCountByLine = null;
            foreach (Entity dead in m_DeadVehicles)
            {
                VehicleState deadState = m_Runtime.m_VehicleView.TryGetState(dead, out VehicleState removedState)
                    ? removedState
                    : default;
                Entity mappedLine = m_Runtime.m_VehicleView.TryGetLine(dead, out Entity removedLine)
                    ? removedLine
                    : Entity.Null;
                if (deadState == VehicleState.Preparing)
                {
                    int removedTargetMin = m_Runtime.m_VehicleView.TryGetTarget(dead, out int removedTarget)
                        ? removedTarget
                        : -1;
                    int removedCachedWp = m_Runtime.m_CachedWpIdx.TryGetValue(dead, out int removedWp)
                        ? removedWp
                        : -1;
                    uint removedPrepAge = m_Runtime.m_VehicleView.TryGetPreparing(dead, out uint removedPrepStart)
                        ? m_Runtime.m_SimulationSystem.frameIndex - removedPrepStart
                        : 0;
                    if (RtLog.VerboseEnabled)
                    {
                        log.Info("[PreparingRemoved] 车辆" + dead.Index
                            + " line=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                            + " targetMin=" + (removedTargetMin >= 0 ? ModRuntimeHostSystem.SlotStr(removedTargetMin) : "-")
                            + " cachedWp=" + removedCachedWp
                            + " prepAgeFrames=" + removedPrepAge);
                    }
                }
                m_Runtime.m_StationContextQuery.RemoveVehicle(dead);
                m_Runtime.m_CommandApplier.FlushRetireShadowSnapshots(dead, "entity-removed");
                m_Runtime.m_CommandApplier.ResetRetireShadowSnapshots(dead);
                StopCancelResult cancelledStop = m_Runtime.m_StopRuntime.CancelStopSession(
                    dead,
                    m_Runtime.m_SimulationSystem.frameIndex);
                if (cancelledStop.Exists)
                {
                    m_PublishStopFact(cancelledStop.Fact);
                    m_ApplyStopControl(dead, cancelledStop.Control.WaypointIndex, cancelledStop.Control);
                }
                m_Runtime.m_VehicleRegistry.Remove(dead);
                m_Runtime.m_ObsPersist.ClearLap(dead);
                m_Runtime.m_UICache.Remove(dead);
                m_Runtime.m_BoardingFirstFrameGuardState.Remove(dead);
                m_Runtime.m_StopRuntime.RemoveVehicle(dead);
                m_Runtime.m_CachedWpIdx.Remove(dead);
                m_Runtime.TrackProjection.ClearVehicle(dead);
                m_Runtime.m_WaypointIndex.Remove(dead);
                m_Runtime.m_RouteProgress.Remove(dead);
                m_Runtime.Bypass.ClearVehicle(dead);
                m_Runtime.TrackProjection.ClearVehicleProgressSuspect(dead, "vehicle-removed");
                m_Runtime.m_Observation.ClearForcedMidStop(dead);
                m_Runtime.m_CommandApplier.RemoveRetireHandoff(dead);
                m_Runtime.m_PreparingFixCooldownUntil.Remove(dead);
                m_ClearAssistLaunchPending(dead);
                m_Runtime.m_Observation.ClearDwellDeadlineCache(dead);
                m_Runtime.m_Observation.ClearDispatchEta(dead);
                m_Runtime.m_SpawnIntentTrace?.Remove(dead);
                m_Runtime.m_ObsPersist.ClearDwell(dead);
                m_Runtime.m_Observation.ClearVehicleSlices(dead);
                m_Runtime.m_Observation.ClearDebug(dead);
                m_Runtime.m_RuntimeLog.ClearVehicle(dead);
                List<Entity> affectedLocals = m_Runtime.Bypass.ForgetBlocker(dead);
                if (affectedLocals != null)
                {
                    for (int i = 0; i < affectedLocals.Count; i++)
                    {
                        m_Runtime.m_RuntimeFramePlan.AddStage(
                            affectedLocals[i],
                            RuntimeStageMask.Bypass);
                    }
                }
                if (mappedLine != Entity.Null && deadState != VehicleState.Retiring)
                {
                    removedCountByLine ??= new Dictionary<Entity, int>();
                    removedCountByLine[mappedLine] = removedCountByLine.TryGetValue(mappedLine, out int removedCount)
                        ? removedCount + 1
                        : 1;
                }
                log.Info("[清理] 车辆" + dead.Index + " 消失");
            }
            if (removedCountByLine != null && removedCountByLine.Count > 0)
            {
                m_LineSpawnControl.ApplyCleanupTargetReduction(removedCountByLine);
            }
            if (m_DeadVehicles.Count > 0)
                m_Runtime.m_WaypointIndex.Clear();
            if (m_DeadVehicles.Count > 0)
                m_Runtime.m_RouteProgress.Clear();
            if (m_DeadVehicles.Count > 0)
                m_Runtime.TrackProjection.ClearLineRunningVehicleSnapshots();
            if (m_DeadVehicles.Count > 0 && RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                log.Info("[VehicleCleanupSummary] deadVehicles=" + m_DeadVehicles.Count
                    + " affectedLines=" + (removedCountByLine != null ? removedCountByLine.Count : 0)
                    + " clearedWaypointIndex=1"
                    + " clearedRouteProgress=1"
                    + " clearedLineRunningSnapshots=1");
            }
            m_DeadVehicles.Clear();
        }
    }
}
