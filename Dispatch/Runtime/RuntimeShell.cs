using System;
using Colossal.Serialization.Entities;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RuntimeShell
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public RuntimeShell(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Tick()
        {
            if (GameManager.instance.gameMode != GameMode.Game) return;
            m_Runtime.m_SelectPanel.UpdateVersionBucket();

#if RT_DEBUG_TOOLS
            if (Input.GetKey(KeyCode.LeftControl)
                && Input.GetKey(KeyCode.LeftAlt)
                && Input.GetKey(KeyCode.X))
            {
                ClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                SpawnTest();
                return;
            }

            if (m_Runtime.m_Bypass.ToggleKey(Input.GetKey(KeyCode.F5)))
                return;

            if (Input.GetKeyDown(KeyCode.F6))
            {
                ClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                m_Runtime.m_CommandApplier.ForceRetireOne(m_Runtime.m_EndFrameBarrier.CreateCommandBuffer());
                return;
            }
#endif

            if (!m_Runtime.m_SystemReady)
            {
                if (!m_Runtime.m_StartupRuntimeStateCleared)
                {
                    ClearTracking();
                    m_Runtime.m_StartupRuntimeStateCleared = true;
                }

                BufferLookup<RouteVehicle> routeVehicles = m_Runtime.GetBufferLookup<RouteVehicle>(true);
                NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
                int totalVehicles = 0;
                foreach (Entity line in lines)
                {
                    if (routeVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                        totalVehicles += vehicles.Length;
                }

                lines.Dispose();
                if (totalVehicles != m_Runtime.m_LastVehicleCount)
                {
                    m_Runtime.m_LastVehicleCount = totalVehicles;
                    m_Runtime.m_StableFrameCount = 0;
                    return;
                }

                m_Runtime.m_StableFrameCount++;
                if (m_Runtime.m_StableFrameCount < DispatchRuntimeSystem.STABLE_FRAMES_REQUIRED)
                    return;

                m_Runtime.m_SystemReady = true;
                m_Runtime.log.Info("[启动] 稳定检测通过，系统就绪(车辆数=" + totalVehicles + ")");
            }

            EntityCommandBuffer commandBuffer = m_Runtime.m_EndFrameBarrier.CreateCommandBuffer();
            int nowMin = Minute();

            m_Runtime.m_LapCache.Ensure();
            m_Runtime.m_VehicleCache.Ensure();
            m_Runtime.m_DispatchCache.Ensure();
            if (DispatchRuntimeSystem.IsStationDwellObservationPersistenceEnabled())
            {
                m_Runtime.m_ObsBuffers.EnsureStationDwell();
                m_Runtime.m_RuntimeCache.LoadStationDwell();
            }

            if (DispatchRuntimeSystem.IsTraversalSliceObservationPersistenceEnabled())
            {
                m_Runtime.m_ObsBuffers.EnsureSlice();
                m_Runtime.m_RuntimeCache.LoadSlice();
            }

            m_Runtime.m_LineStructureInvalidator.Drain();

            bool runFullRegisterSweep = nowMin != m_Runtime.m_LastRegisterSweepMinute;
            try
            {
                m_Runtime.m_VehicleRegistrar.Register(runFullRegisterSweep);
                if (runFullRegisterSweep)
                    m_Runtime.m_LastRegisterSweepMinute = nowMin;
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[运行异常] VehicleRegistrar -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            DrainDisabledLineLateSpawnRetireQueue(commandBuffer);

            try
            {
                m_Runtime.m_RuntimeController.Tick(commandBuffer, nowMin);
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[运行异常] RuntimeController.Tick -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
            if (nowFrame - m_Runtime.m_LastVehicleCacheFlushFrame >= DispatchRuntimeSystem.VEHICLE_CACHE_FLUSH_INTERVAL)
            {
                m_Runtime.m_VehicleCache.Save();
                m_Runtime.m_LastVehicleCacheFlushFrame = nowFrame;
            }

            m_Runtime.m_WorkbenchCatalogDirty.Check(nowFrame);
            m_Runtime.m_WorkbenchCatalogCache.Tick(nowFrame);

            m_Runtime.m_Bypass.FlushProbeLogs(nowFrame);
            m_Runtime.m_RuntimeHotPathProbe.FlushIfDue(nowFrame);
        }

        public void Loaded(Context serializationContext)
        {
            PassengerFlow.SamplingSystem.ClearState();
            try
            {
                PassengerFlow.Persistence.RestoreFromCity(m_Runtime.EntityManager, m_Runtime.m_CitySystem.City);
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[PassengerFlowPersistence] Restore failed -> " + ex.GetType().Name + ": " + ex.Message);
            }
            ResetCityBufferReadyFlags();
            m_Runtime.m_SystemReady = false;
            m_Runtime.m_StartupRuntimeStateCleared = false;
            m_Runtime.m_StableFrameCount = 0;
            m_Runtime.m_LastVehicleCount = -1;
            m_Runtime.m_AnnouncementWorkbench.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Restore();
            m_Runtime.m_WorkbenchBridge.Reset();
            m_Runtime.m_WorkbenchBridge.Restore();
            m_Runtime.m_WorkbenchBridge.Applied().Load();
            m_Runtime.m_Bypass.WarmStaticSceneIndex();
            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                Entity city = m_Runtime.m_CitySystem.City;
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
        }

        public void ClearAll()
        {
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
            m_Runtime.m_ObsPersist.ClearLaps();
            m_Runtime.m_UICache.Clear();
            m_Runtime.m_VehicleLabels.Clear();
            m_Runtime.m_LastEffectiveBoardingState.Clear();
            m_Runtime.m_LastOfficialBoardingState.Clear();
            m_Runtime.m_BoardingFirstFrameGuardState.Clear();
            m_Runtime.m_StopSessionLine.Clear();
            m_Runtime.m_StopSessionWaypointIndex.Clear();
            m_Runtime.m_StopSessionArrivalFrame.Clear();
            m_Runtime.m_StopSessionBoardingChangeCount.Clear();
            m_Runtime.m_DeparturePendingSinceFrame.Clear();
            m_Runtime.m_CachedWpIdx.Clear();
            m_Runtime.m_BVMisfire.Clear();
            m_Runtime.m_BVMisfireStartFrame.Clear();
            m_Runtime.m_ForcedMidStopBoardingGraceUntil.Clear();
            m_Runtime.m_CommandApplier.ClearRetireHandoffState();
            m_Runtime.m_LastRetireFixLogFrame.Clear();
            m_Runtime.m_RetireFixCooldownUntil.Clear();
            m_Runtime.m_PreparingFixCooldownUntil.Clear();
            m_Runtime.m_RetireFixCount.Clear();
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
            m_Runtime.m_Obs.Clear();
            m_Runtime.m_TraversalSliceObservationBufferReady = false;
            m_Runtime.m_TraversalSliceObservationCacheLoaded = false;
            m_Runtime.m_JustLaunched.Clear();
            m_Runtime.m_RuntimeController.ClearAssistLaunchPending();
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
            m_Runtime.log.Info("[清场] 已清除所有公共交通车辆");
        }

        public void ClearTracking()
        {
            m_Runtime.m_Announcements.Clear();
            m_Runtime.m_VehicleRegistry.Clear();
            m_Runtime.m_ObsPersist.ClearLaps();
            m_Runtime.m_UICache.Clear();
            m_Runtime.m_VehicleLabels.Clear();
            m_Runtime.m_LastEffectiveBoardingState.Clear();
            m_Runtime.m_LastOfficialBoardingState.Clear();
            m_Runtime.m_BoardingFirstFrameGuardState.Clear();
            m_Runtime.m_StopSessionLine.Clear();
            m_Runtime.m_StopSessionWaypointIndex.Clear();
            m_Runtime.m_StopSessionArrivalFrame.Clear();
            m_Runtime.m_StopSessionBoardingChangeCount.Clear();
            m_Runtime.m_DeparturePendingSinceFrame.Clear();
            m_Runtime.m_CachedWpIdx.Clear();
            m_Runtime.m_BVMisfire.Clear();
            m_Runtime.m_BVMisfireStartFrame.Clear();
            m_Runtime.m_ForcedMidStopBoardingGraceUntil.Clear();
            m_Runtime.m_CommandApplier.ClearRetireHandoffState();
            m_Runtime.m_LastRetireFixLogFrame.Clear();
            m_Runtime.m_RetireFixCooldownUntil.Clear();
            m_Runtime.m_PreparingFixCooldownUntil.Clear();
            m_Runtime.m_RetireFixCount.Clear();
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
            m_Runtime.m_Obs.Clear();
            m_Runtime.m_TraversalSliceObservationBufferReady = false;
            m_Runtime.m_TraversalSliceObservationCacheLoaded = false;
            m_Runtime.m_JustLaunched.Clear();
            m_Runtime.m_RuntimeController.ClearAssistLaunchPending();
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
            return (int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440;
        }

        private void ResetCityBufferReadyFlags()
        {
            m_Runtime.m_LapCacheBufferReady = false;
            m_Runtime.m_VehicleCacheBufferReady = false;
            m_Runtime.m_DispatchCacheBufferReady = false;
            m_Runtime.m_BypassStationBufferReady = false;
            m_Runtime.m_LineMileageBufferReady = false;
        }

        private void DrainDisabledLineLateSpawnRetireQueue(EntityCommandBuffer commandBuffer)
        {
            IReadOnlyList<Entity> queue = m_Runtime.m_VehicleRegistrar.DisabledLineLateSpawnRetireQueue;
            if (queue.Count == 0)
                return;

            try
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    Entity vehicle = queue[i];
                    if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                        continue;
                    if (m_Runtime.EntityManager.HasComponent<Deleted>(vehicle)
                        || m_Runtime.EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        continue;
                    }
                    if (!m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                        || !m_Runtime.EntityManager.HasComponent<Owner>(vehicle))
                    {
                        m_Runtime.log.Info("[DisabledLineLateSpawnSkip] 车辆" + vehicle.Index
                            + " 缺少回库前置组件，跳过误产车回库");
                        continue;
                    }

                    PublicTransport publicTransport = m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
                    Target target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
                    m_Runtime.m_CommandApplier.Retire(vehicle, publicTransport, target, commandBuffer, "关闭线路误产车");
                }
            }
            finally
            {
                m_Runtime.m_VehicleRegistrar.ClearDisabledLineLateSpawnRetireQueue();
            }
        }
    }
}
