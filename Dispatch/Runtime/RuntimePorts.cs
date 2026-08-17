using System;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Planner;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class TrackBuffers : TrackModelContext.IBuffers
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public TrackBuffers(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public BufferLookup<T> Get<T>(bool readOnly) where T : unmanaged, IBufferElementData
        {
            return m_Runtime.GetBufferLookup<T>(readOnly);
        }
    }

    internal static class RuntimePorts
    {
        private static bool CanBypass(ModRuntimeHostSystem runtime, Entity line)
        {
            return runtime != null
                && line != Entity.Null
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(runtime.EntityManager, line)).CanBypass;
        }

        public static TrackModelContext.IBuffers Buffers(ModRuntimeHostSystem runtime)
        {
            return new TrackBuffers(runtime);
        }

        internal static bool TryResolveVehicleLifecycle(
            ModRuntimeHostSystem runtime,
            Entity vehicle,
            out LifecycleKind lifecycle)
        {
            lifecycle = LifecycleKind.Unknown;
            if (runtime == null
                || vehicle == Entity.Null
                || !runtime.m_VehicleView.TryGetLine(vehicle, out Entity line))
            {
                return false;
            }

            return TryResolveLineLifecycle(runtime, line, out lifecycle);
        }

        internal static bool TryResolveLineLifecycle(
            ModRuntimeHostSystem runtime,
            Entity line,
            out LifecycleKind lifecycle)
        {
            lifecycle = LifecycleKind.Unknown;
            if (runtime == null || line == Entity.Null)
                return false;

            lifecycle = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(runtime.EntityManager, line)).Lifecycle;
            return lifecycle == LifecycleKind.Rail || lifecycle == LifecycleKind.Road;
        }

        internal static void SetSourceDemand(
            ModRuntimeHostSystem runtime,
            Entity vehicle,
            RuntimeDemandMask demand,
            bool active)
        {
            if (!TryResolveVehicleLifecycle(runtime, vehicle, out LifecycleKind lifecycle))
                return;

            if (lifecycle == LifecycleKind.Rail)
                runtime.m_RailEventSource.SetDemand(vehicle, demand, active);
            else if (lifecycle == LifecycleKind.Road)
            {
                RuntimeDemandMask roadDemand = demand & ~RuntimeDemandMask.InboundWatch;
                if (roadDemand != RuntimeDemandMask.None)
                    runtime.m_RoadEventSource.SetDemand(vehicle, roadDemand, active);
            }
        }

        internal static void SetRailDemand(
            ModRuntimeHostSystem runtime,
            Entity vehicle,
            RuntimeDemandMask demand,
            bool active)
        {
            if (TryResolveVehicleLifecycle(runtime, vehicle, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail)
            {
                runtime.m_RailEventSource.SetDemand(vehicle, demand, active);
            }
        }

        public static void Build(ModRuntimeHostSystem runtime)
        {
            runtime.m_SelectPort = BuildSelect(runtime);
            runtime.m_SelectPanel = new SelectPanel(runtime.m_SelectPort);
            runtime.m_PlannerPort = new PlannerPort(runtime);
            runtime.m_PlannerExport = new PlannerExport(runtime.m_PlannerPort);
            runtime.m_PlannerJobs = new PlannerJobs(runtime.m_PlannerExport, new DispatchWorkbenchPlannerService());
            runtime.m_PlannerApi = new PlannerApi(runtime.m_PlannerExport, runtime.m_PlannerJobs);
        }

        private static SelectPort BuildSelect(ModRuntimeHostSystem runtime)
        {
            return new SelectPort
            {
                EntityManager = runtime.EntityManager,
                Log = runtime.log,
                Time = runtime.m_TimeSystem,
                Sim = runtime.m_SimulationSystem,
                ClockSnapshot = () => runtime.m_SimClock.Snapshot,
                Names = runtime.m_NameSystem,
                City = runtime.m_CitySystem,
                Barrier = runtime.m_EndFrameBarrier,
                Vehicles = runtime.m_VehicleView,
                Lines = runtime.m_LineView,
                Obs = runtime.m_ObsQuery,
                Spawns = runtime.m_SpawningLines,
                SpawnFrames = runtime.m_LineSpawnRequestFrame,
                CachedWp = runtime.m_CachedWpIdx,
                Commands = runtime.m_CommandApplier,
                Runtime = runtime.m_RuntimeEngine,
                Scheduler = runtime.m_DispatchScheduler,
                Labels = runtime.m_VehicleLabels,
                FramePlan = runtime.m_RuntimeFramePlan,
                ResolveLine = runtime.m_Resolve.SelectedLine,
                ResolveVehicle = runtime.m_Resolve.SelectedVehicle,
                ResolveVehicleLine = runtime.m_Resolve.Line,
                ResolveLineDisplayName = line =>
                {
                    if (line == Entity.Null
                        || runtime.m_WorkbenchBridge == null
                        || !runtime.m_WorkbenchBridge.Catalog().TryRuntimeLine(line, out var runtimeLine))
                    {
                        return string.Empty;
                    }

                    return runtimeLine?.Name ?? string.Empty;
                },
                ResolveBypassBuilding = runtime.m_Resolve.PassingStation,
                EnsureBypassBuffer = runtime.m_BypassStore.Ensure,
                InvalidateBypassModel = () =>
                {
                    runtime.m_Bypass.ClearAll();
                    runtime.m_TrackModel.InvalidateAll();
                },
                ReadLap = runtime.m_LapCache.Read,
                ReadLineDuration = line =>
                {
                    if (line == Entity.Null)
                        return 0f;

                    if (TransportModeResolver.Resolve(runtime.EntityManager, line) == TransitMode.Bus)
                        return runtime.m_LineTimes.Duration(line) * 60f;

                    var routeWaypoints = runtime.GetBufferLookup<RouteWaypoint>(true);
                    if (routeWaypoints.TryGetBuffer(line, out var waypoints)
                        && waypoints.Length > 0
                        && runtime.m_Observation.LapTiming(line, waypoints, out float runFrames, out float stopFrames, out _, out _))
                    {
                        float totalFrames = runFrames + stopFrames;
                        if (totalFrames > 0f)
                            return totalFrames;
                    }

                    float durationFrames = runtime.m_LineTimes.Duration(line) * 60f;
                    if (durationFrames > 0f && durationFrames < float.MaxValue)
                        return durationFrames;

                    return runtime.m_LapCache.Read(line);
                },
                ReadDispatch = runtime.m_DispatchCache.Read,
                RouteVehicles = runtime.GetBufferLookup<RouteVehicle>,
                RouteWaypoints = runtime.GetBufferLookup<RouteWaypoint>,
                CountVehicles = runtime.m_LineVehicles.Count,
                ComputeWp = runtime.m_WaypointIndex.Compute,
                PrepEta = (vehicle, line, waypoints, nowFrame, lineDurationFrames) => runtime.m_LineTimes.Prep(vehicle, line, waypoints, lineDurationFrames),
                RunEta = runtime.m_LineTimes.Run,
                TryProgress = runtime.m_RouteProgress.Try,
                TryBlocker = (Entity vehicle, out Entity blocker) => runtime.m_Bypass.TryGetLatchedBlocker(vehicle, out blocker),
                TrySessionArrival = runtime.m_StopRuntime.TryGetSessionArrivalFrame,
                TryStopSession = runtime.m_StopRuntime.TryGetSession,
                TryVehicleTimes = runtime.m_ObsRecorder.TryVehicleTimes,
                ClearBypass = (vehicle, reason) => runtime.m_Bypass.ClearVehicle(vehicle, reason),
                Stations = (Entity vehicle, Entity line, out string current, out string nextPhysical, out string nextStop, out bool nextPhysicalIsPass) =>
                {
                    runtime.m_StationContextQuery.TryPanelContext(
                        vehicle,
                        line,
                        out current,
                        out nextStop,
                        out nextPhysical,
                        out nextPhysicalIsPass,
                        out _);
                },
                TryPanelStations = runtime.m_StationContextQuery.TryPanelStations
            };
        }

        public static LineHost BuildLineHost(ModRuntimeHostSystem runtime)
        {
            return new LineHost
            {
                Times = new LineTimesPort
                {
                    EntityManager = runtime.EntityManager,
                    Log = message => runtime.log.Info(message),
                    MixSignature = runtime.m_LineProfile.MixSignature,
                    ClockSnapshot = () => runtime.m_SimClock.Snapshot,
                    ProfileStopStartBufferMinutes = ModRuntimeHostSystem.PROFILE_STOP_START_BUFFER_MINUTES,
                    EtaScaleMin = ModRuntimeHostSystem.ETA_SCALE_MIN,
                    EtaScaleMax = ModRuntimeHostSystem.ETA_SCALE_MAX,
                    DispatchFallbackFramesPerMeter = ModRuntimeHostSystem.DISPATCH_FALLBACK_FRAMES_PER_METER,
                    DispatchEstimateMinFrames = ModRuntimeHostSystem.DISPATCH_ESTIMATE_MIN_FRAMES,
                    DispatchEstimateDefaultFrames = ModRuntimeHostSystem.DISPATCH_ESTIMATE_DEFAULT_FRAMES,
                    DispatchEstimateMaxFrames = runtime.m_DispatchScheduler.Policy.MaxSpawnLeadFrames,
                    ReadLapFrames = runtime.m_LapCache.Read,
                    ReadDispatchFrames = runtime.m_DispatchCache.Read,
                    DwellMinutes = line => runtime.m_LineView.Dwell(line),
                    TryObservedWaypointStopFrames = (Entity line, int waypointIndex, out float dwellFrames) =>
                        runtime.m_Observation.TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames),
                    TryRouteProgress = runtime.m_RouteProgress.Try,
                    CachedWaypointIndex = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                    IsPreparingKnown = entity => runtime.m_VehicleStateStore.PreparingStartFrame.ContainsKey(entity),
                    TryLapFrames = (Entity vehicle, out uint lapFrames) => runtime.m_Observation.TryLapFrames(vehicle, out lapFrames),
                    TryLapStartFrame = (Entity vehicle, out uint lapStartFrame) => runtime.m_Observation.TryLapStartFrame(vehicle, out lapStartFrame),
                    TryBusSegFrames = (Entity line, Entity fromWaypoint, Entity fromStop, Entity toWaypoint, Entity toStop, out float frames) =>
                        runtime.m_Observation.TryBusSegFrames(line, fromWaypoint, fromStop, toWaypoint, toStop, out frames),
                    ResolveMode = line => TransportModeResolver.Resolve(runtime.EntityManager, line)
                },
                Mileage = new LineMileagePort
                {
                    EntityManager = runtime.EntityManager,
                    Frame = () => runtime.m_SimulationSystem.frameIndex,
                    MixSignature = runtime.m_LineProfile.MixSignature,
                    Log = message => runtime.log.Info(message),
                    Name = entity => runtime.m_NameSystem.GetRenderedLabelName(entity),
                    WaypointSignature = runtime.m_LineProfile.ComputeWaypointSignature,
                    StationBuildingForWaypoint = runtime.m_SharedCorridor.GetStationBuildingForWaypoint,
                    BypassBuildingForWaypoint = runtime.m_SharedCorridor.GetBypassBuildingForWaypoint,
                    IsBypassStation = runtime.IsBypassStationSetting,
                    AppliedLines = () => runtime.AppliedLines,
                    IsLocalLine = line => runtime.m_LineView.Local(line),
                    TryRouteProgress = runtime.m_RouteProgress.Try,
                    CachedWaypointIndex = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                    ResolveStop = waypoint => runtime.m_Resolve.Stop(waypoint)
                }
            };
        }

        public static TrackProjectionPort BuildTrackProjection(ModRuntimeHostSystem runtime)
        {
            return new TrackProjectionPort(
                runtime.EntityManager,
                runtime.log,
                () => runtime.m_SimulationSystem.frameIndex,
                () => runtime.m_CachedWpIdx,
                runtime.m_TrackModel,
                Buffers(runtime),
                runtime.m_RouteProgress,
                runtime.m_VehicleView,
                runtime.m_LineMileage,
                runtime.IsVehicleBoarding,
                runtime.m_RuntimeHotPathProbe);
        }

        public static BypassAdmissionPort BuildBypassAdmission(ModRuntimeHostSystem runtime)
        {
            return new BypassAdmissionPort(
                runtime.EntityManager,
                runtime.log,
                () => runtime.m_SimulationSystem.frameIndex,
                () => runtime.m_SimClock.Snapshot,
                () => runtime.AppliedLines,
                runtime.m_TrackModel,
                () => runtime.m_TrackProjection,
                Buffers(runtime),
                () => runtime.m_Features.BypassRun(),
                line => CanBypass(runtime, line) && runtime.m_LineView.Managed(line, runtime.m_Features.Dispatch()),
                line => CanBypass(runtime, line) && runtime.m_LineView.Local(line),
                line => CanBypass(runtime, line) && runtime.m_LineView.Express(line),
                runtime.m_Resolve,
                ModRuntimeHostSystem.IsLineOrderedRuntimeLoggingEnabled,
                runtime.m_WaypointIndex,
                runtime.m_Observation,
                (Entity vehicle, Entity line, int waypointIndex, uint nowFrame, out DwellSnapshot snapshot) =>
                    runtime.m_StopRuntime.TryGetDwellSnapshot(vehicle, line, waypointIndex, nowFrame, out snapshot),
                runtime.m_SharedCorridor,
                runtime.m_RuntimeLog.Once,
                runtime.m_VehicleView,
                runtime.m_LineMileage,
                runtime.m_LineTimes,
                runtime.EntityName,
                runtime.m_RuntimeHotPathProbe);
        }

        public static BypassRuntimePort BuildBypassRuntime(ModRuntimeHostSystem runtime)
        {
            return new BypassRuntimePort(
                runtime.EntityManager,
                runtime.log,
                () => runtime.m_SimulationSystem.frameIndex,
                () => runtime.m_SimClock.Snapshot,
                () => runtime.AppliedLines,
                runtime.m_TrackModel,
                () => runtime.m_TrackProjection,
                Buffers(runtime),
                () => runtime.m_Features.BypassRun(),
                line => CanBypass(runtime, line) && runtime.m_LineView.Managed(line, runtime.m_Features.Dispatch()),
                line => CanBypass(runtime, line) && runtime.m_LineView.Local(line),
                line => CanBypass(runtime, line) && runtime.m_LineView.Express(line),
                runtime.m_Resolve,
                ModRuntimeHostSystem.IsLineOrderedRuntimeLoggingEnabled,
                runtime.m_WaypointIndex,
                runtime.m_Observation,
                (Entity vehicle, Entity line, int waypointIndex, uint nowFrame, out DwellSnapshot snapshot) =>
                    runtime.m_StopRuntime.TryGetDwellSnapshot(vehicle, line, waypointIndex, nowFrame, out snapshot),
                runtime.m_SharedCorridor,
                runtime.m_RuntimeLog.Once,
                runtime.m_VehicleView,
                runtime.m_LineMileage,
                runtime.m_LineTimes,
                runtime.EntityName,
                runtime.m_RuntimeHotPathProbe,
                ModRuntimeHostSystem.IsBypassRuntimeLoggingEnabled,
                (vehicle, blocker, station, waypointIndex, frame, reason) =>
                {
                    runtime.m_FrameEvents.AppendBypass(new BypassFact(
                        BypassFactKind.Held,
                        vehicle,
                        runtime.m_Resolve.Line(vehicle),
                        blocker,
                        waypointIndex,
                        true,
                        false,
                        reason), frame);
                },
                (vehicle, blocker, frame, reason) =>
                {
                    runtime.m_FrameEvents.AppendBypass(new BypassFact(
                        BypassFactKind.Released,
                        vehicle,
                        runtime.m_Resolve.Line(vehicle),
                        blocker,
                        -1,
                        false,
                        true,
                        reason), frame);
                },
                fact =>
                {
                    runtime.m_FrameEvents.AppendBypass(fact, runtime.m_SimulationSystem.frameIndex);
                },
                (vehicle, route, waypoints, waypointIndex) => { },
                (vehicle, publicTransport) =>
                {
                    runtime.m_RailEventSource.AppendPublicTransportWrite(vehicle, publicTransport, runtime.m_SimulationSystem.frameIndex);
                },
                () => runtime.m_Features.BypassRun(),
                runtime.m_RuntimeFramePlan.SetDeadline,
                runtime.m_RuntimeFramePlan.ClearDeadline,
                runtime.m_RuntimeFramePlan.ClearDeadlines,
                (vehicle, active) => SetRailDemand(runtime, vehicle, RuntimeDemandMask.BypassActive, active),
                () => runtime.m_RailEventSource.ClearDemands(RuntimeDemandMask.BypassActive),
                (vehicle, active) => SetRailDemand(runtime, vehicle, RuntimeDemandMask.BypassWatch, active),
                () => runtime.m_RailEventSource.ClearDemands(RuntimeDemandMask.BypassWatch));
        }

        public static CapturePort BuildCapture(ModRuntimeHostSystem runtime)
        {
            return new CapturePort
            {
                Exists = entity => runtime.EntityManager.Exists(entity),
                HasOdo = entity => runtime.EntityManager.HasComponent<Odometer>(entity),
                Odo = entity => runtime.EntityManager.GetComponentData<Odometer>(entity).m_Distance,
                HasMoving = entity => runtime.EntityManager.HasComponent<Game.Objects.Moving>(entity),
                Speed = entity => math.length(runtime.EntityManager.GetComponentData<Game.Objects.Moving>(entity).m_Velocity),
                Range = runtime.VehicleMaintenanceRange,
                Frame = () => runtime.m_SimulationSystem.frameIndex,
                ToFramesCeil = gameMinutes => runtime.m_SimClock.Snapshot.ToFramesCeil(gameMinutes),
                ToMinutes = runtime.m_SimClock.ToMinutes,
                LineId = runtime.LineStableId,
                Name = runtime.EntityName,
                LineOf = entity => runtime.m_VehicleView.TryGetLine(entity, out Entity line) ? line : Entity.Null,
                SlotOf = entity => runtime.m_VehicleView.TryGetSlot(entity, out int slot) ? slot : -1,
                CachedWp = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                Express = line => runtime.m_LineView.Express(line),
                Waypoints = line => runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true),
                HasWaypoints = line => runtime.EntityManager.HasBuffer<RouteWaypoint>(line),
                Stop = runtime.m_Resolve.Stop,
                Anchor = runtime.m_Resolve.Anchor,
                AnchorFromStop = runtime.m_Resolve.AnchorFromStop,
                EnsureSak = runtime.m_Resolve.EnsureSak,
                StationOf = runtime.m_Resolve.StationOf,
                ResolveStation = runtime.m_Resolve.PassingStation,
                RouteProgress = runtime.m_RouteProgress.Try,
                FlushLap = runtime.m_LapCache.Flush,
                FlushSlice = (line, sliceIndex, observation) => runtime.m_ObsBuffers.Flush(line, sliceIndex, observation),
                FlushStationDwell = (observationKey, observation) => runtime.m_ObsBuffers.Flush(observationKey, observation),
                HotPathProbe = runtime.m_RuntimeHotPathProbe,
                SetDeadline = runtime.m_RuntimeFramePlan.SetDeadline,
                ClearDeadline = runtime.m_RuntimeFramePlan.ClearDeadline,
                Log = message => runtime.log.Info(message)
            };
        }

        public static SliceAdmissionPort BuildSliceAdmission(ModRuntimeHostSystem runtime)
        {
            return new SliceAdmissionPort
            {
                StableKey = line => runtime.m_LineView.TryFrame(line, out LineFrame frame)
                    && LineKey.IsStableGuidKey(frame.StoreKey)
                        ? (true, frame.StoreKey)
                        : (false, LineKey.Empty),
                ServiceDate = () => runtime.m_SimClock.NowDate,
                DepartureMinutes = line => runtime.m_LineView.Times(line),
                FormatMinute = minute => ModRuntimeHostSystem.SlotStr(minute),
                ProfileSignature = line => runtime.m_ObsBuffers.TrySliceSignature(line, out ulong signature)
                    ? (true, signature)
                    : (false, 0UL),
                TryFlushDailyQuota = runtime.m_ObsBuffers.TryFlushDailyQuota,
                TryFlushColdStart = runtime.m_ObsBuffers.TryFlushColdStart,
                RemoveColdStart = runtime.m_ObsBuffers.RemoveColdStart,
                Log = message => runtime.log.Info(message)
            };
        }

        public static Port BuildObservation(ModRuntimeHostSystem runtime)
        {
            return new Port
            {
                Store = runtime.m_Obs,
                Frame = () => runtime.m_SimulationSystem != null ? runtime.m_SimulationSystem.frameIndex : 0,
                Date = () => runtime.m_SimClock != null ? runtime.m_SimClock.NowDate : DateTime.MinValue.Date,
                LoadApplied = runtime.LoadApplied,
                Lines = () => runtime.m_Observation.Lines(),
                Contracts = () => runtime.m_Observation.Contracts(),
                Preferred = () => runtime.DraftStore().Preferred(),
                LineId = runtime.LineStableId,
                StationName = runtime.m_Resolve.StationName,
                StopName = RuntimeRoot.StopService(runtime).Name,
                StopId = runtime.m_Resolve.StopId,
                OriginId = Stops.OriginId,
                Origin = line =>
                {
                    runtime.m_Resolve.Origin(line, out string id, out string name);
                    return (id, name);
                },
                Stop = runtime.m_Resolve.Stop,
                HasWaypoints = line => line != Entity.Null && runtime.EntityManager.HasBuffer<RouteWaypoint>(line),
                Waypoints = line => runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true),
                TargetMinute = entity => runtime.m_Observation.TargetMinute(entity),
                LineOf = runtime.m_Resolve.Line,
                Parse = RapidTransitMod.Dispatch.Workbench.Time.Parse,
                Slot = RapidTransitMod.Dispatch.Workbench.Time.Slot,
                Json = Workbenches.Json.Write,
                Log = message => runtime.log.Info(message),
                ClockSnapshot = () => runtime.m_SimClock.Snapshot
            };
        }
    }
}
