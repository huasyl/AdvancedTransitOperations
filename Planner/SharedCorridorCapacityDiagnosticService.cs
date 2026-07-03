using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RapidTransitMod.Planner
{
    internal sealed class SharedCorridorCapacityDiagnosticService
    {
        public PlannerCapacityDiagnostic Analyze(
            PlannerContext context,
            PlannerRuntimeCatalog runtimeCatalog,
            IEnumerable<PlannerWorkingRow> rows)
        {
            PlannerCapacityDiagnostic empty = new PlannerCapacityDiagnostic();
            if (context == null || runtimeCatalog == null)
            {
                empty.Summary = "capacity diagnostic skipped: missing planner context.";
                return empty;
            }

            List<DispatchPlannerSharedCorridorDto> exportedCorridors =
                (context.Snapshot.currentTrackScenario?.sharedCorridors
                    ?? Array.Empty<DispatchPlannerSharedCorridorDto>())
                .Where(corridor => corridor != null)
                .ToList();
            SharedCorridorCapacityRequestScope request = BuildRequestScope(context);
            List<CapacityTripRecord> trips = BuildTripRecords(runtimeCatalog, rows);
            List<DispatchPlannerSharedCorridorDto> validCorridors = exportedCorridors
                .Where(IsValidCorridor)
                .ToList();
            List<DispatchPlannerSharedCorridorDto> relevantCorridors = validCorridors
                .Where(corridor => IsRelevantCorridor(corridor, request))
                .GroupBy(BuildCanonicalCorridorResourceKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            List<CapacityProjectedInterval> projectedIntervals = BuildProjectedIntervals(relevantCorridors, request);
            List<CapacityElementarySection> elementarySections = BuildElementarySections(projectedIntervals);
            List<CapacitySectionGroup> reportGroups = BuildReportGroups(elementarySections);
            List<CapacitySectionResult> sectionResults = reportGroups
                .Select(group => AnalyzeSectionGroup(context, group, trips, request, PlannerDefaults.MinSharedGapMinutes))
                .Where(result => result != null)
                .OrderByDescending(result => VerdictRank(result.Verdict))
                .ThenByDescending(result => result.RequiredMaxWaitMinutes)
                .ThenByDescending(result => result.CapacityConsumptionRatio)
                .ToList();

            CapacitySectionResult tightest = sectionResults.FirstOrDefault();
            PlannerCapacityDiagnostic diagnostic = new PlannerCapacityDiagnostic
            {
                Success = tightest != null,
                OverallVerdict = ResolveOverallVerdict(sectionResults),
                CapacityLikely = sectionResults.Any(result => string.Equals(result.Verdict, "infeasible", StringComparison.Ordinal)),
                MinGapMinutes = PlannerDefaults.MinSharedGapMinutes,
                ExportedSharedCorridorCount = exportedCorridors.Count,
                ValidSharedCorridorCount = validCorridors.Count,
                RelevantSharedCorridorCount = relevantCorridors.Count,
                ProjectedIntervalCount = projectedIntervals.Count,
                ElementarySectionCount = elementarySections.Count,
                ReportGroupCount = reportGroups.Count
            };

            if (tightest == null)
            {
                diagnostic.Reason = "insufficientData";
                diagnostic.Summary = "capacity diagnostic found no usable shared corridor section.";
                return diagnostic;
            }

            diagnostic.HighestCapacityConsumptionRatio = Round4(tightest.CapacityConsumptionRatio);
            diagnostic.HighestCapacityConsumptionPercent = Round2(tightest.CapacityConsumptionRatio * 100f);
            diagnostic.HighestCompressedSpanMinutes = Round2(tightest.CompressedSpanMinutes);
            diagnostic.HighestZeroGapConsumptionRatio = Round4(tightest.ZeroGapConsumptionRatio);
            diagnostic.RequiredMaxShiftMinutes = Round2(tightest.RequiredMaxShiftMinutes);
            diagnostic.RequiredMaxWaitMinutes = Round2(tightest.RequiredMaxWaitMinutes);
            diagnostic.MinResidualSlackMinutes = Round2(tightest.MinResidualSlackMinutes);
            diagnostic.CriticalResourceId = tightest.ResourceId;
            diagnostic.CriticalTargetLineId = tightest.TargetLineId;
            diagnostic.CriticalCoverageLineIds = tightest.CoverageLineIds;
            diagnostic.CriticalCoverageLines = tightest.CoverageLineIds.Select(lineId => ResolveLineName(context, lineId)).ToArray();
            diagnostic.CriticalTargetStartAtomIndex = tightest.TargetStartAtomIndex;
            diagnostic.CriticalTargetEndAtomIndexExclusive = tightest.TargetEndAtomIndexExclusive;
            diagnostic.TripCount = tightest.TripCount;
            diagnostic.Reason = tightest.Reason;
            diagnostic.Summary = BuildSummary(diagnostic);
            return diagnostic;
        }

        private static SharedCorridorCapacityRequestScope BuildRequestScope(PlannerContext context)
        {
            int windowEndExclusive = Math.Min(1440, context.WindowEndMinute + 1);
            return new SharedCorridorCapacityRequestScope
            {
                WindowStartMinute = context.WindowStartMinute,
                WindowEndExclusiveMinute = windowEndExclusive,
                WindowMinutes = Math.Max(1, windowEndExclusive - context.WindowStartMinute),
                TargetLineIds = new HashSet<string>(context.TargetLineIds ?? Array.Empty<string>(), StringComparer.Ordinal),
                AdjustableLineIds = new HashSet<string>(context.AdjustableLineIds ?? Array.Empty<string>(), StringComparer.Ordinal),
                FixedLineIds = new HashSet<string>(context.FixedLineIds ?? Array.Empty<string>(), StringComparer.Ordinal),
                MaxOffsetMinutes = Math.Max(0, context.Request.maxOffsetMinutes),
                MaxLocalRetimeMinutes = Math.Max(0, context.Request.maxLocalRetimeMinutes),
                MaxLocalWaitMinutes = Math.Max(0, context.Request.maxLocalWaitMinutes)
            };
        }

        private static List<CapacityTripRecord> BuildTripRecords(
            PlannerRuntimeCatalog runtimeCatalog,
            IEnumerable<PlannerWorkingRow> rows)
        {
            List<CapacityTripRecord> trips = new List<CapacityTripRecord>();
            foreach (PlannerWorkingRow row in rows ?? Enumerable.Empty<PlannerWorkingRow>())
            {
                if (row == null
                    || string.IsNullOrEmpty(row.LineId)
                    || !runtimeCatalog.ModelsByLineId.TryGetValue(row.LineId, out PlannerLineRuntimeModel model))
                {
                    continue;
                }

                trips.Add(new CapacityTripRecord
                {
                    TripId = row.Id ?? string.Empty,
                    LineId = row.LineId,
                    Kind = row.Kind ?? string.Empty,
                    DepartureMinute = row.Minute,
                    AtomBoundaryMinuteOffsets = model.AtomBoundaryMinuteOffsets ?? Array.Empty<float>()
                });
            }

            return trips;
        }

        private static bool IsValidCorridor(DispatchPlannerSharedCorridorDto corridor)
        {
            return corridor != null
                && string.Equals(corridor.traversalRelation, "sameDirection", StringComparison.OrdinalIgnoreCase)
                && !corridor.hasMirroredContext
                && corridor.orderedRun > 0
                && corridor.physicalOverlap > 0;
        }

        private static bool IsRelevantCorridor(
            DispatchPlannerSharedCorridorDto corridor,
            SharedCorridorCapacityRequestScope request)
        {
            return request.TargetLineIds.Count == 0
                || request.TargetLineIds.Contains(corridor.lineId ?? string.Empty)
                || request.TargetLineIds.Contains(corridor.otherLineId ?? string.Empty);
        }

        private static string BuildCanonicalCorridorResourceKey(DispatchPlannerSharedCorridorDto corridor)
        {
            string left = (corridor.lineId ?? string.Empty) + ":" + corridor.lineStartAtomIndex.ToString(CultureInfo.InvariantCulture) + "-" + corridor.lineEndAtomIndexExclusive.ToString(CultureInfo.InvariantCulture);
            string right = (corridor.otherLineId ?? string.Empty) + ":" + corridor.otherStartAtomIndex.ToString(CultureInfo.InvariantCulture) + "-" + corridor.otherEndAtomIndexExclusive.ToString(CultureInfo.InvariantCulture);
            return string.CompareOrdinal(left, right) <= 0 ? left + "|" + right : right + "|" + left;
        }

        private static List<CapacityProjectedInterval> BuildProjectedIntervals(
            List<DispatchPlannerSharedCorridorDto> corridors,
            SharedCorridorCapacityRequestScope request)
        {
            List<CapacityProjectedInterval> intervals = new List<CapacityProjectedInterval>();
            foreach (DispatchPlannerSharedCorridorDto corridor in corridors)
            {
                bool lineIsTarget = request.TargetLineIds.Count == 0 || request.TargetLineIds.Contains(corridor.lineId ?? string.Empty);
                bool otherIsTarget = request.TargetLineIds.Contains(corridor.otherLineId ?? string.Empty);
                if (lineIsTarget)
                {
                    intervals.Add(new CapacityProjectedInterval
                    {
                        SourceCorridorId = corridor.id ?? string.Empty,
                        TargetLineId = corridor.lineId ?? string.Empty,
                        OtherLineId = corridor.otherLineId ?? string.Empty,
                        TargetStartAtomIndex = corridor.lineStartAtomIndex,
                        TargetEndAtomIndexExclusive = corridor.lineEndAtomIndexExclusive,
                        OtherStartBoundaryPosition = corridor.otherStartAtomIndex,
                        OtherEndBoundaryPosition = corridor.otherEndAtomIndexExclusive
                    });
                }

                if (otherIsTarget)
                {
                    intervals.Add(new CapacityProjectedInterval
                    {
                        SourceCorridorId = corridor.id ?? string.Empty,
                        TargetLineId = corridor.otherLineId ?? string.Empty,
                        OtherLineId = corridor.lineId ?? string.Empty,
                        TargetStartAtomIndex = corridor.otherStartAtomIndex,
                        TargetEndAtomIndexExclusive = corridor.otherEndAtomIndexExclusive,
                        OtherStartBoundaryPosition = corridor.lineStartAtomIndex,
                        OtherEndBoundaryPosition = corridor.lineEndAtomIndexExclusive
                    });
                }
            }

            return intervals
                .Where(interval => interval.TargetStartAtomIndex >= 0
                    && interval.TargetEndAtomIndexExclusive > interval.TargetStartAtomIndex
                    && !string.IsNullOrEmpty(interval.TargetLineId))
                .OrderBy(interval => interval.TargetLineId, StringComparer.Ordinal)
                .ThenBy(interval => interval.TargetStartAtomIndex)
                .ThenBy(interval => interval.TargetEndAtomIndexExclusive)
                .ThenBy(interval => interval.OtherLineId, StringComparer.Ordinal)
                .ToList();
        }

        private static List<CapacityElementarySection> BuildElementarySections(List<CapacityProjectedInterval> intervals)
        {
            List<CapacityElementarySection> sections = new List<CapacityElementarySection>();
            foreach (IGrouping<string, CapacityProjectedInterval> targetGroup in intervals.GroupBy(interval => interval.TargetLineId, StringComparer.Ordinal))
            {
                List<int> boundaries = targetGroup
                    .SelectMany(interval => new[] { interval.TargetStartAtomIndex, interval.TargetEndAtomIndexExclusive })
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();
                for (int boundaryIndex = 0; boundaryIndex < boundaries.Count - 1; boundaryIndex++)
                {
                    int sectionStart = boundaries[boundaryIndex];
                    int sectionEnd = boundaries[boundaryIndex + 1];
                    List<CapacityProjectedInterval> covering = targetGroup
                        .Where(interval => interval.TargetStartAtomIndex <= sectionStart && interval.TargetEndAtomIndexExclusive >= sectionEnd)
                        .ToList();
                    if (sectionEnd <= sectionStart || covering.Count == 0)
                    {
                        continue;
                    }

                    CapacityElementarySection section = new CapacityElementarySection
                    {
                        ResourceId = "capacity-section-" + targetGroup.Key + "-" + sections.Count.ToString(CultureInfo.InvariantCulture),
                        TargetLineId = targetGroup.Key,
                        TargetStartAtomIndex = sectionStart,
                        TargetEndAtomIndexExclusive = sectionEnd
                    };
                    section.AddLineRange(targetGroup.Key, sectionStart, sectionEnd);
                    foreach (CapacityProjectedInterval interval in covering)
                    {
                        if (!string.IsNullOrEmpty(interval.SourceCorridorId))
                        {
                            section.SourceCorridorIds.Add(interval.SourceCorridorId);
                        }
                        if (!string.IsNullOrEmpty(interval.OtherLineId))
                        {
                            section.CoverageLineIds.Add(interval.OtherLineId);
                        }
                        Tuple<float, float> otherRange = ProjectOtherBoundaryRange(interval, sectionStart, sectionEnd);
                        section.AddLineRange(interval.OtherLineId, otherRange.Item1, otherRange.Item2);
                    }

                    section.SourceCorridorIds = section.SourceCorridorIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    section.CoverageLineIds = section.CoverageLineIds
                        .Append(targetGroup.Key)
                        .Where(lineId => !string.IsNullOrEmpty(lineId))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(lineId => string.Equals(lineId, targetGroup.Key, StringComparison.Ordinal) ? 0 : 1)
                        .ThenBy(lineId => lineId, StringComparer.Ordinal)
                        .ToList();
                    sections.Add(section);
                }
            }

            return sections;
        }

        private static Tuple<float, float> ProjectOtherBoundaryRange(
            CapacityProjectedInterval interval,
            int targetSectionStartAtomIndex,
            int targetSectionEndAtomIndexExclusive)
        {
            float targetSpan = Math.Max(1f, interval.TargetEndAtomIndexExclusive - interval.TargetStartAtomIndex);
            float startRatio = (targetSectionStartAtomIndex - interval.TargetStartAtomIndex) / targetSpan;
            float endRatio = (targetSectionEndAtomIndexExclusive - interval.TargetStartAtomIndex) / targetSpan;
            float otherSpan = interval.OtherEndBoundaryPosition - interval.OtherStartBoundaryPosition;
            float otherStart = interval.OtherStartBoundaryPosition + otherSpan * startRatio;
            float otherEnd = interval.OtherStartBoundaryPosition + otherSpan * endRatio;
            return Tuple.Create(Math.Min(otherStart, otherEnd), Math.Max(otherStart, otherEnd));
        }

        private static List<CapacitySectionGroup> BuildReportGroups(List<CapacityElementarySection> sections)
        {
            List<CapacitySectionGroup> groups = new List<CapacitySectionGroup>();
            foreach (IGrouping<string, CapacityElementarySection> targetGroup in sections.GroupBy(section => section.TargetLineId, StringComparer.Ordinal))
            {
                CapacitySectionGroup current = null;
                foreach (CapacityElementarySection section in targetGroup.OrderBy(item => item.TargetStartAtomIndex).ThenBy(item => item.TargetEndAtomIndexExclusive))
                {
                    if (current == null || !current.CanMerge(section))
                    {
                        current = new CapacitySectionGroup
                        {
                            ResourceId = "capacity-group-" + targetGroup.Key + "-" + groups.Count.ToString(CultureInfo.InvariantCulture),
                            TargetLineId = section.TargetLineId
                        };
                        current.Append(section);
                        groups.Add(current);
                        continue;
                    }

                    current.Append(section);
                }
            }

            return groups;
        }

        private static CapacitySectionResult AnalyzeSectionGroup(
            PlannerContext context,
            CapacitySectionGroup group,
            List<CapacityTripRecord> trips,
            SharedCorridorCapacityRequestScope request,
            float minGapMinutes)
        {
            List<CapacitySectionResult> childResults = group.Sections.Count > 1
                ? group.Sections.Select(section => AnalyzeElementarySection(context, section, trips, request, minGapMinutes)).ToList()
                : new List<CapacitySectionResult>();
            CapacitySectionResult criticalChild = childResults
                .OrderByDescending(result => VerdictRank(result.Verdict))
                .ThenByDescending(result => result.CapacityConsumptionRatio)
                .FirstOrDefault();

            CapacityCompressionResult summary = criticalChild == null
                ? AnalyzeCompression(group, trips, request, minGapMinutes)
                : new CapacityCompressionResult
                {
                    CompressedSpanMinutes = criticalChild.CompressedSpanMinutes,
                    CapacityConsumptionRatio = criticalChild.CapacityConsumptionRatio,
                    ZeroGapConsumptionRatio = criticalChild.ZeroGapConsumptionRatio,
                    MinResidualSlackMinutes = criticalChild.MinResidualSlackMinutes,
                    RequiredMaxShiftMinutes = criticalChild.RequiredMaxShiftMinutes,
                    RequiredMaxWaitMinutes = criticalChild.RequiredMaxWaitMinutes,
                    HasShiftLimitExceeded = criticalChild.HasShiftLimitExceeded,
                    HasWaitLimitExceeded = criticalChild.HasWaitLimitExceeded,
                    HasFixedLineBlocker = criticalChild.HasFixedLineBlocker,
                    TripCount = criticalChild.TripCount
                };

            CapacitySectionResult result = new CapacitySectionResult
            {
                ResourceId = group.ResourceId,
                TargetLineId = group.TargetLineId,
                TargetStartAtomIndex = group.TargetStartAtomIndex,
                TargetEndAtomIndexExclusive = group.TargetEndAtomIndexExclusive,
                CoverageLineIds = group.CoverageLineIds.ToArray(),
                TripCount = summary.TripCount,
                CompressedSpanMinutes = Round2(summary.CompressedSpanMinutes),
                CapacityConsumptionRatio = request.WindowMinutes > 0 ? Round4(summary.CompressedSpanMinutes / request.WindowMinutes) : 0f,
                ZeroGapConsumptionRatio = summary.ZeroGapConsumptionRatio,
                MinResidualSlackMinutes = Round2(summary.MinResidualSlackMinutes),
                RequiredMaxShiftMinutes = Round2(summary.RequiredMaxShiftMinutes),
                RequiredMaxWaitMinutes = Round2(summary.RequiredMaxWaitMinutes),
                HasShiftLimitExceeded = summary.HasShiftLimitExceeded,
                HasWaitLimitExceeded = summary.HasWaitLimitExceeded,
                HasFixedLineBlocker = summary.HasFixedLineBlocker
            };
            Classify(result);
            return result;
        }

        private static CapacitySectionResult AnalyzeElementarySection(
            PlannerContext context,
            CapacityElementarySection section,
            List<CapacityTripRecord> trips,
            SharedCorridorCapacityRequestScope request,
            float minGapMinutes)
        {
            CapacitySectionGroup group = new CapacitySectionGroup
            {
                ResourceId = section.ResourceId,
                TargetLineId = section.TargetLineId
            };
            group.Append(section);
            return AnalyzeSectionGroup(context, group, trips, request, minGapMinutes);
        }

        private static CapacityCompressionResult AnalyzeCompression(
            CapacitySectionGroup group,
            List<CapacityTripRecord> trips,
            SharedCorridorCapacityRequestScope request,
            float minGapMinutes)
        {
            int axisSampleCount = Math.Max(2, Math.Min(96, group.LineRanges.Values
                .Select(range => (int)Math.Ceiling(range.EndBoundaryPosition - range.StartBoundaryPosition))
                .DefaultIfEmpty(1)
                .Max() + 1));
            List<CapacityOccupation> occupations = new List<CapacityOccupation>();
            foreach (CapacityLineAxisRange range in group.LineRanges.Values)
            {
                occupations.AddRange(ProjectOccupations(
                    trips.Where(trip => string.Equals(trip.LineId, range.LineId, StringComparison.Ordinal)),
                    range.LineId,
                    range.StartBoundaryPosition,
                    range.EndBoundaryPosition,
                    axisSampleCount,
                    request));
            }

            occupations = occupations
                .Where(item => item.ExitMinute > item.EntryMinute)
                .OrderBy(item => item.EntryMinute)
                .ThenBy(item => item.LineId, StringComparer.Ordinal)
                .ThenBy(item => item.TripId, StringComparer.Ordinal)
                .ToList();

            CapacityCompressionResult result = CompressOccupations(occupations, request, minGapMinutes);
            CapacityCompressionResult zeroGap = CompressOccupations(occupations, request, 0f);
            result.TripCount = occupations.Count;
            result.CapacityConsumptionRatio = request.WindowMinutes > 0 ? result.CompressedSpanMinutes / request.WindowMinutes : 0f;
            result.ZeroGapConsumptionRatio = request.WindowMinutes > 0 ? zeroGap.CompressedSpanMinutes / request.WindowMinutes : 0f;
            return result;
        }

        private static IEnumerable<CapacityOccupation> ProjectOccupations(
            IEnumerable<CapacityTripRecord> trips,
            string lineId,
            float startBoundaryPosition,
            float endBoundaryPosition,
            int axisSampleCount,
            SharedCorridorCapacityRequestScope request)
        {
            if (startBoundaryPosition < 0f || endBoundaryPosition <= startBoundaryPosition)
            {
                yield break;
            }

            foreach (CapacityTripRecord trip in trips)
            {
                float entry = trip.DepartureMinute + BoundaryOffsetAt(trip.AtomBoundaryMinuteOffsets, startBoundaryPosition);
                float exit = trip.DepartureMinute + BoundaryOffsetAt(trip.AtomBoundaryMinuteOffsets, endBoundaryPosition);
                if (exit <= request.WindowStartMinute || entry >= request.WindowEndExclusiveMinute)
                {
                    continue;
                }

                float clippedEntry = Math.Max(entry, request.WindowStartMinute);
                float clippedExit = Math.Min(exit, request.WindowEndExclusiveMinute);
                if (clippedExit <= clippedEntry)
                {
                    continue;
                }

                CapacityLineRole role = request.ResolveRole(lineId);
                yield return new CapacityOccupation
                {
                    TripId = trip.TripId,
                    LineId = lineId,
                    Role = role.Name,
                    EntryMinute = clippedEntry,
                    ExitMinute = clippedExit,
                    DurationMinutes = clippedExit - clippedEntry,
                    AxisMinuteOffsets = BuildAxisMinuteOffsets(trip.AtomBoundaryMinuteOffsets, startBoundaryPosition, endBoundaryPosition, axisSampleCount),
                    EarliestStartMinute = clippedEntry - role.EarlierBudgetMinutes,
                    LatestStartMinute = clippedEntry + role.LaterBudgetMinutes,
                    IsMovable = role.EarlierBudgetMinutes > 0f || role.LaterBudgetMinutes > 0f,
                    IsAdjustableLocal = role.IsAdjustableLocal
                };
            }
        }

        private static CapacityCompressionResult CompressOccupations(
            List<CapacityOccupation> occupations,
            SharedCorridorCapacityRequestScope request,
            float minGapMinutes)
        {
            CapacityCompressionResult result = new CapacityCompressionResult();
            if (occupations.Count == 0)
            {
                return result;
            }

            List<List<CapacityOccupation>> groups = BuildInteractionGroups(occupations, minGapMinutes);
            foreach (List<CapacityOccupation> group in groups)
            {
                CompressInteractionGroup(group, request, minGapMinutes, result);
            }

            return result;
        }

        private static List<List<CapacityOccupation>> BuildInteractionGroups(List<CapacityOccupation> occupations, float minGapMinutes)
        {
            List<List<CapacityOccupation>> groups = new List<List<CapacityOccupation>>();
            List<CapacityOccupation> current = new List<CapacityOccupation>();
            float currentMaxExit = float.MinValue;
            foreach (CapacityOccupation occupation in occupations)
            {
                if (current.Count > 0 && occupation.EntryMinute >= currentMaxExit + minGapMinutes)
                {
                    groups.Add(current);
                    current = new List<CapacityOccupation>();
                    currentMaxExit = float.MinValue;
                }

                current.Add(occupation);
                currentMaxExit = Math.Max(currentMaxExit, occupation.ExitMinute);
            }

            if (current.Count > 0)
            {
                groups.Add(current);
            }
            return groups;
        }

        private static void CompressInteractionGroup(
            List<CapacityOccupation> occupations,
            SharedCorridorCapacityRequestScope request,
            float minGapMinutes,
            CapacityCompressionResult result)
        {
            float firstStart = 0f;
            float lastEnd = 0f;
            List<CapacityOccupation> compressedItems = new List<CapacityOccupation>();
            CapacityOccupation previous = null;
            CapacityOccupation previousOriginal = null;
            for (int i = 0; i < occupations.Count; i++)
            {
                CapacityOccupation original = occupations[i];
                CapacityOccupation compressed = original.Clone();
                float earliestByPrevious = compressed.EarliestStartMinute;
                for (int previousIndex = 0; previousIndex < compressedItems.Count; previousIndex++)
                {
                    CapacityOccupation candidate = compressedItems[previousIndex];
                    int sampleCount = Math.Min(candidate.AxisMinuteOffsets.Length, compressed.AxisMinuteOffsets.Length);
                    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                    {
                        float requiredStart = candidate.CompressedEntryMinute
                            + candidate.AxisMinuteOffsets[sampleIndex]
                            + minGapMinutes
                            - compressed.AxisMinuteOffsets[sampleIndex];
                        earliestByPrevious = Math.Max(earliestByPrevious, requiredStart);
                    }
                }

                compressed.CompressedEntryMinute = Math.Max(compressed.EarliestStartMinute, earliestByPrevious);
                compressed.CompressedExitMinute = compressed.CompressedEntryMinute + compressed.DurationMinutes;
                float shift = compressed.CompressedEntryMinute - original.EntryMinute;
                result.RequiredMaxShiftMinutes = Math.Max(result.RequiredMaxShiftMinutes, Math.Abs(shift));
                if (original.IsAdjustableLocal && shift > 0f)
                {
                    result.RequiredMaxWaitMinutes = Math.Max(result.RequiredMaxWaitMinutes, shift);
                }
                if (compressed.CompressedEntryMinute > compressed.LatestStartMinute + 0.001f)
                {
                    result.HasShiftLimitExceeded = true;
                    if (!original.IsMovable)
                    {
                        result.HasFixedLineBlocker = true;
                    }
                }
                if (original.IsAdjustableLocal && shift > request.MaxLocalWaitMinutes + 0.001f)
                {
                    result.HasWaitLimitExceeded = true;
                }
                if (previous != null && previousOriginal != null)
                {
                    float residualSlack = ComputeMinimumAxisGap(previous, compressed) - minGapMinutes;
                    result.MinResidualSlackMinutes = result.HasResidualSlack
                        ? Math.Min(result.MinResidualSlackMinutes, residualSlack)
                        : residualSlack;
                    result.HasResidualSlack = true;
                }

                if (i == 0)
                {
                    firstStart = compressed.CompressedEntryMinute;
                }
                lastEnd = compressed.CompressedExitMinute;
                previous = compressed;
                previousOriginal = original;
                compressedItems.Add(compressed);
            }

            result.CompressedSpanMinutes += Math.Max(0f, lastEnd - firstStart);
        }

        private static float ComputeMinimumAxisGap(CapacityOccupation previous, CapacityOccupation current)
        {
            int sampleCount = Math.Min(previous.AxisMinuteOffsets.Length, current.AxisMinuteOffsets.Length);
            if (sampleCount == 0)
            {
                return current.CompressedEntryMinute - previous.CompressedEntryMinute;
            }

            float minGap = float.MaxValue;
            for (int i = 0; i < sampleCount; i++)
            {
                float gap = current.CompressedEntryMinute + current.AxisMinuteOffsets[i]
                    - previous.CompressedEntryMinute - previous.AxisMinuteOffsets[i];
                minGap = Math.Min(minGap, gap);
            }

            return minGap == float.MaxValue ? 0f : minGap;
        }

        private static float BoundaryOffsetAt(float[] offsets, float boundaryPosition)
        {
            if (offsets == null || offsets.Length == 0)
            {
                return 0f;
            }

            float clamped = Math.Max(0f, Math.Min(boundaryPosition, offsets.Length - 1));
            int lower = (int)Math.Floor(clamped);
            int upper = (int)Math.Ceiling(clamped);
            if (lower == upper)
            {
                return offsets[lower];
            }

            float ratio = clamped - lower;
            return offsets[lower] + (offsets[upper] - offsets[lower]) * ratio;
        }

        private static float[] BuildAxisMinuteOffsets(float[] offsets, float startBoundaryPosition, float endBoundaryPosition, int axisSampleCount)
        {
            axisSampleCount = Math.Max(2, axisSampleCount);
            float[] result = new float[axisSampleCount];
            float startOffset = BoundaryOffsetAt(offsets, startBoundaryPosition);
            float boundarySpan = Math.Max(0.0001f, endBoundaryPosition - startBoundaryPosition);
            for (int i = 0; i < result.Length; i++)
            {
                float ratio = result.Length == 1 ? 0f : (float)i / (result.Length - 1);
                float boundaryPosition = startBoundaryPosition + boundarySpan * ratio;
                result[i] = Math.Max(0f, BoundaryOffsetAt(offsets, boundaryPosition) - startOffset);
            }

            return result;
        }

        private static void Classify(CapacitySectionResult result)
        {
            if (result.TripCount < 2)
            {
                result.Verdict = "feasible";
                result.Reason = "insufficientData";
                return;
            }

            if (result.CapacityConsumptionRatio >= 1f)
            {
                result.Verdict = "infeasible";
                result.Reason = "saturatedSection";
                return;
            }

            if (result.HasFixedLineBlocker || result.HasWaitLimitExceeded || result.HasShiftLimitExceeded)
            {
                result.Verdict = "requestLimited";
                result.Reason = result.HasFixedLineBlocker ? "fixedLine" : (result.HasWaitLimitExceeded ? "waitLimit" : "shiftLimit");
                return;
            }

            if (result.CapacityConsumptionRatio >= 0.9f || result.MinResidualSlackMinutes <= 0.25f)
            {
                result.Verdict = "fragile";
                result.Reason = result.CapacityConsumptionRatio >= 0.9f ? "saturatedSection" : "minGap";
                return;
            }

            result.Verdict = "feasible";
            result.Reason = "none";
        }

        private static string ResolveOverallVerdict(List<CapacitySectionResult> results)
        {
            if (results == null || results.Count == 0)
            {
                return "insufficientData";
            }

            if (results.Any(result => string.Equals(result.Verdict, "infeasible", StringComparison.Ordinal)))
            {
                return "infeasible";
            }

            if (results.Any(result => string.Equals(result.Verdict, "requestLimited", StringComparison.Ordinal)))
            {
                return "requestLimited";
            }

            return results.Any(result => string.Equals(result.Verdict, "fragile", StringComparison.Ordinal))
                ? "fragile"
                : "feasible";
        }

        private static int VerdictRank(string verdict)
        {
            if (string.Equals(verdict, "infeasible", StringComparison.Ordinal))
            {
                return 3;
            }
            if (string.Equals(verdict, "requestLimited", StringComparison.Ordinal))
            {
                return 2;
            }
            return string.Equals(verdict, "fragile", StringComparison.Ordinal) ? 1 : 0;
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

            return context.LinesById.TryGetValue(lineId, out DispatchPlannerLineDto line)
                ? line.name ?? lineId
                : lineId;
        }

        private static string BuildSummary(PlannerCapacityDiagnostic diagnostic)
        {
            return "verdict=" + diagnostic.OverallVerdict
                + " highestConsumption=" + diagnostic.HighestCapacityConsumptionPercent.ToString("0.##", CultureInfo.InvariantCulture)
                + "% critical=" + string.Join(" / ", diagnostic.CriticalCoverageLines ?? Array.Empty<string>())
                + " atoms=" + diagnostic.CriticalTargetStartAtomIndex.ToString(CultureInfo.InvariantCulture)
                + "-" + diagnostic.CriticalTargetEndAtomIndexExclusive.ToString(CultureInfo.InvariantCulture)
                + " reason=" + diagnostic.Reason;
        }

        private static float Round2(float value)
        {
            return (float)Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        private static float Round4(float value)
        {
            return (float)Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        private sealed class SharedCorridorCapacityRequestScope
        {
            public int WindowStartMinute;
            public int WindowEndExclusiveMinute;
            public int WindowMinutes = 1440;
            public int MaxOffsetMinutes;
            public int MaxLocalRetimeMinutes;
            public int MaxLocalWaitMinutes;
            public HashSet<string> TargetLineIds = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> AdjustableLineIds = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> FixedLineIds = new HashSet<string>(StringComparer.Ordinal);

            public CapacityLineRole ResolveRole(string lineId)
            {
                if (TargetLineIds.Contains(lineId ?? string.Empty))
                {
                    return new CapacityLineRole
                    {
                        Name = "targetExpress",
                        EarlierBudgetMinutes = MaxOffsetMinutes,
                        LaterBudgetMinutes = MaxOffsetMinutes
                    };
                }

                if (AdjustableLineIds.Contains(lineId ?? string.Empty))
                {
                    return new CapacityLineRole
                    {
                        Name = "adjustableLocal",
                        EarlierBudgetMinutes = MaxLocalRetimeMinutes,
                        LaterBudgetMinutes = MaxLocalRetimeMinutes,
                        IsAdjustableLocal = true
                    };
                }

                return new CapacityLineRole
                {
                    Name = FixedLineIds.Contains(lineId ?? string.Empty) ? "fixedConstraint" : "fixed"
                };
            }
        }

        private sealed class CapacityLineRole
        {
            public string Name = string.Empty;
            public float EarlierBudgetMinutes;
            public float LaterBudgetMinutes;
            public bool IsAdjustableLocal;
        }

        private sealed class CapacityTripRecord
        {
            public string TripId = string.Empty;
            public string LineId = string.Empty;
            public string Kind = string.Empty;
            public int DepartureMinute;
            public float[] AtomBoundaryMinuteOffsets = Array.Empty<float>();
        }

        private sealed class CapacityProjectedInterval
        {
            public string SourceCorridorId = string.Empty;
            public string TargetLineId = string.Empty;
            public string OtherLineId = string.Empty;
            public int TargetStartAtomIndex = -1;
            public int TargetEndAtomIndexExclusive = -1;
            public float OtherStartBoundaryPosition = -1f;
            public float OtherEndBoundaryPosition = -1f;
        }

        private sealed class CapacityLineAxisRange
        {
            public string LineId = string.Empty;
            public float StartBoundaryPosition = -1f;
            public float EndBoundaryPosition = -1f;
        }

        private sealed class CapacityElementarySection
        {
            public string ResourceId = string.Empty;
            public string TargetLineId = string.Empty;
            public int TargetStartAtomIndex = -1;
            public int TargetEndAtomIndexExclusive = -1;
            public List<string> SourceCorridorIds = new List<string>();
            public List<string> CoverageLineIds = new List<string>();
            public Dictionary<string, CapacityLineAxisRange> LineRanges = new Dictionary<string, CapacityLineAxisRange>(StringComparer.Ordinal);

            public void AddLineRange(string lineId, float startBoundaryPosition, float endBoundaryPosition)
            {
                if (string.IsNullOrEmpty(lineId) || startBoundaryPosition < 0f || endBoundaryPosition <= startBoundaryPosition)
                {
                    return;
                }

                if (!LineRanges.TryGetValue(lineId, out CapacityLineAxisRange range))
                {
                    LineRanges[lineId] = new CapacityLineAxisRange
                    {
                        LineId = lineId,
                        StartBoundaryPosition = startBoundaryPosition,
                        EndBoundaryPosition = endBoundaryPosition
                    };
                    return;
                }

                range.StartBoundaryPosition = Math.Min(range.StartBoundaryPosition, startBoundaryPosition);
                range.EndBoundaryPosition = Math.Max(range.EndBoundaryPosition, endBoundaryPosition);
            }
        }

        private sealed class CapacitySectionGroup
        {
            public string ResourceId = string.Empty;
            public string TargetLineId = string.Empty;
            public int TargetStartAtomIndex = -1;
            public int TargetEndAtomIndexExclusive = -1;
            public List<string> CoverageLineIds = new List<string>();
            public List<CapacityElementarySection> Sections = new List<CapacityElementarySection>();
            public Dictionary<string, CapacityLineAxisRange> LineRanges = new Dictionary<string, CapacityLineAxisRange>(StringComparer.Ordinal);

            public bool CanMerge(CapacityElementarySection section)
            {
                int gapAtoms = section.TargetStartAtomIndex - TargetEndAtomIndexExclusive;
                return string.Equals(TargetLineId, section.TargetLineId, StringComparison.Ordinal)
                    && gapAtoms >= 0
                    && gapAtoms <= PlannerDefaults.PursuitTrunkMergeGapAtoms
                    && HasCompatibleCoverage(section.CoverageLineIds);
            }

            public void Append(CapacityElementarySection section)
            {
                if (Sections.Count == 0)
                {
                    TargetStartAtomIndex = section.TargetStartAtomIndex;
                    CoverageLineIds = section.CoverageLineIds.ToList();
                }
                else
                {
                    CoverageLineIds = CoverageLineIds
                        .Union(section.CoverageLineIds, StringComparer.Ordinal)
                        .OrderBy(lineId => string.Equals(lineId, TargetLineId, StringComparison.Ordinal) ? 0 : 1)
                        .ThenBy(lineId => lineId, StringComparer.Ordinal)
                        .ToList();
                }

                TargetEndAtomIndexExclusive = section.TargetEndAtomIndexExclusive;
                Sections.Add(section);
                foreach (CapacityLineAxisRange range in section.LineRanges.Values)
                {
                    AddLineRange(range.LineId, range.StartBoundaryPosition, range.EndBoundaryPosition);
                }
            }

            private void AddLineRange(string lineId, float startBoundaryPosition, float endBoundaryPosition)
            {
                if (!LineRanges.TryGetValue(lineId, out CapacityLineAxisRange range))
                {
                    LineRanges[lineId] = new CapacityLineAxisRange
                    {
                        LineId = lineId,
                        StartBoundaryPosition = startBoundaryPosition,
                        EndBoundaryPosition = endBoundaryPosition
                    };
                    return;
                }

                range.StartBoundaryPosition = Math.Min(range.StartBoundaryPosition, startBoundaryPosition);
                range.EndBoundaryPosition = Math.Max(range.EndBoundaryPosition, endBoundaryPosition);
            }

            private bool HasCompatibleCoverage(List<string> nextCoverageLineIds)
            {
                HashSet<string> current = new HashSet<string>(CoverageLineIds, StringComparer.Ordinal);
                HashSet<string> next = new HashSet<string>(nextCoverageLineIds, StringComparer.Ordinal);
                current.Remove(TargetLineId);
                next.Remove(TargetLineId);
                return current.Count > 0 && next.Count > 0 && current.Overlaps(next);
            }
        }

        private sealed class CapacityOccupation
        {
            public string TripId = string.Empty;
            public string LineId = string.Empty;
            public string Role = string.Empty;
            public float EntryMinute;
            public float ExitMinute;
            public float DurationMinutes;
            public float EarliestStartMinute;
            public float LatestStartMinute;
            public float CompressedEntryMinute;
            public float CompressedExitMinute;
            public float[] AxisMinuteOffsets = Array.Empty<float>();
            public bool IsMovable;
            public bool IsAdjustableLocal;

            public CapacityOccupation Clone()
            {
                return (CapacityOccupation)MemberwiseClone();
            }
        }

        private sealed class CapacityCompressionResult
        {
            public int TripCount;
            public float CompressedSpanMinutes;
            public float CapacityConsumptionRatio;
            public float ZeroGapConsumptionRatio;
            public float MinResidualSlackMinutes;
            public float RequiredMaxShiftMinutes;
            public float RequiredMaxWaitMinutes;
            public bool HasResidualSlack;
            public bool HasShiftLimitExceeded;
            public bool HasWaitLimitExceeded;
            public bool HasFixedLineBlocker;
        }

        private sealed class CapacitySectionResult
        {
            public string ResourceId = string.Empty;
            public string TargetLineId = string.Empty;
            public int TargetStartAtomIndex = -1;
            public int TargetEndAtomIndexExclusive = -1;
            public string[] CoverageLineIds = Array.Empty<string>();
            public int TripCount;
            public float CompressedSpanMinutes;
            public float CapacityConsumptionRatio;
            public float ZeroGapConsumptionRatio;
            public float MinResidualSlackMinutes;
            public float RequiredMaxShiftMinutes;
            public float RequiredMaxWaitMinutes;
            public bool HasShiftLimitExceeded;
            public bool HasWaitLimitExceeded;
            public bool HasFixedLineBlocker;
            public string Verdict = string.Empty;
            public string Reason = string.Empty;
        }
    }
}
