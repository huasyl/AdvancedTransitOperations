using System;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
using Game;
using Game.Common;
using Game.Routes;
using Game.Simulation;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SelectPort
    {
        internal delegate bool Progress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);
        internal delegate bool Blocker(Entity vehicle, out Entity blockerVehicle);
        internal delegate bool SessionArrival(Entity vehicle, out uint arrivalFrame);
        internal delegate bool StopSession(
            Entity vehicle,
            out Entity line,
            out int waypointIndex,
            out uint arrivalFrame);
        internal delegate bool VehicleTimes(
            Entity vehicle,
            out int currentWaypointIndex,
            out int nextWaypointIndex,
            out int nextArrival,
            out int plannedArrival,
            out int actualArrival,
            out int plannedDeparture);
        internal delegate bool PanelStations(
            Entity vehicle,
            Entity line,
            int preferredCurrentWaypointIndex,
            bool includePhysical,
            out VehicleStationContext context);
        internal delegate void StationText(
            Entity vehicle,
            Entity line,
            out string currentStationName,
            out string nextPhysicalStationName,
            out string nextStopStationName,
            out bool nextPhysicalIsPass);

        internal EntityManager EntityManager;
        internal TimedLogger Log;
        internal TimeSystem Time;
        internal SimulationSystem Sim;
        internal Func<ClockSnapshot> ClockSnapshot;
        internal NameSystem Names;
        internal CitySystem City;
        internal EndFrameBarrier Barrier;
        internal VehicleView Vehicles;
        internal LineView Lines;
        internal RapidTransitMod.Dispatch.Observation.Query Obs;
        internal NativeHashMap<Entity, int> Spawns;
        internal NativeHashMap<Entity, uint> SpawnFrames;
        internal NativeHashMap<Entity, int> CachedWp;
        internal DispatchCommandApplier Commands;
        internal DispatchEngine Runtime;
        internal DispatchScheduler Scheduler;
        internal RuntimeVehicleLabels Labels;
        internal RuntimeFramePlan FramePlan;

        internal Func<Entity, Entity, Entity> ResolveLine;
        internal Func<Entity, Entity> ResolveVehicle;
        internal Func<Entity, Entity> ResolveVehicleLine;
        internal Func<Entity, string> ResolveLineDisplayName;
        internal Func<Entity, Entity> ResolveBypassBuilding;
        internal Action EnsureBypassBuffer;
        internal Action InvalidateBypassModel;
        internal Func<Entity, float> ReadLap;
        internal Func<Entity, float> ReadLineDuration;
        internal Func<Entity, float> ReadDispatch;
        internal Func<bool, BufferLookup<RouteVehicle>> RouteVehicles;
        internal Func<bool, BufferLookup<RouteWaypoint>> RouteWaypoints;
        internal Func<Entity, BufferLookup<RouteVehicle>, int> CountVehicles;
        internal Func<Entity, DynamicBuffer<RouteWaypoint>, int> ComputeWp;
        internal Func<Entity, Entity, DynamicBuffer<RouteWaypoint>, uint, float, float> PrepEta;
        internal Func<Entity, Entity, DynamicBuffer<RouteWaypoint>, uint, float, bool, float> RunEta;
        internal Progress TryProgress;
        internal Blocker TryBlocker;
        internal SessionArrival TrySessionArrival;
        internal StopSession TryStopSession;
        internal VehicleTimes TryVehicleTimes;
        internal Action<Entity, string> ClearBypass;
        internal StationText Stations;
        internal PanelStations TryPanelStations;
    }
}
