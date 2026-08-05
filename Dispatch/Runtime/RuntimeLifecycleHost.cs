using System;
using Colossal.Serialization.Entities;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using Game.Vehicles;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RuntimeLifecycleHost
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public RuntimeLifecycleHost(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Loaded(Context serializationContext)
        {
            ClearTracking();
            m_Runtime.m_SimClock.ForceRefresh(m_Runtime.m_SimulationSystem.frameIndex);
            m_Runtime.m_StartupRuntimeStateCleared = true;

            // 阶段 B（前半，Reset 类）——保持原位，不动迁移语义。
            m_Runtime.m_SpawnIntentTrace?.Clear();
            m_Runtime.m_SpawnLeadTheory?.Clear();
            m_Runtime.m_RailEtaService?.ResetCity();
#if RT_DEBUG_TOOLS
            RailEtaHost.RailEtaHotDebugApi.RequestReloadLatest();
#endif
            m_Runtime.m_Observation.ClearDispatchEta();
            PassengerFlow.SamplingSystem.ClearState();

            // 阶段 A: ScanLineAnchors（在任何 Applied/draft 恢复前完成）。Scan 后映射冻结。
            try
            {
                if (RuntimeRoot.ScanLineAnchors(m_Runtime))
                {
                    m_Runtime.m_WorkbenchCatalogCache?.MarkDirty();
                    m_Runtime.m_LineView?.Clear();
                }
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[LineAnchorCatalog] Initial scan failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            // 阶段 B（后半，Reset 类）——保持原位，不动迁移语义。
            ResetCityBufferReadyFlags();
            m_Runtime.m_CommandApplier.ResetRetireDispatchLockStages();
            m_Runtime.m_CommandApplier.ProjectRetireDispatchLocksImmediatelyOnLoad();
            m_Runtime.m_SystemReady = false;
            m_Runtime.m_StartupRuntimeStateCleared = true;
            m_Runtime.m_StableFrameCount = 0;
            m_Runtime.m_LastVehicleCount = -1;
            m_Runtime.m_AnnouncementWorkbench.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Restore();
            m_Runtime.m_WorkbenchBridge.Reset();

            // 阶段 C: Applied buffer 恢复 vs 字符串 draft，按 buffer 是否存在分流。
            Entity city = m_Runtime.m_CitySystem.City;
            bool hasAppliedBuffer = city != Entity.Null
                && (m_Runtime.EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city)
                    || m_Runtime.EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city));
            if (hasAppliedBuffer)
            {
                try
                {
                    m_Runtime.m_WorkbenchBridge.Applied().Load();
                }
                catch (Exception ex)
                {
                    m_Runtime.log.Info("[Loaded] Applied.Load (buffer path) failed -> "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                try
                {
                    m_Runtime.m_WorkbenchBridge.Restore();
                }
                catch (Exception ex)
                {
                    m_Runtime.log.Info("[Loaded] Restore (buffer path) failed -> "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else
            {
                try
                {
                    m_Runtime.m_WorkbenchBridge.Restore();
                }
                catch (Exception ex)
                {
                    m_Runtime.log.Info("[Loaded] Restore (no-buffer path) failed -> "
                        + ex.GetType().Name + ": " + ex.Message);
                }
                try
                {
                    m_Runtime.m_WorkbenchBridge.Applied().Load();
                }
                catch (Exception ex)
                {
                    m_Runtime.log.Info("[Loaded] Applied.Load (no-buffer path, Backfill) failed -> "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // 阶段 D: store 迁移（Applied/LineConfig）。
            MigrationReport report = new MigrationReport();
            try
            {
                report = LineKeyMigration.MigrateStores(
                    m_Runtime.m_LineAnchorCatalog,
                    m_Runtime.m_WorkbenchBridge.AppliedStore,
                    m_Runtime.m_WorkbenchBridge.LineStore);
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[Loaded] MigrateStores failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // 阶段 E: 先恢复 PassengerFlow 持久化聚合，再运行各字符串域迁移。
            try
            {
                PassengerFlow.Persistence.RestoreFromCity(m_Runtime.EntityManager, m_Runtime.m_CitySystem.City);
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[PassengerFlowPersistence] Restore failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            try
            {
                LineKeyMigration.RunDomainMigrations(m_Runtime.m_LineAnchorCatalog, report);
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[Loaded] RunDomainMigrations failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // 阶段 F: 恢复 Applied 配置（LineConfigStore 已迁移到 stable）。
            try
            {
                m_Runtime.m_WorkbenchBridge.Applied().RefreshCfg();
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[Loaded] Applied.RefreshCfg failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            // 阶段 G: 清理 + 发布 + 保存。
            m_Runtime.m_Bypass.WarmStaticSceneIndex();
            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                int CountBuffer<T>() where T : unmanaged, IBufferElementData
                {
                    return city != Entity.Null && m_Runtime.EntityManager.HasBuffer<T>(city)
                        ? m_Runtime.EntityManager.GetBuffer<T>(city, true).Length
                        : 0;
                }

                m_Runtime.log.Info("[RuntimeLoadedSummary] city=" + city.Index
                    + " appliedLines=" + m_Runtime.m_WorkbenchBridge.Applied().Lines.Count
                    + " vehicleStateCache=" + CountBuffer<VehicleStateCacheElement>()
                    + " lineLapCache=" + CountBuffer<LineLapCacheElement>()
                    + " lineDispatchCache=" + CountBuffer<LineDispatchCacheElement>()
                    + " bypassStationSettings=" + CountBuffer<BypassStationSettingElement>()
                    + " stationDwellBuffer=" + CountBuffer<StationDwellObservationElement>()
                    + " sliceObservationBuffer=" + CountBuffer<TraversalSliceObservationElement>());
            }
            try
            {
                Workbenches.UiEvents.Push(m_Runtime.m_WorkbenchBridge.Build(m_Runtime.m_WorkbenchBridge.Drafts().Preferred()));
            }
            catch (Exception ex)
            {
                m_Runtime.m_WorkbenchBridge.Ui().Fault("OnGameLoaded.NotifyWorkbenchSnapshotChanged", ex);
            }

            // 阶段 H: 迁移汇总。
#if RT_VERBOSE_LOGS
            report.LogDetails(message => m_Runtime.log.Info(message));
#endif
            if (report.Count > 0)
            {
                m_Runtime.log.Info("[LineKeyMigration] summary: " + report.Summary());
            }
        }

        public void ClearAll()
        {
            m_Runtime.m_SpawnIntentTrace?.Clear();
            m_Runtime.m_SpawnLeadTheory?.Clear();
            m_Runtime.m_RailEtaService?.ResetCity();
            m_Runtime.m_Observation.ClearDispatchEta();
            PassengerFlow.SamplingSystem.ClearState();
            EntityCommandBuffer commandBuffer = m_Runtime.m_EndFrameBarrier.CreateCommandBuffer();
            NativeArray<Entity> entities = m_Runtime.m_AllPublicTransportQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in entities)
            {
                if (!m_Runtime.EntityManager.HasComponent<Game.Prefabs.PrefabData>(entity))
                    commandBuffer.AddComponent<Deleted>(entity);
            }

            entities.Dispose();
            m_Runtime.m_VehicleRegistry.Clear();
            m_Runtime.m_VehicleRegistrar.ClearPendingRebindCandidates();
            m_Runtime.m_VehicleRegistrar.ClearDisabledLineLateSpawnRetireQueue();
            m_Runtime.m_VehicleRegistrar.ClearStartupGate();
            m_Runtime.m_RailEventSource.ResetTracking();
            m_Runtime.m_ObsPersist.ClearLaps();
            m_Runtime.m_UICache.Clear();
            m_Runtime.m_VehicleLabels.Clear();
            m_Runtime.m_StopRuntimeState.ClearBoardingStates();
            m_Runtime.m_BoardingFirstFrameGuardState.Clear();
            m_Runtime.m_StopRuntimeState.ClearStopSessions();
            m_Runtime.m_StopRuntimeState.ClearInvalidatedRecovery();
            m_Runtime.m_CachedWpIdx.Clear();
            m_Runtime.m_StopRuntimeState.ClearForcedMidStopGrace();
            m_Runtime.m_CommandApplier.ClearRetireHandoffState();
            m_Runtime.m_PreparingFixCooldownUntil.Clear();
            m_Runtime.m_SpawningLines.Clear();
            m_Runtime.m_LineSpawnRequestFrame.Clear();
            m_Runtime.m_LastSpawnBlockedLogFrame.Clear();
            m_Runtime.m_LastScheduleDiagnosticLogFrame.Clear();
            m_Runtime.m_LineTimes.Clear();
            m_Runtime.m_LineProfile.ClearStability();
            ResetCityBufferReadyFlags();
            m_Runtime.m_Observation.ClearDwellDeadlineCache();
            m_Runtime.m_ObsPersist.ClearDwell();
            m_Runtime.m_DwellObservationBufferReady = false;
            m_Runtime.m_DwellObservationCacheLoaded = false;
            m_Runtime.m_StationDwellObservationBufferReady = false;
            m_Runtime.m_StationDwellObservationCacheLoaded = false;
            m_Runtime.m_Observation.ClearStationAnchorObservationDiagnosticsState();
            m_Runtime.m_ObsPersist.ClearSlices();
            m_Runtime.m_SliceAdmission.Clear();
            m_Runtime.m_Obs.Clear();
            m_Runtime.m_TraversalSliceObservationBufferReady = false;
            m_Runtime.m_TraversalSliceObservationCacheLoaded = false;
            m_Runtime.m_JustLaunched.Clear();
            m_Runtime.m_RuntimeEngine.ClearAssistLaunchPending();
            m_Runtime.m_Bypass.ClearAll();
            m_Runtime.m_TrackModel.InvalidateAll();
            m_Runtime.m_TrackProjection.Clear();
            m_Runtime.m_WaypointIndex.Clear();
            m_Runtime.m_RouteProgress.Clear();
            m_Runtime.m_SystemReady = false;
            m_Runtime.m_StartupRuntimeStateCleared = false;
            m_Runtime.m_StableFrameCount = 0;
            m_Runtime.m_LastVehicleCount = -1;
            m_Runtime.m_LastPuppetMasterMinute = -1;
            m_Runtime.m_LastRegisterSweepMinute = -1;
            m_Runtime.m_LastSchedulerTickMinute = -1;
            m_Runtime.m_SelectPanel.ClearDebugSummaries();
            m_Runtime.m_StationContextQuery.Clear();
            m_Runtime.m_RuntimeLog.Clear();
            m_Runtime.m_RuntimeHotPathProbe.Clear();
            m_Runtime.ClearFrameBuffers();
            m_Runtime.log.Info("[清场] 已清除所有公共交通车辆");
        }

        public void ClearTracking()
        {
            m_Runtime.m_SpawnIntentTrace?.Clear();
            m_Runtime.m_RailEtaService?.ResetCity();
            m_Runtime.m_Observation.ClearDispatchEta();
            m_Runtime.m_Announcements.Clear();
            m_Runtime.m_VehicleRegistry.Clear();
            m_Runtime.m_VehicleRegistrar.ClearPendingRebindCandidates();
            m_Runtime.m_VehicleRegistrar.ClearDisabledLineLateSpawnRetireQueue();
            m_Runtime.m_VehicleRegistrar.ClearStartupGate();
            m_Runtime.m_RailEventSource.ResetTracking();
            m_Runtime.m_ObsPersist.ClearLaps();
            m_Runtime.m_UICache.Clear();
            m_Runtime.m_VehicleLabels.Clear();
            m_Runtime.m_StopRuntimeState.ClearBoardingStates();
            m_Runtime.m_BoardingFirstFrameGuardState.Clear();
            m_Runtime.m_StopRuntimeState.ClearStopSessions();
            m_Runtime.m_StopRuntimeState.ClearInvalidatedRecovery();
            m_Runtime.m_CachedWpIdx.Clear();
            m_Runtime.m_StopRuntimeState.ClearForcedMidStopGrace();
            m_Runtime.m_CommandApplier.ClearRetireHandoffState();
            m_Runtime.m_PreparingFixCooldownUntil.Clear();
            m_Runtime.m_SpawningLines.Clear();
            m_Runtime.m_LineSpawnRequestFrame.Clear();
            m_Runtime.m_LastSpawnBlockedLogFrame.Clear();
            m_Runtime.m_LastScheduleDiagnosticLogFrame.Clear();
            m_Runtime.m_LineTimes.Clear();
            m_Runtime.m_LineProfile.ClearStability();
            ResetCityBufferReadyFlags();
            m_Runtime.m_Observation.ClearDwellDeadlineCache();
            m_Runtime.m_ObsPersist.ClearDwell();
            m_Runtime.m_DwellObservationBufferReady = false;
            m_Runtime.m_DwellObservationCacheLoaded = false;
            m_Runtime.m_StationDwellObservationBufferReady = false;
            m_Runtime.m_StationDwellObservationCacheLoaded = false;
            m_Runtime.m_Observation.ClearStationAnchorObservationDiagnosticsState();
            m_Runtime.m_ObsPersist.ClearSlices();
            m_Runtime.m_SliceAdmission.Clear();
            m_Runtime.m_Obs.Clear();
            m_Runtime.m_TraversalSliceObservationBufferReady = false;
            m_Runtime.m_TraversalSliceObservationCacheLoaded = false;
            m_Runtime.m_JustLaunched.Clear();
            m_Runtime.m_RuntimeEngine.ClearAssistLaunchPending();
            m_Runtime.m_Bypass.ClearAll();
            m_Runtime.m_TrackModel.InvalidateAll();
            m_Runtime.m_TrackProjection.Clear();
            m_Runtime.m_WaypointIndex.Clear();
            m_Runtime.m_RouteProgress.Clear();
            m_Runtime.m_LastPuppetMasterMinute = -1;
            m_Runtime.m_LastRegisterSweepMinute = -1;
            m_Runtime.m_LastSchedulerTickMinute = -1;
            m_Runtime.m_SelectPanel.ClearDebugSummaries();
            m_Runtime.m_StationContextQuery.Clear();
            m_Runtime.m_RuntimeLog.Clear();
            m_Runtime.m_RuntimeHotPathProbe.Clear();
            m_Runtime.ClearFrameBuffers();
            m_Runtime.log.Info("[启动] 已清空跨档运行态缓存");
        }

#if RT_DEBUG_TOOLS
        public void SpawnTest()
        {
            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            BufferLookup<RouteVehicle> routeVehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (Entity line in lines)
                {
                    if (!m_Runtime.EntityManager.Exists(line))
                        continue;

                    int actualCount = m_Runtime.m_LineVehicles.Count(line, routeVehicles);
                    if (!m_Runtime.m_SpawningLines.ContainsKey(line))
                    {
                        m_Runtime.m_SpawningLines[line] = actualCount + 1;
                        m_Runtime.m_LineSpawnRequestFrame[line] = m_Runtime.m_SimulationSystem.frameIndex;
                        m_Runtime.log.Info("[F8] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ")");
                    }

                    break;
                }
            }
            finally
            {
                lines.Dispose();
            }
        }
#endif

        public int Minute()
        {
            return m_Runtime.m_SimClock.NowMinute;
        }

        private void ResetCityBufferReadyFlags()
        {
            m_Runtime.m_LapCacheBufferReady = false;
            m_Runtime.m_VehicleCacheBufferReady = false;
            m_Runtime.m_DispatchCacheBufferReady = false;
            m_Runtime.m_BypassStationBufferReady = false;
            m_Runtime.m_LineMileageBufferReady = false;
        }

    }
}
