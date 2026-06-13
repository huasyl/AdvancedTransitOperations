using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Query
    {
        private readonly DraftStore m_Drafts;
        private readonly AppliedTimetableStore m_AppliedTimetables;
        private readonly IReadOnlyDictionary<string, AppliedLine> m_Applied;
        private readonly Func<List<WorkbenchLineRuntime>> m_BuildLines;
        private readonly Func<List<DispatchWorkbenchDepotDto>> m_BuildDepots;
        private readonly Func<Entity, List<DispatchWorkbenchStationDto>> m_BuildStations;
        private readonly Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchTripDto>> m_BuildTrips;
        private readonly Func<HashSet<string>, DispatchWorkbenchLineDraftRowsDto[]> m_BuildLineDraftRows;
        private readonly Func<WorkbenchLineRuntime, WorkbenchLineRuntime> m_CloneLine;
        private readonly Func<DispatchWorkbenchDepotDto, DispatchWorkbenchDepotDto> m_CloneDepot;
        private readonly Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> m_CopyRow;
        private readonly Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> m_ClonePlanRef;
        private readonly Func<byte, string> m_BuildAppliedRowNote;
        private readonly Func<LineKey, string> m_GetStoreLineId;
        private readonly Func<int, string> m_Slot;
        private readonly Func<List<DispatchWorkbenchStagedRowDto>, string, bool> m_HasRowsForDraft;

        public Query(
            DraftStore drafts,
            AppliedTimetableStore appliedTimetables,
            IReadOnlyDictionary<string, AppliedLine> applied,
            Func<List<WorkbenchLineRuntime>> buildLines,
            Func<List<DispatchWorkbenchDepotDto>> buildDepots,
            Func<Entity, List<DispatchWorkbenchStationDto>> buildStations,
            Func<WorkbenchLineRuntime, List<DispatchWorkbenchStationDto>, DispatchWorkbenchDraftState, List<DispatchWorkbenchTripDto>> buildTrips,
            Func<HashSet<string>, DispatchWorkbenchLineDraftRowsDto[]> buildLineDraftRows,
            Func<WorkbenchLineRuntime, WorkbenchLineRuntime> cloneLine,
            Func<DispatchWorkbenchDepotDto, DispatchWorkbenchDepotDto> cloneDepot,
            Func<DispatchWorkbenchStagedRowDto, DispatchWorkbenchStagedRowDto> cloneStagedRow,
            Func<DispatchWorkbenchPlannerImportContractDto, DispatchWorkbenchPlannerImportContractDto> clonePlanRef,
            Func<byte, string> buildAppliedRowNote,
            Func<LineKey, string> getStoreLineId,
            Func<int, string> slot,
            Func<List<DispatchWorkbenchStagedRowDto>, string, bool> hasRowsForDraft)
        {
            m_Drafts = drafts ?? throw new ArgumentNullException(nameof(drafts));
            m_AppliedTimetables = appliedTimetables ?? throw new ArgumentNullException(nameof(appliedTimetables));
            m_Applied = applied ?? throw new ArgumentNullException(nameof(applied));
            m_BuildLines = buildLines ?? throw new ArgumentNullException(nameof(buildLines));
            m_BuildDepots = buildDepots ?? throw new ArgumentNullException(nameof(buildDepots));
            m_BuildStations = buildStations ?? throw new ArgumentNullException(nameof(buildStations));
            m_BuildTrips = buildTrips ?? throw new ArgumentNullException(nameof(buildTrips));
            m_BuildLineDraftRows = buildLineDraftRows ?? throw new ArgumentNullException(nameof(buildLineDraftRows));
            m_CloneLine = cloneLine ?? throw new ArgumentNullException(nameof(cloneLine));
            m_CloneDepot = cloneDepot ?? throw new ArgumentNullException(nameof(cloneDepot));
            m_CopyRow = cloneStagedRow ?? throw new ArgumentNullException(nameof(cloneStagedRow));
            m_ClonePlanRef = clonePlanRef ?? throw new ArgumentNullException(nameof(clonePlanRef));
            m_BuildAppliedRowNote = buildAppliedRowNote ?? throw new ArgumentNullException(nameof(buildAppliedRowNote));
            m_GetStoreLineId = getStoreLineId ?? throw new ArgumentNullException(nameof(getStoreLineId));
            m_Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            m_HasRowsForDraft = hasRowsForDraft ?? throw new ArgumentNullException(nameof(hasRowsForDraft));
        }

        public List<WorkbenchLineRuntime> GetLines()
        {
            return (m_BuildLines() ?? new List<WorkbenchLineRuntime>())
                .Where(line => line != null)
                .Select(m_CloneLine)
                .ToList();
        }

        internal static WorkbenchLineRuntime CopyLine(WorkbenchLineRuntime line)
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

        internal static DispatchWorkbenchDepotDto CopyDepot(DispatchWorkbenchDepotDto depot)
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

        public List<WorkbenchLineRuntime> GetLines(TransitMode mode)
        {
            if (mode == TransitMode.Unknown)
                return GetLines();

            return GetLines()
                .Where(line => MatchesMode(line?.Id, mode))
                .ToList();
        }

        public List<DispatchWorkbenchDepotDto> GetDepots()
        {
            return (m_BuildDepots() ?? new List<DispatchWorkbenchDepotDto>())
                .Where(depot => depot != null)
                .Select(m_CloneDepot)
                .ToList();
        }

        public WorkbenchLineRuntime ResolveActiveLine(
            List<WorkbenchLineRuntime> lines,
            string preferredLineId,
            string savedLineId)
        {
            if (lines == null || lines.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(preferredLineId))
            {
                WorkbenchLineRuntime exact =
                    lines.FirstOrDefault(line => string.Equals(line?.Id, preferredLineId, StringComparison.Ordinal));
                if (exact != null)
                    return exact;
            }

            if (!string.IsNullOrEmpty(savedLineId))
            {
                WorkbenchLineRuntime saved =
                    lines.FirstOrDefault(line => string.Equals(line?.Id, savedLineId, StringComparison.Ordinal));
                if (saved != null)
                    return saved;
            }

            return lines[0];
        }

        public WorkbenchLineRuntime ResolveActiveLine(
            List<WorkbenchLineRuntime> lines,
            string preferredLineId,
            string savedLineId,
            TransitMode mode)
        {
            return ResolveActiveLine(
                lines,
                LineIdentityService.NormalizeForMode(preferredLineId, mode),
                LineIdentityService.NormalizeForMode(savedLineId, mode));
        }

        public HashSet<string> BuildValidLineIds(IEnumerable<WorkbenchLineRuntime> runtimeLines)
        {
            return new HashSet<string>(
                (runtimeLines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                    .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                    .Select(line => line.Id),
                StringComparer.Ordinal);
        }

        public HashSet<string> BuildValidLineIds(
            IEnumerable<WorkbenchLineRuntime> runtimeLines,
            TransitMode mode)
        {
            if (mode == TransitMode.Unknown)
                return BuildValidLineIds(runtimeLines);

            return new HashSet<string>(
                (runtimeLines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                    .Where(line => line != null
                        && !string.IsNullOrEmpty(line.Id)
                        && MatchesMode(line.Id, mode))
                    .Select(line => line.Id),
                StringComparer.Ordinal);
        }

        public List<DispatchWorkbenchStationDto> GetStations(
            WorkbenchLineRuntime activeRuntime)
        {
            if (activeRuntime == null)
                return new List<DispatchWorkbenchStationDto>();

            return m_BuildStations(activeRuntime.Entity) ?? new List<DispatchWorkbenchStationDto>();
        }

        public List<DispatchWorkbenchTripDto> GetTrips(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            if (activeRuntime == null)
                return new List<DispatchWorkbenchTripDto>();

            return m_BuildTrips(
                    activeRuntime,
                    stations ?? new List<DispatchWorkbenchStationDto>(),
                    draft)
                ?? new List<DispatchWorkbenchTripDto>();
        }

        public DispatchWorkbenchLineDraftRowsDto[] GetDraftRows(HashSet<string> validRuntimeLineIds)
        {
            return m_BuildLineDraftRows(validRuntimeLineIds)
                ?? Array.Empty<DispatchWorkbenchLineDraftRowsDto>();
        }

        public DispatchWorkbenchLineDto[] BuildLineDtos(
            IEnumerable<WorkbenchLineRuntime> runtimeLines,
            Func<Entity, int> getOriginHoldLimitMinutes,
            Func<Entity, int> getMaxStationDwellMinutes,
            Func<Entity, string> getAllowedDepotId)
        {
            return (runtimeLines ?? Enumerable.Empty<WorkbenchLineRuntime>())
                .Where(line => line != null)
                .Select(line => new DispatchWorkbenchLineDto
                {
                    id = line.Id,
                    sourceLineId = line.Entity.Index.ToString(),
                    name = line.Name,
                    kind = line.Kind,
                    direction = "up",
                    stationCount = line.StationCount,
                    color = line.Color,
                    originStationId = line.OriginStationId,
                    originStationName = line.OriginStationName,
                    originHoldLimitMinutes = getOriginHoldLimitMinutes(line.Entity),
                    maxStationDwellMinutes = getMaxStationDwellMinutes(line.Entity),
                    transportType = line.TransportType,
                    allowedDepotId = getAllowedDepotId(line.Entity),
                    dispatchSupported = line.DispatchSupported,
                    unsupportedReason = line.UnsupportedReason,
                    originStatus = line.OriginStatus,
                    originMessageKey = line.OriginMessageKey
                })
                .ToArray();
        }

        public DispatchWorkbenchStagedRowDto[] BuildAppliedRows()
        {
            return BuildAppliedRows(TransitMode.Unknown);
        }

        public DispatchWorkbenchStagedRowDto[] BuildAppliedRows(TransitMode mode)
        {
            List<DispatchWorkbenchStagedRowDto> rows = m_AppliedTimetables.GetAll(mode)
                .OrderBy(entry => m_GetStoreLineId(entry.Key), StringComparer.Ordinal)
                .SelectMany(entry =>
                {
                    string lineId = m_GetStoreLineId(entry.Key);
                    AppliedTimetableRow[] appliedRows = entry.Value?.AppliedRows ?? Array.Empty<AppliedTimetableRow>();
                    if (appliedRows.Length == 0)
                    {
                        return (entry.Value?.DepartureMinutes ?? Array.Empty<int>())
                            .Select((minute, index) => new DispatchWorkbenchStagedRowDto
                            {
                                id = "applied-" + lineId + "-" + index.ToString(),
                                lineId = lineId,
                                time = m_Slot(minute),
                                kind = entry.Value?.ServiceKind ?? string.Empty,
                                source = string.Empty,
                                note = string.Empty
                            });
                    }

                    return appliedRows.Select((row, index) => new DispatchWorkbenchStagedRowDto
                    {
                        id = string.IsNullOrEmpty(row.RowId) ? "applied-" + lineId + "-" + index.ToString() : row.RowId,
                        lineId = lineId,
                        time = m_Slot(row.DepartureMinute),
                        kind = row.ServiceKind ?? string.Empty,
                        source = row.Source ?? string.Empty,
                        note = string.IsNullOrEmpty(row.Source)
                            ? string.Empty
                            : m_BuildAppliedRowNote(EncodeAppliedRowSource(row.Source))
                    });
                })
                .Where(row => row != null && !string.IsNullOrEmpty(row.lineId))
                .ToList();
            if (rows.Count > 0 || m_Applied.Count == 0)
            {
                return rows.ToArray();
            }

            return m_Applied
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Where(entry => mode == TransitMode.Unknown || MatchesMode(entry.Key, mode))
                .SelectMany(entry => entry.Value?.StagedRows ?? new List<DispatchWorkbenchStagedRowDto>())
                .Where(row => row != null)
                .Select(m_CopyRow)
                .ToArray();
        }

        public DispatchWorkbenchPlanRefDto[] BuildPlanRefs()
        {
            return BuildPlanRefs(TransitMode.Unknown);
        }

        public DispatchWorkbenchPlanRefDto[] BuildPlanRefs(TransitMode mode)
        {
            return m_Drafts
                .Where(entry =>
                    entry.Value?.PlannerImportContract != null
                    && m_HasRowsForDraft(entry.Value.StagedRows, entry.Key)
                    && !string.IsNullOrEmpty(entry.Key)
                    && !string.Equals(entry.Key, "__default__", StringComparison.Ordinal)
                    && (mode == TransitMode.Unknown || MatchesMode(entry.Key, mode)))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new DispatchWorkbenchPlanRefDto
                {
                    lineId = entry.Key,
                    contract = m_ClonePlanRef(entry.Value.PlannerImportContract)
                })
                .ToArray();
        }

        private static bool MatchesMode(string lineId, TransitMode mode)
        {
            return LineIdentityService.GetKey(lineId, mode).Mode == mode;
        }

        private static byte EncodeAppliedRowSource(string source)
        {
            if (string.Equals(source, "manual", StringComparison.Ordinal))
                return 1;
            if (string.Equals(source, "auto", StringComparison.Ordinal))
                return 2;
            if (string.Equals(source, "planner", StringComparison.Ordinal))
                return 3;
            return 0;
        }
    }
}
