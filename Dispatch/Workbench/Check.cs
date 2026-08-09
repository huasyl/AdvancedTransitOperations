using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal static class Check
    {
        internal static List<string> Request(
            DispatchWorkbenchSaveRequest request,
            TransitMode mode,
            List<WorkbenchLineRuntime> runtimeLines,
            bool validateApplyOnlyConstraints,
            List<DispatchWorkbenchDepotDto> depots,
            Func<string[], string, List<WorkbenchLineRuntime>, List<string>> normalizeLineIdList,
            Func<List<DispatchWorkbenchDepotDto>> buildWorkbenchDepots,
            Func<string, int> parseTimeMinutes,
            Func<string, string> normalizeWorkbenchAllowedDepotId,
            Func<string, string> normalizeWorkbenchAllowedDepotIdFromSnapshot,
            int minOriginHoldLimitMinutes,
            int maxOriginHoldLimitMinutes,
            Func<DispatchWorkbenchSaveRequest, string, Dictionary<string, List<DispatchWorkbenchStagedRowDto>>> buildRequestLineDraftRowsByDraftKey,
            Func<string, string> getDraftKey,
            Func<int, string> slotStr)
        {
            List<string> errors = new List<string>();
            if (request == null)
            {
                errors.Add("Empty request.");
                return errors;
            }

            if (request.mergedView == null)
            {
                errors.Add("Merged view is required.");
                return errors;
            }

            List<string> localIds = normalizeLineIdList(
                request.mergedView.localLineIds,
                request.mergedView.localLineId,
                runtimeLines);
            List<string> expressIds = normalizeLineIdList(
                request.mergedView.expressLineIds,
                request.mergedView.expressLineId,
                runtimeLines);
            HashSet<string> draftScopeLineIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(request.selectedLineId))
            {
                draftScopeLineIds.Add(request.selectedLineId);
            }
            if (!string.IsNullOrEmpty(request.selectedEditLine))
            {
                draftScopeLineIds.Add(request.selectedEditLine);
            }
            foreach (string lineId in localIds)
            {
                draftScopeLineIds.Add(lineId);
            }
            foreach (string lineId in expressIds)
            {
                draftScopeLineIds.Add(lineId);
            }

            if (!request.nativeScheduleWriter && localIds.Count == 0)
            {
                errors.Add("At least one local line must be selected.");
            }

            if (runtimeLines != null && runtimeLines.Count > 0)
            {
                for (int i = 0; i < localIds.Count; i++)
                {
                    string id = localIds[i];
                    if (!runtimeLines.Any(line => line.Id == id))
                    {
                        errors.Add("Selected local line no longer exists.");
                    }
                }

                for (int i = 0; i < expressIds.Count; i++)
                {
                    string id = expressIds[i];
                    if (!runtimeLines.Any(line => line.Id == id))
                    {
                        errors.Add("Selected express line no longer exists.");
                    }
                }

                if (localIds.Any(id => expressIds.Contains(id)))
                {
                    errors.Add("Local and express lines cannot contain the same line.");
                }
            }

            Dictionary<string, WorkbenchLineRuntime> runtimeLineById = runtimeLines?
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .GroupBy(line => line.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            List<IGrouping<string, DispatchWorkbenchDepotDto>> depotGroups = (depots ?? buildWorkbenchDepots())
                .Where(depot => depot != null && !string.IsNullOrEmpty(depot.id))
                .GroupBy(depot => depot.id, StringComparer.Ordinal)
                .ToList();
            foreach (IGrouping<string, DispatchWorkbenchDepotDto> group in depotGroups.Where(group => group.Count() > 1))
            {
                string entries = string.Join(
                    ", ",
                    group.Select(depot => (depot.name ?? string.Empty) + "[" + (depot.transportType ?? string.Empty) + "]"));
                Mod.log.Info(
                    "[WorkbenchDepotDuplicate] id=" + group.Key
                    + " count=" + group.Count().ToString()
                    + " entries=[" + entries + "]");
                errors.Add("Depot catalog contains duplicate id " + group.Key + ".");
            }
            Dictionary<string, DispatchWorkbenchDepotDto> depotById = depotGroups
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            if (request.lineSettings != null)
            {
                HashSet<string> seenLineSettings = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < request.lineSettings.Length; i++)
                {
                    DispatchWorkbenchLineSettingDto setting = request.lineSettings[i];
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    {
                        errors.Add("Line setting is missing lineId.");
                        continue;
                    }

                    if (!seenLineSettings.Add(setting.lineId))
                    {
                        errors.Add("Line setting for " + setting.lineId + " is duplicated.");
                    }

                    if (runtimeLineById.Count > 0 && !runtimeLineById.ContainsKey(setting.lineId))
                    {
                        errors.Add("Line setting " + setting.lineId + " references a line that no longer exists.");
                    }

                    if (setting.originHoldLimitMinutes < minOriginHoldLimitMinutes
                        || setting.originHoldLimitMinutes > maxOriginHoldLimitMinutes)
                    {
                        errors.Add("Line setting " + setting.lineId + " has invalid origin hold limit.");
                    }

                    if (setting.maxStationDwellMinutes < minOriginHoldLimitMinutes
                        || setting.maxStationDwellMinutes > maxOriginHoldLimitMinutes)
                    {
                        errors.Add("Line setting " + setting.lineId + " has invalid max station dwell limit.");
                    }

                    if (!string.IsNullOrEmpty(setting.allowedDepotId))
                    {
                        string normalizedDepotId = depots != null
                            ? normalizeWorkbenchAllowedDepotIdFromSnapshot(setting.allowedDepotId)
                            : normalizeWorkbenchAllowedDepotId(setting.allowedDepotId);
                        if (string.IsNullOrEmpty(normalizedDepotId))
                        {
                            continue;
                        }

                        if (!depotById.TryGetValue(normalizedDepotId, out DispatchWorkbenchDepotDto depot))
                        {
                            errors.Add("Line setting " + setting.lineId + " references a depot that no longer exists.");
                        }
                        else if (runtimeLineById.TryGetValue(setting.lineId, out WorkbenchLineRuntime runtimeLine)
                            && !string.IsNullOrEmpty(runtimeLine.TransportType)
                            && !string.IsNullOrEmpty(depot.transportType)
                            && !string.Equals(runtimeLine.TransportType, depot.transportType, StringComparison.Ordinal))
                        {
                            errors.Add("Line setting " + setting.lineId + " references a depot with a different transport type.");
                        }
                    }
                }
            }

            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> requestLineDraftRowsByKey =
                buildRequestLineDraftRowsByDraftKey(request, getDraftKey(request.selectedLineId));
            List<DispatchWorkbenchStagedRowDto> requestLineDraftRows =
                requestLineDraftRowsByKey
                    .Values
                    .SelectMany(group => group)
                    .ToList();
            if (requestLineDraftRows.Count > 0
                || request.lineDraftRows != null
                || request.lineDraftRowsByLineId != null)
            {
                const int minOriginDepartureGapMinutes = 5;
                HashSet<string> stagedKeys = new HashSet<string>();
                Dictionary<string, string> stagedLineKinds = new Dictionary<string, string>();
                List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> stagedOriginDepartures =
                    new List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)>();
                for (int i = 0; i < requestLineDraftRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = requestLineDraftRows[i];
                    if (string.IsNullOrEmpty(row.lineId))
                    {
                        errors.Add("Line draft row " + row.id + " is missing lineId.");
                    }
                    else if (runtimeLineById.Count > 0 && !runtimeLineById.ContainsKey(row.lineId))
                    {
                        errors.Add("Line draft row " + row.id + " references a line that no longer exists.");
                    }

                    if (parseTimeMinutes(row.time) < 0)
                    {
                        errors.Add("Line draft row " + row.id + " has invalid time.");
                    }

                    string stagedKey = (row.lineId ?? string.Empty)
                        + "|"
                        + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                        + "|"
                        + (row.time ?? string.Empty);
                    if (!stagedKeys.Add(stagedKey))
                    {
                        errors.Add("Line draft rows contain duplicate departures for the same line and service.");
                    }

                    string normalizedKind = string.IsNullOrEmpty(row.kind) ? "local" : row.kind;
                    if (stagedLineKinds.TryGetValue(row.lineId ?? string.Empty, out string existingKind))
                    {
                        if (!string.Equals(existingKind, normalizedKind, StringComparison.Ordinal))
                        {
                            errors.Add("Line draft rows cannot mix local and express departures for the same line.");
                        }
                    }
                    else
                    {
                        stagedLineKinds[row.lineId ?? string.Empty] = normalizedKind;
                    }

                    AddOrigin(row, runtimeLineById, stagedOriginDepartures, parseTimeMinutes);
                }

                if (validateApplyOnlyConstraints)
                {
                    foreach (KeyValuePair<string, List<DispatchWorkbenchStagedRowDto>> entry in requestLineDraftRowsByKey)
                    {
                        if (entry.Value == null || entry.Value.Count == 0)
                        {
                            continue;
                        }

                        if (runtimeLineById.TryGetValue(entry.Key, out var runtimeLine)
                            && runtimeLine != null
                            && !runtimeLine.DispatchSupported)
                        {
                            errors.Add($"line-unsupported:{entry.Key}:{runtimeLine.UnsupportedReason}");
                        }
                    }

                    foreach (IGrouping<string, (string RowId, string LineId, string OriginId, string OriginName, int Minutes)> group in stagedOriginDepartures
                        .GroupBy(row => row.OriginId, StringComparer.Ordinal))
                    {
                        (string RowId, string LineId, string OriginId, string OriginName, int Minutes)[] ordered = group
                            .OrderBy(row => row.Minutes)
                            .ToArray();
                        for (int i = 1; i < ordered.Length; i++)
                        {
                            int gap = Gap(ordered[i - 1].Minutes, ordered[i].Minutes);
                            if (gap < minOriginDepartureGapMinutes)
                            {
                                string originLabel = !string.IsNullOrEmpty(ordered[i].OriginName)
                                    ? ordered[i].OriginName
                                    : ordered[i].OriginId;
                                errors.Add(
                                    "Staged rows depart from the same origin station too close together: "
                                    + originLabel
                                    + " "
                                    + slotStr(ordered[i - 1].Minutes)
                                    + " and "
                                    + slotStr(ordered[i].Minutes)
                                    + ".");
                            }
                        }
                        if (ordered.Length > 1 && ordered[0].Minutes != ordered[ordered.Length - 1].Minutes)
                        {
                            int wrapGap = Gap(ordered[ordered.Length - 1].Minutes, ordered[0].Minutes);
                            if (wrapGap < minOriginDepartureGapMinutes)
                            {
                                string originLabel = !string.IsNullOrEmpty(ordered[0].OriginName)
                                    ? ordered[0].OriginName
                                    : ordered[0].OriginId;
                                errors.Add(
                                    "Staged rows depart from the same origin station too close together: "
                                    + originLabel
                                    + " "
                                    + slotStr(ordered[ordered.Length - 1].Minutes)
                                    + " and "
                                    + slotStr(ordered[0].Minutes)
                                    + ".");
                            }
                        }
                    }
                }
            }

            return errors;
        }

        internal static List<string> RawModeContract(
            DispatchWorkbenchSaveRequest request,
            TransitMode mode)
        {
            List<string> errors = new List<string>();
            if (request == null)
                return errors;

            if (mode == TransitMode.Bus)
                ValidateBusRequest(request, errors);
            else if (mode == TransitMode.Tram)
                ValidateTramRequest(request, errors);
            return errors;
        }

        private static void ValidateBusRequest(
            DispatchWorkbenchSaveRequest request,
            List<string> errors)
        {
            ValidateLocalOnlyRequest(request, errors, "Bus");
        }

        private static void ValidateTramRequest(
            DispatchWorkbenchSaveRequest request,
            List<string> errors)
        {
            if (request.plannerImportContract != null)
                errors.Add("Tram schedule does not support planner contracts.");
            if (request.planRefs != null)
            {
                for (int i = 0; i < request.planRefs.Length; i++)
                {
                    if (request.planRefs[i]?.contract != null)
                        errors.Add("Tram schedule does not support planner contracts.");
                }
            }

            ValidateLocalOnlyRequest(request, errors, "Tram");
        }

        private static void ValidateLocalOnlyRequest(
            DispatchWorkbenchSaveRequest request,
            List<string> errors,
            string modeLabel)
        {
            DispatchWorkbenchMergedView view = request.mergedView;
            if (string.Equals(request.selectedEditLine, "express", StringComparison.Ordinal)
                || (view != null
                    && (!string.IsNullOrEmpty(view.expressLineId)
                        || (view.expressLineIds != null && view.expressLineIds.Length > 0))))
            {
                errors.Add(modeLabel + " schedule does not support express lines.");
            }

            ValidateBusPlanContract(request.plannerImportContract, errors, "plannerImportContract", modeLabel);
            if (request.planRefs != null)
            {
                for (int i = 0; i < request.planRefs.Length; i++)
                {
                    ValidateBusPlanContract(
                        request.planRefs[i]?.contract,
                        errors,
                        "planRefs[" + i + "].contract",
                        modeLabel);
                }
            }

            ValidateBusRows(request.manualRows, errors, "manual row", modeLabel);
            ValidateBusRules(request.autoRules, errors, modeLabel);
            ValidateBusRows(request.lineDraftRows, errors, "staged row", modeLabel);
            if (request.lineDraftRowsByLineId != null)
            {
                for (int i = 0; i < request.lineDraftRowsByLineId.Length; i++)
                {
                    ValidateBusRows(
                        request.lineDraftRowsByLineId[i]?.lineDraftRows,
                        errors,
                        "staged row",
                        modeLabel);
                }
            }

            if (request.lineSettings != null)
            {
                for (int i = 0; i < request.lineSettings.Length; i++)
            {
                if (request.lineSettings[i] != null && !IsLocal(request.lineSettings[i].serviceKind))
                        errors.Add(modeLabel + " line settings only support local service.");
                }
            }
        }

        private static void ValidateBusPlanContract(
            DispatchWorkbenchPlannerImportContractDto contract,
            List<string> errors,
            string fieldName,
            string modeLabel)
        {
            if (contract == null)
                return;

            if (HasValues(contract.selectedBypassStationIds))
                errors.Add(modeLabel + " " + fieldName + " does not support bypass stations.");

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo != null
                && (!string.IsNullOrEmpty(echo.expressLineId)
                    || !string.IsNullOrEmpty(echo.virtualExpressBaseLineId)
                    || HasValues(echo.expressStopStationIds)
                    || HasValues(echo.forcedBypassStationIds)
                    || HasBusPlannerMode(echo.expressSourceMode)
                    || HasBusPlannerMode(echo.departureMode)
                    || !string.IsNullOrEmpty(echo.phaseTime)
                    || echo.expressTripsPerHour != 0
                    || echo.expressOffsetMinutes != 0
                    || echo.maxOffsetMinutes != 0
                    || echo.offsetStepMinutes != 0
                    || echo.maxAdditionalBypassStations != 0))
            {
                errors.Add(modeLabel + " " + fieldName + " contains unsupported express, offset, or bypass planning data.");
            }

            ValidateBusChangedRows(contract.changedRows, errors, fieldName, modeLabel);
            ValidateBusActions(contract.structuredActions, errors, fieldName, modeLabel);
            ValidateBusRiskItems(contract.riskItems, errors, fieldName, modeLabel);
        }

        private static bool HasBusPlannerMode(string value)
        {
            return !string.IsNullOrEmpty(value)
                && !string.Equals(value, "local", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasBusBlockedPlannerTerm(string value)
        {
            return !string.IsNullOrEmpty(value)
                && (value.IndexOf("express", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("bypass", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void ValidateBusChangedRows(
            DispatchPlannerChangedRowDto[] rows,
            List<string> errors,
            string fieldName,
            string modeLabel)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                DispatchPlannerChangedRowDto row = rows[i];
                if (row != null && (!IsLocal(row.kind) || HasBusBlockedPlannerTerm(row.changeType)))
                    errors.Add(modeLabel + " " + fieldName + " contains unsupported planner row data.");
            }
        }

        private static void ValidateBusActions(
            DispatchPlannerScheduleActionDto[] actions,
            List<string> errors,
            string fieldName,
            string modeLabel)
        {
            if (actions == null)
                return;

            for (int i = 0; i < actions.Length; i++)
            {
                DispatchPlannerScheduleActionDto action = actions[i];
                if (action != null
                    && (action.deltaOffsetMinutes != 0f
                        || HasBusBlockedPlannerTerm(action.actionType)
                        || HasBusBlockedPlannerTerm(action.type)))
                {
                    errors.Add(modeLabel + " " + fieldName + " contains unsupported planner action data.");
                }
            }
        }

        private static void ValidateBusRiskItems(
            DispatchPlannerRiskItemDto[] items,
            List<string> errors,
            string fieldName,
            string modeLabel)
        {
            if (items == null)
                return;

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] != null && !string.IsNullOrEmpty(items[i].selectedBypassStationId))
                    errors.Add(modeLabel + " " + fieldName + " contains unsupported bypass risk data.");
            }
        }

        private static bool HasValues(string[] values)
        {
            if (values == null)
                return false;

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrEmpty(values[i]))
                    return true;
            }

            return false;
        }

        private static void ValidateBusRows(
            DispatchWorkbenchManualRowDto[] rows,
            List<string> errors,
            string rowKind,
            string modeLabel)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                DispatchWorkbenchManualRowDto row = rows[i];
                if (row != null
                    && (!IsLocal(row.kind)
                        || !string.IsNullOrEmpty(row.offsetMode)
                        || !string.IsNullOrEmpty(row.offsetMinutes)))
                {
                    errors.Add(modeLabel + " " + rowKind + " only supports local service without offset.");
                }
            }
        }

        private static void ValidateBusRows(
            DispatchWorkbenchStagedRowDto[] rows,
            List<string> errors,
            string rowKind,
            string modeLabel)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null && !IsLocal(rows[i].kind))
                    errors.Add(modeLabel + " " + rowKind + " only supports local service.");
            }
        }

        private static void ValidateBusRules(
            DispatchWorkbenchAutoRuleDto[] rules,
            List<string> errors,
            string modeLabel)
        {
            if (rules == null)
                return;

            for (int i = 0; i < rules.Length; i++)
            {
                DispatchWorkbenchAutoRuleDto rule = rules[i];
                if (rule != null
                    && (!IsLocal(rule.kind)
                        || rule.expressPerHour != 0d
                        || !string.IsNullOrEmpty(rule.expressOffsetMode)
                        || rule.expressOffsetMinutes != 0))
                {
                    errors.Add(modeLabel + " auto rule only supports local service without express offset.");
                }
            }
        }

        private static bool IsLocal(string kind)
        {
            return string.IsNullOrEmpty(kind)
                || string.Equals(kind, "local", StringComparison.Ordinal);
        }

        internal static List<string> AppliedRows(
            string activeLineKey,
            List<DispatchWorkbenchStagedRowDto> activeRows,
            List<WorkbenchLineRuntime> runtimeLines,
            IReadOnlyDictionary<string, AppliedLine> appliedLines,
            Func<string, int> parseTimeMinutes,
            Func<int, string> slotStr)
        {
            const int minOriginDepartureGapMinutes = 5;
            List<string> errors = new List<string>();
            Dictionary<string, WorkbenchLineRuntime> runtimeLineById = runtimeLines?
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .GroupBy(line => line.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                ?? new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            HashSet<string> replacedLineIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(activeLineKey))
            {
                replacedLineIds.Add(activeLineKey);
            }
            if (activeRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in activeRows)
                {
                    if (!string.IsNullOrEmpty(row?.lineId))
                    {
                        replacedLineIds.Add(row.lineId);
                    }
                }
            }

            List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> departures =
                new List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)>();
            foreach (KeyValuePair<string, AppliedLine> entry in appliedLines)
            {
                if (replacedLineIds.Contains(entry.Key))
                    continue;

                AppliedLine state = entry.Value;
                if (state == null || state.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in state.StagedRows)
                {
                    AddOrigin(row, runtimeLineById, departures, parseTimeMinutes);
                }
            }

            if (activeRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in activeRows)
                {
                    AddOrigin(row, runtimeLineById, departures, parseTimeMinutes);
                }
            }

            foreach (IGrouping<string, (string RowId, string LineId, string OriginId, string OriginName, int Minutes)> group in departures
                .GroupBy(row => row.OriginId, StringComparer.Ordinal))
            {
                (string RowId, string LineId, string OriginId, string OriginName, int Minutes)[] ordered = group
                    .OrderBy(row => row.Minutes)
                    .ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    int gap = Gap(ordered[i - 1].Minutes, ordered[i].Minutes);
                    if (gap >= minOriginDepartureGapMinutes)
                        continue;

                    string originLabel = !string.IsNullOrEmpty(ordered[i].OriginName)
                        ? ordered[i].OriginName
                        : ordered[i].OriginId;
                    errors.Add(
                        "Applied timetable would depart from the same origin station too close together: "
                        + originLabel
                        + " "
                        + slotStr(ordered[i - 1].Minutes)
                        + " "
                        + ordered[i - 1].LineId
                        + " and "
                        + slotStr(ordered[i].Minutes)
                        + " "
                        + ordered[i].LineId
                        + ".");
                }
                if (ordered.Length > 1 && ordered[0].Minutes != ordered[ordered.Length - 1].Minutes)
                {
                    int wrapGap = Gap(ordered[ordered.Length - 1].Minutes, ordered[0].Minutes);
                    if (wrapGap >= minOriginDepartureGapMinutes)
                        continue;

                    string originLabel = !string.IsNullOrEmpty(ordered[0].OriginName)
                        ? ordered[0].OriginName
                        : ordered[0].OriginId;
                    errors.Add(
                        "Applied timetable would depart from the same origin station too close together: "
                        + originLabel
                        + " "
                        + slotStr(ordered[ordered.Length - 1].Minutes)
                        + " "
                        + ordered[ordered.Length - 1].LineId
                        + " and "
                        + slotStr(ordered[0].Minutes)
                        + " "
                        + ordered[0].LineId
                        + ".");
                }
            }

            return errors;
        }

        internal static int Gap(int previousMinutes, int nextMinutes)
        {
            const int dayMinutes = 24 * 60;
            int previous = ((previousMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            int next = ((nextMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            return next >= previous
                ? next - previous
                : dayMinutes - previous + next;
        }

        internal static void AddOrigin(
            DispatchWorkbenchStagedRowDto row,
            Dictionary<string, WorkbenchLineRuntime> runtimeLineById,
            List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> departures,
            Func<string, int> parseTimeMinutes)
        {
            int minutes = parseTimeMinutes(row?.time);
            if (minutes < 0)
                return;

            string lineId = row?.lineId ?? string.Empty;
            if (!runtimeLineById.TryGetValue(lineId, out WorkbenchLineRuntime runtimeLine)
                || string.IsNullOrEmpty(runtimeLine.OriginStationId))
            {
                return;
            }

            departures.Add((
                row?.id ?? string.Empty,
                lineId,
                runtimeLine.OriginStationId,
                runtimeLine.OriginStationName ?? string.Empty,
                minutes));
        }

        internal static void NormalizeView(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            Func<string[], string, List<WorkbenchLineRuntime>, List<string>> normalizeLineIdList,
            Func<string, string> normalizeWorkbenchServiceKind,
            Func<string, string> getWorkbenchConfiguredLineServiceKind,
            Dictionary<string, string> configuredKinds = null)
        {
            if (request?.mergedView == null)
                return;

            WorkbenchLineRuntime fallbackLine = runtimeLines?.FirstOrDefault();
            SplitKinds(
                request.mergedView,
                runtimeLines,
                request.lineSettings,
                fallbackLine,
                normalizeLineIdList,
                normalizeWorkbenchServiceKind,
                getWorkbenchConfiguredLineServiceKind,
                configuredKinds);
            if (string.Equals(request.mode, "tram", StringComparison.OrdinalIgnoreCase))
                NormalizeTramView(request);
        }

        private static void NormalizeTramView(DispatchWorkbenchSaveRequest request)
        {
            DispatchWorkbenchMergedView view = request.mergedView;
            view.localLineIds = (view.localLineIds ?? Array.Empty<string>())
                .Concat(view.expressLineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            view.localLineId = view.localLineIds.FirstOrDefault() ?? string.Empty;
            view.expressLineId = string.Empty;
            view.expressLineIds = Array.Empty<string>();
            NormalizeTramSettings(request.lineSettings);
            NormalizeTramRows(request.lineDraftRows);
            if (request.lineDraftRowsByLineId != null)
            {
                for (int i = 0; i < request.lineDraftRowsByLineId.Length; i++)
                    NormalizeTramRows(request.lineDraftRowsByLineId[i]?.lineDraftRows);
            }
        }

        private static void NormalizeTramSettings(DispatchWorkbenchLineSettingDto[] settings)
        {
            if (settings == null)
                return;

            for (int i = 0; i < settings.Length; i++)
            {
                if (settings[i] != null)
                    settings[i].serviceKind = "local";
            }
        }

        private static void NormalizeTramRows(DispatchWorkbenchStagedRowDto[] rows)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null)
                    rows[i].kind = "local";
            }
        }

        internal static void SplitKinds(
            DispatchWorkbenchMergedView mergedView,
            List<WorkbenchLineRuntime> lines,
            IEnumerable<DispatchWorkbenchLineSettingDto> requestedSettings,
            WorkbenchLineRuntime fallbackLine,
            Func<string[], string, List<WorkbenchLineRuntime>, List<string>> normalizeLineIdList,
            Func<string, string> normalizeWorkbenchServiceKind,
            Func<string, string> getWorkbenchConfiguredLineServiceKind,
            Dictionary<string, string> configuredKinds = null)
        {
            if (mergedView == null)
                return;

            Dictionary<string, WorkbenchLineRuntime> lineById = new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            if (lines != null)
            {
                foreach (WorkbenchLineRuntime line in lines)
                {
                    if (line != null && !string.IsNullOrEmpty(line.Id) && !lineById.ContainsKey(line.Id))
                    {
                        lineById[line.Id] = line;
                    }
                }
            }
            Dictionary<string, string> requestedKindById = new Dictionary<string, string>(StringComparer.Ordinal);
            if (requestedSettings != null)
            {
                foreach (DispatchWorkbenchLineSettingDto setting in requestedSettings)
                {
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                        continue;

                    string normalizedKind = normalizeWorkbenchServiceKind(setting.serviceKind);
                    if (!string.IsNullOrEmpty(normalizedKind))
                    {
                        requestedKindById[setting.lineId] = normalizedKind;
                    }
                }
            }

            List<string> mergedLineIds = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in normalizeLineIdList(mergedView.localLineIds, mergedView.localLineId, lines))
            {
                if (seen.Add(lineId))
                {
                    mergedLineIds.Add(lineId);
                }
            }
            foreach (string lineId in normalizeLineIdList(mergedView.expressLineIds, mergedView.expressLineId, lines))
            {
                if (seen.Add(lineId))
                {
                    mergedLineIds.Add(lineId);
                }
            }
            if (mergedLineIds.Count == 0 && fallbackLine != null && !string.IsNullOrEmpty(fallbackLine.Id))
            {
                mergedLineIds.Add(fallbackLine.Id);
            }

            List<string> localIds = new List<string>();
            List<string> expressIds = new List<string>();
            foreach (string lineId in mergedLineIds)
            {
                string kind = ResolveKind(
                    lineId,
                    lineById,
                    requestedKindById,
                    normalizeWorkbenchServiceKind,
                    getWorkbenchConfiguredLineServiceKind,
                    configuredKinds);
                if (string.Equals(kind, "express", StringComparison.Ordinal))
                {
                    expressIds.Add(lineId);
                }
                else
                {
                    localIds.Add(lineId);
                }
            }

            mergedView.localLineIds = localIds.ToArray();
            mergedView.expressLineIds = expressIds.ToArray();
            mergedView.localLineId = localIds.FirstOrDefault() ?? string.Empty;
            mergedView.expressLineId = expressIds.FirstOrDefault() ?? string.Empty;
        }

        internal static string ResolveKind(
            string lineId,
            Dictionary<string, WorkbenchLineRuntime> lineById,
            Dictionary<string, string> requestedKindById,
            Func<string, string> normalizeWorkbenchServiceKind,
            Func<string, string> getWorkbenchConfiguredLineServiceKind,
            Dictionary<string, string> configuredKinds = null)
        {
            if (string.IsNullOrEmpty(lineId))
                return "local";

            if (requestedKindById != null
                && requestedKindById.TryGetValue(lineId, out string requestedKind)
                && !string.IsNullOrEmpty(requestedKind))
            {
                return requestedKind;
            }

            if (lineById != null
                && lineById.TryGetValue(lineId, out WorkbenchLineRuntime runtimeLine)
                && string.Equals(runtimeLine.Kind, "express", StringComparison.Ordinal))
            {
                return "express";
            }

            string configuredKind = configuredKinds != null && configuredKinds.TryGetValue(lineId, out string capturedKind)
                ? normalizeWorkbenchServiceKind(capturedKind)
                : getWorkbenchConfiguredLineServiceKind(lineId);
            return string.IsNullOrEmpty(configuredKind) ? "local" : configuredKind;
        }
    }
}
