using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class RuntimeVehicleCleanup
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly LineSpawnControl m_LineSpawnControl;
        private readonly Action<Entity> m_ClearAssistLaunchPending;

        public RuntimeVehicleCleanup(
            DispatchRuntimeSystem runtime,
            LineSpawnControl lineSpawnControl,
            Action<Entity> clearAssistLaunchPending)
        {
            m_Runtime = runtime;
            m_LineSpawnControl = lineSpawnControl;
            m_ClearAssistLaunchPending = clearAssistLaunchPending;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public void Tick()
        {
            NativeList<Entity> deadKeys = new NativeList<Entity>(Allocator.Temp);
            foreach (var kv in m_Runtime.m_VehicleStateStore.State)
            {
                if (!EntityManager.Exists(kv.Key)) deadKeys.Add(kv.Key);
            }
            Dictionary<Entity, int> removedCountByLine = null;
            foreach (Entity dead in deadKeys)
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
                            + " targetMin=" + (removedTargetMin >= 0 ? DispatchRuntimeSystem.SlotStr(removedTargetMin) : "-")
                            + " cachedWp=" + removedCachedWp
                            + " prepAgeFrames=" + removedPrepAge);
                    }
                }
                m_Runtime.m_Announcements.RemoveVehicle(dead);
                m_Runtime.m_StationContextQuery.RemoveVehicle(dead);
                m_Runtime.m_CommandApplier.FlushRetireShadowSnapshots(dead, "entity-removed");
                m_Runtime.m_CommandApplier.ResetRetireShadowSnapshots(dead);
                m_Runtime.m_VehicleRegistry.Remove(dead);
                m_Runtime.m_ObsPersist.ClearLap(dead);
                m_Runtime.m_UICache.Remove(dead);
                m_Runtime.m_VehicleLabels.Remove(dead);
                m_Runtime.m_LastEffectiveBoardingState.Remove(dead);
                m_Runtime.m_LastOfficialBoardingState.Remove(dead);
                m_Runtime.m_BoardingFirstFrameGuardState.Remove(dead);
                m_Runtime.m_StopSessionLine.Remove(dead);
                m_Runtime.m_StopSessionWaypointIndex.Remove(dead);
                m_Runtime.m_StopSessionArrivalFrame.Remove(dead);
                m_Runtime.m_StopSessionBoardingChangeCount.Remove(dead);
                m_Runtime.m_DeparturePendingSinceFrame.Remove(dead);
                m_Runtime.m_CachedWpIdx.Remove(dead);
                m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(dead);
                m_Runtime.TrackProjection.ClearVehicle(dead);
                m_Runtime.m_WaypointIndex.Remove(dead);
                m_Runtime.m_RouteProgress.Remove(dead);
                m_Runtime.m_BVMisfire.Remove(dead);
                m_Runtime.m_BVMisfireStartFrame.Remove(dead);
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
                m_Runtime.Bypass.ForgetBlocker(dead);
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
            if (deadKeys.Length > 0)
                m_Runtime.m_WaypointIndex.Clear();
            if (deadKeys.Length > 0)
                m_Runtime.m_RouteProgress.Clear();
            if (deadKeys.Length > 0)
                m_Runtime.TrackProjection.ClearLineRunningVehicleSnapshots();
            if (deadKeys.Length > 0 && RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                log.Info("[VehicleCleanupSummary] deadVehicles=" + deadKeys.Length
                    + " affectedLines=" + (removedCountByLine != null ? removedCountByLine.Count : 0)
                    + " clearedWaypointIndex=1"
                    + " clearedRouteProgress=1"
                    + " clearedLineRunningSnapshots=1");
            }
            deadKeys.Dispose();
        }
    }
}
