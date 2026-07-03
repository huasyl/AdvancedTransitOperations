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
        private readonly Func<DispatchWorkbenchManualRowDto, DispatchWorkbenchManualRowDto> m_CopyManual;
        private readonly Func<DispatchWorkbenchAutoRuleDto, DispatchWorkbenchAutoRuleDto> m_CopyRule;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_LastById;
        internal Commands(
            Host host,
            Drafts drafts,
            Query query,
            Snapshot snap,
            Persist persist)
        {
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
            m_Run = host.Run;
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
            m_Snap = snap ?? throw new ArgumentNullException(nameof(snap));
            m_Persist = persist ?? throw new ArgumentNullException(nameof(persist));
            m_CopyManual = Rows.CopyManual;
            m_CopyRule = Rows.CopyRule;
            m_CopyRow = Rows.CopyRow;
            m_LastById = Rows.LastById;
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
                    result.snapshot = BuildSnapshot(prepared.Scope, prepared.Request.selectedLineId);
                }
                return result;
            }

            DispatchWorkbenchSaveRequest request = prepared.Request;
            List<WorkbenchLineRuntime> runtimeLines = prepared.RuntimeLines ?? m_Host.Lines();
            string lineKey = DraftStore.GetKey(request?.selectedLineId);
            DispatchWorkbenchDraftState state = m_Drafts.Get(lineKey);
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> nextLineDraftRowsByKey =
                RowsByDraft(request, lineKey);
            List<DispatchWorkbenchManualRowDto> nextManualRows = request.manualRows != null
                ? request.manualRows.Select(m_CopyManual).ToList()
                : new List<DispatchWorkbenchManualRowDto>();
            List<DispatchWorkbenchAutoRuleDto> nextAutoRules = request.autoRules != null
                ? request.autoRules.Select(m_CopyRule).ToList()
                : new List<DispatchWorkbenchAutoRuleDto>();
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
                    result.snapshot = BuildSnapshot(prepared.Scope, request.selectedLineId);
                    return result;
                }
            }

            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> nextPlanRefsByKey =
                PlanRefs(
                    request,
                    nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }));
            string requestedSelectedLineId = string.IsNullOrEmpty(request.selectedLineId) ? lineKey : request.selectedLineId;
            string requestedSelectedEditLine = string.IsNullOrEmpty(request.selectedEditLine) ? "local" : request.selectedEditLine;
            bool hasAdditionalLineDraftTargets = nextLineDraftRowsByKey.Keys
                .Any(key => !string.Equals(key, lineKey, StringComparison.Ordinal));
            bool rulesChanged = !Rows.SameManual(state.ManualRows, nextManualRows)
                || !Rows.SameRules(state.AutoRules, nextAutoRules)
                || !Rows.SameRows(state.StagedRows, nextStagedRows);
            DispatchWorkbenchPlannerImportContractDto nextPlanRef = PlanRef(
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
                && Rows.SameView(state.MergedView, request.mergedView)
                && Rows.SameManual(state.ManualRows, nextManualRows)
                && Rows.SameRules(state.AutoRules, nextAutoRules)
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
            state.ManualRows = nextManualRows;
            state.AutoRules = nextAutoRules;
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

            RemoveOther(
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
                result.snapshot = BuildSnapshot(prepared.Scope, state.SelectedLineId);
                m_Host.Ui.Push(result.snapshot);
            }
            else
            {
                result.snapshot = null;
            }
            return result;
        }

        private DispatchWorkbenchSnapshot BuildSnapshot(ModeScope scope, string lineId)
        {
            return m_Snap.Build(lineId, scope.Mode, m_Host.Version(), "game-backend");
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
            NormalizeManualRows(scope, request.manualRows, errors);
            NormalizeAutoRules(scope, request.autoRules, errors);
            NormalizeStagedRows(scope, request.lineDraftRows, "lineDraftRows", errors);
            NormalizeLineDraftRowBlocks(scope, request.lineDraftRowsByLineId, errors);
            NormalizeLineSettings(scope, request.lineSettings, errors);
            NormalizePlanRefs(scope, request.planRefs, errors);
            NormalizePlanContract(scope, request.plannerImportContract, "plannerImportContract", errors);
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

        private static void NormalizeManualRows(
            ModeScope scope,
            DispatchWorkbenchManualRowDto[] rows,
            List<string> errors)
        {
            if (rows == null)
                return;

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null)
                    continue;

                rows[i].lineId = NormalizeLineId(scope, rows[i].lineId, "manualRows[" + i + "].lineId", errors);
            }
        }

        private static void NormalizeAutoRules(
            ModeScope scope,
            DispatchWorkbenchAutoRuleDto[] rules,
            List<string> errors)
        {
            if (rules == null)
                return;

            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i] == null)
                    continue;

                rules[i].lineId = NormalizeLineId(scope, rules[i].lineId, "autoRules[" + i + "].lineId", errors);
            }
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
                draft.StagedRows = nextRows;
                draft.RulesApplied = markDraftApplied;
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

        private static string NormalizeDepotId(string depotId)
        {
            return string.IsNullOrWhiteSpace(depotId) ? string.Empty : depotId;
        }

    }
}
