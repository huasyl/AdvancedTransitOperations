using System;
using System.Collections.Generic;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal static class RailEtaPredictionContractValidator
    {
        private const int MaxContractStringLength = 256;
        private const int MaxReasonLength = 512;
        private const int MaxStageTimings = 32;

        public static bool TryValidate(RailEtaPrediction prediction, RailEtaWorldSnapshot snapshot, RailEtaRequest request,
            RailEtaWorkspace workspace, out string detail)
        {
            detail = string.Empty;
            if (prediction == null || snapshot == null || request == null || workspace == null) return Invalid(out detail, "null-result");
            if (!ValidString(request.RequestId, MaxContractStringLength, true) || !String.Equals(prediction.RequestId, request.RequestId, StringComparison.Ordinal)) return Invalid(out detail, "request-id-mismatch");
            if (!ValidString(prediction.PredictorSource, MaxContractStringLength, true) || !ValidString(prediction.PredictorBuildId, MaxContractStringLength, true)) return Invalid(out detail, "predictor-identity-invalid");
            if (!ValidString(prediction.Reason, MaxReasonLength)) return Invalid(out detail, "reason-too-long");
            if (!ValidateSnapshotFacts(snapshot, out detail)) return false;
            if (prediction.InputScale == null || prediction.InputScale.VehicleCount != Length(snapshot.Vehicles)) return Invalid(out detail, "vehicle-count-mismatch");
            if (prediction.Trace == null || prediction.Diagnostics == null || prediction.StageTimings == null || prediction.Checkpoints == null) return Invalid(out detail, "bounded-output-missing");
            if (prediction.Trace.Length > workspace.MaxTraceEvents || prediction.Diagnostics.Length > workspace.MaxDiagnostics || prediction.Checkpoints.Length > workspace.MaxCheckpoints || prediction.StageTimings.Length > MaxStageTimings) return Invalid(out detail, "bounded-output-overflow");
            if (prediction.TraceTruncated && (workspace.MaxTraceEvents <= 0 || prediction.Trace.Length != workspace.MaxTraceEvents)) return Invalid(out detail, "trace-truncated-flag-invalid");
            if (prediction.EventCount < 0 || prediction.EventCount > workspace.MaxEvents || prediction.InputScale.EventCount != prediction.EventCount || prediction.InputScale.EventCount > workspace.MaxEvents) return Invalid(out detail, "event-count-invalid");
            if (!ValidateInputScale(prediction.InputScale, snapshot, prediction)) return Invalid(out detail, "input-scale-invalid");
            if (double.IsNaN(prediction.WorkerMilliseconds) || double.IsInfinity(prediction.WorkerMilliseconds) || prediction.WorkerMilliseconds < 0) return Invalid(out detail, "worker-time-invalid");
            if (!ValidateStageTimings(prediction.StageTimings, out double workerMilliseconds, out detail)) return false;
            if (Math.Abs(prediction.WorkerMilliseconds - workerMilliseconds) > 0.01d) return Invalid(out detail, "worker-time-mismatch");
            if (!ValidateTrace(prediction.Trace, snapshot, out detail)) return false;
            if (!ValidateDiagnostics(prediction.Diagnostics, out detail)) return false;
            if (prediction.Failure != RailEtaFailure.None && !ValidString(prediction.Reason, MaxReasonLength, true)) return Invalid(out detail, "failure-reason-missing");
            if (prediction.Failure == RailEtaFailure.None)
            {
                if (prediction.Confidence == RailEtaConfidence.Unknown || !IsFutureOrCurrent(snapshot.OriginFrame, prediction.PredictedArrivalFrame)) return Invalid(out detail, "arrival-invalid");
                if (!ValidateCheckpoints(prediction.Checkpoints, snapshot, request, workspace.MaxCheckpoints, out uint checkpointArrival)) return Invalid(out detail, "checkpoint-order-or-target-invalid");
                if (!IsFutureOrCurrent(snapshot.OriginFrame, checkpointArrival) || FrameDelta(snapshot.OriginFrame, checkpointArrival) > FrameDelta(snapshot.OriginFrame, prediction.PredictedArrivalFrame)) return Invalid(out detail, "stop-arrival-before-checkpoint");
            }
            return true;
        }

        private static bool ValidateStageTimings(RailEtaStageTiming[] timings, out double workerMilliseconds, out string detail)
        {
            workerMilliseconds = 0;
            for (int i = 0; i < timings.Length; i++)
            {
                RailEtaStageTiming timing = timings[i];
                if (timing == null || !ValidString(timing.Code, MaxContractStringLength, true) || timing.WallTicks < 0 || timing.InputCount < 0 || timing.AllocationBytes < 0
                    || double.IsNaN(timing.WallMilliseconds) || double.IsInfinity(timing.WallMilliseconds) || timing.WallMilliseconds < 0) return Invalid(out detail, "stage-timing-invalid");
                if (IsWorkerTiming(timing.Code)) workerMilliseconds += timing.WallMilliseconds;
            }
            detail = string.Empty;
            return true;
        }

        private static bool IsWorkerTiming(string code) => !String.Equals(code, "index", StringComparison.Ordinal)
            && !String.Equals(code, "scoped", StringComparison.Ordinal)
            && !String.Equals(code, "publish", StringComparison.Ordinal);

        private static bool ValidateTrace(RailEtaTraceEvent[] trace, RailEtaWorldSnapshot snapshot, out string detail)
        {
            uint origin = snapshot.OriginFrame;
            var vehicleIds = new HashSet<long>();
            RailVehicleSnapshot[] vehicles = snapshot.Vehicles ?? Array.Empty<RailVehicleSnapshot>();
            for (int i = 0; i < vehicles.Length; i++) if (vehicles[i] != null) vehicleIds.Add(vehicles[i].VehicleId.Value);
            var resourceIds = new HashSet<long>();
            RailResourceSnapshot[] resources = snapshot.Resources ?? Array.Empty<RailResourceSnapshot>();
            for (int i = 0; i < resources.Length; i++) if (resources[i] != null) resourceIds.Add(resources[i].ResourceId.Value);
            RailReservationSnapshot[] reservations = snapshot.Reservations ?? Array.Empty<RailReservationSnapshot>();
            for (int i = 0; i < reservations.Length; i++) if (reservations[i] != null) resourceIds.Add(reservations[i].ResourceId.Value);
            int previousSequence = -1;
            for (int i = 0; i < trace.Length; i++)
            {
                RailEtaTraceEvent value = trace[i];
                if (value == null || value.Sequence <= previousSequence || !ValidString(value.Kind, MaxContractStringLength, true) || !ValidString(value.ReasonCode, MaxContractStringLength)
                    || !IsFutureOrCurrent(origin, value.StartFrame) || !IsFutureOrCurrent(origin, value.EndFrame) || FrameDelta(value.StartFrame, value.EndFrame) >= 0x80000000u)
                    return Invalid(out detail, "trace-sequence-or-frame-invalid");
                if ((value.VehicleId.Value != 0 && !vehicleIds.Contains(value.VehicleId.Value)) || (value.OtherVehicleId.Value != 0 && !vehicleIds.Contains(value.OtherVehicleId.Value))
                    || (value.ResourceId.Value != 0 && !resourceIds.Contains(value.ResourceId.Value))) return Invalid(out detail, "trace-reference-invalid");
                if (!ValidateEvidence(value.StartEvidence) || !ValidateEvidence(value.EndEvidence))
                    return Invalid(out detail, "trace-evidence-invalid");
                previousSequence = value.Sequence;
            }
            detail = string.Empty;
            return true;
        }

        private static bool ValidateEvidence(RailEtaBlockerEvidence value)
        {
            if (value == null) return true;
            return value.Source >= 0 && value.Source <= 4
                && Finite(value.TargetPosition)
                && Finite(value.BlockerFrontPosition)
                && Finite(value.BlockerRearPosition)
                && Finite(value.OccupancyStart)
                && Finite(value.OccupancyEnd)
                && Finite(value.ReservationOffset)
                && Finite(value.OverlapThisStart)
                && Finite(value.OverlapThisEnd)
                && Finite(value.OverlapOtherStart)
                && Finite(value.OverlapOtherEnd)
                && Finite(value.Parallelism)
                && Finite(value.Distance)
                && Finite(value.DistanceFactor)
                && Finite(value.DistanceOffset)
                && Finite(value.SpeedBefore)
                && Finite(value.LimitedSpeed);
        }

        private static bool ValidateDiagnostics(RailEtaDiagnosticRecord[] diagnostics, out string detail)
        {
            for (int i = 0; i < diagnostics.Length; i++)
            {
                RailEtaDiagnosticRecord value = diagnostics[i];
                if (value == null || !ValidString(value.Code, MaxContractStringLength, true) || !ValidString(value.Message, MaxReasonLength)
                    || double.IsNaN(value.NumericValue) || double.IsInfinity(value.NumericValue)) return Invalid(out detail, "diagnostic-invalid");
            }
            detail = string.Empty;
            return true;
        }

        private static bool ValidateSnapshotFacts(RailEtaWorldSnapshot snapshot, out string detail)
        {
            RailBlockerSnapshot[] blockers = snapshot.Blockers ?? Array.Empty<RailBlockerSnapshot>();
            for (int i = 0; i < blockers.Length; i++)
            {
                RailBlockerSnapshot value = blockers[i];
                if (value == null || value.VehicleId.Value == 0 || !Enum.IsDefined(typeof(RailBlockerType), value.Type)) return Invalid(out detail, "blocker-fact-invalid");
            }
            RailReservationSnapshot[] reservations = snapshot.Reservations ?? Array.Empty<RailReservationSnapshot>();
            for (int i = 0; i < reservations.Length; i++)
            {
                RailReservationSnapshot value = reservations[i];
                if (value == null || value.ResourceId.Value == 0 || value.PreviousPriority < 0 || value.PreviousPriority > 255 || value.NextPriority < 0 || value.NextPriority > 255
                    || !FiniteFraction(value.PreviousOffset) || !FiniteFraction(value.NextOffset) || (value.HasUpdateFrame && value.UpdateFrameIndex >= 16u))
                    return Invalid(out detail, "reservation-fact-invalid");
            }
            RailSignalSnapshot[] signals = snapshot.Signals ?? Array.Empty<RailSignalSnapshot>();
            for (int i = 0; i < signals.Length; i++)
            {
                RailSignalSnapshot value = signals[i];
                if (value == null || value.LaneId.Value == 0 || !Enum.IsDefined(typeof(RailLaneSignalType), value.SignalType)) return Invalid(out detail, "signal-fact-invalid");
            }
            RailResourceSnapshot[] resources = snapshot.Resources ?? Array.Empty<RailResourceSnapshot>();
            for (int i = 0; i < resources.Length; i++)
            {
                RailResourceSnapshot value = resources[i];
                if (value == null || value.ResourceId.Value == 0 || value.LaneIds == null || value.Approaches == null) return Invalid(out detail, "resource-fact-invalid");
                for (int j = 0; j < value.Approaches.Length; j++)
                {
                    RailResourceApproachSnapshot approach = value.Approaches[j];
                    if (approach == null || approach.LaneId.Value == 0 || !FiniteFraction(approach.StartFraction) || !FiniteFraction(approach.EndFraction))
                        return Invalid(out detail, "resource-approach-invalid");
                }
            }
            detail = string.Empty;
            return true;
        }

        private static bool ValidateCheckpoints(RailEtaCheckpointPrediction[] checkpoints, RailEtaWorldSnapshot snapshot, RailEtaRequest request, int maxCheckpoints, out uint targetArrival)
        {
            targetArrival = 0;
            if (checkpoints == null || checkpoints.Length > maxCheckpoints) return false;
            RailVehicleSnapshot target = FindVehicle(snapshot, request.VehicleId);
            if (target == null) return false;
            var ids = new HashSet<long>();
            RailPathSegment[] path = target.RemainingPath ?? Array.Empty<RailPathSegment>();
            long fallbackTarget = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == null || path[i].EndCheckpointId.Value == 0) continue;
                ids.Add(path[i].EndCheckpointId.Value);
                fallbackTarget = path[i].EndCheckpointId.Value;
            }
            long targetId = request.TargetCheckpointId.Value != 0 ? request.TargetCheckpointId.Value : fallbackTarget;
            if (targetId == 0) return false;
            uint previousDelta = 0;
            bool foundTarget = false;
            for (int i = 0; i < checkpoints.Length; i++)
            {
                RailEtaCheckpointPrediction checkpoint = checkpoints[i];
                if (checkpoint == null || checkpoint.CheckpointId.Value == 0 || !ids.Contains(checkpoint.CheckpointId.Value) || !IsFutureOrCurrent(snapshot.OriginFrame, checkpoint.ArrivalFrame)) return false;
                uint delta = FrameDelta(snapshot.OriginFrame, checkpoint.ArrivalFrame);
                if (i > 0 && delta < previousDelta) return false;
                previousDelta = delta;
                if (checkpoint.CheckpointId.Value == targetId) { foundTarget = true; targetArrival = checkpoint.ArrivalFrame; }
            }
            return foundTarget;
        }

        private static RailVehicleSnapshot FindVehicle(RailEtaWorldSnapshot snapshot, RailVehicleId id)
        {
            RailVehicleSnapshot[] vehicles = snapshot?.Vehicles ?? Array.Empty<RailVehicleSnapshot>();
            for (int i = 0; i < vehicles.Length; i++) if (vehicles[i] != null && vehicles[i].VehicleId.Value == id.Value) return vehicles[i];
            return null;
        }

        private static bool ValidateInputScale(RailEtaInputScale scale, RailEtaWorldSnapshot snapshot, RailEtaPrediction prediction)
        {
            int pathSegments = 0;
            RailVehicleSnapshot[] vehicles = snapshot?.Vehicles ?? Array.Empty<RailVehicleSnapshot>();
            for (int i = 0; i < vehicles.Length; i++) pathSegments += vehicles[i]?.RemainingPath?.Length ?? 0;
            return scale.VehicleCount == vehicles.Length && scale.PathSegmentCount == pathSegments && scale.BlockerCount == Length(snapshot.Blockers)
                && scale.ReservationCount == Length(snapshot.Reservations) && scale.SignalCount == Length(snapshot.Signals)
                && scale.OccupancyCount == Length(snapshot.Occupancies) && scale.ResourceCount == Length(snapshot.Resources)
                && scale.CheckpointCount == Length(prediction.Checkpoints);
        }

        private static int Length<T>(T[] values) => values?.Length ?? 0;
        private static uint FrameDelta(uint start, uint end) => unchecked(end - start);
        private static bool IsFutureOrCurrent(uint origin, uint value) => FrameDelta(origin, value) < 0x80000000u;
        private static bool FiniteFraction(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d && value <= 1d;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool ValidString(string value, int maxLength, bool required = false) => value != null && value.Length <= maxLength && (!required || !String.IsNullOrWhiteSpace(value));
        private static bool Invalid(out string detail, string reason) { detail = reason; return false; }
    }
}
