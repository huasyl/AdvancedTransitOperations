using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod
{
    internal sealed class WorkbenchCommandHandler
    {
        private readonly WorkbenchQueryService m_Query;
        private readonly WorkbenchDraftStore m_Drafts;
        private readonly Func<ulong> m_GetSnapshotVersion;
        private readonly Func<ulong> m_AdvanceSnapshotVersion;
        private readonly Func<string, DispatchWorkbenchSnapshot> m_BuildSnapshot;
        private readonly Func<string, DispatchWorkbenchDraftState> m_GetOrCreateDraft;
        private readonly Action<string> m_SetPreferredLineId;
        private readonly Action<RuntimeFeatureSettingsDto> m_ApplyRuntimeFeatureSettings;
        private readonly Func<RuntimeFeatureSettingsDto, bool> m_AreRuntimeFeatureSettingsEquivalent;
        private readonly Action<IEnumerable<DispatchWorkbenchLineSettingDto>> m_ApplyWorkbenchLineSettings;
        private readonly Func<IEnumerable<DispatchWorkbenchLineSettingDto>, bool> m_AreWorkbenchLineSettingsEquivalent;
        private readonly Func<string, List<DispatchWorkbenchStagedRowDto>, List<WorkbenchLineRuntime>, List<string>> m_ValidateAppliedRows;
        private readonly Func<DispatchWorkbenchSaveRequest, List<WorkbenchLineRuntime>, bool, List<DispatchWorkbenchDepotDto>, List<string>> m_ValidateRequest;
        private readonly Func<IEnumerable<string>> m_EnumerateLineSettingIds;
        private readonly Func<string, string> m_GetConfiguredLineServiceKind;
        private readonly Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> m_CloneManualRow;
        private readonly Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> m_CloneAutoRule;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CloneStagedRow;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_ClonePlannerImportContract;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_DeduplicateRowsByIdLast;
        private readonly Func<DispatchWorkbenchMergedView, DispatchWorkbenchMergedView, bool> m_AreMergedViewsEquivalent;
        private readonly Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>, bool> m_AreManualRowsEquivalent;
        private readonly Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>, bool> m_AreAutoRulesEquivalent;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>, bool> m_AreStagedRowsEquivalent;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto, bool> m_ArePlannerImportContractsEquivalent;
        private readonly Action m_RefreshAppliedWorkbenchLineSettings;
        private readonly Action<IEnumerable<string>, List<WorkbenchLineRuntime>> m_ApplyWorkbenchDraftRowsToAppliedRuntime;
        private readonly Action<string> m_SeedObservationFromAppliedRows;
        private readonly Action m_InvalidateAppliedWorkbenchTrackModelState;
        private readonly Action m_SaveWorkbenchPersistence;
        private readonly Action m_SaveAppliedWorkbenchPersistence;
        private readonly Action<DispatchWorkbenchSnapshot> m_PublishSnapshot;

        public WorkbenchCommandHandler(
            WorkbenchQueryService query,
            WorkbenchDraftStore drafts,
            Func<ulong> getSnapshotVersion,
            Func<ulong> advanceSnapshotVersion,
            Func<string, DispatchWorkbenchSnapshot> buildSnapshot,
            Func<string, DispatchWorkbenchDraftState> getOrCreateDraft,
            Action<string> setPreferredLineId,
            Action<RuntimeFeatureSettingsDto> applyRuntimeFeatureSettings,
            Func<RuntimeFeatureSettingsDto, bool> areRuntimeFeatureSettingsEquivalent,
            Action<IEnumerable<DispatchWorkbenchLineSettingDto>> applyWorkbenchLineSettings,
            Func<IEnumerable<DispatchWorkbenchLineSettingDto>, bool> areWorkbenchLineSettingsEquivalent,
            Func<string, List<DispatchWorkbenchStagedRowDto>, List<WorkbenchLineRuntime>, List<string>> validateAppliedRows,
            Func<DispatchWorkbenchSaveRequest, List<WorkbenchLineRuntime>, bool, List<DispatchWorkbenchDepotDto>, List<string>> validateRequest,
            Func<IEnumerable<string>> enumerateLineSettingIds,
            Func<string, string> getConfiguredLineServiceKind,
            Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> cloneManualRow,
            Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> cloneAutoRule,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> clonePlannerImportContract,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> deduplicateRowsByIdLast,
            Func<DispatchWorkbenchMergedView, DispatchWorkbenchMergedView, bool> areMergedViewsEquivalent,
            Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>, bool> areManualRowsEquivalent,
            Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>, bool> areAutoRulesEquivalent,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>, bool> areStagedRowsEquivalent,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto, bool> arePlannerImportContractsEquivalent,
            Action refreshAppliedWorkbenchLineSettings,
            Action<IEnumerable<string>, List<WorkbenchLineRuntime>> applyWorkbenchDraftRowsToAppliedRuntime,
            Action<string> seedObservationFromAppliedRows,
            Action invalidateAppliedWorkbenchTrackModelState,
            Action saveWorkbenchPersistence,
            Action saveAppliedWorkbenchPersistence,
            Action<DispatchWorkbenchSnapshot> publishSnapshot)
        {
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_GetSnapshotVersion = getSnapshotVersion ?? throw new ArgumentNullException(nameof(getSnapshotVersion));
            m_AdvanceSnapshotVersion = advanceSnapshotVersion ?? throw new ArgumentNullException(nameof(advanceSnapshotVersion));
            m_BuildSnapshot = buildSnapshot ?? throw new ArgumentNullException(nameof(buildSnapshot));
            m_GetOrCreateDraft = getOrCreateDraft ?? throw new ArgumentNullException(nameof(getOrCreateDraft));
            m_SetPreferredLineId = setPreferredLineId ?? throw new ArgumentNullException(nameof(setPreferredLineId));
            m_ApplyRuntimeFeatureSettings = applyRuntimeFeatureSettings ?? throw new ArgumentNullException(nameof(applyRuntimeFeatureSettings));
            m_AreRuntimeFeatureSettingsEquivalent = areRuntimeFeatureSettingsEquivalent ?? throw new ArgumentNullException(nameof(areRuntimeFeatureSettingsEquivalent));
            m_ApplyWorkbenchLineSettings = applyWorkbenchLineSettings ?? throw new ArgumentNullException(nameof(applyWorkbenchLineSettings));
            m_AreWorkbenchLineSettingsEquivalent = areWorkbenchLineSettingsEquivalent ?? throw new ArgumentNullException(nameof(areWorkbenchLineSettingsEquivalent));
            m_ValidateAppliedRows = validateAppliedRows ?? throw new ArgumentNullException(nameof(validateAppliedRows));
            m_ValidateRequest = validateRequest ?? throw new ArgumentNullException(nameof(validateRequest));
            m_EnumerateLineSettingIds = enumerateLineSettingIds ?? throw new ArgumentNullException(nameof(enumerateLineSettingIds));
            m_GetConfiguredLineServiceKind = getConfiguredLineServiceKind ?? throw new ArgumentNullException(nameof(getConfiguredLineServiceKind));
            m_CloneManualRow = cloneManualRow ?? throw new ArgumentNullException(nameof(cloneManualRow));
            m_CloneAutoRule = cloneAutoRule ?? throw new ArgumentNullException(nameof(cloneAutoRule));
            m_CloneStagedRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_ClonePlannerImportContract = clonePlannerImportContract ?? throw new ArgumentNullException(nameof(clonePlannerImportContract));
            m_DeduplicateRowsByIdLast = deduplicateRowsByIdLast ?? throw new ArgumentNullException(nameof(deduplicateRowsByIdLast));
            m_AreMergedViewsEquivalent = areMergedViewsEquivalent ?? throw new ArgumentNullException(nameof(areMergedViewsEquivalent));
            m_AreManualRowsEquivalent = areManualRowsEquivalent ?? throw new ArgumentNullException(nameof(areManualRowsEquivalent));
            m_AreAutoRulesEquivalent = areAutoRulesEquivalent ?? throw new ArgumentNullException(nameof(areAutoRulesEquivalent));
            m_AreStagedRowsEquivalent = areStagedRowsEquivalent ?? throw new ArgumentNullException(nameof(areStagedRowsEquivalent));
            m_ArePlannerImportContractsEquivalent = arePlannerImportContractsEquivalent ?? throw new ArgumentNullException(nameof(arePlannerImportContractsEquivalent));
            m_RefreshAppliedWorkbenchLineSettings = refreshAppliedWorkbenchLineSettings ?? throw new ArgumentNullException(nameof(refreshAppliedWorkbenchLineSettings));
            m_ApplyWorkbenchDraftRowsToAppliedRuntime = applyWorkbenchDraftRowsToAppliedRuntime ?? throw new ArgumentNullException(nameof(applyWorkbenchDraftRowsToAppliedRuntime));
            m_SeedObservationFromAppliedRows = seedObservationFromAppliedRows ?? throw new ArgumentNullException(nameof(seedObservationFromAppliedRows));
            m_InvalidateAppliedWorkbenchTrackModelState = invalidateAppliedWorkbenchTrackModelState ?? throw new ArgumentNullException(nameof(invalidateAppliedWorkbenchTrackModelState));
            m_SaveWorkbenchPersistence = saveWorkbenchPersistence ?? throw new ArgumentNullException(nameof(saveWorkbenchPersistence));
            m_SaveAppliedWorkbenchPersistence = saveAppliedWorkbenchPersistence ?? throw new ArgumentNullException(nameof(saveAppliedWorkbenchPersistence));
            m_PublishSnapshot = publishSnapshot ?? throw new ArgumentNullException(nameof(publishSnapshot));
        }

        public WorkbenchSavePrepareContext CaptureSavePrepareContext(string requestJson)
        {
            return new WorkbenchSavePrepareContext
            {
                RequestJson = requestJson ?? string.Empty,
                SnapshotVersion = m_GetSnapshotVersion(),
                RuntimeLines = m_Query.GetLines(),
                Depots = m_Query.GetDepots(),
                ServiceKinds = m_EnumerateLineSettingIds()
                    .ToDictionary(
                        lineId => lineId,
                        lineId => m_GetConfiguredLineServiceKind(lineId),
                        StringComparer.Ordinal)
            };
        }

        public PreparedWorkbenchSave PrepareSave(WorkbenchSavePrepareContext context)
        {
            ulong baseVersion = context?.SnapshotVersion ?? m_GetSnapshotVersion();
            PreparedWorkbenchSave prepared = new PreparedWorkbenchSave
            {
                SnapshotVersion = baseVersion,
                RuntimeLines = context?.RuntimeLines ?? new List<WorkbenchLineRuntime>()
            };

            try
            {
                DispatchWorkbenchSaveRequest request =
                    DispatchWorkbenchJson.Deserialize<DispatchWorkbenchSaveRequest>(context?.RequestJson);
                List<WorkbenchLineRuntime> runtimeLines = (context?.RuntimeLines ?? new List<WorkbenchLineRuntime>())
                    .Select(CloneWorkbenchLineRuntime)
                    .ToList();
                List<DispatchWorkbenchDepotDto> depots = (context?.Depots ?? new List<DispatchWorkbenchDepotDto>())
                    .Select(CloneWorkbenchDepot)
                    .ToList();

                NormalizeRequestedMergedViewFromLineSettings(
                    request,
                    runtimeLines,
                    context?.ServiceKinds);
                List<string> errors = m_ValidateRequest(
                    request,
                    runtimeLines,
                    request?.applyDraft == true,
                    depots);
                prepared.Request = request;
                prepared.RuntimeLines = runtimeLines;
                prepared.Errors = errors;
                prepared.ShouldReturnSnapshot = request?.returnSnapshot != false;
                prepared.LineSettingsChanged = request?.lineSettings != null;
                return prepared;
            }
            catch (Exception ex)
            {
                prepared.Errors = new List<string> { ex.GetType().Name + ": " + ex.Message };
                return prepared;
            }
        }

        public DispatchWorkbenchSaveResult CommitPreparedSave(
            PreparedWorkbenchSave prepared,
            bool persistImmediately)
        {
            DispatchWorkbenchSaveResult result = prepared?.ToResult()
                ?? CreateWorkbenchSaveFailureResult(m_GetSnapshotVersion(), "save-prepare-failed");
            if (prepared == null || prepared.HasErrors)
            {
                if (prepared?.Request != null)
                {
                    result.snapshot = m_BuildSnapshot(prepared.Request.selectedLineId);
                }
                return result;
            }

            DispatchWorkbenchSaveRequest request = prepared.Request;
            List<WorkbenchLineRuntime> runtimeLines = prepared.RuntimeLines ?? m_Query.GetLines();
            string lineKey = WorkbenchDraftStore.GetKey(request?.selectedLineId);
            DispatchWorkbenchDraftState state = m_GetOrCreateDraft(lineKey);
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> nextLineDraftRowsByKey =
                BuildRequestLineDraftRowsByDraftKey(request, lineKey);
            List<DispatchWorkbenchManualRowDto> nextManualRows = request.manualRows != null
                ? request.manualRows.Select(m_CloneManualRow).ToList()
                : new List<DispatchWorkbenchManualRowDto>();
            List<DispatchWorkbenchAutoRuleDto> nextAutoRules = request.autoRules != null
                ? request.autoRules.Select(m_CloneAutoRule).ToList()
                : new List<DispatchWorkbenchAutoRuleDto>();
            bool hasActiveLineDraftRows = nextLineDraftRowsByKey.TryGetValue(
                lineKey,
                out List<DispatchWorkbenchStagedRowDto> activeLineDraftRows);
            List<DispatchWorkbenchStagedRowDto> nextStagedRows = hasActiveLineDraftRows
                ? m_DeduplicateRowsByIdLast(activeLineDraftRows)
                : state.StagedRows.Select(m_CloneStagedRow).ToList();

            if (request.applyDraft)
            {
                List<string> appliedErrors = m_ValidateAppliedRows(
                    lineKey,
                    nextLineDraftRowsByKey.Values.SelectMany(rows => rows).ToList(),
                    runtimeLines);
                if (appliedErrors.Count > 0)
                {
                    result.success = false;
                    result.errors = appliedErrors.ToArray();
                    result.snapshot = m_BuildSnapshot(request.selectedLineId);
                    return result;
                }
            }

            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> nextPlanRefsByKey =
                BuildRequestPlanRefsByDraftKey(
                    request,
                    nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }));
            string requestedSelectedLineId = string.IsNullOrEmpty(request.selectedLineId) ? lineKey : request.selectedLineId;
            string requestedSelectedEditLine = string.IsNullOrEmpty(request.selectedEditLine) ? "local" : request.selectedEditLine;
            bool hasAdditionalLineDraftTargets = nextLineDraftRowsByKey.Keys
                .Any(key => !string.Equals(key, lineKey, StringComparison.Ordinal));
            bool rulesChanged = !m_AreManualRowsEquivalent(state.ManualRows, nextManualRows)
                || !m_AreAutoRulesEquivalent(state.AutoRules, nextAutoRules)
                || !m_AreStagedRowsEquivalent(state.StagedRows, nextStagedRows);
            DispatchWorkbenchPlannerImportContractDto nextPlanRef = ResolvePlanRef(
                lineKey,
                state.PlannerImportContract,
                nextPlanRefsByKey,
                rulesChanged,
                nextStagedRows.Count > 0);

            if (!request.applyDraft
                && !request.markRulesApplied
                && !hasAdditionalLineDraftTargets
                && string.Equals(state.SelectedLineId ?? string.Empty, requestedSelectedLineId, StringComparison.Ordinal)
                && string.Equals(state.SelectedEditLine ?? string.Empty, requestedSelectedEditLine, StringComparison.Ordinal)
                && m_AreMergedViewsEquivalent(state.MergedView, request.mergedView)
                && m_AreManualRowsEquivalent(state.ManualRows, nextManualRows)
                && m_AreAutoRulesEquivalent(state.AutoRules, nextAutoRules)
                && m_AreStagedRowsEquivalent(state.StagedRows, nextStagedRows)
                && m_ArePlannerImportContractsEquivalent(state.PlannerImportContract, nextPlanRef)
                && m_AreWorkbenchLineSettingsEquivalent(request.lineSettings)
                && m_AreRuntimeFeatureSettingsEquivalent(request.featureSettings))
            {
                result.success = true;
                result.version = m_GetSnapshotVersion().ToString();
                result.snapshot = null;
                return result;
            }

            if (request.featureSettings != null)
            {
                m_ApplyRuntimeFeatureSettings(request.featureSettings);
            }
            if (request.lineSettings != null)
            {
                m_ApplyWorkbenchLineSettings(request.lineSettings);
            }

            state.SelectedLineId = requestedSelectedLineId;
            state.SelectedEditLine = requestedSelectedEditLine;
            state.MergedView = request.mergedView ?? state.MergedView;
            state.ManualRows = nextManualRows;
            state.AutoRules = nextAutoRules;
            state.StagedRows = nextStagedRows;
            state.PlannerImportContract = nextPlanRef;
            bool additionalDraftRowsChanged = ApplyAdditionalLineDraftRowsByDraftKey(
                lineKey,
                nextLineDraftRowsByKey,
                nextPlanRefsByKey,
                request.applyDraft);
            if (additionalDraftRowsChanged)
            {
                rulesChanged = true;
            }
            HashSet<string> cleanupLineIds = CollectTouchedWorkbenchLineIds(
                state.SelectedLineId,
                state.SelectedEditLine,
                nextManualRows,
                nextAutoRules,
                nextLineDraftRowsByKey.Values.SelectMany(rows => rows).ToList());
            foreach (string draftTargetKey in nextLineDraftRowsByKey.Keys)
            {
                if (!string.IsNullOrEmpty(draftTargetKey) && !string.Equals(draftTargetKey, "__default__", StringComparison.Ordinal))
                {
                    cleanupLineIds.Add(draftTargetKey);
                }
            }

            RemoveWorkbenchRowsFromOtherDrafts(
                new HashSet<string>(nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }), StringComparer.Ordinal),
                cleanupLineIds);
            state.AppliedDepartureMinutesCache.Clear();

            if (rulesChanged)
            {
                state.RulesApplied = false;
                state.DraftApplied = false;
            }
            if (request.markRulesApplied)
            {
                state.RulesApplied = true;
            }

            int applyRowCount = nextLineDraftRowsByKey.Values.Sum(rows => rows?.Count ?? 0);
            if (request.applyDraft && applyRowCount == 0)
            {
                result.success = false;
                result.errors = new[] { "Add rows into the line draft timetable before applying the draft." };
                m_SetPreferredLineId(state.SelectedLineId);
                m_RefreshAppliedWorkbenchLineSettings();
                if (persistImmediately)
                {
                    m_SaveWorkbenchPersistence();
                    m_SaveAppliedWorkbenchPersistence();
                }
                result.snapshot = m_BuildSnapshot(state.SelectedLineId);
                return result;
            }

            if (request.applyDraft && nextLineDraftRowsByKey.ContainsKey(lineKey))
            {
                state.DraftApplied = true;
            }

            ulong nextVersion = m_AdvanceSnapshotVersion();
            m_SetPreferredLineId(state.SelectedLineId);
            if (request.applyDraft)
            {
                m_ApplyWorkbenchDraftRowsToAppliedRuntime(nextLineDraftRowsByKey.Keys, runtimeLines);
                m_SeedObservationFromAppliedRows(state.SelectedLineId);
            }
            else
            {
                m_RefreshAppliedWorkbenchLineSettings();
                if (prepared.LineSettingsChanged)
                {
                    m_InvalidateAppliedWorkbenchTrackModelState();
                }
            }
            if (persistImmediately)
            {
                m_SaveWorkbenchPersistence();
                m_SaveAppliedWorkbenchPersistence();
            }

            result.success = true;
            result.version = nextVersion.ToString();
            result.appliedLineIds = request.applyDraft
                ? nextLineDraftRowsByKey.Keys
                    .Where(key => !string.IsNullOrEmpty(key) && !string.Equals(key, "__default__", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
            if (prepared.ShouldReturnSnapshot)
            {
                result.snapshot = m_BuildSnapshot(state.SelectedLineId);
                m_PublishSnapshot(result.snapshot);
            }
            else
            {
                result.snapshot = null;
            }
            return result;
        }

        private bool ApplyAdditionalLineDraftRowsByDraftKey(
            string activeLineKey,
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey,
            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> requestRefsByDraftKey,
            bool markDraftApplied)
        {
            if (rowsByDraftKey == null || rowsByDraftKey.Count == 0)
                return false;

            bool changed = false;
            foreach (KeyValuePair<string, List<DispatchWorkbenchStagedRowDto>> entry in rowsByDraftKey)
            {
                string draftKey = WorkbenchDraftStore.GetKey(entry.Key);
                if (string.Equals(draftKey, activeLineKey, StringComparison.Ordinal))
                    continue;

                DispatchWorkbenchDraftState draft = m_GetOrCreateDraft(draftKey);
                List<DispatchWorkbenchStagedRowDto> nextRows =
                    m_DeduplicateRowsByIdLast(entry.Value?.Select(m_CloneStagedRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>());
                bool draftRowsChanged = !m_AreStagedRowsEquivalent(draft.StagedRows, nextRows);
                DispatchWorkbenchPlannerImportContractDto nextRef = ResolvePlanRef(
                    draftKey,
                    draft.PlannerImportContract,
                    requestRefsByDraftKey,
                    draftRowsChanged,
                    nextRows.Count > 0);
                bool refChanged = !m_ArePlannerImportContractsEquivalent(draft.PlannerImportContract, nextRef);
                if (!draftRowsChanged && !refChanged && (!markDraftApplied || draft.DraftApplied))
                    continue;

                draft.SelectedLineId = draftKey;
                draft.SelectedEditLine = draftKey == "__default__" ? string.Empty : draftKey;
                draft.StagedRows = nextRows;
                draft.RulesApplied = markDraftApplied;
                draft.DraftApplied = markDraftApplied;
                draft.AppliedDepartureMinutesCache.Clear();
                draft.PlannerImportContract = nextRef;
                changed = true;
            }

            return changed;
        }

        private void RemoveWorkbenchRowsFromOtherDrafts(HashSet<string> skippedDraftKeys, HashSet<string> lineIds)
        {
            if (lineIds == null || lineIds.Count == 0)
                return;

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                if (skippedDraftKeys != null && skippedDraftKeys.Contains(entry.Key))
                    continue;

                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null)
                    continue;

                int manualBefore = draft.ManualRows?.Count ?? 0;
                int autoBefore = draft.AutoRules?.Count ?? 0;
                int stagedBefore = draft.StagedRows?.Count ?? 0;

                if (draft.ManualRows != null)
                {
                    draft.ManualRows = draft.ManualRows
                        .Where(row => row == null || string.IsNullOrEmpty(row.lineId) || !lineIds.Contains(row.lineId))
                        .ToList();
                }

                if (draft.AutoRules != null)
                {
                    draft.AutoRules = draft.AutoRules
                        .Where(rule => rule == null || string.IsNullOrEmpty(rule.lineId) || !lineIds.Contains(rule.lineId))
                        .ToList();
                }

                if (draft.StagedRows != null)
                {
                    draft.StagedRows = draft.StagedRows
                        .Where(row => row == null || string.IsNullOrEmpty(row.lineId) || !lineIds.Contains(row.lineId))
                        .ToList();
                }

                bool changed = manualBefore != (draft.ManualRows?.Count ?? 0)
                    || autoBefore != (draft.AutoRules?.Count ?? 0)
                    || stagedBefore != (draft.StagedRows?.Count ?? 0);

                if (!changed)
                    continue;

                draft.RulesApplied = false;
                draft.DraftApplied = false;
                draft.PlannerImportContract = null;
                draft.AppliedDepartureMinutesCache.Clear();
            }
        }

        private static DispatchWorkbenchSaveResult CreateWorkbenchSaveFailureResult(
            ulong version,
            string error)
        {
            return new DispatchWorkbenchSaveResult
            {
                success = false,
                errors = new[] { error ?? string.Empty },
                warnings = Array.Empty<string>(),
                version = version.ToString(),
                appliedLineIds = Array.Empty<string>(),
                snapshot = null
            };
        }

        private static WorkbenchLineRuntime CloneWorkbenchLineRuntime(WorkbenchLineRuntime line)
        {
            if (line == null)
                return null;

            return new WorkbenchLineRuntime
            {
                Entity = line.Entity,
                Id = line.Id ?? string.Empty,
                Name = line.Name ?? string.Empty,
                Kind = string.IsNullOrEmpty(line.Kind) ? "local" : line.Kind,
                TransportType = line.TransportType ?? string.Empty,
                RouteNumber = line.RouteNumber,
                StationCount = line.StationCount,
                Color = line.Color ?? string.Empty,
                OriginStationId = line.OriginStationId ?? string.Empty,
                OriginStationName = line.OriginStationName ?? string.Empty
            };
        }

        private static DispatchWorkbenchDepotDto CloneWorkbenchDepot(DispatchWorkbenchDepotDto depot)
        {
            if (depot == null)
                return null;

            return new DispatchWorkbenchDepotDto
            {
                id = depot.id ?? string.Empty,
                name = depot.name ?? string.Empty,
                transportType = depot.transportType ?? string.Empty
            };
        }

        private static DispatchWorkbenchStagedRowDto[] GetRequestLineDraftRows(DispatchWorkbenchSaveRequest request)
        {
            return request?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
        }

        private Dictionary<string, List<DispatchWorkbenchStagedRowDto>> BuildRequestLineDraftRowsByDraftKey(
            DispatchWorkbenchSaveRequest request,
            string fallbackLineKey)
        {
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey =
                new Dictionary<string, List<DispatchWorkbenchStagedRowDto>>(StringComparer.Ordinal);
            string fallbackKey = WorkbenchDraftStore.GetKey(fallbackLineKey);

            if (request?.lineDraftRowsByLineId != null && request.lineDraftRowsByLineId.Length > 0)
            {
                foreach (DispatchWorkbenchLineDraftRowsDto block in request.lineDraftRowsByLineId)
                {
                    string targetKey = WorkbenchDraftStore.GetKey(block?.lineId);
                    if (string.IsNullOrEmpty(targetKey))
                    {
                        targetKey = fallbackKey;
                    }

                    rowsByDraftKey[targetKey] = (block?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>())
                        .Select(m_CloneStagedRow)
                        .ToList();
                }

                return rowsByDraftKey;
            }

            DispatchWorkbenchStagedRowDto[] rows = GetRequestLineDraftRows(request);
            if (rows.Length == 0)
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
                return rowsByDraftKey;
            }

            foreach (IGrouping<string, DispatchWorkbenchStagedRowDto> group in rows
                .Where(row => row != null)
                .GroupBy(row => WorkbenchDraftStore.GetKey(string.IsNullOrEmpty(row.lineId) ? fallbackKey : row.lineId), StringComparer.Ordinal))
            {
                rowsByDraftKey[group.Key] = group.Select(m_CloneStagedRow).ToList();
            }

            if (!rowsByDraftKey.ContainsKey(fallbackKey))
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
            }

            return rowsByDraftKey;
        }

        private Dictionary<string, DispatchWorkbenchPlannerImportContractDto> BuildRequestPlanRefsByDraftKey(
            DispatchWorkbenchSaveRequest request,
            IEnumerable<string> fallbackDraftKeys)
        {
            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> refsByDraftKey =
                new Dictionary<string, DispatchWorkbenchPlannerImportContractDto>(StringComparer.Ordinal);

            if (request?.planRefs != null && request.planRefs.Length > 0)
            {
                foreach (DispatchWorkbenchPlanRefDto entry in request.planRefs)
                {
                    DispatchWorkbenchPlannerImportContractDto contract =
                        m_ClonePlannerImportContract(entry?.contract);
                    string targetKey = WorkbenchDraftStore.GetKey(
                        !string.IsNullOrEmpty(entry?.lineId)
                            ? entry.lineId
                            : contract?.draftKey);
                    if (contract == null || string.Equals(targetKey, "__default__", StringComparison.Ordinal))
                        continue;

                    contract.draftKey = targetKey;
                    refsByDraftKey[targetKey] = contract;
                }

                return refsByDraftKey;
            }

            DispatchWorkbenchPlannerImportContractDto fallbackContract =
                m_ClonePlannerImportContract(request?.plannerImportContract);
            if (fallbackContract == null)
                return refsByDraftKey;

            IEnumerable<string> targetKeys = (fallbackContract.importedLineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Select(WorkbenchDraftStore.GetKey);
            if (!targetKeys.Any())
            {
                targetKeys = (fallbackDraftKeys ?? Array.Empty<string>())
                    .Where(key => !string.IsNullOrEmpty(key))
                    .Select(WorkbenchDraftStore.GetKey);
            }

            foreach (string targetKey in targetKeys
                .Where(key => !string.IsNullOrEmpty(key) && !string.Equals(key, "__default__", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract =
                    m_ClonePlannerImportContract(fallbackContract);
                if (contract == null)
                    continue;

                contract.draftKey = targetKey;
                refsByDraftKey[targetKey] = contract;
            }

            return refsByDraftKey;
        }

        private DispatchWorkbenchPlannerImportContractDto ResolvePlanRef(
            string draftKey,
            DispatchWorkbenchPlannerImportContractDto currentRef,
            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> requestRefsByDraftKey,
            bool draftChanged,
            bool hasRows)
        {
            if (requestRefsByDraftKey != null
                && requestRefsByDraftKey.TryGetValue(draftKey, out DispatchWorkbenchPlannerImportContractDto requestedRef))
            {
                DispatchWorkbenchPlannerImportContractDto nextRef = m_ClonePlannerImportContract(requestedRef);
                if (nextRef != null)
                {
                    nextRef.draftKey = draftKey;
                }
                return nextRef;
            }

            if (!hasRows)
                return null;

            return m_ClonePlannerImportContract(currentRef);
        }

        private static HashSet<string> CollectTouchedWorkbenchLineIds(
            string selectedLineId,
            string selectedEditLine,
            List<DispatchWorkbenchManualRowDto> manualRows,
            List<DispatchWorkbenchAutoRuleDto> autoRules,
            List<DispatchWorkbenchStagedRowDto> stagedRows)
        {
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrEmpty(selectedLineId))
                lineIds.Add(selectedLineId);
            if (!string.IsNullOrEmpty(selectedEditLine))
                lineIds.Add(selectedEditLine);

            if (manualRows != null)
            {
                foreach (DispatchWorkbenchManualRowDto row in manualRows)
                {
                    if (!string.IsNullOrEmpty(row?.lineId))
                        lineIds.Add(row.lineId);
                }
            }

            if (autoRules != null)
            {
                foreach (DispatchWorkbenchAutoRuleDto rule in autoRules)
                {
                    if (!string.IsNullOrEmpty(rule?.lineId))
                        lineIds.Add(rule.lineId);
                }
            }

            if (stagedRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in stagedRows)
                {
                    if (!string.IsNullOrEmpty(row?.lineId))
                        lineIds.Add(row.lineId);
                }
            }

            return lineIds;
        }

        private static void NormalizeRequestedMergedViewFromLineSettings(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            Dictionary<string, string> configuredKinds = null)
        {
            if (request?.mergedView == null)
                return;

            WorkbenchLineRuntime fallbackLine = runtimeLines?.FirstOrDefault();
            NormalizeMergedViewLineKinds(
                request.mergedView,
                runtimeLines,
                request.lineSettings,
                fallbackLine,
                configuredKinds);
        }

        private static void NormalizeMergedViewLineKinds(
            DispatchWorkbenchMergedView mergedView,
            List<WorkbenchLineRuntime> lines,
            IEnumerable<DispatchWorkbenchLineSettingDto> requestedSettings,
            WorkbenchLineRuntime fallbackLine,
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

                    string normalizedKind = NormalizeWorkbenchServiceKind(setting.serviceKind);
                    if (!string.IsNullOrEmpty(normalizedKind))
                    {
                        requestedKindById[setting.lineId] = normalizedKind;
                    }
                }
            }

            List<string> mergedLineIds = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in NormalizeLineIdList(mergedView.localLineIds, mergedView.localLineId, lines))
            {
                if (seen.Add(lineId))
                {
                    mergedLineIds.Add(lineId);
                }
            }
            foreach (string lineId in NormalizeLineIdList(mergedView.expressLineIds, mergedView.expressLineId, lines))
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
                string kind = ResolveMergedViewLineKind(
                    lineId,
                    lineById,
                    requestedKindById,
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

        private static string ResolveMergedViewLineKind(
            string lineId,
            Dictionary<string, WorkbenchLineRuntime> lineById,
            Dictionary<string, string> requestedKindById,
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
                ? NormalizeWorkbenchServiceKind(capturedKind)
                : string.Empty;
            return string.IsNullOrEmpty(configuredKind) ? "local" : configuredKind;
        }

        private static List<string> NormalizeLineIdList(string[] ids, string fallbackId, List<WorkbenchLineRuntime> lines)
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
                    if (lines != null && !lines.Any(line => line.Id == id))
                        continue;
                    seen.Add(id);
                    normalized.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(fallbackId) && !seen.Contains(fallbackId))
            {
                if (lines == null || lines.Any(line => line.Id == fallbackId))
                {
                    seen.Add(fallbackId);
                    normalized.Add(fallbackId);
                }
            }

            return normalized;
        }

        private static string NormalizeWorkbenchServiceKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
                return string.Empty;

            return string.Equals(kind, "express", StringComparison.Ordinal)
                ? "express"
                : "local";
        }
    }
}
