using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Bypass
{
    internal interface IControlContext
    {
        uint Frame { get; }
        bool IsBypassRuntimeLoggingEnabled();
        void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message);
        Entity ResolveStation(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
        void RecordHold(Entity vehicle, Entity blocker, string lineTag, Entity holdStation, int waypointIndex, string stateTag);
        void RecordRelease(Entity vehicle, Entity blocker, string reason);
        void TriggerWaiting(Entity vehicle, Entity route, DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex);
    }
}
