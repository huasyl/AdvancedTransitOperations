using RapidTransitMod.Bypass;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal readonly struct DispatchInput
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly Entity Route;
        public readonly uint SourceFrame;
        public readonly ulong SourceGeneration;
        public readonly bool InputValid;
        public readonly bool HasTarget;
        public readonly bool Boarding;
        public readonly int PreviousWaypoint;
        public readonly int CurrentWaypoint;
        public readonly int WaypointCount;
        public readonly bool WaypointRefreshed;
        public readonly bool AtOrigin;
        public readonly bool PreparingAtOrigin;
        public readonly bool TargetAtOrigin;
        public readonly bool OriginBusy;
        public readonly bool PreparingRouteNeedsRepair;
        public readonly bool PathReady;
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
        public readonly bool HasForcedMidStopGrace;
        public readonly BypassControlResult BypassControl;

        public DispatchInput(
            Entity vehicle,
            Entity line,
            Entity route,
            uint sourceFrame,
            ulong sourceGeneration,
            bool inputValid,
            bool hasTarget,
            bool boarding,
            int previousWaypoint,
            int currentWaypoint,
            int waypointCount,
            bool waypointRefreshed,
            bool atOrigin,
            bool preparingAtOrigin,
            bool targetAtOrigin,
            bool originBusy,
            bool preparingRouteNeedsRepair,
            bool pathReady,
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
            bool hasForcedMidStopGrace,
            BypassControlResult bypassControl)
        {
            Vehicle = vehicle;
            Line = line;
            Route = route;
            SourceFrame = sourceFrame;
            SourceGeneration = sourceGeneration;
            InputValid = inputValid;
            HasTarget = hasTarget;
            Boarding = boarding;
            PreviousWaypoint = previousWaypoint;
            CurrentWaypoint = currentWaypoint;
            WaypointCount = waypointCount;
            WaypointRefreshed = waypointRefreshed;
            AtOrigin = atOrigin;
            PreparingAtOrigin = preparingAtOrigin;
            TargetAtOrigin = targetAtOrigin;
            OriginBusy = originBusy;
            PreparingRouteNeedsRepair = preparingRouteNeedsRepair;
            PathReady = pathReady;
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
            HasForcedMidStopGrace = hasForcedMidStopGrace;
            BypassControl = bypassControl;
        }
    }
}
