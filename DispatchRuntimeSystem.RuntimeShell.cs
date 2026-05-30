using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using RapidTransitMod.Bypass;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        protected override void OnUpdate()
        {
            if (GameManager.instance.gameMode != GameMode.Game) return;
            m_SelectionPanel.UpdateVersionBucket();

            if (Input.GetKey(KeyCode.LeftControl) &&
                Input.GetKey(KeyCode.LeftAlt) &&
                Input.GetKey(KeyCode.X))
            {
                SafeClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                TestSpawnRequest();
                return;
            }

            if (m_Bypass.ToggleKey(Input.GetKey(KeyCode.F5)))
                return;

            if (Input.GetKeyDown(KeyCode.F6))
            {
                SafeClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                m_CommandApplier.ForceRetireOne(m_EndFrameBarrier.CreateCommandBuffer());
                return;
            }

            if (!m_SystemReady)
            {
                if (!m_StartupRuntimeStateCleared)
                {
                    ClearRuntimeTrackingState();
                    m_StartupRuntimeStateCleared = true;
                }
                var rvBuffers0 = GetBufferLookup<RouteVehicle>(true);
                var lines0 = m_LineQuery.ToEntityArray(Allocator.Temp);
                int totalVehicles = 0;
                foreach (var line0 in lines0)
                {
                    if (rvBuffers0.TryGetBuffer(line0, out var rvs0))
                        totalVehicles += rvs0.Length;
                }
                lines0.Dispose();
                if (totalVehicles != m_LastVehicleCount)
                {
                    m_LastVehicleCount = totalVehicles;
                    m_StableFrameCount = 0;
                    return;
                }
                m_StableFrameCount++;
                if (m_StableFrameCount < STABLE_FRAMES_REQUIRED) return;
                m_SystemReady = true;
                log.Info("[启动] 稳定检测通过，系统就绪(车辆数=" + totalVehicles + ")");
            }

            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;

            EnsureLapCacheBuffer();
            EnsureVehicleCacheBuffer();
            EnsureDispatchCacheBuffer();
            if (IsStationStopDwellObservationPersistenceEnabled())
            {
                EnsureStationStopDwellObservationBuffer();
                RestoreStationStopDwellObservationsFromBuffer();
            }
            if (IsTraversalSliceObservationPersistenceEnabled())
            {
                EnsureTraversalSliceObservationBuffer();
                RestoreTraversalSliceObservationsFromBuffer();
            }
            bool runFullRegisterSweep = nowMin != m_LastRegisterSweepMinute;
            try
            {
                m_VehicleRegistrar.Register(runFullRegisterSweep);
                if (runFullRegisterSweep)
                    m_LastRegisterSweepMinute = nowMin;
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] VehicleRegistrar -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            try
            {
                m_RuntimeController.Tick(ecb, nowMin);
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] RuntimeController.Tick -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (nowFrame - m_LastVehicleCacheFlushFrame >= VEHICLE_CACHE_FLUSH_INTERVAL)
            {
                FlushAllVehicleStates();
                m_LastVehicleCacheFlushFrame = nowFrame;
            }

            m_Bypass.FlushProbeLogs(nowFrame);
        }

        private void SafeClearAll()
        {
            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            var ents = m_AllPublicTransportQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in ents)
            {
                if (!EntityManager.HasComponent<Game.Prefabs.PrefabData>(e))
                    ecb.AddComponent<Deleted>(e);
            }
            ents.Dispose();
            m_VehicleRegistry.Clear();
            m_LapObservations.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_CommandApplier.ClearRetireHandoffState();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_StopDwell.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_StationStopDwellObservationBufferReady = false;
            m_StationStopDwellObservationCacheLoaded = false;
            ClearStationAnchorObservationDiagnosticsState();
            m_TraversalSlices.Clear();
            m_RuntimeObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_AssistLaunchPendingByVehicle.Clear();
            m_Bypass.ClearAll();
            m_TrackModel.InvalidateAll();
            m_TrackProjection.Clear();
            m_WaypointIndexFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_PreparingTargetDriftLogCache.Clear();
            m_CrossLineCandidateLogCache.Clear();
            m_RouteVehicleOwnerMismatchLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastLaunchHeadSnapshots.Clear();
            m_LastBoardingHeadSnapshots.Clear();
            m_MidStopTimeoutLogCache.Clear();
            m_SystemReady = false;
            m_StartupRuntimeStateCleared = false;
            m_StableFrameCount = 0;
            m_LastVehicleCount = -1;
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            m_SelectionPanel.ClearDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[清场] 已清除所有公共交通车辆");
        }

        private void ClearRuntimeTrackingState()
        {
            m_Announcements.Clear();
            m_VehicleRegistry.Clear();
            m_LapObservations.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_CommandApplier.ClearRetireHandoffState();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_StopDwell.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_StationStopDwellObservationBufferReady = false;
            m_StationStopDwellObservationCacheLoaded = false;
            ClearStationAnchorObservationDiagnosticsState();
            m_TraversalSlices.Clear();
            m_RuntimeObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_AssistLaunchPendingByVehicle.Clear();
            m_Bypass.ClearAll();
            m_TrackModel.InvalidateAll();
            m_TrackProjection.Clear();
            m_WaypointIndexFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_PreparingTargetDriftLogCache.Clear();
            m_CrossLineCandidateLogCache.Clear();
            m_RouteVehicleOwnerMismatchLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastLaunchHeadSnapshots.Clear();
            m_LastBoardingHeadSnapshots.Clear();
            m_MidStopTimeoutLogCache.Clear();
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            m_SelectionPanel.ClearDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[启动] 已清空跨档运行态缓存");
        }

        private void TestSpawnRequest()
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    int actualCount = CountActiveVehicles(line, rvBuffers);
                    if (!m_SpawningLines.ContainsKey(line))
                    {
                        m_SpawningLines[line] = actualCount + 1;
                        m_LineSpawnRequestFrame[line] = m_SimulationSystem.frameIndex;
                        log.Info("[F8] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ")");
                    }
                    break;
                }
            }
            finally { lines.Dispose(); }
        }


        internal int CurrentGameMinute()
        {
            return (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
        }

        internal void GuardRetireHandoffDispatchInputs(uint nowFrame)
        {
            m_CommandApplier.GuardRetireHandoffInputs(nowFrame);
        }

    }
}
