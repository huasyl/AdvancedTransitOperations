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
    internal static class RuntimePorts
    {
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
                ResolveBypassBuilding = runtime.m_Resolve.PassingStation,
                EnsureBypassBuffer = runtime.m_BypassStore.Ensure,
                ReadLap = runtime.m_LapCache.Read,
                ReadDispatch = runtime.m_DispatchCache.Read,
                RouteVehicles = runtime.GetBufferLookup<RouteVehicle>,
                RouteWaypoints = runtime.GetBufferLookup<RouteWaypoint>,
                CountVehicles = runtime.m_LineVehicles.Count,
                ComputeWp = runtime.m_WaypointIndex.Compute,
                PrepEta = runtime.EstimatePreparingArrivalFrames,
                RunEta = runtime.EstimateRunningArrivalFrames,
                TryProgress = runtime.m_RouteProgress.Try,
                TryBlocker = (Entity vehicle, out Entity blocker) => runtime.m_Bypass.TryGetLatchedBlocker(vehicle, out blocker),
                ClearBypass = (vehicle, reason) => runtime.m_Bypass.ClearVehicle(vehicle, reason),
                Stations = (Entity vehicle, Entity line, out string current, out string next) =>
                {
                    runtime.m_Announcements.TryPanelContext(vehicle, line, out current, out next, out _);
                },
                EventText = runtime.m_Announcements.EventText
            };
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
                Lines = runtime.BuildObservationLines,
                Contracts = runtime.BuildObservationContracts,
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
                TargetMin = runtime.ObservationTargetMin,
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
