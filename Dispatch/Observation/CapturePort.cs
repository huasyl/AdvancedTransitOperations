using System;
using Game.Routes;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class CapturePort
    {
        internal Func<Entity, bool> Exists;
        internal Func<Entity, bool> HasOdo;
        internal Func<Entity, float> Odo;
        internal Func<Entity, float> Range;
        internal Func<uint> Frame;
        internal Func<double, uint> ToFramesCeil;
        internal Func<double, double> ToMinutes;
        internal Func<Entity, string> LineId;
        internal Func<Entity, Entity> LineOf;
        internal Func<Entity, int> SlotOf;
        internal Func<Entity, int> CachedWp;
        internal Func<Entity, DynamicBuffer<RouteWaypoint>> Waypoints;
        internal Func<Entity, bool> HasWaypoints;
        internal Func<Entity, Entity> Stop;
        internal Func<Entity, Entity> Anchor;
        internal Func<Entity, Entity> AnchorFromStop;
        internal Func<Entity, string> EnsureSak;
        internal Func<Entity, Entity> StationOf;
        internal Func<Entity, Entity> ResolveStation;
        internal Action<Entity> FlushLap;
        internal Action<string, StationDwellObservation> FlushStationDwell;
        internal Action<string> Log;
    }
}
