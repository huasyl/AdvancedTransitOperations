#if RT_DEBUG_TOOLS
using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal static class RailEtaDebugSettings
    {
#if RT_RAIL_ETA_DIAGNOSTICS
        internal static bool DetailedLogsEnabled { get; } = true;
        internal static bool HeavyExportsEnabled { get; } = true;
#else
        internal static bool DetailedLogsEnabled { get; } = false;
        internal static bool HeavyExportsEnabled { get; } = false;
#endif
    }

    internal static class RailEtaSnapshotDiagnostics
    {
        public static string BuildSummary(RailEtaWorldSnapshot snapshot, int requestCount)
        {
            if (snapshot == null) return string.Empty;
            int pathSegments = 0;
            for (int i = 0; i < snapshot.Vehicles.Length; i++) pathSegments += snapshot.Vehicles[i].RemainingPath?.Length ?? 0;
            string summary = "batch=" + snapshot.BatchId
                + " originFrame=" + snapshot.OriginFrame
                + " generation=" + snapshot.ServiceGeneration
                + " requests=" + requestCount
                + " vehicles=" + snapshot.Vehicles.Length
                + " pathSegments=" + pathSegments
                + " blockers=" + snapshot.Blockers.Length
                + " reservations=" + snapshot.Reservations.Length
                + " signals=" + snapshot.Signals.Length
                + " occupancies=" + snapshot.Occupancies.Length
                + " resources=" + snapshot.Resources.Length
                + " closureValidated=" + snapshot.ClosureValidated;
            return summary;
        }

        public static string ExportFailure(RailEtaTicketStatus status, RailEtaRequestDescriptor descriptor,
            RailEtaWorldSnapshot snapshot, RailEtaRequest request, RailEtaPrediction prediction, string filePath)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (String.IsNullOrWhiteSpace(filePath)) filePath = DefaultFailurePath(status.Ticket.Value, descriptor.VehicleIndex);
            string directory = Path.GetDirectoryName(filePath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var sb = new StringBuilder(4096);
            sb.AppendLine("Rail ETA failure dump");
            sb.AppendLine("ticket=" + status.Ticket.Value
                + " vehicle=" + descriptor.VehicleIndex + ":" + descriptor.VehicleVersion
                + " targetCheckpoint=" + descriptor.TargetCheckpointId);
            sb.AppendLine("state=" + status.State + " failure=" + status.Failure + " detail=" + (status.Detail ?? string.Empty));
            sb.AppendLine("requestFrame=" + status.RequestFrame
                + " indexScheduledFrame=" + status.IndexScheduledFrame
                + " indexReadyFrame=" + status.IndexReadyFrame
                + " scopeReadyFrame=" + status.ScopeReadyFrame
                + " originFrame=" + status.OriginFrame
                + " snapshotReadyFrame=" + status.SnapshotReadyFrame
                + " predictorQueuedFrame=" + status.PredictorQueuedFrame
                + " predictionStartedFrame=" + status.PredictionStartedFrame
                + " predictionFinishedFrame=" + status.PredictionFinishedFrame
                + " publishedFrame=" + status.PublishedFrame
                + " batch=" + status.BatchId
                + " generation=" + status.ServiceGeneration);
            sb.AppendLine("snapshotAvailable=" + (snapshot != null ? 1 : 0)
                + " requestAvailable=" + (request != null ? 1 : 0)
                + " predictionAvailable=" + (prediction != null ? 1 : 0));
            if (prediction != null)
            {
                sb.AppendLine("predictionFailure=" + prediction.Failure
                    + " confidence=" + prediction.Confidence
                    + " reason=" + (prediction.Reason ?? string.Empty)
                    + " source=" + (prediction.PredictorSource ?? string.Empty)
                    + " build=" + (prediction.PredictorBuildId ?? string.Empty));
                RailEtaDiagnosticRecord[] diagnostics = prediction.Diagnostics ?? Array.Empty<RailEtaDiagnosticRecord>();
                for (int i = 0; i < diagnostics.Length; i++)
                {
                    RailEtaDiagnosticRecord value = diagnostics[i];
                    if (value == null) continue;
                    sb.AppendLine("diagnostic code=" + value.Code + " severity=" + value.Severity
                        + " message=" + value.Message + " vehicle=" + value.VehicleId.Value
                        + " resource=" + value.ResourceId.Value + " frame=" + value.Frame);
                }
            }
            if (snapshot != null && request != null && prediction != null)
            {
                sb.AppendLine();
                sb.Append(BuildDump(snapshot, prediction, 1, BuildSummary(snapshot, 1)));
            }
            File.WriteAllText(filePath, sb.ToString());
            if (snapshot != null && request != null && prediction != null)
                RailEtaSnapshotExporter.Export(filePath, snapshot, request, prediction);
            return filePath;
        }

        private static string DefaultFailurePath(long ticket, int vehicleIndex)
        {
            string logsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Colossal Order", "Cities Skylines II", "Logs");
            return Path.Combine(logsDirectory, "RapidTransitMod-rail-eta-failure-" + ticket + "-" + vehicleIndex
                + "-" + DateTime.UtcNow.Ticks + ".txt");
        }

        private static string BuildDump(RailEtaWorldSnapshot snapshot, RailEtaPrediction prediction, int requestCount, string summary)
        {
            StringBuilder sb = new StringBuilder(16384);
            sb.AppendLine("Rail ETA snapshot dump");
            sb.AppendLine(summary);
            sb.AppendLine("navigationPhase=" + snapshot.NavigationPhase + " requestCount=" + requestCount);
            sb.AppendLine("[Prediction]");
            sb.AppendLine("requestId=" + prediction.RequestId + " source=" + prediction.PredictorSource + " build=" + prediction.PredictorBuildId + " generation=" + prediction.PredictorGeneration);
            sb.AppendLine("failure=" + prediction.Failure + " confidence=" + prediction.Confidence + " arrival=" + prediction.PredictedArrivalFrame + " reason=" + prediction.Reason);
            sb.AppendLine("elapsed=" + unchecked(prediction.PredictedArrivalFrame - snapshot.OriginFrame) + " events=" + prediction.EventCount + " workerMs=" + prediction.WorkerMilliseconds.ToString("F2"));
            sb.AppendLine("traceTruncated=" + prediction.TraceTruncated + " checkpoints=" + (prediction.Checkpoints?.Length ?? 0));
            sb.AppendLine("[StageTimings]");
            RailEtaStageTiming[] timings = prediction.StageTimings ?? Array.Empty<RailEtaStageTiming>();
            for (int i = 0; i < timings.Length; i++)
            {
                RailEtaStageTiming timing = timings[i];
                if (timing == null) continue;
                sb.AppendLine("code=" + timing.Code + " wallMs=" + timing.WallMilliseconds.ToString("F3") + " input=" + timing.InputCount + " allocation=" + timing.AllocationBytes);
            }
            sb.AppendLine("[Trace]");
            RailEtaTraceEvent[] trace = prediction.Trace ?? Array.Empty<RailEtaTraceEvent>();
            for (int i = 0; i < trace.Length; i++)
            {
                RailEtaTraceEvent value = trace[i];
                if (value == null) continue;
                sb.AppendLine("sequence=" + value.Sequence + " kind=" + value.Kind + " vehicle=" + value.VehicleId.Value + " otherVehicle=" + value.OtherVehicleId.Value + " resource=" + value.ResourceId.Value + " frames=" + value.StartFrame + ".." + value.EndFrame + " delay=" + value.DelayFrames + " reason=" + value.ReasonCode);
            }
            sb.AppendLine("[Diagnostics]");
            RailEtaDiagnosticRecord[] diagnostics = prediction.Diagnostics ?? Array.Empty<RailEtaDiagnosticRecord>();
            for (int i = 0; i < diagnostics.Length; i++)
            {
                RailEtaDiagnosticRecord value = diagnostics[i];
                if (value == null) continue;
                sb.AppendLine("code=" + value.Code + " severity=" + value.Severity + " message=" + value.Message + " vehicle=" + value.VehicleId.Value + " resource=" + value.ResourceId.Value + " frame=" + value.Frame + " value=" + value.NumericValue.ToString("F3"));
            }
            sb.AppendLine();
            sb.AppendLine("[Vehicles]");
            for (int i = 0; i < snapshot.Vehicles.Length; i++)
            {
                RailVehicleSnapshot vehicle = snapshot.Vehicles[i];
                sb.Append("vehicle#").Append(i)
                    .Append(" id=").Append(vehicle.VehicleId.Value)
                    .Append(" entity=").Append(Identity(vehicle.Entity))
                    .Append(" target=").Append(Identity(vehicle.Target))
                    .Append(" speed=").Append(vehicle.SpeedMetresPerSecond.ToString("F3"))
                    .Append(" boarding=").Append(vehicle.IsBoarding ? 1 : 0)
                    .Append(" departureFrame=").Append(vehicle.DepartureFrame)
                    .Append(" pathState=").Append(vehicle.PathState)
                    .Append(" pathIndex=").Append(vehicle.PathElementIndex)
                    .Append(" priority=").Append(vehicle.VehiclePriority)
                    .Append(" blockerKind=").Append(vehicle.ExternalBlockerKind)
                    .Append(" units=").Append(vehicle.Consist?.UnitCount ?? 0)
                    .Append(" pathSegments=").Append(vehicle.RemainingPath?.Length ?? 0)
                    .Append(" pathSignature=0x").Append(vehicle.PathSignature.ToString("X16"))
                    .Append(" resourceSignature=0x").Append(vehicle.ResourceSignature.ToString("X16"))
                    .AppendLine();
                RailCurrentLaneSnapshot current = vehicle.CurrentLane;
                sb.Append("  current front=").Append(current.FrontLaneId.Value).Append('@').Append(current.FrontPosition.ToString("F4"))
                    .Append(" rear=").Append(current.RearLaneId.Value).Append('@').Append(current.RearPosition.ToString("F4"))
                    .Append(" frontCache=").Append(current.FrontCacheLaneId.Value)
                    .Append(" rearCache=").Append(current.RearCacheLaneId.Value)
                    .Append(" flags=").Append(current.FrontFlags).Append('/').Append(current.RearFlags)
                    .AppendLine();
                RailConsistUnitSnapshot[] units = vehicle.Consist?.Units ?? Array.Empty<RailConsistUnitSnapshot>();
                for (int u = 0; u < units.Length; u++)
                {
                    RailConsistUnitSnapshot unit = units[u];
                    sb.Append("  unit#").Append(u).Append(" entity=").Append(Identity(unit.Entity))
                        .Append(" prefab=").Append(Identity(unit.Prefab))
                        .Append(" length=").Append(unit.LengthMetres.ToString("F3"))
                        .Append(" bogie=").Append(unit.FrontBogieOffsetMetres.ToString("F3")).Append('/').Append(unit.RearBogieOffsetMetres.ToString("F3"))
                        .Append(" attach=").Append(unit.FrontAttachOffsetMetres.ToString("F3")).Append('/').Append(unit.RearAttachOffsetMetres.ToString("F3"))
                        .AppendLine();
                }
                RailPathSegment[] path = vehicle.RemainingPath ?? Array.Empty<RailPathSegment>();
                for (int p = 0; p < path.Length; p++)
                {
                    RailPathSegment segment = path[p];
                    sb.Append("  path#").Append(p)
                        .Append(" lane=").Append(segment.LaneId.Value)
                        .Append(" range=").Append(segment.StartFraction.ToString("F4")).Append("..").Append(segment.EndFraction.ToString("F4"))
                        .Append(" length=").Append(segment.LengthMetres.ToString("F3"))
                        .Append(" speedLimit=").Append(segment.SpeedLimitMetresPerSecond.ToString("F3"))
                        .Append(" navFlags=").Append(segment.NavigationFlags)
                        .Append(" trackFlags=").Append(segment.TrackFlags)
                        .AppendLine();
                }
            }
            sb.AppendLine();
            sb.AppendLine("[Blockers]");
            for (int i = 0; i < snapshot.Blockers.Length; i++) sb.AppendLine("vehicle=" + snapshot.Blockers[i].VehicleId.Value + " blocker=" + snapshot.Blockers[i].BlockerVehicleId.Value);
            sb.AppendLine();
            sb.AppendLine("[Reservations]");
            for (int i = 0; i < snapshot.Reservations.Length; i++)
            {
                RailReservationSnapshot value = snapshot.Reservations[i];
                sb.AppendLine("resource=" + value.ResourceId.Value + " blocker=" + value.BlockerVehicleId.Value + " external=" + value.ExternalBlockerKind + " prev=" + value.PreviousPriority + '@' + value.PreviousOffset.ToString("F4") + " next=" + value.NextPriority + '@' + value.NextOffset.ToString("F4") + " updateFrame=" + value.UpdateFrameIndex);
            }
            sb.AppendLine();
            sb.AppendLine("[Signals]");
            for (int i = 0; i < snapshot.Signals.Length; i++)
            {
                RailSignalSnapshot value = snapshot.Signals[i];
                sb.AppendLine("lane=" + value.LaneId.Value + " petitioner=" + value.PetitionerVehicleId.Value + " petitionerExternal=" + value.PetitionerExternalKind + " blocker=" + value.BlockerVehicleId.Value + " blockerExternal=" + value.BlockerExternalKind + " priority=" + value.Priority + " flags=" + value.Flags);
            }
            sb.AppendLine();
            sb.AppendLine("[Occupancies]");
            for (int i = 0; i < snapshot.Occupancies.Length; i++)
            {
                RailLaneOccupancySnapshot value = snapshot.Occupancies[i];
                sb.AppendLine("lane=" + value.LaneId.Value + " vehicle=" + value.VehicleId.Value + " range=" + value.StartFraction.ToString("F4") + ".." + value.EndFraction.ToString("F4"));
            }
            sb.AppendLine();
            sb.AppendLine("[Resources]");
            for (int i = 0; i < snapshot.Resources.Length; i++)
            {
                RailResourceSnapshot value = snapshot.Resources[i];
                sb.Append("resource=").Append(value.ResourceId.Value).Append(" priorityDelta=").Append(value.PriorityDelta).Append(" lanes=");
                for (int l = 0; l < value.LaneIds.Length; l++) { if (l > 0) sb.Append(','); sb.Append(value.LaneIds[l].Value); }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Identity(RailEntityIdentity identity) => identity == null ? "null" : identity.Index + ":" + identity.Version;
    }

    internal static class RailEtaSnapshotExporter
    {
        private const int MaxSerializedItems = 1000000;

        public static void Export(string textPath, RailEtaWorldSnapshot snapshot, RailEtaRequest request, RailEtaPrediction prediction)
        {
            string prefix = Path.ChangeExtension(textPath, null);
            WriteJson(prefix + ".snapshot.json", snapshot);
            WriteJson(prefix + ".request.json", request);
            WriteJson(prefix + ".prediction.json", prediction);
        }

        private static void WriteJson<T>(string path, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                MaxItemsInObjectGraph = MaxSerializedItems
            });
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) serializer.WriteObject(stream, value);
        }
    }
}
#endif
