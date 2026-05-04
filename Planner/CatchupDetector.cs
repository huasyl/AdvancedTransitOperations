using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class CatchupDetector
    {
        public List<PlannerCatchupEvent> Detect(
            PlannerContext context,
            PlannerRuntimeCatalog runtimeCatalog,
            List<PursuitTrunk> pursuitTrunks)
        {
            List<PlannerTripModel> trips = BuildTrips(context, runtimeCatalog);
            Dictionary<string, List<PlannerTripModel>> tripsByLineId = new Dictionary<string, List<PlannerTripModel>>(StringComparer.Ordinal);
            for (int i = 0; i < trips.Count; i++)
            {
                PlannerTripModel trip = trips[i];
                if (!tripsByLineId.TryGetValue(trip.LineId, out List<PlannerTripModel> lineTrips))
                {
                    lineTrips = new List<PlannerTripModel>();
                    tripsByLineId[trip.LineId] = lineTrips;
                }
                lineTrips.Add(trip);
            }

            List<PlannerCatchupEvent> events = new List<PlannerCatchupEvent>();
            foreach (PursuitTrunk trunk in pursuitTrunks)
            {
                if (!runtimeCatalog.ModelsByLineId.TryGetValue(trunk.LocalLineId, out PlannerLineRuntimeModel localModel)
                    || !runtimeCatalog.ModelsByLineId.TryGetValue(trunk.ExpressLineId, out PlannerLineRuntimeModel expressModel))
                {
                    continue;
                }

                EnrichTrunkOffsets(trunk, localModel, expressModel);
                List<PlannerTripModel> localTrips = tripsByLineId.TryGetValue(trunk.LocalLineId, out List<PlannerTripModel> resolvedLocalTrips)
                    ? resolvedLocalTrips
                    : new List<PlannerTripModel>();
                List<PlannerTripModel> expressTrips = tripsByLineId.TryGetValue(trunk.ExpressLineId, out List<PlannerTripModel> resolvedExpressTrips)
                    ? resolvedExpressTrips
                    : new List<PlannerTripModel>();
                List<PlannerBypassStation> corridorStations = CollectBypassStations(context, trunk);
                float holdBudgetMinutes = ResolveHoldBudgetMinutes(context, localModel);

                for (int localIndex = 0; localIndex < localTrips.Count; localIndex++)
                {
                    PlannerTripModel localTrip = localTrips[localIndex];
                    PlannerCorridorWindow localWindow = BuildCorridorWindow(localTrip, trunk, true);
                    for (int expressIndex = 0; expressIndex < expressTrips.Count; expressIndex++)
                    {
                        PlannerTripModel expressTrip = expressTrips[expressIndex];
                        PlannerCorridorWindow expressWindow = BuildCorridorWindow(expressTrip, trunk, false);
                        if (!ShouldEvaluateTripPair(localWindow, expressWindow, PlannerDefaults.MinSharedGapMinutes))
                        {
                            continue;
                        }

                        PlannerGapProfile gapProfile = ComputeGapProfile(
                            BuildTripCorridorCurve(localTrip, trunk, true),
                            BuildTripCorridorCurve(expressTrip, trunk, false));
                        PlannerCatchupPoint catchupPoint = FindCatchupPoint(gapProfile, PlannerDefaults.MinSharedGapMinutes);
                        if (catchupPoint == null)
                        {
                            continue;
                        }

                        PlannerBypassEvaluation selectedBypass = PickBestBypassStation(
                            context,
                            localTrip,
                            expressTrip,
                            trunk,
                            gapProfile,
                            catchupPoint,
                            corridorStations,
                            holdBudgetMinutes);
                        bool canUseBypass = selectedBypass != null;
                        float requiredHoldMinutes = canUseBypass ? selectedBypass.HoldNeededMinutes : catchupPoint.SeverityMinutes;
                        float targetHoldMinutes = canUseBypass
                            ? Math.Max(requiredHoldMinutes, catchupPoint.RobustnessRiskMinutes)
                            : 0f;
                        float resolvedHoldMinutes = canUseBypass ? Math.Min(targetHoldMinutes, holdBudgetMinutes) : 0f;
                        float unresolvedRiskMinutes = canUseBypass
                            ? Math.Max(0f, requiredHoldMinutes - resolvedHoldMinutes)
                            : Math.Max(requiredHoldMinutes, catchupPoint.ClosingMinutes * 0.5f);
                        float robustnessRiskMinutes = canUseBypass
                            ? Math.Max(0f, catchupPoint.RobustnessRiskMinutes - resolvedHoldMinutes)
                            : catchupPoint.RobustnessRiskMinutes;

                        PlannerCatchupEvent catchupEvent = new PlannerCatchupEvent();
                        catchupEvent.EventId = expressTrip.TripId + "|" + localTrip.TripId + "|" + trunk.TrunkId;
                        catchupEvent.LocalTripId = localTrip.TripId;
                        catchupEvent.ExpressTripId = expressTrip.TripId;
                        catchupEvent.YieldingTripId = localTrip.TripId;
                        catchupEvent.PriorityTripId = expressTrip.TripId;
                        catchupEvent.LocalLineId = trunk.LocalLineId;
                        catchupEvent.ExpressLineId = trunk.ExpressLineId;
                        catchupEvent.YieldingLineId = string.IsNullOrEmpty(trunk.YieldingLineId) ? trunk.LocalLineId : trunk.YieldingLineId;
                        catchupEvent.PriorityLineId = string.IsNullOrEmpty(trunk.PriorityLineId) ? trunk.ExpressLineId : trunk.PriorityLineId;
                        catchupEvent.TrunkId = trunk.TrunkId;
                        catchupEvent.FromStationId = trunk.FromStationId;
                        catchupEvent.ToStationId = trunk.ToStationId;
                        catchupEvent.LocalEntryMinute = localWindow.EntryMinute;
                        catchupEvent.ExpressEntryMinute = expressWindow.EntryMinute;
                        catchupEvent.LocalExitMinute = localWindow.ExitMinute;
                        catchupEvent.ExpressExitMinute = expressWindow.ExitMinute;
                        catchupEvent.GapAtEntryMinutes = catchupPoint.EntryGapMinutes;
                        catchupEvent.GapAtExitMinutes = catchupPoint.ExitGapMinutes;
                        catchupEvent.ClosingMinutes = catchupPoint.ClosingMinutes;
                        catchupEvent.MinSharedGapMinutes = PlannerDefaults.MinSharedGapMinutes;
                        catchupEvent.MinGapMinutes = catchupPoint.MinGapMinutes;
                        catchupEvent.MinGapUncertaintyMinutes = catchupPoint.MinGapUncertaintyMinutes;
                        catchupEvent.WorstCaseGapMinutes = catchupPoint.WorstCaseGapMinutes;
                        catchupEvent.SeverityMinutes = catchupPoint.SeverityMinutes;
                        catchupEvent.UnresolvedRiskMinutes = PlannerMath.Round2(unresolvedRiskMinutes);
                        catchupEvent.RobustnessRiskMinutes = PlannerMath.Round2(robustnessRiskMinutes);
                        catchupEvent.RequiredHoldMinutes = PlannerMath.Round2(requiredHoldMinutes);
                        catchupEvent.HoldBudgetMinutes = PlannerMath.Round2(holdBudgetMinutes);
                        catchupEvent.ResolvedHoldMinutes = PlannerMath.Round2(resolvedHoldMinutes);
                        catchupEvent.ExpressSavedMinutes = PlannerMath.Round2(Math.Max(0f, trunk.LocalRuntimeMinutes - trunk.ExpressRuntimeMinutes));
                        catchupEvent.CatchupMinute = catchupPoint.CatchupMinute;
                        catchupEvent.CatchupAxisIndex = catchupPoint.CatchupAxisIndex;
                        catchupEvent.DidCatchUp = catchupPoint.DidCatchUp;
                        catchupEvent.WithinHoldBudget = resolvedHoldMinutes >= requiredHoldMinutes;
                        catchupEvent.Confidence = trunk.Confidence;
                        catchupEvent.SelectedBypassStation = selectedBypass;
                        catchupEvent.UsableBypassStations = corridorStations;
                        catchupEvent.SourceCorridorIds = new List<string>(trunk.SourceCorridorIds);
                        events.Add(catchupEvent);
                    }
                }
            }

            return MergeCatchupEvents(events);
        }

        public List<PlannerTripModel> BuildTrips(PlannerContext context, PlannerRuntimeCatalog runtimeCatalog)
        {
            List<PlannerTripModel> trips = new List<PlannerTripModel>();
            foreach (PlannerWorkingRow row in context.WorkingRows)
            {
                if (!runtimeCatalog.ModelsByLineId.TryGetValue(row.LineId, out PlannerLineRuntimeModel runtimeModel))
                {
                    continue;
                }

                PlannerTripModel trip = new PlannerTripModel();
                trip.TripId = row.Id;
                trip.LineId = row.LineId;
                trip.Kind = row.Kind;
                trip.DepartureMinute = row.Minute;
                trip.DepartureTime = PlannerMath.MinutesToTime(row.Minute);
                trip.Source = row.Source;
                trip.Note = row.Note;
                trip.AtomBoundaryMinuteOffsets = runtimeModel.AtomBoundaryMinuteOffsets;
                trip.AtomBoundaryVariabilityOffsets = runtimeModel.AtomBoundaryVariabilityOffsets;
                for (int index = 0; index < runtimeModel.StationOffsets.Count; index++)
                {
                    PlannerStationOffset offset = runtimeModel.StationOffsets[index];
                    trip.StationEvents.Add(new PlannerStationEvent
                    {
                        StationId = offset.StationId,
                        Order = offset.Order,
                        Name = offset.Name,
                        ArrivalMinute = PlannerMath.Round4(row.Minute + offset.ArrivalMinute),
                        DepartureMinute = PlannerMath.Round4(row.Minute + offset.DepartureMinute),
                        DwellMinutes = offset.DwellMinutes,
                        SkippedStopStartLossMinutes = offset.SkippedStopStartLossMinutes
                    });
                }
                trips.Add(trip);
            }

            return trips;
        }

        private static void EnrichTrunkOffsets(
            PursuitTrunk trunk,
            PlannerLineRuntimeModel localModel,
            PlannerLineRuntimeModel expressModel)
        {
            trunk.LocalEntryOffsetMinutes = GetAtomBoundaryMinuteOffset(localModel.AtomBoundaryMinuteOffsets, trunk.LocalStartAtomIndex);
            trunk.LocalExitOffsetMinutes = GetAtomBoundaryMinuteOffset(localModel.AtomBoundaryMinuteOffsets, trunk.LocalEndAtomIndexExclusive);
            trunk.ExpressEntryOffsetMinutes = GetAtomBoundaryMinuteOffset(expressModel.AtomBoundaryMinuteOffsets, trunk.ExpressStartAtomIndex);
            trunk.ExpressExitOffsetMinutes = GetAtomBoundaryMinuteOffset(expressModel.AtomBoundaryMinuteOffsets, trunk.ExpressEndAtomIndexExclusive);
            trunk.LocalRuntimeMinutes = PlannerMath.Round2(Math.Max(0f, trunk.LocalExitOffsetMinutes - trunk.LocalEntryOffsetMinutes));
            trunk.ExpressRuntimeMinutes = PlannerMath.Round2(Math.Max(0f, trunk.ExpressExitOffsetMinutes - trunk.ExpressEntryOffsetMinutes));

            int localLength = Math.Max(1, trunk.LocalEndAtomIndexExclusive - trunk.LocalStartAtomIndex);
            int expressLength = Math.Max(1, trunk.ExpressEndAtomIndexExclusive - trunk.ExpressStartAtomIndex);
            int atomSampleCount = Math.Max(1, Math.Min(Math.Max(localLength, expressLength), Math.Min(localLength, expressLength)));
            float runtimeMinutes = Math.Max(trunk.LocalRuntimeMinutes, trunk.ExpressRuntimeMinutes);
            int runtimeSampleCount = runtimeMinutes > 0f
                ? (int)Math.Ceiling(runtimeMinutes / PlannerDefaults.PursuitCurveSampleStepMinutes)
                : atomSampleCount;
            trunk.AxisSampleCount = Math.Max(2, Math.Min(atomSampleCount, runtimeSampleCount));
        }

        private static PlannerCorridorWindow BuildCorridorWindow(PlannerTripModel trip, PursuitTrunk trunk, bool local)
        {
            int startAtomIndex = local ? trunk.LocalStartAtomIndex : trunk.ExpressStartAtomIndex;
            int endAtomIndexExclusive = local ? trunk.LocalEndAtomIndexExclusive : trunk.ExpressEndAtomIndexExclusive;
            if (startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex)
            {
                return null;
            }

            float entryOffsetMinutes = GetAtomBoundaryMinuteOffset(trip.AtomBoundaryMinuteOffsets, startAtomIndex);
            float exitOffsetMinutes = GetAtomBoundaryMinuteOffset(trip.AtomBoundaryMinuteOffsets, endAtomIndexExclusive);
            PlannerCorridorWindow window = new PlannerCorridorWindow();
            window.EntryMinute = trip.DepartureMinute + entryOffsetMinutes;
            window.ExitMinute = trip.DepartureMinute + exitOffsetMinutes;
            window.RuntimeMinutes = PlannerMath.Round2(Math.Max(0f, exitOffsetMinutes - entryOffsetMinutes));
            return window;
        }

        private static bool ShouldEvaluateTripPair(
            PlannerCorridorWindow localWindow,
            PlannerCorridorWindow expressWindow,
            float minSharedGapMinutes)
        {
            if (localWindow == null || expressWindow == null)
            {
                return false;
            }

            float entryGapMinutes = expressWindow.EntryMinute - localWindow.EntryMinute;
            if (!(entryGapMinutes > 0f))
            {
                return false;
            }
            if (expressWindow.EntryMinute >= localWindow.ExitMinute)
            {
                return false;
            }

            float closingCapacityMinutes = Math.Max(0f, localWindow.RuntimeMinutes - expressWindow.RuntimeMinutes);
            float safetyMarginMinutes = minSharedGapMinutes + 2f + 6f;
            return entryGapMinutes <= closingCapacityMinutes + safetyMarginMinutes;
        }

        private static PlannerCurve BuildTripCorridorCurve(PlannerTripModel trip, PursuitTrunk trunk, bool local)
        {
            PlannerCurve curve = new PlannerCurve();
            curve.Samples = new List<PlannerCurveSample>();
            int sampleCount = Math.Max(1, trunk.AxisSampleCount);
            for (int axisIndex = 0; axisIndex <= sampleCount; axisIndex++)
            {
                int atomIndex = MapAxisToAtomIndex(trunk, local, axisIndex, sampleCount);
                float minuteOffset = GetAtomBoundaryMinuteOffset(trip.AtomBoundaryMinuteOffsets, atomIndex);
                float variabilityMinutes = GetAtomBoundaryMinuteOffset(trip.AtomBoundaryVariabilityOffsets, atomIndex);
                curve.Samples.Add(new PlannerCurveSample
                {
                    AxisIndex = axisIndex,
                    Minute = PlannerMath.Round4(trip.DepartureMinute + minuteOffset),
                    VariabilityMinutes = PlannerMath.Round4(variabilityMinutes)
                });
            }
            return curve;
        }

        private static int MapAxisToAtomIndex(PursuitTrunk trunk, bool local, int axisIndex, int axisSampleCount)
        {
            int startAtomIndex = local ? trunk.LocalStartAtomIndex : trunk.ExpressStartAtomIndex;
            int endAtomIndexExclusive = local ? trunk.LocalEndAtomIndexExclusive : trunk.ExpressEndAtomIndexExclusive;
            int lengthAtoms = Math.Max(1, endAtomIndexExclusive - startAtomIndex);
            float ratio = axisSampleCount <= 0 ? 0f : (float)axisIndex / axisSampleCount;
            int atomOffset = (int)Math.Round(lengthAtoms * ratio);
            return Math.Max(startAtomIndex, Math.Min(endAtomIndexExclusive, startAtomIndex + atomOffset));
        }

        private static PlannerGapProfile ComputeGapProfile(PlannerCurve localCurve, PlannerCurve expressCurve)
        {
            if (localCurve == null || expressCurve == null)
            {
                return null;
            }

            int sampleCount = Math.Min(localCurve.Samples.Count, expressCurve.Samples.Count);
            if (sampleCount <= 0)
            {
                return null;
            }

            PlannerGapProfile profile = new PlannerGapProfile();
            profile.Samples = new List<PlannerGapSample>();
            float minGapMinutes = float.PositiveInfinity;
            int minGapAxisIndex = -1;
            float minGapMinute = 0f;
            float minGapUncertaintyMinutes = 0f;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                PlannerCurveSample localSample = localCurve.Samples[sampleIndex];
                PlannerCurveSample expressSample = expressCurve.Samples[sampleIndex];
                float gapMinutes = PlannerMath.Round4(expressSample.Minute - localSample.Minute);
                float minute = PlannerMath.Round4((localSample.Minute + expressSample.Minute) * 0.5f);
                float uncertaintyMinutes = PlannerMath.Round4(
                    Math.Max(0f, localSample.VariabilityMinutes) + Math.Max(0f, expressSample.VariabilityMinutes));
                profile.Samples.Add(new PlannerGapSample
                {
                    AxisIndex = sampleIndex,
                    Minute = minute,
                    LocalMinute = localSample.Minute,
                    ExpressMinute = expressSample.Minute,
                    GapMinutes = gapMinutes,
                    UncertaintyMinutes = uncertaintyMinutes
                });

                if (gapMinutes < minGapMinutes)
                {
                    minGapMinutes = gapMinutes;
                    minGapAxisIndex = sampleIndex;
                    minGapMinute = minute;
                    minGapUncertaintyMinutes = uncertaintyMinutes;
                }
            }

            profile.EntryGapMinutes = PlannerMath.Round2(profile.Samples[0].GapMinutes);
            profile.ExitGapMinutes = PlannerMath.Round2(profile.Samples[profile.Samples.Count - 1].GapMinutes);
            profile.MinGapMinutes = PlannerMath.Round2(minGapMinutes);
            profile.MinGapAxisIndex = minGapAxisIndex;
            profile.MinGapMinute = PlannerMath.Round2(minGapMinute);
            profile.MinGapUncertaintyMinutes = PlannerMath.Round2(minGapUncertaintyMinutes);
            return profile;
        }

        private static PlannerCatchupPoint FindCatchupPoint(PlannerGapProfile profile, float minSharedGapMinutes)
        {
            if (profile == null || profile.Samples.Count == 0 || profile.EntryGapMinutes <= 0f)
            {
                return null;
            }

            float severityMinutes = Math.Max(0f, minSharedGapMinutes - profile.MinGapMinutes);
            float effectiveUncertaintyMinutes = Math.Max(0f, profile.MinGapUncertaintyMinutes) * 0.5f;
            float worstCaseGapMinutes = PlannerMath.Round2(profile.MinGapMinutes - effectiveUncertaintyMinutes);
            float robustnessRiskMinutes = PlannerMath.Round2(Math.Max(0f, PlannerDefaults.RobustnessMarginTargetMinutes - worstCaseGapMinutes));
            bool didCatchUp = profile.MinGapMinutes <= 0f;
            bool didThreaten = severityMinutes > 0f;
            bool didRobustnessThreaten = robustnessRiskMinutes > 0f
                && profile.MinGapMinutes < PlannerDefaults.RobustnessMarginTargetMinutes;
            if (!didCatchUp && !didThreaten && !didRobustnessThreaten)
            {
                return null;
            }

            PlannerCatchupPoint point = new PlannerCatchupPoint();
            point.CatchupAxisIndex = profile.MinGapAxisIndex;
            point.CatchupMinute = profile.MinGapMinute;
            point.MinGapMinutes = profile.MinGapMinutes;
            point.MinGapUncertaintyMinutes = profile.MinGapUncertaintyMinutes;
            point.EntryGapMinutes = profile.EntryGapMinutes;
            point.ExitGapMinutes = profile.ExitGapMinutes;
            point.ClosingMinutes = PlannerMath.Round2(profile.EntryGapMinutes - profile.MinGapMinutes);
            point.SeverityMinutes = PlannerMath.Round2(severityMinutes);
            point.WorstCaseGapMinutes = worstCaseGapMinutes;
            point.RobustnessRiskMinutes = robustnessRiskMinutes;
            point.DidCatchUp = didCatchUp;
            return point;
        }

        private static List<PlannerBypassStation> CollectBypassStations(PlannerContext context, PursuitTrunk trunk)
        {
            Dictionary<string, PlannerBypassStation> stationsById = new Dictionary<string, PlannerBypassStation>(StringComparer.Ordinal);
            AddBypassStations(stationsById, context.ConfiguredBypassStationsByLineId, trunk);
            AddBypassStations(stationsById, context.CandidateBypassStationsByLineId, trunk, BuildAllowedVirtualBypassStationSet(context));
            return stationsById.Values.OrderBy(station => station.Order).ToList();
        }

        private static HashSet<string> BuildAllowedVirtualBypassStationSet(PlannerContext context)
        {
            HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string stationId in context.ActiveVirtualBypassStationIds ?? new string[0])
            {
                if (!string.IsNullOrEmpty(stationId))
                {
                    allowed.Add(stationId);
                }
            }
            foreach (string stationId in context.ForcedBypassStationIds ?? new string[0])
            {
                if (!string.IsNullOrEmpty(stationId))
                {
                    allowed.Add(stationId);
                }
            }
            return allowed;
        }

        private static void AddBypassStations(
            Dictionary<string, PlannerBypassStation> stationsById,
            Dictionary<string, List<PlannerBypassStation>> source,
            PursuitTrunk trunk,
            HashSet<string> allowedStationIds = null)
        {
            if (!source.TryGetValue(trunk.LocalLineId, out List<PlannerBypassStation> stations))
            {
                return;
            }

            foreach (PlannerBypassStation station in stations)
            {
                if (allowedStationIds != null && !allowedStationIds.Contains(station.StationId))
                {
                    continue;
                }
                if (station.TrackAtomIndex >= trunk.LocalStartAtomIndex
                    && station.TrackAtomIndex <= trunk.LocalEndAtomIndexExclusive)
                {
                    stationsById[station.StationId] = station;
                }
            }
        }

        private static float ResolveHoldBudgetMinutes(PlannerContext context, PlannerLineRuntimeModel localModel)
        {
            float lineBudgetMinutes = localModel.Line?.maxStationDwellMinutes ?? 0;
            if (context.Request.maxLocalWaitMinutes <= 0)
            {
                return lineBudgetMinutes;
            }

            if (lineBudgetMinutes <= 0)
            {
                return context.Request.maxLocalWaitMinutes;
            }

            return Math.Min(lineBudgetMinutes, context.Request.maxLocalWaitMinutes);
        }

        private static PlannerBypassEvaluation PickBestBypassStation(
            PlannerContext context,
            PlannerTripModel localTrip,
            PlannerTripModel expressTrip,
            PursuitTrunk trunk,
            PlannerGapProfile gapProfile,
            PlannerCatchupPoint catchupPoint,
            List<PlannerBypassStation> stations,
            float holdBudgetMinutes)
        {
            List<PlannerBypassEvaluation> evaluations = new List<PlannerBypassEvaluation>();
            foreach (PlannerBypassStation station in stations)
            {
                PlannerBypassEvaluation evaluation = EvaluateBypassStation(
                    localTrip,
                    trunk,
                    gapProfile,
                    catchupPoint,
                    station);
                if (evaluation != null)
                {
                    evaluations.Add(evaluation);
                }
            }

            evaluations.Sort((left, right) =>
            {
                int holdCompare = left.HoldNeededMinutes.CompareTo(right.HoldNeededMinutes);
                if (holdCompare != 0)
                {
                    return holdCompare;
                }
                return left.AxisIndex.CompareTo(right.AxisIndex);
            });

            return evaluations.Count > 0 ? evaluations[0] : null;
        }

        private static PlannerBypassEvaluation EvaluateBypassStation(
            PlannerTripModel localTrip,
            PursuitTrunk trunk,
            PlannerGapProfile gapProfile,
            PlannerCatchupPoint catchupPoint,
            PlannerBypassStation station)
        {
            int axisSampleCount = Math.Max(1, trunk.AxisSampleCount);
            int localLength = Math.Max(1, trunk.LocalEndAtomIndexExclusive - trunk.LocalStartAtomIndex);
            int stationOffset = station.TrackAtomIndex - trunk.LocalStartAtomIndex;
            if (stationOffset < 0 || station.TrackAtomIndex > trunk.LocalEndAtomIndexExclusive)
            {
                return null;
            }

            int axisIndex = Math.Max(0, Math.Min(axisSampleCount, (int)Math.Round(axisSampleCount * (stationOffset / (float)localLength))));
            if (axisIndex >= gapProfile.Samples.Count || axisIndex > catchupPoint.CatchupAxisIndex)
            {
                return null;
            }

            float gapAtStationMinutes = gapProfile.Samples[axisIndex].GapMinutes;
            float holdNeededMinutes = PlannerMath.Round2(Math.Max(
                0f,
                catchupPoint.SeverityMinutes - Math.Max(0f, gapAtStationMinutes - catchupPoint.MinGapMinutes)));
            PlannerStationEvent localStationEvent = localTrip.StationEvents.FirstOrDefault(eventItem =>
                string.Equals(eventItem.StationId, station.StationId, StringComparison.Ordinal));

            PlannerBypassEvaluation evaluation = new PlannerBypassEvaluation();
            evaluation.StationId = station.StationId;
            evaluation.Name = station.Name;
            evaluation.Order = station.Order;
            evaluation.IsConfigured = station.IsConfigured;
            evaluation.IsVirtualCandidate = station.IsVirtualCandidate;
            evaluation.AxisIndex = axisIndex;
            evaluation.GapAtStationMinutes = PlannerMath.Round2(gapAtStationMinutes);
            evaluation.HoldNeededMinutes = holdNeededMinutes;
            evaluation.LocalStationMinute = PlannerMath.Round2(gapProfile.Samples[axisIndex].LocalMinute);
            evaluation.ExpressStationMinute = PlannerMath.Round2(gapProfile.Samples[axisIndex].ExpressMinute);
            evaluation.StationDepartureMinute = localStationEvent != null
                ? PlannerMath.Round2(localStationEvent.DepartureMinute)
                : evaluation.LocalStationMinute;
            return evaluation;
        }

        private static List<PlannerCatchupEvent> MergeCatchupEvents(List<PlannerCatchupEvent> events)
        {
            Dictionary<string, PlannerCatchupEvent> mergedByKey = new Dictionary<string, PlannerCatchupEvent>(StringComparer.Ordinal);
            foreach (PlannerCatchupEvent catchupEvent in events)
            {
                string key = catchupEvent.LocalTripId + "|" + catchupEvent.ExpressTripId + "|" + catchupEvent.LocalLineId + "|" + catchupEvent.ExpressLineId;
                if (!mergedByKey.TryGetValue(key, out PlannerCatchupEvent current))
                {
                    mergedByKey[key] = catchupEvent;
                    continue;
                }

                current.SourceCorridorIds = current.SourceCorridorIds
                    .Union(catchupEvent.SourceCorridorIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (catchupEvent.SeverityMinutes > current.SeverityMinutes)
                {
                    current.TrunkId = catchupEvent.TrunkId;
                    current.YieldingTripId = catchupEvent.YieldingTripId;
                    current.PriorityTripId = catchupEvent.PriorityTripId;
                    current.YieldingLineId = catchupEvent.YieldingLineId;
                    current.PriorityLineId = catchupEvent.PriorityLineId;
                    current.FromStationId = catchupEvent.FromStationId;
                    current.ToStationId = catchupEvent.ToStationId;
                    current.LocalEntryMinute = catchupEvent.LocalEntryMinute;
                    current.ExpressEntryMinute = catchupEvent.ExpressEntryMinute;
                    current.LocalExitMinute = catchupEvent.LocalExitMinute;
                    current.ExpressExitMinute = catchupEvent.ExpressExitMinute;
                    current.GapAtEntryMinutes = catchupEvent.GapAtEntryMinutes;
                    current.GapAtExitMinutes = catchupEvent.GapAtExitMinutes;
                    current.ClosingMinutes = catchupEvent.ClosingMinutes;
                    current.MinSharedGapMinutes = catchupEvent.MinSharedGapMinutes;
                    current.MinGapMinutes = catchupEvent.MinGapMinutes;
                    current.MinGapUncertaintyMinutes = catchupEvent.MinGapUncertaintyMinutes;
                    current.WorstCaseGapMinutes = catchupEvent.WorstCaseGapMinutes;
                    current.SeverityMinutes = catchupEvent.SeverityMinutes;
                    current.UnresolvedRiskMinutes = catchupEvent.UnresolvedRiskMinutes;
                    current.RobustnessRiskMinutes = catchupEvent.RobustnessRiskMinutes;
                    current.RequiredHoldMinutes = catchupEvent.RequiredHoldMinutes;
                    current.HoldBudgetMinutes = catchupEvent.HoldBudgetMinutes;
                    current.ResolvedHoldMinutes = catchupEvent.ResolvedHoldMinutes;
                    current.ExpressSavedMinutes = catchupEvent.ExpressSavedMinutes;
                    current.CatchupMinute = catchupEvent.CatchupMinute;
                    current.CatchupAxisIndex = catchupEvent.CatchupAxisIndex;
                    current.DidCatchUp = catchupEvent.DidCatchUp;
                    current.WithinHoldBudget = catchupEvent.WithinHoldBudget;
                    current.Confidence = Math.Max(current.Confidence, catchupEvent.Confidence);
                    current.SelectedBypassStation = catchupEvent.SelectedBypassStation;
                    current.UsableBypassStations = catchupEvent.UsableBypassStations;
                }
                else
                {
                    current.ExpressSavedMinutes = PlannerMath.Round2(current.ExpressSavedMinutes + catchupEvent.ExpressSavedMinutes);
                    current.UnresolvedRiskMinutes = PlannerMath.Round2(Math.Max(current.UnresolvedRiskMinutes, catchupEvent.UnresolvedRiskMinutes));
                    current.ResolvedHoldMinutes = PlannerMath.Round2(Math.Max(current.ResolvedHoldMinutes, catchupEvent.ResolvedHoldMinutes));
                    current.RobustnessRiskMinutes = PlannerMath.Round2(Math.Max(current.RobustnessRiskMinutes, catchupEvent.RobustnessRiskMinutes));
                }
            }

            return mergedByKey.Values
                .OrderByDescending(item => item.SeverityMinutes)
                .ThenBy(item => item.ExpressEntryMinute)
                .ToList();
        }

        private static float GetAtomBoundaryMinuteOffset(float[] offsets, int atomIndex)
        {
            if (offsets == null || offsets.Length == 0)
            {
                return 0f;
            }

            int clampedIndex = Math.Max(0, Math.Min(offsets.Length - 1, atomIndex));
            return offsets[clampedIndex];
        }

        private sealed class PlannerCorridorWindow
        {
            public float EntryMinute;
            public float ExitMinute;
            public float RuntimeMinutes;
        }

        private sealed class PlannerCurve
        {
            public List<PlannerCurveSample> Samples;
        }

        private sealed class PlannerCurveSample
        {
            public int AxisIndex;
            public float Minute;
            public float VariabilityMinutes;
        }

        private sealed class PlannerGapProfile
        {
            public List<PlannerGapSample> Samples;
            public float EntryGapMinutes;
            public float ExitGapMinutes;
            public float MinGapMinutes;
            public float MinGapUncertaintyMinutes;
            public int MinGapAxisIndex;
            public float MinGapMinute;
        }

        private sealed class PlannerGapSample
        {
            public int AxisIndex;
            public float Minute;
            public float LocalMinute;
            public float ExpressMinute;
            public float GapMinutes;
            public float UncertaintyMinutes;
        }

        private sealed class PlannerCatchupPoint
        {
            public int CatchupAxisIndex;
            public float CatchupMinute;
            public float MinGapMinutes;
            public float MinGapUncertaintyMinutes;
            public float EntryGapMinutes;
            public float ExitGapMinutes;
            public float ClosingMinutes;
            public float SeverityMinutes;
            public float WorstCaseGapMinutes;
            public float RobustnessRiskMinutes;
            public bool DidCatchUp;
        }
    }
}
