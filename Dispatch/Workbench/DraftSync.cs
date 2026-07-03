using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class DraftSync
    {
        private readonly DraftStore m_Drafts;
        private readonly Func<IReadOnlyDictionary<string, AppliedLine>> m_Applied;
        private readonly Func<string, AppliedLine, string> m_Kind;
        private readonly Func<string, DispatchWorkbenchDraftState> m_NewDraft;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>, bool> m_SameRows;
        private readonly Action m_LoadApplied;

        internal DraftSync(
            DraftStore drafts,
            Func<IReadOnlyDictionary<string, AppliedLine>> appliedLines,
            Func<string, AppliedLine, string> kind,
            Func<string, DispatchWorkbenchDraftState> newDraft,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> copyRow,
            Func<List<DispatchWorkbenchStagedRowDto>, List<DispatchWorkbenchStagedRowDto>, bool> sameRows,
            Action loadApplied)
        {
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_Applied = appliedLines ?? throw new ArgumentNullException(nameof(appliedLines));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            m_NewDraft = newDraft ?? throw new ArgumentNullException(nameof(newDraft));
            m_CopyRow = copyRow ?? throw new ArgumentNullException(nameof(copyRow));
            m_SameRows = sameRows ?? throw new ArgumentNullException(nameof(sameRows));
            m_LoadApplied = loadApplied ?? throw new ArgumentNullException(nameof(loadApplied));
        }

        internal bool Ready()
        {
            m_LoadApplied();
            return Sync();
        }

        internal bool Sync()
        {
            bool changed = false;
            IReadOnlyDictionary<string, AppliedLine> appliedLines = m_Applied();
            HashSet<string> appliedKeys = new HashSet<string>(
                appliedLines
                    .Where(pair => pair.Value?.StagedRows != null && pair.Value.StagedRows.Count > 0)
                    .Select(pair => pair.Key),
                StringComparer.Ordinal);
            Dictionary<string, bool> wasApplied = m_Drafts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.DraftApplied == true,
                StringComparer.Ordinal);

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null)
                    continue;

                if (!appliedKeys.Contains(entry.Key))
                {
                    if (draft.DraftApplied || draft.RulesApplied)
                    {
                        changed = true;
                    }

                    draft.DraftApplied = false;
                    draft.RulesApplied = false;
                }

                draft.AppliedDepartureMinutesCache.Clear();
            }

            foreach (KeyValuePair<string, AppliedLine> entry in appliedLines)
            {
                string lineKey = entry.Key;
                AppliedLine applied = entry.Value;
                if (applied?.StagedRows == null || applied.StagedRows.Count == 0)
                    continue;

                bool existed = m_Drafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft);
                bool canRefresh = !existed
                    || (wasApplied.TryGetValue(lineKey, out bool before) && before);
                if (!existed)
                {
                    draft = m_NewDraft(lineKey);
                    draft.ManualRows.Clear();
                    draft.AutoRules.Clear();
                    m_Drafts[lineKey] = draft;
                    changed = true;
                }

                if (!string.Equals(draft.SelectedLineId ?? string.Empty, lineKey, StringComparison.Ordinal))
                {
                    draft.SelectedLineId = lineKey;
                    changed = true;
                }

                if (string.IsNullOrEmpty(draft.SelectedEditLine))
                {
                    draft.SelectedEditLine = lineKey == "__default__" ? string.Empty : lineKey;
                    changed = true;
                }

                if (canRefresh && !m_SameRows(draft.StagedRows, applied.StagedRows))
                {
                    draft.StagedRows = applied.StagedRows.Select(m_CopyRow).ToList();
                    changed = true;
                }

                if (canRefresh)
                {
                    MatchKind(draft, lineKey, applied);
                }

                if (draft.DraftApplied != canRefresh)
                {
                    draft.DraftApplied = canRefresh;
                    changed = true;
                }

                if (draft.RulesApplied != canRefresh)
                {
                    draft.RulesApplied = canRefresh;
                    changed = true;
                }

                draft.AppliedDepartureMinutesCache.Clear();
            }

            return changed;
        }

        internal void MatchKind(
            DispatchWorkbenchDraftState draft,
            string lineKey,
            AppliedLine applied)
        {
            if (draft == null || string.IsNullOrEmpty(lineKey))
                return;

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

            string serviceKind = m_Kind(lineKey, applied);
            if (string.Equals(serviceKind, "express", StringComparison.Ordinal))
            {
                draft.MergedView.localLineIds = Array.Empty<string>();
                draft.MergedView.localLineId = string.Empty;
                draft.MergedView.expressLineIds = new[] { lineKey };
                draft.MergedView.expressLineId = lineKey;
            }
            else
            {
                draft.MergedView.localLineIds = new[] { lineKey };
                draft.MergedView.localLineId = lineKey;
                draft.MergedView.expressLineIds = Array.Empty<string>();
                draft.MergedView.expressLineId = string.Empty;
            }
        }
    }
}
