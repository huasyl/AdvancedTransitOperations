using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using RapidTransitMod.Planner;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private const int MaxPlannerJobHistory = 24;
        private static readonly TimeSpan PlannerJobRetention = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<string, PlannerJobState> m_PlannerJobs =
            new ConcurrentDictionary<string, PlannerJobState>(StringComparer.Ordinal);

        [DataContract]
        public class DispatchPlannerRequest
        {
            [DataMember]
            public string draftKey;
            [DataMember]
            public string analysisWindowId;
            [DataMember]
            public string windowStart;
            [DataMember]
            public string windowEnd;
            [DataMember]
            public string[] localLineIds;
            [DataMember]
            public string[] adjustableLineIds;
            [DataMember]
            public string expressSourceMode;
            [DataMember]
            public string expressLineId;
            [DataMember]
            public string virtualExpressBaseLineId;
            [DataMember]
            public string[] expressStopStationIds;
            [DataMember]
            public string departureMode;
            [DataMember]
            public int expressTripsPerHour;
            [DataMember]
            public int intervalMinutes;
            [DataMember]
            public string phaseTime;
            [DataMember]
            public int expressOffsetMinutes;
            [DataMember]
            public int maxOffsetMinutes;
            [DataMember]
            public int offsetStepMinutes;
            [DataMember]
            public int maxLocalRetimeMinutes;
            [DataMember]
            public int maxLocalWaitMinutes;
            [DataMember]
            public int maxAdditionalBypassStations;
            [DataMember]
            public string[] forcedBypassStationIds;
        }

        [DataContract]
        public class DispatchPlannerRequestEchoDto
        {
            [DataMember]
            public string draftKey;
            [DataMember]
            public string analysisWindowId;
            [DataMember]
            public string windowStart;
            [DataMember]
            public string windowEnd;
            [DataMember]
            public string[] localLineIds;
            [DataMember]
            public string[] adjustableLineIds;
            [DataMember]
            public string expressSourceMode;
            [DataMember]
            public string expressLineId;
            [DataMember]
            public string virtualExpressBaseLineId;
            [DataMember]
            public string[] expressStopStationIds;
            [DataMember]
            public string departureMode;
            [DataMember]
            public int expressTripsPerHour;
            [DataMember]
            public int intervalMinutes;
            [DataMember]
            public string phaseTime;
            [DataMember]
            public int expressOffsetMinutes;
            [DataMember]
            public int maxOffsetMinutes;
            [DataMember]
            public int offsetStepMinutes;
            [DataMember]
            public int maxLocalRetimeMinutes;
            [DataMember]
            public int maxLocalWaitMinutes;
            [DataMember]
            public int maxAdditionalBypassStations;
            [DataMember]
            public string[] forcedBypassStationIds;
        }

        [DataContract]
        public class DispatchPlannerInputSummaryDto
        {
            [DataMember]
            public string[] localLineIds;
            [DataMember]
            public string expressSourceCode;
            [DataMember]
            public string expressBaseLineId;
            [DataMember]
            public string[] expressStopStationIds;
            [DataMember]
            public int configuredBypassStationCount;
            [DataMember]
            public int candidateBypassStationCount;
            [DataMember]
            public int sharedCorridorCount;
            [DataMember]
            public int draftTripCount;
            [DataMember]
            public string[] effectiveLineIds;
            [DataMember]
            public string[] autoFixedConstraintLineIds;
            [DataMember]
            public int suppressedFixedVsFixedClusterCount;
            [DataMember]
            public int primaryRiskClusterCount;
        }

        [DataContract]
        public class DispatchPlannerDiagnosticDto
        {
            [DataMember]
            public string level;
            [DataMember]
            public string code;
            [DataMember]
            public string[] relatedClusterIds;
            [DataMember]
            public string[] lineIds;
            [DataMember]
            public string[] stationIds;
            [DataMember]
            public string[] tripIds;
            [DataMember]
            public float minutesA;
            [DataMember]
            public float minutesB;
            [DataMember]
            public int countA;
        }

        [DataContract]
        public class DispatchPlannerLineRoleDto
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public bool participates;
            [DataMember]
            public bool adjustable;
            [DataMember(Name = "fixed")]
            public bool fixedLine;
            [DataMember]
            public bool target;
        }

        [DataContract]
        public class DispatchPlannerLineRoleSummaryDto
        {
            [DataMember]
            public string[] effectiveLineIds;
            [DataMember]
            public string[] adjustableLineIds;
            [DataMember]
            public string[] fixedLineIds;
            [DataMember]
            public string[] targetLineIds;
            [DataMember]
            public string[] autoFixedConstraintLineIds;
            [DataMember]
            public int suppressedFixedVsFixedClusterCount;
            [DataMember]
            public DispatchPlannerLineRoleDto[] roles;
        }

        [DataContract]
        public class DispatchPlannerProblemIssueDto
        {
            [DataMember]
            public string type;
            [DataMember]
            public string severity;
            [DataMember]
            public string clusterId;
            [DataMember]
            public string catchupId;
            [DataMember]
            public string yieldingLineId;
            [DataMember]
            public string priorityLineId;
            [DataMember]
            public string yieldingTripId;
            [DataMember]
            public string priorityTripId;
            [DataMember]
            public float severityMinutes;
            [DataMember]
            public string recommendedBypassStationId;
            [DataMember]
            public float requiredHoldMinutes;
            [DataMember]
            public float holdBudgetMinutes;
            [DataMember]
            public float riskMinutes;
            [DataMember]
            public string[] lineIds;
        }

        [DataContract]
        public class DispatchPlannerScheduleActionDto
        {
            [DataMember]
            public string actionType;
            [DataMember]
            public string type;
            [DataMember]
            public string shape;
            [DataMember]
            public string reason;
            [DataMember]
            public string[] targetRegionIds;
            [DataMember]
            public string[] reasonRegionIds;
            [DataMember]
            public string[] clusterIds;
            [DataMember]
            public string[] reasonClusterIds;
            [DataMember]
            public string[] stationIds;
            [DataMember]
            public string[] affectedLineIds;
            [DataMember]
            public string affectedLineId;
            [DataMember]
            public string[] affectedTripIds;
            [DataMember]
            public string[] tripIds;
            [DataMember]
            public float[] deltaPattern;
            [DataMember]
            public float deltaMinutes;
            [DataMember]
            public float deltaOffsetMinutes;
            [DataMember]
            public float riskScore;
        }

        [DataContract]
        public class DispatchPlannerIssueCountDto
        {
            [DataMember]
            public string type;
            [DataMember]
            public int count;
        }

        [DataContract]
        public class DispatchPlannerFrontendSummaryDto
        {
            [DataMember]
            public string[] effectiveLineIds;
            [DataMember]
            public string[] adjustableLineIds;
            [DataMember]
            public string[] fixedLineIds;
            [DataMember]
            public string[] targetLineIds;
            [DataMember]
            public string[] actuallyAdjustedLineIds;
            [DataMember]
            public DispatchPlannerIssueCountDto[] issueCountsByType;
            [DataMember]
            public int actionCount;
            [DataMember]
            public int catchupClusterCount;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
        }

        [DataContract]
        public class DispatchPlannerRiskItemDto
        {
            [DataMember]
            public string riskId;
            [DataMember]
            public string problemType;
            [DataMember]
            public string resolutionState;
            [DataMember]
            public string pairRole;
            [DataMember]
            public string treatmentType;
            [DataMember]
            public string blockReasonCode;
            [DataMember]
            public string[] suggestedOptionCodes;
            [DataMember]
            public string yieldingLineId;
            [DataMember]
            public string priorityLineId;
            [DataMember]
            public string yieldingTripId;
            [DataMember]
            public string priorityTripId;
            [DataMember]
            public string yieldingDepartTime;
            [DataMember]
            public string priorityDepartTime;
            [DataMember]
            public string fromStationId;
            [DataMember]
            public string toStationId;
            [DataMember]
            public string catchupFromStationId;
            [DataMember]
            public string catchupToStationId;
            [DataMember]
            public string catchupTime;
            [DataMember]
            public string selectedBypassStationId;
            [DataMember]
            public float requiredHoldMinutes;
            [DataMember]
            public float plannedAdjustmentMinutes;
            [DataMember]
            public float holdBudgetMinutes;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
            [DataMember]
            public float requiredMarginMinutes;
            [DataMember]
            public float currentWorstCaseGapMinutes;
        }

        [DataContract]
        public class DispatchPlannerPlanMetricsDto
        {
            [DataMember]
            public float expressSavedMinutes;
            [DataMember]
            public float localWaitMinutes;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
            [DataMember]
            public int addedBypassStationCount;
            [DataMember]
            public int retimedTripCount;
            [DataMember]
            public int recommendedExpressOffsetDeltaMinutes;
        }

        [DataContract]
        public class DispatchPlannerPlanSummaryDto
        {
            [DataMember]
            public string planId;
            [DataMember]
            public string objectiveId;
            [DataMember]
            public string status;
            [DataMember]
            public float score;
            [DataMember]
            public float expressSavedMinutes;
            [DataMember]
            public float localWaitMinutes;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
            [DataMember]
            public int addedBypassStationCount;
            [DataMember]
            public int retimedTripCount;
            [DataMember]
            public int recommendedExpressOffsetDeltaMinutes;
        }

        [DataContract]
        public class DispatchPlannerRiskClusterDto
        {
            [DataMember]
            public string clusterId;
            [DataMember]
            public string severityLevel;
            [DataMember]
            public string yieldingLineId;
            [DataMember]
            public string priorityLineId;
            [DataMember]
            public string fromStationId;
            [DataMember]
            public string toStationId;
            [DataMember]
            public int catchupCount;
            [DataMember]
            public float maxSeverityMinutes;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
            [DataMember]
            public string recommendedBypassStationId;
            [DataMember]
            public string[] recommendedActionCodes;
            [DataMember]
            public DispatchPlannerRiskEventDto[] representativeEvents;
        }

        [DataContract]
        public class DispatchPlannerRiskEventDto
        {
            [DataMember]
            public string eventId;
            [DataMember]
            public string statusCode;
            [DataMember]
            public string reasonCode;
            [DataMember]
            public string problemType;
            [DataMember]
            public string resolutionState;
            [DataMember]
            public string pairRole;
            [DataMember]
            public string treatmentType;
            [DataMember]
            public string blockReasonCode;
            [DataMember]
            public string[] suggestedOptionCodes;
            [DataMember]
            public string yieldingLineId;
            [DataMember]
            public string priorityLineId;
            [DataMember]
            public string yieldingTripId;
            [DataMember]
            public string priorityTripId;
            [DataMember]
            public string yieldingDepartTime;
            [DataMember]
            public string priorityDepartTime;
            [DataMember]
            public string fromStationId;
            [DataMember]
            public string toStationId;
            [DataMember]
            public string catchupFromStationId;
            [DataMember]
            public string catchupToStationId;
            [DataMember]
            public string catchupTime;
            [DataMember]
            public float requiredHoldMinutes;
            [DataMember]
            public float plannedAdjustmentMinutes;
            [DataMember]
            public float holdBudgetMinutes;
            [DataMember]
            public float unresolvedRiskMinutes;
            [DataMember]
            public float robustnessRiskMinutes;
            [DataMember]
            public string selectedBypassStationId;
            [DataMember]
            public float requiredMarginMinutes;
            [DataMember]
            public float currentWorstCaseGapMinutes;
        }

        [DataContract]
        public class DispatchPlannerOptimizationRegionDto
        {
            [DataMember]
            public string regionId;
            [DataMember]
            public string[] clusterIds;
            [DataMember]
            public string[] yieldingLineIds;
            [DataMember]
            public string[] priorityLineIds;
            [DataMember]
            public int eventCount;
            [DataMember]
            public float firstCatchupMinute;
            [DataMember]
            public float lastCatchupMinute;
            [DataMember]
            public float totalUnresolvedRiskMinutes;
            [DataMember]
            public float totalRobustnessRiskMinutes;
        }

        [DataContract]
        public class DispatchPlannerPreviewRowDto
        {
            [DataMember]
            public string tripId;
            [DataMember]
            public string time;
            [DataMember]
            public string lineId;
            [DataMember]
            public string lineName;
            [DataMember]
            public string kind;
            [DataMember]
            public string originStationId;
            [DataMember]
            public string statusCode;
            [DataMember]
            public int deltaMinutes;
            [DataMember]
            public int statusMinutes;
        }

        [DataContract]
        public class DispatchPlannerChangedRowDto
        {
            [DataMember]
            public string tripId;
            [DataMember]
            public string lineId;
            [DataMember]
            public string kind;
            [DataMember]
            public string beforeTime;
            [DataMember]
            public string afterTime;
            [DataMember]
            public int scheduleShiftMinutes;
            [DataMember]
            public int predictedDelayMinutes;
            [DataMember]
            public int totalDeltaMinutes;
            [DataMember]
            public string changeType;
            [DataMember]
            public string statusCode;
            [DataMember]
            public int statusMinutes;
        }

        [DataContract]
        public class DispatchPlannerChangedWindowDto
        {
            [DataMember]
            public string windowId;
            [DataMember]
            public string regionId;
            [DataMember]
            public string[] lineIds;
            [DataMember]
            public string[] lineNames;
            [DataMember]
            public string fromTime;
            [DataMember]
            public string toTime;
            [DataMember]
            public string[] changeTypes;
            [DataMember]
            public DispatchPlannerChangedRowDto[] rowDiffs;
        }

        [DataContract]
        public class DispatchPlannerPlanDetailDto
        {
            [DataMember]
            public string planId;
            [DataMember]
            public string objectiveId;
            [DataMember]
            public string status;
            [DataMember]
            public float score;
            [DataMember]
            public int recommendedExpressOffsetDeltaMinutes;
            [DataMember]
            public DispatchPlannerPlanMetricsDto metrics;
            [DataMember]
            public string[] selectedBypassStationIds;
            [DataMember]
            public DispatchPlannerRiskClusterDto[] riskClusters;
            [DataMember]
            public DispatchPlannerRiskItemDto[] riskItems;
            [DataMember]
            public DispatchPlannerOptimizationRegionDto[] optimizationRegions;
            [DataMember]
            public DispatchPlannerScheduleActionDto[] structuredScheduleActions;
            [DataMember]
            public DispatchPlannerProblemIssueDto[] problemIssues;
            [DataMember]
            public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
            [DataMember]
            public DispatchPlannerFrontendSummaryDto frontendSummary;
            [DataMember]
            public DispatchPlannerPreviewRowDto[] timetablePreviewRows;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] plannerBaselineRows;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] plannerReplacementRows;
            [DataMember]
            public DispatchPlannerChangedWindowDto[] changedWindows;
            [DataMember]
            public DispatchPlannerDiagnosticDto[] diagnostics;
        }

        [DataContract]
        public class DispatchPlannerPerformanceDto
        {
            [DataMember]
            public string engineMode;
            [DataMember]
            public int localLineCount;
            [DataMember]
            public int expressLineCount;
            [DataMember]
            public int pursuitTrunkCount;
            [DataMember]
            public int rawCatchupEventCount;
            [DataMember]
            public int riskClusterCount;
            [DataMember]
            public int optimizationRegionCount;
        }

        [DataContract]
        public class DispatchPlannerResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string engineVersion;
            [DataMember]
            public DispatchPlannerRequestEchoDto requestEcho;
            [DataMember]
            public DispatchPlannerInputSummaryDto inputSummary;
            [DataMember]
            public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
            [DataMember]
            public string defaultPlanId;
            [DataMember]
            public DispatchPlannerPlanDetailDto[] plans;
            [DataMember]
            public DispatchPlannerPlanSummaryDto[] planSummaries;
            [DataMember]
            public DispatchPlannerPlanDetailDto selectedPlan;
            [DataMember]
            public DispatchPlannerDiagnosticDto[] diagnostics;
            [DataMember]
            public DispatchPlannerPerformanceDto performance;
        }

        [DataContract]
        public class DispatchPlannerJobStatusDto
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string jobId;
            [DataMember]
            public string state;
            [DataMember]
            public string error;
            [DataMember]
            public DispatchPlannerResult result;
        }

        public DispatchPlannerResult RunWorkbenchPlanner(DispatchPlannerRequest request)
        {
            DispatchPlannerExportSnapshot snapshot = BuildPlannerExportSnapshot();
            DispatchWorkbenchPlannerService service = new DispatchWorkbenchPlannerService();
            return service.Execute(snapshot, request ?? new DispatchPlannerRequest());
        }

        public DispatchPlannerJobStatusDto StartWorkbenchPlannerJob(DispatchPlannerRequest request)
        {
            CleanupPlannerJobs();

            DispatchPlannerExportSnapshot snapshot = BuildPlannerExportSnapshot();
            string jobId = "planner-job-" + Guid.NewGuid().ToString("N");
            PlannerJobState jobState = new PlannerJobState(jobId);
            m_PlannerJobs[jobId] = jobState;

            Task.Run(() => ExecutePlannerJob(jobState, snapshot, request ?? new DispatchPlannerRequest()));

            return jobState.CreateStatusCopy();
        }

        public DispatchPlannerJobStatusDto GetWorkbenchPlannerJobStatus(string jobId)
        {
            CleanupPlannerJobs();

            if (string.IsNullOrWhiteSpace(jobId)
                || !m_PlannerJobs.TryGetValue(jobId, out PlannerJobState jobState))
            {
                return new DispatchPlannerJobStatusDto
                {
                    success = false,
                    jobId = jobId ?? string.Empty,
                    state = "missing",
                    error = "planner-job-not-found",
                    result = null
                };
            }

            return jobState.CreateStatusCopy();
        }

        public string RunWorkbenchPlannerJson(string requestJson)
        {
            DispatchPlannerRequest request = DispatchWorkbenchJson.Deserialize<DispatchPlannerRequest>(requestJson);
            return DispatchWorkbenchJson.Serialize(RunWorkbenchPlanner(request));
        }

        public string StartWorkbenchPlannerJobJson(string requestJson)
        {
            try
            {
                DispatchPlannerRequest request = DispatchWorkbenchJson.Deserialize<DispatchPlannerRequest>(requestJson);
                return DispatchWorkbenchJson.Serialize(StartWorkbenchPlannerJob(request));
            }
            catch (Exception ex)
            {
                return DispatchWorkbenchJson.Serialize(new DispatchPlannerJobStatusDto
                {
                    success = false,
                    jobId = string.Empty,
                    state = "failed",
                    error = ex.GetType().Name + ": " + ex.Message,
                    result = null
                });
            }
        }

        public string GetWorkbenchPlannerJobStatusJson(string jobId)
        {
            return DispatchWorkbenchJson.Serialize(GetWorkbenchPlannerJobStatus(jobId));
        }

        private void ExecutePlannerJob(
            PlannerJobState jobState,
            DispatchPlannerExportSnapshot snapshot,
            DispatchPlannerRequest request)
        {
            if (jobState == null)
            {
                return;
            }

            jobState.UpdateStatus("running", true, string.Empty, null);

            try
            {
                DispatchWorkbenchPlannerService service = new DispatchWorkbenchPlannerService();
                DispatchPlannerResult result = service.Execute(snapshot, request ?? new DispatchPlannerRequest());
                jobState.UpdateStatus("completed", true, string.Empty, result);
            }
            catch (Exception ex)
            {
                jobState.UpdateStatus("failed", false, ex.GetType().Name + ": " + ex.Message, null);
                Mod.log.Info("[PlannerJob] failed " + jobState.JobId + ": " + ex);
            }
        }

        private void CleanupPlannerJobs()
        {
            DateTime utcNow = DateTime.UtcNow;
            foreach (var entry in m_PlannerJobs)
            {
                PlannerJobState jobState = entry.Value;
                if (jobState == null)
                {
                    m_PlannerJobs.TryRemove(entry.Key, out _);
                    continue;
                }

                if (!jobState.IsTerminal)
                {
                    continue;
                }

                if ((utcNow - jobState.LastUpdatedUtc) > PlannerJobRetention)
                {
                    m_PlannerJobs.TryRemove(entry.Key, out _);
                }
            }

            if (m_PlannerJobs.Count <= MaxPlannerJobHistory)
            {
                return;
            }

            foreach (var entry in m_PlannerJobs
                .Where(item => item.Value != null && item.Value.IsTerminal)
                .OrderBy(item => item.Value.LastUpdatedUtc)
                .Take(Math.Max(0, m_PlannerJobs.Count - MaxPlannerJobHistory)))
            {
                m_PlannerJobs.TryRemove(entry.Key, out _);
            }
        }

        private sealed class PlannerJobState
        {
            private readonly object m_Sync = new object();
            private DispatchPlannerJobStatusDto m_Status;

            public PlannerJobState(string jobId)
            {
                JobId = jobId ?? string.Empty;
                LastUpdatedUtc = DateTime.UtcNow;
                m_Status = new DispatchPlannerJobStatusDto
                {
                    success = true,
                    jobId = JobId,
                    state = "queued",
                    error = string.Empty,
                    result = null
                };
            }

            public string JobId { get; }

            public DateTime LastUpdatedUtc { get; private set; }

            public bool IsTerminal
            {
                get
                {
                    lock (m_Sync)
                    {
                        return IsTerminalState(m_Status?.state);
                    }
                }
            }

            public DispatchPlannerJobStatusDto CreateStatusCopy()
            {
                lock (m_Sync)
                {
                    return new DispatchPlannerJobStatusDto
                    {
                        success = m_Status?.success ?? false,
                        jobId = m_Status?.jobId ?? string.Empty,
                        state = m_Status?.state ?? string.Empty,
                        error = m_Status?.error ?? string.Empty,
                        result = m_Status?.result
                    };
                }
            }

            public void UpdateStatus(
                string state,
                bool success,
                string error,
                DispatchPlannerResult result)
            {
                lock (m_Sync)
                {
                    m_Status = new DispatchPlannerJobStatusDto
                    {
                        success = success,
                        jobId = JobId,
                        state = state ?? string.Empty,
                        error = error ?? string.Empty,
                        result = result
                    };
                    LastUpdatedUtc = DateTime.UtcNow;
                }
            }

            private static bool IsTerminalState(string state)
            {
                return string.Equals(state, "completed", StringComparison.Ordinal)
                    || string.Equals(state, "failed", StringComparison.Ordinal)
                    || string.Equals(state, "missing", StringComparison.Ordinal);
            }
        }
    }
}
