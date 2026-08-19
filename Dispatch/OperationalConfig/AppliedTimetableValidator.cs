using System;
using System.Collections.Generic;
using System.Linq;
using RapidTransitMod.Dispatch.Lines;

namespace RapidTransitMod
{
    public sealed class AppliedTimetableValidator
    {
        public AppliedTimetableValidationResult Validate(LineKey lineKey, AppliedTimetableState state)
        {
            return Validate(lineKey, state, 0);
        }

        public AppliedTimetableValidationResult Validate(
            string lineId,
            TransitMode mode,
            AppliedTimetableState state)
        {
            return Validate(LineIdentityService.GetKey(lineId, mode), state, 0);
        }

        public AppliedTimetableValidationResult Validate(
            LineKey lineKey,
            AppliedTimetableState state,
            int minSameOriginGapMinutes)
        {
            List<string> errors = new List<string>();
            List<string> warnings = new List<string>();
            LineKey normalizedLineKey = RuntimeConfigStoreDefaults.NormalizeLineKey(lineKey);

            if (normalizedLineKey.IsEmpty)
            {
                errors.Add("line-key-required");
            }

            if (state == null)
            {
                errors.Add("applied-timetable-required");
                return new AppliedTimetableValidationResult(errors, warnings);
            }

            HashSet<int> seenDepartureMinutes = new HashSet<int>();
            int[] departures = state.DepartureMinutes ?? Array.Empty<int>();
            for (int i = 0; i < departures.Length; i++)
            {
                int minute = departures[i];
                if (minute < 0 || minute >= 24 * 60)
                {
                    errors.Add("invalid-departure-minute:" + minute);
                    continue;
                }

                if (!seenDepartureMinutes.Add(minute))
                {
                    errors.Add("duplicate-departure-minute:" + minute);
                }
            }

            Dictionary<string, List<int>> rowsByOrigin = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            HashSet<int> rowDepartureMinutes = new HashSet<int>();
            HashSet<string> rowIds = new HashSet<string>(StringComparer.Ordinal);
            AppliedTimetableRow[] rows = state.AppliedRows ?? Array.Empty<AppliedTimetableRow>();
            for (int i = 0; i < rows.Length; i++)
            {
                AppliedTimetableRow row = rows[i];
                if (row == null)
                {
                    warnings.Add("null-row:" + i);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.RowId))
                    errors.Add("row-id-required:" + i);
                else if (!rowIds.Add(row.RowId))
                    errors.Add("duplicate-row-id:" + row.RowId);

                ValidateTimedStops(row, i, errors);

                if (row.DepartureMinute < 0)
                    continue;

                if (row.DepartureMinute >= 24 * 60)
                {
                    errors.Add("invalid-row-minute:" + row.DepartureMinute);
                    continue;
                }

                rowDepartureMinutes.Add(row.DepartureMinute);

                string originKey = row.OriginKey ?? string.Empty;
                if (string.IsNullOrEmpty(originKey))
                    continue;

                if (!rowsByOrigin.TryGetValue(originKey, out List<int> originMinutes))
                {
                    originMinutes = new List<int>();
                    rowsByOrigin[originKey] = originMinutes;
                }

                originMinutes.Add(row.DepartureMinute);
            }

            if (departures.Length > 0
                && rowDepartureMinutes.Count > 0
                && !departures.OrderBy(minute => minute).SequenceEqual(rowDepartureMinutes.OrderBy(minute => minute)))
            {
                warnings.Add("departure-minutes-do-not-match-applied-rows");
            }

            int requiredGap = Math.Max(0, minSameOriginGapMinutes);
            if (requiredGap > 0)
            {
                foreach (KeyValuePair<string, List<int>> entry in rowsByOrigin)
                {
                    List<int> originMinutes = entry.Value;
                    originMinutes.Sort();
                    for (int i = 1; i < originMinutes.Count; i++)
                    {
                        int gap = originMinutes[i] - originMinutes[i - 1];
                        if (gap < requiredGap)
                        {
                            errors.Add(
                                "same-origin-gap:" + entry.Key + ":" + originMinutes[i - 1] + ":" + originMinutes[i]);
                        }
                    }
                }
            }

            if (!state.Managed
                && (departures.Length > 0 || rows.Length > 0 || !string.IsNullOrEmpty(state.ServiceKind)))
            {
                warnings.Add("unmanaged-line-carries-applied-data");
            }

            return new AppliedTimetableValidationResult(errors, warnings);
        }

        private static void ValidateTimedStops(
            AppliedTimetableRow row,
            int rowIndex,
            List<string> errors)
        {
            TimedStop[] stops = row.TimedStops ?? Array.Empty<TimedStop>();
            if (stops.Length == 0)
                return;

            bool departed = true;
            for (int i = 0; i < stops.Length; i++)
            {
                TimedStop stop = stops[i];
                if (stop == null || string.IsNullOrWhiteSpace(stop.StopKey))
                {
                    errors.Add("timed-stop-key-required:" + rowIndex + ":" + i);
                    continue;
                }

                if (stop.Arrive < -1 || stop.Arrive >= 48 * 60)
                    errors.Add("invalid-timed-stop-arrive:" + rowIndex + ":" + i);
                if (stop.Depart < -1 || stop.Depart >= 48 * 60)
                    errors.Add("invalid-timed-stop-depart:" + rowIndex + ":" + i);

                if (!departed)
                    errors.Add("timed-stop-chain-break:" + rowIndex + ":" + i);

                if (i > 0 && stop.Arrive < 0)
                    errors.Add("timed-stop-arrive-required:" + rowIndex + ":" + i);
                TimedStop previous = i > 0 ? stops[i - 1] : null;
                if (previous != null && previous.Depart >= 0
                    && stop.Arrive >= 0 && stop.Arrive < previous.Depart)
                {
                    errors.Add("timed-stop-arrival-before-departure:" + rowIndex + ":" + i);
                }
                if (stop.Depart >= 0 && stop.Arrive >= 0
                    && stop.Depart - stop.Arrive < 5)
                {
                    errors.Add("timed-stop-minimum-dwell:" + rowIndex + ":" + i);
                }

                if (i == 0 && stop.Depart >= 0 && row.DepartureMinute >= 0
                    && stop.Depart != row.DepartureMinute)
                {
                    errors.Add("origin-departure-mismatch:" + rowIndex);
                }

                departed = stop.Depart >= 0;
            }
        }

        public AppliedTimetableValidationResult Validate(
            string lineId,
            TransitMode mode,
            AppliedTimetableState state,
            int minSameOriginGapMinutes)
        {
            return Validate(LineIdentityService.GetKey(lineId, mode), state, minSameOriginGapMinutes);
        }

        internal AppliedTimetableValidationResult Validate(
            LineKey lineKey,
            AppliedTimetableState state,
            RoutePlan route)
        {
            AppliedTimetableValidationResult baseResult = Validate(lineKey, state, 0);
            List<string> errors = baseResult.Errors.ToList();
            List<string> warnings = baseResult.Warnings.ToList();
            if (state == null)
                return new AppliedTimetableValidationResult(errors, warnings);

            bool hasTimedStops = (state.AppliedRows ?? Array.Empty<AppliedTimetableRow>())
                .Any(row => row != null && (row.TimedStops?.Length ?? 0) > 0);
            if (!hasTimedStops)
                return new AppliedTimetableValidationResult(errors, warnings);

            if (route == null || route.Stops == null || route.Stops.Length == 0)
            {
                errors.Add("route-plan-required");
                return new AppliedTimetableValidationResult(errors, warnings);
            }

            if (!string.Equals(state.StopSig ?? string.Empty, route.StopSig ?? string.Empty, StringComparison.Ordinal))
                errors.Add("stop-sig-mismatch");

            AppliedTimetableRow[] rows = state.AppliedRows ?? Array.Empty<AppliedTimetableRow>();
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                TimedStop[] stops = rows[rowIndex]?.TimedStops ?? Array.Empty<TimedStop>();
                if (stops.Length == 0)
                    continue;

                if (stops.Length > route.Stops.Length + 1)
                {
                    errors.Add("timed-stop-count-exceeded:" + rowIndex);
                    continue;
                }

                for (int stopIndex = 0; stopIndex < stops.Length; stopIndex++)
                {
                    string expectedStopKey = stopIndex == route.Stops.Length
                        ? route.Stops[0].StopKey
                        : route.Stops[stopIndex].StopKey;
                    if (!string.Equals(
                        stops[stopIndex]?.StopKey ?? string.Empty,
                        expectedStopKey ?? string.Empty,
                        StringComparison.Ordinal))
                    {
                        errors.Add("timed-stop-order-mismatch:" + rowIndex + ":" + stopIndex);
                    }
                }
            }

            return new AppliedTimetableValidationResult(errors, warnings);
        }
    }

    public sealed class AppliedTimetableValidationResult
    {
        public string[] Errors { get; }
        public string[] Warnings { get; }
        public bool IsValid => Errors.Length == 0;

        public AppliedTimetableValidationResult(
            IEnumerable<string> errors,
            IEnumerable<string> warnings)
        {
            Errors = (errors ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrEmpty(value))
                .ToArray();
            Warnings = (warnings ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrEmpty(value))
                .ToArray();
        }
    }
}
