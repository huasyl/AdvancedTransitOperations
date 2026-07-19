using System;
using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.Core;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal delegate bool TryLineRouteProgress(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);
    internal delegate bool TryLineLapFrames(Entity vehicle, out uint lapFrames);
    internal delegate bool TryLineLapStartFrame(Entity vehicle, out uint lapStartFrame);
    internal delegate bool TryLineObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames);
    internal delegate ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> waypoints);
    internal delegate Entity ResolveLineWaypointBuilding(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);

    internal sealed class LineHost
    {
        public LineTimesPort Times = null!;
        public LineMileagePort Mileage = null!;
    }

    internal sealed class LineTimesPort
    {
        public EntityManager EntityManager;
        public Func<ulong, int, ulong> MixSignature = null!;
        public Func<ClockSnapshot> ClockSnapshot = null!;
        public float ProfileStopStartBufferMinutes;
        public float EtaScaleMin;
        public float EtaScaleMax;
        public float DispatchFallbackFramesPerMeter;
        public float DispatchEstimateMinFrames;
        public float DispatchEstimateDefaultFrames;
        public float DispatchEstimateMaxFrames;
        public Func<Entity, float> ReadLapFrames = null!;
        public Func<Entity, float> ReadDispatchFrames = null!;
        public Func<Entity, int> DwellMinutes = null!;
        public TryLineObservedWaypointStopFrames TryObservedWaypointStopFrames = null!;
        public TryLineRouteProgress TryRouteProgress = null!;
        public Func<Entity, int> CachedWaypointIndex = null!;
        public Func<Entity, bool> IsPreparingKnown = null!;
        public TryLineLapFrames TryLapFrames = null!;
        public TryLineLapStartFrame TryLapStartFrame = null!;
    }

    internal sealed class LineMileagePort
    {
        public EntityManager EntityManager;
        public Func<uint> Frame = null!;
        public Func<ulong, int, ulong> MixSignature = null!;
        public Action<string> Log = null!;
        public Func<Entity, string> Name = null!;
        public ComputeLineWaypointSignature WaypointSignature = null!;
        public ResolveLineWaypointBuilding StationBuildingForWaypoint = null!;
        public ResolveLineWaypointBuilding BypassBuildingForWaypoint = null!;
        public Func<Entity, bool> IsBypassStation = null!;
        public Func<IReadOnlyDictionary<string, AppliedLine>> AppliedLines = null!;
        public Func<Entity, bool> IsLocalLine = null!;
        public TryLineRouteProgress TryRouteProgress = null!;
        public Func<Entity, int> CachedWaypointIndex = null!;
        public Func<Entity, Entity> ResolveStop = null!;
    }
}
