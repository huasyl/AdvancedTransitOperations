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

                    Mod.log.Info("[WorkbenchRestore] drafts=" + m_Drafts.Count
                        + " " + Report.DraftSummary(m_Drafts));
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
                lineSettings = lineSettings,
                drafts = drafts.ToArray(),
                featureSettings = m_Run.FeatureDto()
            };
            m_BuildCompatState(state);
            return state;
        }

        internal bool Restore(DispatchWorkbenchPersistentState persisted)
        {
            m_Drafts.Clear();
            m_Run.ClearLineCfg();
            m_Run.DropDepotCache();
            m_Run.Features(persisted?.featureSettings);
            m_Drafts.SetPreferredLineId(persisted?.preferredLineId ?? string.Empty);
            m_RestoreCompatState(persisted);

            if (persisted?.lineSettings != null)
            {
                m_Run.LineCfg(persisted.lineSettings);
            }

            if (persisted?.drafts == null)
            {
                return persisted?.featureSettings == null;
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
                m_Drafts[lineKey] = CreateDraftStateFromPersisted(lineKey, dto);
            }

            bool migrated = Migrate();
            return persisted?.featureSettings == null || migrated;
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
