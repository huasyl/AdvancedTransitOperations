using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerResultProjector
    {
        public DepartureControlSystem.DispatchPlannerResult Project(PlannerExecutionState state)
        {
            DepartureControlSystem.DispatchPlannerResult result = new DepartureControlSystem.DispatchPlannerResult();
            PlannerContext context = state.Context;
            List<PlannerPlanModel> projectedPlans = SelectPlansForFrontend(state.Plans);
            result.success = state.Diagnostics.All(issue => !string.Equals(issue.Level, "error", StringComparison.Ordinal));
            result.engineVersion = PlannerDefaults.EngineVersion;
            result.requestEcho = BuildRequestEcho(context);
            result.inputSummary = BuildInputSummary(context, state.RiskClusters);
            result.lineRoleSummary = BuildLineRoleSummary(context);
            result.defaultPlanId = projectedPlans.Count > 0 ? projectedPlans[0].PlanId : string.Empty;
            result.plans = projectedPlans.Select(plan => BuildPlanDetail(context, plan)).ToArray();
            result.planSummaries = projectedPlans.Select(BuildPlanSummary).ToArray();
            result.selectedPlan = result.plans.Length > 0 ? result.plans[0] : null;
            result.diagnostics = state.Diagnostics.Select(BuildDiagnostic).ToArray();
            result.performance = new DepartureControlSystem.DispatchPlannerPerformanceDto
            {
                engineMode = "backend-analysis",
                localLineCount = context.SelectedLocalLineIds.Length,
                expressLineCount = context.SelectedExpressLineIds.Length,
                pursuitTrunkCount = state.PursuitTrunks.Count,
                rawCatchupEventCount = state.CatchupEvents.Count,
                riskClusterCount = state.RiskClusters.Count,
                optimizationRegionCount = state.OptimizationRegions.Count
            };
            return result;
        }

        private static List<PlannerPlanModel> SelectPlansForFrontend(List<PlannerPlanModel> plans)
        {
            Dictionary<string, List<PlannerPlanModel>> plansByObjective = new Dictionary<string, List<PlannerPlanModel>>(StringComparer.Ordinal);
            foreach (PlannerPlanModel plan in plans ?? new List<PlannerPlanModel>())
            {
                if (plan == null || string.IsNullOrEmpty(plan.ObjectiveId))
                {
                    continue;
                }

                if (!plansByObjective.TryGetValue(plan.ObjectiveId, out List<PlannerPlanModel> objectivePlans))
                {
                    objectivePlans = new List<PlannerPlanModel>();
                    plansByObjective[plan.ObjectiveId] = objectivePlans;
                }

                objectivePlans.Add(plan);
            }

            List<PlannerPlanModel> selectedPlans = new List<PlannerPlanModel>();
            foreach (PlannerObjectiveDefinition objective in PlannerDefaults.Objectives)
            {
                if (!plansByObjective.TryGetValue(objective.Id, out List<PlannerPlanModel> objectivePlans)
                    || objectivePlans.Count == 0)
                {
                    continue;
                }

                objectivePlans.Sort(ComparePlansForObjective);
                selectedPlans.Add(objectivePlans[0]);
            }

            return selectedPlans;
        }

        private static int ComparePlansForObjective(PlannerPlanModel left, PlannerPlanModel right)
        {
            int infeasibleCompare = GetInfeasibleRank(left).CompareTo(GetInfeasibleRank(right));
            if (infeasibleCompare != 0)
            {
                return infeasibleCompare;
            }

            int unresolvedCompare = left.UnresolvedRiskMinutes.CompareTo(right.UnresolvedRiskMinutes);
            if (unresolvedCompare != 0)
            {
                return unresolvedCompare;
            }

            int robustnessCompare = left.RobustnessRiskMinutes.CompareTo(right.RobustnessRiskMinutes);
            if (robustnessCompare != 0)
            {
                return robustnessCompare;
            }

            int scoreCompare = right.Score.CompareTo(left.Score);
            if (scoreCompare != 0)
            {
                return scoreCompare;
            }

            int bypassCompare = left.AddedBypassStationCount.CompareTo(right.AddedBypassStationCount);
            if (bypassCompare != 0)
            {
                return bypassCompare;
            }

            int retimedCompare = left.RetimedTripCount.CompareTo(right.RetimedTripCount);
            if (retimedCompare != 0)
            {
                return retimedCompare;
            }

            return string.Compare(left.PlanId, right.PlanId, StringComparison.Ordinal);
        }

        private static int GetInfeasibleRank(PlannerPlanModel plan)
        {
            string status = plan?.Status ?? string.Empty;
            if (string.Equals(status, "feasible", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (string.Equals(status, "needsAction", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (string.Equals(status, "fragile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "risk", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            return string.Equals(status, "infeasible", StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        }

        private static string BuildPlanSignature(PlannerPlanModel plan)
        {
            if (plan == null)
            {
                return string.Empty;
            }

            if (plan.StructuredScheduleActions != null && plan.StructuredScheduleActions.Count > 0)
            {
                IEnumerable<string> actionTokens = plan.StructuredScheduleActions
                    .Select(action =>
                    {
                        string actionType = action?.actionType ?? action?.type ?? string.Empty;
                        string affectedLineIds = string.Join(",",
                            (action?.affectedLineIds ?? Array.Empty<string>())
                                .Where(lineId => !string.IsNullOrEmpty(lineId))
                                .OrderBy(lineId => lineId, StringComparer.Ordinal));
                        string stationIds = string.Join(",",
                            (action?.stationIds ?? Array.Empty<string>())
                                .Where(stationId => !string.IsNullOrEmpty(stationId))
                                .OrderBy(stationId => stationId, StringComparer.Ordinal));
                        string deltaPattern = string.Join(",",
                            (action?.deltaPattern ?? Array.Empty<float>())
                                .Select(delta => PlannerMath.Round2(delta).ToString("0.##", CultureInfo.InvariantCulture)));
                        return actionType
                            + "|line:" + affectedLineIds
                            + "|station:" + stationIds
                            + "|offset:" + PlannerMath.Round2(action?.deltaOffsetMinutes ?? 0f).ToString("0.##", CultureInfo.InvariantCulture)
                            + "|delta:" + PlannerMath.Round2(action?.deltaMinutes ?? 0f).ToString("0.##", CultureInfo.InvariantCulture)
                            + "|pattern:" + deltaPattern;
                    })
                    .OrderBy(token => token, StringComparer.Ordinal);
                return string.Join(";", actionTokens);
            }

            string bypassSignature = string.Join(",",
                (plan.SelectedBypassStationIds ?? new List<string>())
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .OrderBy(stationId => stationId, StringComparer.Ordinal));
            return "offset=" + plan.RecommendedExpressOffsetDeltaMinutes
                + "|status=" + (plan.Status ?? string.Empty)
                + "|bypass=" + bypassSignature
                + "|retimed=" + plan.RetimedTripCount;
        }

        private static DepartureControlSystem.DispatchPlannerRequestEchoDto BuildRequestEcho(PlannerContext context)
        {
            return new DepartureControlSystem.DispatchPlannerRequestEchoDto
            {
                draftKey = context.Request.draftKey,
                analysisWindowId = context.Request.analysisWindowId,
                windowStart = context.WindowStart,
                windowEnd = context.WindowEnd,
                localLineIds = context.SelectedLocalLineIds,
                adjustableLineIds = context.AdjustableLineIds,
                expressSourceMode = context.ExpressSourceMode,
                expressLineId = context.Request.expressLineId,
                virtualExpressBaseLineId = context.VirtualExpressBaseLineId,
                expressStopStationIds = context.SelectedExpressStopStationIds,
                departureMode = context.DepartureMode,
                expressTripsPerHour = context.Request.expressTripsPerHour,
                intervalMinutes = context.Request.intervalMinutes,
                phaseTime = context.Request.phaseTime,
                expressOffsetMinutes = context.Request.expressOffsetMinutes,
                maxOffsetMinutes = context.Request.maxOffsetMinutes,
                offsetStepMinutes = context.Request.offsetStepMinutes,
                maxLocalRetimeMinutes = context.Request.maxLocalRetimeMinutes,
                maxLocalWaitMinutes = context.Request.maxLocalWaitMinutes,
                maxAdditionalBypassStations = context.Request.maxAdditionalBypassStations,
                forcedBypassStationIds = context.ForcedBypassStationIds
            };
        }

        private static DepartureControlSystem.DispatchPlannerInputSummaryDto BuildInputSummary(
            PlannerContext context,
            List<PlannerRiskCluster> riskClusters)
        {
            int draftTripCount = (context.SelectedDraft?.trips?.Length ?? 0);
            return new DepartureControlSystem.DispatchPlannerInputSummaryDto
            {
                localLineIds = context.SelectedLocalLineIds ?? Array.Empty<string>(),
                expressSourceCode = context.ExpressSourceMode,
                expressBaseLineId = context.VirtualExpressBaseLineId ?? string.Empty,
                expressStopStationIds = context.SelectedExpressStopStationIds ?? Array.Empty<string>(),
                configuredBypassStationCount = context.Snapshot.configuredBypassStations?.Length ?? 0,
                candidateBypassStationCount = context.Snapshot.candidateBypassStations?.Length ?? 0,
                sharedCorridorCount = context.Snapshot.currentTrackScenario?.sharedCorridors?.Length ?? 0,
                draftTripCount = draftTripCount,
                effectiveLineIds = context.EffectiveLineIds ?? context.SelectedLineIds ?? Array.Empty<string>(),
                autoFixedConstraintLineIds = context.AutoFixedConstraintLineIds ?? Array.Empty<string>(),
                suppressedFixedVsFixedClusterCount = context.SuppressedFixedVsFixedClusterCount,
                primaryRiskClusterCount = (riskClusters ?? new List<PlannerRiskCluster>()).Count(cluster => cluster.IsPrimaryPlanningRisk)
            };
        }

        private static DepartureControlSystem.DispatchPlannerPlanSummaryDto BuildPlanSummary(PlannerPlanModel plan)
        {
            return new DepartureControlSystem.DispatchPlannerPlanSummaryDto
            {
                planId = plan.PlanId,
                objectiveId = plan.ObjectiveId,
                status = plan.Status,
                score = plan.Score,
                expressSavedMinutes = plan.ExpressSavedMinutes,
                localWaitMinutes = plan.LocalWaitMinutes,
                unresolvedRiskMinutes = plan.UnresolvedRiskMinutes,
                robustnessRiskMinutes = plan.RobustnessRiskMinutes,
                addedBypassStationCount = plan.AddedBypassStationCount,
                retimedTripCount = plan.RetimedTripCount,
                recommendedExpressOffsetDeltaMinutes = plan.RecommendedExpressOffsetDeltaMinutes
            };
        }

        private static DepartureControlSystem.DispatchPlannerPlanDetailDto BuildPlanDetail(PlannerContext context, PlannerPlanModel plan)
        {
            List<PlannerOptimizationRegion> regions = new OptimizationRegionBuilder().BuildOptimizationRegions(plan.RiskClusters ?? new List<PlannerRiskCluster>());
            return new DepartureControlSystem.DispatchPlannerPlanDetailDto
            {
                planId = plan.PlanId,
                objectiveId = plan.ObjectiveId,
                status = plan.Status,
                score = plan.Score,
                recommendedExpressOffsetDeltaMinutes = plan.RecommendedExpressOffsetDeltaMinutes,
                metrics = BuildPlanMetrics(plan),
                selectedBypassStationIds = plan.SelectedBypassStationIds.ToArray(),
                riskClusters = plan.RiskClusters.Select(cluster => BuildRiskCluster(context, plan, cluster)).ToArray(),
                riskItems = BuildRiskItems(context, plan),
                optimizationRegions = BuildOptimizationRegions(regions),
                structuredScheduleActions = plan.StructuredScheduleActions.ToArray(),
                problemIssues = plan.ProblemIssues.ToArray(),
                lineRoleSummary = BuildLineRoleSummary(context),
                frontendSummary = plan.FrontendSummary ?? BuildFallbackFrontendSummary(context, plan),
                timetablePreviewRows = plan.PreviewRows.ToArray(),
                plannerBaselineRows = BuildPlannerRows(plan.BaselineRows, "plannerBaseline"),
                plannerReplacementRows = BuildPlannerRows(plan.AdjustedRows, "plannerReplacement"),
                changedWindows = BuildChangedWindows(context, plan, regions),
                diagnostics = plan.Diagnostics.Select(BuildDiagnostic).ToArray()
            };
        }

        private static DepartureControlSystem.DispatchWorkbenchStagedRowDto[] BuildPlannerRows(
            IEnumerable<PlannerWorkingRow> rows,
            string source)
        {
            return (rows ?? Array.Empty<PlannerWorkingRow>())
                .Where(row => row != null && !string.IsNullOrEmpty(row.LineId))
                .OrderBy(row => row.LineId, StringComparer.Ordinal)
                .ThenBy(row => row.Minute)
                .ThenBy(row => row.Id, StringComparer.Ordinal)
                .Select((row, index) => new DepartureControlSystem.DispatchWorkbenchStagedRowDto
                {
                    id = string.IsNullOrEmpty(row.Id) ? source + "-" + (index + 1).ToString() : row.Id,
                    lineId = row.LineId,
                    time = PlannerMath.MinutesToTime(row.Minute),
                    kind = string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase) ? "express" : "local",
                    source = string.IsNullOrEmpty(row.Source) ? source : row.Source,
                    note = row.Note ?? string.Empty
                })
                .ToArray();
        }

        private static DepartureControlSystem.DispatchPlannerPlanMetricsDto BuildPlanMetrics(PlannerPlanModel plan)
        {
            return new DepartureControlSystem.DispatchPlannerPlanMetricsDto
            {
                expressSavedMinutes = plan.ExpressSavedMinutes,
                localWaitMinutes = plan.LocalWaitMinutes,
                unresolvedRiskMinutes = plan.UnresolvedRiskMinutes,
                robustnessRiskMinutes = plan.RobustnessRiskMinutes,
                addedBypassStationCount = plan.AddedBypassStationCount,
                retimedTripCount = plan.RetimedTripCount,
                recommendedExpressOffsetDeltaMinutes = plan.RecommendedExpressOffsetDeltaMinutes
            };
        }

        private static DepartureControlSystem.DispatchPlannerOptimizationRegionDto[] BuildOptimizationRegions(
            List<PlannerOptimizationRegion> regions)
        {
            return regions.Select(region => new DepartureControlSystem.DispatchPlannerOptimizationRegionDto
            {
                regionId = region.RegionId,
                clusterIds = region.ClusterIds.ToArray(),
                yieldingLineIds = region.YieldingLineIds.ToArray(),
                priorityLineIds = region.PriorityLineIds.ToArray(),
                eventCount = region.EventCount,
                firstCatchupMinute = region.FirstCatchupMinute,
                lastCatchupMinute = region.LastCatchupMinute,
                totalUnresolvedRiskMinutes = region.TotalUnresolvedRiskMinutes,
                totalRobustnessRiskMinutes = region.TotalRobustnessRiskMinutes
            }).ToArray();
        }

        private static DepartureControlSystem.DispatchPlannerChangedWindowDto[] BuildChangedWindows(
            PlannerContext context,
            PlannerPlanModel plan,
            List<PlannerOptimizationRegion> regions)
        {
            Dictionary<string, PlannerWorkingRow> baselineById = plan.BaselineRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            Dictionary<string, PlannerWorkingRow> adjustedById = plan.AdjustedRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            Dictionary<string, DepartureControlSystem.DispatchPlannerPreviewRowDto> previewByTripId = plan.PreviewRows.ToDictionary(row => row.tripId, StringComparer.Ordinal);

            List<DepartureControlSystem.DispatchPlannerChangedRowDto> rowDiffs = new List<DepartureControlSystem.DispatchPlannerChangedRowDto>();
            foreach (KeyValuePair<string, PlannerWorkingRow> entry in adjustedById)
            {
                PlannerWorkingRow adjustedRow = entry.Value;
                baselineById.TryGetValue(entry.Key, out PlannerWorkingRow baselineRow);
                previewByTripId.TryGetValue(entry.Key, out DepartureControlSystem.DispatchPlannerPreviewRowDto previewRow);
                int beforeMinute = baselineRow?.Minute ?? adjustedRow.Minute;
                int scheduleShiftMinutes = adjustedRow.Minute - beforeMinute;
                int predictedDelayMinutes = previewRow?.deltaMinutes ?? 0;
                int totalDeltaMinutes = scheduleShiftMinutes + predictedDelayMinutes;
                if (scheduleShiftMinutes == 0 && predictedDelayMinutes == 0)
                {
                    continue;
                }

                string changeType = scheduleShiftMinutes != 0
                    ? string.Equals(adjustedRow.Kind, "express", StringComparison.OrdinalIgnoreCase)
                        ? "expressOffset"
                        : "retime"
                    : "predictedHold";
                rowDiffs.Add(new DepartureControlSystem.DispatchPlannerChangedRowDto
                {
                    tripId = adjustedRow.Id,
                    lineId = adjustedRow.LineId,
                    kind = adjustedRow.Kind,
                    beforeTime = PlannerMath.MinutesToTime(beforeMinute),
                    afterTime = PlannerMath.MinutesToTime(adjustedRow.Minute + predictedDelayMinutes),
                    scheduleShiftMinutes = scheduleShiftMinutes,
                    predictedDelayMinutes = predictedDelayMinutes,
                    totalDeltaMinutes = totalDeltaMinutes,
                    changeType = changeType,
                    statusCode = previewRow?.statusCode ?? string.Empty,
                    statusMinutes = previewRow?.statusMinutes ?? 0
                });
            }

            if (rowDiffs.Count == 0)
            {
                return Array.Empty<DepartureControlSystem.DispatchPlannerChangedWindowDto>();
            }

            List<DepartureControlSystem.DispatchPlannerChangedWindowDto> windows = new List<DepartureControlSystem.DispatchPlannerChangedWindowDto>();
            List<DepartureControlSystem.DispatchPlannerChangedRowDto> ordered = rowDiffs
                .OrderBy(row => PlannerMath.TimeToMinutes(row.beforeTime) ?? 0)
                .ThenBy(row => row.lineId, StringComparer.Ordinal)
                .ToList();

            List<DepartureControlSystem.DispatchPlannerChangedRowDto> currentRows = new List<DepartureControlSystem.DispatchPlannerChangedRowDto>();
            int previousMinute = -1000;
            for (int i = 0; i < ordered.Count; i++)
            {
                DepartureControlSystem.DispatchPlannerChangedRowDto row = ordered[i];
                int rowMinute = PlannerMath.TimeToMinutes(row.beforeTime) ?? 0;
                bool startsNewWindow = currentRows.Count == 0 || rowMinute - previousMinute > 30;
                if (startsNewWindow && currentRows.Count > 0)
                {
                    windows.Add(BuildChangedWindow(context, currentRows, regions, windows.Count));
                    currentRows = new List<DepartureControlSystem.DispatchPlannerChangedRowDto>();
                }

                currentRows.Add(row);
                previousMinute = rowMinute;
            }

            if (currentRows.Count > 0)
            {
                windows.Add(BuildChangedWindow(context, currentRows, regions, windows.Count));
            }

            return windows.ToArray();
        }

        private static DepartureControlSystem.DispatchPlannerChangedWindowDto BuildChangedWindow(
            PlannerContext context,
            List<DepartureControlSystem.DispatchPlannerChangedRowDto> rows,
            List<PlannerOptimizationRegion> regions,
            int windowIndex)
        {
            string[] lineIds = rows.Select(row => row.lineId).Where(lineId => !string.IsNullOrEmpty(lineId)).Distinct(StringComparer.Ordinal).ToArray();
            string[] lineNames = lineIds.Select(lineId => ResolveLineName(context, lineId)).ToArray();
            string[] changeTypes = rows.Select(row => row.changeType).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
            PlannerOptimizationRegion region = regions.FirstOrDefault(candidate =>
                candidate.YieldingLineIds.Intersect(lineIds, StringComparer.Ordinal).Any()
                || candidate.PriorityLineIds.Intersect(lineIds, StringComparer.Ordinal).Any());
            int fromMinute = rows.Select(row => PlannerMath.TimeToMinutes(row.beforeTime) ?? 0).DefaultIfEmpty(0).Min();
            int toMinute = rows.Select(row => PlannerMath.TimeToMinutes(row.afterTime) ?? 0).DefaultIfEmpty(fromMinute).Max();
            return new DepartureControlSystem.DispatchPlannerChangedWindowDto
            {
                windowId = "window-" + windowIndex,
                regionId = region?.RegionId ?? string.Empty,
                lineIds = lineIds,
                lineNames = lineNames,
                fromTime = PlannerMath.MinutesToTime(fromMinute),
                toTime = PlannerMath.MinutesToTime(toMinute),
                changeTypes = changeTypes,
                rowDiffs = rows.ToArray()
            };
        }

        private static DepartureControlSystem.DispatchPlannerLineRoleSummaryDto BuildLineRoleSummary(PlannerContext context)
        {
            string[] effectiveLineIds = context.EffectiveLineIds ?? context.SelectedLineIds ?? new string[0];
            HashSet<string> adjustable = new HashSet<string>(context.AdjustableLineIds ?? new string[0], StringComparer.Ordinal);
            HashSet<string> fixedLines = new HashSet<string>(context.FixedLineIds ?? new string[0], StringComparer.Ordinal);
            HashSet<string> targets = new HashSet<string>(context.TargetLineIds ?? new string[0], StringComparer.Ordinal);
            return new DepartureControlSystem.DispatchPlannerLineRoleSummaryDto
            {
                effectiveLineIds = effectiveLineIds,
                adjustableLineIds = context.AdjustableLineIds ?? new string[0],
                fixedLineIds = context.FixedLineIds ?? new string[0],
                targetLineIds = context.TargetLineIds ?? new string[0],
                autoFixedConstraintLineIds = context.AutoFixedConstraintLineIds ?? new string[0],
                suppressedFixedVsFixedClusterCount = context.SuppressedFixedVsFixedClusterCount,
                roles = effectiveLineIds.Select(lineId => new DepartureControlSystem.DispatchPlannerLineRoleDto
                {
                    lineId = lineId,
                    participates = true,
                    adjustable = adjustable.Contains(lineId),
                    fixedLine = fixedLines.Contains(lineId),
                    target = targets.Contains(lineId)
                }).ToArray()
            };
        }

        private static DepartureControlSystem.DispatchPlannerFrontendSummaryDto BuildFallbackFrontendSummary(
            PlannerContext context,
            PlannerPlanModel plan)
        {
            return new DepartureControlSystem.DispatchPlannerFrontendSummaryDto
            {
                effectiveLineIds = context.EffectiveLineIds ?? context.SelectedLineIds ?? new string[0],
                adjustableLineIds = context.AdjustableLineIds ?? new string[0],
                fixedLineIds = context.FixedLineIds ?? new string[0],
                targetLineIds = context.TargetLineIds ?? new string[0],
                actuallyAdjustedLineIds = new string[0],
                issueCountsByType = new DepartureControlSystem.DispatchPlannerIssueCountDto[0],
                actionCount = plan.StructuredScheduleActions.Count,
                catchupClusterCount = plan.RiskClusters.Count,
                unresolvedRiskMinutes = plan.UnresolvedRiskMinutes,
                robustnessRiskMinutes = plan.RobustnessRiskMinutes
            };
        }

        private static DepartureControlSystem.DispatchPlannerRiskClusterDto BuildRiskCluster(
            PlannerContext context,
            PlannerPlanModel plan,
            PlannerRiskCluster cluster)
        {
            return new DepartureControlSystem.DispatchPlannerRiskClusterDto
            {
                clusterId = cluster.ClusterId,
                severityLevel = cluster.UnresolvedRiskMinutes > 0f ? "high" : cluster.RobustnessRiskMinutes > 0f ? "fragile" : "ok",
                yieldingLineId = string.IsNullOrEmpty(cluster.YieldingLineId) ? cluster.LocalLineId : cluster.YieldingLineId,
                priorityLineId = string.IsNullOrEmpty(cluster.PriorityLineId) ? cluster.ExpressLineId : cluster.PriorityLineId,
                fromStationId = cluster.FromStationId,
                toStationId = cluster.ToStationId,
                catchupCount = cluster.CatchupCount,
                maxSeverityMinutes = cluster.MaxSeverityMinutes,
                unresolvedRiskMinutes = cluster.UnresolvedRiskMinutes,
                robustnessRiskMinutes = cluster.RobustnessRiskMinutes,
                recommendedBypassStationId = cluster.RecommendedBypassStation?.StationId ?? string.Empty,
                recommendedActionCodes = cluster.RecommendedActionCodes.ToArray(),
                representativeEvents = BuildRiskEvents(plan, cluster)
            };
        }

        private static DepartureControlSystem.DispatchPlannerRiskEventDto[] BuildRiskEvents(
            PlannerPlanModel plan,
            PlannerRiskCluster cluster)
        {
            if (plan == null || cluster == null)
            {
                return Array.Empty<DepartureControlSystem.DispatchPlannerRiskEventDto>();
            }

            HashSet<string> catchupIds = new HashSet<string>(cluster.CatchupIds ?? new List<string>(), StringComparer.Ordinal);
            Dictionary<string, PlannerWorkingRow> adjustedRowsById = (plan.AdjustedRows ?? new List<PlannerWorkingRow>())
                .Where(row => row != null && !string.IsNullOrEmpty(row.Id))
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return (plan.CatchupEvents ?? new List<PlannerCatchupEvent>())
                .Where(item => item != null && catchupIds.Contains(item.EventId))
                .OrderByDescending(item => item.UnresolvedRiskMinutes)
                .ThenByDescending(item => item.RobustnessRiskMinutes)
                .ThenByDescending(item => item.SeverityMinutes)
                .Take(3)
                .Select(item => BuildRiskEvent(item, adjustedRowsById))
                .ToArray();
        }

        private static DepartureControlSystem.DispatchPlannerRiskItemDto[] BuildRiskItems(
            PlannerContext context,
            PlannerPlanModel plan)
        {
            Dictionary<string, PlannerWorkingRow> adjustedRowsById = (plan.AdjustedRows ?? new List<PlannerWorkingRow>())
                .Where(row => row != null && !string.IsNullOrEmpty(row.Id))
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return (plan.CatchupEvents ?? new List<PlannerCatchupEvent>())
                .Where(item =>
                    item != null
                    && !string.Equals(item.PairRole, "fixed-fixed", StringComparison.Ordinal)
                    && (item.UnresolvedRiskMinutes > 0f
                        || item.RobustnessRiskMinutes > 0f
                        || !string.Equals(item.ResolutionState, "resolved", StringComparison.Ordinal)))
                .OrderBy(item => GetRiskItemRoleRank(item.PairRole))
                .ThenByDescending(item => item.UnresolvedRiskMinutes)
                .ThenByDescending(item => item.RobustnessRiskMinutes)
                .ThenBy(item => item.CatchupMinute)
                .Take(16)
                .Select(item => BuildRiskItem(context, item, adjustedRowsById))
                .ToArray();
        }

        private static int GetRiskItemRoleRank(string pairRole)
        {
            if (string.Equals(pairRole, "target-adjustable", StringComparison.Ordinal))
            {
                return 0;
            }
            if (string.Equals(pairRole, "target-fixed", StringComparison.Ordinal))
            {
                return 1;
            }
            if (string.Equals(pairRole, "adjustable-fixed", StringComparison.Ordinal))
            {
                return 2;
            }
            return 3;
        }

        private static DepartureControlSystem.DispatchPlannerRiskItemDto BuildRiskItem(
            PlannerContext context,
            PlannerCatchupEvent catchupEvent,
            Dictionary<string, PlannerWorkingRow> adjustedRowsById)
        {
            string yieldingTripId = string.IsNullOrEmpty(catchupEvent.YieldingTripId) ? catchupEvent.LocalTripId : catchupEvent.YieldingTripId;
            string priorityTripId = string.IsNullOrEmpty(catchupEvent.PriorityTripId) ? catchupEvent.ExpressTripId : catchupEvent.PriorityTripId;
            return new DepartureControlSystem.DispatchPlannerRiskItemDto
            {
                riskId = catchupEvent.EventId,
                problemType = string.IsNullOrEmpty(catchupEvent.ProblemType) ? ResolveProblemTypeFallback(catchupEvent) : catchupEvent.ProblemType,
                resolutionState = string.IsNullOrEmpty(catchupEvent.ResolutionState) ? ResolveRiskEventStatus(catchupEvent) : catchupEvent.ResolutionState,
                pairRole = catchupEvent.PairRole ?? string.Empty,
                treatmentType = catchupEvent.TreatmentType ?? string.Empty,
                blockReasonCode = catchupEvent.BlockReasonCode ?? string.Empty,
                suggestedOptionCodes = catchupEvent.SuggestedOptionCodes ?? Array.Empty<string>(),
                yieldingLineId = string.IsNullOrEmpty(catchupEvent.YieldingLineId) ? catchupEvent.LocalLineId : catchupEvent.YieldingLineId,
                priorityLineId = string.IsNullOrEmpty(catchupEvent.PriorityLineId) ? catchupEvent.ExpressLineId : catchupEvent.PriorityLineId,
                yieldingTripId = yieldingTripId,
                priorityTripId = priorityTripId,
                yieldingDepartTime = ResolveTripDepartTime(adjustedRowsById, yieldingTripId),
                priorityDepartTime = ResolveTripDepartTime(adjustedRowsById, priorityTripId),
                fromStationId = catchupEvent.FromStationId,
                toStationId = catchupEvent.ToStationId,
                catchupFromStationId = catchupEvent.CatchupFromStationId,
                catchupToStationId = catchupEvent.CatchupToStationId,
                catchupTime = PlannerMath.MinutesToTime((int)Math.Round(catchupEvent.CatchupMinute)),
                selectedBypassStationId = catchupEvent.SelectedBypassStation?.StationId ?? string.Empty,
                requiredHoldMinutes = catchupEvent.RequiredHoldMinutes,
                plannedAdjustmentMinutes = catchupEvent.ResolvedHoldMinutes,
                holdBudgetMinutes = catchupEvent.HoldBudgetMinutes,
                unresolvedRiskMinutes = catchupEvent.UnresolvedRiskMinutes,
                robustnessRiskMinutes = catchupEvent.RobustnessRiskMinutes,
                requiredMarginMinutes = catchupEvent.RequiredMarginMinutes,
                currentWorstCaseGapMinutes = catchupEvent.CurrentWorstCaseGapMinutes
            };
        }

        private static string ResolveProblemTypeFallback(PlannerCatchupEvent catchupEvent)
        {
            if (!string.Equals(catchupEvent.PairRole, "target-adjustable", StringComparison.Ordinal))
            {
                return "backgroundConstraint";
            }
            return catchupEvent.RequiredHoldMinutes > 0f || catchupEvent.DidCatchUp
                ? "hardCatchup"
                : "lowMargin";
        }

        private static DepartureControlSystem.DispatchPlannerRiskEventDto BuildRiskEvent(
            PlannerCatchupEvent catchupEvent,
            Dictionary<string, PlannerWorkingRow> adjustedRowsById)
        {
            string yieldingTripId = string.IsNullOrEmpty(catchupEvent.YieldingTripId) ? catchupEvent.LocalTripId : catchupEvent.YieldingTripId;
            string priorityTripId = string.IsNullOrEmpty(catchupEvent.PriorityTripId) ? catchupEvent.ExpressTripId : catchupEvent.PriorityTripId;
            return new DepartureControlSystem.DispatchPlannerRiskEventDto
            {
                eventId = catchupEvent.EventId,
                statusCode = ResolveRiskEventStatus(catchupEvent),
                reasonCode = ResolveRiskEventReason(catchupEvent),
                problemType = string.IsNullOrEmpty(catchupEvent.ProblemType) ? ResolveProblemTypeFallback(catchupEvent) : catchupEvent.ProblemType,
                resolutionState = string.IsNullOrEmpty(catchupEvent.ResolutionState) ? ResolveRiskEventStatus(catchupEvent) : catchupEvent.ResolutionState,
                pairRole = catchupEvent.PairRole ?? string.Empty,
                treatmentType = catchupEvent.TreatmentType ?? string.Empty,
                blockReasonCode = catchupEvent.BlockReasonCode ?? string.Empty,
                suggestedOptionCodes = catchupEvent.SuggestedOptionCodes ?? Array.Empty<string>(),
                yieldingLineId = string.IsNullOrEmpty(catchupEvent.YieldingLineId) ? catchupEvent.LocalLineId : catchupEvent.YieldingLineId,
                priorityLineId = string.IsNullOrEmpty(catchupEvent.PriorityLineId) ? catchupEvent.ExpressLineId : catchupEvent.PriorityLineId,
                yieldingTripId = yieldingTripId,
                priorityTripId = priorityTripId,
                yieldingDepartTime = ResolveTripDepartTime(adjustedRowsById, yieldingTripId),
                priorityDepartTime = ResolveTripDepartTime(adjustedRowsById, priorityTripId),
                fromStationId = catchupEvent.FromStationId,
                toStationId = catchupEvent.ToStationId,
                catchupFromStationId = catchupEvent.CatchupFromStationId,
                catchupToStationId = catchupEvent.CatchupToStationId,
                catchupTime = PlannerMath.MinutesToTime((int)Math.Round(catchupEvent.CatchupMinute)),
                requiredHoldMinutes = catchupEvent.RequiredHoldMinutes,
                plannedAdjustmentMinutes = catchupEvent.ResolvedHoldMinutes,
                holdBudgetMinutes = catchupEvent.HoldBudgetMinutes,
                unresolvedRiskMinutes = catchupEvent.UnresolvedRiskMinutes,
                robustnessRiskMinutes = catchupEvent.RobustnessRiskMinutes,
                selectedBypassStationId = catchupEvent.SelectedBypassStation?.StationId ?? string.Empty,
                requiredMarginMinutes = catchupEvent.RequiredMarginMinutes,
                currentWorstCaseGapMinutes = catchupEvent.CurrentWorstCaseGapMinutes
            };
        }

        private static string ResolveTripDepartTime(
            Dictionary<string, PlannerWorkingRow> adjustedRowsById,
            string tripId)
        {
            if (string.IsNullOrEmpty(tripId)
                || adjustedRowsById == null
                || !adjustedRowsById.TryGetValue(tripId, out PlannerWorkingRow row))
            {
                return string.Empty;
            }

            return PlannerMath.MinutesToTime(row.Minute);
        }

        private static string ResolveRiskEventStatus(PlannerCatchupEvent catchupEvent)
        {
            if (catchupEvent.UnresolvedRiskMinutes > 0f)
            {
                return "unresolved";
            }

            if (catchupEvent.RobustnessRiskMinutes > 0f)
            {
                return "fragile";
            }

            return "handled";
        }

        private static string ResolveRiskEventReason(PlannerCatchupEvent catchupEvent)
        {
            if (catchupEvent.UnresolvedRiskMinutes > 0f)
            {
                return catchupEvent.RequiredHoldMinutes > catchupEvent.HoldBudgetMinutes
                    ? "waitLimitExceeded"
                    : "unresolvedConflict";
            }

            if (catchupEvent.RobustnessRiskMinutes > 0f)
            {
                return "lowMargin";
            }

            return "handled";
        }

        private static DepartureControlSystem.DispatchPlannerDiagnosticDto BuildDiagnostic(PlannerValidationIssue issue)
        {
            return new DepartureControlSystem.DispatchPlannerDiagnosticDto
            {
                level = issue.Level,
                code = issue.Code,
                relatedClusterIds = issue.RelatedClusterIds ?? Array.Empty<string>(),
                lineIds = issue.LineIds ?? Array.Empty<string>(),
                stationIds = issue.StationIds ?? Array.Empty<string>(),
                tripIds = issue.TripIds ?? Array.Empty<string>(),
                minutesA = issue.MinutesA,
                minutesB = issue.MinutesB,
                countA = issue.CountA
            };
        }

        private static string ResolveLineName(PlannerContext context, string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
            {
                return string.Empty;
            }

            if (string.Equals(lineId, context.VirtualExpressLineId, StringComparison.Ordinal))
            {
                return "虚拟快车";
            }

            return context.LinesById.TryGetValue(lineId, out DepartureControlSystem.DispatchPlannerLineDto line)
                ? line.name ?? lineId
                : lineId;
        }

    }
}
