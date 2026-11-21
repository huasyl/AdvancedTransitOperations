using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal readonly struct DispatchInput
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly bool InputValid;
        public readonly bool Boarding;
        public readonly int CurrentWaypoint;
        public readonly bool AtOrigin;
        public readonly bool TargetAtOrigin;
        public readonly uint OriginArrivalFrame;
        public readonly uint OriginMovingFrame;
        public readonly bool OriginSessionRecoveredAway;
        public readonly bool PreparingRouteNeedsRepair;
        public readonly bool OriginDepartureConfirmed;
        public readonly bool HoldingStopUnknown;

        public DispatchInput(
            Entity vehicle,
            Entity line,
            bool inputValid,
            bool boarding,
            int currentWaypoint,
            bool atOrigin,
            bool targetAtOrigin,
            uint originArrivalFrame,
            uint originMovingFrame,
            bool originSessionRecoveredAway,
            bool preparingRouteNeedsRepair,
            bool originDepartureConfirmed,
            bool holdingStopUnknown)
        {
            Vehicle = vehicle;
            Line = line;
            InputValid = inputValid;
            Boarding = boarding;
            CurrentWaypoint = currentWaypoint;
            AtOrigin = atOrigin;
            TargetAtOrigin = targetAtOrigin;
            OriginArrivalFrame = originArrivalFrame;
            OriginMovingFrame = originMovingFrame;
            OriginSessionRecoveredAway = originSessionRecoveredAway;
            PreparingRouteNeedsRepair = preparingRouteNeedsRepair;
            OriginDepartureConfirmed = originDepartureConfirmed;
            HoldingStopUnknown = holdingStopUnknown;
        }
    }
}
