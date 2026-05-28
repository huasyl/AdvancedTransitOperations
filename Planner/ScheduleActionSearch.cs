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
                plan.ExpressSavedMinutes = ComputeExpressSavedMinutes(context, runtimeCatalog);
                plan.LocalWaitMinutes = ComputeLocalWaitMinutes(catchupEvents);
                plan.UnresolvedRiskMinutes = PlannerMath.Round2(riskClusters.Sum(cluster => cluster.UnresolvedRiskMinutes));
                plan.RobustnessRiskMinutes = PlannerMath.Round2(riskClusters.Sum(cluster => cluster.RobustnessRiskMinutes));
                plan.AddedBypassStationCount = (activeVirtualBypassStationIds ?? new string[0])
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                plan.RetimedTripCount = CountRetimedTrips(baseWorkingRows, context.WorkingRows, context, activeExpressOffsetMinutes);
                plan.StructuredScheduleActions = BuildStructuredScheduleActions(
                    context,
                    riskClusters,
                    catchupEvents,
                    activeVirtualBypassStationIds,
                    activeExpressOffsetMinutes,
                    baseWorkingRows,
                    context.WorkingRows);
                plan.ProblemIssues = BuildProblemIssues(context, plan, riskClusters, catchupEvents);
                plan.ProblemIssues.AddRange(BuildOriginDepartureGapIssues(plan.AdjustedRows, runtimeCatalog));
                plan.FrontendSummary = BuildFrontendSummary(context, plan);
                plan.PreviewRows = BuildPreviewRows(context, catchupEvents, runtimeCatalog);
                plan.Status = ResolvePlanStatus(plan, diagnostics);
                plans.Add(plan);
            }

            return plans;
        }

        private static float ComputeExpressSavedMinutes(
            PlannerContext context,
            PlannerRuntimeCatalog runtimeCatalog)
        {
            float totalSavedMinutes = 0f;
            int targetCount = 0;
            foreach (string targetLineId in (context?.TargetLineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal))
            {
                if (runtimeCatalog == null
                    || !runtimeCatalog.ModelsByLineId.TryGetValue(targetLineId, out PlannerLineRuntimeModel expressModel))
                {
                    continue;
                }

                float currentRuntimeMinutes = ResolveLineRuntimeMinutes(expressModel);
                float baselineRuntimeMinutes = currentRuntimeMinutes + ComputeFullStopBaselineExtraMinutes(context, expressModel);
                totalSavedMinutes += Math.Max(0f, baselineRuntimeMinutes - currentRuntimeMinutes);
                targetCount += 1;
            }

            return targetCount == 0
                ? 0f
                : PlannerMath.Round2(totalSavedMinutes / targetCount);
        }

        private static float ResolveLineRuntimeMinutes(PlannerLineRuntimeModel model)
        {
            if (model == null)
            {
                return 0f;
            }

            if (model.TotalMinuteSpan > 0f)
            {
                return model.TotalMinuteSpan;
            }

            if (model.AtomBoundaryMinuteOffsets == null || model.AtomBoundaryMinuteOffsets.Length == 0)
            {
                return 0f;
            }

            return Math.Max(0f, model.AtomBoundaryMinuteOffsets[model.AtomBoundaryMinuteOffsets.Length - 1]);
        }

        private static float ComputeFullStopBaselineExtraMinutes(
            PlannerContext context,
            PlannerLineRuntimeModel expressModel)
        {
            if (context == null || expressModel == null)
            {
                return 0f;
            }

            HashSet<int> targetBuildings = new HashSet<int>();
            foreach (DispatchPlannerStationDto station in expressModel.Stations)
            {
                if (station != null && station.buildingEntityIndex >= 0)
                {
                    targetBuildings.Add(station.buildingEntityIndex);
                }
            }

            float extraMinutes = 0f;
            foreach (PlannerStationOffset stationOffset in expressModel.StationOffsets)
            {
                if (stationOffset == null || stationOffset.ShouldStop)
                {
                    continue;
                }

                extraMinutes += ResolveAverageDwellForStation(context, stationOffset.StationId)
                    + PlannerDefaults.StopStartLossMinutesPerSkippedStop;
            }

            foreach (KeyValuePair<int, int> stop in CollectProjectedBaselineStops(context, expressModel, targetBuildings))
            {
                extraMinutes += ResolveAverageDwellForBuilding(context, stop.Key)
                    + PlannerDefaults.StopStartLossMinutesPerSkippedStop;
            }

            return PlannerMath.Round2(extraMinutes);
        }

        private static Dictionary<int, int> CollectProjectedBaselineStops(
            PlannerContext context,
            PlannerLineRuntimeModel expressModel,
            HashSet<int> targetBuildings)
        {
            Dictionary<int, int> projectedStopsByBuilding = new Dictionary<int, int>();
            if (context == null
                || expressModel == null
                || expressModel.Stations.Count < 2
                || string.IsNullOrEmpty(expressModel.SourceLineId))
            {
                return projectedStopsByBuilding;
            }

            int pathStartAtom = expressModel.Stations[0].trackAtomIndex;
            int pathEndAtom = expressModel.Stations[expressModel.Stations.Count - 1].trackAtomIndex;
            if (pathStartAtom < 0 || pathEndAtom <= pathStartAtom)
            {
                pathStartAtom = 0;
                pathEndAtom = Math.Max(0, expressModel.TrackAtomCount);
            }

            foreach (DispatchPlannerSharedCorridorDto corridor in context.Snapshot.currentTrackScenario?.sharedCorridors
                ?? Array.Empty<DispatchPlannerSharedCorridorDto>())
            {
                if (corridor == null
                    || !string.Equals(corridor.traversalRelation, "SameDirection", StringComparison.OrdinalIgnoreCase)
                    || corridor.hasMirroredContext
                    || corridor.orderedRun <= 0
                    || corridor.physicalOverlap <= 0)
                {
                    continue;
                }

                string sourceLineId;
                int targetStartAtom;
                int targetEndAtom;
                int sourceStartAtom;
                int sourceEndAtom;
                if (string.Equals(corridor.lineId, expressModel.SourceLineId, StringComparison.Ordinal))
                {
                    sourceLineId = corridor.otherLineId ?? string.Empty;
                    targetStartAtom = corridor.lineStartAtomIndex;
                    targetEndAtom = corridor.lineEndAtomIndexExclusive;
                    sourceStartAtom = corridor.otherStartAtomIndex;
                    sourceEndAtom = corridor.otherEndAtomIndexExclusive;
                }
                else if (string.Equals(corridor.otherLineId, expressModel.SourceLineId, StringComparison.Ordinal))
                {
                    sourceLineId = corridor.lineId ?? string.Empty;
                    targetStartAtom = corridor.otherStartAtomIndex;
                    targetEndAtom = corridor.otherEndAtomIndexExclusive;
                    sourceStartAtom = corridor.lineStartAtomIndex;
                    sourceEndAtom = corridor.lineEndAtomIndexExclusive;
                }
                else
                {
                    continue;
                }

                AddProjectedBaselineStopsFromSource(
                    context,
                    sourceLineId,
                    targetStartAtom,
                    targetEndAtom,
                    sourceStartAtom,
                    sourceEndAtom,
                    pathStartAtom,
                    pathEndAtom,
                    targetBuildings,
                    projectedStopsByBuilding);
            }

            return projectedStopsByBuilding;
        }

        private static void AddProjectedBaselineStopsFromSource(
            PlannerContext context,
            string sourceLineId,
            int targetStartAtomIndex,
            int targetEndAtomIndexExclusive,
            int sourceStartAtomIndex,
            int sourceEndAtomIndexExclusive,
            int pathStartAtom,
            int pathEndAtom,
            HashSet<int> targetBuildings,
            Dictionary<int, int> projectedStopsByBuilding)
        {
            if (string.IsNullOrEmpty(sourceLineId)
                || !context.StationsByLineId.TryGetValue(sourceLineId, out List<DispatchPlannerStationDto> sourceStations))
            {
                return;
            }

            int sourceStart = Math.Min(sourceStartAtomIndex, sourceEndAtomIndexExclusive);
            int sourceEnd = Math.Max(sourceStartAtomIndex, sourceEndAtomIndexExclusive);
            int targetStart = Math.Min(targetStartAtomIndex, targetEndAtomIndexExclusive);
            int targetEnd = Math.Max(targetStartAtomIndex, targetEndAtomIndexExclusive);
            int tolerance = PlannerDefaults.BypassStationEndpointToleranceAtoms;
            foreach (DispatchPlannerStationDto sourceStation in sourceStations)
            {
                if (sourceStation == null
                    || sourceStation.buildingEntityIndex < 0
                    || !sourceStation.canConfigureBypass
                    || targetBuildings.Contains(sourceStation.buildingEntityIndex)
                    || sourceStation.trackAtomIndex < sourceStart - tolerance
                    || sourceStation.trackAtomIndex > sourceEnd + tolerance)
                {
                    continue;
                }

                int projectedAtomIndex = ProjectSourceAtomToTargetCorridor(
                    sourceStation.trackAtomIndex,
                    sourceStart,
                    sourceEnd,
                    targetStart,
                    targetEnd);
                if (projectedAtomIndex <= pathStartAtom || projectedAtomIndex >= pathEndAtom)
                {
                    continue;
                }

                if (!projectedStopsByBuilding.ContainsKey(sourceStation.buildingEntityIndex))
                {
                    projectedStopsByBuilding[sourceStation.buildingEntityIndex] = projectedAtomIndex;
                }
            }
        }

        private static int ProjectSourceAtomToTargetCorridor(
            int sourceAtomIndex,
            int sourceStartAtomIndex,
            int sourceEndAtomIndexExclusive,
            int targetStartAtomIndex,
            int targetEndAtomIndexExclusive)
        {
            int sourceLength = Math.Max(1, sourceEndAtomIndexExclusive - sourceStartAtomIndex);
            int targetLength = Math.Max(1, targetEndAtomIndexExclusive - targetStartAtomIndex);
            float ratio = Math.Max(0f, Math.Min(1f, (sourceAtomIndex - sourceStartAtomIndex) / (float)sourceLength));
            return targetStartAtomIndex + (int)Math.Round(targetLength * ratio);
        }

        private static float ResolveAverageDwellForStation(PlannerContext context, string stationId)
        {
            if (context == null
                || string.IsNullOrEmpty(stationId)
                || !context.StationsById.TryGetValue(stationId, out DispatchPlannerStationDto station))
            {
                return 0f;
            }

            if (context.StopDwellByStationId.TryGetValue(station.id ?? string.Empty, out DispatchPlannerStationDwellObservationDto observed)
                && observed.sampleCount > 0
                && observed.averageMinutes > 0f)
            {
                return PlannerMath.Round2(observed.averageMinutes);
            }

            if (station.profileDwellMinutes > 0f)
            {
                return PlannerMath.Round2(station.profileDwellMinutes);
            }

            return ResolveAverageDwellForBuilding(context, station.buildingEntityIndex);
        }

        private static float ResolveAverageDwellForBuilding(PlannerContext context, int buildingEntityIndex)
        {
            if (context == null || buildingEntityIndex < 0)
            {
                return 0f;
            }

            List<float> observedDwellMinutes = new List<float>();
            List<float> profileDwellMinutes = new List<float>();
            foreach (DispatchPlannerStationDto station in context.StationsById.Values)
            {
                if (station == null || station.buildingEntityIndex != buildingEntityIndex)
                {
                    continue;
                }

                if (context.StopDwellByStationId.TryGetValue(station.id ?? string.Empty, out DispatchPlannerStationDwellObservationDto observed)
                    && observed.sampleCount > 0
                    && observed.averageMinutes > 0f)
                {
                    observedDwellMinutes.Add(observed.averageMinutes);
                }
                else if (station.profileDwellMinutes > 0f)
                {
                    profileDwellMinutes.Add(station.profileDwellMinutes);
                }
            }

            if (observedDwellMinutes.Count > 0)
            {
                return PlannerMath.Round2(observedDwellMinutes.Sum() / observedDwellMinutes.Count);
            }

            return profileDwellMinutes.Count > 0
                ? PlannerMath.Round2(profileDwellMinutes.Sum() / profileDwellMinutes.Count)
                : 0f;
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

        private static List<DispatchPlannerProblemIssueDto> BuildProblemIssues(
            PlannerContext context,
            PlannerPlanModel plan,
            List<PlannerRiskCluster> riskClusters,
            List<PlannerCatchupEvent> catchupEvents)
        {
            List<DispatchPlannerProblemIssueDto> issues = new List<DispatchPlannerProblemIssueDto>();
            foreach (PlannerRiskCluster cluster in riskClusters.Where(item => item.UnresolvedRiskMinutes > 0f).Take(8))
            {
                issues.Add(new DispatchPlannerProblemIssueDto
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
                issues.Add(new DispatchPlannerProblemIssueDto
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
                issues.Add(new DispatchPlannerProblemIssueDto
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
                issues.Add(new DispatchPlannerProblemIssueDto
                {
                    type = "fixedLineAffected",
                    severity = "high",
                    lineIds = fixedAffectedLineIds
                });
            }

            return issues;
        }

        private static List<DispatchPlannerProblemIssueDto> BuildOriginDepartureGapIssues(
            List<PlannerWorkingRow> rows,
            PlannerRuntimeCatalog runtimeCatalog)
        {
            List<DispatchPlannerProblemIssueDto> issues =
                new List<DispatchPlannerProblemIssueDto>();
            Dictionary<string, List<PlannerOriginDeparture>> departuresByOrigin =
                new Dictionary<string, List<PlannerOriginDeparture>>(StringComparer.Ordinal);

            foreach (PlannerWorkingRow row in rows ?? new List<PlannerWorkingRow>())
            {
                if (row == null
                    || string.IsNullOrEmpty(row.LineId)
                    || runtimeCatalog == null
                    || !runtimeCatalog.ModelsByLineId.TryGetValue(row.LineId, out PlannerLineRuntimeModel model))
                {
                    continue;
                }

                string originStationId = model.Line?.originStationId ?? string.Empty;
                if (string.IsNullOrEmpty(originStationId))
                    continue;

                if (!departuresByOrigin.TryGetValue(originStationId, out List<PlannerOriginDeparture> departures))
                {
                    departures = new List<PlannerOriginDeparture>();
                    departuresByOrigin[originStationId] = departures;
                }

                departures.Add(new PlannerOriginDeparture
                {
                    LineId = row.LineId,
                    TripId = row.Id ?? string.Empty,
                    OriginStationId = originStationId,
                    Minute = row.Minute
                });
            }

            foreach (List<PlannerOriginDeparture> departures in departuresByOrigin.Values)
            {
                PlannerOriginDeparture[] ordered = departures
                    .OrderBy(item => item.Minute)
                    .ThenBy(item => item.LineId, StringComparer.Ordinal)
                    .ThenBy(item => item.TripId, StringComparer.Ordinal)
                    .ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    AddOriginDepartureGapIssue(issues, ordered[i - 1], ordered[i]);
                }
                if (ordered.Length > 1 && ordered[0].Minute != ordered[ordered.Length - 1].Minute)
                {
                    AddOriginDepartureGapIssue(issues, ordered[ordered.Length - 1], ordered[0]);
                }
            }

            return issues;
        }

        private static void AddOriginDepartureGapIssue(
            List<DispatchPlannerProblemIssueDto> issues,
            PlannerOriginDeparture previous,
            PlannerOriginDeparture next)
        {
            int gap = GetForwardMinuteGap(previous.Minute, next.Minute);
            if (gap >= PlannerDefaults.DefaultMinDepartureGapMinutes)
                return;

            issues.Add(new DispatchPlannerProblemIssueDto
            {
                type = "originDepartureGap",
                severity = "high",
                yieldingLineId = next.LineId,
                priorityLineId = previous.LineId,
                yieldingTripId = next.TripId,
                priorityTripId = previous.TripId,
                severityMinutes = PlannerDefaults.DefaultMinDepartureGapMinutes - gap,
                riskMinutes = PlannerDefaults.DefaultMinDepartureGapMinutes - gap,
                lineIds = new[] { previous.LineId, next.LineId }
            });
        }

        private static int GetForwardMinuteGap(int previousMinutes, int nextMinutes)
        {
            const int dayMinutes = 24 * 60;
            int previous = ((previousMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            int next = ((nextMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            return next >= previous
                ? next - previous
                : dayMinutes - previous + next;
        }

        private static List<DispatchPlannerScheduleActionDto> BuildStructuredScheduleActions(
            PlannerContext context,
            List<PlannerRiskCluster> riskClusters,
            List<PlannerCatchupEvent> catchupEvents,
            string[] activeVirtualBypassStationIds,
            int activeExpressOffsetMinutes,
            List<PlannerWorkingRow> baselineRows,
            List<PlannerWorkingRow> adjustedRows)
        {
            List<DispatchPlannerScheduleActionDto> actions = new List<DispatchPlannerScheduleActionDto>();
            if (activeExpressOffsetMinutes != 0)
            {
                string[] affectedLineIds = context.TargetLineIds ?? new string[0];
                actions.Add(new DispatchPlannerScheduleActionDto
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
                    priorityTripIds = new string[0],
                    predictedHoldPairs = Array.Empty<DispatchPlannerPredictedHoldPairDto>(),
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
                actions.Add(new DispatchPlannerScheduleActionDto
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
                    priorityTripIds = new string[0],
                    predictedHoldPairs = Array.Empty<DispatchPlannerPredictedHoldPairDto>(),
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
                    && baselineById.TryGetValue(row.Id, out PlannerWorkingRow baseline)
                    && ResolveResidualRetimeDeltaMinutes(context, row, baseline, activeExpressOffsetMinutes) != 0)
                .GroupBy(row => row.LineId ?? string.Empty, StringComparer.Ordinal))
            {
                PlannerWorkingRow[] rows = lineGroup.ToArray();
                float[] deltaPattern = rows
                    .Select(row => (float)ResolveResidualRetimeDeltaMinutes(context, row, baselineById[row.Id], activeExpressOffsetMinutes))
                    .ToArray();
                string[] tripIds = rows
                    .Select(row => row.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                float deltaMinutes = deltaPattern.Length == 0 ? 0f : deltaPattern.Max(delta => Math.Abs(delta));
                bool hasExpressTrips = rows.Any(row => string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase));
                actions.Add(new DispatchPlannerScheduleActionDto
                {
                    actionType = "retime",
                    type = "retime",
                    shape = hasExpressTrips ? "tripVector" : "localWindow",
                    reason = hasExpressTrips ? "retimeTargetExpressTrips" : "retimeAdjustableLocalTrips",
                    targetRegionIds = ResolveRegionIdsForLine(riskClusters, lineGroup.Key),
                    reasonRegionIds = ResolveRegionIdsForLine(riskClusters, lineGroup.Key),
                    clusterIds = ResolveClusterIdsForLine(riskClusters, lineGroup.Key),
                    reasonClusterIds = ResolveClusterIdsForLine(riskClusters, lineGroup.Key),
                    stationIds = new string[0],
                    affectedLineIds = new[] { lineGroup.Key },
                    affectedLineId = lineGroup.Key,
                    affectedTripIds = tripIds,
                    priorityTripIds = new string[0],
                    predictedHoldPairs = Array.Empty<DispatchPlannerPredictedHoldPairDto>(),
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
                string[] priorityTripIds = eventsByLine
                    .Select(item => string.IsNullOrEmpty(item.PriorityTripId) ? item.ExpressTripId : item.PriorityTripId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                DispatchPlannerPredictedHoldPairDto[] predictedHoldPairs = eventsByLine
                    .Select(item => new DispatchPlannerPredictedHoldPairDto
                    {
                        catchupId = item.EventId ?? string.Empty,
                        yieldingLineId = string.IsNullOrEmpty(item.YieldingLineId) ? item.LocalLineId : item.YieldingLineId,
                        priorityLineId = string.IsNullOrEmpty(item.PriorityLineId) ? item.ExpressLineId : item.PriorityLineId,
                        yieldingTripId = string.IsNullOrEmpty(item.YieldingTripId) ? item.LocalTripId : item.YieldingTripId,
                        priorityTripId = string.IsNullOrEmpty(item.PriorityTripId) ? item.ExpressTripId : item.PriorityTripId,
                        stationId = item.SelectedBypassStation?.StationId ?? string.Empty,
                        catchupTime = PlannerMath.MinutesToTime((int)Math.Round(item.CatchupMinute)),
                        plannedHoldMinutes = PlannerMath.Round2(item.ResolvedHoldMinutes)
                    })
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
                actions.Add(new DispatchPlannerScheduleActionDto
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
                    priorityTripIds = priorityTripIds,
                    predictedHoldPairs = predictedHoldPairs,
                    tripIds = tripIds,
                    deltaPattern = eventsByLine.Select(item => PlannerMath.Round2(item.ResolvedHoldMinutes)).ToArray(),
                    deltaMinutes = PlannerMath.Round2(deltaMinutes),
                    deltaOffsetMinutes = 0f,
                    riskScore = PlannerMath.Round2(eventsByLine.Sum(item => item.ResolvedHoldMinutes))
                });
            }

            return actions;
        }

        private static DispatchPlannerFrontendSummaryDto BuildFrontendSummary(
            PlannerContext context,
            PlannerPlanModel plan)
        {
            string[] adjustedLineIds = plan.StructuredScheduleActions
                .SelectMany(action => action.affectedLineIds ?? new string[0])
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            DispatchPlannerIssueCountDto[] issueCounts = plan.ProblemIssues
                .GroupBy(issue => issue.type ?? string.Empty)
                .Select(group => new DispatchPlannerIssueCountDto
                {
                    type = group.Key,
                    count = group.Count()
                })
                .ToArray();

            return new DispatchPlannerFrontendSummaryDto
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
                && !string.Equals(item.PairRole, "fixed-fixed", StringComparison.Ordinal)))
            {
                return "blocked";
            }
            if (plan.ProblemIssues.Any(item =>
                string.Equals(item.type, "originDepartureGap", StringComparison.Ordinal)
                && string.Equals(item.severity, "high", StringComparison.Ordinal)))
            {
                return "blocked";
            }
            if (plan.CatchupEvents.Any(item =>
                string.Equals(item.ResolutionState, "actionable", StringComparison.Ordinal)
                && !string.Equals(item.PairRole, "fixed-fixed", StringComparison.Ordinal)))
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

        private static List<DispatchPlannerPreviewRowDto> BuildPreviewRows(
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

            List<DispatchPlannerPreviewRowDto> rows = new List<DispatchPlannerPreviewRowDto>();
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

                rows.Add(new DispatchPlannerPreviewRowDto
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

        private static int CountRetimedTrips(
            List<PlannerWorkingRow> baselineRows,
            List<PlannerWorkingRow> adjustedRows,
            PlannerContext context,
            int activeExpressOffsetMinutes)
        {
            Dictionary<string, PlannerWorkingRow> baselineById = (baselineRows ?? new List<PlannerWorkingRow>())
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            return (adjustedRows ?? new List<PlannerWorkingRow>())
                .Count(row =>
                    row != null
                    && !string.IsNullOrEmpty(row.Id)
                    && baselineById.TryGetValue(row.Id, out PlannerWorkingRow baseline)
                    && ResolveResidualRetimeDeltaMinutes(context, row, baseline, activeExpressOffsetMinutes) != 0);
        }

        private static int ResolveResidualRetimeDeltaMinutes(
            PlannerContext context,
            PlannerWorkingRow adjustedRow,
            PlannerWorkingRow baselineRow,
            int activeExpressOffsetMinutes)
        {
            if (adjustedRow == null || baselineRow == null)
            {
                return 0;
            }

            int scheduleShiftMinutes = adjustedRow.Minute - baselineRow.Minute;
            return scheduleShiftMinutes - ResolveUniformTargetExpressOffsetMinutes(context, adjustedRow, activeExpressOffsetMinutes);
        }

        private static int ResolveUniformTargetExpressOffsetMinutes(
            PlannerContext context,
            PlannerWorkingRow row,
            int activeExpressOffsetMinutes)
        {
            if (activeExpressOffsetMinutes == 0
                || row == null
                || !string.Equals(row.Kind, "express", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return (context?.TargetLineIds ?? Array.Empty<string>()).Contains(row.LineId ?? string.Empty)
                ? activeExpressOffsetMinutes
                : 0;
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

        private sealed class PlannerOriginDeparture
        {
            public string LineId = string.Empty;
            public string TripId = string.Empty;
            public string OriginStationId = string.Empty;
            public int Minute = 0;
        }
    }
}
