using System;
using Colossal.Core;
using Game.Common;
using Game.Routes;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.Core;
using RapidTransitMod.RailEta.BuiltIn;
using RapidTransitMod.Runtime;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal static class RuntimeRoot
    {
        public static void Build(ModRuntimeHostSystem runtime)
        {
            runtime.m_SimClock = new SimClock(runtime.m_TimeSystem);
            runtime.m_SimClock.ForceRefresh(runtime.m_SimulationSystem.frameIndex);
            runtime.m_VehicleStateStore = new VehicleStateStore();
            runtime.m_VehicleStateStore.Init();
            runtime.m_FrameEvents = new FrameEvents();
            runtime.m_VehicleWorksets = new VehicleWorksets();
            runtime.m_VehicleRegistry = new VehicleRegistry(
                runtime.m_VehicleStateStore,
                runtime.m_VehicleWorksets,
                runtime.m_FrameEvents,
                () => runtime.m_SimulationSystem.frameIndex,
                minutes => runtime.m_SimClock.Snapshot.ToFramesCeil(minutes),
                line => TransportModeResolver.Resolve(runtime.EntityManager, line),
                runtime.PublishStopFact);
            runtime.m_RailEventSource = new RailEventSource(runtime, runtime.m_FrameEvents);
            runtime.m_RuntimeFramePlan = new RuntimeFramePlan();
            runtime.m_RoadEventSource = new RoadEventSource(runtime);
            runtime.m_SchedulerApply = new SchedulerApply(runtime);
            runtime.m_VehicleRegistry.BindFramePlan(
                runtime.m_RuntimeFramePlan,
                (vehicle, demand, active) => RuntimePorts.SetSourceDemand(runtime, vehicle, demand, active),
                runtime.m_SchedulerApply.MarkDirty);
            runtime.m_VehicleView = new VehicleView(runtime.m_VehicleStateStore);
            runtime.m_SimClock.ClockChanged += (oldClockSnapshot, newClockSnapshot) =>
            {
                runtime.m_VehicleRegistry.ReprojectReady(
                    runtime.m_SimulationSystem.frameIndex,
                    oldClockSnapshot,
                    newClockSnapshot);
                runtime.m_VehicleRegistry.ReprojectIdle(newClockSnapshot);
            };
            runtime.m_RuntimeEngine = new DispatchEngine(runtime.m_VehicleRegistry, runtime, runtime.PublishStopFact);
            runtime.m_LineSpawnControl = new LineSpawnControl(runtime);
            runtime.m_RuntimeVehicleCleanup = new RuntimeVehicleCleanup(
                runtime,
                runtime.m_LineSpawnControl,
                runtime.m_RuntimeEngine.ClearAssistLaunchPending);
            runtime.m_VehicleRegistrar = new VehicleRegistrar(runtime);
            runtime.m_VehicleLabels = new RuntimeVehicleLabels(runtime);
            runtime.m_LineAnchorCatalog = new LineAnchorCatalog(runtime.EntityManager);
            runtime.m_Resolve = new RuntimeResolve(runtime);
            runtime.m_SharedCorridor = new SharedCorridorSupport(runtime.m_Resolve, runtime.IsBypassStationSetting);
            runtime.m_StationAnchorDiagnostics = new StationAnchorDiagnostics(runtime);
            runtime.m_WorkbenchBridge = new RapidTransitMod.Dispatch.Workbench.Bridge(runtime);
            runtime.m_WorkbenchBridge.AppliedStore.SetDirtyCallbacks(
                line => runtime.m_SchedulerApply.MarkPendingDirty(line.ToString()),
                () => runtime.m_SchedulerApply.MarkPendingAllDirty());
            runtime.m_WorkbenchBridge.LineStore.SetDirtyCallbacks(
                line => runtime.m_SchedulerApply.MarkPendingDirty(line.ToString()),
                () => runtime.m_SchedulerApply.MarkPendingAllDirty());
            runtime.m_WorkbenchCatalogCache = runtime.m_WorkbenchBridge.CatalogCache();
            runtime.m_WorkbenchCatalogDirty = new CatalogDirty(
                runtime.EntityManager,
                () =>
                {
                    runtime.m_WorkbenchCatalogCache.MarkDirty();
                    if (runtime.m_LineView != null)
                        runtime.m_LineView.Clear();
                },
                () => ScanLineAnchors(runtime));
            runtime.m_DispatchCache = new DispatchCache(runtime, runtime.LineStableId, runtime.GetDepot, runtime.DepotId, runtime.m_LineAnchorCatalog);
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
                () => runtime.m_AnnouncementWorkbench.StopPreview(),
                () => runtime.m_SchedulerApply.MarkPendingAllDirty());
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
            runtime.m_SpawnIntentTrace = new SpawnIntentTrace(runtime);
            runtime.m_RuntimeHotPathProbe = new RuntimeHotPathProbe(runtime.log);
            runtime.m_FrameEvents.SetFactCounter(runtime.m_RuntimeHotPathProbe.CountBusinessFact);
            RailEtaHost.RailEtaWorker railEtaWorker = new RailEtaHost.RailEtaWorker();
            runtime.m_RailEtaService = new RailEtaHost.RailEtaBridgeService(railEtaWorker, () => runtime.m_SimClock.Snapshot);
            runtime.m_SimClock.ClockChanged += runtime.m_RailEtaService.OnClockChanged;
            RailEtaHost.RailEtaBridgeService.Bind(runtime.m_RailEtaService);
            runtime.m_RailEtaHotRuntime = new RailEtaHost.RailEtaHotRuntime(railEtaWorker, new RailEtaHotModule());
            runtime.m_RailEtaHotRuntime.Attach(new RailEtaHost.RailEtaHotContext(
                runtime.World,
                () => runtime.m_SimulationSystem.frameIndex,
                runtime.World.GetOrCreateSystemManaged<RailTravel.QuerySystem>(),
                new RailEtaHost.RailEtaRuntimeReadPort
                {
                    ClockSnapshot = () => runtime.m_SimClock.Snapshot,
                    LineDwellMinutes = line => runtime.m_LineView.Dwell(line),
                    TryReadOriginScheduledHold = (Entity vehicle, uint frame, out uint earliestReleaseFrame) =>
                    {
                        earliestReleaseFrame = 0u;
                        if (!runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                            || state != VehicleState.Holding
                            || !runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute)
                            || targetMinute < 0) return false;
                        ClockSnapshot clockSnapshot = runtime.m_SimClock.Snapshot;
                        int nowMinute = clockSnapshot.NowMinute;
                        if (RapidTransitMod.Dispatch.Scheduling.ScheduleClock.Reached(nowMinute, targetMinute)
                            || RapidTransitMod.Dispatch.Scheduling.ScheduleClock.CanLate(nowMinute, targetMinute))
                        {
                            earliestReleaseFrame = frame;
                            return true;
                        }
                        double deltaDayFraction = targetMinute / 1440.0 - runtime.m_TimeSystem.normalizedTime;
                        if (deltaDayFraction <= 0.0) deltaDayFraction += 1.0;
                        earliestReleaseFrame = unchecked(
                            frame + (uint)Math.Ceiling(clockSnapshot.DayFractionToFrames(deltaDayFraction)));
                        return true;
                    },
                    TryReadHold = (Entity vehicle, uint frame, out RailEtaHost.RailEtaRuntimeHoldFact fact) =>
                    {
                        fact = default;
                        if (!runtime.m_Bypass.TryGetHoldCadence(vehicle, out RapidTransitMod.Bypass.BypassHoldCadenceSnapshot cadence)
                            || !cadence.ShouldHold || cadence.EvaluatedFrame > frame
                            || !runtime.m_Bypass.TryGetConflictEpisode(vehicle, out RapidTransitMod.Bypass.BypassConflictEpisode episode)
                            || episode.AcquiredFrame > frame || !episode.HasLatchedBlockerProjection || !episode.LatchedBlockerProjection.Available) return false;
                        RapidTransitMod.Bypass.BypassLatchedBlockerProjection projection = episode.LatchedBlockerProjection;
                        fact = new RailEtaHost.RailEtaRuntimeHoldFact { ReleaseVehicle = episode.BlockerVehicle, ReleaseLine = projection.ExpressLine,
                            ReleaseCoordinate = projection.ExpressReleaseCoordinate, ExpectedChainSignature = projection.ExpressChainSignature,
                            IntervalStartAtomIndex = projection.ExpressProtectedInterval.StartAtomIndex,
                            IntervalEndAtomIndexExclusive = projection.ExpressProtectedInterval.EndAtomIndexExclusive };
                        return true;
                    },
                    TryReadTrackChain = (Entity line, out RailEtaHost.RailEtaRuntimeTrackChainFact fact) =>
                    {
                        fact = null;
                        if (line == Entity.Null || !runtime.EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line)) return false;
                        DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = runtime.EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                        if (!runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)) return false;
                        var atoms = new RailEtaHost.RailEtaRuntimeTrackAtomFact[chain.TrackAtoms.Count];
                        for (int atomIndex = 0; atomIndex < atoms.Length; atomIndex++)
                        {
                            TrackAtom atom = chain.TrackAtoms[atomIndex];
                            atoms[atomIndex] = new RailEtaHost.RailEtaRuntimeTrackAtomFact { PhysicalLane = atom.Key.PhysicalLaneKey,
                                PreviousTarget = atom.Key.PreviousTarget, NextTarget = atom.Key.NextTarget, Start = atom.TargetDelta.x, End = atom.TargetDelta.y,
                                SourceFlags = (uint)atom.SourceFlags, AtomClass = (byte)atom.AtomClass, Direction = (sbyte)atom.TraversalDir };
                        }
                        fact = new RailEtaHost.RailEtaRuntimeTrackChainFact { Line = line, Signature = chain.Signature, Atoms = atoms };
                        return true;
                    }
                },
                railEtaWorker,
                result => runtime.PublishRailEtaPublicResult(result),
                message => runtime.log.Info(message)));
            runtime.m_RailEtaService.SetHotRuntime(runtime.m_RailEtaHotRuntime);
            runtime.m_SpawnLeadTheory = new SpawnLeadTheory(runtime);
            runtime.m_RuntimeLifecycleHost = new RuntimeLifecycleHost(runtime);
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
            runtime.m_Slices = new SliceStore(runtime.m_RuntimeFramePlan);
            runtime.m_SliceAdmission = new SliceAdmission(runtime.m_Slices, RuntimePorts.BuildSliceAdmission(runtime));
            var busSegStore = new BusSegStore();
            runtime.m_ObsQuery = new RapidTransitMod.Dispatch.Observation.Query(
                runtime.m_Laps,
                runtime.m_Dwell,
                runtime.m_Slices,
                busSegStore);
            runtime.m_ObsPersist = new RapidTransitMod.Dispatch.Observation.Persist(
                runtime.m_Laps,
                runtime.m_Dwell,
                runtime.m_Slices,
                runtime.m_SliceAdmission,
                busSegStore);
            runtime.m_WaypointIndex = new WaypointIndex(runtime);
            runtime.m_LineRange = new LineRange(runtime.EntityManager, runtime.m_ObsQuery, ModRuntimeHostSystem.MAINTENANCE_THRESHOLD);
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
                ClockSnapshot = () => runtime.m_SimClock.Snapshot,
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
                runtime.m_SliceAdmission,
                runtime.m_TrackModel,
                runtime.m_TrackProjection,
                RuntimePorts.BuildCapture(runtime));
            var busSegCapture = new BusSegCapture(
                runtime,
                busSegStore,
                runtime.m_ObsBuffers.SyncBusSeg);
            runtime.m_Observation = new ObservationPort(
                runtime,
                runtime.m_ObsCapture,
                runtime.m_SliceAdmission,
                busSegCapture);
            runtime.m_Bypass = new RuntimeFacade(RuntimePorts.BuildBypassRuntime(runtime));
            runtime.m_LineView = new LineView(
                runtime.EntityManager,
                waypoint => runtime.m_Resolve.Stop(waypoint),
                entity => entity != Entity.Null && runtime.EntityManager.Exists(entity),
                () => runtime.m_SimulationSystem.frameIndex,
                runtime.m_WorkbenchBridge.Ids().StableId,
                RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                runtime.m_WorkbenchBridge.Ids().Key,
                runtime.m_WorkbenchBridge.StableEntityKey,
                runtime.m_WorkbenchBridge.AppliedStore,
                () => runtime.m_WorkbenchBridge.AppliedLines,
                runtime.m_WorkbenchBridge.LineCfg(),
                () =>
                {
                    runtime.m_TrackModel.MarkSharedIndexDirty();
                    runtime.m_Bypass.ClearAll();
                },
                RapidTransitMod.Dispatch.Workbench.Time.Slot,
                message => Mod.log.Info(message),
                runtime.m_WorkbenchBridge.Ids().StableKey);

            runtime.m_UICache = new NativeHashMap<Entity, FixedString64Bytes>(1024, Allocator.Persistent);
            runtime.m_StopRuntimeState = new StopRuntimeState();
            runtime.m_StopRuntime = new StopRuntime(
                runtime.m_StopRuntimeState,
                runtime.m_RuntimeFramePlan,
                (vehicle, active) => RuntimePorts.SetSourceDemand(
                    runtime,
                    vehicle,
                    RuntimeDemandMask.DeparturePending,
                    active));
            runtime.m_StopRuntime.BindDwell(
                line => runtime.m_LineView.Dwell(line),
                minutes => runtime.m_SimClock.Snapshot.ToFramesCeil(minutes),
                runtime.m_Observation.TryGetObservedWaypointStopFrames,
                vehicle =>
                {
                    if (!runtime.m_ObsQuery.TryDwellStart(vehicle, out uint legacyStart))
                        return null;

                    runtime.m_ObsPersist.RemoveDwellStart(vehicle);
                    return (uint?)legacyStart;
                });
            runtime.m_SimClock.ClockChanged += (oldClockSnapshot, newClockSnapshot) =>
            {
                runtime.m_StopRuntime.ReprojectDwell();
            };
            runtime.m_BoardingFirstFrameGuardState = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            runtime.m_CachedWpIdx = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
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
                runtime.LineStableId,
                RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                runtime.m_CachedWpIdx);
            runtime.m_StopRuntimeState.InitForcedGrace();
            runtime.m_PreparingFixCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            runtime.m_SpawningLines = new NativeHashMap<Entity, int>(64, Allocator.Persistent);
            runtime.m_LastSpawnBlockedLogFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            runtime.m_LastScheduleDiagnosticLogFrame = new NativeHashMap<ulong, uint>(256, Allocator.Persistent);
            runtime.m_LineInitialAdopted = new NativeHashSet<Entity>(64, Allocator.Persistent);
            runtime.m_JustLaunched = new NativeHashSet<Entity>(64, Allocator.Persistent);
            runtime.m_LineSpawnRequestFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);

            runtime.m_CommandApplier = new DispatchCommandApplier(runtime);

            LifecyclePort.Bind(new LifecyclePort(
                new ManagedRequestPort(runtime),
                new OriginRepairPort(
                    runtime.IsRuntimeReadyForOriginArrivingRepair,
                    runtime.TryGetRuntimeVehicleState,
                    runtime.m_WaypointIndex.ComputeForOriginArrivingRepair,
                    runtime.m_RouteProgress.TryOriginArrivalRepair)));

            RuntimePorts.Build(runtime);
            PassengerFlow.Port passengerFlowPort = new PassengerFlow.Port(runtime);
            PassengerFlow.Runtime.Bind(passengerFlowPort);
            passengerFlowPort.SubscribeClockChanged((oldClockSnapshot, newClockSnapshot) =>
                PassengerFlow.SamplingSystem.ClockChanged(passengerFlowPort));
        }

        /// <summary>
        /// Full line-anchor scan from the live line query snapshot.
        /// Clears only in-memory catalog ownership on <see cref="Clear"/>; never removes entity Lak.
        /// </summary>
        internal static bool ScanLineAnchors(ModRuntimeHostSystem runtime)
        {
            if (runtime == null || runtime.m_LineAnchorCatalog == null)
                return false;

            NativeArray<Entity> lines = runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                return runtime.m_LineAnchorCatalog.Scan(lines);
            }
            finally
            {
                if (lines.IsCreated)
                    lines.Dispose();
            }
        }

        public static void Clear(ModRuntimeHostSystem runtime)
        {
            runtime.m_SpawnLeadTheory?.Clear();
            runtime.m_RailEtaHotRuntime?.Dispose();
            runtime.m_RailEtaHotRuntime = null!;
            runtime.m_RailEtaService?.Dispose();
            runtime.m_RailEtaService = null!;
            PassengerFlow.SamplingSystem.ClearState();
            PassengerFlow.Runtime.Clear();
            LifecyclePort.Clear();
            runtime.m_CommandApplier?.ClearRoadCommandLogs();
            runtime.m_CommandApplier = null!;
            runtime.m_DispatchScheduler = null!;
            runtime.m_SchedulerApply = null!;
            runtime.m_RuntimeVehicleCleanup = null!;
            runtime.m_LineSpawnControl = null!;
            runtime.m_SpawnLeadTheory = null!;
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
            runtime.m_LineAnchorCatalog = null!;
            runtime.m_SimClock = null!;
            runtime.m_PlannerApi = null!;
            runtime.m_PlannerJobs = null!;
            runtime.m_PlannerExport = null!;
            runtime.m_PlannerPort = null!;
            runtime.m_AnnouncementWorkbench = null!;
            runtime.m_Announcements = null!;
            runtime.m_RuntimeEngine = null!;
            runtime.m_OverviewFeatureSettingsPersist = null!;
            runtime.m_Features = null!;
            runtime.m_LineView = null!;
            runtime.m_VehicleView = null!;
            runtime.m_VehicleRegistry = null!;
            runtime.m_RuntimeFramePlan?.Dispose();
            runtime.m_RuntimeFramePlan = null!;
            runtime.m_RoadEventSource?.Dispose();
            runtime.m_RoadEventSource = null!;
            runtime.m_RailEventSource?.Dispose();
            runtime.m_RailEventSource = null!;
            runtime.m_VehicleWorksets?.Dispose();
            runtime.m_VehicleWorksets = null!;
            runtime.m_FrameEvents?.Dispose();
            runtime.m_FrameEvents = null!;
            runtime.m_Resolve = null!;
            runtime.m_DispatchCache = null!;
            runtime.m_LapCache = null!;
            runtime.m_VehicleCache = null!;
            runtime.m_ObsBuffers = null!;
            runtime.m_MileageStore = null!;
            runtime.m_BypassStore = null!;
            runtime.m_RuntimeCache = null!;
            runtime.m_Observation?.ClearBusSeg();
            runtime.m_Observation = null!;
            runtime.m_SharedCorridor = null!;
            runtime.m_RuntimeLog = null!;
            runtime.m_RuntimeLifecycleHost = null!;
            runtime.m_LineStructureInvalidator = null!;
            if (runtime.m_Bypass != null) runtime.m_Bypass.Dispose();
            runtime.m_TrackModel = null!;
            runtime.m_Bypass = null!;
            runtime.m_TrackProjection = null!;
            if (runtime.m_VehicleStateStore != null) runtime.m_VehicleStateStore.Dispose();
            if (runtime.m_Laps != null) runtime.m_Laps.Dispose();
            if (runtime.m_Dwell != null) runtime.m_Dwell.Dispose();
            runtime.m_Slices = null!;
            runtime.m_SliceAdmission = null!;
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
