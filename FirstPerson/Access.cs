using System;
using RapidTransitMod.Core;
using RapidTransitMod.SignalPriority;
using RapidTransitMod.ViewTracking;
using Unity.Entities;

namespace RapidTransitMod.FirstPerson
{
    internal sealed class FirstPersonAccess
    {
        internal delegate bool VehicleStateRead(Entity vehicle, out VehicleState state);
        internal delegate bool StopSessionRead(Entity vehicle, out Entity line, out int waypoint, out uint arrivalFrame);
        internal delegate bool TargetRead(Entity vehicle, out int minute);
        internal delegate bool VehicleTimesRead(Entity vehicle, out int currentWaypoint, out int nextWaypoint, out int nextArrival, out int plannedArrival, out int actualArrival, out int plannedDeparture);
        internal delegate bool SignalRead(Entity vehicle, out SignalDisplay display);

        internal Func<FollowView> ReadContext;
        internal VehicleStateRead TryVehicleState;
        internal StopSessionRead TryStopSession;
        internal TargetRead TryTargetMinute;
        internal VehicleTimesRead TryVehicleTimes;
        internal Func<ClockSnapshot> ReadClock;
        internal Func<uint> ReadFrame;
        internal SignalRead TrySignalDisplay;
        internal Func<Entity, string> ReadName;
    }
}
