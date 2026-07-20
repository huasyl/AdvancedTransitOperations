using System;
using System.Collections.Generic;
using System.Diagnostics;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal static class RailEtaResultValidator
    {
        public static RailEtaStageTiming Timing(string code, long wallTicks, int inputCount, long allocationBytes = 0)
        {
            return new RailEtaStageTiming
            {
                Code = code ?? string.Empty,
                WallTicks = Math.Max(0, wallTicks),
                WallMilliseconds = wallTicks <= 0 ? 0 : wallTicks * 1000d / Stopwatch.Frequency,
                InputCount = Math.Max(0, inputCount),
                AllocationBytes = Math.Max(0, allocationBytes)
            };
        }

        public static void ApplyHostMetadata(RailEtaPrediction prediction, RailEtaWorldSnapshot snapshot, RailEtaRequest request,
            string predictorBuildId, long predictorGeneration, IList<RailEtaStageTiming> hostTimings)
        {
            if (prediction == null) return;
            prediction.PredictorSource = "hot";
            prediction.PredictorBuildId = predictorBuildId ?? string.Empty;
            prediction.PredictorGeneration = predictorGeneration;
            prediction.InputScale = BuildInputScale(snapshot, prediction);
            prediction.Trace ??= Array.Empty<RailEtaTraceEvent>();
            prediction.Diagnostics ??= Array.Empty<RailEtaDiagnosticRecord>();
            prediction.StageTimings = MergeStageTimings(prediction.StageTimings, hostTimings);
            prediction.WorkerMilliseconds = 0;
            for (int i = 0; i < prediction.StageTimings.Length; i++)
                if (IsWorkerTiming(prediction.StageTimings[i])) prediction.WorkerMilliseconds += prediction.StageTimings[i].WallMilliseconds;
        }

        public static bool TryValidate(RailEtaPrediction prediction, RailEtaWorldSnapshot snapshot, RailEtaRequest request,
            RailEtaWorkspace workspace, out string detail)
            => RailEtaPredictionContractValidator.TryValidate(prediction, snapshot, request, workspace, out detail);

        private static RailEtaStageTiming[] MergeStageTimings(RailEtaStageTiming[] predictorTimings, IList<RailEtaStageTiming> hostTimings)
        {
            var merged = new List<RailEtaStageTiming>(RailEtaLimits.MaxStageTimings);
            var hostCodes = new HashSet<string>(StringComparer.Ordinal);
            if (hostTimings != null)
                for (int i = 0; i < hostTimings.Count; i++) if (hostTimings[i] != null) hostCodes.Add(hostTimings[i].Code ?? string.Empty);
            if (predictorTimings != null)
                for (int i = 0; i < predictorTimings.Length && merged.Count < RailEtaLimits.MaxStageTimings; i++)
                    if (predictorTimings[i] != null && !hostCodes.Contains(predictorTimings[i].Code ?? string.Empty)) merged.Add(predictorTimings[i]);
            if (hostTimings != null)
                for (int i = 0; i < hostTimings.Count && merged.Count < RailEtaLimits.MaxStageTimings; i++) if (hostTimings[i] != null) merged.Add(hostTimings[i]);
            return merged.ToArray();
        }

        private static bool IsWorkerTiming(RailEtaStageTiming timing)
        {
            if (timing == null) return false;
            return !String.Equals(timing.Code, "index", StringComparison.Ordinal)
                && !String.Equals(timing.Code, "scoped", StringComparison.Ordinal)
                && !String.Equals(timing.Code, "publish", StringComparison.Ordinal);
        }

        private static RailEtaInputScale BuildInputScale(RailEtaWorldSnapshot snapshot, RailEtaPrediction prediction)
        {
            RailEtaInputScale scale = new RailEtaInputScale
            {
                VehicleCount = Length(snapshot?.Vehicles),
                BlockerCount = Length(snapshot?.Blockers),
                ReservationCount = Length(snapshot?.Reservations),
                SignalCount = Length(snapshot?.Signals),
                OccupancyCount = Length(snapshot?.Occupancies),
                ResourceCount = Length(snapshot?.Resources),
                EventCount = prediction?.EventCount ?? 0,
                CheckpointCount = Length(prediction?.Checkpoints)
            };
            RailVehicleSnapshot[] vehicles = snapshot?.Vehicles ?? Array.Empty<RailVehicleSnapshot>();
            for (int i = 0; i < vehicles.Length; i++) scale.PathSegmentCount += vehicles[i]?.RemainingPath?.Length ?? 0;
            return scale;
        }

        private static int Length<T>(T[] values) => values?.Length ?? 0;
    }
}
