using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class Rows
    {
        internal static DispatchWorkbenchManualRowDto CopyManual(DispatchWorkbenchManualRowDto row)
        {
            return new DispatchWorkbenchManualRowDto
            {
                id = row.id,
                lineId = row.lineId,
                time = row.time,
                kind = row.kind,
                offsetMode = row.offsetMode,
                offsetMinutes = row.offsetMinutes
            };
        }

        internal static DispatchWorkbenchAutoRuleDto CopyRule(DispatchWorkbenchAutoRuleDto rule)
        {
            return new DispatchWorkbenchAutoRuleDto
            {
                id = rule.id,
                lineId = rule.lineId,
                enabled = rule.enabled,
                start = rule.start,
                end = rule.end,
                kind = rule.kind,
                departuresPerHour = rule.departuresPerHour,
                localPerHour = rule.localPerHour,
                expressPerHour = rule.expressPerHour,
                expressOffsetMode = rule.expressOffsetMode,
                expressOffsetMinutes = rule.expressOffsetMinutes
            };
        }

        internal static DispatchWorkbenchStagedRowDto CopyRow(DispatchWorkbenchStagedRowDto row)
        {
            return new DispatchWorkbenchStagedRowDto
            {
                id = row.id,
                lineId = row.lineId,
                time = row.time,
                kind = row.kind,
                source = row.source,
                note = row.note
            };
        }

        internal static int[] Times(IEnumerable<DispatchWorkbenchStagedRowDto> rows, string lineId)
        {
            if (rows == null)
                return Array.Empty<int>();

            HashSet<int> seen = new HashSet<int>();
            List<int> minutes = new List<int>();
            foreach (DispatchWorkbenchStagedRowDto row in rows)
            {
                if (row == null)
                    continue;
                if (!string.IsNullOrEmpty(row.lineId)
                    && !string.Equals(row.lineId, lineId, StringComparison.Ordinal))
                {
                    continue;
                }

                int minute = Time.Parse(row.time);
                if (minute < 0 || !seen.Add(minute))
                    continue;

                minutes.Add(minute);
            }

            minutes.Sort();
            return minutes.ToArray();
        }

        internal static string Note(byte sourceCode)
        {
            return sourceCode switch
            {
                1 => "restored-manual",
                2 => "restored-auto",
                3 => "restored-planner",
                _ => string.Empty
            };
        }

        internal static bool Has(List<DispatchWorkbenchStagedRowDto> rows, string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(Drafts.Key(row.lineId), draftKey, StringComparison.Ordinal));
        }

        internal static List<DispatchWorkbenchManualRowDto> KeepManual(
            List<DispatchWorkbenchManualRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchManualRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchManualRowDto> result =
                new List<DispatchWorkbenchManualRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchManualRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = IdKey(row.lineId, row.id);
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                string semanticKey = ManualKey(row);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        internal static List<DispatchWorkbenchAutoRuleDto> KeepRules(
            List<DispatchWorkbenchAutoRuleDto> rules)
        {
            if (rules == null || rules.Count <= 1)
                return rules ?? new List<DispatchWorkbenchAutoRuleDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchAutoRuleDto> result =
                new List<DispatchWorkbenchAutoRuleDto>(rules.Count);
            for (int index = rules.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchAutoRuleDto rule = rules[index];
                if (rule == null)
                    continue;

                string ruleId = IdKey(rule.lineId, rule.id);
                if (!string.IsNullOrEmpty(ruleId) && !seenIds.Add(ruleId))
                    continue;

                string semanticKey = RuleKey(rule);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(rule);
            }

            result.Reverse();
            return result;
        }

        internal static List<DispatchWorkbenchStagedRowDto> KeepRows(
            List<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchStagedRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchStagedRowDto> result =
                new List<DispatchWorkbenchStagedRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchStagedRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = IdKey(row.lineId, row.id);
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                string semanticKey = RowKey(row);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        internal static List<DispatchWorkbenchStagedRowDto> LastById(
            List<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchStagedRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchStagedRowDto> result = new List<DispatchWorkbenchStagedRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchStagedRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = row.id ?? string.Empty;
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        internal static string RowKey(DispatchWorkbenchStagedRowDto row)
        {
            if (row == null)
                return string.Empty;

            return (row.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                + "|"
                + (row.time ?? string.Empty);
        }

        internal static string ManualKey(DispatchWorkbenchManualRowDto row)
        {
            if (row == null)
                return string.Empty;

            return (row.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                + "|"
                + (row.time ?? string.Empty)
                + "|"
                + (row.offsetMode ?? string.Empty)
                + "|"
                + (row.offsetMinutes ?? string.Empty);
        }

        internal static string RuleKey(DispatchWorkbenchAutoRuleDto rule)
        {
            if (rule == null)
                return string.Empty;

            return (rule.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(rule.kind) ? "local" : rule.kind)
                + "|"
                + (rule.start ?? string.Empty)
                + "|"
                + (rule.end ?? string.Empty)
                + "|"
                + rule.departuresPerHour.ToString("R")
                + "|"
                + rule.localPerHour.ToString("R")
                + "|"
                + rule.expressPerHour.ToString("R")
                + "|"
                + (rule.expressOffsetMode ?? string.Empty)
                + "|"
                + rule.expressOffsetMinutes.ToString();
        }

        internal static string IdKey(string lineId, string rowId)
        {
            return string.IsNullOrEmpty(rowId)
                ? string.Empty
                : ((lineId ?? string.Empty) + "|" + rowId);
        }

        internal static string MatchKey(DispatchWorkbenchStagedRowDto row)
        {
            if (row == null)
                return string.Empty;

            return RowKey(row);
        }

        internal static bool SameView(DispatchWorkbenchMergedView left, DispatchWorkbenchMergedView right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            List<string> leftLocalIds = Ids(left.localLineIds, left.localLineId, null);
            List<string> rightLocalIds = Ids(right.localLineIds, right.localLineId, null);
            List<string> leftExpressIds = Ids(left.expressLineIds, left.expressLineId, null);
            List<string> rightExpressIds = Ids(right.expressLineIds, right.expressLineId, null);
            if (left.isLoop != right.isLoop
                || !string.Equals(left.turnbackStationId ?? string.Empty, right.turnbackStationId ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.direction ?? string.Empty, right.direction ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.windowStart ?? string.Empty, right.windowStart ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.windowEnd ?? string.Empty, right.windowEnd ?? string.Empty, StringComparison.Ordinal)
                || leftLocalIds.Count != rightLocalIds.Count
                || leftExpressIds.Count != rightExpressIds.Count)
            {
                return false;
            }

            for (int i = 0; i < leftLocalIds.Count; i++)
            {
                if (!string.Equals(leftLocalIds[i], rightLocalIds[i], StringComparison.Ordinal))
                    return false;
            }

            for (int i = 0; i < leftExpressIds.Count; i++)
            {
                if (!string.Equals(leftExpressIds[i], rightExpressIds[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        internal static bool SameManual(List<DispatchWorkbenchManualRowDto> left, List<DispatchWorkbenchManualRowDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchManualRowDto a = left[i];
                DispatchWorkbenchManualRowDto b = right[i];
                if (!string.Equals(a?.id, b?.id, StringComparison.Ordinal)
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || !string.Equals(a?.time, b?.time, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !string.Equals(a?.offsetMode, b?.offsetMode, StringComparison.Ordinal)
                    || !string.Equals(a?.offsetMinutes, b?.offsetMinutes, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool SameRules(List<DispatchWorkbenchAutoRuleDto> left, List<DispatchWorkbenchAutoRuleDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchAutoRuleDto a = left[i];
                DispatchWorkbenchAutoRuleDto b = right[i];
                if (!string.Equals(a?.id, b?.id, StringComparison.Ordinal)
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || a.enabled != b.enabled
                    || !string.Equals(a?.start, b?.start, StringComparison.Ordinal)
                    || !string.Equals(a?.end, b?.end, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !SameNum(a.departuresPerHour, b.departuresPerHour)
                    || !SameNum(a.localPerHour, b.localPerHour)
                    || !SameNum(a.expressPerHour, b.expressPerHour)
                    || !string.Equals(a?.expressOffsetMode, b?.expressOffsetMode, StringComparison.Ordinal)
                    || a.expressOffsetMinutes != b.expressOffsetMinutes)
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool SameNum(double left, double right)
        {
            return Math.Abs(left - right) < 0.000001d;
        }

        internal static bool SameRows(List<DispatchWorkbenchStagedRowDto> left, List<DispatchWorkbenchStagedRowDto> right)
        {
            return SameRowsCore(left, right, true);
        }

        internal static bool SameRowsSoft(List<DispatchWorkbenchStagedRowDto> left, List<DispatchWorkbenchStagedRowDto> right)
        {
            return SameRowsCore(left, right, false);
        }

        internal static bool SamePlan(DispatchWorkbenchPlannerImportContractDto left, DispatchWorkbenchPlannerImportContractDto right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            string leftJson = Workbenches.Json.Write(left);
            string rightJson = Workbenches.Json.Write(right);
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        internal static DispatchWorkbenchPlannerImportContractDto CopyPlan(
            DispatchWorkbenchPlannerImportContractDto contract)
        {
            if (contract == null)
                return null;

            string json = Workbenches.Json.Write(contract);
            return string.IsNullOrEmpty(json)
                ? null
                : Workbenches.Json.Read<DispatchWorkbenchPlannerImportContractDto>(json);
        }

        internal static List<string> Ids(string[] ids, string fallbackId, List<WorkbenchLineRuntime> lines)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> normalized = new List<string>();

            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id) || seen.Contains(id))
                        continue;
                    if (lines != null && !lines.Exists(line => line.Id == id))
                        continue;
                    seen.Add(id);
                    normalized.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(fallbackId) && !seen.Contains(fallbackId))
            {
                if (lines == null || lines.Exists(line => line.Id == fallbackId))
                {
                    seen.Add(fallbackId);
                    normalized.Add(fallbackId);
                }
            }

            return normalized;
        }

        private static bool SameRowsCore(
            List<DispatchWorkbenchStagedRowDto> left,
            List<DispatchWorkbenchStagedRowDto> right,
            bool strict)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchStagedRowDto a = left[i];
                DispatchWorkbenchStagedRowDto b = right[i];
                if ((strict && !string.Equals(a?.id, b?.id, StringComparison.Ordinal))
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || !string.Equals(a?.time, b?.time, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !string.Equals(a?.source, b?.source, StringComparison.Ordinal)
                    || (strict && !string.Equals(a?.note, b?.note, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
