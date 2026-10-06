using System;
using System.Collections.Generic;
using RapidTransitMod.TrackModel;
using Newtonsoft.Json;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Api
    {
        internal static string Load(string requestJson)
        {
            FlowQueryDto options = string.IsNullOrWhiteSpace(requestJson)
                ? new FlowQueryDto()
                : Workbenches.Json.Read<FlowQueryDto>(requestJson) ?? new FlowQueryDto();
            Port port = Runtime.Current;
            uint frame = port != null ? port.Frame() : 0u;
            State state = SamplingSystem.CurrentState;
            Core.ClockSnapshot clock = port != null ? port.Clock() : default;
            TramStationGroup[] groups = port != null && (options.mode == "tram" || options.mode == "all")
                ? port.ReadStationGroups(options) : Array.Empty<TramStationGroup>();
            FlowQuery query = FlowQuery.Read(options, state, clock, groups);
            return JsonConvert.SerializeObject(Snapshot.Build(state, frame, query, clock, port));
        }
    }
    internal readonly struct FlowQuery
    {
        internal readonly TransitMode Mode;
        internal readonly bool AllModes;
        internal readonly string ModeToken;
        internal readonly string View;
        internal readonly string Day;
        internal readonly int SelectedDayIndex;
        internal readonly SectionKind SectionKind;
        internal readonly FlowOptions Options;
        internal readonly int StationRankLimit;
        internal readonly FlowBucketDto RequestedFrom;
        internal readonly FlowBucketDto RequestedTo;
        internal readonly string LineId;
        internal readonly string StationId;
        internal readonly int StationIndex;
        internal readonly HashSet<int> StationIndices;
        internal readonly StationGroups Groups;
        internal readonly bool DirectoryOnly;
        internal readonly bool GroupSelection;
        internal readonly int FromAbsolute;
        internal readonly int ToAbsolute;
        internal readonly bool HasSelection;
        internal readonly FlowBucketDto RetainedFrom;
        internal readonly FlowBucketDto RetainedTo;
        internal readonly FlowBucketDto EffectiveFrom;
        internal readonly FlowBucketDto EffectiveTo;

        private FlowQuery(FlowQueryDto request, TransitMode mode, bool allModes, FlowOptions options,
            SectionKind sectionKind, int stationIndex, string day, int selectedDayIndex,
            int fromAbsolute, int toAbsolute, bool hasSelection,
            FlowBucketDto retainedFrom, FlowBucketDto retainedTo,
            FlowBucketDto effectiveFrom, FlowBucketDto effectiveTo, StationGroups groups)
        {
            Mode = mode; AllModes = allModes;
            ModeToken = allModes ? "all" : TransitModeCodec.Format(mode);
            View = request.view; Options = options; SectionKind = sectionKind;
            StationRankLimit = request.stationRankLimit ?? 10;
            Day = day; SelectedDayIndex = selectedDayIndex;
            RequestedFrom = new FlowBucketDto
            { dayIndex = selectedDayIndex, bucketStartMinute = request.fromMinute ?? 0 };
            RequestedTo = new FlowBucketDto
            { dayIndex = selectedDayIndex, bucketStartMinute = request.toMinute ?? 1440 };
            LineId = request.lineId; StationId = request.stationGroupId ?? request.stationId; StationIndex = request.stationGroupId != null && groups.FirstIndex(request.stationGroupId) >= 0
                ? groups.FirstIndex(request.stationGroupId) : stationIndex;
            Groups = groups; DirectoryOnly = request.directoryOnly;
            GroupSelection = !string.IsNullOrEmpty(request.stationGroupId);
            StationIndices = groups.SelectionIndices(request.stationGroupId, stationIndex);
            FromAbsolute = fromAbsolute; ToAbsolute = toAbsolute;
            HasSelection = hasSelection;
            RetainedFrom = retainedFrom; RetainedTo = retainedTo;
            EffectiveFrom = effectiveFrom; EffectiveTo = effectiveTo;
        }

        internal static FlowQuery Read(FlowQueryDto request, State state, Core.ClockSnapshot clock, TramStationGroup[] stationGroups = null)
        {
            var groups = new StationGroups(stationGroups, state?.Anchors);
            bool allModes = request.mode == "all";
            TransitMode mode = TransitMode.Train;
            if (!allModes && request.mode != null)
                TransitModeCodec.TryParse(request.mode, out mode);
            FlowOptions options = new FlowOptions(request, !allModes && SamplingSystem.SupportsMode(mode));
            SectionKind sectionKind = options.includeSections && request.sectionKind == "stops"
                ? SectionKind.Stops : SectionKind.Track;
            string day = request.day ?? "today";
            int selectedDayIndex = clock.DayIndex - (day == "yesterday" ? 1 : 0);
            int fromMinute = request.fromMinute ?? 0;
            int toMinute = request.toMinute ?? 1440;
            bool validRange = (day == "today" || day == "yesterday")
                && selectedDayIndex >= 0 && fromMinute >= 0 && fromMinute < toMinute
                && toMinute <= 1440 && fromMinute % Snapshot.BucketMinutes == 0
                && toMinute % Snapshot.BucketMinutes == 0;
            int requestedFrom = selectedDayIndex * SamplingSystem.BucketsPerDay + fromMinute / Snapshot.BucketMinutes;
            int requestedTo = selectedDayIndex * SamplingSystem.BucketsPerDay + toMinute / Snapshot.BucketMinutes;

            int stationIndex = -1;
            string selectedStationId = request.stationGroupId ?? request.stationId;
            if (!string.IsNullOrEmpty(selectedStationId))
            {
                if (state == null || !state.Anchors.TryGetIndex(selectedStationId, out stationIndex))
                    stationIndex = -2;
            }
            bool hasWindow = state != null && state.LastMinute >= 0;
            if (!hasWindow)
                return new FlowQuery(request, mode, allModes, options, sectionKind, stationIndex,
                    day, selectedDayIndex, 0, 0, false, null, null, null, null, groups);

            int windowFrom = SamplingSystem.RetainedFromAbsolute(clock.DayIndex);
            int windowTo = SamplingSystem.AbsoluteBucketIndex(SamplingSystem.BucketFor(clock.DayIndex, clock.NowMinute)) + 1;
            int from = Math.Max(windowFrom, requestedFrom);
            int to = Math.Min(windowTo, requestedTo);
            bool hasSelection = validRange && from < to;
            return new FlowQuery(request, mode, allModes, options, sectionKind, stationIndex,
                day, selectedDayIndex, from, to, hasSelection,
                ToDto(windowFrom), ToDto(windowTo),
                hasSelection ? ToDto(from) : null,
                hasSelection ? ToDto(to) : null, groups);
        }

        private static FlowBucketDto ToDto(int absolute)
        {
            TimeBucketKey bucket = SamplingSystem.BucketFromAbsoluteIndex(absolute);
            return new FlowBucketDto
            { dayIndex = bucket.DayIndex, bucketStartMinute = bucket.BucketStartMinute };
        }

        internal int GroupIndex(int index)
            => GroupSelection || string.IsNullOrEmpty(StationId) ? Groups.Canonical(index) : index;

        internal bool Line(string lineId)
            => string.IsNullOrEmpty(LineId) || string.Equals(LineId, lineId, StringComparison.Ordinal);

        internal bool Station(int index)
            => string.IsNullOrEmpty(StationId) || StationIndices.Contains(index);

        internal bool EitherStation(int from, int to)
            => string.IsNullOrEmpty(StationId) || Station(from) || Station(to);

        internal bool Includes(TransitMode mode) => AllModes || Mode == mode;

        internal bool Transfer(TransferKey key)
            => (Includes(key.FromMode) && Line(key.FromLineId) && Station(key.FromStationSakIndex))
                || (Includes(key.ToMode) && Line(key.ToLineId) && Station(key.ToStationSakIndex));
    }

    internal readonly struct FlowOptions
    {
        internal readonly bool includeSummary, includeSeries, includePurposes, includeTransfers,
            includeStationWaiting, includeStationCatalog, includeLineStops,
            includeSections, includeRelationSeries, includePurposeSeries, includeLineSeries, includeNetworkLoads, includeLineComparison;

        internal FlowOptions(FlowQueryDto request, bool supportedMode)
        {
            bool line = request.view == "line";
            bool station = request.view == "station";
            bool page = line || station || request.view == "network";
            includeSummary = request.includeSummary ?? page;
            includeSeries = request.includeSeries ?? !page;
            includePurposes = request.includePurposes ?? page;
            includeTransfers = request.includeTransfers ?? station;
            includeStationWaiting = request.includeStationWaiting ?? (line || station);
            includeLineComparison = request.includeLineComparison ?? false;
            includeStationCatalog = request.includeStationCatalog ?? !page;
            includeLineStops = request.includeLineStops ?? false;
            includeSections = (request.includeSections ?? (line || !string.IsNullOrEmpty(request.sectionKind)))
                && (request.sectionKind == "stops" || request.sectionKind == "track");
            includeRelationSeries = includeSeries && !page;
            includePurposeSeries = includePurposes && !page;
            includeLineSeries = includeSeries && (!page || line);
            includeNetworkLoads = request.view == "network" && string.IsNullOrEmpty(request.lineId)
                && string.IsNullOrEmpty(request.stationId) && string.IsNullOrEmpty(request.stationGroupId) && supportedMode;
        }
    }
}
