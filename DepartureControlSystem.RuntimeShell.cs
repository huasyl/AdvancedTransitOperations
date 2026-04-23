using System;
using Game;
using Game.Common;
using Game.Routes;
using Game.SceneFlow;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        protected override void OnUpdate()
        {
            if (GameManager.instance.gameMode != GameMode.Game) return;
            UpdatePanelDataVersionBucket();

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

            if (!Input.GetKey(KeyCode.F5))
            {
                m_BypassToggleKeyArmed = true;
            }
            else if (m_BypassToggleKeyArmed)
            {
                m_BypassToggleKeyArmed = false;
                SetBypassRuntimeEnabled(!m_BypassRuntimeEnabled);
                return;
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                SafeClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                ForceRetireOne(m_EndFrameBarrier.CreateCommandBuffer());
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
            CleanupDeferredBoardingTailIgnores(m_SimulationSystem.frameIndex);
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            DrainPatchedTrainReverseSignals();

            EnsureLapCacheBuffer();
            EnsureVehicleCacheBuffer();
            EnsureDispatchCacheBuffer();
            if (IsStopDwellObservationPersistenceEnabled())
            {
                EnsureStopDwellObservationBuffer();
                RestoreStopDwellObservationsFromBuffer();
            }
            if (IsTraversalSliceObservationPersistenceEnabled())
            {
                EnsureTraversalSliceObservationBuffer();
                RestoreTraversalSliceObservationsFromBuffer();
            }
            bool runFullRegisterSweep = nowMin != m_LastRegisterSweepMinute;
            try
            {
                RegisterNewVehicles(runFullRegisterSweep);
                if (runFullRegisterSweep)
                    m_LastRegisterSweepMinute = nowMin;
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] RegisterNewVehicles -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            try
            {
                DriveStateMachine(ecb, nowMin);
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] DriveStateMachine -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            if (nowMin != m_LastPuppetMasterMinute)
            {
                try
                {
                    PuppetMasterControl(nowMin);
                    m_LastPuppetMasterMinute = nowMin;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] PuppetMasterControl -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
            if (nowMin != m_LastSchedulerTickMinute)
            {
                try
                {
                    SchedulerTick(ecb, nowMin);
                    m_LastSchedulerTickMinute = nowMin;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] SchedulerTick -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }

            ProcessPendingRetireHandoffs(ecb);

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (nowFrame - m_LastVehicleCacheFlushFrame >= VEHICLE_CACHE_FLUSH_INTERVAL)
            {
                FlushAllVehicleStates();
                m_LastVehicleCacheFlushFrame = nowFrame;
            }

            FlushBypassPerfProbeIfDue(nowFrame);
            FlushLineOrderedProbeIfDue(nowFrame);
        }

        private void FlushBypassPerfProbeIfDue(uint nowFrame)
        {
            if (!IsBypassPerfProbeLoggingEnabled())
                return;

            if (m_BypassPerfProbeLastLogFrame == 0)
            {
                m_BypassPerfProbeLastLogFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_BypassPerfProbeLastLogFrame;
            if (elapsedFrames < BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES)
                return;

            if (m_BypassPerfProbeCadenceCalls > 0
                || m_BypassPerfProbeCadenceMisses > 0
                || m_BypassPerfProbeBaselineCalls > 0
                || m_BypassPerfProbeTrackDecisionCalls > 0
                || m_BypassPerfProbeResolveCalls > 0
                || m_BypassPerfProbeActiveCorridorCalls > 0
                || m_BypassPerfProbeSceneSamples > 0
                || m_BypassPerfProbeSceneCandidateVehicles > 0
                || m_BypassPerfProbeSceneAdmittedCandidates > 0
                || m_BypassPerfProbeSceneFrontiers > 0
                || m_BypassPerfProbeSameStationCalls > 0
                || m_BypassPerfProbeSameStationReusedCandidates > 0
                || m_BypassPerfProbeDeepCorridorEntries > 0
                || m_BypassPerfProbeEpisodeReuses > 0
                || m_PerfProbeSceneExpressLineQueries > 0
                || m_PerfProbeSceneExpressLineSameFrameRequeries > 0
                || m_PerfProbeSceneExpressLineConsecutiveFrameRequeries > 0
                || m_PerfProbeSceneExpressLineRecentFrameRequeries > 0
                || m_PerfProbeWorkbenchLineFrameSnapshotHits > 0
                || m_PerfProbeWorkbenchLineFrameSnapshotMisses > 0
                || m_PerfProbeOriginSettleCalls > 0
                || m_PerfProbeOriginSettleFastPathHits > 0
                || m_PerfProbeOriginSettleSlowPathEntered > 0
                || m_PerfProbeOriginSettlePreSnapshotMisses > 0
                || m_PerfProbeOriginSettleWindowHits > 0)
            {
                log.Info("[待避轻量计数] frames=" + elapsedFrames
                    + " cadence=" + m_BypassPerfProbeCadenceCalls
                    + " miss=" + m_BypassPerfProbeCadenceMisses
                    + " baseline=" + m_BypassPerfProbeBaselineCalls
                    + " trackDecision=" + m_BypassPerfProbeTrackDecisionCalls
                    + " resolve=" + m_BypassPerfProbeResolveCalls
                    + " activeCorridor=" + m_BypassPerfProbeActiveCorridorCalls
                    + " scenes=" + m_BypassPerfProbeSceneSamples
                    + " cand=" + m_BypassPerfProbeSceneCandidateVehicles
                    + " admitted=" + m_BypassPerfProbeSceneAdmittedCandidates
                    + " frontiers=" + m_BypassPerfProbeSceneFrontiers
                    + " sameReuse=" + m_BypassPerfProbeSameStationReusedCandidates
                    + " sameCalls=" + m_BypassPerfProbeSameStationCalls
                    + " deepCorridor=" + m_BypassPerfProbeDeepCorridorEntries
                    + " episodeReuse=" + m_BypassPerfProbeEpisodeReuses
                    + " expressLineQ=" + m_PerfProbeSceneExpressLineQueries
                    + " expressLineSameFrame=" + m_PerfProbeSceneExpressLineSameFrameRequeries
                    + " expressLineConsecutive=" + m_PerfProbeSceneExpressLineConsecutiveFrameRequeries
                    + " expressLineRecent=" + m_PerfProbeSceneExpressLineRecentFrameRequeries
                    + " wbHit=" + m_PerfProbeWorkbenchLineFrameSnapshotHits
                    + " wbMiss=" + m_PerfProbeWorkbenchLineFrameSnapshotMisses
                    + " settleCalls=" + m_PerfProbeOriginSettleCalls
                    + " settleFast=" + m_PerfProbeOriginSettleFastPathHits
                    + " settleSlow=" + m_PerfProbeOriginSettleSlowPathEntered
                    + " settleSnapMiss=" + m_PerfProbeOriginSettlePreSnapshotMisses
                    + " settleWindowHit=" + m_PerfProbeOriginSettleWindowHits);
            }

            m_BypassPerfProbeLastLogFrame = nowFrame;
            m_BypassPerfProbeCadenceCalls = 0;
            m_BypassPerfProbeCadenceMisses = 0;
            m_BypassPerfProbeBaselineCalls = 0;
            m_BypassPerfProbeTrackDecisionCalls = 0;
            m_BypassPerfProbeResolveCalls = 0;
            m_BypassPerfProbeActiveCorridorCalls = 0;
            m_BypassPerfProbeSceneSamples = 0;
            m_BypassPerfProbeSceneCandidateVehicles = 0;
            m_BypassPerfProbeSceneAdmittedCandidates = 0;
            m_BypassPerfProbeSceneFrontiers = 0;
            m_BypassPerfProbeSameStationCalls = 0;
            m_BypassPerfProbeSameStationReusedCandidates = 0;
            m_BypassPerfProbeDeepCorridorEntries = 0;
            m_BypassPerfProbeEpisodeReuses = 0;
            m_PerfProbeSceneExpressLineQueries = 0;
            m_PerfProbeSceneExpressLineSameFrameRequeries = 0;
            m_PerfProbeSceneExpressLineConsecutiveFrameRequeries = 0;
            m_PerfProbeSceneExpressLineRecentFrameRequeries = 0;
            m_PerfProbeWorkbenchLineFrameSnapshotHits = 0;
            m_PerfProbeWorkbenchLineFrameSnapshotMisses = 0;
            m_PerfProbeOriginSettleCalls = 0;
            m_PerfProbeOriginSettleFastPathHits = 0;
            m_PerfProbeOriginSettleSlowPathEntered = 0;
            m_PerfProbeOriginSettlePreSnapshotMisses = 0;
            m_PerfProbeOriginSettleWindowHits = 0;
        }

        private void FlushLineOrderedProbeIfDue(uint nowFrame)
        {
            if (!IsLineOrderedRuntimeProbeLoggingEnabled())
                return;

            if (m_LineOrderedProbeLastLogFrame == 0)
            {
                m_LineOrderedProbeLastLogFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_LineOrderedProbeLastLogFrame;
            if (elapsedFrames < LINE_ORDERED_PROBE_LOG_INTERVAL_FRAMES)
                return;

            if (m_LineOrderedProbeExpressLineQueries > 0
                || m_LineOrderedProbeOrderedAttempts > 0
                || m_LineOrderedProbeHeadOnlySuccesses > 0
                || m_LineOrderedProbeFallbacks > 0
                || m_LineOrderedProbeHeadCandidateBuilds > 0
                || m_LineOrderedProbeFallbackCandidateBuilds > 0)
            {
                log.Info("[LineOrderedProbe] frames=" + elapsedFrames
                    + " expressLineQ=" + m_LineOrderedProbeExpressLineQueries
                    + " orderedAttempts=" + m_LineOrderedProbeOrderedAttempts
                    + " headOnly=" + m_LineOrderedProbeHeadOnlySuccesses
                    + " fallback=" + m_LineOrderedProbeFallbacks
                    + " headBuilds=" + m_LineOrderedProbeHeadCandidateBuilds
                    + " fallbackBuilds=" + m_LineOrderedProbeFallbackCandidateBuilds);
            }

            m_LineOrderedProbeLastLogFrame = nowFrame;
            m_LineOrderedProbeExpressLineQueries = 0;
            m_LineOrderedProbeOrderedAttempts = 0;
            m_LineOrderedProbeHeadOnlySuccesses = 0;
            m_LineOrderedProbeFallbacks = 0;
            m_LineOrderedProbeHeadCandidateBuilds = 0;
            m_LineOrderedProbeFallbackCandidateBuilds = 0;
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
            m_VehicleState.Clear();
            m_VehicleTargetMin.Clear();
            m_VehicleLapStartOdometer.Clear();
            m_VehicleLapDistance.Clear();
            m_VehicleLapStartFrame.Clear();
            m_VehicleLapFrames.Clear();
            m_VehicleIdleStartFrame.Clear();
            m_VehiclePreparingStartFrame.Clear();
            m_VehicleDispatchRequestStartFrame.Clear();
            m_VehicleCurrentSlot.Clear();
            m_VehicleLastLaunchFrame.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_ForcedMidStopBoardingHardCloseAfter.Clear();
            m_VehicleLine.Clear();
            m_PendingRetireHandoffs.Clear();
            m_RetireShadowHistory.Clear();
            m_RetireShadowLastSnapshot.Clear();
            m_RetireShadowLastFrame.Clear();
            m_LaunchCooldownUntil.Clear();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_TraversalRunSliceObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_VehicleTraversalSliceLastSampleFrame.Clear();
            m_VehicleTraversalSliceSamplingPlans.Clear();
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_ForcedOriginBoardingGraceUntil.Clear();
            m_AssistLaunchPendingByVehicle.Clear();
            m_StopDwellStartFrame.Clear();
            m_StopDwellStartFrame.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            ClearBypassRuntimeState();
            m_LineTrackChainFrameSnapshots.Clear();
            m_LineWaypointIndexLookups.Clear();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_WaypointIndexFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastLaunchHeadSnapshots.Clear();
            m_LastBoardingHeadSnapshots.Clear();
            m_DeferredBoardingTailIgnores.Clear();
            m_DeferredBoardingHumanTailIgnores.Clear();
            m_DeferredBoardingPetTailIgnores.Clear();
            m_DeferredBoardingTailScratch.Clear();
            m_MidStopTimeoutLogCache.Clear();
            ClearPatchedTrainReverseSignals();
            m_SystemReady = false;
            m_StartupRuntimeStateCleared = false;
            m_StableFrameCount = 0;
            m_LastVehicleCount = -1;
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            ClearLineDispatchDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[清场] 已清除所有公共交通车辆");
        }

        private void ClearRuntimeTrackingState()
        {
            ClearAllBroadcastRuntimeState();
            m_VehicleState.Clear();
            m_VehicleTargetMin.Clear();
            m_VehicleLapStartOdometer.Clear();
            m_VehicleLapDistance.Clear();
            m_VehicleLapStartFrame.Clear();
            m_VehicleLapFrames.Clear();
            m_VehicleIdleStartFrame.Clear();
            m_VehiclePreparingStartFrame.Clear();
            m_VehicleDispatchRequestStartFrame.Clear();
            m_VehicleCurrentSlot.Clear();
            m_VehicleLastLaunchFrame.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_ForcedMidStopBoardingHardCloseAfter.Clear();
            m_VehicleLine.Clear();
            m_PendingRetireHandoffs.Clear();
            m_RetireShadowHistory.Clear();
            m_RetireShadowLastSnapshot.Clear();
            m_RetireShadowLastFrame.Clear();
            m_LaunchCooldownUntil.Clear();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_TraversalRunSliceObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_VehicleTraversalSliceLastSampleFrame.Clear();
            m_VehicleTraversalSliceSamplingPlans.Clear();
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_NearingTerminus.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_ForcedOriginBoardingGraceUntil.Clear();
            m_AssistLaunchPendingByVehicle.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            ClearBypassRuntimeState();
            m_LineTrackChainFrameSnapshots.Clear();
            m_LineWaypointIndexLookups.Clear();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_WaypointIndexFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastLaunchHeadSnapshots.Clear();
            m_LastBoardingHeadSnapshots.Clear();
            m_DeferredBoardingTailIgnores.Clear();
            m_DeferredBoardingHumanTailIgnores.Clear();
            m_DeferredBoardingPetTailIgnores.Clear();
            m_DeferredBoardingTailScratch.Clear();
            m_MidStopTimeoutLogCache.Clear();
            ClearPatchedTrainReverseSignals();
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            ClearLineDispatchDebugSummaries();
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


    }
}
