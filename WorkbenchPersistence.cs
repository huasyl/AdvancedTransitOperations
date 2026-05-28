using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class WorkbenchPersistence
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<Entity> m_GetCity;
        private readonly WorkbenchDraftStore m_Drafts;
        private readonly Dictionary<string, int> m_OriginHoldLimits;
        private readonly Dictionary<string, int> m_MaxStationDwellMinutes;
        private readonly Dictionary<string, string> m_AllowedDepots;
        private readonly Dictionary<string, string> m_ServiceKinds;
        private readonly Dictionary<string, AppliedWorkbenchLineState> m_AppliedLines;
        private readonly Action m_ClearLineSettingsStore;
        private readonly Action m_InvalidateConfiguredAllowedDepotCache;
        private readonly Action<RuntimeFeatureSettingsDto> m_ApplyRuntimeFeatureSettings;
        private readonly Func<RuntimeFeatureSettingsDto> m_BuildRuntimeFeatureSettings;
        private readonly Action<IEnumerable<DispatchWorkbenchLineSettingDto>> m_ApplyWorkbenchLineSettings;
        private readonly Action<DispatchWorkbenchPersistentState> m_FillBroadcastPersistenceState;
        private readonly Action<DispatchWorkbenchPersistentState> m_RestoreBroadcastPersistenceState;
        private readonly Func<IEnumerable<string>> m_EnumerateLineSettingIds;
        private readonly Func<string, int> m_GetOriginHoldLimitMinutes;
        private readonly Func<string, int> m_GetMaxStationDwellMinutes;
        private readonly Func<string, string> m_GetAllowedDepotId;
        private readonly Func<string, string> m_GetConfiguredServiceKind;
        private readonly Func<HashSet<string>> m_BuildRuntimeLineIdsForRestore;
        private readonly Action<DispatchWorkbenchMergedView> m_ApplyCurrentTimeWindowDefaults;
        private readonly Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> m_CloneManualRow;
        private readonly Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> m_CloneAutoRule;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CloneStagedRow;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_ClonePlannerImportContract;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_DeduplicateRowsByIdLast;
        private readonly Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>> m_DeduplicateManualRowsForMigration;
        private readonly Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>> m_DeduplicateAutoRulesForMigration;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_DeduplicateStagedRowsForMigration;
        private readonly Func<string, int> m_ParseTimeMinutes;
        private readonly Func<string> m_SummarizeWorkbenchDraftRows;
        private readonly Action<string, Exception> m_LogException;
        private bool m_WorkbenchPersistenceLoaded;

        public WorkbenchPersistence(
            EntityManager entityManager,
            Func<Entity> getCity,
            WorkbenchDraftStore drafts,
            Dictionary<string, int> originHoldLimits,
            Dictionary<string, int> maxStationDwellMinutes,
            Dictionary<string, string> allowedDepots,
            Dictionary<string, string> serviceKinds,
            Dictionary<string, AppliedWorkbenchLineState> appliedLines,
            Action clearLineSettingsStore,
            Action invalidateConfiguredAllowedDepotCache,
            Action<RuntimeFeatureSettingsDto> applyRuntimeFeatureSettings,
            Func<RuntimeFeatureSettingsDto> buildRuntimeFeatureSettings,
            Action<IEnumerable<DispatchWorkbenchLineSettingDto>> applyWorkbenchLineSettings,
            Action<DispatchWorkbenchPersistentState> fillBroadcastPersistenceState,
            Action<DispatchWorkbenchPersistentState> restoreBroadcastPersistenceState,
            Func<IEnumerable<string>> enumerateLineSettingIds,
            Func<string, int> getOriginHoldLimitMinutes,
            Func<string, int> getMaxStationDwellMinutes,
            Func<string, string> getAllowedDepotId,
            Func<string, string> getConfiguredServiceKind,
            Func<HashSet<string>> buildRuntimeLineIdsForRestore,
            Action<DispatchWorkbenchMergedView> applyCurrentTimeWindowDefaults,
            Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> cloneManualRow,
            Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> cloneAutoRule,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> clonePlannerImportContract,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> deduplicateRowsByIdLast,
            Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>> deduplicateManualRowsForMigration,
            Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>> deduplicateAutoRulesForMigration,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> deduplicateStagedRowsForMigration,
            Func<string, int> parseTimeMinutes,
            Func<string> summarizeWorkbenchDraftRows,
            Action<string, Exception> logException)
        {
            m_EntityManager = entityManager;
            m_GetCity = getCity ?? throw new ArgumentNullException(nameof(getCity));
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_OriginHoldLimits = originHoldLimits ?? throw new ArgumentNullException(nameof(originHoldLimits));
            m_MaxStationDwellMinutes = maxStationDwellMinutes ?? throw new ArgumentNullException(nameof(maxStationDwellMinutes));
            m_AllowedDepots = allowedDepots ?? throw new ArgumentNullException(nameof(allowedDepots));
            m_ServiceKinds = serviceKinds ?? throw new ArgumentNullException(nameof(serviceKinds));
            m_AppliedLines = appliedLines ?? throw new ArgumentNullException(nameof(appliedLines));
            m_ClearLineSettingsStore = clearLineSettingsStore ?? throw new ArgumentNullException(nameof(clearLineSettingsStore));
            m_InvalidateConfiguredAllowedDepotCache = invalidateConfiguredAllowedDepotCache ?? throw new ArgumentNullException(nameof(invalidateConfiguredAllowedDepotCache));
            m_ApplyRuntimeFeatureSettings = applyRuntimeFeatureSettings ?? throw new ArgumentNullException(nameof(applyRuntimeFeatureSettings));
            m_BuildRuntimeFeatureSettings = buildRuntimeFeatureSettings ?? throw new ArgumentNullException(nameof(buildRuntimeFeatureSettings));
            m_ApplyWorkbenchLineSettings = applyWorkbenchLineSettings ?? throw new ArgumentNullException(nameof(applyWorkbenchLineSettings));
            m_FillBroadcastPersistenceState = fillBroadcastPersistenceState ?? throw new ArgumentNullException(nameof(fillBroadcastPersistenceState));
            m_RestoreBroadcastPersistenceState = restoreBroadcastPersistenceState ?? throw new ArgumentNullException(nameof(restoreBroadcastPersistenceState));
            m_EnumerateLineSettingIds = enumerateLineSettingIds ?? throw new ArgumentNullException(nameof(enumerateLineSettingIds));
            m_GetOriginHoldLimitMinutes = getOriginHoldLimitMinutes ?? throw new ArgumentNullException(nameof(getOriginHoldLimitMinutes));
            m_GetMaxStationDwellMinutes = getMaxStationDwellMinutes ?? throw new ArgumentNullException(nameof(getMaxStationDwellMinutes));
            m_GetAllowedDepotId = getAllowedDepotId ?? throw new ArgumentNullException(nameof(getAllowedDepotId));
            m_GetConfiguredServiceKind = getConfiguredServiceKind ?? throw new ArgumentNullException(nameof(getConfiguredServiceKind));
            m_BuildRuntimeLineIdsForRestore = buildRuntimeLineIdsForRestore ?? throw new ArgumentNullException(nameof(buildRuntimeLineIdsForRestore));
            m_ApplyCurrentTimeWindowDefaults = applyCurrentTimeWindowDefaults ?? throw new ArgumentNullException(nameof(applyCurrentTimeWindowDefaults));
            m_CloneManualRow = cloneManualRow ?? throw new ArgumentNullException(nameof(cloneManualRow));
            m_CloneAutoRule = cloneAutoRule ?? throw new ArgumentNullException(nameof(cloneAutoRule));
            m_CloneStagedRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_ClonePlannerImportContract = clonePlannerImportContract ?? throw new ArgumentNullException(nameof(clonePlannerImportContract));
            m_DeduplicateRowsByIdLast = deduplicateRowsByIdLast ?? throw new ArgumentNullException(nameof(deduplicateRowsByIdLast));
            m_DeduplicateManualRowsForMigration = deduplicateManualRowsForMigration ?? throw new ArgumentNullException(nameof(deduplicateManualRowsForMigration));
            m_DeduplicateAutoRulesForMigration = deduplicateAutoRulesForMigration ?? throw new ArgumentNullException(nameof(deduplicateAutoRulesForMigration));
            m_DeduplicateStagedRowsForMigration = deduplicateStagedRowsForMigration ?? throw new ArgumentNullException(nameof(deduplicateStagedRowsForMigration));
            m_ParseTimeMinutes = parseTimeMinutes ?? throw new ArgumentNullException(nameof(parseTimeMinutes));
            m_SummarizeWorkbenchDraftRows = summarizeWorkbenchDraftRows ?? throw new ArgumentNullException(nameof(summarizeWorkbenchDraftRows));
            m_LogException = logException ?? throw new ArgumentNullException(nameof(logException));
        }

        public void ResetForLoad()
        {
            m_WorkbenchPersistenceLoaded = false;
        }

        public void EnsureLoaded()
        {
            if (!m_WorkbenchPersistenceLoaded)
            {
                TryRestoreWorkbenchPersistence();
            }
        }

        public bool TryRestoreWorkbenchPersistence()
        {
            if (m_WorkbenchPersistenceLoaded)
                return true;

            Entity city = m_GetCity();
            if (city == Entity.Null || !m_EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
                return false;

            var buffer = m_EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city, true);
            if (buffer.Length == 0)
            {
                m_WorkbenchPersistenceLoaded = true;
                return true;
            }

            try
            {
                DispatchWorkbenchPersistentState persisted = ReadWorkbenchPersistenceState(buffer);
                if (persisted != null)
                {
                    bool persistenceCleaned = RestoreWorkbenchPersistence(persisted);
                    if (persistenceCleaned)
                    {
                        SaveWorkbenchPersistence();
                    }
                    Mod.log.Info("[WorkbenchRestore] drafts=" + m_Drafts.Count
                        + " " + m_SummarizeWorkbenchDraftRows());
                }

                m_WorkbenchPersistenceLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                m_LogException("TryRestoreWorkbenchPersistence", ex);
                m_WorkbenchPersistenceLoaded = true;
                return false;
            }
        }

        public void SaveWorkbenchPersistence()
        {
            EnsureWorkbenchPersistenceBuffer();

            Entity city = m_GetCity();
            if (city == Entity.Null || !m_EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
                return;

            string payloadJson = DispatchWorkbenchJson.Serialize(BuildWorkbenchPersistenceState());
            List<string> payloadChunks = SplitWorkbenchPersistencePayload(payloadJson);
            var buffer = m_EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city);
            buffer.Clear();
            WriteWorkbenchPersistenceBuffer(buffer, payloadChunks);
        }

        public WorkbenchSavePersistencePayload CaptureSavePayload()
        {
            return new WorkbenchSavePersistencePayload
            {
                WorkbenchState = BuildWorkbenchPersistenceState(),
                AppliedLineElements = BuildAppliedWorkbenchLinePersistenceElements(m_AppliedLines),
                AppliedRowElements = BuildAppliedWorkbenchRowPersistenceElements(m_AppliedLines)
            };
        }

        public DispatchWorkbenchPersistentState BuildState()
        {
            return BuildWorkbenchPersistenceState();
        }

        public bool RestoreState(DispatchWorkbenchPersistentState persisted)
        {
            return RestoreWorkbenchPersistence(persisted);
        }

        public PreparedWorkbenchSavePersistence PrepareSavePersistence(WorkbenchSavePersistencePayload payload)
        {
            return new PreparedWorkbenchSavePersistence
            {
                WorkbenchPersistenceChunks = SplitWorkbenchPersistencePayload(
                    DispatchWorkbenchJson.Serialize(payload?.WorkbenchState)),
                AppliedLineElements = payload?.AppliedLineElements ?? new List<AppliedWorkbenchLineStateElement>(),
                AppliedRowElements = payload?.AppliedRowElements ?? new List<AppliedWorkbenchStagedRowElement>()
            };
        }

        public void CommitPreparedSavePersistence(PreparedWorkbenchSavePersistence prepared)
        {
            WritePreparedWorkbenchPersistence(prepared?.WorkbenchPersistenceChunks);
            WritePreparedAppliedWorkbenchPersistence(
                prepared?.AppliedLineElements,
                prepared?.AppliedRowElements);
        }

        private DispatchWorkbenchPersistentState BuildWorkbenchPersistenceState()
        {
            List<DispatchWorkbenchPersistedDraftState> drafts =
                new List<DispatchWorkbenchPersistedDraftState>(m_Drafts.Count);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                drafts.Add(CreatePersistedDraftState(entry.Key, entry.Value));
            }

            DispatchWorkbenchLineSettingDto[] lineSettings = m_EnumerateLineSettingIds()
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(lineId => new DispatchWorkbenchLineSettingDto
                {
                    lineId = lineId,
                    originHoldLimitMinutes = m_GetOriginHoldLimitMinutes(lineId),
                    maxStationDwellMinutes = m_GetMaxStationDwellMinutes(lineId),
                    allowedDepotId = m_GetAllowedDepotId(lineId),
                    serviceKind = m_GetConfiguredServiceKind(lineId)
                })
                .ToArray();

            DispatchWorkbenchPersistentState state = new DispatchWorkbenchPersistentState
            {
                preferredLineId = m_Drafts.GetPreferredLineId(),
                lineSettings = lineSettings,
                drafts = drafts.ToArray(),
                featureSettings = m_BuildRuntimeFeatureSettings()
            };
            m_FillBroadcastPersistenceState(state);
            return state;
        }

        private bool RestoreWorkbenchPersistence(DispatchWorkbenchPersistentState persisted)
        {
            m_Drafts.Clear();
            m_OriginHoldLimits.Clear();
            m_MaxStationDwellMinutes.Clear();
            m_AllowedDepots.Clear();
            m_ServiceKinds.Clear();
            m_ClearLineSettingsStore();
            m_InvalidateConfiguredAllowedDepotCache();
            m_ApplyRuntimeFeatureSettings(persisted?.featureSettings);
            m_Drafts.SetPreferredLineId(persisted?.preferredLineId ?? string.Empty);
            m_RestoreBroadcastPersistenceState(persisted);

            if (persisted?.lineSettings != null)
            {
                m_ApplyWorkbenchLineSettings(persisted.lineSettings);
            }

            if (persisted?.drafts == null)
                return persisted?.featureSettings == null;

            for (int i = 0; i < persisted.drafts.Length; i++)
            {
                DispatchWorkbenchPersistedDraftState dto = persisted.drafts[i];
                if (dto == null)
                    continue;

                string lineKey = !string.IsNullOrEmpty(dto.lineKey)
                    ? dto.lineKey
                    : WorkbenchDraftStore.GetKey(dto.selectedLineId);
                m_Drafts[lineKey] = CreateDraftStateFromPersisted(lineKey, dto);
            }

            bool migrated = MigrateRestoredWorkbenchDraftRowsByLineId();
            return persisted?.featureSettings == null || migrated;
        }

        private bool MigrateRestoredWorkbenchDraftRowsByLineId()
        {
            if (m_Drafts.Count == 0)
                return false;

            List<KeyValuePair<string, DispatchWorkbenchDraftState>> restoredDrafts = m_Drafts.ToList();
            HashSet<string> validRuntimeLineIds = m_BuildRuntimeLineIdsForRestore();
            HashSet<string> touchedDraftKeys = new HashSet<string>(StringComparer.Ordinal);
            int relocatedRows = 0;
            int droppedRows = 0;
            int createdDrafts = 0;

            for (int draftIndex = 0; draftIndex < restoredDrafts.Count; draftIndex++)
            {
                string sourceKey = restoredDrafts[draftIndex].Key;
                DispatchWorkbenchDraftState sourceDraft = restoredDrafts[draftIndex].Value;
                if (sourceDraft == null)
                    continue;

                relocatedRows += MigrateRestoredManualRowsByLineId(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
                relocatedRows += MigrateRestoredAutoRulesByLineId(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
                relocatedRows += MigrateRestoredStagedRowsByLineId(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
            }

            if (relocatedRows == 0 && droppedRows == 0)
                return false;

            foreach (string draftKey in touchedDraftKeys)
            {
                if (!m_Drafts.TryGetValue(draftKey, out DispatchWorkbenchDraftState draft)
                    || draft == null)
                {
                    continue;
                }

                draft.ManualRows = m_DeduplicateManualRowsForMigration(draft.ManualRows);
                draft.AutoRules = m_DeduplicateAutoRulesForMigration(draft.AutoRules);
                draft.StagedRows = m_DeduplicateStagedRowsForMigration(draft.StagedRows);
                NormalizeMigratedWorkbenchDraftState(draftKey, draft);
            }

            Mod.log.Info("[WorkbenchDraftMigration] relocatedRows="
                + relocatedRows
                + " droppedRows="
                + droppedRows
                + " touchedDrafts="
                + touchedDraftKeys.Count
                + " createdDrafts="
                + createdDrafts);
            return true;
        }

        private int MigrateRestoredManualRowsByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.ManualRows == null || sourceDraft.ManualRows.Count == 0)
                return 0;

            List<DispatchWorkbenchManualRowDto> retainedRows =
                new List<DispatchWorkbenchManualRowDto>(sourceDraft.ManualRows.Count);
            int movedRows = 0;
            for (int i = 0; i < sourceDraft.ManualRows.Count; i++)
            {
                DispatchWorkbenchManualRowDto row = sourceDraft.ManualRows[i];
                RestoredWorkbenchRowRepairAction action = ClassifyRestoredWorkbenchRow(
                    sourceKey,
                    row?.lineId,
                    validRuntimeLineIds,
                    out string targetKey);
                if (action == RestoredWorkbenchRowRepairAction.Keep)
                {
                    retainedRows.Add(row);
                    continue;
                }
                if (action == RestoredWorkbenchRowRepairAction.Drop)
                {
                    touchedDraftKeys.Add(sourceKey);
                    droppedRows++;
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.ManualRows.Add(m_CloneManualRow(row));
                if (sourceDraft.RulesApplied || sourceDraft.DraftApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRows++;
            }

            sourceDraft.ManualRows = retainedRows;
            return movedRows;
        }

        private int MigrateRestoredAutoRulesByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.AutoRules == null || sourceDraft.AutoRules.Count == 0)
                return 0;

            List<DispatchWorkbenchAutoRuleDto> retainedRules =
                new List<DispatchWorkbenchAutoRuleDto>(sourceDraft.AutoRules.Count);
            int movedRules = 0;
            for (int i = 0; i < sourceDraft.AutoRules.Count; i++)
            {
                DispatchWorkbenchAutoRuleDto rule = sourceDraft.AutoRules[i];
                RestoredWorkbenchRowRepairAction action = ClassifyRestoredWorkbenchRow(
                    sourceKey,
                    rule?.lineId,
                    validRuntimeLineIds,
                    out string targetKey);
                if (action == RestoredWorkbenchRowRepairAction.Keep)
                {
                    retainedRules.Add(rule);
                    continue;
                }
                if (action == RestoredWorkbenchRowRepairAction.Drop)
                {
                    touchedDraftKeys.Add(sourceKey);
                    droppedRows++;
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.AutoRules.Add(m_CloneAutoRule(rule));
                if (sourceDraft.RulesApplied || sourceDraft.DraftApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRules++;
            }

            sourceDraft.AutoRules = retainedRules;
            return movedRules;
        }

        private int MigrateRestoredStagedRowsByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.StagedRows == null || sourceDraft.StagedRows.Count == 0)
                return 0;

            List<DispatchWorkbenchStagedRowDto> retainedRows =
                new List<DispatchWorkbenchStagedRowDto>(sourceDraft.StagedRows.Count);
            int movedRows = 0;
            for (int i = 0; i < sourceDraft.StagedRows.Count; i++)
            {
                DispatchWorkbenchStagedRowDto row = sourceDraft.StagedRows[i];
                RestoredWorkbenchRowRepairAction action = ClassifyRestoredWorkbenchRow(
                    sourceKey,
                    row?.lineId,
                    validRuntimeLineIds,
                    out string targetKey);
                if (action == RestoredWorkbenchRowRepairAction.Keep)
                {
                    retainedRows.Add(row);
                    continue;
                }
                if (action == RestoredWorkbenchRowRepairAction.Drop)
                {
                    touchedDraftKeys.Add(sourceKey);
                    droppedRows++;
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.StagedRows.Add(m_CloneStagedRow(row));
                if (sourceDraft.DraftApplied)
                {
                    targetDraft.DraftApplied = true;
                    targetDraft.RulesApplied = true;
                }
                else if (sourceDraft.RulesApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRows++;
            }

            sourceDraft.StagedRows = retainedRows;
            return movedRows;
        }

        private RestoredWorkbenchRowRepairAction ClassifyRestoredWorkbenchRow(
            string sourceKey,
            string rowLineId,
            HashSet<string> validRuntimeLineIds,
            out string targetKey)
        {
            targetKey = string.Empty;
            if (string.IsNullOrEmpty(rowLineId)
                || string.Equals(rowLineId, "local", StringComparison.Ordinal)
                || string.Equals(rowLineId, "express", StringComparison.Ordinal))
            {
                return RestoredWorkbenchRowRepairAction.Drop;
            }

            if (validRuntimeLineIds != null
                && validRuntimeLineIds.Count > 0
                && !validRuntimeLineIds.Contains(rowLineId))
            {
                return RestoredWorkbenchRowRepairAction.Drop;
            }

            targetKey = WorkbenchDraftStore.GetKey(rowLineId);
            return !string.Equals(targetKey, sourceKey ?? string.Empty, StringComparison.Ordinal)
                ? RestoredWorkbenchRowRepairAction.Move
                : RestoredWorkbenchRowRepairAction.Keep;
        }

        private DispatchWorkbenchDraftState GetOrCreateRestoredWorkbenchMigrationDraft(
            string lineKey,
            ref int createdDrafts)
        {
            if (!m_Drafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft)
                || draft == null)
            {
                draft = CreateEmptyWorkbenchDraftState(lineKey);
                m_Drafts[lineKey] = draft;
                createdDrafts++;
            }

            return draft;
        }

        private void CopyPlannerImportContractForMigratedDraft(
            DispatchWorkbenchDraftState sourceDraft,
            DispatchWorkbenchDraftState targetDraft,
            string targetKey)
        {
            if (sourceDraft?.PlannerImportContract == null
                || targetDraft == null
                || targetDraft.PlannerImportContract != null)
            {
                return;
            }

            DispatchWorkbenchPlannerImportContractDto contract =
                m_ClonePlannerImportContract(sourceDraft.PlannerImportContract);
            if (contract == null)
                return;

            contract.draftKey = targetKey;
            targetDraft.PlannerImportContract = contract;
        }

        private void NormalizeMigratedWorkbenchDraftState(
            string draftKey,
            DispatchWorkbenchDraftState draft)
        {
            if (draft == null)
                return;

            if (string.IsNullOrEmpty(draft.SelectedLineId))
            {
                draft.SelectedLineId = draftKey;
            }
            if (string.IsNullOrEmpty(draft.SelectedEditLine)
                || string.Equals(draft.SelectedEditLine, "local", StringComparison.Ordinal)
                || string.Equals(draft.SelectedEditLine, "express", StringComparison.Ordinal))
            {
                draft.SelectedEditLine = draftKey == "__default__" ? string.Empty : draftKey;
            }

            if (draft.DraftApplied && !HasWorkbenchRowsForDraft(draft.StagedRows, draftKey))
            {
                draft.DraftApplied = false;
            }
            if (!HasWorkbenchRowsForDraft(draft.StagedRows, draftKey))
            {
                draft.PlannerImportContract = null;
            }
            if (draft.RulesApplied
                && !HasWorkbenchRowsForDraft(draft.ManualRows, draftKey)
                && !HasWorkbenchRulesForDraft(draft.AutoRules, draftKey)
                && !HasWorkbenchRowsForDraft(draft.StagedRows, draftKey))
            {
                draft.RulesApplied = false;
            }

            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView
                {
                    localLineIds = Array.Empty<string>(),
                    expressLineIds = Array.Empty<string>(),
                    isLoop = true,
                    turnbackStationId = string.Empty,
                    direction = "up"
                };
            }

            m_ApplyCurrentTimeWindowDefaults(draft.MergedView);
            draft.AppliedDepartureMinutesCache.Clear();
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchManualRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(WorkbenchDraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRulesForDraft(
            List<DispatchWorkbenchAutoRuleDto> rules,
            string draftKey)
        {
            return rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(WorkbenchDraftStore.GetKey(rule.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchStagedRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(WorkbenchDraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private DispatchWorkbenchPersistedDraftState CreatePersistedDraftState(
            string lineKey,
            DispatchWorkbenchDraftState draft)
        {
            return new DispatchWorkbenchPersistedDraftState
            {
                lineKey = lineKey,
                selectedLineId = draft?.SelectedLineId ?? string.Empty,
                selectedEditLine = draft?.SelectedEditLine ?? string.Empty,
                mergedView = CloneMergedViewForPersistence(draft?.MergedView),
                manualRows = draft?.ManualRows?.Select(m_CloneManualRow).ToArray() ?? Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = draft?.AutoRules?.Select(m_CloneAutoRule).ToArray() ?? Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                lineDraftRows = draft?.StagedRows?.Select(m_CloneStagedRow).ToArray() ?? Array.Empty<DispatchWorkbenchStagedRowDto>(),
                rulesApplied = draft?.RulesApplied == true,
                draftApplied = draft?.DraftApplied == true,
                plannerImportContract = m_ClonePlannerImportContract(draft?.PlannerImportContract)
            };
        }

        private DispatchWorkbenchDraftState CreateDraftStateFromPersisted(
            string lineKey,
            DispatchWorkbenchPersistedDraftState dto)
        {
            DispatchWorkbenchDraftState draft = new DispatchWorkbenchDraftState
            {
                SelectedLineId = !string.IsNullOrEmpty(dto.selectedLineId) ? dto.selectedLineId : lineKey,
                SelectedEditLine = !string.IsNullOrEmpty(dto.selectedEditLine)
                    ? dto.selectedEditLine
                    : (lineKey == "__default__" ? string.Empty : lineKey),
                MergedView = CloneMergedView(dto.mergedView),
                ManualRows = dto.manualRows?.Select(m_CloneManualRow).ToList() ?? new List<DispatchWorkbenchManualRowDto>(),
                AutoRules = dto.autoRules?.Select(m_CloneAutoRule).ToList() ?? new List<DispatchWorkbenchAutoRuleDto>(),
                StagedRows = m_DeduplicateRowsByIdLast(
                    dto.lineDraftRows?.Select(m_CloneStagedRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>()),
                RulesApplied = dto.rulesApplied,
                DraftApplied = dto.draftApplied,
                PlannerImportContract = m_ClonePlannerImportContract(dto.plannerImportContract)
            };

            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            return draft;
        }

        private DispatchWorkbenchDraftState CreateEmptyWorkbenchDraftState(string lineKey)
        {
            DispatchWorkbenchMergedView mergedView = new DispatchWorkbenchMergedView
            {
                localLineIds = Array.Empty<string>(),
                expressLineIds = Array.Empty<string>(),
                isLoop = true,
                turnbackStationId = string.Empty,
                direction = "up"
            };
            m_ApplyCurrentTimeWindowDefaults(mergedView);

            return new DispatchWorkbenchDraftState
            {
                SelectedLineId = lineKey,
                SelectedEditLine = lineKey == "__default__" ? string.Empty : lineKey,
                MergedView = mergedView,
                DraftApplied = false
            };
        }

        private static DispatchWorkbenchMergedView CloneMergedView(DispatchWorkbenchMergedView view)
        {
            if (view == null)
            {
                return new DispatchWorkbenchMergedView
                {
                    localLineIds = Array.Empty<string>(),
                    expressLineIds = Array.Empty<string>(),
                    isLoop = true,
                    turnbackStationId = string.Empty,
                    direction = "up",
                    windowStart = string.Empty,
                    windowEnd = string.Empty
                };
            }

            return new DispatchWorkbenchMergedView
            {
                localLineId = view.localLineId ?? string.Empty,
                expressLineId = view.expressLineId ?? string.Empty,
                localLineIds = view.localLineIds != null ? view.localLineIds.ToArray() : Array.Empty<string>(),
                expressLineIds = view.expressLineIds != null ? view.expressLineIds.ToArray() : Array.Empty<string>(),
                isLoop = view.isLoop,
                turnbackStationId = view.turnbackStationId ?? string.Empty,
                direction = string.IsNullOrEmpty(view.direction) ? "up" : view.direction,
                windowStart = view.windowStart ?? string.Empty,
                windowEnd = view.windowEnd ?? string.Empty
            };
        }

        private static DispatchWorkbenchMergedView CloneMergedViewForPersistence(DispatchWorkbenchMergedView view)
        {
            DispatchWorkbenchMergedView mergedView = CloneMergedView(view);
            mergedView.windowStart = string.Empty;
            mergedView.windowEnd = string.Empty;
            return mergedView;
        }

        private static List<AppliedWorkbenchLineStateElement> BuildAppliedWorkbenchLinePersistenceElements(
            IReadOnlyDictionary<string, AppliedWorkbenchLineState> appliedLines)
        {
            List<AppliedWorkbenchLineStateElement> elements = new List<AppliedWorkbenchLineStateElement>();
            if (appliedLines == null)
                return elements;

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in appliedLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.LineEntity == Entity.Null || state.StagedRows == null || state.StagedRows.Count == 0)
                    continue;

                elements.Add(new AppliedWorkbenchLineStateElement
                {
                    m_LineEntity = state.LineEntity,
                    m_OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.NormalizeOriginHoldLimitMinutes(state.OriginHoldLimitMinutes)
                });
            }

            return elements;
        }

        private List<AppliedWorkbenchStagedRowElement> BuildAppliedWorkbenchRowPersistenceElements(
            IReadOnlyDictionary<string, AppliedWorkbenchLineState> appliedLines)
        {
            List<AppliedWorkbenchStagedRowElement> elements = new List<AppliedWorkbenchStagedRowElement>();
            if (appliedLines == null)
                return elements;

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in appliedLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.LineEntity == Entity.Null || state.StagedRows == null)
                    continue;

                for (int i = 0; i < state.StagedRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = state.StagedRows[i];
                    int minute = m_ParseTimeMinutes(row?.time);
                    if (minute < 0)
                        continue;

                    elements.Add(new AppliedWorkbenchStagedRowElement
                    {
                        m_LineEntity = state.LineEntity,
                        m_Order = i,
                        m_Minute = minute,
                        m_KindCode = EncodeAppliedRowKind(row?.kind),
                        m_SourceCode = EncodeAppliedRowSource(row?.source)
                    });
                }
            }

            return elements;
        }

        private void WritePreparedWorkbenchPersistence(List<string> payloadChunks)
        {
            EnsureWorkbenchPersistenceBuffer();

            Entity city = m_GetCity();
            if (city == Entity.Null || !m_EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
                return;

            var buffer = m_EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city);
            buffer.Clear();
            WriteWorkbenchPersistenceBuffer(buffer, payloadChunks);
        }

        private void WritePreparedAppliedWorkbenchPersistence(
            List<AppliedWorkbenchLineStateElement> lineElements,
            List<AppliedWorkbenchStagedRowElement> rowElements)
        {
            EnsureAppliedWorkbenchPersistenceBuffers();

            Entity city = m_GetCity();
            if (city == Entity.Null)
                return;

            var lineBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city);
            var rowBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city);
            lineBuffer.Clear();
            rowBuffer.Clear();

            if (lineElements != null)
            {
                for (int i = 0; i < lineElements.Count; i++)
                {
                    lineBuffer.Add(lineElements[i]);
                }
            }
            if (rowElements != null)
            {
                for (int i = 0; i < rowElements.Count; i++)
                {
                    rowBuffer.Add(rowElements[i]);
                }
            }
        }

        private void EnsureWorkbenchPersistenceBuffer()
        {
            Entity city = m_GetCity();
            if (city != Entity.Null && !m_EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                m_EntityManager.AddBuffer<WorkbenchTimetableStateElement>(city);
            }
        }

        private void EnsureAppliedWorkbenchPersistenceBuffers()
        {
            Entity city = m_GetCity();
            if (city == Entity.Null)
                return;

            if (!m_EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
            {
                m_EntityManager.AddBuffer<AppliedWorkbenchLineStateElement>(city);
            }
            if (!m_EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                m_EntityManager.AddBuffer<AppliedWorkbenchStagedRowElement>(city);
            }
        }

        private static void WriteWorkbenchPersistenceBuffer(
            DynamicBuffer<WorkbenchTimetableStateElement> buffer,
            List<string> payloadChunks)
        {
            if (buffer.Length > 0)
            {
                buffer.Clear();
            }
            if (payloadChunks == null || payloadChunks.Count == 0)
                return;

            for (int i = 0; i < payloadChunks.Count; i++)
            {
                buffer.Add(new WorkbenchTimetableStateElement
                {
                    m_ChunkIndex = i,
                    m_PayloadChunk = new FixedString4096Bytes(payloadChunks[i] ?? string.Empty)
                });
            }
        }

        private static DispatchWorkbenchPersistentState ReadWorkbenchPersistenceState(
            DynamicBuffer<WorkbenchTimetableStateElement> buffer)
        {
            WorkbenchTimetableStateElement[] orderedEntries = new WorkbenchTimetableStateElement[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                orderedEntries[i] = buffer[i];
            }

            Array.Sort(orderedEntries, (left, right) => left.m_ChunkIndex.CompareTo(right.m_ChunkIndex));
            StringBuilder payloadBuilder = new StringBuilder();
            for (int i = 0; i < orderedEntries.Length; i++)
            {
                payloadBuilder.Append(orderedEntries[i].m_PayloadChunk.ToString());
            }

            string payloadJson = payloadBuilder.ToString();
            if (string.IsNullOrEmpty(payloadJson))
                return null;

            return DispatchWorkbenchJson.Deserialize<DispatchWorkbenchPersistentState>(payloadJson);
        }

        private static List<string> SplitWorkbenchPersistencePayload(string payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
                return new List<string>();

            List<string> chunks = new List<string>();
            int offset = 0;
            while (offset < payloadJson.Length)
            {
                int chunkLength = FindWorkbenchPersistenceChunkLength(payloadJson, offset);
                if (chunkLength <= 0)
                {
                    chunkLength = 1;
                }

                chunks.Add(payloadJson.Substring(offset, chunkLength));
                offset += chunkLength;
            }

            return chunks;
        }

        private static int FindWorkbenchPersistenceChunkLength(string payloadJson, int offset)
        {
            int remaining = payloadJson.Length - offset;
            if (remaining <= 0)
                return 0;

            int low = 1;
            int high = remaining;
            int best = 1;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                int candidateLength = AdjustWorkbenchPersistenceChunkLength(payloadJson, offset, mid);
                if (candidateLength <= 0)
                {
                    high = mid - 1;
                    continue;
                }

                string candidate = payloadJson.Substring(offset, candidateLength);
                FixedString4096Bytes fixedCandidate = candidate;
                if (fixedCandidate.ToString() == candidate)
                {
                    best = candidateLength;
                    low = candidateLength + 1;
                }
                else
                {
                    high = candidateLength - 1;
                }
            }

            return best;
        }

        private static int AdjustWorkbenchPersistenceChunkLength(string payloadJson, int offset, int proposedLength)
        {
            int endIndex = Math.Min(payloadJson.Length, offset + proposedLength);
            if (endIndex <= offset)
                return 0;

            if (endIndex < payloadJson.Length
                && char.IsHighSurrogate(payloadJson[endIndex - 1])
                && char.IsLowSurrogate(payloadJson[endIndex]))
            {
                endIndex--;
            }

            return endIndex - offset;
        }

        private static byte EncodeAppliedRowKind(string kind)
        {
            return string.Equals(kind, "express", StringComparison.Ordinal) ? (byte)1 : (byte)0;
        }

        private static byte EncodeAppliedRowSource(string source)
        {
            if (string.Equals(source, "manual", StringComparison.Ordinal))
                return 1;
            if (string.Equals(source, "auto", StringComparison.Ordinal))
                return 2;
            if (string.Equals(source, "planner", StringComparison.Ordinal))
                return 3;
            return 0;
        }
    }

    internal enum RestoredWorkbenchRowRepairAction
    {
        Keep = 0,
        Move = 1,
        Drop = 2
    }
}
