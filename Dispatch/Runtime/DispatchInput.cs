using RapidTransitMod.Bypass;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal readonly struct DispatchInput
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly Entity Route;
        public readonly bool InputValid;
        public readonly bool Boarding;
        public readonly int PreviousWaypoint;
        public readonly int CurrentWaypoint;
        public readonly int WaypointCount;
        public readonly bool AtOrigin;
        public readonly bool PreparingAtOrigin;
        public readonly bool OriginBusy;
        public readonly bool PreparingRouteNeedsRepair;
        public readonly bool ShouldEvaluateOriginSettle;
        public readonly bool SettledAtOrigin;
        public readonly bool ForcedAtOrigin;
        public readonly bool BrokenRecoveredRun;
        public readonly bool Moving;
        public readonly bool RunDistanceReady;
        public readonly float TravelledDistance;
        public readonly float ObservedLapDistance;
        public readonly bool HadStopSession;
        public readonly bool BoardingChanged;
        public readonly BypassControlResult BypassControl;

        public DispatchInput(
            Entity vehicle,
            Entity line,
            Entity route,
            bool inputValid,
            bool boarding,
            int previousWaypoint,
            int currentWaypoint,
            int waypointCount,
            bool atOrigin,
            bool preparingAtOrigin,
            bool originBusy,
            bool preparingRouteNeedsRepair,
            bool shouldEvaluateOriginSettle,
            bool settledAtOrigin,
            bool forcedAtOrigin,
            bool brokenRecoveredRun,
            bool moving,
            bool runDistanceReady,
            float travelledDistance,
            float observedLapDistance,
            bool hadStopSession,
            bool boardingChanged,
            BypassControlResult bypassControl)
        {
            Vehicle = vehicle;
            Line = line;
            Route = route;
            InputValid = inputValid;
            Boarding = boarding;
            PreviousWaypoint = previousWaypoint;
            CurrentWaypoint = currentWaypoint;
            WaypointCount = waypointCount;
            AtOrigin = atOrigin;
            PreparingAtOrigin = preparingAtOrigin;
            OriginBusy = originBusy;
            PreparingRouteNeedsRepair = preparingRouteNeedsRepair;
            ShouldEvaluateOriginSettle = shouldEvaluateOriginSettle;
            SettledAtOrigin = settledAtOrigin;
            ForcedAtOrigin = forcedAtOrigin;
            BrokenRecoveredRun = brokenRecoveredRun;
            Moving = moving;
            RunDistanceReady = runDistanceReady;
            TravelledDistance = travelledDistance;
            ObservedLapDistance = observedLapDistance;
            HadStopSession = hadStopSession;
            BoardingChanged = boardingChanged;
            BypassControl = bypassControl;
        }
    }
}
