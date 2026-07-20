using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Commands
    {
        private const int MinHold = 5;
        private const int MaxHold = 120;
        private readonly Host m_Host;
        private readonly RunPort m_Run;
        private readonly Drafts m_Drafts;
        private readonly Query m_Query;
        private readonly Snapshot m_Snap;
        private readonly Persist m_Persist;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_LastById;
        private readonly Func<DispatchWorkbenchCleanupInfoDto> m_ConsumeCleanupInfo;
        internal Commands(
            Host host,
            Drafts drafts,
            Query query,
            Snapshot snap,
            Persist persist,
            Func<DispatchWorkbenchCleanupInfoDto> consumeCleanupInfo)
        {
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
            m_Run = host.Run;
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
            m_Snap = snap ?? throw new ArgumentNullException(nameof(snap));
            m_Persist = persist ?? throw new ArgumentNullException(nameof(persist));
            m_CopyRow = Rows.CopyRow;
            m_LastById = Rows.LastById;
            m_ConsumeCleanupInfo = consumeCleanupInfo ?? throw new ArgumentNullException(nameof(consumeCleanupInfo));
        }

        internal WorkbenchSavePrepareContext Capture(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "saveNativeWorkbenchDraft");
            return new WorkbenchSavePrepareContext
            {
                RequestJson = requestJson ?? string.Empty,
                Scope = scope,
                SnapshotVersion = m_Host.Version(),
                RuntimeLines = m_Query.GetLines(scope.Mode),
                Depots = m_Host.Depots(),
                ServiceKinds = m_Run.Keys()
                    .ToDictionary(
                        lineId => lineId,
                        lineId => m_Run.Kind(lineId),
                        StringComparer.Ordinal)
            };
        }

        internal PreparedWorkbenchSave Prep(WorkbenchSavePrepareContext context)
        {
            ulong baseVersion = context?.SnapshotVersion ?? m_Host.Version();
            PreparedWorkbenchSave prepared = new PreparedWorkbenchSave
            {
                SnapshotVersion = baseVersion,
                Scope = context?.Scope ?? ModeScope.DefaultWorkbench,
                RuntimeLines = context?.RuntimeLines ?? new List<WorkbenchLineRuntime>()
            };

            try
            {
                DispatchWorkbenchSaveRequest request =
                    Workbenches.Json.Read<DispatchWorkbenchSaveRequest>(context?.RequestJson);
                if (request != null)
                {
                    request.mode = prepared.Scope.Token;
                }
                List<string> errors = NormalizeRequestForScope(request, prepared.Scope);
                List<WorkbenchLineRuntime> runtimeLines = (context?.RuntimeLines ?? new List<WorkbenchLineRuntime>())
                    .Select(CloneWorkbenchLineRuntime)
                    .ToList();
                List<DispatchWorkbenchDepotDto> depots = (context?.Depots ?? new List<DispatchWorkbenchDepotDto>())
                    .Select(CloneWorkbenchDepot)
                    .ToList();

                Check.NormalizeView(
                    request,
                    runtimeLines,
                    Rows.Ids,
                    RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind,
                    lineId => m_Run.Kind(lineId),
                    context?.ServiceKinds);
                errors.AddRange(ValidateRequest(
                    request,
                    runtimeLines,
                    request?.applyDraft == true,
                    depots));
                prepared.Request = request;
                prepared.RuntimeLines = runtimeLines;
                prepared.Errors = errors;
                prepared.ShouldReturnSnapshot = ReturnSnapshot(request);
                prepared.LineSettingsChanged = request?.lineSettings != null
                    && !m_Run.SameLineCfg(prepared.Scope, request.lineSettings);
                return prepared;
            }
            catch (Exception ex)
            {
                prepared.Errors = new List<string> { ex.GetType().Name + ": " + ex.Message };
                return prepared;
            }
        }

        internal DispatchWorkbenchSaveResult Commit(
            PreparedWorkbenchSave prepared,
            bool persistImmediately)
        {
            DispatchWorkbenchSaveResult result = prepared?.ToResult()
                ?? CreateWorkbenchSaveFailureResult(m_Host.Version(), "save-prepare-failed");
            if (prepared == null || prepared.HasErrors)
            {
                if (prepared?.Request != null)
                {
                    result.snapshot = BuildSnapshot(
                        prepared.Scope,
                        prepared.Request.selectedLineId,
                        prepared.Request.clientRequestSequence);
                    result.cleanupInfo = result.snapshot?.cleanupInfo;
                }
                return result;
            }

            DispatchWorkbenchSaveRequest request = prepared.Request;
            int clientRequestSequence = Math.Max(0, request?.clientRequestSequence ?? 0);
            List<WorkbenchLineRuntime> runtimeLines = prepared.RuntimeLines ?? m_Host.Lines();
            bool autoCleanupChanged = m_Run.CleanupInvalidApplied();
            string lineKey = DraftStore.GetKey(request?.selectedLineId);
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> nextLineDraftRowsByKey =
                RowsByDraft(request, lineKey);
            HashSet<string> clearedAppliedLineIds = ClearedAppliedLineIds(request?.applyDraft == true, nextLineDraftRowsByKey);
            Dictionary<string, string> requestedCleanupReasons = CleanupReasons(
                Array.Empty<string>(),
                clearedAppliedLineIds,
                Array.Empty<string>());
            bool requestedCleanupChanged = requestedCleanupReasons.Count > 0
                && m_Run.CleanupRequestedLines(requestedCleanupReasons);
            HashSet<string> invalidatedLineIds = new HashSet<string>(requestedCleanupReasons.Keys, StringComparer.Ordinal);
            if (invalidatedLineIds.Count > 0)
            {
                StripInvalidatedRequestLines(
                    request,
                    nextLineDraftRowsByKey,
                    invalidatedLineIds);
                prepared.LineSettingsChanged = request?.lineSettings != null
                    && !m_Run.SameLineCfg(prepared.Scope, request.lineSettings);
                lineKey = ResolveLineKeyAfterCleanup(lineKey, invalidatedLineIds, requestedCleanupReasons, runtimeLines);
            }

            DispatchWorkbenchDraftState state = m_Drafts.Get(lineKey);
            bool hasActiveLineDraftRows = nextLineDraftRowsByKey.TryGetValue(
                lineKey,
                out List<DispatchWorkbenchStagedRowDto> activeLineDraftRows);
            List<DispatchWorkbenchStagedRowDto> nextStagedRows = hasActiveLineDraftRows
                ? m_LastById(activeLineDraftRows)
                : state.StagedRows.Select(m_CopyRow).ToList();

            if (request.applyDraft)
            {
                List<string> appliedErrors = ValidateApplied(
                    lineKey,
                    nextLineDraftRowsByKey.Values.SelectMany(rows => rows).ToList(),
                    runtimeLines,
                    prepared.Scope.Mode);
                if (appliedErrors.Count > 0)
                {
                    result.success = false;
                    result.errors = appliedErrors.ToArray();
                    result.snapshot = BuildSnapshot(
                        prepared.Scope,
                        lineKey,
                        clientRequestSequence);
                    result.cleanupInfo = result.snapshot?.cleanupInfo;
                    return result;
                }
            }

            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> nextPlanRefsByKey =
                PlanRefs(
                    request,
                    nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }));
            string requestedSelectedLineId = RequestedSelectedLineId(request, lineKey, runtimeLines);
            string requestedSelectedEditLine = RequestedSelectedEditLine(request, lineKey, runtimeLines);
            bool hasAdditionalLineDraftTargets = nextLineDraftRowsByKey.Keys
                .Any(key => !string.Equals(key, lineKey, StringComparison.Ordinal));
            bool rulesChanged = !Rows.SameRows(state.StagedRows, nextStagedRows);
            DispatchWorkbenchPlannerImportContractDto nextPlanRef = PlanRef(
                lineKey,
                state.PlannerImportContract,
                nextPlanRefsByKey,
                rulesChanged,
                nextStagedRows.Count > 0);

            if (!request.applyDraft
                && !autoCleanupChanged
                && !requestedCleanupChanged
                && !hasAdditionalLineDraftTargets
                && string.Equals(state.SelectedLineId ?? string.Empty, requestedSelectedLineId, StringComparison.Ordinal)
                && string.Equals(state.SelectedEditLine ?? string.Empty, requestedSelectedEditLine, StringComparison.Ordinal)
                && Rows.SameView(state.MergedView, request.mergedView)
                && Rows.SameRows(state.StagedRows, nextStagedRows)
                && Rows.SamePlan(state.PlannerImportContract, nextPlanRef)
                && m_Run.SameLineCfg(prepared.Scope, request.lineSettings))
            {
                result.success = true;
                result.version = m_Host.Version().ToString();
                result.snapshot = null;
                return result;
            }

            if (request.lineSettings != null)
            {
                m_Run.LineCfg(prepared.Scope, request.lineSettings);
            }

            state.SelectedLineId = requestedSelectedLineId;
            state.SelectedEditLine = requestedSelectedEditLine;
            state.MergedView = request.mergedView ?? state.MergedView;
            state.ManualRows = new List<DispatchWorkbenchManualRowDto>();
            state.AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
            state.StagedRows = nextStagedRows;
            state.PlannerImportContract = nextPlanRef;
            bool additionalDraftRowsChanged = ApplyMore(
                lineKey,
                nextLineDraftRowsByKey,
                nextPlanRefsByKey,
                request.applyDraft);
            if (additionalDraftRowsChanged)
            {
                rulesChanged = true;
            }
            HashSet<string> cleanupLineIds = TouchLines(
                state.SelectedLineId,
                state.SelectedEditLine,
                nextLineDraftRowsByKey.Values.SelectMany(rows => rows).ToList());
            foreach (string draftTargetKey in nextLineDraftRowsByKey.Keys)
            {
                if (!string.IsNullOrEmpty(draftTargetKey) && !string.Equals(draftTargetKey, "__default__", StringComparison.Ordinal))
                {
                    cleanupLineIds.Add(draftTargetKey);
                }
            }

            RemoveOther(
                new HashSet<string>(nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }), StringComparer.Ordinal),
                cleanupLineIds);
            state.AppliedDepartureMinutesCache.Clear();

            if (rulesChanged)
            {
                state.DraftApplied = false;
            }

            if (request.applyDraft && nextLineDraftRowsByKey.ContainsKey(lineKey))
            {
                state.DraftApplied = true;
            }

            m_Host.Dirty();
            ulong nextVersion = m_Host.Version();
            m_Drafts.SetPreferred(state.SelectedLineId, prepared.Scope.Mode);
            if (request.applyDraft)
            {
                m_Run.ApplyDraft(nextLineDraftRowsByKey.Keys, runtimeLines);
                m_Host.Seed(state.SelectedLineId);
            }
            else
            {
                m_Run.RefreshApplied();
                if (prepared.LineSettingsChanged)
                {
                    m_Run.Invalidate();
                }
            }
            if (persistImmediately)
            {
                m_Persist.Save();
                m_Host.SaveApplied();
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
                result.snapshot = BuildSnapshot(
                    prepared.Scope,
                    state.SelectedLineId,
                    clientRequestSequence);
                result.cleanupInfo = result.snapshot?.cleanupInfo;
                m_Host.Ui.Push(result.snapshot);
            }
            else
            {
                result.snapshot = null;
                result.cleanupInfo = m_ConsumeCleanupInfo();
            }
            return result;
        }

        private DispatchWorkbenchSnapshot BuildSnapshot(
            ModeScope scope,
            string lineId,
            int clientRequestSequence = 0)
        {
            DispatchWorkbenchSnapshot snapshot = m_Snap.Build(lineId, scope.Mode, m_Host.Version(), "game-backend");
            if (snapshot != null)
            {
                snapshot.clientRequestSequence = Math.Max(0, clientRequestSequence);
            }

            return snapshot;
        }

        private static List<string> NormalizeRequestForScope(
            DispatchWorkbenchSaveRequest request,
            ModeScope scope)
        {
            List<string> errors = new List<string>();
            if (request == null)
                return errors;

            request.selectedLineId = NormalizeLineId(scope, request.selectedLineId, "selectedLineId", errors);
            request.selectedEditLine = NormalizeSelectedEditLine(scope, request.selectedEditLine, errors);
            NormalizeMergedView(scope, request.mergedView, errors);
            NormalizeStagedRows(scope, request.lineDraftRows, "lineDraftRows", errors);
            NormalizeLineDraftRowBlocks(scope, request.lineDraftRowsByLineId, errors);
            NormalizeLineSettings(scope, request.lineSettings, errors);
            NormalizePlanRefs(scope, request.planRefs, errors);
            NormalizePlanContract(scope, request.plannerImportContract, "plannerImportContract", errors);
            request.removedLineIds = NormalizeLineIds(scope, request.removedLineIds, "removedLineIds", errors);
            NormalizeLineRuntimeRefs(scope, request.lineRuntimeRefs, errors);
            request.clientRequestSequence = Math.Max(0, request.clientRequestSequence);
            return errors;
        }

        private static void NormalizeMergedView(
            ModeScope scope,
            DispatchWorkbenchMergedView view,
            List<string> errors)
        {
            if (view == null)
                return;

            view.localLineId = NormalizeLineId(scope, view.localLineId, "mergedView.localLineId", errors);
            view.expressLineId = NormalizeLineId(scope, view.expressLineId, "mergedView.expressLineId", errors);
            view.localLineIds = NormalizeLineIds(scope, view.localLineIds, "mergedView.localLineIds", errors);
            view.expressLineIds = NormalizeLineIds(scope, view.expressLineIds, "mergedView.expressLineIds", errors);
        }

        private static void NormalizeStagedRows(
            ModeScope scope,
            DispatchWorkbenchStagedRowDto[] rows,
            string fieldName,
            List<string> errors)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null)
                    continue;

                rows[i].lineId = NormalizeLineId(scope, rows[i].lineId, fieldName + "[" + i + "].lineId", errors);
            }
        }

        private static void NormalizeLineDraftRowBlocks(
            ModeScope scope,
            DispatchWorkbenchLineDraftRowsDto[] blocks,
            List<string> errors)
        {
            if (blocks == null)
                return;

            for (int i = 0; i < blocks.Length; i++)
            {
                DispatchWorkbenchLineDraftRowsDto block = blocks[i];
                if (block == null)
                    continue;

                block.lineId = NormalizeLineId(scope, block.lineId, "lineDraftRowsByLineId[" + i + "].lineId", errors);
                NormalizeStagedRows(
                    scope,
                    block.lineDraftRows,
                    "lineDraftRowsByLineId[" + i + "].lineDraftRows",
                    errors);
            }
        }

        private static void NormalizeLineSettings(
            ModeScope scope,
            DispatchWorkbenchLineSettingDto[] settings,
            List<string> errors)
        {
            if (settings == null)
                return;

            for (int i = 0; i < settings.Length; i++)
            {
                if (settings[i] == null)
                    continue;

                settings[i].lineId = NormalizeLineId(scope, settings[i].lineId, "lineSettings[" + i + "].lineId", errors);
            }
        }

        private static void NormalizePlanRefs(
            ModeScope scope,
            DispatchWorkbenchPlanRefDto[] planRefs,
            List<string> errors)
        {
            if (planRefs == null)
                return;

            for (int i = 0; i < planRefs.Length; i++)
            {
                DispatchWorkbenchPlanRefDto planRef = planRefs[i];
                if (planRef == null)
                    continue;

                planRef.lineId = NormalizeLineId(scope, planRef.lineId, "planRefs[" + i + "].lineId", errors);
                NormalizePlanContract(scope, planRef.contract, "planRefs[" + i + "].contract", errors);
            }
        }

        private static void NormalizePlanContract(
            ModeScope scope,
            DispatchWorkbenchPlannerImportContractDto contract,
            string fieldName,
            List<string> errors)
        {
            if (contract == null)
                return;

            contract.draftKey = NormalizeLineId(scope, contract.draftKey, fieldName + ".draftKey", errors);
            contract.importedLineIds = NormalizeLineIds(scope, contract.importedLineIds, fieldName + ".importedLineIds", errors);
            NormalizePlannerRequestEcho(scope, contract.requestEcho, fieldName + ".requestEcho", errors);
        }

        private static void NormalizePlannerRequestEcho(
            ModeScope scope,
            DispatchPlannerRequestEchoDto requestEcho,
            string fieldName,
            List<string> errors)
        {
            if (requestEcho == null)
                return;

            if (!string.IsNullOrEmpty(requestEcho.mode)
                && (!ModeScope.TryParseWorkbench(requestEcho.mode, out ModeScope echoScope)
                    || echoScope.Mode != scope.Mode))
            {
                errors.Add(fieldName + ".mode does not belong to mode " + scope.Token + ": " + requestEcho.mode);
            }

            requestEcho.mode = scope.Token;
            requestEcho.draftKey = NormalizeLineId(scope, requestEcho.draftKey, fieldName + ".draftKey", errors);
            requestEcho.localLineIds = NormalizeLineIds(scope, requestEcho.localLineIds, fieldName + ".localLineIds", errors);
            requestEcho.adjustableLineIds = NormalizeLineIds(scope, requestEcho.adjustableLineIds, fieldName + ".adjustableLineIds", errors);
            requestEcho.expressLineId = NormalizeLineId(scope, requestEcho.expressLineId, fieldName + ".expressLineId", errors);
            requestEcho.virtualExpressBaseLineId = NormalizeLineId(scope, requestEcho.virtualExpressBaseLineId, fieldName + ".virtualExpressBaseLineId", errors);
        }

        private static string NormalizeSelectedEditLine(
            ModeScope scope,
            string lineId,
            List<string> errors)
        {
            if (string.Equals(lineId, "local", StringComparison.Ordinal)
                || string.Equals(lineId, "express", StringComparison.Ordinal))
            {
                return lineId;
            }

            return NormalizeLineId(scope, lineId, "selectedEditLine", errors);
        }

        private static string[] NormalizeLineIds(
            ModeScope scope,
            string[] lineIds,
            string fieldName,
            List<string> errors)
        {
            if (lineIds == null)
                return Array.Empty<string>();

            string[] normalized = new string[lineIds.Length];
            for (int i = 0; i < lineIds.Length; i++)
            {
                normalized[i] = NormalizeLineId(scope, lineIds[i], fieldName + "[" + i + "]", errors);
            }

            return normalized;
        }

        private static void NormalizeLineRuntimeRefs(
            ModeScope scope,
            DispatchWorkbenchLineRuntimeRefDto[] lineRuntimeRefs,
            List<string> errors)
        {
            if (lineRuntimeRefs == null)
            {
                return;
            }

            for (int i = 0; i < lineRuntimeRefs.Length; i++)
            {
                DispatchWorkbenchLineRuntimeRefDto lineRuntimeRef = lineRuntimeRefs[i];
                if (lineRuntimeRef == null)
                {
                    continue;
                }

                lineRuntimeRef.lineId = NormalizeLineId(scope, lineRuntimeRef.lineId, "lineRuntimeRefs[" + i + "].lineId", errors);
                lineRuntimeRef.sourceLineId = lineRuntimeRef.sourceLineId ?? string.Empty;
            }
        }

        private static string NormalizeLineId(
            ModeScope scope,
            string lineId,
            string fieldName,
            List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                return string.Empty;

            scope.ValidateLineId(lineId, fieldName, errors);
            return scope.MatchesLineId(lineId)
                ? scope.NormalizeLineId(lineId)
                : lineId;
        }

        private List<string> ValidateRequest(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            bool validateApplyOnlyConstraints,
            List<DispatchWorkbenchDepotDto> depots)
        {
            return Check.Request(
                request,
                runtimeLines,
                validateApplyOnlyConstraints,
                depots,
                Rows.Ids,
                m_Host.Depots,
                Time.Parse,
                NormalizeDepotId,
                NormalizeDepotId,
                MinHold,
                MaxHold,
                RowsByDraft,
                DraftStore.GetKey,
                Time.Slot);
        }

        private List<string> ValidateApplied(
            string lineKey,
            List<DispatchWorkbenchStagedRowDto> rows,
            List<WorkbenchLineRuntime> runtimeLines,
            TransitMode mode)
        {
            return Check.AppliedRows(
                lineKey,
                rows,
                runtimeLines,
                BuildAppliedState(mode),
                Time.Parse,
                Time.Slot);
        }

        private Dictionary<string, AppliedLine> BuildAppliedState(TransitMode mode)
        {
            return m_Query.BuildAppliedRows(mode)
                .Where(row => row != null && !string.IsNullOrEmpty(row.lineId))
                .GroupBy(row => row.lineId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new AppliedLine
                    {
                        StagedRows = group.Select(m_CopyRow).ToList()
                    },
                    StringComparer.Ordinal);
        }

        private static bool ReturnSnapshot(DispatchWorkbenchSaveRequest request)
        {
            return request?.returnSnapshot != false;
        }

        private bool ApplyMore(
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
                string draftKey = DraftStore.GetKey(entry.Key);
                if (string.Equals(draftKey, activeLineKey, StringComparison.Ordinal))
                    continue;

                DispatchWorkbenchDraftState draft = m_Drafts.Get(draftKey);
                List<DispatchWorkbenchStagedRowDto> nextRows =
                    m_LastById(entry.Value?.Select(m_CopyRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>());
                bool draftRowsChanged = !Rows.SameRows(draft.StagedRows, nextRows);
                DispatchWorkbenchPlannerImportContractDto nextRef = PlanRef(
                    draftKey,
                    draft.PlannerImportContract,
                    requestRefsByDraftKey,
                    draftRowsChanged,
                    nextRows.Count > 0);
                bool refChanged = !Rows.SamePlan(draft.PlannerImportContract, nextRef);
                if (!draftRowsChanged && !refChanged && (!markDraftApplied || draft.DraftApplied))
                    continue;

                draft.SelectedLineId = draftKey;
                draft.SelectedEditLine = draftKey == "__default__" ? string.Empty : draftKey;
                draft.ManualRows = new List<DispatchWorkbenchManualRowDto>();
                draft.AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
                draft.StagedRows = nextRows;
                draft.DraftApplied = markDraftApplied;
                draft.AppliedDepartureMinutesCache.Clear();
                draft.PlannerImportContract = nextRef;
                changed = true;
            }

            return changed;
        }

        private void RemoveOther(HashSet<string> skippedDraftKeys, HashSet<string> lineIds)
        {
            if (lineIds == null || lineIds.Count == 0)
                return;

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts.Store)
            {
                if (skippedDraftKeys != null && skippedDraftKeys.Contains(entry.Key))
                    continue;

                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null)
                    continue;

                int stagedBefore = draft.StagedRows?.Count ?? 0;
                bool clearedLegacyDraftState =
                    (draft.ManualRows?.Count ?? 0) > 0
                    || (draft.AutoRules?.Count ?? 0) > 0
                    || draft.RulesApplied;

                if (draft.StagedRows != null)
                {
                    draft.StagedRows = draft.StagedRows
                        .Where(row => row == null || string.IsNullOrEmpty(row.lineId) || !lineIds.Contains(row.lineId))
                        .ToList();
                }

                draft.ManualRows = new List<DispatchWorkbenchManualRowDto>();
                draft.AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
                draft.RulesApplied = false;

                bool changed = clearedLegacyDraftState
                    || stagedBefore != (draft.StagedRows?.Count ?? 0);

                if (!changed)
                    continue;

                draft.DraftApplied = false;
                draft.PlannerImportContract = null;
                draft.AppliedDepartureMinutesCache.Clear();
            }
        }

        private static Dictionary<string, string> CleanupReasons(
            IEnumerable<string> removedLineIds,
            IEnumerable<string> clearedAppliedLineIds,
            IEnumerable<string> replacedLineIds)
        {
            Dictionary<string, string> reasons =
                new Dictionary<string, string>(StringComparer.Ordinal);
            AddCleanupReasons(reasons, removedLineIds, "runtime-line-missing");
            AddCleanupReasons(reasons, clearedAppliedLineIds, "applied-cleared-empty");
            AddCleanupReasons(reasons, replacedLineIds, "line-replaced-under-same-lineId");
            return reasons;
        }

        private static void AddCleanupReasons(
            Dictionary<string, string> reasons,
            IEnumerable<string> lineIds,
            string reason)
        {
            if (reasons == null || lineIds == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            foreach (string lineId in lineIds
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal))
            {
                reasons[lineId] = reason;
            }
        }

        private static HashSet<string> ReplacedLineIds(
            IEnumerable<DispatchWorkbenchLineRuntimeRefDto> lineRuntimeRefs,
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            Dictionary<string, string> runtimeRefs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (WorkbenchLineRuntime runtimeLine in runtimeLines ?? Array.Empty<WorkbenchLineRuntime>())
            {
                string lineId = DraftStore.GetKey(runtimeLine?.Id);
                if (string.IsNullOrEmpty(lineId))
                {
                    continue;
                }

                runtimeRefs[lineId] = RuntimeSourceLineId(runtimeLine);
            }

            HashSet<string> replacedLineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchWorkbenchLineRuntimeRefDto lineRuntimeRef in lineRuntimeRefs ?? Array.Empty<DispatchWorkbenchLineRuntimeRefDto>())
            {
                string lineId = DraftStore.GetKey(lineRuntimeRef?.lineId);
                string requestSourceLineId = lineRuntimeRef?.sourceLineId ?? string.Empty;
                if (string.IsNullOrEmpty(lineId)
                    || string.IsNullOrEmpty(requestSourceLineId)
                    || !runtimeRefs.TryGetValue(lineId, out string currentSourceLineId)
                    || string.IsNullOrEmpty(currentSourceLineId))
                {
                    continue;
                }

                if (!string.Equals(requestSourceLineId, currentSourceLineId, StringComparison.Ordinal))
                {
                    replacedLineIds.Add(lineId);
                }
            }

            return replacedLineIds;
        }

        private static HashSet<string> ClearedAppliedLineIds(
            bool applyDraft,
            IReadOnlyDictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey)
        {
            if (!applyDraft || rowsByDraftKey == null || rowsByDraftKey.Count == 0)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return new HashSet<string>(
                rowsByDraftKey
                    .Where(entry => !string.IsNullOrEmpty(entry.Key)
                        && !string.Equals(entry.Key, "__default__", StringComparison.Ordinal)
                        && (entry.Value == null || entry.Value.Count == 0))
                    .Select(entry => entry.Key),
                StringComparer.Ordinal);
        }

        private static void StripInvalidatedRequestLines(
            DispatchWorkbenchSaveRequest request,
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey,
            HashSet<string> invalidatedLineIds)
        {
            if (invalidatedLineIds == null || invalidatedLineIds.Count == 0)
            {
                return;
            }

            if (rowsByDraftKey != null)
            {
                foreach (string lineId in invalidatedLineIds.ToArray())
                {
                    rowsByDraftKey.Remove(lineId);
                }
            }

            if (request == null)
            {
                return;
            }

            if (request.lineSettings != null)
            {
                request.lineSettings = request.lineSettings
                    .Where(setting => setting == null
                        || string.IsNullOrEmpty(setting.lineId)
                        || !invalidatedLineIds.Contains(DraftStore.GetKey(setting.lineId)))
                    .ToArray();
            }

            if (request.planRefs != null)
            {
                request.planRefs = request.planRefs
                    .Where(planRef =>
                    {
                        string lineId = DraftStore.GetKey(!string.IsNullOrEmpty(planRef?.lineId)
                            ? planRef.lineId
                            : planRef?.contract?.draftKey);
                        return string.IsNullOrEmpty(lineId) || !invalidatedLineIds.Contains(lineId);
                    })
                    .ToArray();
            }

            if (PlannerContractTouchesAnyLine(request.plannerImportContract, invalidatedLineIds))
            {
                request.plannerImportContract = null;
            }

            if (request.lineRuntimeRefs != null)
            {
                request.lineRuntimeRefs = request.lineRuntimeRefs
                    .Where(lineRuntimeRef => lineRuntimeRef == null
                        || string.IsNullOrEmpty(lineRuntimeRef.lineId)
                        || !invalidatedLineIds.Contains(DraftStore.GetKey(lineRuntimeRef.lineId)))
                    .ToArray();
            }

            StripMergedViewInvalidatedLineIds(request.mergedView, invalidatedLineIds);
        }

        private static bool PlannerContractTouchesAnyLine(
            DispatchWorkbenchPlannerImportContractDto contract,
            HashSet<string> lineIds)
        {
            if (contract == null || lineIds == null || lineIds.Count == 0)
            {
                return false;
            }

            if (lineIds.Contains(DraftStore.GetKey(contract.draftKey)))
            {
                return true;
            }

            if ((contract.importedLineIds ?? Array.Empty<string>())
                .Any(lineId => lineIds.Contains(DraftStore.GetKey(lineId))))
            {
                return true;
            }

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo == null)
            {
                return false;
            }

            return lineIds.Contains(DraftStore.GetKey(echo.draftKey))
                || lineIds.Contains(DraftStore.GetKey(echo.expressLineId))
                || lineIds.Contains(DraftStore.GetKey(echo.virtualExpressBaseLineId))
                || (echo.localLineIds ?? Array.Empty<string>())
                    .Any(lineId => lineIds.Contains(DraftStore.GetKey(lineId)))
                || (echo.adjustableLineIds ?? Array.Empty<string>())
                    .Any(lineId => lineIds.Contains(DraftStore.GetKey(lineId)));
        }

        private static void StripMergedViewInvalidatedLineIds(
            DispatchWorkbenchMergedView view,
            HashSet<string> invalidatedLineIds)
        {
            if (view == null || invalidatedLineIds == null || invalidatedLineIds.Count == 0)
            {
                return;
            }

            string[] nextLocal = (view.localLineIds ?? Array.Empty<string>())
                .Where(lineId => !invalidatedLineIds.Contains(DraftStore.GetKey(lineId)))
                .ToArray();
            string[] nextExpress = (view.expressLineIds ?? Array.Empty<string>())
                .Where(lineId => !invalidatedLineIds.Contains(DraftStore.GetKey(lineId)))
                .ToArray();
            view.localLineIds = nextLocal;
            view.expressLineIds = nextExpress;
            view.localLineId = invalidatedLineIds.Contains(DraftStore.GetKey(view.localLineId))
                ? (nextLocal.FirstOrDefault() ?? string.Empty)
                : (view.localLineId ?? string.Empty);
            view.expressLineId = invalidatedLineIds.Contains(DraftStore.GetKey(view.expressLineId))
                ? (nextExpress.FirstOrDefault() ?? string.Empty)
                : (view.expressLineId ?? string.Empty);
        }

        private static string ResolveLineKeyAfterCleanup(
            string lineKey,
            HashSet<string> invalidatedLineIds,
            IReadOnlyDictionary<string, string> reasons,
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            string normalizedLineKey = DraftStore.GetKey(lineKey);
            if (string.IsNullOrEmpty(normalizedLineKey)
                || invalidatedLineIds == null
                || !invalidatedLineIds.Contains(normalizedLineKey)
                || !reasons.TryGetValue(normalizedLineKey, out string reason)
                || !string.Equals(reason, "runtime-line-missing", StringComparison.Ordinal))
            {
                return normalizedLineKey;
            }

            return (runtimeLines ?? Array.Empty<WorkbenchLineRuntime>())
                .Select(runtimeLine => DraftStore.GetKey(runtimeLine?.Id))
                .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate))
                ?? "__default__";
        }

        private static string RequestedSelectedLineId(
            DispatchWorkbenchSaveRequest request,
            string fallbackLineKey,
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            string requestedLineKey = DraftStore.GetKey(request?.selectedLineId);
            if (!string.IsNullOrEmpty(requestedLineKey)
                && RuntimeContainsLineKey(runtimeLines, requestedLineKey))
            {
                return requestedLineKey;
            }

            return string.IsNullOrEmpty(fallbackLineKey) || string.Equals(fallbackLineKey, "__default__", StringComparison.Ordinal)
                ? string.Empty
                : fallbackLineKey;
        }

        private static string RequestedSelectedEditLine(
            DispatchWorkbenchSaveRequest request,
            string fallbackLineKey,
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            string selectedEditLine = request?.selectedEditLine ?? string.Empty;
            if (string.Equals(selectedEditLine, "local", StringComparison.Ordinal)
                || string.Equals(selectedEditLine, "express", StringComparison.Ordinal))
            {
                return selectedEditLine;
            }

            string requestedLineKey = DraftStore.GetKey(selectedEditLine);
            if (!string.IsNullOrEmpty(requestedLineKey)
                && RuntimeContainsLineKey(runtimeLines, requestedLineKey))
            {
                return requestedLineKey;
            }

            return string.IsNullOrEmpty(fallbackLineKey) || string.Equals(fallbackLineKey, "__default__", StringComparison.Ordinal)
                ? string.Empty
                : fallbackLineKey;
        }

        private static bool RuntimeContainsLineKey(
            IEnumerable<WorkbenchLineRuntime> runtimeLines,
            string lineKey)
        {
            return RuntimeLineIds(runtimeLines).Contains(lineKey ?? string.Empty);
        }

        private static HashSet<string> RuntimeLineIds(IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            return new HashSet<string>(
                (runtimeLines ?? Array.Empty<WorkbenchLineRuntime>())
                    .Select(runtimeLine => DraftStore.GetKey(runtimeLine?.Id))
                    .Where(lineId => !string.IsNullOrEmpty(lineId)),
                StringComparer.Ordinal);
        }

        private static string RuntimeSourceLineId(WorkbenchLineRuntime runtimeLine)
        {
            if (runtimeLine == null || runtimeLine.Entity == Unity.Entities.Entity.Null)
            {
                return string.Empty;
            }

            return runtimeLine.Entity.Index.ToString();
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
                snapshot = null,
                cleanupInfo = null
            };
        }

        private static HashSet<string> RemovedLineIds(IEnumerable<string> lineIds)
        {
            return new HashSet<string>(
                (lineIds ?? Array.Empty<string>())
                    .Where(lineId => !string.IsNullOrEmpty(lineId))
                    .Select(DraftStore.GetKey),
                StringComparer.Ordinal);
        }

        private static WorkbenchLineRuntime CloneWorkbenchLineRuntime(WorkbenchLineRuntime line)
        {
            if (line == null)
                return null;

            return new WorkbenchLineRuntime
            {
                Entity = line.Entity,
                Id = line.Id ?? string.Empty,
                StableSignature = line.StableSignature ?? string.Empty,
                Name = line.Name ?? string.Empty,
                Kind = string.IsNullOrEmpty(line.Kind) ? "local" : line.Kind,
                TransportType = line.TransportType ?? string.Empty,
                RouteNumber = line.RouteNumber,
                StationCount = line.StationCount,
                Color = line.Color ?? string.Empty,
                OriginStationId = line.OriginStationId ?? string.Empty,
                OriginStationName = line.OriginStationName ?? string.Empty,
                DispatchSupported = line.DispatchSupported,
                UnsupportedReason = line.UnsupportedReason ?? string.Empty,
                OriginStatus = line.OriginStatus ?? string.Empty,
                OriginMessageKey = line.OriginMessageKey ?? string.Empty
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

        private static DispatchWorkbenchStagedRowDto[] RequestRows(DispatchWorkbenchSaveRequest request)
        {
            return request?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
        }

        private Dictionary<string, List<DispatchWorkbenchStagedRowDto>> RowsByDraft(
            DispatchWorkbenchSaveRequest request,
            string fallbackLineKey)
        {
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey =
                new Dictionary<string, List<DispatchWorkbenchStagedRowDto>>(StringComparer.Ordinal);
            string fallbackKey = DraftStore.GetKey(fallbackLineKey);

            if (request?.lineDraftRowsByLineId != null && request.lineDraftRowsByLineId.Length > 0)
            {
                foreach (DispatchWorkbenchLineDraftRowsDto block in request.lineDraftRowsByLineId)
                {
                    string targetKey = DraftStore.GetKey(block?.lineId);
                    if (string.IsNullOrEmpty(targetKey))
                    {
                        targetKey = fallbackKey;
                    }

                    rowsByDraftKey[targetKey] = (block?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>())
                        .Select(m_CopyRow)
                        .ToList();
                }

                return rowsByDraftKey;
            }

            DispatchWorkbenchStagedRowDto[] rows = RequestRows(request);
            if (rows.Length == 0)
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
                return rowsByDraftKey;
            }

            foreach (IGrouping<string, DispatchWorkbenchStagedRowDto> group in rows
                .Where(row => row != null)
                .GroupBy(row => DraftStore.GetKey(string.IsNullOrEmpty(row.lineId) ? fallbackKey : row.lineId), StringComparer.Ordinal))
            {
                rowsByDraftKey[group.Key] = group.Select(m_CopyRow).ToList();
            }

            if (!rowsByDraftKey.ContainsKey(fallbackKey))
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
            }

            return rowsByDraftKey;
        }

        private Dictionary<string, DispatchWorkbenchPlannerImportContractDto> PlanRefs(
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
                        Rows.CopyPlan(entry?.contract);
                    string targetKey = DraftStore.GetKey(
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
                Rows.CopyPlan(request?.plannerImportContract);
            if (fallbackContract == null)
                return refsByDraftKey;

            IEnumerable<string> targetKeys = (fallbackContract.importedLineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Select(DraftStore.GetKey);
            if (!targetKeys.Any())
            {
                targetKeys = (fallbackDraftKeys ?? Array.Empty<string>())
                    .Where(key => !string.IsNullOrEmpty(key))
                    .Select(DraftStore.GetKey);
            }

            foreach (string targetKey in targetKeys
                .Where(key => !string.IsNullOrEmpty(key) && !string.Equals(key, "__default__", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract =
                    Rows.CopyPlan(fallbackContract);
                if (contract == null)
                    continue;

                contract.draftKey = targetKey;
                refsByDraftKey[targetKey] = contract;
            }

            return refsByDraftKey;
        }

        private DispatchWorkbenchPlannerImportContractDto PlanRef(
            string draftKey,
            DispatchWorkbenchPlannerImportContractDto currentRef,
            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> requestRefsByDraftKey,
            bool draftChanged,
            bool hasRows)
        {
            if (requestRefsByDraftKey != null
                && requestRefsByDraftKey.TryGetValue(draftKey, out DispatchWorkbenchPlannerImportContractDto requestedRef))
            {
                DispatchWorkbenchPlannerImportContractDto nextRef = Rows.CopyPlan(requestedRef);
                if (nextRef != null)
                {
                    nextRef.draftKey = draftKey;
                }
                return nextRef;
            }

            if (!hasRows)
                return null;

            return Rows.CopyPlan(currentRef);
        }

        private static HashSet<string> TouchLines(
            string selectedLineId,
            string selectedEditLine,
            List<DispatchWorkbenchStagedRowDto> stagedRows)
        {
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrEmpty(selectedLineId))
                lineIds.Add(selectedLineId);
            if (!string.IsNullOrEmpty(selectedEditLine))
                lineIds.Add(selectedEditLine);

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

        private static string NormalizeDepotId(string depotId)
        {
            return string.IsNullOrWhiteSpace(depotId) ? string.Empty : depotId;
        }

    }
}
