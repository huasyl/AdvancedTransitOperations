using System;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
using RapidTransitMod.SignalPriority;
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
        internal delegate bool SessionArrival(Entity vehicle, out uint arrivalFrame);
        internal delegate bool SignalInfo(Entity vehicle, out SignalDisplay display);
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
        internal TimeSystem Time;
        internal SimulationSystem Sim;
        internal Func<ClockSnapshot> ClockSnapshot;
        internal NameSystem Names;
        internal EndFrameBarrier Barrier;
        internal VehicleView Vehicles;
        internal LineView Lines;
        internal RapidTransitMod.Dispatch.Observation.Query Obs;
        internal Func<Entity, int> SpawnTarget;
        internal NativeHashMap<Entity, int> CachedWp;
        internal DispatchCommandApplier Commands;
        internal DispatchEngine Runtime;
        internal RuntimeVehicleLabels Labels;
        internal RuntimeFramePlan FramePlan;

        internal Func<Entity, Entity, Entity> ResolveLine;
        internal Func<Entity, Entity> ResolveVehicle;
        internal Func<Entity, Entity> ResolveVehicleLine;
        internal Func<Entity, string> ResolveLineDisplayName;
        internal Func<Entity, float> ReadLap;
        internal Func<Entity, float> ReadLineDuration;
        internal Func<Entity, float> ReadDispatch;
        internal Func<bool, BufferLookup<RouteVehicle>> RouteVehicles;
        internal Func<bool, BufferLookup<RouteWaypoint>> RouteWaypoints;
        internal Func<Entity, DynamicBuffer<RouteWaypoint>, int> ComputeWp;
        internal LineTimes Times;
        internal Progress TryProgress;
        internal SessionArrival TrySessionArrival;
        internal StopSession TryStopSession;
        internal VehicleTimes TryVehicleTimes;
        internal StationText Stations;
        internal PanelStations TryPanelStations;
        internal SignalInfo TrySignalDisplay;
    }
}
