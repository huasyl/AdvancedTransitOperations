using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class PlannerInputNormalizer
    {
        public PlannerContext Normalize(
            DispatchPlannerExportSnapshot snapshot,
            DispatchPlannerRequest request)
        {
            PlannerContext context = new PlannerContext();
            context.Snapshot = snapshot ?? new DispatchPlannerExportSnapshot();
            context.Request = request ?? new DispatchPlannerRequest();
            context.WindowStart = string.IsNullOrEmpty(context.Request.windowStart) ? "00:00" : context.Request.windowStart;
            context.WindowEnd = string.IsNullOrEmpty(context.Request.windowEnd) ? "23:59" : context.Request.windowEnd;
            context.WindowStartMinute = PlannerMath.TimeToMinutes(context.WindowStart) ?? 0;
            context.WindowEndMinute = PlannerMath.TimeToMinutes(context.WindowEnd) ?? 1439;
            context.ExpressSourceMode = string.IsNullOrEmpty(context.Request.expressSourceMode) ? "virtual" : context.Request.expressSourceMode;
            context.DepartureMode = string.IsNullOrEmpty(context.Request.departureMode) ? "fixedInterval" : context.Request.departureMode;
            context.VirtualExpressBaseLineId = context.Request.virtualExpressBaseLineId ?? string.Empty;
            context.ForcedBypassStationIds = context.Request.forcedBypassStationIds ?? new string[0];

            BuildLineMaps(context);
            BuildStationMaps(context);
            BuildSegmentMaps(context);
            BuildLineTrackMaps(context);
            BuildObservationMaps(context);
            BuildBypassMaps(context);

            context.SelectedDraft = SelectDraft(context);
            context.SelectedLocalLineIds = SelectLocalLines(context);
            context.VirtualExpressBaseLineId = ResolveVirtualExpressBaseLine(context);
            context.VirtualExpressLineId = string.IsNullOrEmpty(context.VirtualExpressBaseLineId)
                ? string.Empty
                : CreateVirtualExpressLineId(context.VirtualExpressBaseLineId);
            context.SelectedExpressLineIds = SelectExpressLines(context);
            context.SelectedExpressStopStationIds = SelectExpressStops(context);
            BuildLineRoles(context);
            context.WorkingRows = BuildWorkingRows(context);

            if (context.SelectedLocalLineIds.Length == 0)
            {
                context.ValidationIssues.Add(PlannerDiagnosticFactory.Create(
                    "error",
                    "NO_LOCAL_LINES",
                    "Planner request does not select any local lines."));
            }

            if (string.Equals(context.ExpressSourceMode, "virtual", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(context.VirtualExpressBaseLineId))
            {
                context.ValidationIssues.Add(PlannerDiagnosticFactory.Create(
                    "error",
                    "VIRTUAL_BASE_LINE_MISSING",
                    "Virtual express source is selected without a base local line."));
            }

            if (context.WorkingRows.Count == 0)
            {
                context.ValidationIssues.Add(PlannerDiagnosticFactory.Create(
                    "warning",
                    "NO_WORKING_ROWS",
                    "No staged or derived timetable rows were found inside the selected analysis window."));
            }

            return context;
        }

        public static string CreateVirtualExpressLineId(string baseLineId)
        {
            return "virtual:" + (baseLineId ?? string.Empty);
        }

        private static void BuildLineMaps(PlannerContext context)
        {
            foreach (DispatchPlannerLineDto line in context.Snapshot.lines ?? new DispatchPlannerLineDto[0])
            {
                if (line != null && !string.IsNullOrEmpty(line.id))
                {
                    context.LinesById[line.id] = line;
                }
            }
        }

        private static void BuildStationMaps(PlannerContext context)
        {
            foreach (DispatchPlannerStationDto station in context.Snapshot.stations ?? new DispatchPlannerStationDto[0])
            {
                if (station == null || string.IsNullOrEmpty(station.id))
                {
                    continue;
                }

                context.StationsById[station.id] = station;
                if (!context.StationsByLineId.TryGetValue(station.lineId ?? string.Empty, out List<DispatchPlannerStationDto> lineStations))
                {
                    lineStations = new List<DispatchPlannerStationDto>();
                    context.StationsByLineId[station.lineId ?? string.Empty] = lineStations;
                }
                lineStations.Add(station);
            }

            foreach (KeyValuePair<string, List<DispatchPlannerStationDto>> entry in context.StationsByLineId)
            {
                entry.Value.Sort((left, right) => left.order.CompareTo(right.order));
            }
        }

        private static void BuildSegmentMaps(PlannerContext context)
        {
            foreach (DispatchPlannerSegmentDto segment in context.Snapshot.segments ?? new DispatchPlannerSegmentDto[0])
            {
                if (segment == null || string.IsNullOrEmpty(segment.lineId))
                {
                    continue;
                }

                if (!context.SegmentsByLineId.TryGetValue(segment.lineId, out List<DispatchPlannerSegmentDto> lineSegments))
                {
                    lineSegments = new List<DispatchPlannerSegmentDto>();
                    context.SegmentsByLineId[segment.lineId] = lineSegments;
                }
                lineSegments.Add(segment);
            }

            foreach (KeyValuePair<string, List<DispatchPlannerSegmentDto>> entry in context.SegmentsByLineId)
            {
                entry.Value.Sort((left, right) => left.fromOrder.CompareTo(right.fromOrder));
            }
        }

        private static void BuildLineTrackMaps(PlannerContext context)
        {
            foreach (DispatchPlannerLineTrackDto track in context.Snapshot.currentTrackScenario?.lines ?? new DispatchPlannerLineTrackDto[0])
            {
                if (track != null && !string.IsNullOrEmpty(track.lineId))
                {
                    context.LineTracksByLineId[track.lineId] = track;
                }
            }
        }

        private static void BuildObservationMaps(PlannerContext context)
        {
            foreach (DispatchPlannerStationDwellObservationDto dwell in context.Snapshot.observations?.stopDwell ?? new DispatchPlannerStationDwellObservationDto[0])
            {
                if (dwell != null && !string.IsNullOrEmpty(dwell.stationId))
                {
                    context.StopDwellByStationId[dwell.stationId] = dwell;
                }
            }

            Dictionary<string, List<float>> samplesByPair = new Dictionary<string, List<float>>(StringComparer.Ordinal);
            HashSet<string> seenTrips = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchPlannerDraftDto draft in context.Snapshot.drafts ?? new DispatchPlannerDraftDto[0])
            {
                foreach (DispatchWorkbenchTripDto trip in draft?.trips ?? new DispatchWorkbenchTripDto[0])
                {
                    if (trip == null || string.IsNullOrEmpty(trip.lineId))
                    {
                        continue;
                    }

                    string stopSignature = string.Join(";",
                        (trip.stops ?? new DispatchWorkbenchTripStopDto[0])
                            .Select(stop => (stop?.stationId ?? string.Empty)
                                + "|"
                                + (stop?.arrivalTime ?? string.Empty)
                                + "|"
                                + (stop?.departureTime ?? string.Empty)));
                    string tripSignature = (trip.lineId ?? string.Empty) + "|" + (trip.id ?? string.Empty) + "|" + stopSignature;
                    if (!seenTrips.Add(tripSignature))
                    {
                        continue;
                    }

                    DispatchWorkbenchTripStopDto[] stops = trip.stops ?? new DispatchWorkbenchTripStopDto[0];
                    for (int index = 0; index + 1 < stops.Length; index++)
                    {
                        DispatchWorkbenchTripStopDto fromStop = stops[index];
                        DispatchWorkbenchTripStopDto toStop = stops[index + 1];
                        if (fromStop == null
                            || toStop == null
                            || string.IsNullOrEmpty(fromStop.stationId)
                            || string.IsNullOrEmpty(toStop.stationId)
                            || string.Equals(fromStop.stationId, toStop.stationId, StringComparison.Ordinal)
                            || string.IsNullOrEmpty(fromStop.departureTime)
                            || string.IsNullOrEmpty(toStop.arrivalTime))
                        {
                            continue;
                        }

                        float runtimeMinutes = PlannerMath.ComputeForwardMinuteDelta(fromStop.departureTime, toStop.arrivalTime);
                        if (!(runtimeMinutes > 0f) || runtimeMinutes > 180f)
                        {
                            continue;
                        }

                        string key = trip.lineId + "|" + fromStop.stationId + "->" + toStop.stationId;
                        if (!samplesByPair.TryGetValue(key, out List<float> samples))
                        {
                            samples = new List<float>();
                            samplesByPair[key] = samples;
                        }
                        samples.Add(runtimeMinutes);
                    }
                }
            }

            foreach (KeyValuePair<string, List<float>> entry in samplesByPair)
            {
                PlannerObservedRuntimeSummary summary = PlannerMath.SummarizeRuntimeSamples(entry.Value);
                if (summary != null)
                {
                    context.StationRuntimeByLinePair[entry.Key] = summary;
                }
            }
        }

        private static void BuildBypassMaps(PlannerContext context)
        {
            BuildBypassMap(
                context,
                context.ConfiguredBypassStationsByLineId,
                context.Snapshot.configuredBypassStations,
                true,
                false);
            BuildBypassMap(
                context,
                context.CandidateBypassStationsByLineId,
                context.Snapshot.candidateBypassStations,
                false,
                true);
            BuildTemporaryStopBypassCandidates(context);
        }

        private static void BuildBypassMap(
            PlannerContext context,
            Dictionary<string, List<PlannerBypassStation>> destination,
            DispatchPlannerBypassStationDto[] stations,
            bool configured,
            bool candidate)
        {
            foreach (DispatchPlannerBypassStationDto station in stations ?? new DispatchPlannerBypassStationDto[0])
            {
                if (station == null || string.IsNullOrEmpty(station.lineId))
                {
                    continue;
                }
                if (candidate && station.isConfigured)
                {
                    continue;
                }

                if (!destination.TryGetValue(station.lineId, out List<PlannerBypassStation> lineStations))
                {
                    lineStations = new List<PlannerBypassStation>();
                    destination[station.lineId] = lineStations;
                }
                lineStations.Add(new PlannerBypassStation
                {
                    StationId = station.stationId ?? string.Empty,
                    WorkbenchStationId = station.workbenchStationId ?? string.Empty,
                    LineId = station.lineId ?? string.Empty,
                    Name = station.name ?? string.Empty,
                    Order = station.order,
                    TrackAtomIndex = ResolveTrackAtomIndex(context, station.stationId),
                    IsConfigured = configured || station.isConfigured,
                    IsVirtualCandidate = candidate && station.isVirtualCandidate && !station.isConfigured
                });
            }

            foreach (KeyValuePair<string, List<PlannerBypassStation>> entry in destination)
            {
                entry.Value.Sort((left, right) => left.Order.CompareTo(right.Order));
            }
        }

        private static void BuildTemporaryStopBypassCandidates(PlannerContext context)
        {
            foreach (DispatchPlannerSharedCorridorDto corridor in context.Snapshot.currentTrackScenario?.sharedCorridors ?? new DispatchPlannerSharedCorridorDto[0])
            {
                if (!IsValidSameDirectionCorridor(corridor))
                {
                    continue;
                }

                AddTemporaryStopCandidatesForCorridor(
                    context,
                    corridor.lineId ?? string.Empty,
                    corridor.otherLineId ?? string.Empty,
                    corridor.lineStartAtomIndex,
                    corridor.lineEndAtomIndexExclusive,
                    corridor.otherStartAtomIndex,
                    corridor.otherEndAtomIndexExclusive);
            }

            foreach (KeyValuePair<string, List<PlannerBypassStation>> entry in context.CandidateBypassStationsByLineId)
            {
                entry.Value.Sort((left, right) =>
                {
                    int orderCompare = left.Order.CompareTo(right.Order);
                    return orderCompare != 0 ? orderCompare : string.Compare(left.StationId, right.StationId, StringComparison.Ordinal);
                });
            }
        }

        private static void AddTemporaryStopCandidatesForCorridor(
            PlannerContext context,
            string targetLineId,
            string sourceLineId,
            int targetStartAtomIndex,
            int targetEndAtomIndexExclusive,
            int sourceStartAtomIndex,
            int sourceEndAtomIndexExclusive)
        {
            if (string.IsNullOrEmpty(targetLineId)
                || string.IsNullOrEmpty(sourceLineId)
                || string.Equals(targetLineId, sourceLineId, StringComparison.Ordinal)
                || !context.StationsByLineId.TryGetValue(sourceLineId, out List<DispatchPlannerStationDto> sourceStations))
            {
                return;
            }

            if (!context.CandidateBypassStationsByLineId.TryGetValue(targetLineId, out List<PlannerBypassStation> targetCandidates))
            {
                targetCandidates = new List<PlannerBypassStation>();
                context.CandidateBypassStationsByLineId[targetLineId] = targetCandidates;
            }

            int tolerance = PlannerDefaults.BypassStationEndpointToleranceAtoms;
            int sourceStart = Math.Min(sourceStartAtomIndex, sourceEndAtomIndexExclusive);
            int sourceEnd = Math.Max(sourceStartAtomIndex, sourceEndAtomIndexExclusive);
            int targetStart = Math.Min(targetStartAtomIndex, targetEndAtomIndexExclusive);
            int targetEnd = Math.Max(targetStartAtomIndex, targetEndAtomIndexExclusive);
            HashSet<string> existingIds = new HashSet<string>(
                targetCandidates.Select(station => station.StationId ?? string.Empty),
                StringComparer.Ordinal);

            foreach (PlannerBypassStation station in context.ConfiguredBypassStationsByLineId.TryGetValue(targetLineId, out List<PlannerBypassStation> configured)
                ? configured
                : new List<PlannerBypassStation>())
            {
                existingIds.Add(station.StationId ?? string.Empty);
            }

            foreach (DispatchPlannerStationDto sourceStation in sourceStations)
            {
                if (sourceStation == null
                    || string.IsNullOrEmpty(sourceStation.id)
                    || !sourceStation.canConfigureBypass
                    || sourceStation.trackAtomIndex < sourceStart - tolerance
                    || sourceStation.trackAtomIndex > sourceEnd + tolerance
                    || HasTargetLineStationAtBuilding(context, targetLineId, sourceStation.buildingEntityIndex)
                    || !existingIds.Add(sourceStation.id))
                {
                    continue;
                }

                int projectedAtomIndex = ProjectSourceAtomToTargetCorridor(
                    sourceStation.trackAtomIndex,
                    sourceStart,
                    sourceEnd,
                    targetStart,
                    targetEnd);
                targetCandidates.Add(new PlannerBypassStation
                {
                    StationId = sourceStation.id,
                    WorkbenchStationId = sourceStation.workbenchStationId ?? string.Empty,
                    LineId = targetLineId,
                    Name = sourceStation.name ?? sourceStation.id,
                    Order = projectedAtomIndex,
                    TrackAtomIndex = projectedAtomIndex,
                    IsConfigured = false,
                    IsVirtualCandidate = true
                });
            }
        }

        private static bool HasTargetLineStationAtBuilding(
            PlannerContext context,
            string targetLineId,
            int buildingEntityIndex)
        {
            if (buildingEntityIndex < 0
                || !context.StationsByLineId.TryGetValue(targetLineId ?? string.Empty, out List<DispatchPlannerStationDto> targetStations))
            {
                return false;
            }

            return targetStations.Any(station => station != null && station.buildingEntityIndex == buildingEntityIndex);
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

        private static int ResolveTrackAtomIndex(PlannerContext context, string stationId)
        {
            return !string.IsNullOrEmpty(stationId)
                && context.StationsById.TryGetValue(stationId, out DispatchPlannerStationDto station)
                ? station.trackAtomIndex
                : -1;
        }

        private static DispatchPlannerDraftDto SelectDraft(PlannerContext context)
        {
            DispatchPlannerDraftDto[] drafts = context.Snapshot.drafts ?? new DispatchPlannerDraftDto[0];
            if (!string.IsNullOrEmpty(context.Request.draftKey))
            {
                DispatchPlannerDraftDto exact = drafts.FirstOrDefault(draft =>
                    string.Equals(draft?.lineKey, context.Request.draftKey, StringComparison.Ordinal));
                if (exact != null)
                {
                    return exact;
                }
            }

            return drafts
                .OrderByDescending(draft => ((draft?.lineDraftRows ?? draft?.stagedRows)?.Length ?? 0) + (draft?.trips?.Length ?? 0))
                .FirstOrDefault();
        }

        private static string[] SelectLocalLines(PlannerContext context)
        {
            HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in context.Request.localLineIds ?? new string[0])
            {
                if (!string.IsNullOrEmpty(lineId) && context.LinesById.ContainsKey(lineId))
                {
                    selected.Add(lineId);
                }
            }

            if (selected.Count == 0)
            {
                AddLocalFallbackLines(selected, context, context.Request.adjustableLineIds);
            }

            if (selected.Count == 0 && context.SelectedDraft?.mergedView?.localLineIds != null)
            {
                foreach (string lineId in context.SelectedDraft.mergedView.localLineIds)
                {
                    if (!string.IsNullOrEmpty(lineId) && context.LinesById.ContainsKey(lineId))
                    {
                        selected.Add(lineId);
                    }
                }
            }

            return selected.ToArray();
        }

        private static void AddLocalFallbackLines(
            HashSet<string> selected,
            PlannerContext context,
            IEnumerable<string> lineIds)
        {
            foreach (string lineId in lineIds ?? Enumerable.Empty<string>())
            {
                if (IsSelectableLocalLine(context, lineId))
                {
                    selected.Add(lineId);
                }
            }
        }

        private static bool IsSelectableLocalLine(PlannerContext context, string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !context.LinesById.TryGetValue(lineId, out DispatchPlannerLineDto line))
            {
                return false;
            }

            string configuredKind = line.configuredKind ?? string.Empty;
            string runtimeKind = line.kind ?? string.Empty;
            return !string.Equals(configuredKind, "express", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(runtimeKind, "express", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveVirtualExpressBaseLine(PlannerContext context)
        {
            if (!string.IsNullOrEmpty(context.VirtualExpressBaseLineId) && context.LinesById.ContainsKey(context.VirtualExpressBaseLineId))
            {
                return context.VirtualExpressBaseLineId;
            }

            return context.SelectedLocalLineIds.FirstOrDefault() ?? string.Empty;
        }

        private static string[] SelectExpressLines(PlannerContext context)
        {
            if (string.Equals(context.ExpressSourceMode, "existing", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(context.Request.expressLineId) && context.LinesById.ContainsKey(context.Request.expressLineId))
                {
                    return new[] { context.Request.expressLineId };
                }

                if (context.SelectedDraft?.mergedView?.expressLineIds != null
                    && context.SelectedDraft.mergedView.expressLineIds.Length > 0)
                {
                    return context.SelectedDraft.mergedView.expressLineIds
                        .Where(lineId => !string.IsNullOrEmpty(lineId) && context.LinesById.ContainsKey(lineId))
                        .ToArray();
                }

                return new string[0];
            }

            return string.IsNullOrEmpty(context.VirtualExpressLineId)
                ? new string[0]
                : new[] { context.VirtualExpressLineId };
        }

        private static string[] SelectExpressStops(PlannerContext context)
        {
            if (context.Request.expressStopStationIds != null && context.Request.expressStopStationIds.Length > 0)
            {
                return context.Request.expressStopStationIds
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }

            if (!string.Equals(context.ExpressSourceMode, "existing", StringComparison.OrdinalIgnoreCase))
            {
                return new string[0];
            }

            HashSet<string> stops = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> expressLineSet = new HashSet<string>(context.SelectedExpressLineIds ?? new string[0], StringComparer.Ordinal);
            foreach (DispatchWorkbenchTripDto trip in EnumeratePlannerTrips(context))
            {
                if (trip == null
                    || !string.Equals(trip.kind, "express", StringComparison.OrdinalIgnoreCase)
                    || !expressLineSet.Contains(trip.lineId ?? string.Empty))
                {
                    continue;
                }

                foreach (DispatchWorkbenchTripStopDto stop in trip.stops ?? new DispatchWorkbenchTripStopDto[0])
                {
                    if (stop != null
                        && !string.IsNullOrEmpty(stop.stationId)
                        && !string.Equals(stop.stopType, "pass", StringComparison.OrdinalIgnoreCase))
                    {
                        stops.Add(stop.stationId);
                    }
                }
            }

            return stops.ToArray();
        }

        private static void BuildLineRoles(PlannerContext context)
        {
            HashSet<string> target = new HashSet<string>(
                (context.SelectedExpressLineIds ?? new string[0]).Where(lineId => !string.IsNullOrEmpty(lineId)),
                StringComparer.Ordinal);
            HashSet<string> physicalTargetLineIds = new HashSet<string>(StringComparer.Ordinal);
            if (string.Equals(context.ExpressSourceMode, "virtual", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(context.VirtualExpressBaseLineId)
                    && context.LinesById.ContainsKey(context.VirtualExpressBaseLineId))
                {
                    physicalTargetLineIds.Add(context.VirtualExpressBaseLineId);
                }
            }
            else
            {
                foreach (string lineId in context.SelectedExpressLineIds ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(lineId) && context.LinesById.ContainsKey(lineId))
                    {
                        physicalTargetLineIds.Add(lineId);
                    }
                }
            }

            HashSet<string> effective = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in target)
            {
                if (!string.IsNullOrEmpty(lineId)
                    && (context.LinesById.ContainsKey(lineId)
                        || string.Equals(lineId, context.VirtualExpressLineId, StringComparison.Ordinal)))
                {
                    effective.Add(lineId);
                }
            }
            foreach (string lineId in physicalTargetLineIds)
            {
                if (!string.IsNullOrEmpty(lineId) && context.LinesById.ContainsKey(lineId))
                {
                    effective.Add(lineId);
                }
            }

            HashSet<string> autoConstraintLineIds = DiscoverAutoConstraintLineIds(context, physicalTargetLineIds);
            foreach (string lineId in autoConstraintLineIds)
            {
                effective.Add(lineId);
            }

            HashSet<string> adjustable = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in context.Request.adjustableLineIds ?? new string[0])
            {
                if (!string.IsNullOrEmpty(lineId) && effective.Contains(lineId))
                {
                    adjustable.Add(lineId);
                }
            }

            if (adjustable.Count == 0)
            {
                foreach (string lineId in context.SelectedLocalLineIds ?? new string[0])
                {
                    if (!string.IsNullOrEmpty(lineId) && effective.Contains(lineId))
                    {
                        adjustable.Add(lineId);
                    }
                }
            }

            string[] effectiveArray = effective
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .OrderBy(lineId => lineId, StringComparer.Ordinal)
                .ToArray();
            string[] adjustableArray = adjustable
                .Where(lineId => !string.IsNullOrEmpty(lineId) && effective.Contains(lineId))
                .OrderBy(lineId => lineId, StringComparer.Ordinal)
                .ToArray();
            string[] fixedArray = effectiveArray
                .Where(lineId => !adjustable.Contains(lineId))
                .ToArray();

            context.SelectedLineIds = effectiveArray;
            context.EffectiveLineIds = effectiveArray;
            context.AdjustableLineIds = adjustableArray;
            context.FixedLineIds = fixedArray;
            context.AutoFixedConstraintLineIds = fixedArray
                .Where(lineId => !target.Contains(lineId))
                .OrderBy(lineId => lineId, StringComparer.Ordinal)
                .ToArray();
            context.TargetLineIds = target
                .Where(lineId => effective.Contains(lineId))
                .OrderBy(lineId => lineId, StringComparer.Ordinal)
                .ToArray();
        }

        private static HashSet<string> DiscoverAutoConstraintLineIds(
            PlannerContext context,
            HashSet<string> physicalTargetLineIds)
        {
            HashSet<string> discovered = new HashSet<string>(StringComparer.Ordinal);
            if (physicalTargetLineIds == null || physicalTargetLineIds.Count == 0)
            {
                return discovered;
            }

            foreach (DispatchPlannerSharedCorridorDto corridor in context.Snapshot.currentTrackScenario?.sharedCorridors ?? new DispatchPlannerSharedCorridorDto[0])
            {
                if (!IsValidSameDirectionCorridor(corridor))
                {
                    continue;
                }

                string lineId = corridor.lineId ?? string.Empty;
                string otherLineId = corridor.otherLineId ?? string.Empty;
                if (physicalTargetLineIds.Contains(lineId))
                {
                    AddDiscoveredConstraintLine(context, discovered, otherLineId);
                }
                if (physicalTargetLineIds.Contains(otherLineId))
                {
                    AddDiscoveredConstraintLine(context, discovered, lineId);
                }
            }

            return discovered;
        }

        private static void AddDiscoveredConstraintLine(
            PlannerContext context,
            HashSet<string> discovered,
            string lineId)
        {
            if (string.IsNullOrEmpty(lineId)
                || !context.LinesById.ContainsKey(lineId)
                || !HasLineActivityInsideWindow(context, lineId))
            {
                return;
            }

            discovered.Add(lineId);
        }

        private static bool IsValidSameDirectionCorridor(DispatchPlannerSharedCorridorDto corridor)
        {
            return corridor != null
                && string.Equals(corridor.traversalRelation, "SameDirection", StringComparison.OrdinalIgnoreCase)
                && !corridor.hasMirroredContext
                && corridor.orderedRun > 0
                && corridor.physicalOverlap > 0;
        }

        private static bool HasLineActivityInsideWindow(PlannerContext context, string lineId)
        {
            foreach (DispatchWorkbenchStagedRowDto row in EnumeratePlannerDraftRows(context))
            {
                if (row == null || !string.Equals(row.lineId, lineId, StringComparison.Ordinal))
                {
                    continue;
                }
                int? minute = PlannerMath.TimeToMinutes(row.time);
                if (minute.HasValue && PlannerMath.IsMinuteInsideWindow(minute.Value, context.WindowStartMinute, context.WindowEndMinute))
                {
                    return true;
                }
            }

            foreach (DispatchWorkbenchTripDto trip in EnumeratePlannerTrips(context))
            {
                if (trip == null || !string.Equals(trip.lineId, lineId, StringComparison.Ordinal))
                {
                    continue;
                }
                int? minute = PlannerMath.TimeToMinutes(trip.depart);
                if (minute.HasValue && PlannerMath.IsMinuteInsideWindow(minute.Value, context.WindowStartMinute, context.WindowEndMinute))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<PlannerWorkingRow> BuildWorkingRows(PlannerContext context)
        {
            List<PlannerWorkingRow> rows = new List<PlannerWorkingRow>();
            HashSet<string> selectedLineSet = new HashSet<string>(context.SelectedLineIds ?? new string[0], StringComparer.Ordinal);
            HashSet<string> rowIds = new HashSet<string>(StringComparer.Ordinal);

            List<DispatchWorkbenchStagedRowDto> stagedRows =
                EnumeratePlannerDraftRows(context).ToList();
            if (stagedRows.Count > 0)
            {
                foreach (DispatchWorkbenchStagedRowDto row in stagedRows)
                {
                    if (row == null)
                    {
                        continue;
                    }

                    int? minute = PlannerMath.TimeToMinutes(row.time);
                    if (!minute.HasValue || !PlannerMath.IsMinuteInsideWindow(minute.Value, context.WindowStartMinute, context.WindowEndMinute))
                    {
                        continue;
                    }

                    if (!selectedLineSet.Contains(row.lineId ?? string.Empty))
                    {
                        continue;
                    }

                    string rowId = row.id ?? string.Empty;
                    if (!string.IsNullOrEmpty(rowId) && !rowIds.Add(rowId))
                    {
                        continue;
                    }

                    rows.Add(new PlannerWorkingRow
                    {
                        Id = rowId,
                        LineId = row.lineId ?? string.Empty,
                        Kind = string.Equals(row.kind, "express", StringComparison.OrdinalIgnoreCase) ? "express" : "local",
                        Minute = minute.Value,
                        Source = row.source ?? string.Empty,
                        Note = row.note ?? string.Empty
                    });
                }
            }

            HashSet<string> coveredLineIds = new HashSet<string>(rows.Select(row => row.LineId), StringComparer.Ordinal);
            HashSet<string> missingLineIds = new HashSet<string>(
                selectedLineSet.Where(lineId => !coveredLineIds.Contains(lineId)),
                StringComparer.Ordinal);

            if (rows.Count == 0 || missingLineIds.Count > 0)
            {
                HashSet<string> tripLineFilter = rows.Count == 0 ? selectedLineSet : missingLineIds;
                foreach (DispatchWorkbenchTripDto trip in EnumeratePlannerTrips(context))
                {
                    if (trip == null || !tripLineFilter.Contains(trip.lineId ?? string.Empty))
                    {
                        continue;
                    }

                    int? minute = PlannerMath.TimeToMinutes(trip.depart);
                    if (!minute.HasValue || !PlannerMath.IsMinuteInsideWindow(minute.Value, context.WindowStartMinute, context.WindowEndMinute))
                    {
                        continue;
                    }

                    string rowId = trip.id ?? string.Empty;
                    if (!string.IsNullOrEmpty(rowId) && !rowIds.Add(rowId))
                    {
                        continue;
                    }

                    rows.Add(new PlannerWorkingRow
                    {
                        Id = rowId,
                        LineId = trip.lineId ?? string.Empty,
                        Kind = string.Equals(trip.kind, "express", StringComparison.OrdinalIgnoreCase) ? "express" : "local",
                        Minute = minute.Value,
                        Source = "tripDerived",
                        Note = string.Empty
                    });
                }
            }

            if (string.Equals(context.ExpressSourceMode, "virtual", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(context.VirtualExpressLineId))
            {
                rows.RemoveAll(row => string.Equals(row.LineId, context.VirtualExpressLineId, StringComparison.Ordinal));
                rows.AddRange(BuildVirtualExpressRows(context));
            }

            rows.Sort((left, right) =>
            {
                int minuteCompare = left.Minute.CompareTo(right.Minute);
                if (minuteCompare != 0)
                {
                    return minuteCompare;
                }

                int lineCompare = string.Compare(left.LineId, right.LineId, StringComparison.Ordinal);
                if (lineCompare != 0)
                {
                    return lineCompare;
                }

                return string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });
            return rows;
        }

        private static IEnumerable<DispatchWorkbenchStagedRowDto> EnumeratePlannerDraftRows(PlannerContext context)
        {
            foreach (DispatchPlannerDraftDto draft in context?.Snapshot?.drafts
                ?? new DispatchPlannerDraftDto[0])
            {
                DispatchWorkbenchStagedRowDto[] rows =
                    draft?.lineDraftRows
                    ?? draft?.stagedRows
                    ?? new DispatchWorkbenchStagedRowDto[0];
                foreach (DispatchWorkbenchStagedRowDto row in rows)
                {
                    if (row != null)
                    {
                        yield return row;
                    }
                }
            }
        }

        private static IEnumerable<DispatchWorkbenchTripDto> EnumeratePlannerTrips(PlannerContext context)
        {
            HashSet<string> seenTripIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchPlannerDraftDto draft in context.Snapshot?.drafts ?? new DispatchPlannerDraftDto[0])
            {
                foreach (DispatchWorkbenchTripDto trip in draft?.trips ?? new DispatchWorkbenchTripDto[0])
                {
                    if (trip == null)
                    {
                        continue;
                    }

                    string tripId = trip.id ?? string.Empty;
                    if (!string.IsNullOrEmpty(tripId) && !seenTripIds.Add(tripId))
                    {
                        continue;
                    }

                    yield return trip;
                }
            }
        }

        private static IEnumerable<PlannerWorkingRow> BuildVirtualExpressRows(PlannerContext context)
        {
            List<PlannerWorkingRow> rows = new List<PlannerWorkingRow>();
            if (!context.LinesById.TryGetValue(context.VirtualExpressBaseLineId, out DispatchPlannerLineDto baseLine))
            {
                return rows;
            }

            int intervalMinutes = context.Request.intervalMinutes > 0
                ? context.Request.intervalMinutes
                : context.Request.expressTripsPerHour > 0
                    ? Math.Max(1, (int)Math.Round(60d / context.Request.expressTripsPerHour))
                    : 30;
            int? anchorMinute = PlannerMath.TimeToMinutes(context.Request.phaseTime);
            List<int> departures = PlannerMath.GeneratePeriodicMinutes(
                context.WindowStartMinute,
                context.WindowEndMinute,
                60f / intervalMinutes,
                anchorMinute);

            for (int i = 0; i < departures.Count; i++)
            {
                rows.Add(new PlannerWorkingRow
                {
                    Id = "virtual-express-" + i,
                    LineId = context.VirtualExpressLineId,
                    Kind = "express",
                    Minute = departures[i],
                    Source = "planner",
                    Note = "virtual:" + (baseLine.name ?? baseLine.id)
                });
            }

            return rows;
        }
    }
}
