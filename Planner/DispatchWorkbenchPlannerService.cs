using System.Collections.Generic;
using System.Linq;

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
        private readonly PlannerResultProjector m_Projector = new PlannerResultProjector();

        public DepartureControlSystem.DispatchPlannerResult Execute(
            DepartureControlSystem.DispatchPlannerExportSnapshot snapshot,
            DepartureControlSystem.DispatchPlannerRequest request)
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

            List<int> offsetVariants = BuildExpressOffsetVariants(context);
            List<string[]> stationSets = BuildVirtualBypassStationSets(context, state.PursuitTrunks);
            if (stationSets.Count == 0)
            {
                stationSets.Add(new string[0]);
            }

            List<PlannerPlanModel> candidatePlans = new List<PlannerPlanModel>();
            for (int offsetIndex = 0; offsetIndex < offsetVariants.Count; offsetIndex++)
            {
                int offsetMinutes = offsetVariants[offsetIndex];
                context.ActiveExpressOffsetMinutes = offsetMinutes;
                List<PlannerWorkingRow> offsetWorkingRows = BuildOffsetWorkingRows(context, baseWorkingRows, offsetMinutes);
                for (int stationSetIndex = 0; stationSetIndex < stationSets.Count; stationSetIndex++)
                {
                    context.ActiveVirtualBypassStationIds = stationSets[stationSetIndex];
                    context.WorkingRows = offsetWorkingRows;
                    List<PlannerCatchupEvent> catchupEvents = m_CatchupDetector.Detect(context, state.RuntimeCatalog, state.PursuitTrunks);
                    List<PlannerRiskCluster> riskClusters = m_RegionBuilder.BuildRiskClusters(context, catchupEvents);
                    m_BypassEvaluator.Enrich(riskClusters, context);
                    List<PlannerPlanModel> plans = m_Search.BuildInitialPlans(
                        context,
                        riskClusters,
                        catchupEvents,
                        state.Diagnostics,
                        state.RuntimeCatalog,
                        stationSets[stationSetIndex],
                        offsetMinutes,
                        string.Empty,
                        baseWorkingRows);
                    candidatePlans.AddRange(plans);
                    if (offsetIndex == 0 && stationSetIndex == 0)
                    {
                        state.CatchupEvents = catchupEvents;
                        state.RiskClusters = riskClusters;
                        state.OptimizationRegions = m_RegionBuilder.BuildOptimizationRegions(riskClusters);
                        state.Trips = m_CatchupDetector.BuildTrips(context, state.RuntimeCatalog);
                    }

                    List<PlannerRetimeVariant> retimeVariants = BuildLocalRetimeVariants(context, offsetWorkingRows, catchupEvents);
                    for (int retimeIndex = 0; retimeIndex < retimeVariants.Count; retimeIndex++)
                    {
                        PlannerRetimeVariant retimeVariant = retimeVariants[retimeIndex];
                        context.WorkingRows = retimeVariant.Rows;
                        List<PlannerCatchupEvent> retimedCatchupEvents = m_CatchupDetector.Detect(context, state.RuntimeCatalog, state.PursuitTrunks);
                        List<PlannerRiskCluster> retimedRiskClusters = m_RegionBuilder.BuildRiskClusters(context, retimedCatchupEvents);
                        m_BypassEvaluator.Enrich(retimedRiskClusters, context);
                        candidatePlans.AddRange(m_Search.BuildInitialPlans(
                            context,
                            retimedRiskClusters,
                            retimedCatchupEvents,
                            state.Diagnostics,
                            state.RuntimeCatalog,
                            stationSets[stationSetIndex],
                            offsetMinutes,
                            retimeVariant.Key,
                            baseWorkingRows));
                    }
                }
            }
            for (int i = 0; i < candidatePlans.Count; i++)
            {
                m_Scorer.Apply(candidatePlans[i]);
            }
            state.Plans = SelectBestPlansByObjective(candidatePlans);

            if (state.Diagnostics.Count == 0)
            {
                state.Diagnostics.Add(PlannerDiagnosticFactory.Create(
                    "info",
                    "BACKEND_ANALYSIS_READY",
                    "Backend planner analysis completed successfully."));
            }

            return m_Projector.Project(state);
        }

        private static int ComparePlans(PlannerPlanModel left, PlannerPlanModel right)
        {
            int infeasibleCompare = GetInfeasibleRank(left).CompareTo(GetInfeasibleRank(right));
            if (infeasibleCompare != 0)
            {
                return infeasibleCompare;
            }

            int scoreCompare = right.Score.CompareTo(left.Score);
            if (scoreCompare != 0)
            {
                return scoreCompare;
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
            HashSet<string> usedPlanSignatures = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (PlannerObjectiveDefinition objective in PlannerDefaults.Objectives)
            {
                if (!plansByObjective.TryGetValue(objective.Id, out List<PlannerPlanModel> objectivePlans)
                    || objectivePlans.Count == 0)
                {
                    continue;
                }

                objectivePlans.Sort(ComparePlans);
                PlannerPlanModel selectedPlan = null;
                for (int index = 0; index < objectivePlans.Count; index++)
                {
                    PlannerPlanModel candidate = objectivePlans[index];
                    string signature = BuildPlanSignature(candidate);
                    if (usedPlanSignatures.Add(signature))
                    {
                        selectedPlan = candidate;
                        break;
                    }
                }

                if (selectedPlan == null)
                {
                    continue;
                }

                selectedPlans.Add(selectedPlan);
            }

            return selectedPlans;
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

        private static List<PlannerRetimeVariant> BuildLocalRetimeVariants(
            PlannerContext context,
            List<PlannerWorkingRow> baseRows,
            List<PlannerCatchupEvent> catchupEvents)
        {
            int maxRetimeMinutes = System.Math.Max(0, context.Request.maxLocalRetimeMinutes);
            if (maxRetimeMinutes <= 0)
            {
                return new List<PlannerRetimeVariant>();
            }

            HashSet<string> adjustableLineIds = new HashSet<string>(context.AdjustableLineIds ?? new string[0], System.StringComparer.Ordinal);
            Dictionary<string, int> shiftsByTripId = new Dictionary<string, int>(System.StringComparer.Ordinal);
            List<PlannerRetimeVariant> variants = new List<PlannerRetimeVariant>();
            HashSet<string> variantKeys = new HashSet<string>(System.StringComparer.Ordinal);

            PlannerCatchupEvent[] orderedEvents = (catchupEvents ?? new List<PlannerCatchupEvent>())
                .Where(item =>
                    item != null
                    && !string.IsNullOrEmpty(item.LocalTripId)
                    && adjustableLineIds.Contains(item.LocalLineId))
                .OrderByDescending(item => item.UnresolvedRiskMinutes + item.RobustnessRiskMinutes)
                .ThenByDescending(item => item.RequiredHoldMinutes)
                .Take(8)
                .ToArray();

            foreach (PlannerCatchupEvent catchupEvent in orderedEvents)
            {
                int deltaMinutes = ResolveRetimeDeltaMinutes(catchupEvent, maxRetimeMinutes);
                if (deltaMinutes <= 0)
                {
                    continue;
                }

                AddRetimeVariant(variants, variantKeys, baseRows, catchupEvent.LocalTripId, -deltaMinutes);
                AddRetimeVariant(variants, variantKeys, baseRows, catchupEvent.LocalTripId, deltaMinutes);

                if (!shiftsByTripId.ContainsKey(catchupEvent.LocalTripId))
                {
                    shiftsByTripId[catchupEvent.LocalTripId] = -deltaMinutes;
                }
            }

            if (shiftsByTripId.Count > 1)
            {
                AddRetimeVariant(variants, variantKeys, baseRows, shiftsByTripId);
            }

            return variants;
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
            string[] forcedArray = forced.Where(stationId => !string.IsNullOrEmpty(stationId)).ToArray();
            sets.Add(forcedArray);

            int maxAdditional = context.Request.maxAdditionalBypassStations;
            if (maxAdditional <= forcedArray.Length)
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

            List<string> candidates = candidateScores
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, System.StringComparer.Ordinal)
                .Select(entry => entry.Key)
                .ToList();
            int candidateLimit = System.Math.Min(candidates.Count, 6);
            for (int i = 0; i < candidateLimit; i++)
            {
                AddStationSet(sets, forcedArray, new[] { candidates[i] }, maxAdditional);
            }
            for (int i = 0; i < candidateLimit; i++)
            {
                for (int j = i + 1; j < candidateLimit; j++)
                {
                    AddStationSet(sets, forcedArray, new[] { candidates[i], candidates[j] }, maxAdditional);
                    if (sets.Count >= 16)
                    {
                        return sets;
                    }
                }
            }
            for (int i = 0; i < candidateLimit; i++)
            {
                for (int j = i + 1; j < candidateLimit; j++)
                {
                    for (int k = j + 1; k < candidateLimit; k++)
                    {
                        AddStationSet(sets, forcedArray, new[] { candidates[i], candidates[j], candidates[k] }, maxAdditional);
                        if (sets.Count >= 24)
                        {
                            return sets;
                        }
                    }
                }
            }

            return sets;
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

        private sealed class PlannerRetimeVariant
        {
            public string Key = string.Empty;
            public List<PlannerWorkingRow> Rows = new List<PlannerWorkingRow>();
        }
    }
}
