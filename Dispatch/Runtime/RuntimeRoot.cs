using System;
using Colossal.Core;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal static class RuntimeRoot
    {
        public static void Build(DispatchRuntimeSystem runtime)
        {
            runtime.m_VehicleStateStore = new VehicleStateStore();
            runtime.m_VehicleStateStore.Init();
            runtime.m_VehicleRegistry = new VehicleRegistry(runtime.m_VehicleStateStore);
            runtime.m_VehicleView = new VehicleView(runtime.m_VehicleStateStore);
            runtime.m_RuntimeController = new DispatchRuntimeController(runtime.m_VehicleRegistry, runtime);
            runtime.m_VehicleRegistrar = new VehicleRegistrar(runtime);
            runtime.m_VehicleLabels = new RuntimeVehicleLabels(runtime);
            runtime.m_Resolve = new RuntimeResolve(runtime);
            runtime.m_SharedCorridor = new SharedCorridorSupport(runtime.m_Resolve, runtime.IsBypassStationSetting);
            runtime.m_StationAnchorDiagnostics = new StationAnchorDiagnostics(runtime);
            runtime.m_WorkbenchBridge = new RapidTransitMod.Dispatch.Workbench.Bridge(runtime);
            runtime.m_WorkbenchCatalogCache = runtime.m_WorkbenchBridge.CatalogCache();
            runtime.m_WorkbenchCatalogDirty = new CatalogDirty(
                runtime.EntityManager,
                () =>
                {
                    runtime.m_WorkbenchCatalogCache.MarkDirty();
                    if (runtime.m_LineView != null)
                        runtime.m_LineView.Clear();
                });
            runtime.m_DispatchCache = new DispatchCache(runtime, runtime.LineId, runtime.GetDepot, runtime.DepotId);
            runtime.m_LapCache = new LapCache(runtime);
            runtime.m_RouteProgress = new RouteProgress(runtime);
            runtime.m_VehicleCache = new VehicleCache(runtime, runtime.m_LapCache.Read, runtime.m_LapCache.Distance, runtime.m_RouteProgress.Try);
            runtime.m_ObsBuffers = new Buffers(runtime);
            runtime.m_MileageStore = new MileageStore(runtime);
            runtime.m_BypassStore = new BypassStore(runtime);
            runtime.m_RuntimeCache = new RuntimeCache(runtime, runtime.m_ObsBuffers, runtime.m_MileageStore, runtime.m_BypassStore);

            AnnouncementServices announcements = new AnnouncementHost(runtime).Create();
            runtime.m_AnnouncementWorkbench = announcements.Workbench;
            runtime.m_Announcements = announcements.Runtime;
            runtime.m_Features = new FeatureGate(
                new FeatureSettingsStore(),
                () => runtime.m_Bypass.RuntimeEnabled(),
                () => runtime.m_Bypass.ClearAll(),
                () => runtime.m_AnnouncementWorkbench.StopPreview());
            runtime.m_OverviewFeatureSettingsPersist = new RapidTransitMod.Overview.FeatureSettingsPersist(
                runtime.EntityManager,
                () => runtime.m_CitySystem.City,
                runtime.m_Features);
            runtime.m_OverviewFeatureSettingsOperations = new RapidTransitMod.Overview.FeatureSettingsOperations(
                new RapidTransitMod.Overview.FeatureSettingsService(
                    runtime.m_Features,
                    () => runtime.m_OverviewFeatureSettingsPersist.MarkDirty(),
                    () => runtime.m_WorkbenchBridge.Version.ToString()),
                action => MainThreadDispatcher.RunOnMainThread(action));
            runtime.m_LineProfile = new LineProfile(runtime);
            runtime.m_RuntimeLog = new RuntimeLog(runtime);
            runtime.m_RuntimeHotPathProbe = new RuntimeHotPathProbe(runtime.log);
            runtime.m_RuntimeShell = new RuntimeShell(runtime);
            runtime.m_LineStructureInvalidator = new LineStructureInvalidator(runtime);
            runtime.m_DispatchScheduler = new DispatchScheduler(
                runtime,
                line => runtime.m_LineView.Managed(line, runtime.m_Features.Dispatch()),
                line => runtime.m_LineView.Times(line),
                line => runtime.m_LineView.Hold(line),
                runtime.m_DispatchCache.Read,
                runtime.m_LapCache.Read,
                runtime.m_Resolve.RuntimeVehicle,
                runtime.m_LineProfile.IsStable,
                runtime.m_LineProfile.ShouldHoldSpawnForNearestRunningCandidate,
                runtime.m_LineProfile.HasBorderlineOriginArrivalCandidate,
                runtime.m_RuntimeLog.DispatchSlotHeld,
                (line, now, slot, count) => runtime.m_SelectPanel.RecordLineSpawnTriggerSummary(line, now, slot, count));
            runtime.m_Laps = new LapStore();
            runtime.m_Laps.Init();
            runtime.m_Dwell = new DwellStore();
            runtime.m_Dwell.Init();
            runtime.m_Slices = new SliceStore();
            runtime.m_ObsQuery = new RapidTransitMod.Dispatch.Observation.Query(runtime.m_Laps, runtime.m_Dwell, runtime.m_Slices);
            runtime.m_ObsPersist = new RapidTransitMod.Dispatch.Observation.Persist(runtime.m_Laps, runtime.m_Dwell, runtime.m_Slices);
            runtime.m_WaypointIndex = new WaypointIndex(runtime);
            runtime.m_LineRange = new LineRange(runtime.EntityManager, runtime.m_ObsQuery, DispatchRuntimeSystem.MAINTENANCE_THRESHOLD);
            LineHost lineHost = RuntimePorts.BuildLineHost(runtime);
            runtime.m_LineTimes = new LineTimes(lineHost.Times);
            runtime.m_LineTimes.Init();
            runtime.m_LineMileage = new LineMileage(lineHost.Mileage);
            runtime.m_LineVehicles = new LineVehicles(runtime);
            runtime.m_Obs = new TraceStore();
            runtime.m_ObsRecorder = new Recorder(RuntimePorts.BuildObservation(runtime));
            runtime.m_TrackModel = new TrackModelService(new TrackModelContext(new TrackModelContext.Args
            {
                EntityMgr = () => runtime.EntityManager,
                Log = runtime.log,
                Frame = () => runtime.m_SimulationSystem.frameIndex,
                VehicleCount = () => runtime.m_VehicleView.Count,
                AppliedLines = () => runtime.m_WorkbenchBridge.AppliedLines,
                LineQuery = runtime.m_LineQuery,
                Buffers = RuntimePorts.Buffers(runtime),
                Name = runtime.m_NameSystem,
                IsBypassStation = runtime.IsBypassStationSetting,
                GetProfile = runtime.m_LineTimes.Get,
                GetStopFrames = runtime.m_LineTimes.Stop,
                GetDepartFrames = runtime.m_LineTimes.Depart,
                ResolveStop = runtime.m_Resolve.Stop,
                FindStation = runtime.m_Resolve.StationOf,
                ResolveStation = runtime.m_Resolve.PassingStation,
                IsLocal = line => runtime.m_LineView.Local(line),
                IsExpress = line => runtime.m_LineView.Express(line),
                GetBypassContext = runtime.m_SharedCorridor.TryGetBypassWaypointContext,
                GetBypassBuilding = runtime.m_SharedCorridor.GetBypassBuildingForWaypoint,
                GetStationBuilding = runtime.m_SharedCorridor.GetStationBuildingForWaypoint,
                FindBypassWaypoint = runtime.m_SharedCorridor.TryFindWaypointIndexForBypassBuilding,
                FindSharedWaypoint = runtime.m_SharedCorridor.TryFindFutureSharedCorridorWaypoint,
                BuildCorridorMap = runtime.m_SharedCorridor.BuildLocalBypassCorridorWaypointMap,
                CollectTurnback = Turnbacks.TryCollectTurnbackStationBoundaries,
                ResolveTurnback = Turnbacks.TryResolveTurnbackStationBoundary,
                NotifyLineTrackChainRebuilt = runtime.m_LineStructureInvalidator.Request
            }));
            runtime.m_TrackProjection = new TrackProjectionService(RuntimePorts.BuildTrackProjection(runtime));
            runtime.m_ObsCapture = new Capture(
                runtime.m_Laps,
                runtime.m_Dwell,
                runtime.m_Slices,
                runtime.m_TrackModel,
                runtime.m_TrackProjection,
                RuntimePorts.BuildCapture(runtime));
            runtime.m_Observation = new ObservationPort(runtime, runtime.m_ObsCapture);
            runtime.m_Bypass = new RuntimeFacade(RuntimePorts.BuildBypassRuntime(runtime));
            runtime.m_LineView = new LineView(
                runtime.EntityManager,
                waypoint => runtime.m_Resolve.Stop(waypoint),
                entity => entity != Entity.Null && runtime.EntityManager.Exists(entity),
                () => runtime.m_SimulationSystem.frameIndex,
                runtime.m_WorkbenchBridge.Ids().Get,
                RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                runtime.m_WorkbenchBridge.Ids().Key,
                runtime.m_WorkbenchBridge.Ids().Key,
                runtime.m_WorkbenchBridge.AppliedStore,
                () => runtime.m_WorkbenchBridge.AppliedLines,
                runtime.m_WorkbenchBridge.LineCfg(),
                () =>
                {
                    runtime.m_TrackModel.MarkSharedIndexDirty();
                    runtime.m_Bypass.ClearAll();
                },
                RapidTransitMod.Dispatch.Workbench.Time.Slot,
                message => Mod.log.Info(message));

            runtime.m_UICache = new NativeHashMap<Entity, FixedString64Bytes>(1024, Allocator.Persistent);
            runtime.m_LastEffectiveBoardingState = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            runtime.m_LastOfficialBoardingState = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            runtime.m_BoardingFirstFrameGuardState = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            runtime.m_StopSessionLine = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            runtime.m_StopSessionWaypointIndex = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            runtime.m_StopSessionArrivalFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_StopSessionBoardingChangeCount = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_DeparturePendingSinceFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_CachedWpIdx = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            runtime.m_InvalidatedMidStopRecoveryPending = new NativeHashSet<Entity>(256, Allocator.Persistent);
            runtime.m_StationContextQuery = new VehicleStationContextQuery(
                runtime.EntityManager,
                runtime.m_Resolve.Stop,
                runtime.m_Resolve.Anchor,
                runtime.m_Resolve.AnchorFromStop,
                runtime.m_Resolve.EnsureSak,
                runtime.m_Resolve.Sak,
                runtime.m_Resolve.StationId,
                runtime.m_Resolve.StationName,
                runtime.EntityName,
                runtime.m_LineProfile.ComputeWaypointSignature,
                (line, waypoints) => runtime.m_WaypointIndex.TryLookup(line, waypoints, out LineWaypointIndexLookup lookup)
                    ? lookup
                    : null,
                (line, waypoints) => runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                    ? chain
                    : null,
                (vehicle, line, waypoints, chain) => runtime.m_TrackProjection.TryGetVehicleTrackCursorCurrentFrame(
                        vehicle,
                        line,
                        waypoints,
                        chain,
                        out VehicleTrackCursor cursor)
                    ? (VehicleTrackCursor?)cursor
                    : null,
                runtime.LineId,
                RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                runtime.m_CachedWpIdx);
            runtime.m_BVMisfire = new NativeHashSet<Entity>(64, Allocator.Persistent);
            runtime.m_BVMisfireStartFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            runtime.m_ForcedMidStopBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            runtime.m_LastRetireFixLogFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_RetireFixCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_PreparingFixCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_RetireFixCount = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            runtime.m_SpawningLines = new NativeHashMap<Entity, int>(64, Allocator.Persistent);
            runtime.m_LastSpawnBlockedLogFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            runtime.m_LastScheduleDiagnosticLogFrame = new NativeHashMap<ulong, uint>(256, Allocator.Persistent);
            runtime.m_LineInitialAdopted = new NativeHashSet<Entity>(64, Allocator.Persistent);
            runtime.m_JustLaunched = new NativeHashSet<Entity>(64, Allocator.Persistent);
            runtime.m_LineSpawnRequestFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);

            runtime.m_CommandApplier = new DispatchCommandApplier(runtime);

            LifecyclePort.Bind(new LifecyclePort(
                new ManagedRequestPort(runtime),
                new RetireGuardPort(runtime.m_CommandApplier.GuardRetireHandoffInputs),
                new OriginRepairPort(
                    runtime.IsRuntimeReadyForOriginArrivingRepair,
                    runtime.TryGetRuntimeVehicleState,
                    runtime.m_WaypointIndex.ComputeForOriginArrivingRepair,
                    runtime.m_RouteProgress.TryOriginArrivalRepair)));

            RuntimePorts.Build(runtime);
            PassengerFlow.Runtime.Bind(new PassengerFlow.Port(runtime));
        }

        public static void Clear(DispatchRuntimeSystem runtime)
        {
            PassengerFlow.SamplingSystem.ClearState();
            PassengerFlow.Runtime.Clear();
            LifecyclePort.Clear();
            runtime.m_CommandApplier = null!;
            runtime.m_DispatchScheduler = null!;
            runtime.m_VehicleRegistrar = null!;
            runtime.m_VehicleLabels = null!;
            runtime.m_SelectPanel = null!;
            runtime.m_SelectPort = null!;
            runtime.m_StationContextQuery = null!;
            runtime.m_StationAnchorDiagnostics = null!;
            runtime.m_OverviewFeatureSettingsOperations = null!;
            runtime.m_WorkbenchCatalogDirty = null!;
            runtime.m_WorkbenchCatalogCache = null!;
            runtime.m_WorkbenchBridge = null!;
            runtime.m_PlannerApi = null!;
            runtime.m_PlannerJobs = null!;
            runtime.m_PlannerExport = null!;
            runtime.m_PlannerPort = null!;
            runtime.m_AnnouncementWorkbench = null!;
            runtime.m_Announcements = null!;
            runtime.m_RuntimeController = null!;
            runtime.m_OverviewFeatureSettingsPersist = null!;
            runtime.m_Features = null!;
            runtime.m_LineView = null!;
            runtime.m_VehicleView = null!;
            runtime.m_VehicleRegistry = null!;
            runtime.m_Resolve = null!;
            runtime.m_DispatchCache = null!;
            runtime.m_LapCache = null!;
            runtime.m_VehicleCache = null!;
            runtime.m_ObsBuffers = null!;
            runtime.m_MileageStore = null!;
            runtime.m_BypassStore = null!;
            runtime.m_RuntimeCache = null!;
            runtime.m_Observation = null!;
            runtime.m_SharedCorridor = null!;
            runtime.m_RuntimeLog = null!;
            runtime.m_RuntimeShell = null!;
            runtime.m_LineStructureInvalidator = null!;
            if (runtime.m_Bypass != null) runtime.m_Bypass.Dispose();
            runtime.m_TrackModel = null!;
            runtime.m_Bypass = null!;
            runtime.m_TrackProjection = null!;
            if (runtime.m_VehicleStateStore != null) runtime.m_VehicleStateStore.Dispose();
            if (runtime.m_Laps != null) runtime.m_Laps.Dispose();
            if (runtime.m_Dwell != null) runtime.m_Dwell.Dispose();
            runtime.m_Slices = null!;
            runtime.m_Obs = null!;
            runtime.m_ObsRecorder = null!;
            runtime.m_ObsCapture = null!;
            runtime.m_LineRange = null!;
            runtime.m_LineProfile?.Dispose();
            runtime.m_LineProfile = null!;
            if (runtime.m_LineTimes != null) runtime.m_LineTimes.Dispose();
            runtime.m_LineTimes = null!;
            runtime.m_LineMileage = null!;
            runtime.m_LineVehicles = null!;
            runtime.m_RouteProgress = null!;
            runtime.m_WaypointIndex = null!;
            runtime.m_ObsQuery = null!;
            runtime.m_ObsPersist = null!;
        }
    }
}
