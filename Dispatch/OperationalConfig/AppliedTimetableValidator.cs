using System;
using System.Collections.Generic;
using System.Linq;

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
            AppliedTimetableRow[] rows = state.AppliedRows ?? Array.Empty<AppliedTimetableRow>();
            for (int i = 0; i < rows.Length; i++)
            {
                AppliedTimetableRow row = rows[i];
                if (row == null)
                {
                    warnings.Add("null-row:" + i);
                    continue;
                }

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

        public AppliedTimetableValidationResult Validate(
            string lineId,
            TransitMode mode,
            AppliedTimetableState state,
            int minSameOriginGapMinutes)
        {
            return Validate(LineIdentityService.GetKey(lineId, mode), state, minSameOriginGapMinutes);
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
