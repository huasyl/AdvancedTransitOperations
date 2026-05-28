using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class WorkbenchSnapshotBuilder
    {
        private readonly WorkbenchQueryService m_Query;
        private readonly WorkbenchDraftStore m_Drafts;
        private readonly Func<string, DispatchWorkbenchDraftState> m_GetOrCreateDraft;
        private readonly Action<string> m_SetPreferredLineId;
        private readonly Func<string> m_GetPreferredLineId;
        private readonly Action<DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, WorkbenchLineRuntime> m_EnsureMergedViewDefaults;
        private readonly Action<string, HashSet<string>, List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchAutoRuleDto>, HashSet<string>, HashSet<string>> m_CollectDraftRules;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CloneStagedRow;
        private readonly Func<Entity, int> m_GetOriginHoldLimitMinutes;
        private readonly Func<Entity, int> m_GetMaxStationDwellMinutes;
        private readonly Func<Entity, string> m_GetAllowedDepotId;
        private readonly Action<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, List<DispatchWorkbenchTripDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_LogSnapshot;
        private readonly Action<string, WorkbenchLineRuntime, string, DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_WriteReport;

        public WorkbenchSnapshotBuilder(
            WorkbenchQueryService query,
            WorkbenchDraftStore drafts,
            Func<string, DispatchWorkbenchDraftState> getOrCreateDraft,
            Action<string> setPreferredLineId,
            Func<string> getPreferredLineId,
            Action<DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, WorkbenchLineRuntime> ensureMergedViewDefaults,
            Action<string, HashSet<string>, List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchAutoRuleDto>, HashSet<string>, HashSet<string>> collectDraftRules,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<Entity, int> getOriginHoldLimitMinutes,
            Func<Entity, int> getMaxStationDwellMinutes,
            Func<Entity, string> getAllowedDepotId,
            Action<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, List<DispatchWorkbenchTripDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> logSnapshot,
            Action<string, WorkbenchLineRuntime, string, DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> writeReport)
        {
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_GetOrCreateDraft = getOrCreateDraft ?? throw new ArgumentNullException(nameof(getOrCreateDraft));
            m_SetPreferredLineId = setPreferredLineId ?? throw new ArgumentNullException(nameof(setPreferredLineId));
            m_GetPreferredLineId = getPreferredLineId ?? throw new ArgumentNullException(nameof(getPreferredLineId));
            m_EnsureMergedViewDefaults = ensureMergedViewDefaults ?? throw new ArgumentNullException(nameof(ensureMergedViewDefaults));
            m_CollectDraftRules = collectDraftRules ?? throw new ArgumentNullException(nameof(collectDraftRules));
            m_CloneStagedRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_GetOriginHoldLimitMinutes = getOriginHoldLimitMinutes ?? throw new ArgumentNullException(nameof(getOriginHoldLimitMinutes));
            m_GetMaxStationDwellMinutes = getMaxStationDwellMinutes ?? throw new ArgumentNullException(nameof(getMaxStationDwellMinutes));
            m_GetAllowedDepotId = getAllowedDepotId ?? throw new ArgumentNullException(nameof(getAllowedDepotId));
            m_LogSnapshot = logSnapshot ?? throw new ArgumentNullException(nameof(logSnapshot));
            m_WriteReport = writeReport ?? throw new ArgumentNullException(nameof(writeReport));
        }

        public DispatchWorkbenchSnapshot Build(
            string preferredLineId,
            ulong snapshotVersion,
            string sourceMode)
        {
            return Build(preferredLineId, TransitMode.Unknown, snapshotVersion, sourceMode);
        }

        public DispatchWorkbenchSnapshot Build(
            string preferredLineId,
            TransitMode mode,
            ulong snapshotVersion,
            string sourceMode)
        {
            List<WorkbenchLineRuntime> runtimeLines = m_Query.GetLines(mode);
            WorkbenchLineRuntime activeRuntime =
                m_Query.ResolveActiveLine(runtimeLines, preferredLineId, m_GetPreferredLineId(), mode);
            string draftKey = WorkbenchDraftStore.GetKey(activeRuntime?.Id);
            DispatchWorkbenchDraftState draft = m_GetOrCreateDraft(draftKey);
            List<DispatchWorkbenchManualRowDto> mergedManualRows =
                new List<DispatchWorkbenchManualRowDto>();
            List<DispatchWorkbenchAutoRuleDto> mergedAutoRules =
                new List<DispatchWorkbenchAutoRuleDto>();
            HashSet<string> mergedManualRowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mergedAutoRuleIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> validRuntimeLineIds = m_Query.BuildValidLineIds(runtimeLines, mode);
            List<DispatchWorkbenchStationDto> stations = m_Query.GetStations(activeRuntime);

            if (string.IsNullOrEmpty(draft.SelectedLineId) && activeRuntime != null)
            {
                draft.SelectedLineId = activeRuntime.Id;
            }

            if (activeRuntime != null)
            {
                m_SetPreferredLineId(activeRuntime.Id);
            }

            m_EnsureMergedViewDefaults(draft, runtimeLines, activeRuntime);
            m_CollectDraftRules(
                draftKey,
                validRuntimeLineIds,
                mergedManualRows,
                mergedAutoRules,
                mergedManualRowIds,
                mergedAutoRuleIds);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in
                m_Drafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.Equals(entry.Key, draftKey, StringComparison.Ordinal))
                    continue;

                m_CollectDraftRules(
                    entry.Key,
                    validRuntimeLineIds,
                    mergedManualRows,
                    mergedAutoRules,
                    mergedManualRowIds,
                    mergedAutoRuleIds);
            }

            List<DispatchWorkbenchTripDto> trips =
                m_Query.GetTrips(activeRuntime, stations, draft);
            List<DispatchWorkbenchDepotDto> depots = m_Query.GetDepots();
            DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId =
                m_Query.GetDraftRows(validRuntimeLineIds);
            DispatchWorkbenchStagedRowDto[] canonicalCombinedDraftRows =
                lineDraftRowsByLineId
                    .SelectMany(block => block?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>())
                    .Select(m_CloneStagedRow)
                    .ToArray();
            DispatchWorkbenchStagedRowDto[] canonicalActiveLineDraftRows =
                lineDraftRowsByLineId
                    .FirstOrDefault(block => string.Equals(block?.lineId, activeRuntime?.Id ?? string.Empty, StringComparison.Ordinal))
                    ?.lineDraftRows
                    ?.Select(m_CloneStagedRow)
                    .ToArray()
                ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRowsForReport =
                canonicalActiveLineDraftRows.ToList();
            List<DispatchWorkbenchStagedRowDto> combinedDraftRowsForReport =
                canonicalCombinedDraftRows.ToList();

            m_LogSnapshot(
                activeRuntime,
                stations,
                trips,
                draft,
                activeLineDraftRowsForReport,
                combinedDraftRowsForReport);
            m_WriteReport(
                "snapshot",
                activeRuntime,
                draftKey,
                draft,
                runtimeLines,
                activeLineDraftRowsForReport,
                combinedDraftRowsForReport);

            return new DispatchWorkbenchSnapshot
            {
                selectedLineId = draft.SelectedLineId,
                selectedEditLine = draft.SelectedEditLine,
                mergedView = draft.MergedView,
                lines = m_Query.BuildLineDtos(
                    runtimeLines,
                    m_GetOriginHoldLimitMinutes,
                    m_GetMaxStationDwellMinutes,
                    m_GetAllowedDepotId),
                depots = depots.ToArray(),
                stations = stations.ToArray(),
                trips = trips.ToArray(),
                manualRows = mergedManualRows.ToArray(),
                autoRules = mergedAutoRules.ToArray(),
                lineDraftRows = canonicalActiveLineDraftRows,
                lineDraftRowsByLineId = lineDraftRowsByLineId,
                combinedDraftRows = canonicalCombinedDraftRows,
                appliedRows = m_Query.BuildAppliedRows(mode),
                planRefs = m_Query.BuildPlanRefs(mode),
                version = snapshotVersion.ToString(),
                sourceMode = sourceMode ?? string.Empty,
                rulesApplied = draft.RulesApplied,
                draftApplied = draft.DraftApplied
            };
        }

        public DispatchWorkbenchSnapshot BuildMetadata(
            string preferredLineId,
            ulong snapshotVersion,
            string sourceMode)
        {
            return BuildMetadata(preferredLineId, TransitMode.Unknown, snapshotVersion, sourceMode);
        }

        public DispatchWorkbenchSnapshot BuildMetadata(
            string preferredLineId,
            TransitMode mode,
            ulong snapshotVersion,
            string sourceMode)
        {
            List<WorkbenchLineRuntime> runtimeLines = m_Query.GetLines(mode);
            List<DispatchWorkbenchDepotDto> depots = m_Query.GetDepots();

            return new DispatchWorkbenchSnapshot
            {
                selectedLineId = preferredLineId ?? string.Empty,
                selectedEditLine = preferredLineId ?? string.Empty,
                mergedView = new DispatchWorkbenchMergedView(),
                lines = m_Query.BuildLineDtos(
                    runtimeLines,
                    m_GetOriginHoldLimitMinutes,
                    m_GetMaxStationDwellMinutes,
                    m_GetAllowedDepotId),
                depots = depots.ToArray(),
                stations = Array.Empty<DispatchWorkbenchStationDto>(),
                trips = Array.Empty<DispatchWorkbenchTripDto>(),
                manualRows = Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                lineDraftRows = Array.Empty<DispatchWorkbenchStagedRowDto>(),
                lineDraftRowsByLineId = Array.Empty<DispatchWorkbenchLineDraftRowsDto>(),
                combinedDraftRows = Array.Empty<DispatchWorkbenchStagedRowDto>(),
                appliedRows = m_Query.BuildAppliedRows(mode),
                planRefs = m_Query.BuildPlanRefs(mode),
                version = snapshotVersion.ToString(),
                sourceMode = sourceMode ?? string.Empty,
                rulesApplied = false,
                draftApplied = false
            };
        }
    }
}
