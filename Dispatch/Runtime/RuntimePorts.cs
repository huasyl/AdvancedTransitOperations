using System;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
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

        public ComponentLookup<T> GetComponent<T>(bool readOnly) where T : unmanaged, IComponentData
        {
            return m_Runtime.GetComponentLookup<T>(readOnly);
        }
    }

    internal static class RuntimePorts
    {
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

        public static void Build(ModRuntimeHostSystem runtime)
        {
            runtime.m_FirstPersonQuery = new FirstPerson.FirstPersonQuery(new FirstPerson.FirstPersonAccess
            {
                ReadContext = runtime.m_FollowContext.ReadContext,
                TryVehicleState = runtime.m_VehicleView.TryGetState,
                TryStopSession = runtime.m_StopRuntime.TryGetSession,
                TryTargetMinute = runtime.m_VehicleView.TryGetTarget,
                TryVehicleTimes = runtime.m_ObsRecorder.TryVehicleTimes,
                ReadClock = () => runtime.m_SimClock.Snapshot,
                ReadFrame = () => runtime.m_SimulationSystem.frameIndex,
                TrySignalDisplay = runtime.m_TransitSignalRuntime.TryReadDisplay,
                ReadName = entity => runtime.m_NameSystem.GetRenderedLabelName(entity)
            });
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
                Time = runtime.m_TimeSystem,
                Sim = runtime.m_SimulationSystem,
                ClockSnapshot = () => runtime.m_SimClock.Snapshot,
                Names = runtime.m_NameSystem,
                Barrier = runtime.m_EndFrameBarrier,
                Vehicles = runtime.m_VehicleView,
                Lines = runtime.m_LineView,
                Obs = runtime.m_ObsQuery,
                SpawnTarget = runtime.m_LineSpawnControl.DisplayTarget,
                CachedWp = runtime.m_CachedWpIdx,
                Commands = runtime.m_CommandApplier,
                Runtime = runtime.m_RuntimeEngine,
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
                ReadLap = runtime.m_LapCache.Read,
                ReadLineDuration = line =>
                {
                    if (line == Entity.Null)
                        return 0f;

                    if (TransportModeResolver.Resolve(runtime.EntityManager, line) == TransitMode.Bus)
                        return runtime.m_LineTimes.Duration(line) * 60f;

                    if (runtime.m_LineTimes.TryMonitorLoopFrames(line, out float monitorFrames))
                        return monitorFrames;

                    float durationFrames = runtime.m_LineTimes.Duration(line) * 60f;
                    if (durationFrames > 0f && durationFrames < float.MaxValue)
                        return durationFrames;

                    return runtime.m_LapCache.Read(line);
                },
                ReadDispatch = runtime.m_DispatchCache.Read,
                RouteVehicles = runtime.GetBufferLookup<RouteVehicle>,
                RouteWaypoints = runtime.GetBufferLookup<RouteWaypoint>,
                ComputeWp = runtime.m_WaypointIndex.Compute,
                Times = runtime.m_LineTimes,
                TryProgress = runtime.m_RouteProgress.Try,
                TrySessionArrival = runtime.m_StopRuntime.TryGetSessionArrivalFrame,
                TryStopSession = runtime.m_StopRuntime.TryGetSession,
                TryVehicleTimes = runtime.m_ObsRecorder.TryVehicleTimes,
                TrySignalDisplay = runtime.m_TransitSignalRuntime.TryReadDisplay,
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
                    DispatchEstimateMinFrames = ModRuntimeHostSystem.DISPATCH_ESTIMATE_MIN_FRAMES,
                    DispatchEstimateDefaultFrames = ModRuntimeHostSystem.DISPATCH_ESTIMATE_DEFAULT_FRAMES,
                    ReadLapFrames = runtime.m_LapCache.Read,
                    DwellMinutes = line => runtime.m_LineView.Dwell(line),
                    TryObservedWaypointStopFrames = (Entity line, int waypointIndex, out float dwellFrames) =>
                        runtime.m_Observation.TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames),
                    TryRouteProgress = runtime.m_RouteProgress.Try,
                    CachedWaypointIndex = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                    TryLapFrames = (Entity vehicle, out uint lapFrames) => runtime.m_Observation.TryLapFrames(vehicle, out lapFrames),
                    TryLapStartFrame = (Entity vehicle, out uint lapStartFrame) => runtime.m_Observation.TryLapStartFrame(vehicle, out lapStartFrame),
                    TryBusSegFrames = (Entity line, Entity fromWaypoint, Entity fromStop, Entity toWaypoint, Entity toStop, out float frames) =>
                        runtime.m_Observation.TryBusSegFrames(line, fromWaypoint, fromStop, toWaypoint, toStop, out frames),
                    ResolveMode = line => TransportModeResolver.Resolve(runtime.EntityManager, line),
                    IsLinePending = runtime.m_LineStructureInvalidator.IsLinePending
                },
                Mileage = new LineMileagePort
                {
                    EntityManager = runtime.EntityManager,
                    Frame = () => runtime.m_SimulationSystem.frameIndex,
                    MixSignature = runtime.m_LineProfile.MixSignature,
                    Log = message => runtime.log.Info(message),
                    Name = entity => runtime.m_NameSystem.GetRenderedLabelName(entity),
                    WaypointSignature = runtime.m_LineProfile.ComputeWaypointSignature,
                    StationBuildingForWaypoint = runtime.m_Resolve.GetStationBuildingForWaypoint,
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
                runtime.m_RouteProgress,
                runtime.m_WaypointIndex,
                runtime.m_RailEventSource,
                runtime.m_RuntimeHotPathProbe,
                runtime.m_LineStructureInvalidator.IsLinePending,
                vehicle => runtime.m_StopRuntime != null && runtime.m_StopRuntime.HasOpenStopSession(vehicle),
                (vehicle, line) => runtime.m_StopRuntime != null
                    && runtime.m_StopRuntime.IsDeparturePending(vehicle)
                    && runtime.m_StopRuntime.TryGetSession(vehicle, out Entity sessionLine, out int waypoint, out _)
                    && sessionLine == line
                        ? waypoint
                        : -1);
        }

        public static CapturePort BuildCapture(ModRuntimeHostSystem runtime)
        {
            return new CapturePort
            {
                Exists = entity => runtime.EntityManager.Exists(entity),
                HasOdo = entity => runtime.EntityManager.HasComponent<Odometer>(entity),
                Odo = entity => runtime.EntityManager.GetComponentData<Odometer>(entity).m_Distance,
                Range = runtime.VehicleMaintenanceRange,
                Frame = () => runtime.m_SimulationSystem.frameIndex,
                ToFramesCeil = gameMinutes => runtime.m_SimClock.Snapshot.ToFramesCeil(gameMinutes),
                ToMinutes = runtime.m_SimClock.ToMinutes,
                LineId = runtime.LineStableId,
                LineOf = entity => runtime.m_VehicleView.TryGetLine(entity, out Entity line) ? line : Entity.Null,
                SlotOf = entity => runtime.m_VehicleView.TryGetSlot(entity, out int slot) ? slot : -1,
                CachedWp = entity => runtime.m_CachedWpIdx.TryGetValue(entity, out int waypointIndex) ? waypointIndex : -1,
                Waypoints = line => runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true),
                HasWaypoints = line => runtime.EntityManager.HasBuffer<RouteWaypoint>(line),
                Stop = runtime.m_Resolve.Stop,
                Anchor = runtime.m_Resolve.Anchor,
                AnchorFromStop = runtime.m_Resolve.AnchorFromStop,
                EnsureSak = runtime.m_Resolve.EnsureSak,
                StationOf = runtime.m_Resolve.StationOf,
                ResolveStation = runtime.m_Resolve.PassingStation,
                FlushLap = runtime.m_LapCache.Flush,
                FlushStationDwell = (observationKey, observation) => runtime.m_ObsBuffers.Flush(observationKey, observation),
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
                Origin = line =>
                {
                    runtime.m_Resolve.Origin(line, out string id, out string name);
                    return (id, name);
                },
                Stop = runtime.m_Resolve.Stop,
                HasWaypoints = line => line != Entity.Null && runtime.EntityManager.HasBuffer<RouteWaypoint>(line),
                Waypoints = line => runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true),
                LineOf = runtime.m_Resolve.Line,
                Parse = RapidTransitMod.Dispatch.Workbench.Time.Parse,
                Slot = RapidTransitMod.Dispatch.Workbench.Time.Slot,
                Json = Workbenches.Json.Write,
                Log = message => runtime.log.Info(message),
                ClockSnapshot = () => runtime.m_SimClock.Snapshot,
                IsLinePending = runtime.m_LineStructureInvalidator.IsLinePending
            };
        }
    }
}
