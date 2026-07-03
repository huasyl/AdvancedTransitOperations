using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using WorkbenchBuffer = RapidTransitMod.Workbenches.Buffer;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Persist
    {
        private readonly Host m_Host;
        private readonly RunPort m_Run;
        private readonly DraftStore m_Drafts;
        private readonly AppliedTimetable m_Applied;
        private readonly Action<DispatchWorkbenchPersistentState> m_BuildCompatState;
        private readonly Action<DispatchWorkbenchPersistentState> m_RestoreCompatState;
        private readonly Func<IEnumerable<string>> m_EnumerateLineSettingIds;
        private readonly Func<string, int> m_GetOriginHoldLimitMinutes;
        private readonly Func<string, int> m_GetMaxStationDwellMinutes;
        private readonly Func<string, string> m_GetAllowedDepotId;
        private readonly Func<string, string> m_GetConfiguredServiceKind;
        private readonly Func<HashSet<string>> m_BuildRuntimeLineIdsForRestore;
        private readonly Action<DispatchWorkbenchMergedView> m_Window;
        private readonly Action<RuntimeFeatureSettingsDto> m_MigrateLegacyFeatureSettings;
        private readonly Func<string, DispatchWorkbenchDraftState> m_NewDraft;
        private readonly Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> m_CopyManual;
        private readonly Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> m_CopyRule;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_CopyPlan;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_LastById;
        private readonly Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>> m_KeepManual;
        private readonly Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>> m_KeepRules;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_KeepRows;
        private bool m_Loaded;

        internal Persist(
            Host host,
            DraftStore drafts,
            AppliedTimetable applied,
            Action<DispatchWorkbenchPersistentState> buildCompatState,
            Action<DispatchWorkbenchPersistentState> restoreCompatState,
            Func<IEnumerable<string>> enumerateLineSettingIds,
            Func<string, int> getOriginHoldLimitMinutes,
            Func<string, int> getMaxStationDwellMinutes,
            Func<string, string> getAllowedDepotId,
            Func<string, string> getConfiguredServiceKind,
            Func<HashSet<string>> buildRuntimeLineIdsForRestore,
            Action<DispatchWorkbenchMergedView> window,
            Action<RuntimeFeatureSettingsDto> migrateLegacyFeatureSettings,
            Func<string, DispatchWorkbenchDraftState> newDraft,
            Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> cloneManualRow,
            Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> cloneAutoRule,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> copyPlan,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> deduplicateRowsByIdLast,
            Func<List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchManualRowDto>> deduplicateManualRowsForMigration,
            Func<List<DispatchWorkbenchAutoRuleDto>, List<DispatchWorkbenchAutoRuleDto>> deduplicateAutoRulesForMigration,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> deduplicateStagedRowsForMigration)
        {
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
            m_Run = host.Run;
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_Applied = applied ?? throw new ArgumentNullException(nameof(applied));
            m_BuildCompatState = buildCompatState ?? throw new ArgumentNullException(nameof(buildCompatState));
            m_RestoreCompatState = restoreCompatState ?? throw new ArgumentNullException(nameof(restoreCompatState));
            m_EnumerateLineSettingIds = enumerateLineSettingIds ?? throw new ArgumentNullException(nameof(enumerateLineSettingIds));
            m_GetOriginHoldLimitMinutes = getOriginHoldLimitMinutes ?? throw new ArgumentNullException(nameof(getOriginHoldLimitMinutes));
            m_GetMaxStationDwellMinutes = getMaxStationDwellMinutes ?? throw new ArgumentNullException(nameof(getMaxStationDwellMinutes));
            m_GetAllowedDepotId = getAllowedDepotId ?? throw new ArgumentNullException(nameof(getAllowedDepotId));
            m_GetConfiguredServiceKind = getConfiguredServiceKind ?? throw new ArgumentNullException(nameof(getConfiguredServiceKind));
            m_BuildRuntimeLineIdsForRestore = buildRuntimeLineIdsForRestore ?? throw new ArgumentNullException(nameof(buildRuntimeLineIdsForRestore));
            m_Window = window ?? throw new ArgumentNullException(nameof(window));
            m_MigrateLegacyFeatureSettings = migrateLegacyFeatureSettings ?? throw new ArgumentNullException(nameof(migrateLegacyFeatureSettings));
            m_NewDraft = newDraft ?? throw new ArgumentNullException(nameof(newDraft));
            m_CopyManual = cloneManualRow ?? throw new ArgumentNullException(nameof(cloneManualRow));
            m_CopyRule = cloneAutoRule ?? throw new ArgumentNullException(nameof(cloneAutoRule));
            m_CopyRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_CopyPlan = copyPlan ?? throw new ArgumentNullException(nameof(copyPlan));
            m_LastById = deduplicateRowsByIdLast ?? throw new ArgumentNullException(nameof(deduplicateRowsByIdLast));
            m_KeepManual = deduplicateManualRowsForMigration ?? throw new ArgumentNullException(nameof(deduplicateManualRowsForMigration));
            m_KeepRules = deduplicateAutoRulesForMigration ?? throw new ArgumentNullException(nameof(deduplicateAutoRulesForMigration));
            m_KeepRows = deduplicateStagedRowsForMigration ?? throw new ArgumentNullException(nameof(deduplicateStagedRowsForMigration));
        }

        internal void Reset()
        {
            m_Loaded = false;
        }

        internal void Load()
        {
            if (!m_Loaded)
            {
                Restore();
            }
        }

        internal bool Restore()
        {
            if (m_Loaded)
            {
                return true;
            }

            Entity city = m_Host.City();
            if (city == Entity.Null || !m_Host.EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                return false;
            }

            var buffer = m_Host.EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city, true);
            if (buffer.Length == 0)
            {
                m_Loaded = true;
                return true;
            }

            try
            {
                string payload = WorkbenchBuffer.Read(buffer);
                DispatchWorkbenchPersistentState persisted =
                    string.IsNullOrEmpty(payload)
                        ? null
                        : Workbenches.Json.Read<DispatchWorkbenchPersistentState>(payload);
                if (persisted != null)
                {
                    bool cleaned = Restore(persisted);
                    if (cleaned)
                    {
                        Save();
                    }

                    if (RtLog.VerboseEnabled)
                    {
                        Mod.log.Info("[WorkbenchRestore] drafts=" + m_Drafts.Count
                            + " " + Report.DraftSummary(m_Drafts));
                    }
                }

                m_Loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                m_Host.Ui.Fault("Persist.Restore", ex);
                m_Loaded = true;
                return false;
            }
        }

        internal void Save()
        {
            Entity city = m_Host.City();
            WorkbenchBuffer.Ensure(m_Host.EntityManager, city);
            if (city == Entity.Null || !m_Host.EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                return;
            }

            string payload = Workbenches.Json.Write(Build());
            List<string> chunks = WorkbenchBuffer.Split(payload);
            var buffer = m_Host.EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city);
            WorkbenchBuffer.Write(buffer, chunks);
        }

        internal DispatchWorkbenchPersistentState Build()
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
                preferredLineIdsByMode = m_Drafts.GetPreferredLineIdsByMode()
                    .Select(entry => new DispatchWorkbenchModePreferredLineDto
                    {
                        mode = TransitModeCodec.Format(entry.Key),
                        lineId = entry.Value
                    })
                    .ToArray(),
                lineSettings = lineSettings,
                drafts = drafts.ToArray()
            };
            m_BuildCompatState(state);
            return state;
        }

        internal bool Restore(DispatchWorkbenchPersistentState persisted)
        {
            m_Drafts.Clear();
            m_Run.ClearLineCfg();
            m_Run.DropDepotCache();
            if (persisted?.featureSettings != null)
            {
                m_MigrateLegacyFeatureSettings(persisted.featureSettings);
            }
            bool normalizedLegacy = HasLegacyRestoredState(persisted);
            bool migratedLegacyFeatureSettings = persisted?.featureSettings != null;
            m_Drafts.SetPreferredLineId(
                NormalizeLegacyRestoredLineId(persisted?.preferredLineId ?? string.Empty));
            if (persisted?.preferredLineIdsByMode != null)
            {
                for (int i = 0; i < persisted.preferredLineIdsByMode.Length; i++)
                {
                    DispatchWorkbenchModePreferredLineDto preferred = persisted.preferredLineIdsByMode[i];
                    if (preferred == null
                        || string.IsNullOrEmpty(preferred.lineId)
                        || !ModeScope.TryParseWorkbench(preferred.mode, out ModeScope scope)
                        || !scope.IsSupportedWorkbenchMode)
                    {
                        continue;
                    }

                    m_Drafts.SetPreferredLineId(scope.Mode, preferred.lineId);
                }
            }
            m_RestoreCompatState(persisted);

            if (persisted?.lineSettings != null)
            {
                m_Run.LineCfg(NormalizeLegacyLineSettings(persisted.lineSettings));
            }

            if (persisted?.drafts == null)
            {
                return migratedLegacyFeatureSettings || normalizedLegacy;
            }

            for (int i = 0; i < persisted.drafts.Length; i++)
            {
                DispatchWorkbenchPersistedDraftState dto = persisted.drafts[i];
                if (dto == null)
                {
                    continue;
                }

                string lineKey = !string.IsNullOrEmpty(dto.lineKey)
                    ? dto.lineKey
                    : DraftStore.GetKey(dto.selectedLineId);
                lineKey = NormalizeLegacyRestoredLineId(lineKey);
                DispatchWorkbenchDraftState draft = CreateDraftStateFromPersisted(lineKey, dto);
                NormalizeLegacyRestoredDraftState(draft);
                m_Drafts[lineKey] = draft;
            }

            bool migrated = Migrate();
            return migrated || normalizedLegacy || migratedLegacyFeatureSettings;
        }

        internal WorkbenchSavePersistencePayload Capture()
        {
            return new WorkbenchSavePersistencePayload
            {
                WorkbenchState = Build(),
                AppliedLineElements = m_Applied.LineElems(),
                AppliedRowElements = m_Applied.RowElems()
            };
        }

        internal PreparedWorkbenchSavePersistence Prep(WorkbenchSavePersistencePayload payload)
        {
            return new PreparedWorkbenchSavePersistence
            {
                WorkbenchPersistenceChunks = WorkbenchBuffer.Split(
                    Workbenches.Json.Write(payload?.WorkbenchState)),
                AppliedLineElements = payload?.AppliedLineElements ?? new List<AppliedWorkbenchLineStateElement>(),
                AppliedRowElements = payload?.AppliedRowElements ?? new List<AppliedWorkbenchStagedRowElement>()
            };
        }

        internal void Commit(PreparedWorkbenchSavePersistence prepared)
        {
            Write(prepared?.WorkbenchPersistenceChunks);
            m_Applied.Write(
                prepared?.AppliedLineElements,
                prepared?.AppliedRowElements);
        }

        private bool Migrate()
        {
            if (m_Drafts.Count == 0)
            {
                return false;
            }

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
                {
                    continue;
                }

                relocatedRows += MigrateManualRows(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
                relocatedRows += MigrateAutoRules(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
                relocatedRows += MigrateStagedRows(
                    sourceKey,
                    sourceDraft,
                    validRuntimeLineIds,
                    touchedDraftKeys,
                    ref createdDrafts,
                    ref droppedRows);
            }

            if (relocatedRows == 0 && droppedRows == 0)
            {
                return false;
            }

            foreach (string draftKey in touchedDraftKeys)
            {
                if (!m_Drafts.TryGetValue(draftKey, out DispatchWorkbenchDraftState draft)
                    || draft == null)
                {
                    continue;
                }

                draft.ManualRows = m_KeepManual(draft.ManualRows);
                draft.AutoRules = m_KeepRules(draft.AutoRules);
                draft.StagedRows = m_KeepRows(draft.StagedRows);
                NormalizeMigratedWorkbenchDraftState(draftKey, draft);
            }

            if (RtLog.VerboseEnabled)
            {
                Mod.log.Info("[WorkbenchDraftMigration] relocatedRows="
                    + relocatedRows
                    + " droppedRows="
                    + droppedRows
                    + " touchedDrafts="
                    + touchedDraftKeys.Count
                    + " createdDrafts="
                    + createdDrafts);
            }
            return true;
        }

        private int MigrateManualRows(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.ManualRows == null || sourceDraft.ManualRows.Count == 0)
            {
                return 0;
            }

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
                targetDraft.ManualRows.Add(m_CopyManual(row));
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

        private int MigrateAutoRules(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.AutoRules == null || sourceDraft.AutoRules.Count == 0)
            {
                return 0;
            }

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
                targetDraft.AutoRules.Add(m_CopyRule(rule));
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

        private int MigrateStagedRows(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> validRuntimeLineIds,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts,
            ref int droppedRows)
        {
            if (sourceDraft.StagedRows == null || sourceDraft.StagedRows.Count == 0)
            {
                return 0;
            }

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
                targetDraft.StagedRows.Add(m_CopyRow(row));
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

            targetKey = DraftStore.GetKey(rowLineId);
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
                draft = m_NewDraft(lineKey);
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
                m_CopyPlan(sourceDraft.PlannerImportContract);
            if (contract == null)
            {
                return;
            }

            contract.draftKey = targetKey;
            targetDraft.PlannerImportContract = contract;
        }

        private void NormalizeMigratedWorkbenchDraftState(
            string draftKey,
            DispatchWorkbenchDraftState draft)
        {
            if (draft == null)
            {
                return;
            }

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

            m_Window(draft.MergedView);
            draft.AppliedDepartureMinutesCache.Clear();
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchManualRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(DraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRulesForDraft(
            List<DispatchWorkbenchAutoRuleDto> rules,
            string draftKey)
        {
            return rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(DraftStore.GetKey(rule.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchStagedRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(DraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
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
                manualRows = draft?.ManualRows?.Select(m_CopyManual).ToArray() ?? Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = draft?.AutoRules?.Select(m_CopyRule).ToArray() ?? Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                lineDraftRows = draft?.StagedRows?.Select(m_CopyRow).ToArray() ?? Array.Empty<DispatchWorkbenchStagedRowDto>(),
                rulesApplied = draft?.RulesApplied == true,
                draftApplied = draft?.DraftApplied == true,
                plannerImportContract = m_CopyPlan(draft?.PlannerImportContract)
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
                ManualRows = dto.manualRows?.Select(m_CopyManual).ToList() ?? new List<DispatchWorkbenchManualRowDto>(),
                AutoRules = dto.autoRules?.Select(m_CopyRule).ToList() ?? new List<DispatchWorkbenchAutoRuleDto>(),
                StagedRows = m_LastById(
                    dto.lineDraftRows?.Select(m_CopyRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>()),
                RulesApplied = dto.rulesApplied,
                DraftApplied = dto.draftApplied,
                PlannerImportContract = m_CopyPlan(dto.plannerImportContract)
            };

            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            return draft;
        }

        private static DispatchWorkbenchLineSettingDto[] NormalizeLegacyLineSettings(
            DispatchWorkbenchLineSettingDto[] settings)
        {
            if (settings == null)
                return null;

            return settings.Select(setting => setting == null
                ? null
                : new DispatchWorkbenchLineSettingDto
                {
                    lineId = NormalizeLegacyRestoredLineId(setting.lineId),
                    originHoldLimitMinutes = setting.originHoldLimitMinutes,
                    maxStationDwellMinutes = setting.maxStationDwellMinutes,
                    allowedDepotId = setting.allowedDepotId,
                    serviceKind = setting.serviceKind
                }).ToArray();
        }

        private static bool HasLegacyRestoredState(DispatchWorkbenchPersistentState persisted)
        {
            if (persisted == null)
                return false;

            if (IsLegacyRestoredLineId(persisted.preferredLineId))
                return true;

            if (persisted.lineSettings != null
                && persisted.lineSettings.Any(setting => IsLegacyRestoredLineId(setting?.lineId)))
            {
                return true;
            }

            if (persisted.drafts == null)
                return false;

            return persisted.drafts.Any(HasLegacyRestoredDraftState);
        }

        private static bool HasLegacyRestoredDraftState(
            DispatchWorkbenchPersistedDraftState draft)
        {
            return draft != null
                && (IsLegacyRestoredLineId(draft.lineKey)
                    || IsLegacyRestoredLineId(draft.selectedLineId)
                    || IsLegacyRestoredLineId(draft.selectedEditLine)
                    || HasLegacyRestoredMergedView(draft.mergedView)
                    || HasLegacyRestoredRows(draft.manualRows)
                    || HasLegacyRestoredRules(draft.autoRules)
                    || HasLegacyRestoredRows(draft.lineDraftRows)
                    || HasLegacyRestoredPlanContract(draft.plannerImportContract));
        }

        private static bool HasLegacyRestoredMergedView(DispatchWorkbenchMergedView view)
        {
            return view != null
                && (IsLegacyRestoredLineId(view.localLineId)
                    || IsLegacyRestoredLineId(view.expressLineId)
                    || HasLegacyRestoredLineIds(view.localLineIds)
                    || HasLegacyRestoredLineIds(view.expressLineIds));
        }

        private static bool HasLegacyRestoredRows(
            DispatchWorkbenchManualRowDto[] rows)
        {
            return rows != null && rows.Any(row => IsLegacyRestoredLineId(row?.lineId));
        }

        private static bool HasLegacyRestoredRules(
            DispatchWorkbenchAutoRuleDto[] rules)
        {
            return rules != null && rules.Any(rule => IsLegacyRestoredLineId(rule?.lineId));
        }

        private static bool HasLegacyRestoredRows(
            DispatchWorkbenchStagedRowDto[] rows)
        {
            return rows != null && rows.Any(row => IsLegacyRestoredLineId(row?.lineId));
        }

        private static bool HasLegacyRestoredPlanContract(
            DispatchWorkbenchPlannerImportContractDto contract)
        {
            if (contract == null)
                return false;

            return IsLegacyRestoredLineId(contract.draftKey)
                || HasLegacyRestoredLineIds(contract.importedLineIds)
                || IsLegacyRestoredLineId(contract.requestEcho?.draftKey)
                || HasLegacyRestoredLineIds(contract.requestEcho?.localLineIds)
                || HasLegacyRestoredLineIds(contract.requestEcho?.adjustableLineIds)
                || IsLegacyRestoredLineId(contract.requestEcho?.expressLineId)
                || IsLegacyRestoredLineId(contract.requestEcho?.virtualExpressBaseLineId);
        }

        private static bool HasLegacyRestoredLineIds(string[] lineIds)
        {
            return lineIds != null && lineIds.Any(IsLegacyRestoredLineId);
        }

        private static void NormalizeLegacyRestoredDraftState(DispatchWorkbenchDraftState draft)
        {
            if (draft == null)
                return;

            draft.SelectedLineId = NormalizeLegacyRestoredLineId(draft.SelectedLineId);
            draft.SelectedEditLine = NormalizeLegacyRestoredLineReference(draft.SelectedEditLine);
            NormalizeLegacyRestoredMergedView(draft.MergedView);
            NormalizeLegacyRestoredManualRows(draft.ManualRows);
            NormalizeLegacyRestoredAutoRules(draft.AutoRules);
            NormalizeLegacyRestoredStagedRows(draft.StagedRows);
            NormalizeLegacyRestoredPlanContract(draft.PlannerImportContract);
        }

        private static void NormalizeLegacyRestoredMergedView(DispatchWorkbenchMergedView view)
        {
            if (view == null)
                return;

            view.localLineId = NormalizeLegacyRestoredLineId(view.localLineId);
            view.expressLineId = NormalizeLegacyRestoredLineId(view.expressLineId);
            view.localLineIds = NormalizeLegacyRestoredLineIds(view.localLineIds);
            view.expressLineIds = NormalizeLegacyRestoredLineIds(view.expressLineIds);
        }

        private static void NormalizeLegacyRestoredManualRows(
            List<DispatchWorkbenchManualRowDto> rows)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                {
                    rows[i].lineId = NormalizeLegacyRestoredLineId(rows[i].lineId);
                }
            }
        }

        private static void NormalizeLegacyRestoredAutoRules(
            List<DispatchWorkbenchAutoRuleDto> rules)
        {
            if (rules == null)
                return;

            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i] != null)
                {
                    rules[i].lineId = NormalizeLegacyRestoredLineId(rules[i].lineId);
                }
            }
        }

        private static void NormalizeLegacyRestoredStagedRows(
            List<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null)
                {
                    rows[i].lineId = NormalizeLegacyRestoredLineId(rows[i].lineId);
                }
            }
        }

        private static void NormalizeLegacyRestoredPlanContract(
            DispatchWorkbenchPlannerImportContractDto contract)
        {
            if (contract == null)
                return;

            contract.draftKey = NormalizeLegacyRestoredLineId(contract.draftKey);
            contract.importedLineIds = NormalizeLegacyRestoredLineIds(contract.importedLineIds);
            if (contract.requestEcho == null)
                return;

            if (string.IsNullOrWhiteSpace(contract.requestEcho.mode))
            {
                contract.requestEcho.mode = ModeScope.DefaultWorkbench.Token;
            }

            contract.requestEcho.draftKey =
                NormalizeLegacyRestoredLineId(contract.requestEcho.draftKey);
            contract.requestEcho.localLineIds =
                NormalizeLegacyRestoredLineIds(contract.requestEcho.localLineIds);
            contract.requestEcho.adjustableLineIds =
                NormalizeLegacyRestoredLineIds(contract.requestEcho.adjustableLineIds);
            contract.requestEcho.expressLineId =
                NormalizeLegacyRestoredLineId(contract.requestEcho.expressLineId);
            contract.requestEcho.virtualExpressBaseLineId =
                NormalizeLegacyRestoredLineId(contract.requestEcho.virtualExpressBaseLineId);
        }

        private static string[] NormalizeLegacyRestoredLineIds(string[] lineIds)
        {
            if (lineIds == null)
                return Array.Empty<string>();

            string[] normalized = new string[lineIds.Length];
            for (int i = 0; i < lineIds.Length; i++)
            {
                normalized[i] = NormalizeLegacyRestoredLineId(lineIds[i]);
            }

            return normalized;
        }

        private static string NormalizeLegacyRestoredLineReference(string lineId)
        {
            if (string.Equals(lineId, "local", StringComparison.Ordinal)
                || string.Equals(lineId, "express", StringComparison.Ordinal))
            {
                return lineId;
            }

            return NormalizeLegacyRestoredLineId(lineId);
        }

        private static string NormalizeLegacyRestoredLineId(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            string trimmed = lineId.Trim();
            if (string.Equals(trimmed, "__default__", StringComparison.Ordinal)
                || string.Equals(trimmed, "local", StringComparison.Ordinal)
                || string.Equals(trimmed, "express", StringComparison.Ordinal)
                || trimmed.IndexOf(':') >= 0)
            {
                return trimmed;
            }

            return string.Empty;
        }

        private static bool IsLegacyRestoredLineId(string lineId)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return false;

            string trimmed = lineId.Trim();
            return !string.Equals(trimmed, "__default__", StringComparison.Ordinal)
                && !string.Equals(trimmed, "local", StringComparison.Ordinal)
                && !string.Equals(trimmed, "express", StringComparison.Ordinal)
                && trimmed.IndexOf(':') < 0;
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

        private void Write(List<string> chunks)
        {
            Entity city = m_Host.City();
            WorkbenchBuffer.Ensure(m_Host.EntityManager, city);
            if (city == Entity.Null || !m_Host.EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                return;
            }

            var buffer = m_Host.EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city);
            WorkbenchBuffer.Write(buffer, chunks);
        }
    }

    internal enum RestoredWorkbenchRowRepairAction
    {
        Keep = 0,
        Move = 1,
        Drop = 2
    }
}
