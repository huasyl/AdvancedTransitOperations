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
        private readonly Action m_SaveApplied;
        private readonly Func<bool> m_SyncDrafts;
        private readonly Action<string> m_Seed;
        private readonly Action m_MarkTrack;
        private readonly Action<string> m_Log;
        private readonly Action<string, Exception> m_Fault;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_ClonePlan;
        private readonly Func<string, DispatchWorkbenchPlannerImportContractDto> m_PlanFromDraft;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<IEnumerable<string>, string[]> m_RemoveLineCfg;
        private readonly Func<Entity, string> m_StableId;
        private readonly Func<Entity, LineKey> m_StableKey;

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
            Action saveApplied,
            Func<bool> syncDrafts,
            Action<string> seed,
            Action markTrack,
            Action<string> log,
            Action<string, Exception> fault,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> clonePlan,
            Func<string, DispatchWorkbenchPlannerImportContractDto> planFromDraft,
            Func<Entity, Entity> stop,
            Func<IEnumerable<string>, string[]> removeLineCfg,
            Func<Entity, string> stableId,
            Func<Entity, LineKey> stableKey)
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
            m_SaveApplied = saveApplied ?? throw new ArgumentNullException(nameof(saveApplied));
            m_SyncDrafts = syncDrafts ?? throw new ArgumentNullException(nameof(syncDrafts));
            m_Seed = seed ?? throw new ArgumentNullException(nameof(seed));
            m_MarkTrack = markTrack ?? throw new ArgumentNullException(nameof(markTrack));
            m_Log = log ?? throw new ArgumentNullException(nameof(log));
            m_Fault = fault ?? throw new ArgumentNullException(nameof(fault));
            m_ClonePlan = clonePlan ?? throw new ArgumentNullException(nameof(clonePlan));
            m_PlanFromDraft = planFromDraft ?? throw new ArgumentNullException(nameof(planFromDraft));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_RemoveLineCfg = removeLineCfg ?? throw new ArgumentNullException(nameof(removeLineCfg));
            m_StableId = stableId ?? throw new ArgumentNullException(nameof(stableId));
            m_StableKey = stableKey ?? throw new ArgumentNullException(nameof(stableKey));
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
        internal void SaveApplied() => m_SaveApplied();
        internal bool SyncDrafts() => m_SyncDrafts();
        internal void Seed(string lineId) => m_Seed(lineId);
        internal void MarkTrack() => m_MarkTrack();
        internal void Log(string message) => m_Log(message);
        internal void Fault(string scope, Exception ex) => m_Fault(scope, ex);
        internal DispatchWorkbenchPlannerImportContractDto ClonePlan(DispatchWorkbenchPlannerImportContractDto dto) => m_ClonePlan(dto);
        internal DispatchWorkbenchPlannerImportContractDto PlanFromDraft(string key) => m_PlanFromDraft(key);
        internal Entity Stop(Entity waypoint) => m_Stop(waypoint);
        internal string[] RemoveLineCfg(IEnumerable<string> lineIds) => m_RemoveLineCfg(lineIds);
        internal string StableId(Entity line) => m_StableId(line) ?? string.Empty;

        internal LineKey StableKey(Entity line) => m_StableKey(line);
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
        private readonly HashSet<string> m_RemovedAppliedLineIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_RemovedDraftLineIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_RemovedLineSettingIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_CleanupReasons =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_RestoreOrphans =
            new Dictionary<string, string>(StringComparer.Ordinal);

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
        internal IReadOnlyDictionary<string, string> RestoreOrphans => m_RestoreOrphans;
        internal bool Loaded { get; private set; }

        internal void Reset()
        {
            Loaded = false;
            m_Lines.Clear();
            m_PlanRefs.Clear();
            m_Store.Clear();
            m_RestoreOrphans.Clear();
            ClearCleanupInfo();
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
                    if (!CleanupDeletedOrReplacedAppliedLines(saveChanges: false))
                    {
                        Sync(saveDrafts: false);
                    }
                    if (HasRows())
                    {
                        string firstLine = m_Lines.Keys.OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
                        m_Host.Seed(firstLine);
                    }

                    Loaded = true;
                    return true;
                }

                Loaded = true;
                return false;
            }

            m_Lines.Clear();
            m_Store.Clear();
            m_RestoreOrphans.Clear();

            bool filteredAny = false;
            HashSet<Entity> unsupportedLines = new HashSet<Entity>();
            HashSet<Entity> orphanedEntities = new HashSet<Entity>();
            HashSet<string> conflictedStables = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Entity> boundByStable = new Dictionary<string, Entity>(StringComparer.Ordinal);
            try
            {
                if (m_EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
                {
                    var lineBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city, true);
                    for (int i = 0; i < lineBuffer.Length; i++)
                    {
                        AppliedWorkbenchLineStateElement entry = lineBuffer[i];
                        if (entry.m_LineEntity == Entity.Null || !m_EntityManager.Exists(entry.m_LineEntity))
                        {
                            filteredAny = true;
                            RecordOrphan("entity-missing", "entity-missing");
                            continue;
                        }

                        LineDispatchSupport support = DispatchLineEligibility.ComputeDispatchSupport(
                            m_EntityManager, entry.m_LineEntity, waypoint => m_Host.Stop(waypoint));
                        if (!support.Supported)
                        {
                            unsupportedLines.Add(entry.m_LineEntity);
                            filteredAny = true;
                            RecordOrphan(OrphanMark(entry.m_LineEntity), "unsupported");
                            continue;
                        }

                        if (!TryResolveStableId(entry.m_LineEntity, out string key, out string orphanReason))
                        {
                            orphanedEntities.Add(entry.m_LineEntity);
                            filteredAny = true;
                            RecordOrphan(OrphanMark(entry.m_LineEntity), orphanReason);
                            continue;
                        }

                        if (!TryBindStable(
                            boundByStable,
                            conflictedStables,
                            key,
                            entry.m_LineEntity,
                            orphanedEntities))
                        {
                            filteredAny = true;
                            continue;
                        }

                        m_Lines[key] = new AppliedLine
                        {
                            LineEntity = entry.m_LineEntity,
                            OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.Hold(entry.m_OriginHoldLimitMinutes),
                            MaxStationDwellMinutes = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes
                        };
                    }
                }

                if (m_EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
                {
                    var rowBuffer = m_EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city, true);
                    for (int i = 0; i < rowBuffer.Length; i++)
                    {
                        AppliedWorkbenchStagedRowElement row = rowBuffer[i];
                        if (row.m_LineEntity == Entity.Null || !m_EntityManager.Exists(row.m_LineEntity))
                        {
                            filteredAny = true;
                            continue;
                        }

                        if (unsupportedLines.Contains(row.m_LineEntity)
                            || orphanedEntities.Contains(row.m_LineEntity))
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
                            RecordOrphan(OrphanMark(row.m_LineEntity), "unsupported");
                            continue;
                        }

                        if (!TryResolveStableId(row.m_LineEntity, out string key, out string orphanReason))
                        {
                            orphanedEntities.Add(row.m_LineEntity);
                            filteredAny = true;
                            RecordOrphan(OrphanMark(row.m_LineEntity), orphanReason);
                            continue;
                        }

                        if (!TryBindStable(
                            boundByStable,
                            conflictedStables,
                            key,
                            row.m_LineEntity,
                            orphanedEntities))
                        {
                            filteredAny = true;
                            continue;
                        }

                        if (!m_Lines.TryGetValue(key, out AppliedLine line))
                        {
                            line = new AppliedLine
                            {
                                LineEntity = row.m_LineEntity,
                                OriginHoldLimitMinutes = RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes,
                                MaxStationDwellMinutes = RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes
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

                foreach (KeyValuePair<string, AppliedLine> entry in m_Lines)
                {
                    AppliedLine line = entry.Value;
                    string lineId = entry.Key;
                    line.StagedRows = line.StagedRows
                        .OrderBy(row => m_Host.Minutes(row.time))
                        .ThenBy(row => row.id, StringComparer.Ordinal)
                        .Select(m_Host.CopyRow)
                        .ToList();
                    line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, lineId);
                }

                RecoverRows();
                if (!CleanupDeletedOrReplacedAppliedLines(saveChanges: false))
                {
                    Sync(saveDrafts: false);
                }
                if (HasRows())
                {
                    string firstLine = m_Lines.Keys.OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;
                    m_Host.Seed(firstLine);
                }

                m_Host.Log("[AppliedRestore] lines=" + m_Lines.Count
                    + " orphans=" + m_RestoreOrphans.Count);
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

                    if (!TryResolveStableId(runtime.Entity, out string stableKey, out _))
                    {
                        filteredAny = true;
                        continue;
                    }

                    List<DispatchWorkbenchStagedRowDto> rows = group
                        .Select(row =>
                        {
                            DispatchWorkbenchStagedRowDto copy = m_Host.CopyRow(row);
                            if (copy != null)
                                copy.lineId = stableKey;
                            return copy;
                        })
                        .Where(row => row != null)
                        .ToList();
                    if (rows.Count == 0)
                    {
                        continue;
                    }

                    // LineConfigStore has migrated to stable keys; read hold/dwell under stableKey.
                    AppliedLine line = new AppliedLine
                    {
                        LineEntity = runtime.Entity,
                        OriginHoldLimitMinutes = m_Host.Hold(stableKey),
                        MaxStationDwellMinutes = m_Host.Dwell(stableKey),
                        StagedRows = rows
                    };
                    line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, stableKey);
                    m_Lines[stableKey] = line;
                }
            }

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

                List<DispatchWorkbenchStagedRowDto> draftRows = draft.StagedRows
                    .Where(row => row != null
                        && !string.IsNullOrEmpty(row.lineId)
                        && string.Equals(m_Host.DraftKey(row.lineId), key, StringComparison.Ordinal))
                    .ToList();
                if (draftRows.Count == 0 || !runtimeById.TryGetValue(key, out WorkbenchLineRuntime runtime) || runtime == null)
                {
                    RemoveAppliedByDraftOrStable(key, Entity.Null);
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
                    RemoveAppliedByDraftOrStable(key, runtime.Entity);
                    continue;
                }

                if (!TryResolveStableId(runtime.Entity, out string stableKey, out string orphanReason))
                {
                    m_Host.Log($"Line {key} skipped apply: {orphanReason}");
                    RemoveAppliedByDraftOrStable(key, runtime.Entity);
                    continue;
                }

                List<DispatchWorkbenchStagedRowDto> rows = draftRows
                    .Select(row =>
                    {
                        DispatchWorkbenchStagedRowDto copy = m_Host.CopyRow(row);
                        if (copy != null)
                            copy.lineId = stableKey;
                        return copy;
                    })
                    .Where(row => row != null)
                    .ToList();
                if (rows.Count == 0)
                {
                    RemoveAppliedByDraftOrStable(key, runtime.Entity);
                    continue;
                }

                // LineConfigStore has migrated to stable keys; read hold/dwell under stableKey.
                AppliedLine line = new AppliedLine
                {
                    LineEntity = runtime.Entity,
                    OriginHoldLimitMinutes = m_Host.Hold(stableKey),
                    MaxStationDwellMinutes = m_Host.Dwell(stableKey),
                    StagedRows = rows
                };
                line.DepartureMinutesCache = m_Host.BuildMinutes(line.StagedRows, stableKey);
                m_Lines.Remove(key);
                m_Lines[stableKey] = line;
            }

            Sync(saveDrafts: false);
        }

        internal bool CleanupDeletedOrReplacedAppliedLines(bool saveChanges)
        {
            return CleanupLines(
                CollectDeletedOrReplacedLineReasons(
                    BuildRuntimeIndex(m_RuntimeLines()),
                    includeRuntimeMissingReasons: false),
                saveChanges);
        }

        internal Dictionary<string, string> CollectRuntimeMissingLineReasons(
            IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            Dictionary<string, WorkbenchLineRuntime> runtimeById =
                BuildRuntimeIndex(runtimeLines ?? m_RuntimeLines());
            HashSet<string> ownedLineIds = new HashSet<string>(m_Lines.Keys, StringComparer.Ordinal);

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                string draftKey = DraftStore.GetKey(entry.Key);
                DispatchWorkbenchDraftState draft = entry.Value;
                if (!string.IsNullOrEmpty(draftKey)
                    && !string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                    && draft != null
                    && ((draft.ManualRows?.Count ?? 0) > 0
                        || (draft.AutoRules?.Count ?? 0) > 0
                        || (draft.StagedRows?.Count ?? 0) > 0
                        || draft.RulesApplied
                        || draft.DraftApplied
                        || draft.PlannerImportContract != null))
                {
                    ownedLineIds.Add(draftKey);
                }

                CollectOwnedDraftLineIds(ownedLineIds, draft);
            }

            Dictionary<string, string> reasons =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string lineId in ownedLineIds)
            {
                if (!runtimeById.ContainsKey(lineId))
                {
                    reasons[lineId] = "runtime-line-missing";
                }
            }

            return reasons;
        }

        internal bool RemoveDeletedLines(IEnumerable<string> lineIds, bool saveChanges)
        {
            Dictionary<string, string> reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string lineId in (lineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Select(m_Host.DraftKey)
                .Distinct(StringComparer.Ordinal))
            {
                reasons[lineId] = "runtime-line-missing";
            }

            return CleanupLines(reasons, saveChanges);
        }

        internal bool CleanupRequestedLines(
            IReadOnlyDictionary<string, string> reasons,
            bool saveChanges)
        {
            return CleanupLines(reasons, saveChanges);
        }

        internal DispatchWorkbenchCleanupInfoDto ConsumeCleanupInfo()
        {
            if (m_RemovedAppliedLineIds.Count == 0
                && m_RemovedDraftLineIds.Count == 0
                && m_RemovedLineSettingIds.Count == 0
                && m_CleanupReasons.Count == 0)
            {
                return null;
            }

            DispatchWorkbenchCleanupInfoDto info = new DispatchWorkbenchCleanupInfoDto
            {
                removedAppliedLineIds = m_RemovedAppliedLineIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                removedDraftLineIds = m_RemovedDraftLineIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                removedLineSettingIds = m_RemovedLineSettingIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                reasons = m_CleanupReasons
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new DispatchWorkbenchCleanupReasonDto
                    {
                        lineId = entry.Key,
                        reason = entry.Value
                    })
                    .ToArray()
            };
            ClearCleanupInfo();
            return info;
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

                string lineId = entry.Key;
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
                    continue;

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

        private Dictionary<string, string> CollectDeletedOrReplacedLineReasons(
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            bool includeRuntimeMissingReasons = true)
        {
            Dictionary<string, string> reasons =
                new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<Entity, WorkbenchLineRuntime> runtimeByEntity =
                BuildRuntimeEntityIndex(runtimeById);
            HashSet<Entity> appliedEntities = new HashSet<Entity>();

            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines)
            {
                AppliedLine applied = entry.Value;
                if (applied != null
                    && applied.LineEntity != Entity.Null
                    && m_EntityManager.Exists(applied.LineEntity))
                {
                    appliedEntities.Add(applied.LineEntity);
                }
            }

            foreach (KeyValuePair<string, AppliedLine> entry in m_Lines)
            {
                string lineId = entry.Key;
                AppliedLine applied = entry.Value;
                if (string.IsNullOrEmpty(lineId))
                {
                    continue;
                }

                // Applied store keys are stable mode:guid32; match runtime by Entity, not string Id.
                if (applied == null || applied.LineEntity == Entity.Null || !m_EntityManager.Exists(applied.LineEntity))
                {
                    reasons[lineId] = "applied-runtime-entity-missing";
                    continue;
                }

                if (!runtimeByEntity.TryGetValue(applied.LineEntity, out WorkbenchLineRuntime runtime)
                    || runtime == null
                    || runtime.Entity == Entity.Null
                    || !m_EntityManager.Exists(runtime.Entity))
                {
                    if (includeRuntimeMissingReasons)
                    {
                        reasons[lineId] = "runtime-line-missing";
                    }
                    continue;
                }

                if (runtime.Entity != applied.LineEntity)
                {
                    reasons[lineId] = "line-replaced-under-same-lineId";
                }
            }

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                string draftKey = entry.Key;
                DispatchWorkbenchDraftState draft = entry.Value;
                if (string.IsNullOrEmpty(draftKey)
                    || string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                    || draft == null
                    || reasons.ContainsKey(draftKey))
                {
                    continue;
                }

                // Draft keys remain legacy mode:number until Workbench migrates.
                if (!runtimeById.TryGetValue(draftKey, out WorkbenchLineRuntime draftRuntime)
                    || draftRuntime == null)
                {
                    if (includeRuntimeMissingReasons)
                    {
                        reasons[draftKey] = "runtime-line-missing";
                    }
                    continue;
                }

                if (!draft.DraftApplied)
                {
                    continue;
                }

                Entity draftEntity = draftRuntime.Entity;
                bool hasAppliedForEntity = draftEntity != Entity.Null
                    && appliedEntities.Contains(draftEntity);
                bool hasAppliedByKey = m_Lines.ContainsKey(draftKey);
                if (!hasAppliedForEntity && !hasAppliedByKey)
                {
                    reasons[draftKey] = "applied-runtime-entity-missing";
                }
            }

            return reasons;
        }

        private bool CleanupLines(
            IReadOnlyDictionary<string, string> reasons,
            bool saveChanges)
        {
            if (reasons == null || reasons.Count == 0)
            {
                return false;
            }

            HashSet<string> lineIds = new HashSet<string>(
                reasons.Keys.Where(lineId => !string.IsNullOrEmpty(lineId)),
                StringComparer.Ordinal);
            if (lineIds.Count == 0)
            {
                return false;
            }

            bool changed = false;
            foreach (string lineId in lineIds)
            {
                if (m_Lines.Remove(lineId))
                {
                    RecordCleanup(lineId, reasons, removedApplied: true);
                    changed = true;
                }
            }

            if (CleanupDrafts(lineIds, reasons))
            {
                changed = true;
            }

            string[] removedLineSettingIds = m_Host.RemoveLineCfg(lineIds) ?? Array.Empty<string>();
            for (int i = 0; i < removedLineSettingIds.Length; i++)
            {
                RecordCleanup(removedLineSettingIds[i], reasons, removedLineSetting: true);
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            Sync(saveDrafts: false);
            if (saveChanges)
            {
                m_Host.SaveDrafts();
                m_Host.SaveApplied();
            }

            return true;
        }

        private bool CleanupDrafts(
            HashSet<string> lineIds,
            IReadOnlyDictionary<string, string> reasons)
        {
            bool changed = false;
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts.ToArray())
            {
                string draftKey = entry.Key;
                DispatchWorkbenchDraftState draft = entry.Value;
                if (string.IsNullOrEmpty(draftKey) || draft == null)
                {
                    continue;
                }

                if (lineIds.Contains(draftKey))
                {
                    string reason = CleanupReason(reasons, draftKey);
                    if (string.Equals(reason, "runtime-line-missing", StringComparison.Ordinal))
                    {
                        if (m_Drafts.Remove(draftKey))
                        {
                            RecordCleanup(draftKey, reasons, removedDraft: true);
                            changed = true;
                        }
                    }
                    else if (ClearDraftState(draft, draftKey))
                    {
                        RecordCleanup(draftKey, reasons, removedDraft: true);
                        changed = true;
                    }
                    continue;
                }

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

                bool planChanged = false;
                if (TouchesAnyLine(draft.PlannerImportContract, lineIds))
                {
                    draft.PlannerImportContract = null;
                    planChanged = true;
                }

                bool viewChanged = CleanupMergedView(draft.MergedView, lineIds);
                bool selectedChanged = CleanupDraftSelection(draft, draftKey, lineIds);
                bool rowsChanged = manualBefore != (draft.ManualRows?.Count ?? 0)
                    || autoBefore != (draft.AutoRules?.Count ?? 0)
                    || stagedBefore != (draft.StagedRows?.Count ?? 0);

                if (!(rowsChanged || planChanged || viewChanged || selectedChanged))
                {
                    continue;
                }

                if (!HasManualRowsForDraft(draft.ManualRows, draftKey)
                    && !HasAutoRulesForDraft(draft.AutoRules, draftKey)
                    && !HasStagedRowsForDraft(draft.StagedRows, draftKey))
                {
                    draft.RulesApplied = false;
                }
                if (!HasStagedRowsForDraft(draft.StagedRows, draftKey))
                {
                    draft.DraftApplied = false;
                    draft.PlannerImportContract = null;
                }

                draft.AppliedDepartureMinutesCache.Clear();
                changed = true;
            }

            return changed;
        }

        private static bool CleanupMergedView(
            DispatchWorkbenchMergedView view,
            HashSet<string> lineIds)
        {
            if (view == null || lineIds == null || lineIds.Count == 0)
            {
                return false;
            }

            string[] currentLocal = view.localLineIds ?? Array.Empty<string>();
            string[] currentExpress = view.expressLineIds ?? Array.Empty<string>();
            string[] nextLocal = currentLocal
                .Where(lineId => !string.IsNullOrEmpty(lineId) && !lineIds.Contains(lineId))
                .ToArray();
            string[] nextExpress = currentExpress
                .Where(lineId => !string.IsNullOrEmpty(lineId) && !lineIds.Contains(lineId))
                .ToArray();
            bool changed = nextLocal.Length != currentLocal.Length
                || nextExpress.Length != currentExpress.Length
                || lineIds.Contains(view.localLineId ?? string.Empty)
                || lineIds.Contains(view.expressLineId ?? string.Empty);
            if (!changed)
            {
                return false;
            }

            view.localLineIds = nextLocal;
            view.expressLineIds = nextExpress;
            view.localLineId = nextLocal.FirstOrDefault() ?? string.Empty;
            view.expressLineId = nextExpress.FirstOrDefault() ?? string.Empty;
            return true;
        }

        private static bool CleanupDraftSelection(
            DispatchWorkbenchDraftState draft,
            string draftKey,
            HashSet<string> lineIds)
        {
            if (draft == null || lineIds == null || lineIds.Count == 0)
            {
                return false;
            }

            bool changed = false;
            if (lineIds.Contains(draft.SelectedLineId ?? string.Empty))
            {
                draft.SelectedLineId = string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                    ? string.Empty
                    : draftKey;
                changed = true;
            }

            if (lineIds.Contains(draft.SelectedEditLine ?? string.Empty))
            {
                draft.SelectedEditLine = string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                    ? string.Empty
                    : draftKey;
                changed = true;
            }

            return changed;
        }

        private static bool TouchesAnyLine(
            DispatchWorkbenchPlannerImportContractDto contract,
            HashSet<string> lineIds)
        {
            if (contract == null || lineIds == null || lineIds.Count == 0)
            {
                return false;
            }

            if (lineIds.Contains(contract.draftKey ?? string.Empty))
            {
                return true;
            }

            if ((contract.importedLineIds ?? Array.Empty<string>()).Any(lineIds.Contains))
            {
                return true;
            }

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo == null)
            {
                return false;
            }

            return lineIds.Contains(echo.draftKey ?? string.Empty)
                || lineIds.Contains(echo.expressLineId ?? string.Empty)
                || lineIds.Contains(echo.virtualExpressBaseLineId ?? string.Empty)
                || (echo.localLineIds ?? Array.Empty<string>()).Any(lineIds.Contains)
                || (echo.adjustableLineIds ?? Array.Empty<string>()).Any(lineIds.Contains);
        }

        private static void CollectOwnedDraftLineIds(
            HashSet<string> lineIds,
            DispatchWorkbenchDraftState draft)
        {
            if (lineIds == null || draft == null)
            {
                return;
            }

            AddOwnedLineIds(lineIds, draft.ManualRows?.Select(row => row?.lineId));
            AddOwnedLineIds(lineIds, draft.AutoRules?.Select(rule => rule?.lineId));
            AddOwnedLineIds(lineIds, draft.StagedRows?.Select(row => row?.lineId));

            DispatchWorkbenchPlannerImportContractDto contract = draft.PlannerImportContract;
            if (contract == null)
            {
                return;
            }

            AddOwnedLineId(lineIds, contract.draftKey);
            AddOwnedLineIds(lineIds, contract.importedLineIds);

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo == null)
            {
                return;
            }

            AddOwnedLineId(lineIds, echo.draftKey);
            AddOwnedLineId(lineIds, echo.expressLineId);
            AddOwnedLineId(lineIds, echo.virtualExpressBaseLineId);
            AddOwnedLineIds(lineIds, echo.localLineIds);
            AddOwnedLineIds(lineIds, echo.adjustableLineIds);
        }

        private static void AddOwnedLineIds(
            HashSet<string> lineIds,
            IEnumerable<string> sourceLineIds)
        {
            if (lineIds == null || sourceLineIds == null)
            {
                return;
            }

            foreach (string lineId in sourceLineIds)
            {
                AddOwnedLineId(lineIds, lineId);
            }
        }

        private static void AddOwnedLineId(
            HashSet<string> lineIds,
            string lineId)
        {
            string key = DraftStore.GetKey(lineId);
            if (string.IsNullOrEmpty(key)
                || string.Equals(key, "__default__", StringComparison.Ordinal))
            {
                return;
            }

            lineIds.Add(key);
        }

        private static bool HasManualRowsForDraft(
            List<DispatchWorkbenchManualRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(DraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasAutoRulesForDraft(
            List<DispatchWorkbenchAutoRuleDto> rules,
            string draftKey)
        {
            return rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(DraftStore.GetKey(rule.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasStagedRowsForDraft(
            List<DispatchWorkbenchStagedRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(DraftStore.GetKey(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private void RecordCleanup(
            string lineId,
            IReadOnlyDictionary<string, string> reasons,
            bool removedApplied = false,
            bool removedDraft = false,
            bool removedLineSetting = false)
        {
            if (string.IsNullOrEmpty(lineId))
            {
                return;
            }

            if (removedApplied)
            {
                m_RemovedAppliedLineIds.Add(lineId);
            }

            if (removedDraft)
            {
                m_RemovedDraftLineIds.Add(lineId);
            }

            if (removedLineSetting)
            {
                m_RemovedLineSettingIds.Add(lineId);
            }

            if (!m_CleanupReasons.ContainsKey(lineId))
            {
                m_CleanupReasons[lineId] = CleanupReason(reasons, lineId);
            }
        }

        private static string CleanupReason(
            IReadOnlyDictionary<string, string> reasons,
            string lineId)
        {
            return reasons != null && !string.IsNullOrEmpty(lineId) && reasons.TryGetValue(lineId, out string reason)
                ? reason
                : "runtime-line-missing";
        }

        private static bool ClearDraftState(
            DispatchWorkbenchDraftState draft,
            string draftKey)
        {
            if (draft == null)
            {
                return false;
            }

            bool changed = (draft.ManualRows?.Count ?? 0) > 0
                || (draft.AutoRules?.Count ?? 0) > 0
                || (draft.StagedRows?.Count ?? 0) > 0
                || draft.RulesApplied
                || draft.DraftApplied
                || draft.PlannerImportContract != null
                || (draft.MergedView?.localLineIds?.Length ?? 0) > 0
                || (draft.MergedView?.expressLineIds?.Length ?? 0) > 0
                || !string.IsNullOrEmpty(draft.MergedView?.localLineId)
                || !string.IsNullOrEmpty(draft.MergedView?.expressLineId)
                || !string.Equals(draft.SelectedLineId ?? string.Empty, draftKey ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(draft.SelectedEditLine ?? string.Empty, draftKey ?? string.Empty, StringComparison.Ordinal);
            if (!changed)
            {
                draft.AppliedDepartureMinutesCache.Clear();
                return false;
            }

            draft.SelectedLineId = string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                ? string.Empty
                : draftKey ?? string.Empty;
            draft.SelectedEditLine = string.Equals(draftKey, "__default__", StringComparison.Ordinal)
                ? string.Empty
                : draftKey ?? string.Empty;
            draft.MergedView = new DispatchWorkbenchMergedView
            {
                localLineIds = Array.Empty<string>(),
                expressLineIds = Array.Empty<string>(),
                isLoop = true,
                turnbackStationId = string.Empty,
                direction = "up",
                windowStart = string.Empty,
                windowEnd = string.Empty
            };
            draft.ManualRows = new List<DispatchWorkbenchManualRowDto>();
            draft.AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
            draft.StagedRows = new List<DispatchWorkbenchStagedRowDto>();
            draft.RulesApplied = false;
            draft.DraftApplied = false;
            draft.PlannerImportContract = null;
            draft.AppliedDepartureMinutesCache.Clear();
            return true;
        }

        private void ClearCleanupInfo()
        {
            m_RemovedAppliedLineIds.Clear();
            m_RemovedDraftLineIds.Clear();
            m_RemovedLineSettingIds.Clear();
            m_CleanupReasons.Clear();
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

        private static Dictionary<Entity, WorkbenchLineRuntime> BuildRuntimeEntityIndex(
            Dictionary<string, WorkbenchLineRuntime> runtimeById)
        {
            Dictionary<Entity, WorkbenchLineRuntime> byEntity = new Dictionary<Entity, WorkbenchLineRuntime>();
            if (runtimeById == null)
                return byEntity;

            foreach (KeyValuePair<string, WorkbenchLineRuntime> entry in runtimeById)
            {
                WorkbenchLineRuntime runtime = entry.Value;
                if (runtime == null || runtime.Entity == Entity.Null)
                    continue;

                if (!byEntity.ContainsKey(runtime.Entity))
                    byEntity[runtime.Entity] = runtime;
            }

            return byEntity;
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

        private bool TryResolveStableId(Entity line, out string stableId, out string reason)
        {
            stableId = string.Empty;
            reason = string.Empty;
            if (line == Entity.Null || !m_EntityManager.Exists(line))
            {
                reason = "entity-missing";
                return false;
            }

            // Catalog resolver is the sole stable-identity path. Empty = isolated/missing.
            LineKey key = m_Host.StableKey(line);
            if (LineKey.IsStableGuidKey(key))
            {
                stableId = LineIdentityService.GetId(key);
                return !string.IsNullOrEmpty(stableId);
            }

            string id = m_Host.StableId(line);
            if (!string.IsNullOrEmpty(id) && LineKey.IsStableGuidId(id))
            {
                stableId = id;
                return true;
            }

            reason = string.IsNullOrEmpty(id) && key.IsEmpty ? "missing-lak" : "invalid-lak";
            return false;
        }

        private bool TryBindStable(
            Dictionary<string, Entity> boundByStable,
            HashSet<string> conflictedStables,
            string stableId,
            Entity line,
            HashSet<Entity> orphanedEntities)
        {
            if (string.IsNullOrEmpty(stableId) || line == Entity.Null)
                return false;

            if (conflictedStables.Contains(stableId))
            {
                orphanedEntities.Add(line);
                return false;
            }

            if (boundByStable.TryGetValue(stableId, out Entity existing))
            {
                if (existing == line)
                    return true;

                m_Lines.Remove(stableId);
                boundByStable.Remove(stableId);
                conflictedStables.Add(stableId);
                orphanedEntities.Add(existing);
                orphanedEntities.Add(line);
                RecordOrphan(stableId, "duplicate-lak");
                return false;
            }

            boundByStable[stableId] = line;
            return true;
        }

        private void RecordOrphan(string mark, string reason)
        {
            if (string.IsNullOrEmpty(mark) || string.IsNullOrEmpty(reason))
                return;

            if (!m_RestoreOrphans.ContainsKey(mark))
                m_RestoreOrphans[mark] = reason;
        }

        private static string OrphanMark(Entity line)
        {
            return line == Entity.Null
                ? "entity-missing"
                : "entity-" + line.Index.ToString();
        }

        private void RemoveAppliedByDraftOrStable(string draftKey, Entity line)
        {
            if (!string.IsNullOrEmpty(draftKey))
                m_Lines.Remove(draftKey);

            if (line != Entity.Null
                && TryResolveStableId(line, out string stableKey, out _)
                && !string.IsNullOrEmpty(stableKey))
            {
                m_Lines.Remove(stableKey);
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
