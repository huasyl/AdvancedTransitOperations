using System;
using System.Collections.Generic;
using System.Linq;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Drafts
    {
        private readonly Action m_Load;
        private readonly Clock m_Clock;
        private readonly Func<string, string> m_Kind;

        internal Drafts(
            DraftStore store,
            Action load,
            Clock clock,
            Func<string, string> kind)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Load = load ?? throw new ArgumentNullException(nameof(load));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        }

        internal DraftStore Store { get; }

        internal static string Key(string lineId)
        {
            return DraftStore.GetKey(lineId);
        }

        internal string Preferred()
        {
            return Store.ResolvePreferredLineId();
        }

        internal string Preferred(TransitMode mode)
        {
            return Store.ResolvePreferredLineId(mode);
        }

        internal void SetPreferred(string lineId)
        {
            Store.SetPreferredLineId(lineId);
        }

        internal void SetPreferred(string lineId, TransitMode mode)
        {
            Store.SetPreferredLineId(lineId, mode);
        }

        internal DispatchWorkbenchDraftState Get(string lineKey)
        {
            m_Load();
            if (!Store.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft))
            {
                draft = New(lineKey);
                Store[lineKey] = draft;
            }

            return draft;
        }

        internal DispatchWorkbenchDraftState New(string lineKey)
        {
            DispatchWorkbenchMergedView view = new DispatchWorkbenchMergedView
            {
                localLineIds = Array.Empty<string>(),
                expressLineIds = Array.Empty<string>(),
                isLoop = true,
                turnbackStationId = string.Empty,
                direction = "up"
            };
            EnsureView(view);

            return new DispatchWorkbenchDraftState
            {
                SelectedLineId = lineKey,
                SelectedEditLine = lineKey == "__default__" ? string.Empty : lineKey,
                MergedView = view,
                DraftApplied = false
            };
        }

        internal void Collect(
            string lineKey,
            HashSet<string> validLineIds,
            List<DispatchWorkbenchManualRowDto> manualRows,
            List<DispatchWorkbenchAutoRuleDto> autoRules,
            HashSet<string> manualIds,
            HashSet<string> autoIds)
        {
            if (string.IsNullOrEmpty(lineKey))
                return;

            if (!Store.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft) || draft == null)
                return;

            if (draft.ManualRows != null)
            {
                foreach (DispatchWorkbenchManualRowDto row in draft.ManualRows)
                {
                    if (row != null
                        && validLineIds.Contains(row.lineId ?? string.Empty)
                        && manualIds.Add(row.id ?? string.Empty))
                    {
                        manualRows.Add(Rows.CopyManual(row));
                    }
                }
            }

            if (draft.AutoRules != null)
            {
                foreach (DispatchWorkbenchAutoRuleDto rule in draft.AutoRules)
                {
                    if (rule != null
                        && validLineIds.Contains(rule.lineId ?? string.Empty)
                        && autoIds.Add(rule.id ?? string.Empty))
                    {
                        autoRules.Add(Rows.CopyRule(rule));
                    }
                }
            }
        }

        internal DispatchWorkbenchLineDraftRowsDto[] RowsByLine(HashSet<string> validLineIds)
        {
            List<DispatchWorkbenchLineDraftRowsDto> blocks = new List<DispatchWorkbenchLineDraftRowsDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in Store.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string draftKey = entry.Key;
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft?.StagedRows == null || draft.StagedRows.Count == 0)
                    continue;

                List<DispatchWorkbenchStagedRowDto> rows = draft.StagedRows
                    .Where(row => row != null
                        && !string.IsNullOrEmpty(row.lineId)
                        && string.Equals(Key(row.lineId), draftKey, StringComparison.Ordinal)
                        && (validLineIds == null || validLineIds.Contains(row.lineId)))
                    .Select(Rows.CopyRow)
                    .OrderBy(row => Time.Parse(row.time))
                    .ThenBy(row => row.id, StringComparer.Ordinal)
                    .ToList();
                if (rows.Count == 0)
                    continue;

                blocks.Add(new DispatchWorkbenchLineDraftRowsDto
                {
                    lineId = rows[0].lineId,
                    lineDraftRows = Rows.LastById(rows).ToArray()
                });
            }

            return blocks.ToArray();
        }

        internal void EnsureView(
            DispatchWorkbenchDraftState draft,
            List<WorkbenchLineRuntime> lines,
            WorkbenchLineRuntime active)
        {
            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            Check.SplitKinds(
                draft.MergedView,
                lines,
                null,
                active ?? lines.FirstOrDefault(),
                Rows.Ids,
                RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind,
                m_Kind);
            if (string.IsNullOrEmpty(draft.MergedView.turnbackStationId))
            {
                draft.MergedView.turnbackStationId = string.Empty;
            }

            draft.MergedView.direction = string.IsNullOrEmpty(draft.MergedView.direction)
                ? "up"
                : draft.MergedView.direction;
            EnsureView(draft.MergedView);
        }

        internal void EnsureView(DispatchWorkbenchMergedView view)
        {
            m_Clock.Window(view);
        }
    }
}
