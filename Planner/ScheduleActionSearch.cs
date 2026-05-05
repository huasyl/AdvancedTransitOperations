using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class ScheduleActionSearch
    {
        public List<PlannerPlanModel> BuildInitialPlans(
            PlannerContext context,
            List<PlannerRiskCluster> riskClusters,
            List<PlannerCatchupEvent> catchupEvents,
            List<PlannerValidationIssue> diagnostics,
            PlannerRuntimeCatalog runtimeCatalog,
            string[] activeVirtualBypassStationIds,
            int activeExpressOffsetMinutes,
            string planVariantKey,
            List<PlannerWorkingRow> baseWorkingRows)
        {
            List<PlannerPlanModel> plans = new List<PlannerPlanModel>();
            foreach (PlannerObjectiveDefinition objective in PlannerDefaults.Objectives)
            {
                PlannerPlanModel plan = new PlannerPlanModel();
                string stationSetKey = string.Join("+", (activeVirtualBypassStationIds ?? new string[0])
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .OrderBy(stationId => stationId));
                plan.PlanId = "backend-" + objective.Id
                    + ":offset:" + activeExpressOffsetMinutes
                    + (string.IsNullOrEmpty(planVariantKey) ? "" : ":variant:" + planVariantKey)
                    + (string.IsNullOrEmpty(stationSetKey) ? "" : ":bypass:" + stationSetKey);
                plan.ObjectiveId = objective.Id;
                plan.RecommendedExpressOffsetDeltaMinutes = activeExpressOffsetMinutes;
                plan.BaselineRows = CloneWorkingRows(baseWorkingRows);
                plan.AdjustedRows = CloneWorkingRows(context.WorkingRows);
                plan.RiskClusters.AddRange(riskClusters);
                plan.CatchupEvents.AddRange(catchupEvents);
                plan.Diagnostics.AddRange(diagnostics);
                plan.SelectedBypassStationIds = riskClusters
                    .Where(cluster => cluster.RecommendedBypassStation != null)
                    .Select(cluster => cluster.RecommendedBypassStation.StationId)
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                plan.ExpressSavedMinutes = ComputeExpressSavedMinutes(context, catchupEvents);
                plan.LocalWaitMinutes = ComputeLocalWaitMinutes(catchupEvents);
                plan.UnresolvedRiskMinutes = PlannerMath.Round2(riskClusters.Sum(cluster => cluster.UnresolvedRiskMinutes));
                plan.RobustnessRiskMinutes = PlannerMath.Round2(riskClusters.Sum(cluster => cluster.RobustnessRiskMinutes));
                plan.AddedBypassStationCount = (activeVirtualBypassStationIds ?? new string[0])
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                plan.RetimedTripCount = CountRetimedTrips(baseWorkingRows, context.WorkingRows);
                plan.StructuredScheduleActions = BuildStructuredScheduleActions(
                    context,
                    riskClusters,
                    catchupEvents,
                    activeVirtualBypassStationIds,
                    activeExpressOffsetMinutes,
                    baseWorkingRows,
                    context.WorkingRows);
                plan.ProblemIssues = BuildProblemIssues(context, plan, riskClusters, catchupEvents);
                plan.FrontendSummary = BuildFrontendSummary(context, plan);
                plan.PreviewRows = BuildPreviewRows(context, catchupEvents, runtimeCatalog);
                plan.Status = ResolvePlanStatus(plan, diagnostics);
                plans.Add(plan);
            }

            return plans;
        }

        private static float ComputeExpressSavedMinutes(
            PlannerContext context,
            List<PlannerCatchupEvent> catchupEvents)
        {
            Dictionary<string, float> savedByTripId = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in catchupEvents ?? new List<PlannerCatchupEvent>())
            {
                if (catchupEvent == null || string.IsNullOrEmpty(catchupEvent.ExpressTripId))
                {
                    continue;
                }
                if (!savedByTripId.TryGetValue(catchupEvent.ExpressTripId, out float current)
                    || catchupEvent.ExpressSavedMinutes > current)
                {
                    savedByTripId[catchupEvent.ExpressTripId] = catchupEvent.ExpressSavedMinutes;
                }
            }

            return savedByTripId.Count == 0
                ? 0f
                : PlannerMath.Round2(savedByTripId.Values.Sum() / savedByTripId.Count);
        }

        private static float ComputeLocalWaitMinutes(List<PlannerCatchupEvent> catchupEvents)
        {
            Dictionary<string, float> waitByTripId = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in catchupEvents ?? new List<PlannerCatchupEvent>())
            {
                if (catchupEvent == null || string.IsNullOrEmpty(catchupEvent.LocalTripId) || catchupEvent.ResolvedHoldMinutes <= 0f)
                {
                    continue;
                }
                if (!waitByTripId.TryGetValue(catchupEvent.LocalTripId, out float current)
                    || catchupEvent.ResolvedHoldMinutes > current)
                {
                    waitByTripId[catchupEvent.LocalTripId] = catchupEvent.ResolvedHoldMinutes;
                }
            }

            return PlannerMath.Round2(waitByTripId.Values.Sum());
        }

        private static List<PlannerWorkingRow> CloneWorkingRows(IEnumerable<PlannerWorkingRow> rows)
        {
            return (rows ?? Array.Empty<PlannerWorkingRow>())
                .Select(row => new PlannerWorkingRow
                {
                    Id = row.Id,
                    LineId = row.LineId,
                    Kind = row.Kind,
                    Minute = row.Minute,
                    Source = row.Source,
                    Note = row.Note
                })
                .ToList();
        }

        private static List<DepartureControlSystem.DispatchPlannerProblemIssueDto> BuildProblemIssues(
            PlannerContext context,
            PlannerPlanModel plan,
            List<PlannerRiskCluster> riskClusters,
            List<PlannerCatchupEvent> catchupEvents)
        {
            List<DepartureControlSystem.DispatchPlannerProblemIssueDto> issues = new List<DepartureControlSystem.DispatchPlannerProblemIssueDto>();
            foreach (PlannerRiskCluster cluster in riskClusters.Where(item => item.UnresolvedRiskMinutes > 0f).Take(8))
            {
                issues.Add(new DepartureControlSystem.DispatchPlannerProblemIssueDto
                {
                    type = "unresolvedConflict",
                    severity = "high",
                    clusterId = cluster.ClusterId,
                    yieldingLineId = string.IsNullOrEmpty(cluster.YieldingLineId) ? cluster.LocalLineId : cluster.YieldingLineId,
                    priorityLineId = string.IsNullOrEmpty(cluster.PriorityLineId) ? cluster.ExpressLineId : cluster.PriorityLineId,
                    severityMinutes = PlannerMath.Round2(cluster.UnresolvedRiskMinutes),
                    recommendedBypassStationId = cluster.RecommendedBypassStation?.StationId ?? string.Empty,
                    lineIds = new[] { cluster.LocalLineId, cluster.ExpressLineId }
                        .Where(lineId => !string.IsNullOrEmpty(lineId))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                });
            }

            foreach (PlannerCatchupEvent catchupEvent in catchupEvents
                .Where(item => item.RequiredHoldMinutes > item.HoldBudgetMinutes)
                .Take(8))
            {
                issues.Add(new DepartureControlSystem.DispatchPlannerProblemIssueDto
                {
                    type = "waitLimitExceeded",
                    severity = "medium",
                    catchupId = catchupEvent.EventId,
                    yieldingLineId = string.IsNullOrEmpty(catchupEvent.YieldingLineId) ? catchupEvent.LocalLineId : catchupEvent.YieldingLineId,
                    priorityLineId = string.IsNullOrEmpty(catchupEvent.PriorityLineId) ? catchupEvent.ExpressLineId : catchupEvent.PriorityLineId,
                    yieldingTripId = string.IsNullOrEmpty(catchupEvent.YieldingTripId) ? catchupEvent.LocalTripId : catchupEvent.YieldingTripId,
                    priorityTripId = string.IsNullOrEmpty(catchupEvent.PriorityTripId) ? catchupEvent.ExpressTripId : catchupEvent.PriorityTripId,
                    requiredHoldMinutes = catchupEvent.RequiredHoldMinutes,
                    holdBudgetMinutes = catchupEvent.HoldBudgetMinutes,
                    lineIds = new[] { catchupEvent.LocalLineId, catchupEvent.ExpressLineId }
                        .Where(lineId => !string.IsNullOrEmpty(lineId))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                });
            }

            if (plan.RobustnessRiskMinutes > 0f)
            {
                issues.Add(new DepartureControlSystem.DispatchPlannerProblemIssueDto
                {
                    type = "robustnessWeak",
                    severity = plan.RobustnessRiskMinutes > 10f ? "medium" : "low",
                    riskMinutes = plan.RobustnessRiskMinutes,
                    lineIds = new string[0]
                });
            }

            HashSet<string> targetLineIds = new HashSet<string>(context.TargetLineIds ?? new string[0], StringComparer.Ordinal);
            string[] fixedAffectedLineIds = plan.StructuredScheduleActions
                .SelectMany(action => action.affectedLineIds ?? new string[0])
                .Where(lineId =>
                    !string.IsNullOrEmpty(lineId)
                    && (context.FixedLineIds ?? Array.Empty<string>()).Contains(lineId)
                    && !targetLineIds.Contains(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (fixedAffectedLineIds.Length > 0)
            {
                issues.Add(new DepartureControlSystem.DispatchPlannerProblemIssueDto
                {
                    type = "fixedLineAffected",
                    severity = "high",
                    lineIds = fixedAffectedLineIds
                });
            }

            return issues;
        }

        private static List<DepartureControlSystem.DispatchPlannerScheduleActionDto> BuildStructuredScheduleActions(
            PlannerContext context,
            List<PlannerRiskCluster> riskClusters,
            List<PlannerCatchupEvent> catchupEvents,
            string[] activeVirtualBypassStationIds,
            int activeExpressOffsetMinutes,
            List<PlannerWorkingRow> baselineRows,
            List<PlannerWorkingRow> adjustedRows)
        {
            List<DepartureControlSystem.DispatchPlannerScheduleActionDto> actions = new List<DepartureControlSystem.DispatchPlannerScheduleActionDto>();
            if (activeExpressOffsetMinutes != 0)
            {
                string[] affectedLineIds = context.TargetLineIds ?? new string[0];
                actions.Add(new DepartureControlSystem.DispatchPlannerScheduleActionDto
                {
                    actionType = "expressOffset",
                    type = "expressOffset",
                    shape = "uniform",
                    reason = "shiftTargetService",
                    targetRegionIds = new string[0],
                    reasonRegionIds = new string[0],
                    clusterIds = new string[0],
                    reasonClusterIds = new string[0],
                    stationIds = new string[0],
                    affectedLineIds = affectedLineIds,
                    affectedLineId = affectedLineIds.Length > 0 ? affectedLineIds[0] : string.Empty,
                    affectedTripIds = new string[0],
                    tripIds = new string[0],
                    deltaPattern = new[] { (float)activeExpressOffsetMinutes },
                    deltaMinutes = Math.Abs(activeExpressOffsetMinutes),
                    deltaOffsetMinutes = activeExpressOffsetMinutes,
                    riskScore = 0f
                });
            }

            HashSet<string> activeStationIds = new HashSet<string>(activeVirtualBypassStationIds ?? new string[0], StringComparer.Ordinal);
            foreach (PlannerRiskCluster cluster in riskClusters
                .Where(item => item.RecommendedBypassStation != null
                    && item.RecommendedBypassStation.IsVirtualCandidate
                    && !item.RecommendedBypassStation.IsConfigured
                    && activeStationIds.Contains(item.RecommendedBypassStation.StationId))
                .Take(4))
            {
                string affectedLineId = string.IsNullOrEmpty(cluster.YieldingLineId) ? cluster.LocalLineId : cluster.YieldingLineId;
                actions.Add(new DepartureControlSystem.DispatchPlannerScheduleActionDto
                {
                    actionType = "bypassSet",
                    type = "bypassSet",
                    shape = "singleStation",
                    reason = cluster.UnresolvedRiskMinutes > 0f ? "resolveConflict" : "improveRobustness",
                    targetRegionIds = new[] { cluster.ClusterId },
                    reasonRegionIds = new[] { cluster.ClusterId },
                    clusterIds = new[] { cluster.ClusterId },
                    reasonClusterIds = new[] { cluster.ClusterId },
                    stationIds = new[] { cluster.RecommendedBypassStation.StationId },
                    affectedLineIds = string.IsNullOrEmpty(affectedLineId) ? new string[0] : new[] { affectedLineId },
                    affectedLineId = affectedLineId,
                    affectedTripIds = new string[0],
                    tripIds = new string[0],
                    deltaPattern = new float[0],
                    deltaMinutes = 0f,
                    deltaOffsetMinutes = 0f,
                    riskScore = PlannerMath.Round2(cluster.UnresolvedRiskMinutes + (cluster.RobustnessRiskMinutes * 0.75f))
                });
            }

            Dictionary<string, PlannerWorkingRow> baselineById = (baselineRows ?? new List<PlannerWorkingRow>())
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            foreach (IGrouping<string, PlannerWorkingRow> lineGroup in (adjustedRows ?? new List<PlannerWorkingRow>())
                .Where(row =>
                    row != null
                    && !string.IsNullOrEmpty(row.Id)
                    && !string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase)
                    && baselineById.TryGetValue(row.Id, out PlannerWorkingRow baseline)
                    && baseline.Minute != row.Minute)
                .GroupBy(row => row.LineId ?? string.Empty, StringComparer.Ordinal))
            {
                PlannerWorkingRow[] rows = lineGroup.ToArray();
                float[] deltaPattern = rows
                    .Select(row => (float)(row.Minute - baselineById[row.Id].Minute))
                    .ToArray();
                string[] tripIds = rows
                    .Select(row => row.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                float deltaMinutes = deltaPattern.Length == 0 ? 0f : deltaPattern.Max(delta => Math.Abs(delta));
                actions.Add(new DepartureControlSystem.DispatchPlannerScheduleActionDto
                {
                    actionType = "retime",
                    type = "retime",
                    shape = "localWindow",
                    reason = "retimeAdjustableLocalTrips",
                    targetRegionIds = ResolveRegionIdsForLine(riskClusters, lineGroup.Key),
                    reasonRegionIds = ResolveRegionIdsForLine(riskClusters, lineGroup.Key),
                    clusterIds = ResolveClusterIdsForLine(riskClusters, lineGroup.Key),
                    reasonClusterIds = ResolveClusterIdsForLine(riskClusters, lineGroup.Key),
                    stationIds = new string[0],
                    affectedLineIds = new[] { lineGroup.Key },
                    affectedLineId = lineGroup.Key,
                    affectedTripIds = tripIds,
                    tripIds = tripIds,
                    deltaPattern = deltaPattern,
                    deltaMinutes = PlannerMath.Round2(deltaMinutes),
                    deltaOffsetMinutes = 0f,
                    riskScore = PlannerMath.Round2(deltaMinutes)
                });
            }

            Dictionary<string, string[]> clusterIdsByCatchupEventId = riskClusters
                .SelectMany(cluster => cluster.CatchupIds.Select(catchupId => new { catchupId, clusterId = cluster.ClusterId }))
                .GroupBy(item => item.catchupId ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.clusterId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            foreach (IGrouping<string, PlannerCatchupEvent> lineGroup in (catchupEvents ?? new List<PlannerCatchupEvent>())
                .Where(item => item != null && item.ResolvedHoldMinutes > 0f && !string.IsNullOrEmpty(item.LocalLineId))
                .GroupBy(item => item.LocalLineId, StringComparer.Ordinal))
            {
                PlannerCatchupEvent[] eventsByLine = lineGroup.ToArray();
                string[] tripIds = eventsByLine
                    .Select(item => item.LocalTripId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                string[] clusterIds = eventsByLine
                    .SelectMany(item => clusterIdsByCatchupEventId.TryGetValue(item.EventId ?? string.Empty, out string[] ids) ? ids : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                string[] regionIds = riskClusters
                    .Where(cluster => clusterIds.Contains(cluster.ClusterId))
                    .Select(cluster => cluster.ClusterId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                float deltaMinutes = eventsByLine.Max(item => item.ResolvedHoldMinutes);
                actions.Add(new DepartureControlSystem.DispatchPlannerScheduleActionDto
                {
                    actionType = "predictedHold",
                    type = "predictedHold",
                    shape = "runtimeWait",
                    reason = "yieldToPriorityService",
                    targetRegionIds = regionIds,
                    reasonRegionIds = regionIds,
                    clusterIds = clusterIds,
                    reasonClusterIds = clusterIds,
                    stationIds = eventsByLine
                        .Select(item => item.SelectedBypassStation?.StationId ?? string.Empty)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    affectedLineIds = new[] { lineGroup.Key },
                    affectedLineId = lineGroup.Key,
                    affectedTripIds = tripIds,
                    tripIds = tripIds,
                    deltaPattern = eventsByLine.Select(item => PlannerMath.Round2(item.ResolvedHoldMinutes)).ToArray(),
                    deltaMinutes = PlannerMath.Round2(deltaMinutes),
                    deltaOffsetMinutes = 0f,
                    riskScore = PlannerMath.Round2(eventsByLine.Sum(item => item.ResolvedHoldMinutes))
                });
            }

            return actions;
        }

        private static DepartureControlSystem.DispatchPlannerFrontendSummaryDto BuildFrontendSummary(
            PlannerContext context,
            PlannerPlanModel plan)
        {
            string[] adjustedLineIds = plan.StructuredScheduleActions
                .SelectMany(action => action.affectedLineIds ?? new string[0])
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            DepartureControlSystem.DispatchPlannerIssueCountDto[] issueCounts = plan.ProblemIssues
                .GroupBy(issue => issue.type ?? string.Empty)
                .Select(group => new DepartureControlSystem.DispatchPlannerIssueCountDto
                {
                    type = group.Key,
                    count = group.Count()
                })
                .ToArray();

            return new DepartureControlSystem.DispatchPlannerFrontendSummaryDto
            {
                effectiveLineIds = context.EffectiveLineIds ?? context.SelectedLineIds ?? new string[0],
                adjustableLineIds = context.AdjustableLineIds ?? new string[0],
                fixedLineIds = context.FixedLineIds ?? new string[0],
                targetLineIds = context.TargetLineIds ?? new string[0],
                actuallyAdjustedLineIds = adjustedLineIds,
                issueCountsByType = issueCounts,
                actionCount = plan.StructuredScheduleActions.Count,
                catchupClusterCount = plan.RiskClusters.Count,
                unresolvedRiskMinutes = plan.UnresolvedRiskMinutes,
                robustnessRiskMinutes = plan.RobustnessRiskMinutes
            };
        }

        private static string ResolvePlanStatus(PlannerPlanModel plan, List<PlannerValidationIssue> diagnostics)
        {
            if (diagnostics.Any(issue => string.Equals(issue.Level, "error", StringComparison.Ordinal)))
            {
                return "infeasible";
            }
            if (plan.CatchupEvents.Any(item =>
                string.Equals(item.ResolutionState, "blocked", StringComparison.Ordinal)
                && string.Equals(item.PairRole, "target-adjustable", StringComparison.Ordinal)))
            {
                return "blocked";
            }
            if (plan.CatchupEvents.Any(item =>
                string.Equals(item.ResolutionState, "actionable", StringComparison.Ordinal)
                && string.Equals(item.PairRole, "target-adjustable", StringComparison.Ordinal)))
            {
                return "needsAction";
            }
            if (plan.UnresolvedRiskMinutes > 0f)
            {
                return "risk";
            }
            if (plan.RobustnessRiskMinutes > 0f)
            {
                return "fragile";
            }
            return "feasible";
        }

        private static List<DepartureControlSystem.DispatchPlannerPreviewRowDto> BuildPreviewRows(
            PlannerContext context,
            List<PlannerCatchupEvent> catchupEvents,
            PlannerRuntimeCatalog runtimeCatalog)
        {
            Dictionary<string, float> delayByTripId = new Dictionary<string, float>(StringComparer.Ordinal);
            HashSet<string> expressThroughTripIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in catchupEvents)
            {
                if (!delayByTripId.TryGetValue(catchupEvent.LocalTripId, out float delayMinutes)
                    || catchupEvent.ResolvedHoldMinutes > delayMinutes)
                {
                    delayByTripId[catchupEvent.LocalTripId] = catchupEvent.ResolvedHoldMinutes;
                }
                expressThroughTripIds.Add(catchupEvent.ExpressTripId);
            }

            List<DepartureControlSystem.DispatchPlannerPreviewRowDto> rows = new List<DepartureControlSystem.DispatchPlannerPreviewRowDto>();
            Dictionary<string, bool> seenFirstByLine = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (PlannerWorkingRow row in context.WorkingRows.OrderBy(item => item.Minute).ThenBy(item => item.LineId))
            {
                string lineName = runtimeCatalog.ModelsByLineId.TryGetValue(row.LineId, out PlannerLineRuntimeModel model)
                    ? model.LineName
                    : row.LineId;
                string originStationId = runtimeCatalog.ModelsByLineId.TryGetValue(row.LineId, out PlannerLineRuntimeModel runtimeModel)
                    && runtimeModel.Stations.Count > 0
                    ? runtimeModel.Stations[0].id
                    : string.Empty;
                int deltaMinutes = delayByTripId.TryGetValue(row.Id, out float resolvedHoldMinutes)
                    ? (int)Math.Round(resolvedHoldMinutes)
                    : 0;
                string statusCode;
                if (string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase))
                {
                    bool through = expressThroughTripIds.Contains(row.Id);
                    statusCode = through ? "expressPass" : "express";
                }
                else if (!seenFirstByLine.ContainsKey(row.LineId))
                {
                    statusCode = "firstDeparture";
                    seenFirstByLine[row.LineId] = true;
                }
                else if (deltaMinutes > 0)
                {
                    statusCode = "delayedByBypass";
                }
                else
                {
                    statusCode = "normal";
                }

                rows.Add(new DepartureControlSystem.DispatchPlannerPreviewRowDto
                {
                    tripId = row.Id,
                    time = PlannerMath.MinutesToTime(row.Minute),
                    lineId = row.LineId,
                    lineName = lineName,
                    kind = row.Kind,
                    originStationId = originStationId,
                    statusCode = statusCode,
                    deltaMinutes = deltaMinutes,
                    statusMinutes = deltaMinutes
                });
            }

            return rows;
        }

        private static int CountRetimedTrips(List<PlannerWorkingRow> baselineRows, List<PlannerWorkingRow> adjustedRows)
        {
            Dictionary<string, PlannerWorkingRow> baselineById = (baselineRows ?? new List<PlannerWorkingRow>())
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            return (adjustedRows ?? new List<PlannerWorkingRow>())
                .Count(row =>
                    row != null
                    && !string.IsNullOrEmpty(row.Id)
                    && !string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase)
                    && baselineById.TryGetValue(row.Id, out PlannerWorkingRow baseline)
                    && baseline.Minute != row.Minute);
        }

        private static string[] ResolveClusterIdsForLine(List<PlannerRiskCluster> riskClusters, string lineId)
        {
            return (riskClusters ?? new List<PlannerRiskCluster>())
                .Where(cluster =>
                    string.Equals(cluster.YieldingLineId, lineId, StringComparison.Ordinal)
                    || string.Equals(cluster.PriorityLineId, lineId, StringComparison.Ordinal)
                    || string.Equals(cluster.LocalLineId, lineId, StringComparison.Ordinal)
                    || string.Equals(cluster.ExpressLineId, lineId, StringComparison.Ordinal))
                .Select(cluster => cluster.ClusterId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] ResolveRegionIdsForLine(List<PlannerRiskCluster> riskClusters, string lineId)
        {
            return ResolveClusterIdsForLine(riskClusters, lineId);
        }
    }
}
