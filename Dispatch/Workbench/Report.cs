using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class Report
    {
        internal static void Snapshot(
            ref string lastWorkbenchSnapshotLogKey,
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            List<DispatchWorkbenchTripDto> trips,
            DispatchWorkbenchDraftState draft,
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows,
            Action<string> log)
        {
            string lineId = activeRuntime?.Id ?? "none";
            string lineName = activeRuntime?.Name ?? "none";
            string stationPreview = stations.Count == 0
                ? "-"
                : string.Join(" | ", stations.Take(4).Select(station => station.name ?? "-"));
            string tripPreview = trips == null || trips.Count == 0
                ? "-"
                : string.Join(" | ", trips.Take(2).Select(trip =>
                {
                    DispatchWorkbenchTripStopDto firstStop = trip.stops?.FirstOrDefault();
                    DispatchWorkbenchTripStopDto secondStop = trip.stops != null && trip.stops.Length > 1 ? trip.stops[1] : null;
                    string first = firstStop == null
                        ? "-"
                        : (firstStop.stationId + "@" + (firstStop.departureTime ?? firstStop.time ?? "--:--"));
                    string second = secondStop == null
                        ? "-"
                        : (secondStop.stationId + "@" + (secondStop.arrivalTime ?? secondStop.time ?? "--:--"));
                    return trip.id + "(" + trip.kind + "," + trip.lineId + "):" + first + "->" + second;
                }));
            int drawableTripCount = trips?.Count(trip => trip?.stops != null && trip.stops.Length >= 2) ?? 0;
            string localPreview = draft?.MergedView?.localLineIds != null
                ? string.Join(",", draft.MergedView.localLineIds)
                : draft?.MergedView?.localLineId ?? string.Empty;
            string expressPreview = draft?.MergedView?.expressLineIds != null
                ? string.Join(",", draft.MergedView.expressLineIds)
                : draft?.MergedView?.expressLineId ?? string.Empty;
            string activeRowsPreview = SummarizeStagedRowsByLine(activeLineDraftRows);
            string mergedRowsPreview = SummarizeStagedRowsByLine(combinedDraftRows);
            string logKey = lineId
                + "|"
                + stations.Count
                + "|"
                + (trips?.Count ?? 0)
                + "|"
                + localPreview
                + "|"
                + expressPreview
                + "|"
                + activeRowsPreview
                + "|"
                + mergedRowsPreview;
            if (logKey == lastWorkbenchSnapshotLogKey)
            {
                return;
            }

            lastWorkbenchSnapshotLogKey = logKey;
            log(
                "Workbench snapshot line="
                + lineId
                + " name=\""
                + lineName
                + "\" stations="
                + stations.Count
                + " local=["
                + localPreview
                + "] express=["
                + expressPreview
                + "] firstStops=["
                + stationPreview
                + "] trips="
                + (trips?.Count ?? 0)
                + " drawableTrips="
                + drawableTripCount
                + " firstTrips=["
                + tripPreview
                + "] activeRows=["
                + activeRowsPreview
                + "] combinedRows=["
                + mergedRowsPreview
                + "]");
        }

        internal static string DraftSummary(DraftStore drafts)
        {
            if (drafts.Count == 0)
                return "draftRows=[]";

            return "draftRows=["
                + string.Join("; ", drafts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        DispatchWorkbenchDraftState draft = pair.Value;
                        return pair.Key
                            + " sel="
                            + (draft?.SelectedLineId ?? string.Empty)
                            + " edit="
                            + (draft?.SelectedEditLine ?? string.Empty)
                            + " applied="
                            + (draft?.DraftApplied == true ? "1" : "0")
                            + " staged="
                            + SummarizeStagedRowsByLine(draft?.StagedRows);
                    }))
                + "]";
        }

        internal static string AppliedSummary(
            IReadOnlyDictionary<string, AppliedLine> appliedLines)
        {
            if (appliedLines.Count == 0)
                return "appliedRows=[]";

            return "appliedRows=["
                + string.Join("; ", appliedLines
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        AppliedLine state = pair.Value;
                        string entityText = state?.LineEntity == Entity.Null
                            ? "none"
                            : state?.LineEntity.Index.ToString();
                        return pair.Key
                            + " entity="
                            + entityText
                            + " staged="
                            + SummarizeStagedRowsByLine(state?.StagedRows);
                    }))
                + "]";
        }

        internal static void Integrity(
            bool enabled,
            string reason,
            WorkbenchLineRuntime activeRuntime,
            string draftKey,
            DispatchWorkbenchDraftState activeDraft,
            List<WorkbenchLineRuntime> runtimeLines,
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows,
            DraftStore drafts,
            IReadOnlyDictionary<string, AppliedLine> appliedLines,
            int frameIndex,
            Func<string[], string, List<WorkbenchLineRuntime>, List<string>> normalizeLineIdList,
            Func<string, int> parseTimeMinutes,
            Action<string> log)
        {
            if (!enabled)
                return;

            try
            {
                Dictionary<string, WorkbenchLineRuntime> runtimeById = runtimeLines
                    .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                    .GroupBy(line => line.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                Dictionary<string, List<string>> provenanceByKey = BuildProvenance(drafts, appliedLines);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("RapidTransitMod Workbench Integrity Report");
                sb.AppendLine("reason=" + (reason ?? string.Empty));
                sb.AppendLine("frame=" + frameIndex.ToString());
                sb.AppendLine("activeLine=" + (activeRuntime?.Id ?? "none") + " name=\"" + (activeRuntime?.Name ?? "none") + "\" draftKey=" + (draftKey ?? string.Empty));
                sb.AppendLine("activeDraft selected=" + (activeDraft?.SelectedLineId ?? string.Empty)
                    + " edit=" + (activeDraft?.SelectedEditLine ?? string.Empty)
                    + " rulesApplied=" + (activeDraft?.RulesApplied == true ? "1" : "0")
                    + " draftApplied=" + (activeDraft?.DraftApplied == true ? "1" : "0")
                    + " local=[" + string.Join(",", normalizeLineIdList(activeDraft?.MergedView?.localLineIds, activeDraft?.MergedView?.localLineId, null)) + "]"
                    + " express=[" + string.Join(",", normalizeLineIdList(activeDraft?.MergedView?.expressLineIds, activeDraft?.MergedView?.expressLineId, null)) + "]");
                sb.AppendLine();
                LineCatalog(sb, runtimeLines);
                Drafts(sb, drafts);
                Applied(sb, runtimeById, appliedLines);
                RowSet(sb, "activeRows", activeLineDraftRows, runtimeById, provenanceByKey, parseTimeMinutes);
                RowSet(sb, "combinedRows", combinedDraftRows, runtimeById, provenanceByKey, parseTimeMinutes);
                Conflicts(sb, "combinedRows", combinedDraftRows, runtimeById, provenanceByKey, parseTimeMinutes);

                string filePath = GetWorkbenchReportPath("RapidTransitMod-workbench-integrity-latest.txt");
                File.WriteAllText(filePath, sb.ToString());
                log("[WorkbenchIntegrityReport] exported to " + filePath);
            }
            catch (Exception ex)
            {
                log("[WorkbenchIntegrityReport] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SaveRequest(
            bool enabled,
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            List<string> errors,
            DraftStore drafts,
            IReadOnlyDictionary<string, AppliedLine> appliedLines,
            int frameIndex,
            Func<DispatchWorkbenchSaveRequest, string, Dictionary<string, List<DispatchWorkbenchStagedRowDto>>> buildRequestLineDraftRowsByDraftKey,
            Func<string, string> getDraftKey,
            Func<string[], string, List<WorkbenchLineRuntime>, List<string>> normalizeLineIdList,
            Func<string, int> parseTimeMinutes,
            Action<string> log)
        {
            if (!enabled || request == null)
                return;

            try
            {
                Dictionary<string, WorkbenchLineRuntime> runtimeById = runtimeLines
                    .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                    .GroupBy(line => line.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                Dictionary<string, List<string>> provenanceByKey = BuildProvenance(drafts, appliedLines);
                List<DispatchWorkbenchStagedRowDto> rows = buildRequestLineDraftRowsByDraftKey(
                        request,
                        getDraftKey(request.selectedLineId))
                    .Values
                    .SelectMany(group => group)
                    .Select(Rows.CopyRow)
                    .ToList();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("RapidTransitMod Workbench Save Request Report");
                sb.AppendLine("frame=" + frameIndex.ToString());
                sb.AppendLine("selectedLineId=" + (request.selectedLineId ?? string.Empty)
                    + " selectedEditLine=" + (request.selectedEditLine ?? string.Empty)
                    + " applyDraft=" + (request.applyDraft ? "1" : "0")
                    + " markRulesApplied=" + (request.markRulesApplied ? "1" : "0")
                    + " nativeScheduleWriter=" + (request.nativeScheduleWriter ? "1" : "0"));
                sb.AppendLine("mergedView local=[" + string.Join(",", normalizeLineIdList(request.mergedView?.localLineIds, request.mergedView?.localLineId, runtimeLines)) + "]"
                    + " express=[" + string.Join(",", normalizeLineIdList(request.mergedView?.expressLineIds, request.mergedView?.expressLineId, runtimeLines)) + "]");
                sb.AppendLine("validationErrors=" + (errors == null || errors.Count == 0 ? "-" : string.Join(" | ", errors)));
                sb.AppendLine("planRefs=" + SummarizePlanRefs(request));
                sb.AppendLine("manualRows=" + SummarizeManualRowsByLine(request.manualRows));
                sb.AppendLine("autoRules=" + SummarizeAutoRulesByLine(request.autoRules));
                sb.AppendLine("lineDraftRows=" + SummarizeStagedRowsByLine(rows));
                sb.AppendLine();
                RowSet(sb, "request.lineDraftRows", rows, runtimeById, provenanceByKey, parseTimeMinutes);
                Conflicts(sb, "request.lineDraftRows", rows, runtimeById, provenanceByKey, parseTimeMinutes);

                string filePath = GetWorkbenchReportPath("RapidTransitMod-workbench-save-request-latest.txt");
                File.WriteAllText(filePath, sb.ToString());
                log("[WorkbenchSaveRequestReport] exported to " + filePath);
            }
            catch (Exception ex)
            {
                log("[WorkbenchSaveRequestReport] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void LineCatalog(StringBuilder sb, List<WorkbenchLineRuntime> runtimeLines)
        {
            sb.AppendLine("== runtime lines ==");
            if (runtimeLines == null || runtimeLines.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (WorkbenchLineRuntime line in runtimeLines.OrderBy(line => line.Id, StringComparer.Ordinal))
            {
                sb.AppendLine((line.Id ?? string.Empty)
                    + " entity=" + line.Entity.Index
                    + " routeNumber=" + (line.RouteNumber == int.MaxValue ? "-" : line.RouteNumber.ToString())
                    + " kind=" + (line.Kind ?? string.Empty)
                    + " name=\"" + (line.Name ?? string.Empty) + "\""
                    + " origin=" + (line.OriginStationId ?? string.Empty)
                    + " \"" + (line.OriginStationName ?? string.Empty) + "\"");
            }
            sb.AppendLine();
        }

        internal static void Drafts(StringBuilder sb, DraftStore drafts)
        {
            sb.AppendLine("== drafts ==");
            if (drafts.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                List<string> localIds = NormalizeLineIdList(draft?.MergedView?.localLineIds, draft?.MergedView?.localLineId);
                List<string> expressIds = NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId);
                HashSet<string> scope = new HashSet<string>(localIds.Concat(expressIds), StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(draft?.SelectedLineId)) scope.Add(draft.SelectedLineId);
                if (!string.IsNullOrEmpty(draft?.SelectedEditLine)) scope.Add(draft.SelectedEditLine);
                if (!string.IsNullOrEmpty(entry.Key)) scope.Add(entry.Key);
                string outOfScope = draft?.StagedRows == null
                    ? "-"
                    : string.Join(",", draft.StagedRows
                        .Where(row => row != null && !string.IsNullOrEmpty(row.lineId) && !scope.Contains(row.lineId))
                        .Select(row => row.lineId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal));
                if (string.IsNullOrEmpty(outOfScope)) outOfScope = "-";
                sb.AppendLine("draft=" + entry.Key
                    + " selected=" + (draft?.SelectedLineId ?? string.Empty)
                    + " edit=" + (draft?.SelectedEditLine ?? string.Empty)
                    + " rulesApplied=" + (draft?.RulesApplied == true ? "1" : "0")
                    + " draftApplied=" + (draft?.DraftApplied == true ? "1" : "0")
                    + " local=[" + string.Join(",", localIds) + "]"
                    + " express=[" + string.Join(",", expressIds) + "]"
                    + " staged=" + SummarizeStagedRowsByLine(draft?.StagedRows)
                    + " outOfScope=[" + outOfScope + "]");
            }
            sb.AppendLine();
        }

        internal static void Applied(
            StringBuilder sb,
            IReadOnlyDictionary<string, WorkbenchLineRuntime> runtimeById,
            IReadOnlyDictionary<string, AppliedLine> appliedLines)
        {
            sb.AppendLine("== applied ==");
            if (appliedLines.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (KeyValuePair<string, AppliedLine> entry in appliedLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedLine state = entry.Value;
                string runtimeOrigin = runtimeById.TryGetValue(entry.Key, out WorkbenchLineRuntime runtime)
                    ? (runtime.OriginStationId + " \"" + runtime.OriginStationName + "\"")
                    : "-";
                sb.AppendLine("applied=" + entry.Key
                    + " entity=" + (state?.LineEntity == Entity.Null ? "none" : state?.LineEntity.Index.ToString())
                    + " origin=" + runtimeOrigin
                    + " staged=" + SummarizeStagedRowsByLine(state?.StagedRows));
            }
            sb.AppendLine();
        }

        internal static void Conflicts(
            StringBuilder sb,
            string title,
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            Dictionary<string, List<string>> provenanceByKey,
            Func<string, int> parseTimeMinutes)
        {
            const int minGapMinutes = 5;
            sb.AppendLine("== " + title + " conflicts ==");
            List<(DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName)> entries =
                (rows ?? Enumerable.Empty<DispatchWorkbenchStagedRowDto>())
                    .Where(row => row != null)
                    .Select(row =>
                    {
                        int minute = parseTimeMinutes(row.time);
                        string originId = string.Empty;
                        string originName = string.Empty;
                        if (runtimeById.TryGetValue(row.lineId ?? string.Empty, out WorkbenchLineRuntime line))
                        {
                            originId = line.OriginStationId ?? string.Empty;
                            originName = line.OriginStationName ?? string.Empty;
                        }
                        return (Row: row, Minute: minute, OriginId: originId, OriginName: originName);
                    })
                    .Where(entry => entry.Minute >= 0 && !string.IsNullOrEmpty(entry.OriginId))
                    .OrderBy(entry => entry.Minute)
                    .ThenBy(entry => entry.Row.lineId, StringComparer.Ordinal)
                    .ToList();

            int frontendConflictCount = 0;
            for (int i = 1; i < entries.Count; i++)
            {
                (DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName) previous = entries[i - 1];
                (DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName) current = entries[i];
                if (!string.Equals(previous.OriginId, current.OriginId, StringComparison.Ordinal))
                    continue;
                int gap = current.Minute - previous.Minute;
                if (gap >= minGapMinutes)
                    continue;

                frontendConflictCount++;
                string originLabel = !string.IsNullOrEmpty(current.OriginName)
                    ? current.OriginName
                    : current.OriginId;
                sb.AppendLine("frontendConflict origin=" + originLabel
                    + " gap=" + gap
                    + " left={" + DescribeWorkbenchRow(previous.Row, runtimeById, provenanceByKey) + "}"
                    + " right={" + DescribeWorkbenchRow(current.Row, runtimeById, provenanceByKey) + "}");
            }
            sb.AppendLine("frontendPairCount=" + frontendConflictCount);

            int originConflictCount = 0;
            foreach (IGrouping<string, (DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName)> group in entries
                .GroupBy(entry => entry.OriginId, StringComparer.Ordinal))
            {
                (DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName)[] ordered = group.ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    int gap = Check.Gap(ordered[i - 1].Minute, ordered[i].Minute);
                    if (gap >= minGapMinutes)
                        continue;

                    originConflictCount++;
                    string originLabel = !string.IsNullOrEmpty(ordered[i].OriginName)
                        ? ordered[i].OriginName
                        : ordered[i].OriginId;
                    sb.AppendLine("originConflict origin=" + originLabel
                        + " gap=" + gap
                        + " left={" + DescribeWorkbenchRow(ordered[i - 1].Row, runtimeById, provenanceByKey) + "}"
                        + " right={" + DescribeWorkbenchRow(ordered[i].Row, runtimeById, provenanceByKey) + "}");
                }
                if (ordered.Length > 1 && ordered[0].Minute != ordered[ordered.Length - 1].Minute)
                {
                    int wrapGap = Check.Gap(ordered[ordered.Length - 1].Minute, ordered[0].Minute);
                    if (wrapGap >= minGapMinutes)
                        continue;

                    originConflictCount++;
                    string originLabel = !string.IsNullOrEmpty(ordered[0].OriginName)
                        ? ordered[0].OriginName
                        : ordered[0].OriginId;
                    sb.AppendLine("originConflict origin=" + originLabel
                        + " gap=" + wrapGap
                        + " left={" + DescribeWorkbenchRow(ordered[ordered.Length - 1].Row, runtimeById, provenanceByKey) + "}"
                        + " right={" + DescribeWorkbenchRow(ordered[0].Row, runtimeById, provenanceByKey) + "}");
                }
            }
            sb.AppendLine("originScopedPairCount=" + originConflictCount);
            sb.AppendLine();
        }

        private static void RowSet(
            StringBuilder sb,
            string title,
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            Dictionary<string, List<string>> provenanceByKey,
            Func<string, int> parseTimeMinutes)
        {
            sb.AppendLine("== " + title + " ==");
            List<DispatchWorkbenchStagedRowDto> rowList = rows?.Where(row => row != null).ToList()
                ?? new List<DispatchWorkbenchStagedRowDto>();
            sb.AppendLine("summary=" + SummarizeStagedRowsByLine(rowList));
            foreach (DispatchWorkbenchStagedRowDto row in rowList
                .OrderBy(row => parseTimeMinutes(row.time))
                .ThenBy(row => row.lineId, StringComparer.Ordinal)
                .Take(240))
            {
                sb.AppendLine(DescribeWorkbenchRow(row, runtimeById, provenanceByKey));
            }
            if (rowList.Count > 240)
            {
                sb.AppendLine("... truncated rows=" + (rowList.Count - 240));
            }
            sb.AppendLine();
        }

        private static string DescribeWorkbenchRow(
            DispatchWorkbenchStagedRowDto row,
            IReadOnlyDictionary<string, WorkbenchLineRuntime> runtimeById,
            IReadOnlyDictionary<string, List<string>> provenanceByKey)
        {
            if (row == null)
                return "(null)";

            string lineName = runtimeById.TryGetValue(row.lineId ?? string.Empty, out WorkbenchLineRuntime line)
                ? line.Name ?? string.Empty
                : "(unknown-line)";
            string origin = runtimeById.TryGetValue(row.lineId ?? string.Empty, out line)
                ? ((line.OriginStationId ?? string.Empty) + " \"" + (line.OriginStationName ?? string.Empty) + "\"")
                : "-";
            string key = Rows.RowKey(row);
            string provenance = provenanceByKey.TryGetValue(key, out List<string> sources)
                ? string.Join(",", sources.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                : "-";
            return "id=" + (row.id ?? string.Empty)
                + " line=" + (row.lineId ?? string.Empty)
                + " name=\"" + lineName + "\""
                + " kind=" + (row.kind ?? string.Empty)
                + " time=" + (row.time ?? string.Empty)
                + " source=" + (row.source ?? string.Empty)
                + " note=\"" + (row.note ?? string.Empty) + "\""
                + " origin=" + origin
                + " provenance=[" + provenance + "]";
        }

        private static Dictionary<string, List<string>> BuildProvenance(
            DraftStore drafts,
            IReadOnlyDictionary<string, AppliedLine> appliedLines)
        {
            Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in drafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft?.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    AddProvenance(result, row, "draft:" + entry.Key + ":applied=" + (draft.DraftApplied ? "1" : "0"));
                }
            }

            foreach (KeyValuePair<string, AppliedLine> entry in appliedLines)
            {
                AppliedLine state = entry.Value;
                if (state?.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in state.StagedRows)
                {
                    AddProvenance(result, row, "applied:" + entry.Key);
                }
            }

            return result;
        }

        private static void AddProvenance(
            Dictionary<string, List<string>> result,
            DispatchWorkbenchStagedRowDto row,
            string label)
        {
            string key = Rows.RowKey(row);
            if (string.IsNullOrEmpty(key))
                return;

            if (!result.TryGetValue(key, out List<string> sources))
            {
                sources = new List<string>();
                result[key] = sources;
            }

            sources.Add(label);
        }

        private static string SummarizePlanRefs(DispatchWorkbenchSaveRequest request)
        {
            if (request?.planRefs != null && request.planRefs.Length > 0)
            {
                return string.Join("|", request.planRefs
                    .Where(entry => entry != null && !string.IsNullOrEmpty(entry.lineId))
                    .Select(entry => (entry.lineId ?? string.Empty)
                        + ":"
                        + (entry.contract?.importedPlanId ?? string.Empty)
                        + ":"
                        + (entry.contract?.importedObjectiveId ?? string.Empty)));
            }

            if (request?.plannerImportContract != null)
            {
                return "legacy:"
                    + (request.plannerImportContract.importedPlanId ?? string.Empty)
                    + ":"
                    + (request.plannerImportContract.importedObjectiveId ?? string.Empty);
            }

            return "-";
        }

        private static string SummarizeStagedRowsByLine(IEnumerable<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null)
                return "-";

            string[] parts = rows
                .Where(row => row != null)
                .GroupBy(row => string.IsNullOrEmpty(row.lineId) ? "(missing)" : row.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    string preview = string.Join(",", group
                        .Select(row => row.time ?? string.Empty)
                        .Where(time => !string.IsNullOrEmpty(time))
                        .OrderBy(time => time, StringComparer.Ordinal)
                        .Take(6));
                    return group.Key + ":" + group.Count() + "(" + preview + ")";
                })
                .ToArray();

            return parts.Length == 0 ? "-" : string.Join("|", parts);
        }

        private static string SummarizeManualRowsByLine(IEnumerable<DispatchWorkbenchManualRowDto> rows)
        {
            if (rows == null)
                return "-";

            string[] parts = rows
                .Where(row => row != null)
                .GroupBy(row => string.IsNullOrEmpty(row.lineId) ? "(missing)" : row.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + ":" + group.Count())
                .ToArray();
            return parts.Length == 0 ? "-" : string.Join("|", parts);
        }

        private static string SummarizeAutoRulesByLine(IEnumerable<DispatchWorkbenchAutoRuleDto> rules)
        {
            if (rules == null)
                return "-";

            string[] parts = rules
                .Where(rule => rule != null)
                .GroupBy(rule => string.IsNullOrEmpty(rule.lineId) ? "(missing)" : rule.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + ":" + group.Count())
                .ToArray();
            return parts.Length == 0 ? "-" : string.Join("|", parts);
        }

        private static string GetWorkbenchReportPath(string fileName)
        {
            string logsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "Colossal Order",
                "Cities Skylines II",
                "Logs");
            Directory.CreateDirectory(logsDirectory);
            return Path.Combine(logsDirectory, fileName);
        }

        private static List<string> NormalizeLineIdList(string[] ids, string fallbackId)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> normalized = new List<string>();

            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id) || !seen.Add(id))
                        continue;
                    normalized.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(fallbackId) && seen.Add(fallbackId))
            {
                normalized.Add(fallbackId);
            }

            return normalized;
        }
    }
}
