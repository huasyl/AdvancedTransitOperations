using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Port
    {
        internal TraceStore Store;
        internal Func<uint> Frame;
        internal Func<DateTime> Date;
        internal Action LoadApplied;
        internal Func<IReadOnlyDictionary<string, LinePlan>> Lines;
        internal Func<ContractDto[]> Contracts;
        internal Func<string> Preferred;
        internal Func<Entity, string> LineId;
        internal Func<Entity, string> StationName;
        internal Func<Entity, ResolvedStopKind, string> StopName;
        internal Func<Entity, ResolvedStopKind, string> StopId;
        internal Func<Entity, string> OriginId;
        internal Func<Entity, (string Id, string Name)> Origin;
        internal Func<Entity, Entity> Stop;
        internal Func<Entity, bool> HasWaypoints;
        internal Func<Entity, DynamicBuffer<RouteWaypoint>> Waypoints;
        internal Func<Entity, int> TargetMin;
        internal Func<Entity, Entity> LineOf;
        internal Func<string, int> Parse;
        internal Func<int, string> Slot;
        internal Func<SnapshotDto, string> Json;
        internal Action<string> Log;
        internal double FramesPerMinute;
    }
}
