using System;
using System.Collections.Generic;
using Game;
using Game.Routes;
using Game.SceneFlow;
using Game.Vehicles;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RuntimeLifecycleHost
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly List<LaunchedSeed> m_LaunchedSeeds = new List<LaunchedSeed>();

        public RuntimeLifecycleHost(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Loaded()
        {
            m_Runtime.EntityManager.CompleteAllTrackedJobs();
            ResetForLoad();
            RemoveLegacySliceBuffers();
            MigrationReport report = RestoreSavedConfig();
            RestoreLineBaseline();
            PublishLoaded(report);
        }

        internal void ActivateFirstFrame(uint frame, ClockSnapshot clock)
        {
            m_Runtime.m_LineProfile.ConfirmLoadedRoutes();
            m_Runtime.m_VehicleStartup.Activate(frame);
            m_Runtime.m_SystemReady = true;
            m_Runtime.m_DispatchScheduler.ResetMonitorClock(clock);
            m_Runtime.m_ObsBuffers.LoadMonitor();
            m_Runtime.m_ObsBuffers.LoadMonitorIntegrity();
            m_Runtime.m_ObsBuffers.LoadMonitorAverages();
            m_Runtime.m_LineTimes.RebuildAll();
            // Temporary recovery from monitor persistence; schema changes can invalidate this mapping.
            // Remove it when scheduled trips are persisted on vehicle components.
            m_LaunchedSeeds.Clear();
            foreach (MonitorTrip trip in m_Runtime.m_Observation.ActiveMonitorTrips)
                m_LaunchedSeeds.Add(new LaunchedSeed(trip.Line, trip.Vehicle, trip.SlotMinute,
                    trip.ServiceDateKey));
            m_Runtime.m_DispatchScheduler.BackfillLaunched(m_LaunchedSeeds, clock);
            m_LaunchedSeeds.Clear();
            m_Runtime.m_LineStructureInvalidator.OnObservationRestored();
            m_Runtime.log.Info("[Startup] Takeover complete, system ready");
        }

        private void ResetForLoad()
        {
            ClearTracking();
            m_Runtime.m_SimClock.ForceRefresh(m_Runtime.m_SimulationSystem.frameIndex);
            m_Runtime.m_SpawnLeadTheory?.Clear();
#if RT_DEBUG_TOOLS
            RailEtaHost.RailEtaHotDebugApi.RequestReloadLatest();
#endif
            PassengerFlow.SamplingSystem.ClearState();
        }

        private void RemoveLegacySliceBuffers()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            EntityManager entityManager = m_Runtime.EntityManager;
            if (entityManager.HasBuffer<TraversalSliceObservationElement>(city))
                entityManager.RemoveComponent<TraversalSliceObservationElement>(city);
            if (entityManager.HasBuffer<TraversalSliceQuotaElement>(city))
                entityManager.RemoveComponent<TraversalSliceQuotaElement>(city);
            if (entityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                entityManager.RemoveComponent<TraversalSliceColdStartElement>(city);
        }

        private MigrationReport RestoreSavedConfig()
        {
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

            m_Runtime.m_ServiceWindowStore.Load();

            try
            {
                m_Runtime.m_LineStructureInvalidator.RestorePending(
                    m_Runtime.m_LineStructurePendingStore.Restore());
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[LineStructurePending] Restore failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            ResetCityBufferReadyFlags();
            m_Runtime.m_CommandApplier.ResetRetireDispatchLockStages();
            m_Runtime.m_CommandApplier.ProjectRetireDispatchLocksImmediatelyOnLoad();
            m_Runtime.m_SystemReady = false;
            m_Runtime.m_AnnouncementWorkbench.Reset();
            m_Runtime.m_WorkbenchBridge.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Reset();
            m_Runtime.m_OverviewFeatureSettingsPersist.Restore();

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

            try
            {
                m_Runtime.m_WorkbenchBridge.Applied().RefreshCfg();
            }
            catch (Exception ex)
            {
                m_Runtime.log.Info("[Loaded] Applied.RefreshCfg failed -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            return report;
        }

        private void RestoreLineBaseline()
        {
            m_Runtime.m_LineStructureInvalidator.AcceptLoadedChains();
            m_Runtime.m_LineProfile.BaselineLoadedLines();
        }

        private void PublishLoaded(MigrationReport report)
        {
            Entity city = m_Runtime.m_CitySystem.City;
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
                    + " stationDwellBuffer=" + CountBuffer<StationDwellObservationElement>());
            }
            try
            {
                Workbenches.UiEvents.Push(m_Runtime.m_WorkbenchBridge.Build(m_Runtime.m_WorkbenchBridge.Drafts().Preferred()));
            }
            catch (Exception ex)
            {
                m_Runtime.m_WorkbenchBridge.Ui().Fault("OnGameLoaded.NotifyWorkbenchSnapshotChanged", ex);
            }

            if (MigrationReport.DetailsEnabled)
                report.LogDetails(message => m_Runtime.log.Info(message));
            if (report.Count > 0)
            {
                m_Runtime.log.Info("[LineKeyMigration] summary: " + report.Summary());
            }
        }

        public void ClearTracking()
        {
            m_Runtime.m_FollowContext?.ResetConnection();
            m_Runtime.m_FirstPersonQuery?.Clear();
            m_Runtime.m_TransitSignalRuntime?.Clear("tracking-cleared");
            m_Runtime.m_LineStructureInvalidator?.ResetRuntimeState();
            m_Runtime.m_LineChangeSource?.ResetPending();
            m_Runtime.m_RailEtaService?.ResetCity();
            m_Runtime.m_Observation.ClearDispatchEta();
            m_Runtime.m_Announcements.Clear();
            m_Runtime.m_VehicleRegistry.Clear();
            m_Runtime.m_DispatchScheduler.ResetCity();
            m_Runtime.m_VehicleRegistrar.ClearRegistrationConfirmations();
            m_Runtime.m_LineServiceState.Reset();
            m_Runtime.m_ServiceCalendar.Clear();
            m_Runtime.m_ServiceWindowStore.ClearTracking();
            m_Runtime.m_RailEventSource.ResetTracking();
            m_Runtime.m_ObsPersist.ClearLaps();
            m_Runtime.m_UICache.Clear();
            m_Runtime.m_VehicleLabels.Clear();
            m_Runtime.m_StopRuntimeState.ClearBoardingStates();
            m_Runtime.m_BoardingFirstFrameGuardState.Clear();
            m_Runtime.m_StopRuntimeState.ClearStopSessions();
            m_Runtime.m_StopRuntimeState.ClearInvalidatedRecovery();
            m_Runtime.m_StopRuntime.ClearTimedDeadlines();
            m_Runtime.m_CachedWpIdx.Clear();
            m_Runtime.m_StopRuntimeState.ClearForcedMidStopGrace();
            m_Runtime.m_CommandApplier.ClearRetireHandoffState();
            m_Runtime.m_PreparingFixCooldownUntil.Clear();
            m_Runtime.m_LineSpawnControl.Clear();
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
            m_Runtime.m_Obs.Clear();
            m_Runtime.m_BusSegObservationBufferReady = false;
            m_Runtime.m_BusSegObservationCacheLoaded = false;
            m_Runtime.m_TrackModel.InvalidateAll();
            m_Runtime.m_TrackProjection.Clear();
            m_Runtime.m_WaypointIndex.Clear();
            m_Runtime.m_WaypointIndex.ClearLookups();
            m_Runtime.m_RouteProgress.Clear();
            m_Runtime.m_LastVehicleIntervalMinute = -1;
            m_Runtime.m_LastSchedulerTickMinute = -1;
            m_Runtime.m_SelectPanel.ClearDebugSummaries();
            m_Runtime.m_StationContextQuery.Clear();
            m_Runtime.m_RuntimeLog.Clear();
            m_Runtime.m_RuntimeHotPathProbe.Clear();
            m_Runtime.log.Info("[Startup] Cleared runtime state from the previous save");
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
                    if (!m_Runtime.m_LineView.Applied(line)
                        || !m_Runtime.m_LineServiceState.IsOperational(line))
                        continue;

                    int actualCount = m_Runtime.m_LineVehicles.Count(line, routeVehicles);
                    if (m_Runtime.m_LineSpawnControl.DisplayTarget(line) == 0)
                    {
                        m_Runtime.m_LineSpawnControl.AddWithoutSlot(line, m_Runtime.m_SimulationSystem.frameIndex);
                        m_Runtime.log.Info("[F8] Spawn requested line=" + line.Index + " current=" + actualCount);
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
            m_Runtime.m_LineMileageBufferReady = false;
        }

    }
}
