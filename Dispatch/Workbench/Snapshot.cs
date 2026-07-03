using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Snapshot
    {
        private readonly Query m_Query;
        private readonly DraftStore m_Drafts;
        private readonly Func<string, DispatchWorkbenchDraftState> m_GetOrCreateDraft;
        private readonly Action<string> m_SetPreferredLineId;
        private readonly Func<string> m_GetPreferredLineId;
        private readonly Action<DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, WorkbenchLineRuntime> m_EnsureView;
        private readonly Action<string, HashSet<string>, List<DispatchWorkbenchManualRowDto>, List<DispatchWorkbenchAutoRuleDto>, HashSet<string>, HashSet<string>> m_CollectDraftRules;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<Entity, int> m_GetOriginHoldLimitMinutes;
        private readonly Func<Entity, int> m_GetMaxStationDwellMinutes;
        private readonly Func<Entity, string> m_GetAllowedDepotId;
        private readonly Action<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, List<DispatchWorkbenchTripDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_LogSnapshot;
        private readonly Action<string, WorkbenchLineRuntime, string, DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> m_WriteReport;
        private readonly Func<RuntimeFeatureSettingsDto> m_Features;

        internal Snapshot(
            Query query,
            Drafts drafts,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<Entity, int> getOriginHoldLimitMinutes,
            Func<Entity, int> getMaxStationDwellMinutes,
            Func<Entity, string> getAllowedDepotId,
            Action<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, List<DispatchWorkbenchTripDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> logSnapshot,
            Action<string, WorkbenchLineRuntime, string, DispatchWorkbenchDraftState, List<WorkbenchLineRuntime>, List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>> writeReport,
            Func<RuntimeFeatureSettingsDto> features)
        {
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
            if (drafts == null)
            {
                throw new ArgumentNullException(nameof(drafts));
            }
            m_Drafts = drafts.Store;
            m_GetOrCreateDraft = drafts.Get;
            m_SetPreferredLineId = drafts.SetPreferred;
            m_GetPreferredLineId = drafts.Preferred;
            m_EnsureView = drafts.EnsureView;
            m_CollectDraftRules = drafts.Collect;
            m_CopyRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_GetOriginHoldLimitMinutes = getOriginHoldLimitMinutes ?? throw new ArgumentNullException(nameof(getOriginHoldLimitMinutes));
            m_GetMaxStationDwellMinutes = getMaxStationDwellMinutes ?? throw new ArgumentNullException(nameof(getMaxStationDwellMinutes));
            m_GetAllowedDepotId = getAllowedDepotId ?? throw new ArgumentNullException(nameof(getAllowedDepotId));
            m_LogSnapshot = logSnapshot ?? throw new ArgumentNullException(nameof(logSnapshot));
            m_WriteReport = writeReport ?? throw new ArgumentNullException(nameof(writeReport));
            m_Features = features ?? throw new ArgumentNullException(nameof(features));
        }

        internal DispatchWorkbenchSnapshot Build(
            string preferredLineId,
            ulong snapshotVersion,
            string sourceMode)
        {
            return Build(preferredLineId, TransitMode.Unknown, snapshotVersion, sourceMode);
        }

        internal DispatchWorkbenchSnapshot Build(
            string preferredLineId,
            TransitMode mode,
            ulong snapshotVersion,
            string sourceMode)
        {
            List<WorkbenchLineRuntime> runtimeLines = m_Query.GetLines(mode);
            WorkbenchLineRuntime activeRuntime =
                m_Query.ResolveActiveLine(runtimeLines, preferredLineId, m_Drafts.ResolvePreferredLineId(mode), mode);
            string draftKey = DraftStore.GetKey(activeRuntime?.Id);
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
                m_Drafts.SetPreferredLineId(activeRuntime.Id, mode);
            }

            m_EnsureView(draft, runtimeLines, activeRuntime);
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
            List<DispatchWorkbenchDepotDto> depots = GetDepotsForLines(runtimeLines);
            DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId =
                m_Query.GetDraftRows(validRuntimeLineIds);
            DispatchWorkbenchStagedRowDto[] canonicalCombinedDraftRows =
                lineDraftRowsByLineId
                    .SelectMany(block => block?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>())
                    .Select(m_CopyRow)
                    .ToArray();
            DispatchWorkbenchStagedRowDto[] canonicalActiveLineDraftRows =
                lineDraftRowsByLineId
                    .FirstOrDefault(block => string.Equals(block?.lineId, activeRuntime?.Id ?? string.Empty, StringComparison.Ordinal))
                    ?.lineDraftRows
                    ?.Select(m_CopyRow)
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

            DispatchWorkbenchSnapshot snapshot = new DispatchWorkbenchSnapshot
            {
                mode = TransitModeCodec.Format(mode),
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
                draftApplied = draft.DraftApplied,
                featureSettings = m_Features()
            };
            return snapshot;
        }

        internal DispatchWorkbenchSnapshot Meta(
            string preferredLineId,
            ulong snapshotVersion,
            string sourceMode)
        {
            return Meta(preferredLineId, TransitMode.Unknown, snapshotVersion, sourceMode);
        }

        internal DispatchWorkbenchSnapshot Meta(
            string preferredLineId,
            TransitMode mode,
            ulong snapshotVersion,
            string sourceMode)
        {
            List<WorkbenchLineRuntime> runtimeLines = m_Query.GetLines(mode);
            List<DispatchWorkbenchDepotDto> depots = GetDepotsForLines(runtimeLines);

            DispatchWorkbenchSnapshot snapshot = new DispatchWorkbenchSnapshot
            {
                mode = TransitModeCodec.Format(mode),
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
                draftApplied = false,
                featureSettings = m_Features()
            };
            return snapshot;
        }

        private List<DispatchWorkbenchDepotDto> GetDepotsForLines(List<WorkbenchLineRuntime> runtimeLines)
        {
            if (runtimeLines == null || runtimeLines.Count == 0)
                return new List<DispatchWorkbenchDepotDto>();

            HashSet<string> transportTypes = new HashSet<string>(
                runtimeLines
                    .Where(line => line != null && !string.IsNullOrEmpty(line.TransportType))
                    .Select(line => line.TransportType),
                StringComparer.Ordinal);
            if (transportTypes.Count == 0)
                return new List<DispatchWorkbenchDepotDto>();

            return m_Query.GetDepots()
                .Where(depot => depot != null
                    && !string.IsNullOrEmpty(depot.transportType)
                    && transportTypes.Contains(depot.transportType))
                .ToList();
        }
    }
}
