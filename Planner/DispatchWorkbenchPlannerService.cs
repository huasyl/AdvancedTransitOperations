using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RapidTransitMod.Planner
{
    internal sealed class DispatchWorkbenchPlannerService
    {
        private readonly PlannerInputNormalizer m_Normalizer = new PlannerInputNormalizer();
        private readonly LineRuntimeModelBuilder m_RuntimeModelBuilder = new LineRuntimeModelBuilder();
        private readonly PursuitTrunkBuilder m_PursuitTrunkBuilder = new PursuitTrunkBuilder();
        private readonly CatchupDetector m_CatchupDetector = new CatchupDetector();
        private readonly OptimizationRegionBuilder m_RegionBuilder = new OptimizationRegionBuilder();
        private readonly BypassCandidateEvaluator m_BypassEvaluator = new BypassCandidateEvaluator();
        private readonly ScheduleActionSearch m_Search = new ScheduleActionSearch();
        private readonly PlanScorer m_Scorer = new PlanScorer();
        private readonly SharedCorridorCapacityDiagnosticService m_CapacityDiagnosticService = new SharedCorridorCapacityDiagnosticService();
        private readonly PlannerResultProjector m_Projector = new PlannerResultProjector();

        public DispatchPlannerResult Execute(
            DispatchPlannerExportSnapshot snapshot,
            DispatchPlannerRequest request)
        {
            PlannerContext context = m_Normalizer.Normalize(snapshot, request);
            PlannerExecutionState state = new PlannerExecutionState();
            state.Context = context;
            state.Diagnostics = new List<PlannerValidationIssue>(context.ValidationIssues);
            state.RuntimeCatalog = m_RuntimeModelBuilder.Build(context);
            state.PursuitTrunks = m_PursuitTrunkBuilder.Build(context);
            List<PlannerWorkingRow> baseWorkingRows = context.WorkingRows
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
            if (baseWorkingRows.Count == 0)
            {
                return m_Projector.Project(state);
            }
            state.BaselineCapacityDiagnostic = m_CapacityDiagnosticService.Analyze(
                context,
                state.RuntimeCatalog,
                baseWorkingRows);

            List<int> offsetVariants = BuildExpressOffsetVariants(context);
            List<string[]> stationSets = BuildVirtualBypassStationSets(context, state.PursuitTrunks);
            if (stationSets.Count == 0)
            {
                stationSets.Add(new string[0]);
            }

            PlannerBestPlanCollector bestPlans = new PlannerBestPlanCollector();
            Parallel.ForEach(Enumerable.Range(0, offsetVariants.Count), CreatePlannerParallelOptions(), offsetIndex =>
            {
                int offsetMinutes = offsetVariants[offsetIndex];
                PlannerContext searchContext = ClonePlannerContext(context);
                searchContext.ActiveExpressOffsetMinutes = offsetMinutes;
                CatchupDetector catchupDetector = new CatchupDetector();
                OptimizationRegionBuilder regionBuilder = new OptimizationRegionBuilder();
                BypassCandidateEvaluator bypassEvaluator = new BypassCandidateEvaluator();
                ScheduleActionSearch search = new ScheduleActionSearch();
                PlanScorer scorer = new PlanScorer();
                List<PlannerValidationIssue> diagnostics = new List<PlannerValidationIssue>(state.Diagnostics);
                Dictionary<string, PlannerSearchEvaluation> evaluationCache = new Dictionary<string, PlannerSearchEvaluation>(StringComparer.Ordinal);
                List<PlannerWorkingRow> offsetWorkingRows = BuildOffsetWorkingRows(searchContext, baseWorkingRows, offsetMinutes);
                List<PlannerWorkingRow> repairedOffsetWorkingRows = TryRepairOriginDepartureGaps(
                    searchContext,
                    state.RuntimeCatalog,
                    baseWorkingRows,
                    offsetWorkingRows);
                if (repairedOffsetWorkingRows == null)
                {
                    return;
                }
                for (int stationSetIndex = 0; stationSetIndex < stationSets.Count; stationSetIndex++)
                {
                    searchContext.ActiveVirtualBypassStationIds = stationSets[stationSetIndex];
                    searchContext.WorkingRows = CloneWorkingRows(repairedOffsetWorkingRows);
                    PlannerSearchEvaluation evaluation = GetOrCreateSearchEvaluation(
                        evaluationCache,
                        searchContext,
                        state.RuntimeCatalog,
                        state.PursuitTrunks,
                        catchupDetector,
                        regionBuilder,
                        bypassEvaluator,
                        stationSets[stationSetIndex]);
                    List<PlannerPlanModel> plans = search.BuildInitialPlans(
                        searchContext,
                        evaluation.RiskClusters,
                        evaluation.CatchupEvents,
                        diagnostics,
                        state.RuntimeCatalog,
                        stationSets[stationSetIndex],
                        offsetMinutes,
                        string.Empty,
                        baseWorkingRows);
                    AddCandidatePlans(bestPlans, plans, scorer);
                    if (offsetIndex == 0 && stationSetIndex == 0)
                    {
                        lock (state)
                        {
                            state.CatchupEvents = evaluation.CatchupEvents;
                            state.RiskClusters = evaluation.RiskClusters;
                            state.OptimizationRegions = regionBuilder.BuildOptimizationRegions(evaluation.RiskClusters);
                            state.Trips = catchupDetector.BuildTrips(searchContext, state.RuntimeCatalog);
                        }
                    }

                    List<PlannerRetimeVariant> retimeVariants = BuildTripRetimeVariants(searchContext, searchContext.WorkingRows, evaluation.CatchupEvents);
                    for (int retimeIndex = 0; retimeIndex < retimeVariants.Count; retimeIndex++)
                    {
                        PlannerRetimeVariant retimeVariant = retimeVariants[retimeIndex];
                        List<PlannerWorkingRow> repairedRetimeRows = TryRepairOriginDepartureGaps(
                            searchContext,
                            state.RuntimeCatalog,
                            baseWorkingRows,
                            retimeVariant.Rows);
                        if (repairedRetimeRows == null)
                        {
                            continue;
                        }

                        searchContext.WorkingRows = repairedRetimeRows;
                        PlannerSearchEvaluation retimedEvaluation = GetOrCreateSearchEvaluation(
                            evaluationCache,
                            searchContext,
                            state.RuntimeCatalog,
                            state.PursuitTrunks,
                            catchupDetector,
                            regionBuilder,
                            bypassEvaluator,
                            stationSets[stationSetIndex]);
                        AddCandidatePlans(bestPlans, search.BuildInitialPlans(
                            searchContext,
                            retimedEvaluation.RiskClusters,
                            retimedEvaluation.CatchupEvents,
                            diagnostics,
                            state.RuntimeCatalog,
                            stationSets[stationSetIndex],
                            offsetMinutes,
                            retimeVariant.Key,
                            baseWorkingRows),
                            scorer);
                    }
                }
            });
            state.Plans = bestPlans.ToSelectedPlans();
            for (int planIndex = 0; planIndex < state.Plans.Count; planIndex++)
            {
                PlannerPlanModel plan = state.Plans[planIndex];
                List<PlannerWorkingRow> capacityRows = plan.AdjustedRows != null && plan.AdjustedRows.Count > 0
                    ? plan.AdjustedRows
                    : baseWorkingRows;
                plan.CapacityDiagnostic = m_CapacityDiagnosticService.Analyze(
                    context,
                    state.RuntimeCatalog,
                    capacityRows);
            }

            if (state.Diagnostics.Count == 0)
            {
                state.Diagnostics.Add(PlannerDiagnosticFactory.Create(
                    "info",
                    "BACKEND_ANALYSIS_READY",
                    "Backend planner analysis completed successfully."));
            }

            return m_Projector.Project(state);
        }

        private static ParallelOptions CreatePlannerParallelOptions()
        {
            int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
            return new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(workerCount, 4)
            };
        }

        private static void AddCandidatePlans(
            PlannerBestPlanCollector bestPlans,
            IEnumerable<PlannerPlanModel> plans,
            PlanScorer scorer)
        {
            if (bestPlans == null || plans == null || scorer == null)
            {
                return;
            }

            foreach (PlannerPlanModel plan in plans)
            {
                if (plan != null)
                {
                    scorer.Apply(plan);
                    bestPlans.Add(plan);
                }
            }
        }

        private static PlannerContext ClonePlannerContext(PlannerContext source)
        {
            return new PlannerContext
            {
                Snapshot = source.Snapshot,
                Request = source.Request,
                SelectedDraft = source.SelectedDraft,
                SelectedLineIds = CloneStringArray(source.SelectedLineIds),
                EffectiveLineIds = CloneStringArray(source.EffectiveLineIds),
                AutoFixedConstraintLineIds = CloneStringArray(source.AutoFixedConstraintLineIds),
                SuppressedFixedVsFixedClusterCount = source.SuppressedFixedVsFixedClusterCount,
                AdjustableLineIds = CloneStringArray(source.AdjustableLineIds),
                FixedLineIds = CloneStringArray(source.FixedLineIds),
                TargetLineIds = CloneStringArray(source.TargetLineIds),
                ActiveVirtualBypassStationIds = CloneStringArray(source.ActiveVirtualBypassStationIds),
                ActiveExpressOffsetMinutes = source.ActiveExpressOffsetMinutes,
                SelectedLocalLineIds = CloneStringArray(source.SelectedLocalLineIds),
                SelectedExpressLineIds = CloneStringArray(source.SelectedExpressLineIds),
                VirtualExpressLineId = source.VirtualExpressLineId,
                SelectedExpressStopStationIds = CloneStringArray(source.SelectedExpressStopStationIds),
                ForcedBypassStationIds = CloneStringArray(source.ForcedBypassStationIds),
                WindowStart = source.WindowStart,
                WindowEnd = source.WindowEnd,
                WindowStartMinute = source.WindowStartMinute,
                WindowEndMinute = source.WindowEndMinute,
                ExpressSourceMode = source.ExpressSourceMode,
                DepartureMode = source.DepartureMode,
                VirtualExpressBaseLineId = source.VirtualExpressBaseLineId,
                LinesById = source.LinesById,
                StationsById = source.StationsById,
                StationsByLineId = source.StationsByLineId,
                SegmentsByLineId = source.SegmentsByLineId,
                LineTracksByLineId = source.LineTracksByLineId,
                StopDwellByStationId = source.StopDwellByStationId,
                StationRuntimeByLinePair = source.StationRuntimeByLinePair,
                ConfiguredBypassStationsByLineId = source.ConfiguredBypassStationsByLineId,
                CandidateBypassStationsByLineId = source.CandidateBypassStationsByLineId,
                WorkingRows = CloneWorkingRows(source.WorkingRows),
                ValidationIssues = new List<PlannerValidationIssue>(source.ValidationIssues)
            };
        }

        private static string[] CloneStringArray(string[] source)
        {
            return source == null ? new string[0] : source.ToArray();
        }

        private static PlannerSearchEvaluation GetOrCreateSearchEvaluation(
            Dictionary<string, PlannerSearchEvaluation> evaluationCache,
            PlannerContext context,
            PlannerRuntimeCatalog runtimeCatalog,
            List<PursuitTrunk> pursuitTrunks,
            CatchupDetector catchupDetector,
            OptimizationRegionBuilder regionBuilder,
            BypassCandidateEvaluator bypassEvaluator,
            string[] stationSet)
        {
            string key = BuildSearchEvaluationKey(stationSet, context.WorkingRows);
            if (evaluationCache != null && evaluationCache.TryGetValue(key, out PlannerSearchEvaluation cached))
            {
                return cached;
            }

            List<PlannerCatchupEvent> catchupEvents = catchupDetector.Detect(context, runtimeCatalog, pursuitTrunks);
            List<PlannerRiskCluster> riskClusters = regionBuilder.BuildRiskClusters(context, catchupEvents);
            bypassEvaluator.Enrich(riskClusters, context);
            PlannerSearchEvaluation evaluation = new PlannerSearchEvaluation
            {
                CatchupEvents = catchupEvents,
                RiskClusters = riskClusters
            };
            if (evaluationCache != null)
            {
                evaluationCache[key] = evaluation;
            }
            return evaluation;
        }

        private static string BuildSearchEvaluationKey(string[] stationSet, IEnumerable<PlannerWorkingRow> rows)
        {
            return BuildStationSetKey(stationSet) + "||" + BuildWorkingRowsSignature(rows);
        }

        private static string BuildStationSetKey(string[] stationSet)
        {
            return string.Join("+", (stationSet ?? new string[0])
                .Where(stationId => !string.IsNullOrEmpty(stationId))
                .OrderBy(stationId => stationId, StringComparer.Ordinal));
        }

        private static string BuildWorkingRowsSignature(IEnumerable<PlannerWorkingRow> rows)
        {
            return string.Join(";",
                (rows ?? Array.Empty<PlannerWorkingRow>())
                    .Where(row => row != null)
                    .OrderBy(row => row.Minute)
                    .ThenBy(row => row.LineId, StringComparer.Ordinal)
                    .ThenBy(row => row.Id, StringComparer.Ordinal)
                    .Select(row => (row.LineId ?? string.Empty)
                        + "|" + (row.Id ?? string.Empty)
                        + "|" + row.Minute.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "|" + (row.Kind ?? string.Empty)));
        }

        private static int ComparePlans(PlannerPlanModel left, PlannerPlanModel right)
        {
            int infeasibleCompare = GetInfeasibleRank(left).CompareTo(GetInfeasibleRank(right));
            if (infeasibleCompare != 0)
            {
                return infeasibleCompare;
            }

            int originGapCompare = HasBlockingOriginDepartureGap(left).CompareTo(HasBlockingOriginDepartureGap(right));
            if (originGapCompare != 0)
            {
                return originGapCompare;
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

            return string.Compare(left.PlanId, right.PlanId, System.StringComparison.Ordinal);
        }

        private static int GetInfeasibleRank(PlannerPlanModel plan)
        {
            string status = plan?.Status ?? string.Empty;
            if (string.Equals(status, "feasible", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            if (string.Equals(status, "needsAction", System.StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            if (string.Equals(status, "fragile", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "risk", System.StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (string.Equals(status, "blocked", System.StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            return string.Equals(status, "infeasible", System.StringComparison.OrdinalIgnoreCase) ? 4 : 2;
        }

        private static bool HasBlockingOriginDepartureGap(PlannerPlanModel plan)
        {
            return (plan?.ProblemIssues ?? new List<DispatchPlannerProblemIssueDto>()).Any(item =>
                string.Equals(item?.type, "originDepartureGap", System.StringComparison.Ordinal)
                && string.Equals(item?.severity, "high", System.StringComparison.Ordinal));
        }

        private static List<PlannerPlanModel> SelectBestPlansByObjective(List<PlannerPlanModel> candidatePlans)
        {
            Dictionary<string, List<PlannerPlanModel>> plansByObjective = new Dictionary<string, List<PlannerPlanModel>>(System.StringComparer.Ordinal);
            foreach (PlannerPlanModel candidate in candidatePlans ?? new List<PlannerPlanModel>())
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.ObjectiveId))
                {
                    continue;
                }

                if (!plansByObjective.TryGetValue(candidate.ObjectiveId, out List<PlannerPlanModel> objectivePlans))
                {
                    objectivePlans = new List<PlannerPlanModel>();
                    plansByObjective[candidate.ObjectiveId] = objectivePlans;
                }
                objectivePlans.Add(candidate);
            }

            List<PlannerPlanModel> selectedPlans = new List<PlannerPlanModel>();
            foreach (PlannerObjectiveDefinition objective in PlannerDefaults.Objectives)
            {
                if (!plansByObjective.TryGetValue(objective.Id, out List<PlannerPlanModel> objectivePlans)
                    || objectivePlans.Count == 0)
                {
                    continue;
                }

                objectivePlans.Sort(ComparePlans);
                selectedPlans.Add(objectivePlans[0]);
            }

            return selectedPlans;
        }

        private sealed class PlannerBestPlanCollector
        {
            private readonly object m_Sync = new object();
            private readonly Dictionary<string, PlannerPlanModel> m_BestByObjective =
                new Dictionary<string, PlannerPlanModel>(System.StringComparer.Ordinal);

            public void Add(PlannerPlanModel plan)
            {
                if (plan == null || string.IsNullOrEmpty(plan.ObjectiveId))
                {
                    return;
                }

                lock (m_Sync)
                {
                    if (!m_BestByObjective.TryGetValue(plan.ObjectiveId, out PlannerPlanModel current)
                        || ComparePlans(plan, current) < 0)
                    {
                        m_BestByObjective[plan.ObjectiveId] = plan;
                    }
                }
            }

            public List<PlannerPlanModel> ToSelectedPlans()
            {
                lock (m_Sync)
                {
                    List<PlannerPlanModel> selectedPlans = new List<PlannerPlanModel>();
                    foreach (PlannerObjectiveDefinition objective in PlannerDefaults.Objectives)
                    {
                        if (m_BestByObjective.TryGetValue(objective.Id, out PlannerPlanModel plan))
                        {
                            selectedPlans.Add(plan);
                        }
                    }
                    return selectedPlans;
                }
            }
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
                            (action?.affectedLineIds ?? new string[0])
                                .Where(lineId => !string.IsNullOrEmpty(lineId))
                                .OrderBy(lineId => lineId, System.StringComparer.Ordinal));
                        string stationIds = string.Join(",",
                            (action?.stationIds ?? new string[0])
                                .Where(stationId => !string.IsNullOrEmpty(stationId))
                                .OrderBy(stationId => stationId, System.StringComparer.Ordinal));
                        string deltaPattern = string.Join(",",
                            (action?.deltaPattern ?? new float[0])
                                .Select(delta => PlannerMath.Round2(delta).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                        return actionType
                            + "|line:" + affectedLineIds
                            + "|station:" + stationIds
                            + "|offset:" + PlannerMath.Round2(action?.deltaOffsetMinutes ?? 0f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                            + "|delta:" + PlannerMath.Round2(action?.deltaMinutes ?? 0f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                            + "|pattern:" + deltaPattern;
                    })
                    .OrderBy(token => token, System.StringComparer.Ordinal);
                return string.Join(";", actionTokens);
            }

            string bypassSignature = string.Join(",",
                (plan.SelectedBypassStationIds ?? new List<string>())
                    .Where(stationId => !string.IsNullOrEmpty(stationId))
                    .OrderBy(stationId => stationId, System.StringComparer.Ordinal));
            return "offset=" + plan.RecommendedExpressOffsetDeltaMinutes
                + "|status=" + (plan.Status ?? string.Empty)
                + "|bypass=" + bypassSignature
                + "|retimed=" + plan.RetimedTripCount;
        }

        private static List<int> BuildExpressOffsetVariants(PlannerContext context)
        {
            int baseOffset = context.Request.expressOffsetMinutes;
            int maxOffset = System.Math.Max(0, context.Request.maxOffsetMinutes);
            int step = context.Request.offsetStepMinutes > 0 ? context.Request.offsetStepMinutes : 2;
            List<int> offsets = new List<int>();
            if (maxOffset <= 0)
            {
                offsets.Add(baseOffset);
                return offsets;
            }

            for (int delta = -maxOffset; delta <= maxOffset; delta += step)
            {
                offsets.Add(baseOffset + delta);
            }
            if (!offsets.Contains(baseOffset))
            {
                offsets.Add(baseOffset);
            }
            offsets.Sort();
            return offsets;
        }

        private static List<PlannerWorkingRow> BuildOffsetWorkingRows(
            PlannerContext context,
            List<PlannerWorkingRow> baseRows,
            int offsetMinutes)
        {
            HashSet<string> targetLineIds = new HashSet<string>(context.TargetLineIds ?? new string[0], System.StringComparer.Ordinal);
            List<PlannerWorkingRow> rows = new List<PlannerWorkingRow>();
            foreach (PlannerWorkingRow row in baseRows)
            {
                int minute = targetLineIds.Contains(row.LineId)
                    ? row.Minute + offsetMinutes
                    : row.Minute;
                rows.Add(new PlannerWorkingRow
                {
                    Id = row.Id,
                    LineId = row.LineId,
                    Kind = row.Kind,
                    Minute = minute,
                    Source = row.Source,
                    Note = row.Note
                });
            }

            rows.Sort((left, right) =>
            {
                int minuteCompare = left.Minute.CompareTo(right.Minute);
                if (minuteCompare != 0)
                {
                    return minuteCompare;
                }
                int lineCompare = string.Compare(left.LineId, right.LineId, System.StringComparison.Ordinal);
                return lineCompare != 0 ? lineCompare : string.Compare(left.Id, right.Id, System.StringComparison.Ordinal);
            });
            return rows;
        }

        private static List<PlannerRetimeVariant> BuildTripRetimeVariants(
            PlannerContext context,
            List<PlannerWorkingRow> baseRows,
            List<PlannerCatchupEvent> catchupEvents)
        {
            int maxLocalRetimeMinutes = System.Math.Max(0, context.Request.maxLocalRetimeMinutes);
            int maxTargetExpressRetimeMinutes = ResolveTargetExpressRetimeBudgetMinutes(context);
            if (maxLocalRetimeMinutes <= 0 && maxTargetExpressRetimeMinutes <= 0)
            {
                return new List<PlannerRetimeVariant>();
            }

            HashSet<string> adjustableLineIds = new HashSet<string>(context.AdjustableLineIds ?? new string[0], System.StringComparer.Ordinal);
            HashSet<string> targetLineIds = new HashSet<string>(context.TargetLineIds ?? new string[0], System.StringComparer.Ordinal);
            Dictionary<string, int> localSuggestedShiftsByTripId = new Dictionary<string, int>(System.StringComparer.Ordinal);
            List<PlannerRetimeVariant> variants = new List<PlannerRetimeVariant>();
            HashSet<string> variantKeys = new HashSet<string>(System.StringComparer.Ordinal);

            if (maxLocalRetimeMinutes > 0)
            {
                List<PlannerCatchupEvent> localCandidateEvents = (catchupEvents ?? new List<PlannerCatchupEvent>())
                    .Where(item =>
                        item != null
                        && !string.IsNullOrEmpty(item.LocalTripId)
                        && adjustableLineIds.Contains(item.LocalLineId))
                    .ToList();
                foreach (PlannerCatchupEvent catchupEvent in BuildOrderedLocalRetimeEvents(localCandidateEvents))
                {
                    int deltaMinutes = ResolveRetimeDeltaMinutes(catchupEvent, maxLocalRetimeMinutes);
                    if (deltaMinutes <= 0)
                    {
                        continue;
                    }

                    AddRetimeVariant(variants, variantKeys, baseRows, catchupEvent.LocalTripId, -deltaMinutes);
                    AddRetimeVariant(variants, variantKeys, baseRows, catchupEvent.LocalTripId, deltaMinutes);

                    if (!localSuggestedShiftsByTripId.ContainsKey(catchupEvent.LocalTripId))
                    {
                        localSuggestedShiftsByTripId[catchupEvent.LocalTripId] = -deltaMinutes;
                    }
                }
            }

            if (maxTargetExpressRetimeMinutes > 0)
            {
                List<PlannerCatchupEvent> expressCandidateEvents = (catchupEvents ?? new List<PlannerCatchupEvent>())
                    .Where(item =>
                        item != null
                        && (string.Equals(item.PairRole, "target-adjustable", System.StringComparison.Ordinal)
                            || string.Equals(item.PairRole, "target-fixed", System.StringComparison.Ordinal))
                        && !string.IsNullOrEmpty(ResolveTargetExpressTripId(item, targetLineIds)))
                    .ToList();
                foreach (PlannerCatchupEvent catchupEvent in BuildOrderedTargetExpressRetimeEvents(expressCandidateEvents, targetLineIds))
                {
                    string targetTripId = ResolveTargetExpressTripId(catchupEvent, targetLineIds);
                    if (string.IsNullOrEmpty(targetTripId))
                    {
                        continue;
                    }

                    int deltaMinutes = ResolveRetimeDeltaMinutes(catchupEvent, maxTargetExpressRetimeMinutes);
                    if (deltaMinutes <= 0)
                    {
                        continue;
                    }

                    int preferredDirection = ResolveTargetExpressPreferredShiftDirection(catchupEvent, targetLineIds);
                    AddRetimeVariant(variants, variantKeys, baseRows, targetTripId, preferredDirection * deltaMinutes);
                    if (deltaMinutes > PlannerDefaults.PursuitCurveSampleStepMinutes)
                    {
                        AddRetimeVariant(variants, variantKeys, baseRows, targetTripId, preferredDirection * (deltaMinutes / 2));
                    }
                }
            }

            if (localSuggestedShiftsByTripId.Count > 1)
            {
                AddRetimeVariant(variants, variantKeys, baseRows, localSuggestedShiftsByTripId);
            }

            return variants;
        }

        private static List<PlannerCatchupEvent> BuildOrderedLocalRetimeEvents(List<PlannerCatchupEvent> candidateEvents)
        {
            List<PlannerCatchupEvent> orderedEvents = new List<PlannerCatchupEvent>();
            HashSet<string> eventIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in candidateEvents
                .Where(item => string.Equals(item.PairRole, "target-adjustable", System.StringComparison.Ordinal))
                .GroupBy(item => (item.LocalLineId ?? string.Empty) + "|" + (item.TrunkId ?? string.Empty), System.StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.UnresolvedRiskMinutes + item.RobustnessRiskMinutes)
                    .ThenByDescending(item => item.RequiredHoldMinutes)
                    .First()))
            {
                if (eventIds.Add(catchupEvent.EventId ?? string.Empty))
                {
                    orderedEvents.Add(catchupEvent);
                }
            }

            foreach (PlannerCatchupEvent catchupEvent in candidateEvents
                .OrderByDescending(item => item.UnresolvedRiskMinutes + item.RobustnessRiskMinutes)
                .ThenByDescending(item => item.RequiredHoldMinutes)
                .Take(12))
            {
                if (eventIds.Add(catchupEvent.EventId ?? string.Empty))
                {
                    orderedEvents.Add(catchupEvent);
                }
            }

            return orderedEvents;
        }

        private static List<PlannerCatchupEvent> BuildOrderedTargetExpressRetimeEvents(
            List<PlannerCatchupEvent> candidateEvents,
            HashSet<string> targetLineIds)
        {
            List<PlannerCatchupEvent> orderedEvents = new List<PlannerCatchupEvent>();
            HashSet<string> eventIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in candidateEvents
                .GroupBy(item => ResolveTargetExpressTripId(item, targetLineIds), System.StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(item => item.UnresolvedRiskMinutes + item.RobustnessRiskMinutes)
                    .ThenByDescending(item => item.RequiredHoldMinutes)
                    .First()))
            {
                if (eventIds.Add(catchupEvent.EventId ?? string.Empty))
                {
                    orderedEvents.Add(catchupEvent);
                }
            }

            foreach (PlannerCatchupEvent catchupEvent in candidateEvents
                .OrderByDescending(item => item.UnresolvedRiskMinutes + item.RobustnessRiskMinutes)
                .ThenByDescending(item => item.RequiredHoldMinutes)
                .Take(4))
            {
                if (eventIds.Add(catchupEvent.EventId ?? string.Empty))
                {
                    orderedEvents.Add(catchupEvent);
                }
            }

            return orderedEvents;
        }

        private static int ResolveTargetExpressRetimeBudgetMinutes(PlannerContext context)
        {
            if (context == null || context.Request == null)
            {
                return 0;
            }

            return System.Math.Max(0, context.Request.maxOffsetMinutes - System.Math.Abs(context.ActiveExpressOffsetMinutes));
        }

        private static string ResolveTargetExpressTripId(
            PlannerCatchupEvent catchupEvent,
            HashSet<string> targetLineIds)
        {
            if (catchupEvent == null || targetLineIds == null || targetLineIds.Count == 0)
            {
                return string.Empty;
            }

            string priorityTripId = string.IsNullOrEmpty(catchupEvent.PriorityTripId) ? catchupEvent.ExpressTripId : catchupEvent.PriorityTripId;
            if (!string.IsNullOrEmpty(priorityTripId)
                && targetLineIds.Contains(catchupEvent.PriorityLineId ?? string.Empty))
            {
                return priorityTripId;
            }

            string yieldingTripId = string.IsNullOrEmpty(catchupEvent.YieldingTripId) ? catchupEvent.LocalTripId : catchupEvent.YieldingTripId;
            if (!string.IsNullOrEmpty(yieldingTripId)
                && targetLineIds.Contains(catchupEvent.YieldingLineId ?? string.Empty))
            {
                return yieldingTripId;
            }

            if (!string.IsNullOrEmpty(catchupEvent.ExpressTripId)
                && targetLineIds.Contains(catchupEvent.ExpressLineId ?? string.Empty))
            {
                return catchupEvent.ExpressTripId;
            }

            if (!string.IsNullOrEmpty(catchupEvent.LocalTripId)
                && targetLineIds.Contains(catchupEvent.LocalLineId ?? string.Empty))
            {
                return catchupEvent.LocalTripId;
            }

            return string.Empty;
        }

        private static int ResolveTargetExpressPreferredShiftDirection(
            PlannerCatchupEvent catchupEvent,
            HashSet<string> targetLineIds)
        {
            if (catchupEvent == null || targetLineIds == null || targetLineIds.Count == 0)
            {
                return 1;
            }

            if (targetLineIds.Contains(catchupEvent.PriorityLineId ?? string.Empty))
            {
                return 1;
            }

            if (targetLineIds.Contains(catchupEvent.YieldingLineId ?? string.Empty))
            {
                return -1;
            }

            if (targetLineIds.Contains(catchupEvent.ExpressLineId ?? string.Empty))
            {
                return 1;
            }

            return targetLineIds.Contains(catchupEvent.LocalLineId ?? string.Empty) ? -1 : 1;
        }

        private static int ResolveRetimeDeltaMinutes(PlannerCatchupEvent catchupEvent, int maxRetimeMinutes)
        {
            float targetMinutes = System.Math.Max(catchupEvent.RequiredHoldMinutes, catchupEvent.RobustnessRiskMinutes);
            if (targetMinutes <= 0f)
            {
                targetMinutes = PlannerDefaults.PursuitCurveSampleStepMinutes;
            }

            int stepMinutes = System.Math.Max(1, (int)PlannerDefaults.PursuitCurveSampleStepMinutes);
            int roundedMinutes = (int)(System.Math.Ceiling(targetMinutes / stepMinutes) * stepMinutes);
            return System.Math.Max(0, System.Math.Min(maxRetimeMinutes, roundedMinutes));
        }

        private static void AddRetimeVariant(
            List<PlannerRetimeVariant> variants,
            HashSet<string> variantKeys,
            List<PlannerWorkingRow> baseRows,
            string tripId,
            int shiftMinutes)
        {
            if (string.IsNullOrEmpty(tripId) || shiftMinutes == 0)
            {
                return;
            }

            AddRetimeVariant(
                variants,
                variantKeys,
                baseRows,
                new Dictionary<string, int>(System.StringComparer.Ordinal)
                {
                    { tripId, shiftMinutes }
                });
        }

        private static void AddRetimeVariant(
            List<PlannerRetimeVariant> variants,
            HashSet<string> variantKeys,
            List<PlannerWorkingRow> baseRows,
            Dictionary<string, int> shiftsByTripId)
        {
            if (shiftsByTripId == null || shiftsByTripId.Count == 0)
            {
                return;
            }

            string key = BuildRetimeVariantKey(shiftsByTripId);
            if (!variantKeys.Add(key))
            {
                return;
            }

            List<PlannerWorkingRow> rows = new List<PlannerWorkingRow>();
            foreach (PlannerWorkingRow row in baseRows)
            {
                shiftsByTripId.TryGetValue(row.Id, out int shiftMinutes);
                rows.Add(new PlannerWorkingRow
                {
                    Id = row.Id,
                    LineId = row.LineId,
                    Kind = row.Kind,
                    Minute = System.Math.Max(0, System.Math.Min(1439, row.Minute + shiftMinutes)),
                    Source = row.Source,
                    Note = row.Note
                });
            }

            rows.Sort((left, right) =>
            {
                int minuteCompare = left.Minute.CompareTo(right.Minute);
                if (minuteCompare != 0)
                {
                    return minuteCompare;
                }
                int lineCompare = string.Compare(left.LineId, right.LineId, System.StringComparison.Ordinal);
                return lineCompare != 0 ? lineCompare : string.Compare(left.Id, right.Id, System.StringComparison.Ordinal);
            });

            variants.Add(new PlannerRetimeVariant
            {
                Key = key,
                Rows = rows
            });
        }

        private static List<PlannerWorkingRow> TryRepairOriginDepartureGaps(
            PlannerContext context,
            PlannerRuntimeCatalog runtimeCatalog,
            List<PlannerWorkingRow> baselineRows,
            List<PlannerWorkingRow> candidateRows)
        {
            List<PlannerWorkingRow> rows = CloneWorkingRows(candidateRows);
            if (rows.Count <= 1)
            {
                return rows;
            }

            Dictionary<string, PlannerWorkingRow> baselineById = (baselineRows ?? new List<PlannerWorkingRow>())
                .Where(row => row != null && !string.IsNullOrEmpty(row.Id))
                .ToDictionary(row => row.Id, System.StringComparer.Ordinal);

            for (int iteration = 0; iteration < 24; iteration++)
            {
                PlannerOriginGapIssue issue = FindWorstOriginDepartureGapIssue(rows, runtimeCatalog);
                if (issue == null)
                {
                    return rows;
                }

                if (!TryResolveOriginDepartureGapIssue(context, baselineById, rows, issue))
                {
                    return null;
                }
            }

            return FindWorstOriginDepartureGapIssue(rows, runtimeCatalog) == null ? rows : null;
        }

        private static PlannerOriginGapIssue FindWorstOriginDepartureGapIssue(
            List<PlannerWorkingRow> rows,
            PlannerRuntimeCatalog runtimeCatalog)
        {
            PlannerOriginGapIssue worst = null;
            Dictionary<string, List<PlannerWorkingRow>> rowsByOrigin = new Dictionary<string, List<PlannerWorkingRow>>(System.StringComparer.Ordinal);
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
                {
                    continue;
                }

                if (!rowsByOrigin.TryGetValue(originStationId, out List<PlannerWorkingRow> departures))
                {
                    departures = new List<PlannerWorkingRow>();
                    rowsByOrigin[originStationId] = departures;
                }
                departures.Add(row);
            }

            foreach (KeyValuePair<string, List<PlannerWorkingRow>> entry in rowsByOrigin)
            {
                PlannerWorkingRow[] ordered = entry.Value
                    .OrderBy(item => item.Minute)
                    .ThenBy(item => item.LineId, System.StringComparer.Ordinal)
                    .ThenBy(item => item.Id, System.StringComparer.Ordinal)
                    .ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    worst = SelectWorseOriginGapIssue(worst, BuildOriginGapIssue(entry.Key, ordered[i - 1], ordered[i]));
                }
                if (ordered.Length > 1 && ordered[0].Minute != ordered[ordered.Length - 1].Minute)
                {
                    worst = SelectWorseOriginGapIssue(worst, BuildOriginGapIssue(entry.Key, ordered[ordered.Length - 1], ordered[0]));
                }
            }

            return worst;
        }

        private static PlannerOriginGapIssue BuildOriginGapIssue(
            string originStationId,
            PlannerWorkingRow previousRow,
            PlannerWorkingRow nextRow)
        {
            int gapMinutes = GetForwardMinuteGap(previousRow.Minute, nextRow.Minute);
            if (gapMinutes >= PlannerDefaults.DefaultMinDepartureGapMinutes)
            {
                return null;
            }

            return new PlannerOriginGapIssue
            {
                OriginStationId = originStationId ?? string.Empty,
                PreviousRowId = previousRow.Id ?? string.Empty,
                NextRowId = nextRow.Id ?? string.Empty,
                GapMinutes = gapMinutes,
                DeficitMinutes = PlannerDefaults.DefaultMinDepartureGapMinutes - gapMinutes
            };
        }

        private static PlannerOriginGapIssue SelectWorseOriginGapIssue(
            PlannerOriginGapIssue current,
            PlannerOriginGapIssue candidate)
        {
            if (candidate == null)
            {
                return current;
            }

            if (current == null)
            {
                return candidate;
            }

            if (candidate.DeficitMinutes != current.DeficitMinutes)
            {
                return candidate.DeficitMinutes > current.DeficitMinutes ? candidate : current;
            }

            return string.Compare(candidate.NextRowId, current.NextRowId, System.StringComparison.Ordinal) < 0
                ? candidate
                : current;
        }

        private static bool TryResolveOriginDepartureGapIssue(
            PlannerContext context,
            Dictionary<string, PlannerWorkingRow> baselineById,
            List<PlannerWorkingRow> rows,
            PlannerOriginGapIssue issue)
        {
            if (context == null
                || baselineById == null
                || rows == null
                || issue == null)
            {
                return false;
            }

            Dictionary<string, PlannerWorkingRow> rowsById = rows
                .Where(row => row != null && !string.IsNullOrEmpty(row.Id))
                .ToDictionary(row => row.Id, System.StringComparer.Ordinal);
            if (!rowsById.TryGetValue(issue.PreviousRowId, out PlannerWorkingRow previousRow)
                || !rowsById.TryGetValue(issue.NextRowId, out PlannerWorkingRow nextRow)
                || !baselineById.TryGetValue(issue.PreviousRowId, out PlannerWorkingRow previousBaseline)
                || !baselineById.TryGetValue(issue.NextRowId, out PlannerWorkingRow nextBaseline))
            {
                return false;
            }

            ResolveAllowedMinuteBounds(context, previousRow, previousBaseline, out int previousMinMinute, out _);
            ResolveAllowedMinuteBounds(context, nextRow, nextBaseline, out _, out int nextMaxMinute);

            int previousEarlierCapacity = System.Math.Max(0, previousRow.Minute - previousMinMinute);
            int nextLaterCapacity = System.Math.Max(0, nextMaxMinute - nextRow.Minute);
            int requiredMinutes = issue.DeficitMinutes;

            if (ApplyOriginGapRepair(previousRow, previousEarlierCapacity, nextRow, nextLaterCapacity, requiredMinutes, preferMoveNextLater: true))
            {
                return true;
            }

            return ApplyOriginGapRepair(previousRow, previousEarlierCapacity, nextRow, nextLaterCapacity, requiredMinutes, preferMoveNextLater: false);
        }

        private static void ResolveAllowedMinuteBounds(
            PlannerContext context,
            PlannerWorkingRow row,
            PlannerWorkingRow baselineRow,
            out int minMinute,
            out int maxMinute)
        {
            int minute = baselineRow?.Minute ?? row?.Minute ?? 0;
            if (row == null || baselineRow == null)
            {
                minMinute = minute;
                maxMinute = minute;
                return;
            }

            bool isTargetExpress = string.Equals(row.Kind, "express", System.StringComparison.OrdinalIgnoreCase)
                && (context?.TargetLineIds ?? new string[0]).Contains(row.LineId ?? string.Empty);
            if (isTargetExpress)
            {
                int budget = System.Math.Max(0, context?.Request?.maxOffsetMinutes ?? 0);
                minMinute = System.Math.Max(0, minute - budget);
                maxMinute = System.Math.Min(1439, minute + budget);
                return;
            }

            bool isAdjustableLocal = (context?.AdjustableLineIds ?? new string[0]).Contains(row.LineId ?? string.Empty);
            if (isAdjustableLocal)
            {
                int budget = System.Math.Max(0, context?.Request?.maxLocalRetimeMinutes ?? 0);
                minMinute = System.Math.Max(0, minute - budget);
                maxMinute = System.Math.Min(1439, minute + budget);
                return;
            }

            minMinute = minute;
            maxMinute = minute;
        }

        private static bool ApplyOriginGapRepair(
            PlannerWorkingRow previousRow,
            int previousEarlierCapacity,
            PlannerWorkingRow nextRow,
            int nextLaterCapacity,
            int requiredMinutes,
            bool preferMoveNextLater)
        {
            int moveNext = 0;
            int movePrevious = 0;
            if (preferMoveNextLater)
            {
                moveNext = System.Math.Min(nextLaterCapacity, requiredMinutes);
                movePrevious = System.Math.Min(previousEarlierCapacity, System.Math.Max(0, requiredMinutes - moveNext));
            }
            else
            {
                movePrevious = System.Math.Min(previousEarlierCapacity, requiredMinutes);
                moveNext = System.Math.Min(nextLaterCapacity, System.Math.Max(0, requiredMinutes - movePrevious));
            }

            if (moveNext + movePrevious < requiredMinutes)
            {
                return false;
            }

            if (movePrevious > 0)
            {
                previousRow.Minute -= movePrevious;
            }
            if (moveNext > 0)
            {
                nextRow.Minute += moveNext;
            }
            return true;
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

        private static List<PlannerWorkingRow> CloneWorkingRows(IEnumerable<PlannerWorkingRow> rows)
        {
            return (rows ?? System.Array.Empty<PlannerWorkingRow>())
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

        private static string BuildRetimeVariantKey(Dictionary<string, int> shiftsByTripId)
        {
            return string.Join("_", shiftsByTripId
                .Where(entry => !string.IsNullOrEmpty(entry.Key) && entry.Value != 0)
                .OrderBy(entry => entry.Key, System.StringComparer.Ordinal)
                .Select(entry => entry.Key.Replace(":", "-").Replace("|", "-") + (entry.Value > 0 ? "+" : "") + entry.Value));
        }

        private static List<string[]> BuildVirtualBypassStationSets(
            PlannerContext context,
            List<PursuitTrunk> pursuitTrunks)
        {
            List<string[]> sets = new List<string[]>();
            HashSet<string> forced = new HashSet<string>(context.ForcedBypassStationIds ?? new string[0], System.StringComparer.Ordinal);
            HashSet<string> configuredStationIds = BuildConfiguredBypassStationIdSet(context);
            string[] forcedVirtualArray = forced
                .Where(stationId => !string.IsNullOrEmpty(stationId) && !configuredStationIds.Contains(stationId))
                .ToArray();
            sets.Add(forcedVirtualArray);

            int maxAdditional = context.Request.maxAdditionalBypassStations;
            if (maxAdditional <= forcedVirtualArray.Length)
            {
                return sets;
            }

            HashSet<string> adjustableLineIds = new HashSet<string>(context.AdjustableLineIds ?? new string[0], System.StringComparer.Ordinal);
            Dictionary<string, float> candidateScores = new Dictionary<string, float>(System.StringComparer.Ordinal);
            foreach (string lineId in adjustableLineIds)
            {
                if (!context.CandidateBypassStationsByLineId.TryGetValue(lineId, out List<PlannerBypassStation> stations))
                {
                    continue;
                }
                for (int i = 0; i < stations.Count; i++)
                {
                    if (stations[i].IsVirtualCandidate
                        && !stations[i].IsConfigured
                        && !forced.Contains(stations[i].StationId)
                        && !candidateScores.ContainsKey(stations[i].StationId))
                    {
                        candidateScores[stations[i].StationId] = ScoreVirtualBypassCandidate(stations[i], pursuitTrunks);
                    }
                }
            }

            List<string> candidates = BuildPrioritizedVirtualBypassCandidates(
                context,
                pursuitTrunks,
                forced,
                candidateScores);
            int candidateLimit = System.Math.Min(candidates.Count, 8);
            for (int i = 0; i < candidateLimit; i++)
            {
                AddStationSet(sets, forcedVirtualArray, new[] { candidates[i] }, maxAdditional);
            }
            foreach (VirtualBypassStationSetOption option in BuildVirtualBypassStationSetOptions(candidates, candidateLimit, candidateScores, 2))
            {
                AddStationSet(sets, forcedVirtualArray, option.StationIds, maxAdditional);
                if (sets.Count >= 16)
                {
                    return sets;
                }
            }
            foreach (VirtualBypassStationSetOption option in BuildVirtualBypassStationSetOptions(candidates, candidateLimit, candidateScores, 3))
            {
                AddStationSet(sets, forcedVirtualArray, option.StationIds, maxAdditional);
                if (sets.Count >= 24)
                {
                    return sets;
                }
            }

            return sets;
        }

        private static HashSet<string> BuildConfiguredBypassStationIdSet(PlannerContext context)
        {
            HashSet<string> configured = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (List<PlannerBypassStation> stations in (context?.ConfiguredBypassStationsByLineId ?? new Dictionary<string, List<PlannerBypassStation>>()).Values)
            {
                foreach (PlannerBypassStation station in stations ?? new List<PlannerBypassStation>())
                {
                    if (!string.IsNullOrEmpty(station?.StationId))
                    {
                        configured.Add(station.StationId);
                    }
                }
            }

            return configured;
        }

        private static List<string> BuildPrioritizedVirtualBypassCandidates(
            PlannerContext context,
            List<PursuitTrunk> pursuitTrunks,
            HashSet<string> forced,
            Dictionary<string, float> candidateScores)
        {
            List<string> prioritized = new List<string>();
            HashSet<string> seen = new HashSet<string>(System.StringComparer.Ordinal);
            Dictionary<string, float> protectedScores = new Dictionary<string, float>(System.StringComparer.Ordinal);
            foreach (PursuitTrunk trunk in pursuitTrunks ?? new List<PursuitTrunk>())
            {
                string stationId = PickBestVirtualBypassCandidateForTrunk(context, trunk, forced, candidateScores);
                if (!string.IsNullOrEmpty(stationId))
                {
                    candidateScores.TryGetValue(stationId, out float score);
                    if (!protectedScores.TryGetValue(stationId, out float current) || score > current)
                    {
                        protectedScores[stationId] = score;
                    }
                }
            }

            foreach (string stationId in protectedScores
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, System.StringComparer.Ordinal)
                .Select(entry => entry.Key))
            {
                if (seen.Add(stationId))
                {
                    prioritized.Add(stationId);
                }
            }

            foreach (string stationId in candidateScores
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, System.StringComparer.Ordinal)
                .Select(entry => entry.Key))
            {
                if (!string.IsNullOrEmpty(stationId) && seen.Add(stationId))
                {
                    prioritized.Add(stationId);
                }
            }

            return prioritized;
        }

        private static string PickBestVirtualBypassCandidateForTrunk(
            PlannerContext context,
            PursuitTrunk trunk,
            HashSet<string> forced,
            Dictionary<string, float> candidateScores)
        {
            if (trunk == null
                || !trunk.IsPrimaryPlanningRisk
                || string.IsNullOrEmpty(trunk.LocalLineId)
                || !context.CandidateBypassStationsByLineId.TryGetValue(trunk.LocalLineId, out List<PlannerBypassStation> stations))
            {
                return string.Empty;
            }

            PlannerBypassStation bestStation = null;
            float bestScore = float.MinValue;
            int tolerance = PlannerDefaults.BypassStationEndpointToleranceAtoms;
            int midpoint = (trunk.LocalStartAtomIndex + trunk.LocalEndAtomIndexExclusive) / 2;
            for (int i = 0; i < stations.Count; i++)
            {
                PlannerBypassStation station = stations[i];
                if (station == null
                    || !station.IsVirtualCandidate
                    || station.IsConfigured
                    || forced.Contains(station.StationId)
                    || station.TrackAtomIndex < trunk.LocalStartAtomIndex - tolerance
                    || station.TrackAtomIndex > trunk.LocalEndAtomIndexExclusive + tolerance)
                {
                    continue;
                }

                candidateScores.TryGetValue(station.StationId, out float score);
                if (bestStation == null
                    || score > bestScore
                    || (score == bestScore && System.Math.Abs(station.TrackAtomIndex - midpoint) < System.Math.Abs(bestStation.TrackAtomIndex - midpoint))
                    || (score == bestScore && station.TrackAtomIndex == bestStation.TrackAtomIndex && string.Compare(station.StationId, bestStation.StationId, System.StringComparison.Ordinal) < 0))
                {
                    bestStation = station;
                    bestScore = score;
                }
            }

            return bestStation?.StationId ?? string.Empty;
        }

        private static List<VirtualBypassStationSetOption> BuildVirtualBypassStationSetOptions(
            List<string> candidates,
            int candidateLimit,
            Dictionary<string, float> candidateScores,
            int setSize)
        {
            List<VirtualBypassStationSetOption> options = new List<VirtualBypassStationSetOption>();
            if (setSize == 2)
            {
                for (int i = 0; i < candidateLimit; i++)
                {
                    for (int j = i + 1; j < candidateLimit; j++)
                    {
                        options.Add(CreateVirtualBypassStationSetOption(
                            new[] { candidates[i], candidates[j] },
                            candidateScores));
                    }
                }
            }
            else if (setSize == 3)
            {
                for (int i = 0; i < candidateLimit; i++)
                {
                    for (int j = i + 1; j < candidateLimit; j++)
                    {
                        for (int k = j + 1; k < candidateLimit; k++)
                        {
                            options.Add(CreateVirtualBypassStationSetOption(
                                new[] { candidates[i], candidates[j], candidates[k] },
                                candidateScores));
                        }
                    }
                }
            }

            options.Sort((left, right) =>
            {
                int scoreCompare = right.Score.CompareTo(left.Score);
                if (scoreCompare != 0)
                {
                    return scoreCompare;
                }

                return string.Compare(left.Key, right.Key, System.StringComparison.Ordinal);
            });
            return options;
        }

        private static VirtualBypassStationSetOption CreateVirtualBypassStationSetOption(
            string[] stationIds,
            Dictionary<string, float> candidateScores)
        {
            float score = 0f;
            for (int i = 0; i < stationIds.Length; i++)
            {
                candidateScores.TryGetValue(stationIds[i], out float stationScore);
                score += stationScore;
            }

            return new VirtualBypassStationSetOption
            {
                StationIds = stationIds,
                Score = score,
                Key = string.Join("|", stationIds.OrderBy(stationId => stationId, System.StringComparer.Ordinal))
            };
        }

        private static float ScoreVirtualBypassCandidate(
            PlannerBypassStation station,
            List<PursuitTrunk> pursuitTrunks)
        {
            float score = 0f;
            foreach (PursuitTrunk trunk in pursuitTrunks ?? new List<PursuitTrunk>())
            {
                if (trunk == null
                    || !string.Equals(trunk.LocalLineId, station.LineId, System.StringComparison.Ordinal)
                    || station.TrackAtomIndex < trunk.LocalStartAtomIndex
                    || station.TrackAtomIndex > trunk.LocalEndAtomIndexExclusive)
                {
                    continue;
                }

                score += trunk.IsPrimaryPlanningRisk ? 4f : 1f;
                int length = System.Math.Max(1, trunk.LocalEndAtomIndexExclusive - trunk.LocalStartAtomIndex);
                float relative = (station.TrackAtomIndex - trunk.LocalStartAtomIndex) / (float)length;
                score += System.Math.Max(0f, 1f - relative);
            }

            return score;
        }

        private static void AddStationSet(List<string[]> sets, string[] baseStationIds, string[] addedStationIds, int maxAdditional)
        {
            string[] merged = baseStationIds
                .Concat(addedStationIds)
                .Where(stationId => !string.IsNullOrEmpty(stationId))
                .Distinct(System.StringComparer.Ordinal)
                .ToArray();
            if (merged.Length > maxAdditional)
            {
                return;
            }
            string key = string.Join("|", merged.OrderBy(stationId => stationId));
            for (int i = 0; i < sets.Count; i++)
            {
                if (string.Join("|", sets[i].OrderBy(stationId => stationId)) == key)
                {
                    return;
                }
            }
            sets.Add(merged);
        }

        private sealed class PlannerOriginGapIssue
        {
            public string OriginStationId = string.Empty;
            public string PreviousRowId = string.Empty;
            public string NextRowId = string.Empty;
            public int GapMinutes = 0;
            public int DeficitMinutes = 0;
        }

        private sealed class PlannerSearchEvaluation
        {
            public List<PlannerCatchupEvent> CatchupEvents = new List<PlannerCatchupEvent>();
            public List<PlannerRiskCluster> RiskClusters = new List<PlannerRiskCluster>();
        }

        private sealed class PlannerRetimeVariant
        {
            public string Key = string.Empty;
            public List<PlannerWorkingRow> Rows = new List<PlannerWorkingRow>();
        }

        private sealed class VirtualBypassStationSetOption
        {
            public string[] StationIds = new string[0];
            public float Score = 0f;
            public string Key = string.Empty;
        }
    }
}
