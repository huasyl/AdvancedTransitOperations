using System;
#if RT_DEBUG_TOOLS
using System.Diagnostics;
using System.Globalization;
using System.Text;
#endif
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using Unity.Jobs;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    public sealed class RailEtaHotModule : IRailEtaHotModule
    {
        private readonly string m_BuildId = new RailPredictionSolver().Version + "-"
            + typeof(RailEtaHotModule).Module.ModuleVersionId.ToString("N");
        private RailEtaHotContext m_Context;
        private RailEtaService m_Service;
        private RailEtaSnapshotSystem m_SnapshotSystem;
#if RT_DEBUG_TOOLS
        private RailEtaComparisonSystem m_ComparisonSystem;
#endif
        private RailEtaTicket m_ActiveTicket;
        private RailEtaHotCommand m_ActiveCommand;
        private bool m_Disposed;
#if RT_DEBUG_TOOLS
        private const int CostReportRequestCount = 32;
        private long m_CostWindowStartTicks;
        private long m_CostMainTicks;
        private long m_CostMainMaxTicks;
        private long m_CostMainCalls;
        private int m_CostRequests;
        private int m_CostCompleted;
        private int m_CostFailed;
        private int m_CostNotConverged;
        private double m_CostWorkerMilliseconds;
        private double m_CostIndexLatencyMilliseconds;
        private double m_CostMaterializeMilliseconds;
        private double m_CostPredictMilliseconds;
        private double m_CostValidateMilliseconds;
        private ulong m_CostVirtualFrames;
        private ulong m_CostNavigationTicks;
        private ulong m_CostVehicleTicks;
        private long m_CostPathSegments;
        private long m_CostOccupancies;
        private bool m_CostReportPending;
#endif

        public string BuildId => m_BuildId;
        public bool Busy => m_ActiveTicket.IsValid;
        public bool NeedsTick
        {
            get
            {
                if (Busy) return true;
#if RT_DEBUG_TOOLS
                return m_ComparisonSystem != null && m_ComparisonSystem.NeedsTick;
#else
                return false;
#endif
            }
        }

        public void Attach(RailEtaHotContext context)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(RailEtaHotModule));
            if (m_Context != null) return;
            m_Context = context ?? throw new ArgumentNullException(nameof(context));
            m_Service = new RailEtaService(context.Worker);
            RailEtaService.Bind(m_Service);
            m_SnapshotSystem = (RailEtaSnapshotSystem)context.World.CreateSystemManaged(typeof(RailEtaSnapshotSystem));
            m_SnapshotSystem.Configure(context.RuntimeReadPort, () => m_ActiveCommand.Generation);
#if RT_DEBUG_TOOLS
            m_ComparisonSystem = (RailEtaComparisonSystem)context.World.CreateSystemManaged(typeof(RailEtaComparisonSystem));
            m_CostWindowStartTicks = Stopwatch.GetTimestamp();
#endif
        }

        public void Submit(RailEtaHotCommand command)
        {
            if (m_Disposed || m_Service == null) return;
            if (Busy)
            {
                if (m_ActiveCommand.Mode == RailEtaMode.Theory && command.Mode != RailEtaMode.Theory)
                {
                    RailEtaHotCommand preempted = m_ActiveCommand;
                    m_Service.Cancel(m_ActiveTicket);
                    Publish(preempted, "Cancelled", RailEtaFailure.Cancelled.ToString(), "A normal vehicle ETA has priority over theory prewarm.", 0, 0, false, string.Empty, string.Empty);
                    m_ActiveTicket = default;
                    m_ActiveCommand = default;
                }
                else
                {
                    Publish(command, "Busy", RailEtaFailure.Busy.ToString(), "Another ETA request is active.", 0, 0, false, string.Empty, string.Empty);
                    return;
                }
            }
            m_ActiveCommand = command;
            m_ActiveTicket = m_Service.SubmitHot(command.Ticket, m_Service.Generation, command.VehicleIndex, command.VehicleVersion,
                command.TargetWaypoint, command.Mode, command.DepotIndex, command.DepotVersion, command.ModelIndex, command.ModelVersion,
                command.SecondaryModelIndex, command.SecondaryModelVersion);
        }

        public JobHandle Tick(uint simulationFrame, JobHandle inputDependency)
        {
            if (m_Disposed || m_Service == null) return inputDependency;
#if RT_DEBUG_TOOLS
            long tickStart = RailEtaDebugSettings.DetailedLogsEnabled ? Stopwatch.GetTimestamp() : 0L;
#endif
            JobHandle handle = m_SnapshotSystem.TickExternal(inputDependency);
#if RT_DEBUG_TOOLS
            if (RailEtaDebugSettings.HeavyExportsEnabled || m_ComparisonSystem.NeedsTick)
                handle = m_ComparisonSystem.TickExternal(simulationFrame, handle);
#endif
            PublishCompleted();
#if RT_DEBUG_TOOLS
            if (RailEtaDebugSettings.DetailedLogsEnabled)
            {
                RecordMainTick(Stopwatch.GetTimestamp() - tickStart);
                if (m_CostReportPending) WriteCostReport();
            }
#endif
            return handle;
        }

        private void PublishCompleted()
        {
            if (!m_ActiveTicket.IsValid) return;
            if (!m_Service.TryGetState(m_ActiveTicket, out RailEtaTicketStatus status)) return;
            bool terminal = status.State == RailEtaRequestState.Completed || status.State == RailEtaRequestState.Failed
                || status.State == RailEtaRequestState.Cancelled || status.State == RailEtaRequestState.Busy || status.State == RailEtaRequestState.WorkerLost;
            if (!terminal) return;
            m_Service.TryGetPrediction(m_ActiveTicket, out RailEtaPrediction prediction);
            string publicState = status.Failure == RailEtaFailure.NotConverged ? "NotConverged" : status.State.ToString();
            Publish(m_ActiveCommand, publicState, status.Failure.ToString(), status.Detail,
                prediction?.PredictedArrivalFrame ?? 0, status.RequestFrame, m_Service.IsIncomplete(m_ActiveTicket), string.Empty, prediction?.PredictorBuildId);
#if RT_DEBUG_TOOLS
            if (RailEtaDebugSettings.DetailedLogsEnabled)
                RecordPredictionCost(status, prediction);
            if (RailEtaDebugSettings.HeavyExportsEnabled
                && (status.State == RailEtaRequestState.Failed || status.State == RailEtaRequestState.WorkerLost))
            {
                _ = m_Service.ExportFailureAsync(m_ActiveTicket).ContinueWith(task =>
                {
                    if (task.IsFaulted) m_Context?.Log("[RailEtaFailureExport] failed: " + task.Exception?.GetBaseException());
                    else m_Context?.Log("[RailEtaFailureExport] completed path=" + task.Result);
                });
            }
#endif
            m_ActiveTicket = default;
            m_ActiveCommand = default;
        }

#if RT_DEBUG_TOOLS
        private void RecordMainTick(long ticks)
        {
            if (ticks < 0) return;
            m_CostMainTicks += ticks;
            m_CostMainMaxTicks = Math.Max(m_CostMainMaxTicks, ticks);
            m_CostMainCalls++;
        }

        private void RecordPredictionCost(RailEtaTicketStatus status, RailEtaPrediction prediction)
        {
            m_CostRequests++;
            if (prediction != null && prediction.Failure == RailEtaFailure.None) m_CostCompleted++;
            else m_CostFailed++;
            if (status.Failure == RailEtaFailure.NotConverged) m_CostNotConverged++;
            if (prediction != null)
            {
                m_CostWorkerMilliseconds += prediction.WorkerMilliseconds;
                RailEtaStageTiming[] timings = prediction.StageTimings ?? Array.Empty<RailEtaStageTiming>();
                for (int i = 0; i < timings.Length; i++)
                {
                    RailEtaStageTiming timing = timings[i];
                    if (timing == null) continue;
                    if (String.Equals(timing.Code, "index", StringComparison.Ordinal)) m_CostIndexLatencyMilliseconds += timing.WallMilliseconds;
                    else if (String.Equals(timing.Code, "materialize", StringComparison.Ordinal)) m_CostMaterializeMilliseconds += timing.WallMilliseconds;
                    else if (String.Equals(timing.Code, "predict", StringComparison.Ordinal)) m_CostPredictMilliseconds += timing.WallMilliseconds;
                    else if (String.Equals(timing.Code, "validate", StringComparison.Ordinal)) m_CostValidateMilliseconds += timing.WallMilliseconds;
                }
                RailEtaInputScale scale = prediction.InputScale;
                int vehicleCount = Math.Max(0, scale?.VehicleCount ?? 0);
                m_CostPathSegments += Math.Max(0, scale?.PathSegmentCount ?? 0);
                m_CostOccupancies += Math.Max(0, scale?.OccupancyCount ?? 0);
                uint virtualFrames = prediction.Failure == RailEtaFailure.None
                    ? unchecked(prediction.PredictedArrivalFrame - status.OriginFrame)
                    : prediction.Failure == RailEtaFailure.NotConverged ? RailPredictionSolver.MaximumPredictionFrames : 0u;
                uint navigationTicks = CountNavigationTicks(status.OriginFrame, virtualFrames);
                m_CostVirtualFrames += virtualFrames;
                m_CostNavigationTicks += navigationTicks;
                m_CostVehicleTicks += (ulong)navigationTicks * (uint)vehicleCount;
            }
            if (m_CostRequests >= CostReportRequestCount) m_CostReportPending = true;
        }

        private static uint CountNavigationTicks(uint originFrame, uint virtualFrames)
        {
            if (virtualFrames == 0u) return 0u;
            uint nextPhase = unchecked(originFrame + 1u) & 15u;
            uint first = 1u + (3u - nextPhase + 16u) % 16u;
            return first > virtualFrames ? 0u : 1u + (virtualFrames - first) / 16u;
        }

        private void WriteCostReport()
        {
            long now = Stopwatch.GetTimestamp();
            double tickMilliseconds = 1000d / Stopwatch.Frequency;
            double windowSeconds = Math.Max(0d, (now - m_CostWindowStartTicks) / (double)Stopwatch.Frequency);
            m_Context?.Log("[RailEtaCost] requests=" + m_CostRequests
                + " completed=" + m_CostCompleted + " failed=" + m_CostFailed
                + " notConverged=" + m_CostNotConverged
                + " windowSeconds=" + windowSeconds.ToString("F2", CultureInfo.InvariantCulture)
                + " mainCalls=" + m_CostMainCalls
                + " mainMs=" + (m_CostMainTicks * tickMilliseconds).ToString("F2", CultureInfo.InvariantCulture)
                + " mainMaxMs=" + (m_CostMainMaxTicks * tickMilliseconds).ToString("F2", CultureInfo.InvariantCulture)
                + " workerMs=" + m_CostWorkerMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                + " indexLatencyMs=" + m_CostIndexLatencyMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                + " materializeMs=" + m_CostMaterializeMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                + " predictMs=" + m_CostPredictMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                + " validateMs=" + m_CostValidateMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                + " virtualFrames=" + m_CostVirtualFrames + " navigationTicks=" + m_CostNavigationTicks
                + " vehicleTicks=" + m_CostVehicleTicks + " pathSegments=" + m_CostPathSegments
                + " occupancies=" + m_CostOccupancies);
            m_CostWindowStartTicks = now;
            m_CostMainTicks = 0;
            m_CostMainMaxTicks = 0;
            m_CostMainCalls = 0;
            m_CostRequests = 0;
            m_CostCompleted = 0;
            m_CostFailed = 0;
            m_CostNotConverged = 0;
            m_CostWorkerMilliseconds = 0d;
            m_CostIndexLatencyMilliseconds = 0d;
            m_CostMaterializeMilliseconds = 0d;
            m_CostPredictMilliseconds = 0d;
            m_CostValidateMilliseconds = 0d;
            m_CostVirtualFrames = 0;
            m_CostNavigationTicks = 0;
            m_CostVehicleTicks = 0;
            m_CostPathSegments = 0;
            m_CostOccupancies = 0;
            m_CostReportPending = false;
        }
#endif

        private void Publish(RailEtaHotCommand command, string state, string failure, string detail, uint etaFrame, uint originFrame, bool incomplete, string comparison, string build)
        {
            m_Context.PublishResult(new RailEtaPublicResult
            {
                Ticket = command.Ticket,
                State = state,
                Failure = failure,
                Detail = detail ?? string.Empty,
                TargetVehicle = ((long)(uint)command.VehicleIndex << 32) | (uint)command.VehicleVersion,
                TargetWaypoint = command.TargetWaypoint,
                EtaFrame = etaFrame,
                OriginFrame = originFrame,
                Source = "hot",
                Build = String.IsNullOrWhiteSpace(build) ? BuildId : build,
                Generation = command.Generation,
                Incomplete = incomplete,
                Mode = command.Mode,
                ComparisonSummary = comparison ?? string.Empty
            });
        }

        public void Cancel(long ticket)
        {
            if (m_ActiveTicket.Value == ticket) m_Service?.Cancel(m_ActiveTicket);
        }

        public bool TryGetComparisonSummary(long ticket, out string summary)
        {
#if RT_DEBUG_TOOLS
            summary = string.Empty;
            if (!RailEtaComparisonSystem.TryGetStatus(out RailEtaComparisonStatus status) || status.Ticket != ticket) return false;
            summary = FormatComparisonSummary(status);
            return true;
#else
            summary = string.Empty;
            return false;
#endif
        }

        public bool PrepareForReload(out long ticket, out string summary)
        {
#if RT_DEBUG_TOOLS
            ticket = 0;
            summary = string.Empty;
            if (!RailEtaDebugSettings.HeavyExportsEnabled) return false;
            if (m_ComparisonSystem == null || !m_ComparisonSystem.PrepareForHotReload(out RailEtaComparisonStatus status)) return false;
            ticket = status.Ticket;
            summary = FormatComparisonSummary(status);
            return ticket != 0;
#else
            ticket = 0;
            summary = string.Empty;
            return false;
#endif
        }

#if RT_DEBUG_TOOLS
        private static string FormatComparisonSummary(RailEtaComparisonStatus status)
        {
            var sb = new StringBuilder(640);
            sb.Append('{');
            AppendJsonString(sb, "comparisonState", status.State);
            AppendJsonBool(sb, "comparisonValid", status.ComparisonValid);
            AppendJsonString(sb, "comparisonInvalidReason", status.InvalidReason);
            AppendJsonString(sb, "comparisonVehicleId", status.VehicleId.ToString(CultureInfo.InvariantCulture));
            AppendJsonNumber(sb, "comparisonVehicleIndex", unchecked((int)(uint)((ulong)status.VehicleId >> 32)));
            AppendJsonNumber(sb, "comparisonOriginFrame", status.OriginFrame);
            AppendJsonRaw(sb, "etaGameMinutes", status.EtaGameMinutes.ToString("F2", CultureInfo.InvariantCulture));
            AppendJsonNumber(sb, "comparisonPredictedArrival", status.PredictedArrivalFrame);
            AppendJsonNumber(sb, "comparisonActualArrival", status.ActualArrivalFrame);
            AppendJsonNumber(sb, "comparisonFinishDelta", status.ActualStopMinusPredictionFinishedFrames);
            AppendJsonNumber(sb, "comparisonPublishDelta", status.ActualStopMinusPublishedFrames);
            AppendJsonNumber(sb, "comparisonOriginDelta", status.ActualStopMinusOriginFrames);
            AppendJsonNumber(sb, "comparisonPredictionDelta", status.ActualStopMinusPredictedArrivalFrames);
            AppendJsonNumber(sb, "comparisonFramesToOrPastPrediction", status.FramesToOrPastPrediction);
            if (sb[sb.Length - 1] == ',') sb.Length--;
            return sb.Append('}').ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":\"");
            string text = value ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\\' || ch == '"') sb.Append('\\').Append(ch);
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\n') sb.Append("\\n");
                else sb.Append(ch);
            }
            sb.Append("\",");
        }

        private static void AppendJsonBool(StringBuilder sb, string name, bool value) =>
            AppendJsonRaw(sb, name, value ? "true" : "false");

        private static void AppendJsonNumber(StringBuilder sb, string name, long value) =>
            AppendJsonRaw(sb, name, value.ToString(CultureInfo.InvariantCulture));

        private static void AppendJsonRaw(StringBuilder sb, string name, string value) =>
            sb.Append('"').Append(name).Append("\":").Append(value).Append(',');
#endif

        public void Clear(int generation)
        {
            m_Service?.ResetCity();
            m_ActiveTicket = default;
            m_ActiveCommand = default;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Context != null)
            {
#if RT_DEBUG_TOOLS
                if (m_ComparisonSystem != null) m_Context.World.DestroySystemManaged(m_ComparisonSystem);
#endif
                if (m_SnapshotSystem != null) m_Context.World.DestroySystemManaged(m_SnapshotSystem);
            }
            m_Service?.Dispose();
#if RT_DEBUG_TOOLS
            m_ComparisonSystem = null;
#endif
            m_SnapshotSystem = null;
            m_Service = null;
            m_Context = null;
            m_ActiveTicket = default;
        }
    }
}
