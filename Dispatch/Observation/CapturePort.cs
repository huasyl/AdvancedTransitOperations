using System;
using Game.Routes;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal delegate bool RouteProgressReader(Entity vehicle, out int nextWaypointIndex, out float segmentPosition);

    internal sealed class CapturePort
    {
        internal Func<Entity, bool> Exists;
        internal Func<Entity, bool> HasOdo;
        internal Func<Entity, float> Odo;
        internal Func<Entity, bool> HasMoving;
        internal Func<Entity, float> Speed;
        internal Func<Entity, float> Range;
        internal Func<uint> Frame;
        internal Func<double, uint> ToFramesCeil;
        internal Func<double, double> ToMinutes;
        internal Func<Entity, string> LineId;
        internal Func<Entity, string> Name;
        internal Func<Entity, Entity> LineOf;
        internal Func<Entity, int> SlotOf;
        internal Func<Entity, int> CachedWp;
        internal Func<Entity, bool> Express;
        internal Func<Entity, DynamicBuffer<RouteWaypoint>> Waypoints;
        internal Func<Entity, bool> HasWaypoints;
        internal Func<Entity, Entity> Stop;
        internal Func<Entity, Entity> Anchor;
        internal Func<Entity, Entity> AnchorFromStop;
        internal Func<Entity, string> EnsureSak;
        internal Func<Entity, Entity> StationOf;
        internal Func<Entity, Entity> ResolveStation;
        internal RouteProgressReader RouteProgress;
        internal Action<Entity> FlushLap;
        internal Action<Entity, int, TraversalSliceObservation> FlushSlice;
        internal Action<string, StationDwellObservation> FlushStationDwell;
        internal Action<string> Log;
        internal RuntimeHotPathProbe HotPathProbe;
        internal Action<Entity, DeadlineKind, uint> SetDeadline;
        internal Action<Entity, DeadlineKind> ClearDeadline;
    }
}
