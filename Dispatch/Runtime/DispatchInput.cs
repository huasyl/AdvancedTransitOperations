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
        public readonly bool TargetAtOrigin;
        public readonly bool PreparingAtOrigin;
        public readonly bool OriginBusy;
        public readonly bool PreparingRouteNeedsRepair;
        public readonly bool ShouldEvaluateOriginSettle;
        public readonly bool IgnoreOriginCooldown;
        public readonly bool HadStopSession;
        public readonly bool BoardingChanged;
        public readonly bool OriginDeparturePending;
        public readonly bool OriginDepartureConfirmed;
        public readonly bool HoldingStopUnknown;
        public readonly double MinimumOriginDwellMinutes;
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
            bool targetAtOrigin,
            bool preparingAtOrigin,
            bool originBusy,
            bool preparingRouteNeedsRepair,
            bool shouldEvaluateOriginSettle,
            bool ignoreOriginCooldown,
            bool hadStopSession,
            bool boardingChanged,
            bool originDeparturePending,
            bool originDepartureConfirmed,
            bool holdingStopUnknown,
            double minimumOriginDwellMinutes,
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
            TargetAtOrigin = targetAtOrigin;
            PreparingAtOrigin = preparingAtOrigin;
            OriginBusy = originBusy;
            PreparingRouteNeedsRepair = preparingRouteNeedsRepair;
            ShouldEvaluateOriginSettle = shouldEvaluateOriginSettle;
            IgnoreOriginCooldown = ignoreOriginCooldown;
            HadStopSession = hadStopSession;
            BoardingChanged = boardingChanged;
            OriginDeparturePending = originDeparturePending;
            OriginDepartureConfirmed = originDepartureConfirmed;
            HoldingStopUnknown = holdingStopUnknown;
            MinimumOriginDwellMinutes = minimumOriginDwellMinutes;
            BypassControl = bypassControl;
        }
    }
}
