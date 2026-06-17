using System;
using System.Collections.Generic;
using System.Linq;
using RapidTransitMod.Dispatch.Workbench;
using Unity.Entities;
using static RapidTransitMod.Dispatch.Workbench.Rows;

namespace RapidTransitMod.Dispatch
{
    internal sealed class AppliedPort
    {
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<string, string> m_DraftKey;
        private readonly Func<string, int> m_Hold;
        private readonly Func<string, int> m_Dwell;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<string, int> m_Minutes;
        private readonly Func<int, string> m_Slot;
        private readonly Func<byte, string> m_Note;
        private readonly Func<IEnumerable<DispatchWorkbenchStagedRowDto>, string, int[]> m_BuildMinutes;
        private readonly Action m_SaveDrafts;
        private readonly Func<bool> m_SyncDrafts;
        private readonly Action<string> m_Seed;
        private readonly Action m_MarkTrack;
        private readonly Action<string> m_Log;
        private readonly Action<string, Exception> m_Fault;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_ClonePlan;
        private readonly Func<string, DispatchWorkbenchPlannerImportContractDto> m_PlanFromDraft;
        private readonly Func<Entity, Entity> m_Stop;

        internal AppliedPort(
            Func<Entity, string> lineId,
            Func<string, string> draftKey,
            Func<string, int> hold,
            Func<string, int> dwell,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> copyRow,
            Func<string, int> minutes,
            Func<int, string> slot,
            Func<byte, string> note,
            Func<IEnumerable<DispatchWorkbenchStagedRowDto>, string, int[]> buildMinutes,
            Action saveDrafts,
            Func<bool> syncDrafts,
            Action<string> seed,
            Action markTrack,
            Action<string> log,
            Action<string, Exception> fault,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> clonePlan,
            Func<string, DispatchWorkbenchPlannerImportContractDto> planFromDraft,
            Func<Entity, Entity> stop)
        {
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_DraftKey = draftKey ?? throw new ArgumentNullException(nameof(draftKey));
            m_Hold = hold ?? throw new ArgumentNullException(nameof(hold));
            m_Dwell = dwell ?? throw new ArgumentNullException(nameof(dwell));
            m_CopyRow = copyRow ?? throw new ArgumentNullException(nameof(copyRow));
            m_Minutes = minutes ?? throw new ArgumentNullException(nameof(minutes));
            m_Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            m_Note = note ?? throw new ArgumentNullException(nameof(note));
            m_BuildMinutes = buildMinutes ?? throw new ArgumentNullException(nameof(buildMinutes));
            m_SaveDrafts = saveDrafts ?? throw new ArgumentNullException(nameof(saveDrafts));
            m_SyncDrafts = syncDrafts ?? throw new ArgumentNullException(nameof(syncDrafts));
            m_Seed = seed ?? throw new ArgumentNullException(nameof(seed));
            m_MarkTrack = markTrack ?? throw new ArgumentNullException(nameof(markTrack));
            m_Log = log ?? throw new ArgumentNullException(nameof(log));
            m_Fault = fault ?? throw new ArgumentNullException(nameof(fault));
            m_ClonePlan = clonePlan ?? throw new ArgumentNullException(nameof(clonePlan));
            m_PlanFromDraft = planFromDraft ?? throw new ArgumentNullException(nameof(planFromDraft));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
        }

        internal string LineId(Entity line) => m_LineId(line);
        internal string DraftKey(string lineId) => m_DraftKey(lineId);
        internal int Hold(string lineId) => m_Hold(lineId);
        internal int Dwell(string lineId) => m_Dwell(lineId);
        internal DispatchWorkbenchStagedRowDto CopyRow(DispatchWorkbenchStagedRowDto row) => m_CopyRow(row);
        internal int Minutes(string time) => m_Minutes(time);
        internal string Slot(int minute) => m_Slot(minute);
        internal string Note(byte source) => m_Note(source);
        internal int[] BuildMinutes(IEnumerable<DispatchWorkbenchStagedRowDto> rows, string lineId) => m_BuildMinutes(rows, lineId);
        internal void SaveDrafts() => m_SaveDrafts();
        internal bool SyncDrafts() => m_SyncDrafts();
        internal void Seed(string lineId) => m_Seed(lineId);
        internal void MarkTrack() => m_MarkTrack();
        internal void Log(string message) => m_Log(message);
        internal void Fault(string scope, Exception ex) => m_Fault(scope, ex);
        internal DispatchWorkbenchPlannerImportContractDto ClonePlan(DispatchWorkbenchPlannerImportContractDto dto) => m_ClonePlan(dto);
        internal DispatchWorkbenchPlannerImportContractDto PlanFromDraft(string key) => m_PlanFromDraft(key);
        internal Entity Stop(Entity waypoint) => m_Stop(waypoint);
    }

    internal sealed class AppliedTimetable
    {
        private readonly EntityManager m_EntityManager;
        private readonly Func<Entity> m_City;
        private readonly DraftStore m_Drafts;
        private readonly AppliedTimetableStore m_Store;
        private readonly Config m_Cfg;
        private readonly Func<List<WorkbenchLineRuntime>> m_RuntimeLines;
        private readonly AppliedPort m_Host;
        private readonly Dictionary<string, AppliedLine> m_Lines =
            new Dictionary<string, AppliedLine>(StringComparer.Ordinal);
        private readonly Dictionary<string, DispatchWorkbenchPlannerImportContractDto> m_PlanRefs =
            new Dictionary<string, DispatchWorkbenchPlannerImportContractDto>(StringComparer.Ordinal);

        internal AppliedTimetable(
            EntityManager entityManager,
            Func<Entity> city,
            DraftStore drafts,
            AppliedTimetableStore store,
            Config cfg,
            Func<List<WorkbenchLineRuntime>> lines,
            AppliedPort host)
        {
            m_EntityManager = entityManager;
            m_City = city ?? throw new ArgumentNullException(nameof(city));
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_Store = store ?? throw new ArgumentNullException(nameof(store));
            m_Cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            m_RuntimeLines = lines ?? throw new ArgumentNullException(nameof(lines));
            m_Host = host ?? throw new ArgumentNullException(nameof(host));
        }

        internal IReadOnlyDictionary<string, AppliedLine> Lines => m_Lines;
        internal IReadOnlyDictionary<string, DispatchWorkbenchPlannerImportContractDto> Refs => m_PlanRefs;
        internal bool Loaded { get; private set; }

        internal void Reset()
        {
            Loaded = false;
            m_Lines.Clear();
            m_PlanRefs.Clear();
            m_Store.Clear();
        }

        internal bool Load()
        {
            if (Loaded)
            {
                return true;
            }

            m_PlanRefs.Clear();
            Entity city = m_City();
            if (city == Entity.Null)
            {
                m_Lines.Clear();
                m_Store.Clear();
                return false;
            }

            if (!m_EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city)
                && !m_EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                m_Lines.Clear();
                m_Store.Clear();
                if (HasDraftRows())
                {
                    Backfill();
                    Loaded = true;
                    return true;
                }

                Loaded = true;
                return false;
            }

            m_Lines.Clear();
            m_Store.Clear();

            bool filteredAny = false;
            HashSet<Entity> unsupportedLines = new HashSet<Entity>();
            try
            {
                if (m_EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
                {
                    var lineBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city, true);
                    for (int i = 0; i < lineBuffer.Length; i++)
                    {
                        AppliedWorkbenchLineStateElement entry = lineBuffer[i];
                        if (entry.m_LineEntity == Entity.Null)
                        {
                            continue;
                        }

                        LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(
                            m_EntityManager, entry.m_LineEntity, waypoint => m_Host.Stop(waypoint));
                        if (!support.Supported)
                        {
                            unsupportedLines.Add(entry.m_LineEntity);
                            filteredAny = true;
                            continue;
                        }

                        string lineId = m_Host.LineId(entry.m_LineEntity);
                        string key = m_Host.DraftKey(lineId);
                        m_Lines[key] = new AppliedLine
                        {
                            LineEntity = entry.m_LineEntity,
                            OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.Hold(entry.m_OriginHoldLimitMinutes),
                            MaxStationDwellMinutes = m_Host.Dwell(key)
                        };
                    }
                }

                if (m_EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
                {
                    var rowBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city, true);
                    for (int i = 0; i < rowBuffer.Length; i++)
                    {
                        AppliedWorkbenchStagedRowElement row = rowBuffer[i];
                        if (row.m_LineEntity == Entity.Null)
                        {
                            continue;
                        }

                        if (unsupportedLines.Contains(row.m_LineEntity))
                        {
                            filteredAny = true;
                            continue;
                        }

                        LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(
                            m_EntityManager, row.m_LineEntity, waypoint => m_Host.Stop(waypoint));
                        if (!support.Supported)
                        {
                            unsupportedLines.Add(row.m_LineEntity);
                            filteredAny = true;
                            continue;
                        }

                        string lineId = m_Host.LineId(row.m_LineEntity);
                        string key = m_Host.DraftKey(lineId);
                        if (!m_Lines.TryGetValue(key, out AppliedLine line))
                        {
                            line = new AppliedLine
                            {
                                LineEntity = row.m_LineEntity,
                                OriginHoldLimitMinutes = m_Host.Hold(key),
                                MaxStationDwellMinutes = m_Host.Dwell(key)
                            };
                            m_Lines[key] = line;
                        }

                        line.StagedRows.Add(new DispatchWorkbenchStagedRowDto
                        {
                            id = "applied-" + key + "-" + row.m_Order.ToString(),
                            lineId = key,
                            time = m_Host.Slot(row.m_Minute),
                            kind = DecodeKind(row.m_KindCode),
                            source = DecodeSource(row.m_SourceCode),
                            note = m_Host.Note(row.m_SourceCode)
                        });
                    }
                }

                foreach (AppliedLine line in m_Lines.Values)
                {
                    string lineId = m_Host.DraftKey(m_Host.LineId(line.LineEntity));
                    line.StagedRows = line.StagedRows
                        .OrderBy(row => m_Host.Minutes(row.time))
                        .ThenBy(row => row.id, StringComparer.Ordinal)
                        .Select(m_Host.CopyRow)
                        .ToList();
                    line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, lineId);
                }

                RecoverRows();
                Sync(saveDrafts: false);
                if (HasRows())
                {
                    string firstLine = m_Lines.Keys.OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
                    m_Host.Seed(firstLine);
                }

                m_Host.Log("[AppliedRestore] lines=" + m_Lines.Count);
                Loaded = true;
                if (filteredAny)
                    m_Host.Log("[AppliedRestore] filtered unsupported lines during load; persistence deferred");
                return true;
            }
            catch (Exception ex)
            {
                m_Host.Fault("Applied.Load", ex);
                Loaded = true;
                return false;
            }
        }

        internal void Save()
        {
            Entity city = m_City();
            if (city == Entity.Null)
            {
                return;
            }

            Write(LineElems(), RowElems());
        }

        internal void Backfill()
        {
            Dictionary<string, WorkbenchLineRuntime> runtimeById = BuildRuntimeIndex(m_RuntimeLines());
            m_Lines.Clear();
            bool filteredAny = false;
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied || draft.StagedRows == null || draft.StagedRows.Count == 0)
                {
                    continue;
                }

                foreach (IGrouping<string, DispatchWorkbenchStagedRowDto> group in draft.StagedRows
                    .Where(row => row != null && !string.IsNullOrEmpty(row.lineId))
                    .GroupBy(row => row.lineId, StringComparer.Ordinal))
                {
                    string key = group.Key;
                    if (!runtimeById.TryGetValue(key, out WorkbenchLineRuntime runtime))
                    {
                        continue;
                    }

                    LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(
                        m_EntityManager, runtime.Entity, waypoint => m_Host.Stop(waypoint));
                    if (!support.Supported)
                    {
                        filteredAny = true;
                        continue;
                    }

                    List<DispatchWorkbenchStagedRowDto> rows = group.Select(m_Host.CopyRow).ToList();
                    if (rows.Count == 0)
                    {
                        continue;
                    }

                    AppliedLine line = new AppliedLine
                    {
                        LineEntity = runtime.Entity,
                        OriginHoldLimitMinutes = m_Host.Hold(key),
                        MaxStationDwellMinutes = m_Host.Dwell(key),
                        StagedRows = rows
                    };
                    line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, key);
                    m_Lines[key] = line;
                }
            }

            Sync(saveDrafts: false);
            if (filteredAny)
                m_Host.Log("[AppliedBackfill] filtered unsupported lines; persistence deferred");
        }

        internal void ApplyDraft(IEnumerable<string> draftKeys, List<WorkbenchLineRuntime> runtimeLines)
        {
            Dictionary<string, WorkbenchLineRuntime> runtimeById = BuildRuntimeIndex(runtimeLines ?? m_RuntimeLines());
            HashSet<string> keys = new HashSet<string>(
                (draftKeys ?? Array.Empty<string>())
                    .Where(key => !string.IsNullOrEmpty(key))
                    .Select(m_Host.DraftKey),
                StringComparer.Ordinal);

            foreach (string key in keys)
            {
                if (!m_Drafts.TryGetValue(key, out DispatchWorkbenchDraftState draft)
                    || draft == null
                    || draft.StagedRows == null)
                {
                    m_Lines.Remove(key);
                    continue;
                }

                List<DispatchWorkbenchStagedRowDto> rows = draft.StagedRows
                    .Where(row => row != null
                        && !string.IsNullOrEmpty(row.lineId)
                        && string.Equals(m_Host.DraftKey(row.lineId), key, StringComparison.Ordinal))
                    .Select(m_Host.CopyRow)
                    .ToList();
                if (rows.Count == 0 || !runtimeById.TryGetValue(key, out WorkbenchLineRuntime runtime) || runtime == null)
                {
                    m_Lines.Remove(key);
                    continue;
                }

                LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(
                    m_EntityManager, runtime.Entity, waypoint => m_Host.Stop(waypoint));
                if (!runtime.DispatchSupported || !support.Supported)
                {
                    string reason = !string.IsNullOrEmpty(runtime.UnsupportedReason)
                        ? runtime.UnsupportedReason
                        : support.Reason;
                    m_Host.Log($"Line {key} removed from applied timetable: {reason}");
                    m_Lines.Remove(key);
                    continue;
                }

                AppliedLine line = new AppliedLine
                {
                    LineEntity = runtime.Entity,
                    OriginHoldLimitMinutes = m_Host.Hold(key),
                    MaxStationDwellMinutes = m_Host.Dwell(key),
                    StagedRows = rows
                };
                line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, key);
                m_Lines[key] = line;
            }

            Sync(saveDrafts: false);
        }

        internal void RecoverRows()
        {
            Dictionary<string, Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>> rowsByLineAndKey =
                new Dictionary<string, Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied || draft.StagedRows == null || draft.StagedRows.Count == 0)
                {
                    continue;
                }

                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    if (row == null || string.IsNullOrEmpty(row.lineId))
                    {
                        continue;
                    }

                    if (!rowsByLineAndKey.TryGetValue(row.lineId, out Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>> rowQueues))
                    {
                        rowQueues = new Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>(StringComparer.Ordinal);
                        rowsByLineAndKey[row.lineId] = rowQueues;
                    }

                    string key = MatchKey(row);
                    if (!rowQueues.TryGetValue(key, out Queue<DispatchWorkbenchStagedRowDto> queue))
                    {
                        queue = new Queue<DispatchWorkbenchStagedRowDto>();
                        rowQueues[key] = queue;
                    }

                    queue.Enqueue(m_Host.CopyRow(row));
                }
            }

            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines)
            {
                AppliedLine line = entry.Value;
                if (line == null || line.StagedRows == null || line.StagedRows.Count == 0)
                {
                    continue;
                }

                string lineId = m_Host.DraftKey(m_Host.LineId(line.LineEntity));
                if (string.IsNullOrEmpty(lineId)
                    || !rowsByLineAndKey.TryGetValue(lineId, out Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>> rowQueues))
                {
                    continue;
                }

                for (int i = 0; i < line.StagedRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = line.StagedRows[i];
                    string key = MatchKey(row);
                    if (!rowQueues.TryGetValue(key, out Queue<DispatchWorkbenchStagedRowDto> queue)
                        || queue.Count == 0)
                    {
                        continue;
                    }

                    DispatchWorkbenchStagedRowDto restored = queue.Dequeue();
                    if (!string.IsNullOrEmpty(restored.id))
                    {
                        row.id = restored.id;
                    }
                    if (!string.IsNullOrEmpty(restored.note))
                    {
                        row.note = restored.note;
                    }
                }
            }
        }

        internal void RefreshPlans()
        {
            m_PlanRefs.Clear();

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied)
                {
                    continue;
                }

                DispatchWorkbenchPlannerImportContractDto contract = m_Host.ClonePlan(m_Host.PlanFromDraft(entry.Key));
                if (contract == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(contract.draftKey))
                {
                    contract.draftKey = entry.Key;
                }

                m_PlanRefs[entry.Key] = contract;
            }
        }

        internal DispatchWorkbenchStagedRowDto[] Rows()
        {
            return m_Lines
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => entry.Value?.StagedRows ?? new List<DispatchWorkbenchStagedRowDto>())
                .Where(row => row != null)
                .Select(m_Host.CopyRow)
                .ToArray();
        }

        internal DispatchWorkbenchPlanRefDto[] PlanRefs()
        {
            return m_PlanRefs
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new DispatchWorkbenchPlanRefDto
                {
                    lineId = entry.Key,
                    contract = m_Host.ClonePlan(entry.Value)
                })
                .ToArray();
        }

        internal void RefreshCfg()
        {
            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines)
            {
                AppliedLine line = entry.Value;
                if (line == null)
                {
                    continue;
                }

                line.OriginHoldLimitMinutes = m_Host.Hold(entry.Key);
                line.MaxStationDwellMinutes = m_Host.Dwell(entry.Key);
            }
        }

        internal List<AppliedWorkbenchLineStateElement> LineElems()
        {
            List<AppliedWorkbenchLineStateElement> elems = new List<AppliedWorkbenchLineStateElement>();
            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedLine line = entry.Value;
                if (line == null || line.LineEntity == Entity.Null || line.StagedRows == null || line.StagedRows.Count == 0)
                {
                    continue;
                }

                elems.Add(new AppliedWorkbenchLineStateElement
                {
                    m_LineEntity = line.LineEntity,
                    m_OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.Hold(line.OriginHoldLimitMinutes)
                });
            }

            return elems;
        }

        internal List<AppliedWorkbenchStagedRowElement> RowElems()
        {
            List<AppliedWorkbenchStagedRowElement> elems = new List<AppliedWorkbenchStagedRowElement>();
            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedLine line = entry.Value;
                if (line == null || line.LineEntity == Entity.Null || line.StagedRows == null)
                {
                    continue;
                }

                for (int i = 0; i < line.StagedRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = line.StagedRows[i];
                    int minute = m_Host.Minutes(row?.time);
                    if (minute < 0)
                    {
                        continue;
                    }

                    elems.Add(new AppliedWorkbenchStagedRowElement
                    {
                        m_LineEntity = line.LineEntity,
                        m_Order = i,
                        m_Minute = minute,
                        m_KindCode = EncodeKind(row?.kind),
                        m_SourceCode = EncodeSource(row?.source)
                    });
                }
            }

            return elems;
        }

        internal void Write(
            List<AppliedWorkbenchLineStateElement> lineElems,
            List<AppliedWorkbenchStagedRowElement> rowElems)
        {
            Entity city = m_City();
            if (city == Entity.Null)
            {
                return;
            }

            EnsureBuffers(city);
            var lineBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city);
            var rowBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city);
            lineBuffer.Clear();
            rowBuffer.Clear();

            if (lineElems != null)
            {
                for (int i = 0; i < lineElems.Count; i++)
                {
                    lineBuffer.Add(lineElems[i]);
                }
            }

            if (rowElems != null)
            {
                for (int i = 0; i < rowElems.Count; i++)
                {
                    rowBuffer.Add(rowElems[i]);
                }
            }
        }

        private bool HasDraftRows()
        {
            return m_Drafts.Values.Any(draft =>
                draft != null
                && draft.DraftApplied
                && draft.StagedRows != null
                && draft.StagedRows.Count > 0);
        }

        private bool HasRows()
        {
            return m_Lines.Values.Any(line => line?.StagedRows != null && line.StagedRows.Count > 0);
        }

        private void Sync(bool saveDrafts)
        {
            m_Cfg.SyncApplied(m_Lines);
            RefreshPlans();
            bool changed = m_Host.SyncDrafts();
            if (saveDrafts && changed)
            {
                m_Host.SaveDrafts();
            }

            m_Host.MarkTrack();
        }

        private static Dictionary<string, WorkbenchLineRuntime> BuildRuntimeIndex(IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            return (runtimeLines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .GroupBy(line => line.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        private void EnsureBuffers(Entity city)
        {
            if (!m_EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
            {
                m_EntityManager.AddBuffer<AppliedWorkbenchLineStateElement>(city);
            }

            if (!m_EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                m_EntityManager.AddBuffer<AppliedWorkbenchStagedRowElement>(city);
            }
        }

        private static string DecodeKind(byte code)
        {
            return code == 1 ? RuntimeConfigStoreDefaults.ExpressServiceKind : RuntimeConfigStoreDefaults.LocalServiceKind;
        }

        private static string DecodeSource(byte code)
        {
            return code switch
            {
                1 => "manual",
                2 => "auto",
                3 => "planner",
                _ => string.Empty
            };
        }

        private static byte EncodeKind(string kind)
        {
            return string.Equals(kind, RuntimeConfigStoreDefaults.ExpressServiceKind, StringComparison.Ordinal)
                ? (byte)1
                : (byte)0;
        }

        private static byte EncodeSource(string source)
        {
            if (string.Equals(source, "manual", StringComparison.Ordinal))
            {
                return 1;
            }

            if (string.Equals(source, "auto", StringComparison.Ordinal))
            {
                return 2;
            }

            if (string.Equals(source, "planner", StringComparison.Ordinal))
            {
                return 3;
            }

            return 0;
        }
    }
}
