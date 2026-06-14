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
        private readonly DispatchRuntimeSystem m_Runtime;

        public TrackBuffers(DispatchRuntimeSystem runtime)
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
        public static TrackModelContext.IBuffers Buffers(DispatchRuntimeSystem runtime)
        {
            return new TrackBuffers(runtime);
        }

        public static void Build(DispatchRuntimeSystem runtime)
        {
            runtime.m_SelectPort = BuildSelect(runtime);
            runtime.m_SelectPanel = new SelectPanel(runtime.m_SelectPort);
            runtime.m_PlannerPort = new PlannerPort(runtime);
            runtime.m_PlannerExport = new PlannerExport(runtime.m_PlannerPort);
            runtime.m_PlannerJobs = new PlannerJobs(runtime.m_PlannerExport, new DispatchWorkbenchPlannerService());
            runtime.m_PlannerApi = new PlannerApi(runtime.m_PlannerExport, runtime.m_PlannerJobs);
        }

        private static SelectPort BuildSelect(DispatchRuntimeSystem runtime)
        {
            return new SelectPort
            {
                EntityManager = runtime.EntityManager,
                Log = runtime.log,
                Time = runtime.m_TimeSystem,
                Sim = runtime.m_SimulationSystem,
                Names = runtime.m_NameSystem,
                City = runtime.m_CitySystem,
                Barrier = runtime.m_EndFrameBarrier,
                Vehicles = runtime.m_VehicleView,
                Lines = runtime.m_LineView,
                Obs = runtime.m_ObsQuery,
                Spawns = runtime.m_SpawningLines,
                SpawnFrames = runtime.m_LineSpawnRequestFrame,
                CachedWp = runtime.m_CachedWpIdx,
                Misfires = runtime.m_BVMisfire,
                Commands = runtime.m_CommandApplier,
                Runtime = runtime.m_RuntimeController,
                Scheduler = runtime.m_DispatchScheduler,
                Labels = runtime.m_VehicleLabels,
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
                }
            };
        }

        public static LineHost BuildLineHost(DispatchRuntimeSystem runtime)
        {
            return new LineHost
            {
                Times = new LineTimesPort
                {
                    EntityManager = runtime.EntityManager,
                    MixSignature = runtime.m_LineProfile.MixSignature,
                    FramesPerMinute = (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE,
                    ProfileStopStartBufferMinutes = DispatchRuntimeSystem.PROFILE_STOP_START_BUFFER_MINUTES,
                    EtaScaleMin = DispatchRuntimeSystem.ETA_SCALE_MIN,
                    EtaScaleMax = DispatchRuntimeSystem.ETA_SCALE_MAX,
                    DispatchFallbackSpeedMetersPerMinute = DispatchRuntimeSystem.DISPATCH_FALLBACK_SPEED_M_PER_MIN,
                    DispatchEstimateMinMinutes = DispatchRuntimeSystem.DISPATCH_ESTIMATE_MIN_MINUTES,
                    DispatchEstimateMaxMinutes = DispatchRuntimeSystem.DISPATCH_ESTIMATE_MAX_MINUTES,
                    ReadLapFrames = runtime.m_LapCache.Read,
                    ReadDispatchFrames = runtime.m_DispatchCache.Read,
                    DwellMinutes = line => runtime.m_LineView.Dwell(line),
                    TryObservedWaypointStopFrames = (Entity line, int waypointIndex, out float dwellFrames) =>
                        runtime.m_Observation.TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames),
                    TryRouteProgress = runtime.m_RouteProgress.Try,
                    CachedWaypointIndex = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                    IsPreparingKnown = entity => runtime.m_VehicleStateStore.PreparingStartFrame.ContainsKey(entity),
                    TryLapFrames = (Entity vehicle, out uint lapFrames) => runtime.m_Observation.TryLapFrames(vehicle, out lapFrames),
                    TryLapStartFrame = (Entity vehicle, out uint lapStartFrame) => runtime.m_Observation.TryLapStartFrame(vehicle, out lapStartFrame)
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

        public static TrackProjectionPort BuildTrackProjection(DispatchRuntimeSystem runtime)
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
                runtime.IsVehicleBoarding);
        }

        public static BypassAdmissionPort BuildBypassAdmission(DispatchRuntimeSystem runtime)
        {
            return new BypassAdmissionPort(
                runtime.EntityManager,
                runtime.log,
                () => runtime.m_SimulationSystem.frameIndex,
                () => runtime.AppliedLines,
                runtime.m_TrackModel,
                () => runtime.m_TrackProjection,
                Buffers(runtime),
                () => runtime.m_Features.BypassRun(),
                line => runtime.m_LineView.Managed(line, runtime.m_Features.Dispatch()),
                line => runtime.m_LineView.Local(line),
                line => runtime.m_LineView.Express(line),
                runtime.m_Resolve,
                DispatchRuntimeSystem.IsLineOrderedRuntimeLoggingEnabled,
                runtime.m_WaypointIndex,
                runtime.m_Observation,
                runtime.m_SharedCorridor,
                runtime.m_RuntimeLog.Once,
                runtime.m_VehicleView,
                runtime.m_LineMileage,
                runtime.m_LineTimes,
                runtime.EntityName,
                runtime.m_RuntimeHotPathProbe);
        }

        public static BypassRuntimePort BuildBypassRuntime(DispatchRuntimeSystem runtime)
        {
            return new BypassRuntimePort(
                runtime.EntityManager,
                runtime.log,
                () => runtime.m_SimulationSystem.frameIndex,
                () => runtime.AppliedLines,
                runtime.m_TrackModel,
                () => runtime.m_TrackProjection,
                Buffers(runtime),
                () => runtime.m_Features.BypassRun(),
                line => runtime.m_LineView.Managed(line, runtime.m_Features.Dispatch()),
                line => runtime.m_LineView.Local(line),
                line => runtime.m_LineView.Express(line),
                runtime.m_Resolve,
                DispatchRuntimeSystem.IsLineOrderedRuntimeLoggingEnabled,
                runtime.m_WaypointIndex,
                runtime.m_Observation,
                runtime.m_SharedCorridor,
                runtime.m_RuntimeLog.Once,
                runtime.m_VehicleView,
                runtime.m_LineMileage,
                runtime.m_LineTimes,
                runtime.EntityName,
                runtime.m_RuntimeHotPathProbe,
                DispatchRuntimeSystem.IsBypassRuntimeLoggingEnabled,
                runtime.m_Observation.Hold,
                runtime.m_Observation.Release,
                runtime.m_Announcements.BypassWaiting,
                () => runtime.m_Features.BypassRun(),
                runtime.m_LineTimes.Clear);
        }

        public static CapturePort BuildCapture(DispatchRuntimeSystem runtime)
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
                FramesPerMinute = () => (int)math.round((float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE),
                LineId = runtime.LineId,
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
                Log = message => runtime.log.Info(message)
            };
        }

        public static Port BuildObservation(DispatchRuntimeSystem runtime)
        {
            return new Port
            {
                Store = runtime.m_Obs,
                Frame = () => runtime.m_SimulationSystem != null ? runtime.m_SimulationSystem.frameIndex : 0,
                Date = () => runtime.m_TimeSystem != null ? runtime.m_TimeSystem.GetCurrentDateTime().Date : DateTime.MinValue.Date,
                LoadApplied = runtime.LoadApplied,
                Lines = () => runtime.m_Observation.Lines(),
                Contracts = () => runtime.m_Observation.Contracts(),
                Preferred = () => runtime.DraftStore().Preferred(),
                LineId = runtime.LineId,
                StationName = runtime.m_Resolve.StationName,
                StopName = runtime.m_WorkbenchBridge.StopSvc().Name,
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
                TargetMin = entity => runtime.m_Observation.TargetMin(entity),
                LineOf = runtime.m_Resolve.Line,
                Parse = RapidTransitMod.Dispatch.Workbench.Time.Parse,
                Slot = RapidTransitMod.Dispatch.Workbench.Time.Slot,
                Json = Workbenches.Json.Write,
                Log = message => runtime.log.Info(message),
                FramesPerMinute = DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE
            };
        }
    }
}
