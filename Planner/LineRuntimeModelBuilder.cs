using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class LineRuntimeModelBuilder
    {
        public PlannerRuntimeCatalog Build(PlannerContext context)
        {
            PlannerRuntimeCatalog catalog = new PlannerRuntimeCatalog();
            HashSet<string> sourceLineIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (string lineId in context.SelectedLineIds)
            {
                if (!string.IsNullOrEmpty(lineId) && !string.Equals(lineId, context.VirtualExpressLineId, StringComparison.Ordinal))
                {
                    sourceLineIds.Add(lineId);
                }
            }

            if (!string.IsNullOrEmpty(context.VirtualExpressBaseLineId))
            {
                sourceLineIds.Add(context.VirtualExpressBaseLineId);
            }

            foreach (string lineId in sourceLineIds)
            {
                PlannerLineRuntimeModel model = BuildLineRuntimeModel(context, lineId, lineId, false);
                if (model != null)
                {
                    catalog.ModelsByLineId[lineId] = model;
                }
            }

            if (!string.IsNullOrEmpty(context.VirtualExpressLineId) && !string.IsNullOrEmpty(context.VirtualExpressBaseLineId))
            {
                PlannerLineRuntimeModel virtualModel = BuildLineRuntimeModel(
                    context,
                    context.VirtualExpressLineId,
                    context.VirtualExpressBaseLineId,
                    true);
                if (virtualModel != null)
                {
                    catalog.ModelsByLineId[context.VirtualExpressLineId] = virtualModel;
                }
            }

            return catalog;
        }

        private PlannerLineRuntimeModel BuildLineRuntimeModel(
            PlannerContext context,
            string targetLineId,
            string sourceLineId,
            bool treatAsVirtualExpress)
        {
            if (!context.LinesById.TryGetValue(sourceLineId, out DispatchPlannerLineDto sourceLine))
            {
                return null;
            }

            if (!context.StationsByLineId.TryGetValue(sourceLineId, out List<DispatchPlannerStationDto> sourceStations))
            {
                return null;
            }

            List<DispatchPlannerSegmentDto> sourceSegments = context.SegmentsByLineId.TryGetValue(sourceLineId, out List<DispatchPlannerSegmentDto> segments)
                ? segments
                : new List<DispatchPlannerSegmentDto>();
            DispatchPlannerLineTrackDto lineTrack = context.LineTracksByLineId.TryGetValue(sourceLineId, out DispatchPlannerLineTrackDto track)
                ? track
                : null;

            PlannerLineRuntimeModel model = new PlannerLineRuntimeModel();
            model.LineId = targetLineId;
            model.SourceLineId = sourceLineId;
            model.LineName = treatAsVirtualExpress ? ((sourceLine.name ?? sourceLine.id) + " / 虚拟快车") : (sourceLine.name ?? sourceLine.id);
            model.Kind = treatAsVirtualExpress ? "express" : (sourceLine.kind ?? "local");
            model.Line = sourceLine;
            model.LineTrack = lineTrack;
            model.TrackAtomCount = lineTrack?.trackAtomCount ?? 0;
            model.Stations = sourceStations;
            model.Segments = sourceSegments;
            model.StationCount = sourceStations.Count;

            HashSet<string> expressStopStationIds = BuildExpressStopStationIdSet(context, sourceLineId, sourceStations, treatAsVirtualExpress);
            Dictionary<string, PlannerObservedRuntimeSummary> segmentRuntimeByStationPair = new Dictionary<string, PlannerObservedRuntimeSummary>(StringComparer.Ordinal);
            float cursorMinute = 0f;

            for (int index = 0; index < sourceStations.Count; index++)
            {
                DispatchPlannerStationDto station = sourceStations[index];
                PlannerObservedRuntimeSummary dwellSummary = ResolveStationDwellMinutes(context, station);
                bool shouldStop = ShouldStopAtStation(model.Kind, station, expressStopStationIds, index, sourceStations.Count);
                float skippedStopStartLossMinutes = shouldStop || index == 0 || index == sourceStations.Count - 1
                    ? 0f
                    : PlannerDefaults.StopStartLossMinutesPerSkippedStop;
                float effectiveDwellMinutes = shouldStop ? dwellSummary.Minutes : 0f;
                float rawArrivalMinute = index == 0 ? 0f : cursorMinute;
                float arrivalMinute = shouldStop
                    ? rawArrivalMinute
                    : Math.Max(0f, rawArrivalMinute - skippedStopStartLossMinutes);
                float departureMinute = index == 0
                    ? 0f
                    : Math.Max(0f, arrivalMinute + effectiveDwellMinutes);

                PlannerStationOffset stationOffset = new PlannerStationOffset();
                stationOffset.StationId = station.id ?? string.Empty;
                stationOffset.Order = station.order;
                stationOffset.Name = station.name ?? station.id;
                stationOffset.ArrivalMinute = PlannerMath.Round4(arrivalMinute);
                stationOffset.DepartureMinute = PlannerMath.Round4(departureMinute);
                stationOffset.DwellMinutes = PlannerMath.Round4(effectiveDwellMinutes);
                stationOffset.SkippedStopStartLossMinutes = PlannerMath.Round4(skippedStopStartLossMinutes);
                stationOffset.VariabilityMinutes = dwellSummary.VariabilityMinutes;
                stationOffset.Confidence = dwellSummary.Confidence;
                stationOffset.DwellSource = shouldStop ? dwellSummary.Source : "skipPattern";
                stationOffset.ShouldStop = shouldStop;
                model.StationOffsets.Add(stationOffset);
                model.StationOffsetsById[stationOffset.StationId] = stationOffset;

                if (index >= sourceSegments.Count)
                {
                    continue;
                }

                DispatchPlannerSegmentDto segment = sourceSegments[index];
                DispatchPlannerStationDto nextStation = index + 1 < sourceStations.Count ? sourceStations[index + 1] : null;
                PlannerSegmentRuntime fallbackRuntime = ResolveSegmentRuntime(segment, null);
                PlannerObservedRuntimeSummary observedRuntime = ResolveObservedStationRuntime(
                    context,
                    sourceLineId,
                    station,
                    nextStation,
                    fallbackRuntime);
                PlannerSegmentRuntime runtime = ResolveSegmentRuntime(segment, observedRuntime);
                runtime.FromStationId = station.id ?? string.Empty;
                runtime.ToStationId = nextStation?.id ?? segment.toStationId ?? string.Empty;
                runtime.FromOrder = station.order;
                runtime.ToOrder = nextStation?.order ?? segment.toOrder;
                model.SegmentRuntimeOffsets.Add(runtime);
                if (nextStation != null)
                {
                    model.SegmentRuntimeByStationPair[station.id + "->" + nextStation.id] = runtime;
                }
                cursorMinute = departureMinute + runtime.Minutes;
            }

            model.AtomBoundaryMinuteOffsets = BuildAtomBoundaryMinuteOffsets(model);
            model.AtomBoundaryVariabilityOffsets = BuildAtomBoundaryVariabilityOffsets(model);
            if (HasUsableAtomRuntime(model))
            {
                ApplyStationTimelineFromAtomOffsets(model);
                RebuildSegmentRuntimeOffsetsFromAtomTimeline(model);
            }
            model.TotalMinuteSpan = model.StationOffsets.Count > 0
                ? model.StationOffsets[model.StationOffsets.Count - 1].DepartureMinute
                : 0f;
            return model;
        }

        private static HashSet<string> BuildExpressStopStationIdSet(
            PlannerContext context,
            string sourceLineId,
            List<DispatchPlannerStationDto> stations,
            bool treatAsVirtualExpress)
        {
            HashSet<string> stopIds = new HashSet<string>(StringComparer.Ordinal);
            if (!treatAsVirtualExpress && context.SelectedExpressStopStationIds.Length == 0)
            {
                return stopIds;
            }

            for (int i = 0; i < stations.Count; i++)
            {
                DispatchPlannerStationDto station = stations[i];
                if (context.SelectedExpressStopStationIds.Contains(station.workbenchStationId ?? string.Empty))
                {
                    stopIds.Add(station.id);
                }
            }

            return stopIds;
        }

        private static PlannerObservedRuntimeSummary ResolveStationDwellMinutes(
            PlannerContext context,
            DispatchPlannerStationDto station)
        {
            if (station != null
                && context.StopDwellByStationId.TryGetValue(station.id ?? string.Empty, out DispatchPlannerStationDwellObservationDto observed)
                && observed.sampleCount > 0
                && observed.averageMinutes > 0f)
            {
                return new PlannerObservedRuntimeSummary
                {
                    Minutes = observed.averageMinutes,
                    SampleCount = observed.sampleCount,
                    Source = "observed",
                    Confidence = observed.confidence,
                    VariabilityMinutes = PlannerMath.EstimateVariabilityMinutes(
                        observed.averageMinutes,
                        observed.confidence,
                        observed.sampleCount,
                        0f)
                };
            }

            if (station != null && station.profileDwellMinutes > 0f)
            {
                return new PlannerObservedRuntimeSummary
                {
                    Minutes = station.profileDwellMinutes,
                    SampleCount = 0,
                    Source = "profile",
                    Confidence = station.confidence > 0f ? station.confidence : 0.4f,
                    VariabilityMinutes = PlannerMath.EstimateVariabilityMinutes(
                        station.profileDwellMinutes,
                        station.confidence > 0f ? station.confidence : 0.4f,
                        0,
                        0f)
                };
            }

            return new PlannerObservedRuntimeSummary
            {
                Minutes = 0f,
                SampleCount = 0,
                Source = "fallback",
                Confidence = 0.2f,
                VariabilityMinutes = 0f
            };
        }

        private static PlannerObservedRuntimeSummary ResolveObservedStationRuntime(
            PlannerContext context,
            string lineId,
            DispatchPlannerStationDto fromStation,
            DispatchPlannerStationDto toStation,
            PlannerSegmentRuntime fallbackRuntime)
        {
            if (fromStation == null || toStation == null)
            {
                return null;
            }

            string key = (lineId ?? string.Empty)
                + "|"
                + (fromStation.workbenchStationId ?? string.Empty)
                + "->"
                + (toStation.workbenchStationId ?? string.Empty);
            if (!context.StationRuntimeByLinePair.TryGetValue(key, out PlannerObservedRuntimeSummary runtime)
                || runtime.SampleCount < PlannerDefaults.ObservedStationRuntimeMinSamples
                || !(runtime.Minutes > 0f))
            {
                return null;
            }

            float fallbackMinutes = fallbackRuntime?.Minutes ?? 0f;
            if (fallbackMinutes > 0f)
            {
                float maxAllowedMinutes = Math.Max(
                    fallbackMinutes * PlannerDefaults.ObservedStationRuntimeMaxProfileRatio,
                    fallbackMinutes + PlannerDefaults.ObservedStationRuntimeMaxProfileExtraMinutes);
                if (runtime.Minutes > maxAllowedMinutes)
                {
                    return null;
                }
            }

            return runtime;
        }

        private static PlannerSegmentRuntime ResolveSegmentRuntime(
            DispatchPlannerSegmentDto segment,
            PlannerObservedRuntimeSummary observedRuntime)
        {
            if (observedRuntime != null && observedRuntime.SampleCount > 0 && observedRuntime.Minutes > 0f)
            {
                return new PlannerSegmentRuntime
                {
                    Minutes = observedRuntime.Minutes,
                    MedianMinutes = observedRuntime.MedianMinutes,
                    AverageMinutes = observedRuntime.AverageMinutes,
                    MinMinutes = observedRuntime.MinMinutes,
                    MaxMinutes = observedRuntime.MaxMinutes,
                    Confidence = observedRuntime.Confidence > 0f ? observedRuntime.Confidence : 0.65f,
                    VariabilityMinutes = observedRuntime.VariabilityMinutes > 0f
                        ? observedRuntime.VariabilityMinutes
                        : PlannerMath.EstimateVariabilityMinutes(
                            observedRuntime.Minutes,
                            observedRuntime.Confidence > 0f ? observedRuntime.Confidence : 0.65f,
                            observedRuntime.SampleCount,
                            0f),
                    SampleCount = observedRuntime.SampleCount,
                    Source = "tripObserved",
                    BaselinePolicy = observedRuntime.BaselinePolicy ?? string.Empty
                };
            }

            float baseMinutes = segment.profileMinutes > 0f
                ? segment.profileMinutes
                : Math.Max(segment.estimatedMinutes, 0f);
            float confidence = segment.profileMinutes > 0f
                ? (segment.confidence > 0f ? segment.confidence : 0.5f)
                : (segment.confidence > 0f ? segment.confidence : 0.3f);
            return new PlannerSegmentRuntime
            {
                Minutes = baseMinutes,
                Confidence = confidence,
                VariabilityMinutes = PlannerMath.EstimateVariabilityMinutes(baseMinutes, confidence, 0, 0f),
                Source = segment.profileMinutes > 0f ? "profile" : segment.estimatedMinutes > 0f ? "estimated" : "fallback"
            };
        }

        private static bool ShouldStopAtStation(
            string kind,
            DispatchPlannerStationDto station,
            HashSet<string> expressStopStationIds,
            int stationIndex,
            int stationCount)
        {
            if (!string.Equals(kind, "express", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (expressStopStationIds == null || expressStopStationIds.Count == 0)
            {
                return true;
            }

            if (stationIndex == 0 || stationIndex == stationCount - 1)
            {
                return true;
            }

            return station != null && expressStopStationIds.Contains(station.id ?? string.Empty);
        }

        private static float ResolveTraversalSliceRuntimeMinutes(DispatchPlannerTraversalSliceDto slice)
        {
            if (slice == null)
            {
                return 0f;
            }

            return slice.observedSampleCount > 0 && slice.observedAverageMinutes > 0f
                ? slice.observedAverageMinutes
                : Math.Max(slice.modelRunMinutes, 0f);
        }

        private static float ResolveTraversalSliceVariabilityMinutes(DispatchPlannerTraversalSliceDto slice)
        {
            if (slice == null)
            {
                return 0f;
            }

            float baseMinutes = slice.observedSampleCount > 0 && slice.observedAverageMinutes > 0f
                ? slice.observedAverageMinutes
                : Math.Max(slice.modelRunMinutes, 0f);
            float confidence = slice.confidence > 0f
                ? slice.confidence
                : slice.observedSampleCount > 0 ? 0.5f : 0.3f;
            return PlannerMath.EstimateVariabilityMinutes(baseMinutes, confidence, slice.observedSampleCount, slice.observedFastMinutes);
        }

        private static bool HasUsableAtomRuntime(PlannerLineRuntimeModel model)
        {
            if (model == null
                || model.TrackAtomCount <= 0
                || model.LineTrack?.traversalSlices == null
                || model.LineTrack.traversalSlices.Length == 0)
            {
                return false;
            }

            foreach (DispatchPlannerTraversalSliceDto slice in model.LineTrack.traversalSlices)
            {
                if (slice != null && slice.endAtomIndexExclusive > slice.startAtomIndex)
                {
                    return true;
                }
            }
            return false;
        }

        private static PlannerStationOffset FindStationOffsetForTraversalSlice(
            PlannerLineRuntimeModel model,
            DispatchPlannerTraversalSliceDto slice)
        {
            if (model == null
                || slice == null
                || !string.Equals(slice.stationTraversalKind, "stop", StringComparison.Ordinal)
                || slice.stationWaypointIndex < 0)
            {
                return null;
            }

            foreach (DispatchPlannerStationDto station in model.Stations)
            {
                if (station != null
                    && station.waypointIndex == slice.stationWaypointIndex
                    && station.trackAtomIndex >= slice.startAtomIndex
                    && station.trackAtomIndex <= slice.endAtomIndexExclusive
                    && model.StationOffsetsById.TryGetValue(station.id ?? string.Empty, out PlannerStationOffset stationOffset))
                {
                    return stationOffset;
                }
            }

            return null;
        }

        private static float ResolveTraversalSliceRuntimeForStopPattern(
            PlannerLineRuntimeModel model,
            DispatchPlannerTraversalSliceDto slice,
            HashSet<string> dwellIncludedStationIds)
        {
            PlannerStationOffset stationOffset = FindStationOffsetForTraversalSlice(model, slice);
            if (stationOffset != null && !stationOffset.ShouldStop)
            {
                return Math.Max(slice?.modelRunMinutes ?? 0f, 0f);
            }

            float runtimeMinutes = ResolveTraversalSliceRuntimeMinutes(slice);
            if (stationOffset != null
                && stationOffset.ShouldStop
                && slice != null
                && slice.observedIncludesStationStop
                && slice.observedSampleCount > 0
                && slice.observedAverageMinutes > 0f
                && dwellIncludedStationIds != null)
            {
                dwellIncludedStationIds.Add(stationOffset.StationId);
            }
            return runtimeMinutes;
        }

        private static float ResolveTraversalSliceVariabilityForStopPattern(
            PlannerLineRuntimeModel model,
            DispatchPlannerTraversalSliceDto slice,
            HashSet<string> dwellIncludedStationIds)
        {
            PlannerStationOffset stationOffset = FindStationOffsetForTraversalSlice(model, slice);
            if (stationOffset != null && !stationOffset.ShouldStop)
            {
                float modelMinutes = Math.Max(slice?.modelRunMinutes ?? 0f, 0f);
                return PlannerMath.EstimateVariabilityMinutes(modelMinutes, 0.45f, 0, 0f);
            }

            float variabilityMinutes = ResolveTraversalSliceVariabilityMinutes(slice);
            if (stationOffset != null
                && stationOffset.ShouldStop
                && slice != null
                && slice.observedIncludesStationStop
                && slice.observedSampleCount > 0
                && slice.observedAverageMinutes > 0f
                && dwellIncludedStationIds != null)
            {
                dwellIncludedStationIds.Add(stationOffset.StationId);
            }
            return variabilityMinutes;
        }

        private static void FillMissingRunMinutesByAtom(
            PlannerLineRuntimeModel model,
            float[] runMinutesByAtom,
            bool[] coveredBySlice)
        {
            if (model == null || runMinutesByAtom == null || coveredBySlice == null)
            {
                return;
            }

            int trackAtomCount = runMinutesByAtom.Length;
            for (int stationIndex = 0; stationIndex + 1 < model.Stations.Count; stationIndex++)
            {
                DispatchPlannerStationDto station = model.Stations[stationIndex];
                DispatchPlannerStationDto nextStation = model.Stations[stationIndex + 1];
                if (!model.SegmentRuntimeByStationPair.TryGetValue(station.id + "->" + nextStation.id, out PlannerSegmentRuntime runtime)
                    || !(runtime.Minutes > 0f))
                {
                    continue;
                }

                int startAtomIndex = Math.Max(0, station.trackAtomIndex);
                int endAtomIndexExclusive = Math.Min(trackAtomCount, nextStation.trackAtomIndex);
                if (endAtomIndexExclusive <= startAtomIndex)
                {
                    continue;
                }

                int missingAtomCount = 0;
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                {
                    if (!coveredBySlice[atomIndex])
                    {
                        missingAtomCount++;
                    }
                }
                if (missingAtomCount <= 0)
                {
                    continue;
                }

                float perAtomMinutes = runtime.Minutes / Math.Max(1, endAtomIndexExclusive - startAtomIndex);
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                {
                    if (!coveredBySlice[atomIndex])
                    {
                        runMinutesByAtom[atomIndex] = perAtomMinutes;
                    }
                }
            }
        }

        private static void FillMissingVariabilitySquareByAtom(
            PlannerLineRuntimeModel model,
            float[] variabilitySquareByAtom,
            bool[] coveredBySlice)
        {
            if (model == null || variabilitySquareByAtom == null || coveredBySlice == null)
            {
                return;
            }

            int trackAtomCount = variabilitySquareByAtom.Length;
            for (int stationIndex = 0; stationIndex + 1 < model.Stations.Count; stationIndex++)
            {
                DispatchPlannerStationDto station = model.Stations[stationIndex];
                DispatchPlannerStationDto nextStation = model.Stations[stationIndex + 1];
                if (!model.SegmentRuntimeByStationPair.TryGetValue(station.id + "->" + nextStation.id, out PlannerSegmentRuntime runtime)
                    || !(runtime.VariabilityMinutes > 0f))
                {
                    continue;
                }

                int startAtomIndex = Math.Max(0, station.trackAtomIndex);
                int endAtomIndexExclusive = Math.Min(trackAtomCount, nextStation.trackAtomIndex);
                if (endAtomIndexExclusive <= startAtomIndex)
                {
                    continue;
                }

                float perAtomVariabilityMinutes = runtime.VariabilityMinutes / Math.Max(1, endAtomIndexExclusive - startAtomIndex);
                float perAtomVariabilitySquare = perAtomVariabilityMinutes * perAtomVariabilityMinutes;
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                {
                    if (!coveredBySlice[atomIndex])
                    {
                        variabilitySquareByAtom[atomIndex] = perAtomVariabilitySquare;
                    }
                }
            }
        }

        private static float[] BuildAtomBoundaryMinuteOffsets(PlannerLineRuntimeModel model)
        {
            int trackAtomCount = Math.Max(0, model.TrackAtomCount);
            if (trackAtomCount <= 0)
            {
                return new[] { 0f };
            }

            float[] runMinutesByAtom = new float[trackAtomCount];
            bool[] coveredBySlice = new bool[trackAtomCount];
            HashSet<string> dwellIncludedStationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchPlannerTraversalSliceDto slice in model.LineTrack?.traversalSlices ?? new DispatchPlannerTraversalSliceDto[0])
            {
                int startAtomIndex = Math.Max(0, slice.startAtomIndex);
                int endAtomIndexExclusive = Math.Min(trackAtomCount, slice.endAtomIndexExclusive);
                if (endAtomIndexExclusive <= startAtomIndex)
                {
                    continue;
                }

                float runtimeMinutes = ResolveTraversalSliceRuntimeForStopPattern(model, slice, dwellIncludedStationIds);
                int atomCount = Math.Max(1, endAtomIndexExclusive - startAtomIndex);
                float perAtomMinutes = runtimeMinutes / atomCount;
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                {
                    runMinutesByAtom[atomIndex] += perAtomMinutes;
                    coveredBySlice[atomIndex] = true;
                }
            }
            FillMissingRunMinutesByAtom(model, runMinutesByAtom, coveredBySlice);

            float[] dwellMinutesByBoundary = new float[trackAtomCount + 1];
            for (int stationIndex = 0; stationIndex < model.Stations.Count; stationIndex++)
            {
                DispatchPlannerStationDto station = model.Stations[stationIndex];
                if (!model.StationOffsetsById.TryGetValue(station.id, out PlannerStationOffset stationOffset))
                {
                    continue;
                }

                int boundaryIndex = station.trackAtomIndex;
                if (boundaryIndex < 0 || boundaryIndex > trackAtomCount || stationIndex == 0)
                {
                    continue;
                }
                if (dwellIncludedStationIds.Contains(station.id ?? string.Empty))
                {
                    continue;
                }

                dwellMinutesByBoundary[boundaryIndex] += stationOffset.DwellMinutes - stationOffset.SkippedStopStartLossMinutes;
            }

            float[] offsets = new float[trackAtomCount + 1];
            float cumulativeMinutes = 0f;
            for (int boundaryIndex = 0; boundaryIndex <= trackAtomCount; boundaryIndex++)
            {
                cumulativeMinutes += dwellMinutesByBoundary[boundaryIndex];
                offsets[boundaryIndex] = PlannerMath.Round4(cumulativeMinutes);
                if (boundaryIndex < trackAtomCount)
                {
                    cumulativeMinutes += runMinutesByAtom[boundaryIndex];
                }
            }

            return offsets;
        }

        private static float[] BuildAtomBoundaryVariabilityOffsets(PlannerLineRuntimeModel model)
        {
            int trackAtomCount = Math.Max(0, model.TrackAtomCount);
            if (trackAtomCount <= 0)
            {
                return new[] { 0f };
            }

            float[] variabilitySquareByAtom = new float[trackAtomCount];
            bool[] coveredBySlice = new bool[trackAtomCount];
            HashSet<string> dwellIncludedStationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchPlannerTraversalSliceDto slice in model.LineTrack?.traversalSlices ?? new DispatchPlannerTraversalSliceDto[0])
            {
                int startAtomIndex = Math.Max(0, slice.startAtomIndex);
                int endAtomIndexExclusive = Math.Min(trackAtomCount, slice.endAtomIndexExclusive);
                if (endAtomIndexExclusive <= startAtomIndex)
                {
                    continue;
                }

                float variabilityMinutes = ResolveTraversalSliceVariabilityForStopPattern(model, slice, dwellIncludedStationIds);
                int atomCount = Math.Max(1, endAtomIndexExclusive - startAtomIndex);
                float perAtomVariabilityMinutes = variabilityMinutes / atomCount;
                for (int atomIndex = startAtomIndex; atomIndex < endAtomIndexExclusive; atomIndex++)
                {
                    variabilitySquareByAtom[atomIndex] += perAtomVariabilityMinutes * perAtomVariabilityMinutes;
                    coveredBySlice[atomIndex] = true;
                }
            }
            FillMissingVariabilitySquareByAtom(model, variabilitySquareByAtom, coveredBySlice);

            float[] dwellVariabilitySquareByBoundary = new float[trackAtomCount + 1];
            for (int stationIndex = 0; stationIndex < model.Stations.Count; stationIndex++)
            {
                DispatchPlannerStationDto station = model.Stations[stationIndex];
                if (!model.StationOffsetsById.TryGetValue(station.id, out PlannerStationOffset stationOffset))
                {
                    continue;
                }

                int boundaryIndex = station.trackAtomIndex;
                if (boundaryIndex < 0 || boundaryIndex > trackAtomCount || stationIndex == 0)
                {
                    continue;
                }
                if (dwellIncludedStationIds.Contains(station.id ?? string.Empty))
                {
                    continue;
                }

                dwellVariabilitySquareByBoundary[boundaryIndex] += stationOffset.VariabilityMinutes * stationOffset.VariabilityMinutes;
            }

            float[] offsets = new float[trackAtomCount + 1];
            float cumulativeSquare = 0f;
            for (int boundaryIndex = 0; boundaryIndex <= trackAtomCount; boundaryIndex++)
            {
                cumulativeSquare += dwellVariabilitySquareByBoundary[boundaryIndex];
                offsets[boundaryIndex] = PlannerMath.Round4((float)Math.Sqrt(cumulativeSquare));
                if (boundaryIndex < trackAtomCount)
                {
                    cumulativeSquare += variabilitySquareByAtom[boundaryIndex];
                }
            }

            return offsets;
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

        private static void ApplyStationTimelineFromAtomOffsets(PlannerLineRuntimeModel model)
        {
            if (model == null || model.AtomBoundaryMinuteOffsets == null || model.AtomBoundaryMinuteOffsets.Length == 0)
            {
                return;
            }

            for (int stationIndex = 0; stationIndex < model.Stations.Count && stationIndex < model.StationOffsets.Count; stationIndex++)
            {
                DispatchPlannerStationDto station = model.Stations[stationIndex];
                PlannerStationOffset stationOffset = model.StationOffsets[stationIndex];
                float departureMinute = stationIndex == 0
                    ? 0f
                    : GetAtomBoundaryMinuteOffset(model.AtomBoundaryMinuteOffsets, station.trackAtomIndex);
                float arrivalMinute = stationOffset.ShouldStop
                    ? Math.Max(0f, departureMinute - stationOffset.DwellMinutes)
                    : departureMinute;
                stationOffset.ArrivalMinute = PlannerMath.Round4(arrivalMinute);
                stationOffset.DepartureMinute = PlannerMath.Round4(departureMinute);
            }
        }

        private static float GetStationVariabilityMinute(PlannerLineRuntimeModel model, DispatchPlannerStationDto station)
        {
            if (model == null || station == null)
            {
                return 0f;
            }

            return GetAtomBoundaryMinuteOffset(model.AtomBoundaryVariabilityOffsets, station.trackAtomIndex);
        }

        private static void RebuildSegmentRuntimeOffsetsFromAtomTimeline(PlannerLineRuntimeModel model)
        {
            if (model == null)
            {
                return;
            }

            model.SegmentRuntimeOffsets.Clear();
            model.SegmentRuntimeByStationPair.Clear();
            for (int index = 0; index + 1 < model.Stations.Count; index++)
            {
                DispatchPlannerStationDto station = model.Stations[index];
                DispatchPlannerStationDto nextStation = model.Stations[index + 1];
                PlannerStationOffset stationOffset = model.StationOffsets[index];
                PlannerStationOffset nextStationOffset = model.StationOffsets[index + 1];
                float minutes = Math.Max(0f, nextStationOffset.ArrivalMinute - stationOffset.DepartureMinute);
                float variabilityMinutes = Math.Max(
                    0f,
                    GetStationVariabilityMinute(model, nextStation) - GetStationVariabilityMinute(model, station));
                PlannerSegmentRuntime runtime = new PlannerSegmentRuntime
                {
                    FromStationId = station.id ?? string.Empty,
                    ToStationId = nextStation.id ?? string.Empty,
                    FromOrder = station.order,
                    ToOrder = nextStation.order,
                    Minutes = PlannerMath.Round4(minutes),
                    AverageMinutes = PlannerMath.Round4(minutes),
                    Confidence = 0.65f,
                    VariabilityMinutes = PlannerMath.Round4(variabilityMinutes),
                    Source = "atomSlice"
                };
                model.SegmentRuntimeOffsets.Add(runtime);
                model.SegmentRuntimeByStationPair[station.id + "->" + nextStation.id] = runtime;
            }
        }
    }
}
