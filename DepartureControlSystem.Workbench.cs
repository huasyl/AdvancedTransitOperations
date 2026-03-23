using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Colossal.Serialization.Entities;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private static readonly FieldInfo s_NameTypeField =
            typeof(NameSystem.Name).GetField("m_NameType", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameIdField =
            typeof(NameSystem.Name).GetField("m_NameID", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameArgsField =
            typeof(NameSystem.Name).GetField("m_NameArgs", BindingFlags.Instance | BindingFlags.NonPublic);

        [DataContract]
        public class DispatchWorkbenchSnapshot
        {
            [DataMember] 
            public string selectedLineId;
            [DataMember]
            public string selectedEditLine;
            [DataMember]
            public DispatchWorkbenchMergedView mergedView;
            [DataMember]
            public DispatchWorkbenchLineDto[] lines;
            [DataMember]
            public DispatchWorkbenchDepotDto[] depots;
            [DataMember]
            public DispatchWorkbenchStationDto[] stations;
            [DataMember]
            public DispatchWorkbenchTripDto[] trips;
            [DataMember]
            public DispatchWorkbenchManualRowDto[] manualRows;
            [DataMember]
            public DispatchWorkbenchAutoRuleDto[] autoRules;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public string version;
            [DataMember]
            public string sourceMode;
            [DataMember]
            public bool rulesApplied;
            [DataMember]
            public bool draftApplied;
        }

        [DataContract]
        public class DispatchWorkbenchLineSettingDto
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public int originHoldLimitMinutes;
            [DataMember]
            public int maxStationDwellMinutes;
            [DataMember]
            public string allowedDepotId;
            [DataMember]
            public string serviceKind;
        }

        [DataContract]
        public class DispatchWorkbenchDepotDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string name;
            [DataMember]
            public string transportType;
        }

        [DataContract]
        public class DispatchWorkbenchSaveRequest
        {
            [DataMember]
            public string selectedLineId;
            [DataMember]
            public string selectedEditLine;
            [DataMember]
            public DispatchWorkbenchMergedView mergedView;
            [DataMember]
            public DispatchWorkbenchManualRowDto[] manualRows;
            [DataMember]
            public DispatchWorkbenchAutoRuleDto[] autoRules;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public DispatchWorkbenchLineSettingDto[] lineSettings;
            [DataMember]
            public bool markRulesApplied;
            [DataMember]
            public bool applyDraft;
        }

        [DataContract]
        public class DispatchWorkbenchSaveResult
        {
            [DataMember]
            public bool success;
            [DataMember]
            public string[] errors;
            [DataMember]
            public string[] warnings;
            [DataMember]
            public string version;
            [DataMember]
            public DispatchWorkbenchSnapshot snapshot;
        }

        [DataContract]
        public class DispatchWorkbenchPersistentState
        {
            [DataMember]
            public string preferredLineId;
            [DataMember]
            public DispatchWorkbenchLineSettingDto[] lineSettings;
            [DataMember]
            public DispatchWorkbenchPersistedDraftState[] drafts;
        }

        [DataContract]
        public class DispatchWorkbenchPersistedDraftState
        {
            [DataMember]
            public string lineKey;
            [DataMember]
            public string selectedLineId;
            [DataMember]
            public string selectedEditLine;
            [DataMember]
            public DispatchWorkbenchMergedView mergedView;
            [DataMember]
            public DispatchWorkbenchManualRowDto[] manualRows;
            [DataMember]
            public DispatchWorkbenchAutoRuleDto[] autoRules;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public bool rulesApplied;
            [DataMember]
            public bool draftApplied;
        }

        [DataContract]
        public class DispatchWorkbenchMergedView
        {
            [DataMember]
            public string localLineId;
            [DataMember]
            public string expressLineId;
            [DataMember]
            public string[] localLineIds;
            [DataMember]
            public string[] expressLineIds;
            [DataMember]
            public bool isLoop;
            [DataMember]
            public string turnbackStationId;
            [DataMember]
            public string direction;
            [DataMember]
            public string windowStart;
            [DataMember]
            public string windowEnd;
        }

        [DataContract]
        public class DispatchWorkbenchLineDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string sourceLineId;
            [DataMember]
            public string name;
            [DataMember]
            public string kind;
            [DataMember]
            public string direction;
            [DataMember]
            public int stationCount;
            [DataMember]
            public string color;
            [DataMember]
            public string originStationId;
            [DataMember]
            public string originStationName;
            [DataMember]
            public int originHoldLimitMinutes;
            [DataMember]
            public int maxStationDwellMinutes;
            [DataMember]
            public string transportType;
            [DataMember]
            public string allowedDepotId;
        }

        [DataContract]
        public class DispatchWorkbenchStationDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string name;
            [DataMember]
            public int order;
            [DataMember]
            public float distance;
            [DataMember]
            public bool hasSiding;
        }

        [DataContract]
        public class DispatchWorkbenchTripStopDto
        {
            [DataMember]
            public string stationId;
            [DataMember]
            public string time;
            [DataMember]
            public string arrivalTime;
            [DataMember]
            public string departureTime;
            [DataMember]
            public string stopType;
            [DataMember]
            public int? waitMinutes;
        }

        [DataContract]
        public class DispatchWorkbenchTripDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public string kind;
            [DataMember]
            public string depart;
            [DataMember]
            public int realtimeSegment;
            [DataMember]
            public float realtimeProgress;
            [DataMember]
            public string realtimeFromStationId;
            [DataMember]
            public string realtimeToStationId;
            [DataMember]
            public string realtimeTime;
            [DataMember]
            public DispatchWorkbenchTripStopDto[] stops;
        }

        [DataContract]
        public class DispatchWorkbenchManualRowDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public string time;
            [DataMember]
            public string kind;
            [DataMember]
            public string offsetMode;
            [DataMember]
            public string offsetMinutes;
        }

        [DataContract]
        public class DispatchWorkbenchAutoRuleDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public bool enabled;
            [DataMember]
            public string start;
            [DataMember]
            public string end;
            [DataMember]
            public string kind;
            [DataMember]
            public double departuresPerHour;
            [DataMember]
            public double localPerHour;
            [DataMember]
            public double expressPerHour;
            [DataMember]
            public string expressOffsetMode;
            [DataMember]
            public int expressOffsetMinutes;
        }

        [DataContract]
        public class DispatchWorkbenchStagedRowDto
        {
            [DataMember]
            public string id;
            [DataMember]
            public string lineId;
            [DataMember]
            public string time;
            [DataMember]
            public string kind;
            [DataMember]
            public string source;
            [DataMember]
            public string note;
        }

        private sealed class DispatchWorkbenchDraftState
        {
            public string SelectedLineId = string.Empty;
            public string SelectedEditLine = "local";
            public DispatchWorkbenchMergedView MergedView = new DispatchWorkbenchMergedView
            {
                localLineIds = Array.Empty<string>(),
                expressLineIds = Array.Empty<string>(),
                isLoop = true,
                turnbackStationId = string.Empty,
                direction = "up",
                windowStart = string.Empty,
                windowEnd = string.Empty
            };
            public List<DispatchWorkbenchManualRowDto> ManualRows = new List<DispatchWorkbenchManualRowDto>();
            public List<DispatchWorkbenchAutoRuleDto> AutoRules = new List<DispatchWorkbenchAutoRuleDto>();
            public List<DispatchWorkbenchStagedRowDto> StagedRows = new List<DispatchWorkbenchStagedRowDto>();
            public bool RulesApplied = false;
            public bool DraftApplied = false;
            public readonly Dictionary<string, int[]> AppliedDepartureMinutesCache = new Dictionary<string, int[]>(StringComparer.Ordinal);
        }

        private sealed class AppliedWorkbenchLineState
        {
            public Entity LineEntity = Entity.Null;
            public int OriginHoldLimitMinutes = DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;
            public int MaxStationDwellMinutes = DEFAULT_MAX_STATION_DWELL_MINUTES;
            public List<DispatchWorkbenchStagedRowDto> StagedRows = new List<DispatchWorkbenchStagedRowDto>();
            public int[] DepartureMinutesCache = Array.Empty<int>();
        }

        private sealed class WorkbenchLineRuntime
        {
            public Entity Entity;
            public string Id = string.Empty;
            public string Name = string.Empty;
            public string Kind = "local";
            public string TransportType = string.Empty;
            public int RouteNumber = int.MaxValue;
            public int StationCount = 0;
            public string Color = string.Empty;
            public string OriginStationId = string.Empty;
            public string OriginStationName = string.Empty;
        }

        private sealed class WorkbenchRealtimeStopRecord
        {
            public Entity StopEntity;
            public string ArrivalTime = string.Empty;
            public string DepartureTime = string.Empty;
            public uint LastUpdatedFrame;
        }

        private sealed class WorkbenchRealtimeTripRecord
        {
            public int Sequence;
            public readonly List<WorkbenchRealtimeStopRecord> Stops = new List<WorkbenchRealtimeStopRecord>();
            public uint LastUpdatedFrame;
        }

        private sealed class WorkbenchRealtimeVehicleRecord
        {
            public Entity Vehicle;
            public Entity Line;
            public string Kind = "local";
            public int NextSequence = 1;
            public readonly List<WorkbenchRealtimeTripRecord> Trips = new List<WorkbenchRealtimeTripRecord>();
        }

        private readonly Dictionary<string, DispatchWorkbenchDraftState> m_WorkbenchDrafts = new Dictionary<string, DispatchWorkbenchDraftState>();
        private readonly Dictionary<string, int> m_WorkbenchLineOriginHoldLimits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> m_WorkbenchLineMaxStationDwellMinutes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineAllowedDepots = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineServiceKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AppliedWorkbenchLineState> m_AppliedWorkbenchLines = new Dictionary<string, AppliedWorkbenchLineState>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, WorkbenchRealtimeVehicleRecord> m_WorkbenchRealtimeVehicles = new Dictionary<Entity, WorkbenchRealtimeVehicleRecord>();
        private ulong m_WorkbenchSnapshotVersion = 1;
        private string m_LastWorkbenchSnapshotLogKey = string.Empty;
        private string m_LastAppliedWorkbenchInspectLogKey = string.Empty;
        private string m_WorkbenchPreferredLineId = string.Empty;
        private bool m_WorkbenchPersistenceLoaded = false;
        private bool m_AppliedWorkbenchPersistenceLoaded = false;
        private const int DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES = 20;
        private const int DEFAULT_MAX_STATION_DWELL_MINUTES = 10;
        private const int MIN_ORIGIN_HOLD_LIMIT_MINUTES = 1;
        private const int MAX_ORIGIN_HOLD_LIMIT_MINUTES = 120;

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            TryRestoreWorkbenchPersistence();
            TryRestoreAppliedWorkbenchPersistence();
        }

        public string LoadWorkbenchSnapshotJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchSnapshot(null));
        }

        public string RefreshWorkbenchSnapshotJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
        }

        public string RefreshWorkbenchMetadataJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchMetadataSnapshot());
        }

        public string SaveWorkbenchDraftJson(string requestJson)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            DispatchWorkbenchSaveResult result = new DispatchWorkbenchSaveResult
            {
                success = false,
                errors = Array.Empty<string>(),
                warnings = Array.Empty<string>(),
                version = m_WorkbenchSnapshotVersion.ToString()
            };

            try
            {
                DispatchWorkbenchSaveRequest request = DispatchWorkbenchJson.Deserialize<DispatchWorkbenchSaveRequest>(requestJson);
                List<string> errors = ValidateWorkbenchRequest(request, BuildWorkbenchLinesStable());
                if (errors.Count > 0)
                {
                    result.errors = errors.ToArray();
                    result.snapshot = BuildWorkbenchSnapshot(request?.selectedLineId);
                    return DispatchWorkbenchJson.Serialize(result);
                }

                string lineKey = GetDraftKey(request?.selectedLineId);
                DispatchWorkbenchDraftState state = GetOrCreateWorkbenchDraft(lineKey);
                List<DispatchWorkbenchManualRowDto> nextManualRows = request.manualRows != null
                    ? request.manualRows.Select(CloneManualRow).ToList()
                    : new List<DispatchWorkbenchManualRowDto>();
                List<DispatchWorkbenchAutoRuleDto> nextAutoRules = request.autoRules != null
                    ? request.autoRules.Select(CloneAutoRule).ToList()
                    : new List<DispatchWorkbenchAutoRuleDto>();
                List<DispatchWorkbenchStagedRowDto> nextStagedRows = request.stagedRows != null
                    ? request.stagedRows.Select(CloneStagedRow).ToList()
                    : new List<DispatchWorkbenchStagedRowDto>();
                if (request.lineSettings != null)
                {
                    ApplyWorkbenchLineSettings(request.lineSettings);
                }
                bool rulesChanged = !AreManualRowsEquivalent(state.ManualRows, nextManualRows)
                    || !AreAutoRulesEquivalent(state.AutoRules, nextAutoRules)
                    || !AreStagedRowsEquivalent(state.StagedRows, nextStagedRows);

                state.SelectedLineId = string.IsNullOrEmpty(request.selectedLineId) ? lineKey : request.selectedLineId;
                state.SelectedEditLine = string.IsNullOrEmpty(request.selectedEditLine) ? "local" : request.selectedEditLine;
                state.MergedView = request.mergedView ?? state.MergedView;
                state.ManualRows = nextManualRows;
                state.AutoRules = nextAutoRules;
                state.StagedRows = nextStagedRows;
                RemoveWorkbenchRowsFromOtherDrafts(
                    lineKey,
                    CollectTouchedWorkbenchLineIds(
                        state.SelectedLineId,
                        state.SelectedEditLine,
                        nextManualRows,
                        nextAutoRules,
                        nextStagedRows));
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

                if (request.applyDraft && state.StagedRows.Count == 0)
                {
                    result.errors = new[] { "Add rows into the staged timetable before applying the draft." };
                    m_WorkbenchPreferredLineId = state.SelectedLineId;
                    RefreshAppliedWorkbenchLineSettings();
                    SaveWorkbenchPersistence();
                    SaveAppliedWorkbenchPersistence();
                    result.snapshot = BuildWorkbenchSnapshot(state.SelectedLineId);
                    return DispatchWorkbenchJson.Serialize(result);
                }

                if (request.applyDraft)
                {
                    state.DraftApplied = true;
                }

                m_WorkbenchSnapshotVersion++;
                m_WorkbenchPreferredLineId = state.SelectedLineId;
                if (request.applyDraft)
                {
                    RebuildAppliedWorkbenchStateFromDrafts();
                }
                else
                {
                    RefreshAppliedWorkbenchLineSettings();
                }
                SaveWorkbenchPersistence();
                SaveAppliedWorkbenchPersistence();
                result.success = true;
                result.version = m_WorkbenchSnapshotVersion.ToString();
                result.snapshot = BuildWorkbenchSnapshot(state.SelectedLineId);
                DispatchWorkbenchEuisBridge.NotifyWorkbenchSnapshotChanged(result.snapshot);
                return DispatchWorkbenchJson.Serialize(result);
            }
            catch (Exception ex)
            {
                LogWorkbenchException("SaveWorkbenchDraftJson", ex);
                result.errors = new[] { DescribeWorkbenchException(ex) };
                result.snapshot = BuildWorkbenchSnapshot(null);
                return DispatchWorkbenchJson.Serialize(result);
            }
        }

        private DispatchWorkbenchSnapshot BuildWorkbenchSnapshot(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            WorkbenchLineRuntime activeRuntime = runtimeLines.Count > 0
                ? ResolveActiveWorkbenchLine(runtimeLines, preferredLineId)
                : null;

            string draftKey = GetDraftKey(activeRuntime?.Id);
            DispatchWorkbenchDraftState draft = GetOrCreateWorkbenchDraft(draftKey);
            List<DispatchWorkbenchManualRowDto> mergedManualRows = new List<DispatchWorkbenchManualRowDto>();
            List<DispatchWorkbenchAutoRuleDto> mergedAutoRules = new List<DispatchWorkbenchAutoRuleDto>();
            List<DispatchWorkbenchStagedRowDto> mergedStagedRows = new List<DispatchWorkbenchStagedRowDto>();
            HashSet<string> mergedManualRowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mergedAutoRuleIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mergedStagedRowIds = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchStationDto> stations = activeRuntime != null
                ? BuildWorkbenchStations(activeRuntime.Entity)
                : new List<DispatchWorkbenchStationDto>();

            if (string.IsNullOrEmpty(draft.SelectedLineId) && activeRuntime != null)
            {
                draft.SelectedLineId = activeRuntime.Id;
            }

            if (activeRuntime != null)
            {
                m_WorkbenchPreferredLineId = activeRuntime.Id;
            }

            EnsureMergedViewDefaultsStable(draft, runtimeLines, activeRuntime);
            CollectWorkbenchDraftRows(draftKey, mergedManualRows, mergedAutoRules, mergedStagedRows, mergedManualRowIds, mergedAutoRuleIds, mergedStagedRowIds);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.Equals(entry.Key, draftKey, StringComparison.Ordinal))
                    continue;
                CollectWorkbenchDraftRows(entry.Key, mergedManualRows, mergedAutoRules, mergedStagedRows, mergedManualRowIds, mergedAutoRuleIds, mergedStagedRowIds);
            }
            List<DispatchWorkbenchTripDto> trips = activeRuntime != null
                ? BuildRealtimeWorkbenchTrips(activeRuntime, stations, draft)
                : new List<DispatchWorkbenchTripDto>();
            List<DispatchWorkbenchDepotDto> depots = BuildWorkbenchDepots();

            LogWorkbenchSnapshot(activeRuntime, stations, trips, draft);

            return new DispatchWorkbenchSnapshot
            {
                selectedLineId = draft.SelectedLineId,
                selectedEditLine = draft.SelectedEditLine,
                mergedView = draft.MergedView,
                lines = runtimeLines.Select(line => new DispatchWorkbenchLineDto
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
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(line.Id),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line.Id),
                    transportType = line.TransportType,
                    allowedDepotId = GetWorkbenchAllowedDepotId(line.Id)
                }).ToArray(),
                depots = depots.ToArray(),
                stations = stations.ToArray(),
                trips = trips.ToArray(),
                manualRows = mergedManualRows.ToArray(),
                autoRules = mergedAutoRules.ToArray(),
                stagedRows = mergedStagedRows.ToArray(),
                version = m_WorkbenchSnapshotVersion.ToString(),
                sourceMode = "game-backend",
                rulesApplied = draft.RulesApplied,
                draftApplied = draft.DraftApplied
            };
        }

        private DispatchWorkbenchSnapshot BuildWorkbenchMetadataSnapshot()
        {
            EnsureWorkbenchPersistenceLoaded();
            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            List<DispatchWorkbenchDepotDto> depots = BuildWorkbenchDepots();

            return new DispatchWorkbenchSnapshot
            {
                selectedLineId = GetPreferredWorkbenchLineId(),
                selectedEditLine = GetPreferredWorkbenchLineId(),
                mergedView = new DispatchWorkbenchMergedView(),
                lines = runtimeLines.Select(line => new DispatchWorkbenchLineDto
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
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(line.Id),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line.Id),
                    transportType = line.TransportType,
                    allowedDepotId = GetWorkbenchAllowedDepotId(line.Id)
                }).ToArray(),
                depots = depots.ToArray(),
                stations = Array.Empty<DispatchWorkbenchStationDto>(),
                trips = Array.Empty<DispatchWorkbenchTripDto>(),
                manualRows = Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                stagedRows = Array.Empty<DispatchWorkbenchStagedRowDto>(),
                version = m_WorkbenchSnapshotVersion.ToString(),
                sourceMode = "game-backend",
                rulesApplied = false,
                draftApplied = false
            };
        }

        private void CollectWorkbenchDraftRows(
            string lineKey,
            List<DispatchWorkbenchManualRowDto> manualRows,
            List<DispatchWorkbenchAutoRuleDto> autoRules,
            List<DispatchWorkbenchStagedRowDto> stagedRows,
            HashSet<string> manualRowIds,
            HashSet<string> autoRuleIds,
            HashSet<string> stagedRowIds)
        {
            if (string.IsNullOrEmpty(lineKey))
                return;

            if (!m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft)
                || draft == null)
            {
                return;
            }

            if (draft.ManualRows != null)
            {
                foreach (DispatchWorkbenchManualRowDto row in draft.ManualRows)
                {
                    if (row != null && manualRowIds.Add(row.id ?? string.Empty))
                        manualRows.Add(CloneManualRow(row));
                }
            }

            if (draft.AutoRules != null)
            {
                foreach (DispatchWorkbenchAutoRuleDto rule in draft.AutoRules)
                {
                    if (rule != null && autoRuleIds.Add(rule.id ?? string.Empty))
                        autoRules.Add(CloneAutoRule(rule));
                }
            }

            if (draft.StagedRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    if (row != null && stagedRowIds.Add(row.id ?? string.Empty))
                        stagedRows.Add(CloneStagedRow(row));
                }
            }
        }

        private List<WorkbenchLineRuntime> BuildWorkbenchLines()
        {
            List<WorkbenchLineRuntime> lines = new List<WorkbenchLineRuntime>();
            NativeArray<Entity> entities = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                        continue;

                    string name = ResolveWorkbenchEntityName(line);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = "Line " + line.Index;
                    }

                    string kind = GetEffectiveWorkbenchLineServiceKind(line.Index.ToString(), null);
                    if (string.IsNullOrEmpty(kind))
                    {
                        kind = "local";
                    }

                    lines.Add(new WorkbenchLineRuntime
                    {
                        Entity = line,
                        Id = line.Index.ToString(),
                        Name = name,
                        Kind = kind
                    });
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            if (lines.Count == 0)
                return lines;

            bool hasLocal = lines.Any(line => line.Kind == "local");
            bool hasExpress = lines.Any(line => line.Kind == "express");
            WorkbenchLineRuntime first = lines[0];

            if (!hasLocal)
            {
                lines.Insert(0, new WorkbenchLineRuntime
                {
                    Entity = first.Entity,
                    Id = first.Id + "-local-view",
                    Name = first.Name + "（慢车视图）",
                    Kind = "local"
                });
            }

            if (!hasExpress)
            {
                lines.Add(new WorkbenchLineRuntime
                {
                    Entity = first.Entity,
                    Id = first.Id + "-express-view",
                    Name = first.Name + "（快车视图）",
                    Kind = "express"
                });
            }

            return lines;
        }

        private List<WorkbenchLineRuntime> BuildWorkbenchLinesStable()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();

            List<WorkbenchLineRuntime> lines = new List<WorkbenchLineRuntime>();
            NativeArray<Entity> entities = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                        continue;

                    int routeNumber = int.MaxValue;
                    if (EntityManager.HasComponent<RouteNumber>(line))
                    {
                        routeNumber = EntityManager.GetComponentData<RouteNumber>(line).m_Number;
                    }
                    int stationCount = CountWorkbenchStops(line);
                    string originStationId;
                    string originStationName;
                    ResolveWorkbenchLineOrigin(line, out originStationId, out originStationName);
                    string transportType = ResolveWorkbenchLineTransportType(line);

                    string name = ResolveWorkbenchEntityName(line);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = routeNumber != int.MaxValue
                            ? ("Line " + routeNumber.ToString())
                            : ("Line " + line.Index.ToString());
                    }

                    string lineKey = GetDraftKey(line.Index.ToString());
                    m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState appliedState);
                    string kind = GetEffectiveWorkbenchLineServiceKind(lineKey, appliedState);
                    if (string.IsNullOrEmpty(kind))
                    {
                        kind = "local";
                    }

                    lines.Add(new WorkbenchLineRuntime
                    {
                        Entity = line,
                        Id = lineKey,
                        Name = name,
                        Kind = kind,
                        TransportType = transportType,
                        RouteNumber = routeNumber,
                        StationCount = stationCount,
                        Color = ResolveWorkbenchLineColor(line),
                        OriginStationId = originStationId,
                        OriginStationName = originStationName
                    });
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            if (lines.Count == 0)
                return lines;

            return lines;
        }

        private WorkbenchLineRuntime ResolveActiveWorkbenchLine(List<WorkbenchLineRuntime> lines, string preferredLineId)
        {
            if (!string.IsNullOrEmpty(preferredLineId))
            {
                WorkbenchLineRuntime exact = lines.FirstOrDefault(line => line.Id == preferredLineId);
                if (exact != null)
                    return exact;
            }

            string savedLineId = GetPreferredWorkbenchLineId();
            if (!string.IsNullOrEmpty(savedLineId))
            {
                WorkbenchLineRuntime saved = lines.FirstOrDefault(line => line.Id == savedLineId);
                if (saved != null)
                    return saved;
            }

            return lines[0];
        }

        private string GetPreferredWorkbenchLineId()
        {
            if (!string.IsNullOrEmpty(m_WorkbenchPreferredLineId))
                return m_WorkbenchPreferredLineId;
            if (m_WorkbenchDrafts.Count == 0)
                return string.Empty;

            KeyValuePair<string, DispatchWorkbenchDraftState> first = m_WorkbenchDrafts.First();
            return first.Value.SelectedLineId;
        }

        private string GetDraftKey(string lineId)
        {
            return string.IsNullOrEmpty(lineId) ? "__default__" : lineId;
        }

        private void EnsureWorkbenchPersistenceLoaded()
        {
            if (m_WorkbenchPersistenceLoaded)
            {
                EnsureAppliedWorkbenchPersistenceLoaded();
                return;
            }

            TryRestoreWorkbenchPersistence();
            EnsureAppliedWorkbenchPersistenceLoaded();
        }

        private void EnsureAppliedWorkbenchPersistenceLoaded()
        {
            if (m_AppliedWorkbenchPersistenceLoaded)
                return;

            TryRestoreAppliedWorkbenchPersistence();
        }

        private void EnsureWorkbenchPersistenceBuffer()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                EntityManager.AddBuffer<WorkbenchTimetableStateElement>(city);
            }
        }

        private void EnsureAppliedWorkbenchPersistenceBuffers()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
            {
                EntityManager.AddBuffer<AppliedWorkbenchLineStateElement>(city);
            }

            if (!EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                EntityManager.AddBuffer<AppliedWorkbenchStagedRowElement>(city);
            }
        }

        private bool TryRestoreWorkbenchPersistence()
        {
            if (m_WorkbenchPersistenceLoaded)
                return true;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return false;

            if (!EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
            {
                m_WorkbenchPersistenceLoaded = true;
                return true;
            }

            var buffer = EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city, true);
            if (buffer.Length == 0)
            {
                m_WorkbenchPersistenceLoaded = true;
                return true;
            }

            try
            {
                string payloadJson = string.Concat(buffer
                    .OrderBy(entry => entry.m_ChunkIndex)
                    .Select(entry => entry.m_PayloadChunk.ToString()));

                if (string.IsNullOrEmpty(payloadJson))
                {
                    m_WorkbenchPersistenceLoaded = true;
                    return true;
                }

                DispatchWorkbenchPersistentState persisted =
                    DispatchWorkbenchJson.Deserialize<DispatchWorkbenchPersistentState>(payloadJson);

                if (persisted != null)
                {
                    RestoreWorkbenchPersistence(persisted);
                }

                m_WorkbenchPersistenceLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                LogWorkbenchException("TryRestoreWorkbenchPersistence", ex);
                m_WorkbenchPersistenceLoaded = true;
                return false;
            }
        }

        private bool TryRestoreAppliedWorkbenchPersistence()
        {
            if (m_AppliedWorkbenchPersistenceLoaded)
                return true;

            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return false;

            m_AppliedWorkbenchLines.Clear();

            try
            {
                if (EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city))
                {
                    var lineBuffer = EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city, true);
                    for (int i = 0; i < lineBuffer.Length; i++)
                    {
                        AppliedWorkbenchLineStateElement entry = lineBuffer[i];
                        if (entry.m_LineEntity == Entity.Null)
                            continue;

                        string lineKey = GetDraftKey(entry.m_LineEntity.Index.ToString());
                        m_AppliedWorkbenchLines[lineKey] = new AppliedWorkbenchLineState
                        {
                            LineEntity = entry.m_LineEntity,
                            OriginHoldLimitMinutes = NormalizeOriginHoldLimitMinutes(entry.m_OriginHoldLimitMinutes),
                        };
                    }
                }

                if (EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
                {
                    var rowBuffer = EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city, true);
                    for (int i = 0; i < rowBuffer.Length; i++)
                    {
                        AppliedWorkbenchStagedRowElement row = rowBuffer[i];
                        if (row.m_LineEntity == Entity.Null)
                            continue;

                        string lineKey = GetDraftKey(row.m_LineEntity.Index.ToString());
                        if (!m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState state))
                        {
                            state = new AppliedWorkbenchLineState
                            {
                                LineEntity = row.m_LineEntity,
                                OriginHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(lineKey),
                                MaxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(lineKey)
                            };
                            m_AppliedWorkbenchLines[lineKey] = state;
                        }

                        state.StagedRows.Add(new DispatchWorkbenchStagedRowDto
                        {
                            id = "applied-" + lineKey + "-" + row.m_Order.ToString(),
                            lineId = lineKey,
                            time = MinutesToSlotString(row.m_Minute),
                            kind = DecodeAppliedRowKind(row.m_KindCode),
                            source = DecodeAppliedRowSource(row.m_SourceCode),
                            note = BuildAppliedRowNote(row.m_SourceCode)
                        });
                    }
                }

                foreach (AppliedWorkbenchLineState state in m_AppliedWorkbenchLines.Values)
                {
                state.StagedRows = state.StagedRows
                        .OrderBy(row => ParseTimeMinutes(row.time))
                        .ThenBy(row => row.id, StringComparer.Ordinal)
                        .Select(CloneStagedRow)
                        .ToList();
                    state.DepartureMinutesCache = BuildAppliedDepartureMinutes(state.StagedRows, state.LineEntity.Index.ToString());
                }

                SyncWorkbenchDraftsFromAppliedState();
                m_AppliedWorkbenchPersistenceLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                LogWorkbenchException("TryRestoreAppliedWorkbenchPersistence", ex);
                m_AppliedWorkbenchPersistenceLoaded = true;
                return false;
            }
        }

        private void SaveWorkbenchPersistence()
        {
            EnsureWorkbenchPersistenceBuffer();

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<WorkbenchTimetableStateElement>(city))
                return;

            DispatchWorkbenchPersistentState persisted = BuildWorkbenchPersistenceState();
            string payloadJson = DispatchWorkbenchJson.Serialize(persisted);
            var buffer = EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city);
            List<string> payloadChunks = SplitWorkbenchPersistencePayload(payloadJson);
            buffer.Clear();

            if (payloadChunks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < payloadChunks.Count; i++)
            {
                buffer.Add(new WorkbenchTimetableStateElement
                {
                    m_ChunkIndex = i,
                    m_PayloadChunk = new FixedString4096Bytes(payloadChunks[i] ?? string.Empty)
                });
            }
        }

        private void SaveAppliedWorkbenchPersistence()
        {
            EnsureAppliedWorkbenchPersistenceBuffers();

            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
                return;

            var lineBuffer = EntityManager.GetBuffer<AppliedWorkbenchLineStateElement>(city);
            var rowBuffer = EntityManager.GetBuffer<AppliedWorkbenchStagedRowElement>(city);
            lineBuffer.Clear();
            rowBuffer.Clear();

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.LineEntity == Entity.Null || state.StagedRows == null || state.StagedRows.Count == 0)
                    continue;

                lineBuffer.Add(new AppliedWorkbenchLineStateElement
                {
                    m_LineEntity = state.LineEntity,
                    m_OriginHoldLimitMinutes = NormalizeOriginHoldLimitMinutes(state.OriginHoldLimitMinutes),
                });

                for (int i = 0; i < state.StagedRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = state.StagedRows[i];
                    int minute = ParseTimeMinutes(row?.time);
                    if (minute < 0)
                        continue;

                    rowBuffer.Add(new AppliedWorkbenchStagedRowElement
                    {
                        m_LineEntity = state.LineEntity,
                        m_Order = i,
                        m_Minute = minute,
                        m_KindCode = EncodeAppliedRowKind(row?.kind),
                        m_SourceCode = EncodeAppliedRowSource(row?.source)
                    });
                }
            }
        }

        private void RebuildAppliedWorkbenchStateFromDrafts()
        {
            List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
            Dictionary<string, WorkbenchLineRuntime> runtimeById = runtimeLines.ToDictionary(line => line.Id, StringComparer.Ordinal);

            m_AppliedWorkbenchLines.Clear();
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied || draft.StagedRows == null || draft.StagedRows.Count == 0)
                    continue;

                foreach (IGrouping<string, DispatchWorkbenchStagedRowDto> groupedRows in draft.StagedRows
                    .Where(row => row != null && !string.IsNullOrEmpty(row.lineId))
                    .GroupBy(row => row.lineId, StringComparer.Ordinal))
                {
                    string lineKey = groupedRows.Key;
                    if (!runtimeById.TryGetValue(lineKey, out WorkbenchLineRuntime runtime))
                        continue;

                    List<DispatchWorkbenchStagedRowDto> lineRows = groupedRows.Select(CloneStagedRow).ToList();
                    if (lineRows.Count == 0)
                        continue;

                    AppliedWorkbenchLineState applied = new AppliedWorkbenchLineState
                    {
                        LineEntity = runtime.Entity,
                        OriginHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(lineKey),
                        MaxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(lineKey),
                        StagedRows = lineRows
                    };
                    applied.DepartureMinutesCache = BuildAppliedDepartureMinutes(applied.StagedRows, lineKey);
                    m_AppliedWorkbenchLines[lineKey] = applied;
                }
            }

            SyncWorkbenchDraftsFromAppliedState();
        }

        private void RefreshAppliedWorkbenchLineSettings()
        {
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                string lineKey = entry.Key;
                AppliedWorkbenchLineState applied = entry.Value;
                if (applied == null)
                    continue;

                applied.OriginHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(lineKey);
                applied.MaxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(lineKey);
            }
        }

        private static HashSet<string> CollectTouchedWorkbenchLineIds(
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

        private void RemoveWorkbenchRowsFromOtherDrafts(string activeLineKey, HashSet<string> lineIds)
        {
            if (lineIds == null || lineIds.Count == 0)
                return;

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                if (string.Equals(entry.Key, activeLineKey, StringComparison.Ordinal))
                    continue;

                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null)
                    continue;

                bool changed = false;
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

                changed = manualBefore != (draft.ManualRows?.Count ?? 0)
                    || autoBefore != (draft.AutoRules?.Count ?? 0)
                    || stagedBefore != (draft.StagedRows?.Count ?? 0);

                if (!changed)
                    continue;

                draft.RulesApplied = false;
                draft.DraftApplied = false;
                draft.AppliedDepartureMinutesCache.Clear();
            }
        }

        private void SyncWorkbenchDraftsFromAppliedState()
        {
            foreach (DispatchWorkbenchDraftState draft in m_WorkbenchDrafts.Values)
            {
                draft.DraftApplied = false;
                draft.AppliedDepartureMinutesCache.Clear();
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                string lineKey = entry.Key;
                AppliedWorkbenchLineState applied = entry.Value;
                if (!m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft))
                {
                    draft = CreateEmptyWorkbenchDraftState(lineKey);
                    draft.ManualRows.Clear();
                    draft.AutoRules.Clear();
                    m_WorkbenchDrafts[lineKey] = draft;
                }

                draft.SelectedLineId = lineKey;
                if (string.IsNullOrEmpty(draft.SelectedEditLine))
                {
                    draft.SelectedEditLine = lineKey == "__default__" ? string.Empty : lineKey;
                }
                bool hasMixedDraftRows = draft.StagedRows != null
                    && draft.StagedRows
                        .Where(row => row != null && !string.IsNullOrEmpty(row.lineId))
                        .Select(row => row.lineId)
                        .Distinct(StringComparer.Ordinal)
                        .Count() > 1;
                if (!hasMixedDraftRows
                    && !AreStagedRowsEquivalentIgnoringIdAndNote(draft.StagedRows, applied.StagedRows))
                {
                    draft.StagedRows = applied.StagedRows.Select(CloneStagedRow).ToList();
                }
                if (!hasMixedDraftRows)
                {
                    EnsureAppliedMergedViewMatchesLineKind(draft, lineKey, applied);
                }
                draft.DraftApplied = true;
                draft.RulesApplied = true;
                draft.AppliedDepartureMinutesCache.Clear();
                m_WorkbenchLineOriginHoldLimits[lineKey] =
                    NormalizeOriginHoldLimitMinutes(applied.OriginHoldLimitMinutes);
            }
        }

        private void EnsureAppliedMergedViewMatchesLineKind(
            DispatchWorkbenchDraftState draft,
            string lineKey,
            AppliedWorkbenchLineState applied)
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

            string serviceKind = GetEffectiveWorkbenchLineServiceKind(lineKey, applied);
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

        private static int[] BuildAppliedDepartureMinutes(IEnumerable<DispatchWorkbenchStagedRowDto> rows, string lineId)
        {
            if (rows == null)
                return Array.Empty<int>();

            HashSet<int> uniqueMinutes = new HashSet<int>();
            List<int> minutes = new List<int>();
            foreach (DispatchWorkbenchStagedRowDto row in rows)
            {
                if (row == null)
                    continue;
                if (!string.IsNullOrEmpty(row.lineId) && !string.Equals(row.lineId, lineId, StringComparison.Ordinal))
                    continue;

                int minute = ParseTimeMinutes(row.time);
                if (minute < 0 || !uniqueMinutes.Add(minute))
                    continue;

                minutes.Add(minute);
            }

            minutes.Sort();
            return minutes.ToArray();
        }

        private static byte EncodeAppliedRowKind(string kind)
        {
            return string.Equals(kind, "express", StringComparison.Ordinal) ? (byte)1 : (byte)0;
        }

        private static string DecodeAppliedRowKind(byte code)
        {
            return code == 1 ? "express" : "local";
        }

        private static byte EncodeAppliedRowSource(string source)
        {
            if (string.Equals(source, "manual", StringComparison.Ordinal))
                return 1;
            if (string.Equals(source, "auto", StringComparison.Ordinal))
                return 2;
            return 0;
        }

        private static string DecodeAppliedRowSource(byte code)
        {
            return code switch
            {
                1 => "manual",
                2 => "auto",
                _ => string.Empty
            };
        }

        private static string BuildAppliedRowNote(byte sourceCode)
        {
            return sourceCode switch
            {
                1 => "restored-manual",
                2 => "restored-auto",
                _ => string.Empty
            };
        }

        private DispatchWorkbenchPersistentState BuildWorkbenchPersistenceState()
        {
            List<DispatchWorkbenchPersistedDraftState> drafts = new List<DispatchWorkbenchPersistedDraftState>(m_WorkbenchDrafts.Count);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                drafts.Add(CreatePersistedDraftState(entry.Key, entry.Value));
            }

            DispatchWorkbenchLineSettingDto[] lineSettings = m_WorkbenchLineOriginHoldLimits.Keys
                .Concat(m_WorkbenchLineMaxStationDwellMinutes.Keys)
                .Concat(m_WorkbenchLineAllowedDepots.Keys)
                .Concat(m_WorkbenchLineServiceKinds.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(lineId => new DispatchWorkbenchLineSettingDto
                {
                    lineId = lineId,
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(lineId),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(lineId),
                    allowedDepotId = GetWorkbenchAllowedDepotId(lineId),
                    serviceKind = GetWorkbenchConfiguredLineServiceKind(lineId)
                })
                .ToArray();

            return new DispatchWorkbenchPersistentState
            {
                preferredLineId = m_WorkbenchPreferredLineId,
                lineSettings = lineSettings,
                drafts = drafts.ToArray()
            };
        }

        private void RestoreWorkbenchPersistence(DispatchWorkbenchPersistentState persisted)
        {
            m_WorkbenchDrafts.Clear();
            m_WorkbenchLineOriginHoldLimits.Clear();
            m_WorkbenchLineMaxStationDwellMinutes.Clear();
            m_WorkbenchLineAllowedDepots.Clear();
            m_WorkbenchLineServiceKinds.Clear();
            m_WorkbenchPreferredLineId = persisted?.preferredLineId ?? string.Empty;

            if (persisted?.lineSettings != null)
            {
                ApplyWorkbenchLineSettings(persisted.lineSettings);
            }

            if (persisted?.drafts == null)
                return;

            for (int i = 0; i < persisted.drafts.Length; i++)
            {
                DispatchWorkbenchPersistedDraftState dto = persisted.drafts[i];
                if (dto == null)
                    continue;

                string lineKey = !string.IsNullOrEmpty(dto.lineKey)
                    ? dto.lineKey
                    : GetDraftKey(dto.selectedLineId);

                m_WorkbenchDrafts[lineKey] = CreateDraftStateFromPersisted(lineKey, dto);
            }
        }

        private static DispatchWorkbenchPersistedDraftState CreatePersistedDraftState(string lineKey, DispatchWorkbenchDraftState draft)
        {
            return new DispatchWorkbenchPersistedDraftState
            {
                lineKey = lineKey,
                selectedLineId = draft?.SelectedLineId ?? string.Empty,
                selectedEditLine = string.Empty,
                mergedView = CloneMergedViewForPersistence(draft?.MergedView),
                manualRows = Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                stagedRows = draft?.StagedRows?.Select(CloneStagedRow).ToArray() ?? Array.Empty<DispatchWorkbenchStagedRowDto>(),
                rulesApplied = draft?.RulesApplied == true,
                draftApplied = draft?.DraftApplied == true
            };
        }

        private static DispatchWorkbenchDraftState CreateDraftStateFromPersisted(string lineKey, DispatchWorkbenchPersistedDraftState dto)
        {
            DispatchWorkbenchDraftState draft = new DispatchWorkbenchDraftState
            {
                SelectedLineId = !string.IsNullOrEmpty(dto.selectedLineId) ? dto.selectedLineId : lineKey,
                SelectedEditLine = !string.IsNullOrEmpty(dto.selectedEditLine)
                    ? dto.selectedEditLine
                    : (lineKey == "__default__" ? string.Empty : lineKey),
                MergedView = CloneMergedView(dto.mergedView),
                ManualRows = dto.manualRows?.Select(CloneManualRow).ToList() ?? new List<DispatchWorkbenchManualRowDto>(),
                AutoRules = dto.autoRules?.Select(CloneAutoRule).ToList() ?? new List<DispatchWorkbenchAutoRuleDto>(),
                StagedRows = dto.stagedRows?.Select(CloneStagedRow).ToList() ?? new List<DispatchWorkbenchStagedRowDto>(),
                RulesApplied = dto.rulesApplied,
                DraftApplied = dto.draftApplied
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

        private static List<string> SplitWorkbenchPersistencePayload(string payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
                return new List<string>();

            List<string> chunks = new List<string>();
            int offset = 0;
            while (offset < payloadJson.Length)
            {
                int chunkLength = FindWorkbenchPersistenceChunkLength(payloadJson, offset);
                if (chunkLength <= 0)
                {
                    chunkLength = 1;
                }

                chunks.Add(payloadJson.Substring(offset, chunkLength));
                offset += chunkLength;
            }

            return chunks;
        }

        private static int FindWorkbenchPersistenceChunkLength(string payloadJson, int offset)
        {
            int remaining = payloadJson.Length - offset;
            if (remaining <= 0)
                return 0;

            int low = 1;
            int high = remaining;
            int best = 1;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                int candidateLength = AdjustWorkbenchPersistenceChunkLength(payloadJson, offset, mid);
                if (candidateLength <= 0)
                {
                    high = mid - 1;
                    continue;
                }

                string candidate = payloadJson.Substring(offset, candidateLength);
                FixedString4096Bytes fixedCandidate = candidate;
                if (fixedCandidate.ToString() == candidate)
                {
                    best = candidateLength;
                    low = candidateLength + 1;
                }
                else
                {
                    high = candidateLength - 1;
                }
            }

            return best;
        }

        private static int AdjustWorkbenchPersistenceChunkLength(string payloadJson, int offset, int proposedLength)
        {
            int endIndex = Math.Min(payloadJson.Length, offset + proposedLength);
            if (endIndex <= offset)
                return 0;

            if (endIndex < payloadJson.Length
                && char.IsHighSurrogate(payloadJson[endIndex - 1])
                && char.IsLowSurrogate(payloadJson[endIndex]))
            {
                endIndex--;
            }

            return endIndex - offset;
        }

        private bool IsWorkbenchTimetableApplied(Entity line)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            if (line == Entity.Null)
                return false;

            string lineKey = GetDraftKey(line.Index.ToString());
            return m_AppliedWorkbenchLines.ContainsKey(lineKey);
        }

        private int[] GetAppliedWorkbenchDepartureMinutes(Entity line)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return Array.Empty<int>();

            string lineKey = GetDraftKey(line.Index.ToString());
            if (!m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState state)
                || state.StagedRows == null
                || state.StagedRows.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (state.DepartureMinutesCache == null || state.DepartureMinutesCache.Length == 0)
            {
                state.DepartureMinutesCache = BuildAppliedDepartureMinutes(state.StagedRows, line.Index.ToString());
            }

            return state.DepartureMinutesCache;
        }

        private void LogAppliedWorkbenchLineState(Entity line, int nowMin, int nextSlot)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return;

            string lineKey = GetDraftKey(line.Index.ToString());
            if (!m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState state))
                return;

            string staged = state.StagedRows != null && state.StagedRows.Count > 0
                ? string.Join(", ", state.StagedRows.Select(row =>
                    (row?.time ?? "-")
                    + "/"
                    + (string.IsNullOrEmpty(row?.kind) ? "-" : row.kind)
                    + "/"
                    + (string.IsNullOrEmpty(row?.source) ? "-" : row.source)))
                : "-";
            string cache = state.DepartureMinutesCache != null && state.DepartureMinutesCache.Length > 0
                ? string.Join(", ", state.DepartureMinutesCache.Select(MinutesToSlotString))
                : "-";
            string key =
                line.Index.ToString()
                + "|"
                + nowMin.ToString()
                + "|"
                + nextSlot.ToString()
                + "|"
                + staged
                + "|"
                + cache;
            if (string.Equals(key, m_LastAppliedWorkbenchInspectLogKey, StringComparison.Ordinal))
                return;

            m_LastAppliedWorkbenchInspectLogKey = key;
            Mod.log.Info(
                "[AppliedWorkbenchInspect] line="
                + line.Index.ToString()
                + " now="
                + SlotStr(nowMin)
                + " next="
                + (nextSlot >= 0 ? SlotStr(nextSlot) : "-")
                + " cache=["
                + cache
                + "] staged=["
                + staged
                + "]");
        }

        private string GetAppliedWorkbenchLineServiceKind(Entity line)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return string.Empty;

            string lineKey = GetDraftKey(line.Index.ToString());
            string configuredKind = GetWorkbenchConfiguredLineServiceKind(lineKey);
            if (!string.IsNullOrEmpty(configuredKind))
            {
                return configuredKind;
            }

            if (!m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState state)
                || state.StagedRows == null
                || state.StagedRows.Count == 0)
            {
                return string.Empty;
            }

            return GetAppliedWorkbenchLineServiceKind(state);
        }

        private static string GetAppliedWorkbenchLineServiceKind(AppliedWorkbenchLineState state)
        {
            if (state == null || state.StagedRows == null || state.StagedRows.Count == 0)
                return string.Empty;

            bool sawExpress = false;
            bool sawLocal = false;
            for (int i = 0; i < state.StagedRows.Count; i++)
            {
                string kind = state.StagedRows[i]?.kind;
                if (string.Equals(kind, "express", StringComparison.Ordinal))
                {
                    sawExpress = true;
                }
                else
                {
                    sawLocal = true;
                }

                if (sawExpress && sawLocal)
                    return "local";
            }

            if (sawExpress)
                return "express";

            return "local";
        }

        private string GetEffectiveWorkbenchLineServiceKind(string lineKey, AppliedWorkbenchLineState applied)
        {
            string configuredKind = GetWorkbenchConfiguredLineServiceKind(lineKey);
            if (!string.IsNullOrEmpty(configuredKind))
            {
                return configuredKind;
            }

            return GetAppliedWorkbenchLineServiceKind(applied);
        }

        private bool IsAppliedWorkbenchLocalLine(Entity line)
        {
            return string.Equals(GetAppliedWorkbenchLineServiceKind(line), "local", StringComparison.Ordinal);
        }

        private bool IsAppliedWorkbenchExpressLine(Entity line)
        {
            return string.Equals(GetAppliedWorkbenchLineServiceKind(line), "express", StringComparison.Ordinal);
        }

        private DispatchWorkbenchDraftState GetOrCreateWorkbenchDraft(string lineKey)
        {
            EnsureWorkbenchPersistenceLoaded();
            if (!m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft))
            {
                draft = CreateEmptyWorkbenchDraftState(lineKey);
                draft.ManualRows.Add(new DispatchWorkbenchManualRowDto
                {
                    id = "m1",
                    lineId = draft.SelectedEditLine,
                    time = "06:00",
                    kind = "local",
                    offsetMode = "none",
                    offsetMinutes = string.Empty
                });
                draft.ManualRows.Add(new DispatchWorkbenchManualRowDto
                {
                    id = "m2",
                    lineId = draft.SelectedEditLine,
                    time = "06:10",
                    kind = "express",
                    offsetMode = "none",
                    offsetMinutes = string.Empty
                });
                draft.AutoRules.Add(new DispatchWorkbenchAutoRuleDto
                {
                    id = "r1",
                    lineId = draft.SelectedEditLine,
                    enabled = true,
                    start = "06:00",
                    end = "07:00",
                    kind = "local",
                    departuresPerHour = 6,
                    localPerHour = 6,
                    expressPerHour = 0,
                    expressOffsetMode = "after",
                    expressOffsetMinutes = 0
                });
                m_WorkbenchDrafts[lineKey] = draft;
            }

            return draft;
        }

        private DispatchWorkbenchDraftState CreateEmptyWorkbenchDraftState(string lineKey)
        {
            DispatchWorkbenchMergedView mergedView = new DispatchWorkbenchMergedView
            {
                localLineIds = Array.Empty<string>(),
                expressLineIds = Array.Empty<string>(),
                isLoop = true,
                turnbackStationId = string.Empty,
                direction = "up"
            };
            ApplyCurrentTimeWindowDefaults(mergedView);

            return new DispatchWorkbenchDraftState
            {
                SelectedLineId = lineKey,
                SelectedEditLine = lineKey == "__default__" ? string.Empty : lineKey,
                MergedView = mergedView,
                DraftApplied = false
            };
        }

        private void EnsureMergedViewDefaults(DispatchWorkbenchDraftState draft, List<WorkbenchLineRuntime> lines, WorkbenchLineRuntime activeRuntime)
        {
            WorkbenchLineRuntime localLine = lines.FirstOrDefault() ?? activeRuntime;

            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            List<string> localIds = NormalizeLineIdList(draft.MergedView.localLineIds, draft.MergedView.localLineId, lines);
            if (localIds.Count == 0 && localLine != null)
            {
                localIds.Add(localLine.Id);
            }
            List<string> expressIds = NormalizeLineIdList(draft.MergedView.expressLineIds, draft.MergedView.expressLineId, lines);
            expressIds = expressIds.Where(id => !localIds.Contains(id)).ToList();

            draft.MergedView.localLineIds = localIds.ToArray();
            draft.MergedView.expressLineIds = expressIds.ToArray();
            draft.MergedView.localLineId = localIds.FirstOrDefault() ?? string.Empty;
            draft.MergedView.expressLineId = expressIds.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(draft.MergedView.turnbackStationId))
            {
                draft.MergedView.turnbackStationId = string.Empty;
            }
            draft.MergedView.direction = string.IsNullOrEmpty(draft.MergedView.direction) ? "up" : draft.MergedView.direction;
            ApplyCurrentTimeWindowDefaults(draft.MergedView);
        }

        private void EnsureMergedViewDefaultsStable(DispatchWorkbenchDraftState draft, List<WorkbenchLineRuntime> lines, WorkbenchLineRuntime activeRuntime)
        {
            WorkbenchLineRuntime localLine = lines.FirstOrDefault() ?? activeRuntime;

            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            List<string> localIds = NormalizeLineIdList(draft.MergedView.localLineIds, draft.MergedView.localLineId, lines);
            if (localIds.Count == 0 && localLine != null)
            {
                localIds.Add(localLine.Id);
            }

            List<string> expressIds = NormalizeLineIdList(draft.MergedView.expressLineIds, draft.MergedView.expressLineId, lines)
                .Where(id => !localIds.Contains(id))
                .ToList();

            draft.MergedView.localLineIds = localIds.ToArray();
            draft.MergedView.expressLineIds = expressIds.ToArray();
            draft.MergedView.localLineId = localIds.FirstOrDefault() ?? string.Empty;
            draft.MergedView.expressLineId = expressIds.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(draft.MergedView.turnbackStationId))
            {
                draft.MergedView.turnbackStationId = string.Empty;
            }
            draft.MergedView.direction = string.IsNullOrEmpty(draft.MergedView.direction) ? "up" : draft.MergedView.direction;
            ApplyCurrentTimeWindowDefaults(draft.MergedView);
        }

        private static List<string> NormalizeLineIdList(string[] ids, string fallbackId, List<WorkbenchLineRuntime> lines)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> normalized = new List<string>();

            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id) || seen.Contains(id))
                        continue;
                    if (lines != null && !lines.Any(line => line.Id == id))
                        continue;
                    seen.Add(id);
                    normalized.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(fallbackId) && !seen.Contains(fallbackId))
            {
                if (lines == null || lines.Any(line => line.Id == fallbackId))
                {
                    seen.Add(fallbackId);
                    normalized.Add(fallbackId);
                }
            }

            return normalized;
        }

        private void ApplyCurrentTimeWindowDefaults(DispatchWorkbenchMergedView mergedView)
        {
            if (mergedView == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(mergedView.windowStart) && !string.IsNullOrEmpty(mergedView.windowEnd))
            {
                return;
            }

            DateTime currentGameTime = World.GetOrCreateSystemManaged<TimeSystem>().GetCurrentDateTime();
            int currentMinutes = currentGameTime.Hour * 60 + currentGameTime.Minute;
            int startMinutes = (currentMinutes / 30) * 30;
            int endMinutes = startMinutes + 90;

            if (endMinutes > 1439)
            {
                startMinutes = 22 * 60 + 30;
                endMinutes = 23 * 60 + 59;
            }

            mergedView.windowStart = string.IsNullOrEmpty(mergedView.windowStart)
                ? FormatMinutes(startMinutes)
                : mergedView.windowStart;
            mergedView.windowEnd = string.IsNullOrEmpty(mergedView.windowEnd)
                ? FormatMinutes(endMinutes)
                : mergedView.windowEnd;
        }

        private List<DispatchWorkbenchStationDto> BuildWorkbenchStations(Entity line)
        {
            List<DispatchWorkbenchStationDto> stations = new List<DispatchWorkbenchStationDto>();
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            float cumulativeDistance = 0f;
            float3 previousPosition = float3.zero;
            bool hasPrevious = false;
            HashSet<Entity> seenStopEntities = new HashSet<Entity>();

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
                if (stopEntity == Entity.Null || !seenStopEntities.Add(stopEntity))
                {
                    continue;
                }

                Entity positionEntity = waypoint;
                if (EntityManager.HasComponent<Connected>(waypoint))
                {
                    Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                    if (connected != Entity.Null)
                    {
                        if (EntityManager.HasComponent<Transform>(connected))
                        {
                            positionEntity = connected;
                        }
                    }
                }

                if (!EntityManager.HasComponent<Transform>(positionEntity))
                    continue;

                float3 position = EntityManager.GetComponentData<Transform>(positionEntity).m_Position;
                if (hasPrevious)
                {
                    cumulativeDistance += math.distance(previousPosition, position);
                }

                previousPosition = position;
                hasPrevious = true;

                string name = ResolveWorkbenchStationName(stopEntity);
                if (string.IsNullOrEmpty(name))
                {
                    name = "Stop " + (stations.Count + 1).ToString();
                }

                stations.Add(new DispatchWorkbenchStationDto
                {
                    id = "station-" + stopEntity.Index,
                    name = name,
                    order = stations.Count,
                    distance = (float)Math.Round(cumulativeDistance, 1),
                    hasSiding = false
                });
            }

            return stations;
        }

        private void ResolveWorkbenchLineOrigin(Entity line, out string originStationId, out string originStationName)
        {
            originStationId = string.Empty;
            originStationName = string.Empty;

            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[i].m_Waypoint);
                if (stopEntity == Entity.Null)
                    continue;

                originStationId = "station-" + stopEntity.Index;
                originStationName = ResolveWorkbenchStationName(stopEntity);
                return;
            }
        }

        private string ResolveWorkbenchEntityName(Entity entity)
        {
            string translatedName = TryTranslateName(m_NameSystem.GetName(entity));
            if (!string.IsNullOrEmpty(translatedName))
            {
                return translatedName;
            }

            string renderedLabel = m_NameSystem.GetRenderedLabelName(entity);
            if (!string.IsNullOrEmpty(renderedLabel))
            {
                return renderedLabel;
            }

            return string.Empty;
        }

        private Entity ResolveWorkbenchStopEntity(Entity waypoint)
        {
            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                Entity stopEntity = FindOwnedTransportStop(connected);
                if (stopEntity != Entity.Null)
                {
                    return stopEntity;
                }
            }

            Entity waypointStop = FindOwnedTransportStop(waypoint);
            return waypointStop != Entity.Null ? waypointStop : Entity.Null;
        }

        private string ResolveWorkbenchStationName(Entity stopEntity)
        {
            Entity buildingEntity = FindTransportStationFromStop(stopEntity);
            if (buildingEntity != Entity.Null)
            {
                string buildingName = ResolveWorkbenchEntityName(buildingEntity);
                if (!string.IsNullOrEmpty(buildingName))
                {
                    return buildingName;
                }
            }

            return TryTranslateName(m_NameSystem.GetName(stopEntity));
        }

        private Entity FindOwnedTransportStop(Entity entity)
        {
            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Game.Routes.TransportStop>(current))
                {
                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        private Entity FindTransportStationFromStop(Entity stop)
        {
            Entity current = stop;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Game.Buildings.TransportStation>(current))
                {
                    if (EntityManager.HasComponent<Owner>(current))
                    {
                        Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                        if (owner != Entity.Null)
                        {
                            return owner;
                        }
                    }

                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        private int CountWorkbenchStops(Entity line)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return 0;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            HashSet<Entity> seenStopEntities = new HashSet<Entity>();

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[i].m_Waypoint);
                if (stopEntity != Entity.Null)
                {
                    seenStopEntities.Add(stopEntity);
                }
            }

            return seenStopEntities.Count;
        }

        private string ResolveWorkbenchLineColor(Entity line)
        {
            if (!EntityManager.HasComponent<Game.Routes.Color>(line))
            {
                return string.Empty;
            }

            UnityEngine.Color32 color = EntityManager.GetComponentData<Game.Routes.Color>(line).m_Color;
            return "#" + color.r.ToString("X2") + color.g.ToString("X2") + color.b.ToString("X2");
        }

        private string GetCurrentWorkbenchClockTime()
        {
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            if (nowMin < 0)
            {
                nowMin += 1440;
            }
            return FormatMinutes(nowMin);
        }

        private void RecordWorkbenchRealtimeStopEvent(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            int currentWaypointIndex,
            int previousWaypointIndex)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !EntityManager.Exists(vehicle))
            {
                return;
            }

            if (!m_WorkbenchRealtimeVehicles.TryGetValue(vehicle, out WorkbenchRealtimeVehicleRecord record))
            {
                record = new WorkbenchRealtimeVehicleRecord
                {
                    Vehicle = vehicle
                };
                m_WorkbenchRealtimeVehicles[vehicle] = record;
            }

            record.Line = line;
            record.Kind = "local";

            int stopWaypointIndex = boarding ? currentWaypointIndex : previousWaypointIndex;
            if (stopWaypointIndex < 0 || stopWaypointIndex >= waypoints.Length)
            {
                return;
            }

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[stopWaypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
            {
                return;
            }

            string nowTime = GetCurrentWorkbenchClockTime();
            uint nowFrame = m_SimulationSystem.frameIndex;
            bool isOriginStop = stopWaypointIndex == 0;

            WorkbenchRealtimeTripRecord activeTrip = record.Trips.Count > 0
                ? record.Trips[record.Trips.Count - 1]
                : null;
            if (activeTrip == null)
            {
                activeTrip = new WorkbenchRealtimeTripRecord
                {
                    Sequence = record.NextSequence++,
                    LastUpdatedFrame = nowFrame
                };
                record.Trips.Add(activeTrip);
                if (record.Trips.Count > 48)
                {
                    record.Trips.RemoveAt(0);
                }
            }

            WorkbenchRealtimeStopRecord stopRecord = activeTrip.Stops.Count > 0
                ? activeTrip.Stops[activeTrip.Stops.Count - 1]
                : null;
            bool canReuseLastStopRecord = stopRecord != null
                && stopRecord.StopEntity == stopEntity
                && ((boarding && string.IsNullOrEmpty(stopRecord.ArrivalTime))
                    || (!boarding && string.IsNullOrEmpty(stopRecord.DepartureTime)));
            if (!canReuseLastStopRecord)
            {
                stopRecord = new WorkbenchRealtimeStopRecord
                {
                    StopEntity = stopEntity
                };
                activeTrip.Stops.Add(stopRecord);
            }

            if (boarding)
            {
                stopRecord.ArrivalTime = nowTime;
            }
            else
            {
                stopRecord.DepartureTime = nowTime;
                if (string.IsNullOrEmpty(stopRecord.ArrivalTime))
                {
                    stopRecord.ArrivalTime = nowTime;
                }
            }

            stopRecord.LastUpdatedFrame = nowFrame;
            activeTrip.LastUpdatedFrame = nowFrame;

            string stopName = ResolveWorkbenchStationName(stopEntity);
            if (string.IsNullOrEmpty(stopName))
            {
                stopName = "Stop " + stopEntity.Index.ToString();
            }

            Mod.log.Info(
                "[WorkbenchRealtime] line="
                + line.Index
                + " vehicle="
                + vehicle.Index
                + " trip="
                + activeTrip.Sequence
                + " event="
                + (boarding ? "arrival" : "departure")
                + " stop=\""
                + stopName
                + "\" stopEntity="
                + stopEntity.Index
                + " wp="
                + stopWaypointIndex
                + " time="
                + nowTime
                + " stopCount="
                + activeTrip.Stops.Count
                + " origin="
                + (isOriginStop ? "1" : "0"));
        }

        private void BeginWorkbenchRealtimeTripAtLaunch(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (vehicle == Entity.Null || line == Entity.Null || !EntityManager.Exists(vehicle) || waypoints.Length == 0)
            {
                return;
            }

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[0].m_Waypoint);
            if (stopEntity == Entity.Null)
            {
                return;
            }

            if (!m_WorkbenchRealtimeVehicles.TryGetValue(vehicle, out WorkbenchRealtimeVehicleRecord record))
            {
                record = new WorkbenchRealtimeVehicleRecord
                {
                    Vehicle = vehicle
                };
                m_WorkbenchRealtimeVehicles[vehicle] = record;
            }

            string nowTime = GetCurrentWorkbenchClockTime();
            uint nowFrame = m_SimulationSystem.frameIndex;
            WorkbenchRealtimeTripRecord latestTrip = record.Trips.Count > 0
                ? record.Trips[record.Trips.Count - 1]
                : null;
            if (latestTrip != null
                && latestTrip.LastUpdatedFrame == nowFrame
                && latestTrip.Stops.Count == 1
                && latestTrip.Stops[0].StopEntity == stopEntity
                && latestTrip.Stops[0].DepartureTime == nowTime)
            {
                return;
            }

            WorkbenchRealtimeTripRecord trip = new WorkbenchRealtimeTripRecord
            {
                Sequence = record.NextSequence++,
                LastUpdatedFrame = nowFrame
            };
            trip.Stops.Add(new WorkbenchRealtimeStopRecord
            {
                StopEntity = stopEntity,
                ArrivalTime = nowTime,
                DepartureTime = nowTime,
                LastUpdatedFrame = nowFrame
            });

            record.Vehicle = vehicle;
            record.Line = line;
            record.Kind = "local";
            record.Trips.Add(trip);
            if (record.Trips.Count > 48)
            {
                record.Trips.RemoveAt(0);
            }

            string stopName = ResolveWorkbenchStationName(stopEntity);
            if (string.IsNullOrEmpty(stopName))
            {
                stopName = "Stop " + stopEntity.Index.ToString();
            }

            Mod.log.Info(
                "[WorkbenchRealtime] line="
                + line.Index
                + " vehicle="
                + vehicle.Index
                + " trip="
                + trip.Sequence
                + " event=launch-origin stop=\""
                + stopName
                + "\" stopEntity="
                + stopEntity.Index
                + " wp=0 time="
                + nowTime
                + " stopCount=1 origin=1");
        }

        private List<DispatchWorkbenchTripDto> BuildRealtimeWorkbenchTrips(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            List<DispatchWorkbenchTripDto> trips = new List<DispatchWorkbenchTripDto>();
            if (activeRuntime == null || stations.Count == 0)
            {
                return trips;
            }

            HashSet<string> allowedLineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in NormalizeLineIdList(draft?.MergedView?.localLineIds, draft?.MergedView?.localLineId, null))
            {
                allowedLineIds.Add(id);
            }
            foreach (string id in NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId, null))
            {
                allowedLineIds.Add(id);
            }
            allowedLineIds.Add(activeRuntime.Id);
            HashSet<string> expressLineIds = new HashSet<string>(
                NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId, null),
                StringComparer.Ordinal);
            if (allowedLineIds.Count == 0)
            {
                allowedLineIds.Add(activeRuntime.Id);
            }

            DynamicBuffer<RouteWaypoint> activeWaypoints = EntityManager.GetBuffer<RouteWaypoint>(activeRuntime.Entity, true);
            Dictionary<Entity, string> stationIdByStopEntity = new Dictionary<Entity, string>();
            HashSet<Entity> seenStops = new HashSet<Entity>();
            for (int i = 0; i < activeWaypoints.Length && i < stations.Count + 8; i++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(activeWaypoints[i].m_Waypoint);
                if (stopEntity == Entity.Null || !seenStops.Add(stopEntity))
                {
                    continue;
                }

                int stationIndex = stationIdByStopEntity.Count;
                if (stationIndex >= stations.Count)
                {
                    break;
                }
                stationIdByStopEntity[stopEntity] = stations[stationIndex].id;
            }

            foreach (KeyValuePair<Entity, WorkbenchRealtimeVehicleRecord> pair in m_WorkbenchRealtimeVehicles)
            {
                Entity vehicle = pair.Key;
                WorkbenchRealtimeVehicleRecord record = pair.Value;
                if (record == null || !EntityManager.Exists(vehicle))
                {
                    continue;
                }

                if (record.Line == Entity.Null || !EntityManager.Exists(record.Line))
                {
                    continue;
                }

                string lineId = record.Line.Index.ToString();
                if (!allowedLineIds.Contains(lineId))
                {
                    continue;
                }
                record.Kind = expressLineIds.Contains(lineId) ? "express" : "local";

                for (int tripIndex = 0; tripIndex < record.Trips.Count; tripIndex++)
                {
                    WorkbenchRealtimeTripRecord tripRecord = record.Trips[tripIndex];
                    if (tripRecord == null || tripRecord.Stops.Count == 0)
                    {
                        continue;
                    }

                    List<DispatchWorkbenchTripStopDto> stops = new List<DispatchWorkbenchTripStopDto>();
                    int previousStopMinutes = -1;
                    for (int i = 0; i < tripRecord.Stops.Count; i++)
                    {
                        WorkbenchRealtimeStopRecord stopRecord = tripRecord.Stops[i];
                        if (!stationIdByStopEntity.TryGetValue(stopRecord.StopEntity, out string stationId))
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(stopRecord.ArrivalTime) && string.IsNullOrEmpty(stopRecord.DepartureTime))
                        {
                            continue;
                        }

                        string time = !string.IsNullOrEmpty(stopRecord.DepartureTime) ? stopRecord.DepartureTime : stopRecord.ArrivalTime;
                        int stopMinutes = ParseTimeMinutes(time);
                        if (previousStopMinutes >= 0 && stopMinutes >= 0 && stopMinutes + 5 < previousStopMinutes)
                        {
                            break;
                        }

                        stops.Add(new DispatchWorkbenchTripStopDto
                        {
                            stationId = stationId,
                            time = time,
                            arrivalTime = string.IsNullOrEmpty(stopRecord.ArrivalTime) ? null : stopRecord.ArrivalTime,
                            departureTime = string.IsNullOrEmpty(stopRecord.DepartureTime) ? null : stopRecord.DepartureTime,
                            stopType = i == 0 ? "origin" : "normal"
                        });
                        previousStopMinutes = stopMinutes;
                    }

                    if (stops.Count == 0)
                    {
                        continue;
                    }

                    string realtimeFromStationId = null;
                    string realtimeToStationId = null;
                    string realtimeTime = null;
                    float realtimeProgress = 0f;
                    bool isLatestTrip = tripIndex == record.Trips.Count - 1;
                    bool isRunningVehicle = m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState)
                        && vehicleState == VehicleState.Running;
                    if (isLatestTrip
                        && isRunningVehicle
                        && record.Line == activeRuntime.Entity
                        && TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                    {
                        int previousWaypointIndex = nextWaypointIndex == 0 ? activeWaypoints.Length - 1 : nextWaypointIndex - 1;
                        Entity fromStopEntity = previousWaypointIndex >= 0 && previousWaypointIndex < activeWaypoints.Length
                            ? ResolveWorkbenchStopEntity(activeWaypoints[previousWaypointIndex].m_Waypoint)
                            : Entity.Null;
                        Entity toStopEntity = nextWaypointIndex >= 0 && nextWaypointIndex < activeWaypoints.Length
                            ? ResolveWorkbenchStopEntity(activeWaypoints[nextWaypointIndex].m_Waypoint)
                            : Entity.Null;

                        if (fromStopEntity != Entity.Null && stationIdByStopEntity.TryGetValue(fromStopEntity, out string fromStationId))
                        {
                            realtimeFromStationId = fromStationId;
                        }
                        if (toStopEntity != Entity.Null && stationIdByStopEntity.TryGetValue(toStopEntity, out string toStationId))
                        {
                            realtimeToStationId = toStationId;
                        }
                        realtimeProgress = math.saturate(segmentPosition);
                        realtimeTime = GetCurrentWorkbenchClockTime();
                    }

                    trips.Add(new DispatchWorkbenchTripDto
                    {
                        id = "RT-" + vehicle.Index.ToString() + "-" + tripRecord.Sequence.ToString(),
                        lineId = lineId,
                        kind = record.Kind,
                        depart = stops.FirstOrDefault()?.departureTime ?? stops.FirstOrDefault()?.time ?? "--:--",
                        realtimeSegment = 0,
                        realtimeProgress = realtimeProgress,
                        realtimeFromStationId = realtimeFromStationId,
                        realtimeToStationId = realtimeToStationId,
                        realtimeTime = realtimeTime,
                        stops = stops.ToArray()
                    });
                }
            }

            return trips;
        }

        private List<string> GetTargetTripLineIds(string kind, string requestedLineId, DispatchWorkbenchDraftState draft, string fallbackLineId)
        {
            if (!string.IsNullOrEmpty(requestedLineId))
            {
                return new List<string> { requestedLineId };
            }

            List<string> targetLineIds = kind == "express"
                ? NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId, null)
                : NormalizeLineIdList(draft?.MergedView?.localLineIds, draft?.MergedView?.localLineId, null);

            if (targetLineIds.Count == 0 && kind != "express" && !string.IsNullOrEmpty(fallbackLineId))
            {
                targetLineIds.Add(fallbackLineId);
            }

            return targetLineIds;
        }

        private static string TryTranslateName(NameSystem.Name name)
        {
            if (s_NameTypeField == null || s_NameIdField == null)
            {
                return string.Empty;
            }

            string nameId = s_NameIdField.GetValue(name) as string;
            if (string.IsNullOrEmpty(nameId))
            {
                return string.Empty;
            }

            NameSystem.NameType nameType = (NameSystem.NameType)s_NameTypeField.GetValue(name);
            switch (nameType)
            {
                case NameSystem.NameType.Custom:
                    return nameId;
                case NameSystem.NameType.Localized:
                    return TranslateLocalizationKey(nameId);
                case NameSystem.NameType.Formatted:
                    return FormatLocalizedName(nameId, s_NameArgsField?.GetValue(name) as string[]);
                default:
                    return nameId;
            }
        }

        private static string FormatLocalizedName(string nameId, string[] nameArgs)
        {
            string template = TranslateLocalizationKey(nameId);
            if (string.IsNullOrEmpty(template) || nameArgs == null || nameArgs.Length < 2)
            {
                return template;
            }

            for (int i = 0; i + 1 < nameArgs.Length; i += 2)
            {
                string token = nameArgs[i] ?? string.Empty;
                string valueKey = nameArgs[i + 1] ?? string.Empty;
                string replacement = TranslateLocalizationKey(valueKey);
                template = template.Replace("{" + token + "}", replacement);
            }

            return template;
        }

        private static string TranslateLocalizationKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary != null
                && Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary.TryGetValue(key, out string translated))
            {
                return translated;
            }

            return key;
        }

        private void LogWorkbenchSnapshot(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            List<DispatchWorkbenchTripDto> trips,
            DispatchWorkbenchDraftState draft)
        {
            string lineId = activeRuntime?.Id ?? "none";
            string lineName = activeRuntime?.Name ?? "none";
            string stationPreview = stations.Count == 0
                ? "-"
                : string.Join(" | ", stations.Take(4).Select(station => station.name ?? "-"));
            string tripPreview = trips == null || trips.Count == 0
                ? "-"
                : string.Join(" | ", trips.Take(2).Select(trip =>
                {
                    DispatchWorkbenchTripStopDto firstStop = trip.stops?.FirstOrDefault();
                    DispatchWorkbenchTripStopDto secondStop = trip.stops != null && trip.stops.Length > 1 ? trip.stops[1] : null;
                    string first = firstStop == null
                        ? "-"
                        : (firstStop.stationId + "@" + (firstStop.departureTime ?? firstStop.time ?? "--:--"));
                    string second = secondStop == null
                        ? "-"
                        : (secondStop.stationId + "@" + (secondStop.arrivalTime ?? secondStop.time ?? "--:--"));
                    return trip.id + "(" + trip.kind + "," + trip.lineId + "):" + first + "->" + second;
                }));
            int drawableTripCount = trips?.Count(trip => trip?.stops != null && trip.stops.Length >= 2) ?? 0;
            string localPreview = draft?.MergedView?.localLineIds != null
                ? string.Join(",", draft.MergedView.localLineIds)
                : draft?.MergedView?.localLineId ?? string.Empty;
            string expressPreview = draft?.MergedView?.expressLineIds != null
                ? string.Join(",", draft.MergedView.expressLineIds)
                : draft?.MergedView?.expressLineId ?? string.Empty;
            string logKey = lineId + "|" + stations.Count + "|" + (trips?.Count ?? 0) + "|" + localPreview + "|" + expressPreview;
            if (logKey == m_LastWorkbenchSnapshotLogKey)
            {
                return;
            }

            m_LastWorkbenchSnapshotLogKey = logKey;
            Mod.log.Info(
                "Workbench snapshot line="
                + lineId
                + " name=\""
                + lineName
                + "\" stations="
                + stations.Count
                + " local=["
                + localPreview
                + "] express=["
                + expressPreview
                + "] firstStops=["
                + stationPreview
                + "] trips="
                + (trips?.Count ?? 0)
                + " drawableTrips="
                + drawableTripCount
                + " firstTrips=["
                + tripPreview
                + "]");
        }

        private List<DispatchWorkbenchTripDto> BuildWorkbenchTrips(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            List<DispatchWorkbenchTripDto> trips = new List<DispatchWorkbenchTripDto>();
            if (stations.Count == 0)
                return trips;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(activeRuntime.Entity, true);
            if (!TryGetLineTimeProfile(activeRuntime.Entity, waypoints, out LineTimeProfileHeader profile))
                return trips;

            List<DispatchWorkbenchManualRowDto> generatedRows = GetWorkbenchGeneratedRows(draft);

            for (int i = 0; i < generatedRows.Count; i++)
            {
                DispatchWorkbenchManualRowDto row = generatedRows[i];
                int departMinutes = ParseTimeMinutes(row.time);
                if (departMinutes < 0)
                    continue;

                string tripLineId = row.kind == "express"
                    ? draft.MergedView.expressLineIds?.FirstOrDefault() ?? draft.MergedView.expressLineId
                    : draft.MergedView.localLineIds?.FirstOrDefault() ?? draft.MergedView.localLineId;
                if (row.kind == "express" && string.IsNullOrEmpty(tripLineId))
                {
                    continue;
                }

                List<DispatchWorkbenchTripStopDto> stops = new List<DispatchWorkbenchTripStopDto>();
                int runningMinutes = departMinutes;
                for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
                {
                    float stopMinutes = ProfileStopMinutes(profile, stationIndex);
                    if (stationIndex == 0)
                    {
                        stops.Add(new DispatchWorkbenchTripStopDto
                        {
                            stationId = stations[stationIndex].id,
                            time = FormatMinutes(runningMinutes),
                            departureTime = FormatMinutes(runningMinutes),
                            stopType = "origin"
                        });
                    }
                    else
                    {
                        runningMinutes += (int)Math.Round(ProfileSegmentMinutes(profile, stationIndex - 1));
                        string arrival = FormatMinutes(runningMinutes);
                        string departure = stationIndex == stations.Count - 1
                            ? null
                            : FormatMinutes(runningMinutes + (int)Math.Round(stopMinutes));
                        stops.Add(new DispatchWorkbenchTripStopDto
                        {
                            stationId = stations[stationIndex].id,
                            time = departure ?? arrival,
                            arrivalTime = arrival,
                            departureTime = departure,
                            stopType = stationIndex == stations.Count - 1 ? "terminal" : "normal"
                        });

                        if (stationIndex < stations.Count - 1)
                        {
                            runningMinutes += (int)Math.Round(stopMinutes);
                        }
                    }
                }

                trips.Add(new DispatchWorkbenchTripDto
                {
                    id = (row.kind == "express" ? "E" : "L") + (100 + i).ToString(),
                    lineId = tripLineId,
                    kind = row.kind,
                    depart = row.time,
                    realtimeSegment = Math.Max(0, Math.Min(stops.Count - 2, stops.Count / 2)),
                    realtimeProgress = 0.35f + (i % 3) * 0.18f,
                    stops = stops.ToArray()
                });
            }

            return trips;
        }

        private List<DispatchWorkbenchTripDto> BuildWorkbenchTripsStable(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            List<DispatchWorkbenchTripDto> trips = new List<DispatchWorkbenchTripDto>();
            if (stations.Count == 0)
                return trips;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(activeRuntime.Entity, true);
            bool hasProfile = TryGetLineTimeProfile(activeRuntime.Entity, waypoints, out LineTimeProfileHeader profile);

            List<DispatchWorkbenchManualRowDto> generatedRows = GetWorkbenchGeneratedRows(draft);

            for (int i = 0; i < generatedRows.Count; i++)
            {
                DispatchWorkbenchManualRowDto row = generatedRows[i];
                int departMinutes = ParseTimeMinutes(row.time);
                if (departMinutes < 0)
                    continue;

                List<string> targetLineIds = GetTargetTripLineIds(row.kind, row.lineId, draft, activeRuntime.Id);
                if (row.kind == "express" && targetLineIds.Count == 0)
                {
                    continue;
                }
                if (targetLineIds.Count == 0)
                {
                    targetLineIds.Add(activeRuntime.Id);
                }

                for (int lineIndex = 0; lineIndex < targetLineIds.Count; lineIndex++)
                {
                    string tripLineId = targetLineIds[lineIndex];
                    List<DispatchWorkbenchTripStopDto> stops = new List<DispatchWorkbenchTripStopDto>();
                    int runningMinutes = departMinutes;
                    for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
                    {
                        int stopMinutes = hasProfile
                            ? (int)Math.Round(ProfileStopMinutes(profile, stationIndex))
                            : EstimateStopMinutes(row.kind, stationIndex, stations.Count);

                        if (stationIndex == 0)
                        {
                            stops.Add(new DispatchWorkbenchTripStopDto
                            {
                                stationId = stations[stationIndex].id,
                                time = FormatMinutes(runningMinutes),
                                departureTime = FormatMinutes(runningMinutes),
                                stopType = "origin"
                            });
                            continue;
                        }

                        runningMinutes += hasProfile
                            ? (int)Math.Round(ProfileSegmentMinutes(profile, stationIndex - 1))
                            : EstimateSegmentMinutes(stations[stationIndex - 1], stations[stationIndex], row.kind);

                        string arrival = FormatMinutes(runningMinutes);
                        bool isTerminal = stationIndex == stations.Count - 1;
                        bool isExpressPass = !hasProfile
                            && row.kind == "express"
                            && stationIndex > 0
                            && stationIndex < stations.Count - 1;
                        string departure = isTerminal
                            ? null
                            : FormatMinutes(runningMinutes + stopMinutes);

                        stops.Add(new DispatchWorkbenchTripStopDto
                        {
                            stationId = stations[stationIndex].id,
                            time = departure ?? arrival,
                            arrivalTime = arrival,
                            departureTime = departure,
                            stopType = isTerminal
                                ? "terminal"
                                : isExpressPass
                                    ? (stations[stationIndex].hasSiding ? "pass_overtake" : "pass")
                                    : "normal"
                        });

                        if (!isTerminal)
                        {
                            runningMinutes += stopMinutes;
                        }
                    }

                    trips.Add(new DispatchWorkbenchTripDto
                    {
                        id = (row.kind == "express" ? "E" : "L") + (100 + i).ToString() + "-" + tripLineId,
                        lineId = tripLineId,
                        kind = row.kind,
                        depart = row.time,
                        realtimeSegment = Math.Max(0, Math.Min(stops.Count - 2, stops.Count / 2)),
                        realtimeProgress = 0.35f + ((i + lineIndex) % 3) * 0.18f,
                        stops = stops.ToArray()
                    });
                }
            }

            return trips;
        }

        private List<DispatchWorkbenchManualRowDto> GenerateAutoRuleRows(List<DispatchWorkbenchAutoRuleDto> rules)
        {
            List<DispatchWorkbenchManualRowDto> rows = new List<DispatchWorkbenchManualRowDto>();
            foreach (DispatchWorkbenchAutoRuleDto rule in rules)
            {
                if (!rule.enabled)
                    continue;

                int startMinutes = ParseTimeMinutes(rule.start);
                int endMinutes = ParseTimeMinutes(rule.end);
                if (startMinutes < 0 || endMinutes <= startMinutes)
                    continue;

                string ruleKind = string.IsNullOrEmpty(rule.kind)
                    ? (rule.expressPerHour > 0 && rule.localPerHour <= 0 ? "express" : "local")
                    : rule.kind;
                double departuresPerHour = rule.departuresPerHour > 0d
                    ? rule.departuresPerHour
                    : (ruleKind == "express" ? rule.expressPerHour : rule.localPerHour);

                if (ruleKind == "local" && departuresPerHour > 0d)
                {
                    double interval = 60d / departuresPerHour;
                    for (double minute = startMinutes; minute < endMinutes; minute += interval)
                    {
                        rows.Add(new DispatchWorkbenchManualRowDto
                        {
                            id = "auto-local-" + rule.id + "-" + minute.ToString("F0"),
                            lineId = rule.lineId,
                            time = FormatMinutes((int)Math.Round(minute)),
                            kind = "local",
                            offsetMode = "none",
                            offsetMinutes = string.Empty
                        });
                    }
                }

                if (ruleKind == "express" && departuresPerHour > 0d)
                {
                    double interval = 60d / departuresPerHour;
                    for (double minute = startMinutes; minute < endMinutes; minute += interval)
                    {
                        int adjusted = (int)Math.Round(minute) + (rule.expressOffsetMode == "before" ? -rule.expressOffsetMinutes : rule.expressOffsetMinutes);
                        rows.Add(new DispatchWorkbenchManualRowDto
                        {
                            id = "auto-express-" + rule.id + "-" + minute.ToString("F0"),
                            lineId = rule.lineId,
                            time = FormatMinutes(adjusted),
                            kind = "express",
                            offsetMode = rule.expressOffsetMode,
                            offsetMinutes = rule.expressOffsetMinutes.ToString()
                        });
                    }
                }
            }

            return rows;
        }

        private List<DispatchWorkbenchManualRowDto> GetWorkbenchGeneratedRows(DispatchWorkbenchDraftState draft)
        {
            if (draft.StagedRows != null && draft.StagedRows.Count > 0)
            {
                return draft.StagedRows
                    .Where(row => !string.IsNullOrEmpty(row.time))
                    .OrderBy(row => row.time, StringComparer.Ordinal)
                    .Select(row => new DispatchWorkbenchManualRowDto
                    {
                        id = row.id,
                        lineId = row.lineId,
                        time = row.time,
                        kind = string.IsNullOrEmpty(row.kind) ? "local" : row.kind,
                        offsetMode = "none",
                        offsetMinutes = string.Empty
                    })
                    .ToList();
            }

            List<DispatchWorkbenchManualRowDto> generatedRows = new List<DispatchWorkbenchManualRowDto>();
            generatedRows.AddRange(draft.ManualRows.Select(CloneManualRow));
            generatedRows.AddRange(GenerateAutoRuleRows(draft.AutoRules));
            return generatedRows
                .Where(row => !string.IsNullOrEmpty(row.time))
                .OrderBy(row => row.time, StringComparer.Ordinal)
                .ToList();
        }

        private float ProfileSegmentMinutes(LineTimeProfileHeader profile, int segmentIndex)
        {
            if (segmentIndex < 0 || segmentIndex >= profile.m_Count)
                return 0f;

            return m_LineTimeProfileSegmentFrames[profile.m_Offset + segmentIndex] / (float)SIM_FRAMES_PER_MINUTE;
        }

        private float ProfileStopMinutes(LineTimeProfileHeader profile, int stopIndex)
        {
            if (stopIndex < 0 || stopIndex >= profile.m_Count)
                return 0f;

            return m_LineTimeProfileStopFrames[profile.m_Offset + stopIndex] / (float)SIM_FRAMES_PER_MINUTE;
        }

        private static int EstimateSegmentMinutes(
            DispatchWorkbenchStationDto previousStation,
            DispatchWorkbenchStationDto nextStation,
            string kind)
        {
            float distanceDelta = math.abs(nextStation.distance - previousStation.distance);
            float metersPerMinute = kind == "express" ? 1200f : 900f;
            int estimate = (int)Math.Round(distanceDelta / metersPerMinute);
            return Math.Max(2, estimate);
        }

        private static int EstimateStopMinutes(string kind, int stationIndex, int stationCount)
        {
            if (stationIndex <= 0 || stationIndex >= stationCount - 1)
            {
                return 0;
            }

            return kind == "express" ? 0 : 1;
        }

        private static int ParseTimeMinutes(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 5 || value[2] != ':')
                return -1;

            if (!int.TryParse(value.Substring(0, 2), out int hour))
                return -1;
            if (!int.TryParse(value.Substring(3, 2), out int minute))
                return -1;
            if (hour == 24 && minute == 0)
                return 1440;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
                return -1;

            return hour * 60 + minute;
        }

        private static string MinutesToSlotString(int minute)
        {
            int normalized = ((minute % 1440) + 1440) % 1440;
            return (normalized / 60).ToString("00") + ":" + (normalized % 60).ToString("00");
        }

        private static string FormatMinutes(int totalMinutes)
        {
            int normalized = ((totalMinutes % 1440) + 1440) % 1440;
            return (normalized / 60).ToString("00") + ":" + (normalized % 60).ToString("00");
        }

        private static DispatchWorkbenchManualRowDto CloneManualRow(DispatchWorkbenchManualRowDto row)
        {
            return new DispatchWorkbenchManualRowDto
            {
                id = row.id,
                lineId = row.lineId,
                time = row.time,
                kind = row.kind,
                offsetMode = row.offsetMode,
                offsetMinutes = row.offsetMinutes
            };
        }

        private static DispatchWorkbenchAutoRuleDto CloneAutoRule(DispatchWorkbenchAutoRuleDto rule)
        {
            return new DispatchWorkbenchAutoRuleDto
            {
                id = rule.id,
                lineId = rule.lineId,
                enabled = rule.enabled,
                start = rule.start,
                end = rule.end,
                kind = rule.kind,
                departuresPerHour = rule.departuresPerHour,
                localPerHour = rule.localPerHour,
                expressPerHour = rule.expressPerHour,
                expressOffsetMode = rule.expressOffsetMode,
                expressOffsetMinutes = rule.expressOffsetMinutes
            };
        }

        private static DispatchWorkbenchStagedRowDto CloneStagedRow(DispatchWorkbenchStagedRowDto row)
        {
            return new DispatchWorkbenchStagedRowDto
            {
                id = row.id,
                lineId = row.lineId,
                time = row.time,
                kind = row.kind,
                source = row.source,
                note = row.note
            };
        }

        private static DispatchWorkbenchLineSettingDto CloneLineSetting(DispatchWorkbenchLineSettingDto setting)
        {
            if (setting == null)
            {
                return new DispatchWorkbenchLineSettingDto
                {
                    lineId = string.Empty,
                    originHoldLimitMinutes = DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES,
                    maxStationDwellMinutes = DEFAULT_MAX_STATION_DWELL_MINUTES,
                    serviceKind = string.Empty
                };
            }

            return new DispatchWorkbenchLineSettingDto
            {
                lineId = setting.lineId ?? string.Empty,
                originHoldLimitMinutes = NormalizeOriginHoldLimitMinutes(setting.originHoldLimitMinutes),
                maxStationDwellMinutes = NormalizeMaxStationDwellMinutes(setting.maxStationDwellMinutes),
                allowedDepotId = setting.allowedDepotId ?? string.Empty,
                serviceKind = NormalizeWorkbenchServiceKind(setting.serviceKind)
            };
        }

        private static int NormalizeOriginHoldLimitMinutes(int value)
        {
            if (value <= 0)
                return DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;

            return math.clamp(value, MIN_ORIGIN_HOLD_LIMIT_MINUTES, MAX_ORIGIN_HOLD_LIMIT_MINUTES);
        }

        private static int NormalizeMaxStationDwellMinutes(int value)
        {
            if (value <= 0)
                return DEFAULT_MAX_STATION_DWELL_MINUTES;

            return math.clamp(value, MIN_ORIGIN_HOLD_LIMIT_MINUTES, MAX_ORIGIN_HOLD_LIMIT_MINUTES);
        }

        private void ApplyWorkbenchLineSettings(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            if (settings == null)
                return;

            m_WorkbenchLineOriginHoldLimits.Clear();
            m_WorkbenchLineMaxStationDwellMinutes.Clear();
            m_WorkbenchLineAllowedDepots.Clear();
            m_WorkbenchLineServiceKinds.Clear();
            foreach (DispatchWorkbenchLineSettingDto setting in settings)
            {
                if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    continue;

                m_WorkbenchLineOriginHoldLimits[setting.lineId] =
                    NormalizeOriginHoldLimitMinutes(setting.originHoldLimitMinutes);
                m_WorkbenchLineMaxStationDwellMinutes[setting.lineId] =
                    NormalizeMaxStationDwellMinutes(setting.maxStationDwellMinutes);
                m_WorkbenchLineAllowedDepots[setting.lineId] = setting.allowedDepotId ?? string.Empty;
                string normalizedKind = NormalizeWorkbenchServiceKind(setting.serviceKind);
                if (!string.IsNullOrEmpty(normalizedKind))
                {
                    m_WorkbenchLineServiceKinds[setting.lineId] = normalizedKind;
                }
            }
        }

        private static string NormalizeWorkbenchServiceKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
                return string.Empty;

            return string.Equals(kind, "express", StringComparison.Ordinal)
                ? "express"
                : "local";
        }

        private int GetWorkbenchOriginHoldLimitMinutes(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;

            return m_WorkbenchLineOriginHoldLimits.TryGetValue(lineId, out int configuredMinutes)
                ? NormalizeOriginHoldLimitMinutes(configuredMinutes)
                : DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;
        }

        private string GetWorkbenchConfiguredLineServiceKind(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            return m_WorkbenchLineServiceKinds.TryGetValue(lineId, out string configuredKind)
                ? NormalizeWorkbenchServiceKind(configuredKind)
                : string.Empty;
        }

        private int GetWorkbenchMaxStationDwellMinutes(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return DEFAULT_MAX_STATION_DWELL_MINUTES;

            return m_WorkbenchLineMaxStationDwellMinutes.TryGetValue(lineId, out int configuredMinutes)
                ? NormalizeMaxStationDwellMinutes(configuredMinutes)
                : DEFAULT_MAX_STATION_DWELL_MINUTES;
        }

        private string GetWorkbenchAllowedDepotId(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            return m_WorkbenchLineAllowedDepots.TryGetValue(lineId, out string configuredDepotId)
                ? configuredDepotId ?? string.Empty
                : string.Empty;
        }

        private int GetWorkbenchOriginHoldLimitMinutes(Entity line)
        {
            return line == Entity.Null
                ? DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES
                : GetWorkbenchOriginHoldLimitMinutes(line.Index.ToString());
        }

        private int GetWorkbenchMaxStationDwellMinutes(Entity line)
        {
            return line == Entity.Null
                ? DEFAULT_MAX_STATION_DWELL_MINUTES
                : GetWorkbenchMaxStationDwellMinutes(line.Index.ToString());
        }

        public Entity GetConfiguredAllowedDepot(Entity line)
        {
            if (line == Entity.Null)
                return Entity.Null;

            string depotId = GetWorkbenchAllowedDepotId(line.Index.ToString());
            return ResolveWorkbenchDepotEntityById(depotId);
        }

        private string ResolveWorkbenchLineTransportType(Entity line)
        {
            if (line == Entity.Null || !EntityManager.HasComponent<PrefabRef>(line))
                return string.Empty;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<TransportLineData>(prefab))
                return string.Empty;

            return EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType.ToString();
        }

        private List<DispatchWorkbenchDepotDto> BuildWorkbenchDepots()
        {
            List<DispatchWorkbenchDepotDto> depots = new List<DispatchWorkbenchDepotDto>();
            EntityQuery depotQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
            NativeArray<Entity> depotEntities = depotQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity depot = depotEntities[i];
                    string name = ResolveWorkbenchEntityName(depot);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = "Depot " + depot.Index;
                    }

                    string transportType = string.Empty;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                    if (prefab != Entity.Null && EntityManager.HasComponent<TransportDepotData>(prefab))
                    {
                        transportType = EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType.ToString();
                    }

                    depots.Add(new DispatchWorkbenchDepotDto
                    {
                        id = depot.Index.ToString(),
                        name = name,
                        transportType = transportType
                    });
                }
            }
            finally
            {
                if (depotEntities.IsCreated) depotEntities.Dispose();
            }

            return depots
                .OrderBy(entry => entry.name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.id, StringComparer.Ordinal)
                .ToList();
        }

        private Entity ResolveWorkbenchDepotEntityById(string depotId)
        {
            if (string.IsNullOrEmpty(depotId))
                return Entity.Null;

            EntityQuery depotQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.Exclude<Deleted>());
            NativeArray<Entity> depotEntities = depotQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity depot = depotEntities[i];
                    if (string.Equals(depot.Index.ToString(), depotId, StringComparison.Ordinal))
                    {
                        return depot;
                    }
                }
            }
            finally
            {
                if (depotEntities.IsCreated) depotEntities.Dispose();
            }

            return Entity.Null;
        }

        private static bool AreManualRowsEquivalent(
            List<DispatchWorkbenchManualRowDto> left,
            List<DispatchWorkbenchManualRowDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchManualRowDto a = left[i];
                DispatchWorkbenchManualRowDto b = right[i];
                if (!string.Equals(a?.id, b?.id, StringComparison.Ordinal)
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || !string.Equals(a?.time, b?.time, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !string.Equals(a?.offsetMode, b?.offsetMode, StringComparison.Ordinal)
                    || !string.Equals(a?.offsetMinutes, b?.offsetMinutes, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreAutoRulesEquivalent(
            List<DispatchWorkbenchAutoRuleDto> left,
            List<DispatchWorkbenchAutoRuleDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchAutoRuleDto a = left[i];
                DispatchWorkbenchAutoRuleDto b = right[i];
                if (!string.Equals(a?.id, b?.id, StringComparison.Ordinal)
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || a.enabled != b.enabled
                    || !string.Equals(a?.start, b?.start, StringComparison.Ordinal)
                    || !string.Equals(a?.end, b?.end, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !AreClose(a.departuresPerHour, b.departuresPerHour)
                    || !AreClose(a.localPerHour, b.localPerHour)
                    || !AreClose(a.expressPerHour, b.expressPerHour)
                    || !string.Equals(a?.expressOffsetMode, b?.expressOffsetMode, StringComparison.Ordinal)
                    || a.expressOffsetMinutes != b.expressOffsetMinutes)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreClose(double left, double right)
        {
            return Math.Abs(left - right) < 0.000001d;
        }

        private static bool AreStagedRowsEquivalent(
            List<DispatchWorkbenchStagedRowDto> left,
            List<DispatchWorkbenchStagedRowDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchStagedRowDto a = left[i];
                DispatchWorkbenchStagedRowDto b = right[i];
                if (!string.Equals(a?.id, b?.id, StringComparison.Ordinal)
                    || !string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || !string.Equals(a?.time, b?.time, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !string.Equals(a?.source, b?.source, StringComparison.Ordinal)
                    || !string.Equals(a?.note, b?.note, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreStagedRowsEquivalentIgnoringIdAndNote(
            List<DispatchWorkbenchStagedRowDto> left,
            List<DispatchWorkbenchStagedRowDto> right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                DispatchWorkbenchStagedRowDto a = left[i];
                DispatchWorkbenchStagedRowDto b = right[i];
                if (!string.Equals(a?.lineId, b?.lineId, StringComparison.Ordinal)
                    || !string.Equals(a?.time, b?.time, StringComparison.Ordinal)
                    || !string.Equals(a?.kind, b?.kind, StringComparison.Ordinal)
                    || !string.Equals(a?.source, b?.source, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private List<string> ValidateWorkbenchRequest(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines)
        {
            List<string> errors = new List<string>();
            if (request == null)
            {
                errors.Add("Empty request.");
                return errors;
            }

            if (request.mergedView == null)
            {
                errors.Add("Merged view is required.");
                return errors;
            }

            List<string> localIds = NormalizeLineIdList(
                request.mergedView.localLineIds,
                request.mergedView.localLineId,
                runtimeLines);
            List<string> expressIds = NormalizeLineIdList(
                request.mergedView.expressLineIds,
                request.mergedView.expressLineId,
                runtimeLines);

            if (localIds.Count == 0)
            {
                errors.Add("At least one local line must be selected.");
            }

            if (runtimeLines != null && runtimeLines.Count > 0)
            {
                for (int i = 0; i < localIds.Count; i++)
                {
                    string id = localIds[i];
                    if (!runtimeLines.Any(line => line.Id == id))
                    {
                        errors.Add("Selected local line no longer exists.");
                    }
                }

                for (int i = 0; i < expressIds.Count; i++)
                {
                    string id = expressIds[i];
                    if (!runtimeLines.Any(line => line.Id == id))
                    {
                        errors.Add("Selected express line no longer exists.");
                    }
                }

                if (localIds.Any(id => expressIds.Contains(id)))
                {
                    errors.Add("Local and express lines cannot contain the same line.");
                }

            }

            Dictionary<string, WorkbenchLineRuntime> runtimeLineById = runtimeLines?
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .ToDictionary(line => line.Id, line => line, StringComparer.Ordinal)
                ?? new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            Dictionary<string, DispatchWorkbenchDepotDto> depotById = BuildWorkbenchDepots()
                .Where(depot => depot != null && !string.IsNullOrEmpty(depot.id))
                .ToDictionary(depot => depot.id, depot => depot, StringComparer.Ordinal);

            if (request.manualRows != null)
            {
                Dictionary<string, HashSet<string>> seenByLine = new Dictionary<string, HashSet<string>>();
                Dictionary<string, int> previousByLine = new Dictionary<string, int>();
                for (int i = 0; i < request.manualRows.Length; i++)
                {
                    DispatchWorkbenchManualRowDto row = request.manualRows[i];
                    if (string.IsNullOrEmpty(row.lineId))
                    {
                        errors.Add("Manual row " + row.id + " is missing lineId.");
                    }

                    int minutes = ParseTimeMinutes(row.time);
                    if (minutes < 0)
                    {
                        errors.Add("Manual row " + row.id + " has invalid time.");
                        continue;
                    }

                    string lineId = row.lineId ?? string.Empty;
                    if (!seenByLine.TryGetValue(lineId, out HashSet<string> seen))
                    {
                        seen = new HashSet<string>();
                        seenByLine[lineId] = seen;
                    }

                    string key = lineId + "|" + (row.kind ?? "local") + "|" + row.time;
                    if (!seen.Add(key))
                    {
                        errors.Add("Manual row " + row.id + " duplicates an existing departure.");
                    }

                    int previous = previousByLine.TryGetValue(lineId, out int previousValue) ? previousValue : -1;
                    if (previous >= 0 && minutes < previous)
                    {
                        errors.Add("Manual rows must be sorted ascending by time.");
                    }
                    previousByLine[lineId] = minutes;
                }
            }

            if (request.autoRules != null)
            {
                for (int i = 0; i < request.autoRules.Length; i++)
                {
                    DispatchWorkbenchAutoRuleDto rule = request.autoRules[i];
                    if (string.IsNullOrEmpty(rule.lineId))
                    {
                        errors.Add("Auto rule " + rule.id + " is missing lineId.");
                    }
                    int start = ParseTimeMinutes(rule.start);
                    int end = ParseTimeMinutes(rule.end);
                    if (start < 0 || end < 0 || end <= start)
                    {
                        errors.Add("Auto rule " + rule.id + " has an invalid time window.");
                    }

                    double departuresPerHour = rule.departuresPerHour > 0d
                        ? rule.departuresPerHour
                        : ("express".Equals(rule.kind, StringComparison.Ordinal)
                            ? rule.expressPerHour
                            : rule.localPerHour);

                    if (double.IsNaN(departuresPerHour)
                        || double.IsInfinity(departuresPerHour)
                        || departuresPerHour <= 0d
                        || rule.localPerHour < 0d
                        || rule.expressPerHour < 0d)
                    {
                        errors.Add("Auto rule " + rule.id + " has invalid frequency values.");
                    }

                    if (rule.expressOffsetMinutes < 0)
                    {
                        errors.Add("Auto rule " + rule.id + " has a negative express offset.");
                    }
                }
            }

            if (request.lineSettings != null)
            {
                HashSet<string> seenLineSettings = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < request.lineSettings.Length; i++)
                {
                    DispatchWorkbenchLineSettingDto setting = request.lineSettings[i];
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    {
                        errors.Add("Line setting is missing lineId.");
                        continue;
                    }

                    if (!seenLineSettings.Add(setting.lineId))
                    {
                        errors.Add("Line setting for " + setting.lineId + " is duplicated.");
                    }

                    if (setting.originHoldLimitMinutes < MIN_ORIGIN_HOLD_LIMIT_MINUTES
                        || setting.originHoldLimitMinutes > MAX_ORIGIN_HOLD_LIMIT_MINUTES)
                    {
                        errors.Add("Line setting " + setting.lineId + " has invalid origin hold limit.");
                    }

                    if (setting.maxStationDwellMinutes < MIN_ORIGIN_HOLD_LIMIT_MINUTES
                        || setting.maxStationDwellMinutes > MAX_ORIGIN_HOLD_LIMIT_MINUTES)
                    {
                        errors.Add("Line setting " + setting.lineId + " has invalid max station dwell limit.");
                    }

                    if (!string.IsNullOrEmpty(setting.allowedDepotId))
                    {
                        if (!depotById.TryGetValue(setting.allowedDepotId, out DispatchWorkbenchDepotDto depot))
                        {
                            errors.Add("Line setting " + setting.lineId + " references a depot that no longer exists.");
                        }
                        else if (runtimeLineById.TryGetValue(setting.lineId, out WorkbenchLineRuntime runtimeLine)
                            && !string.IsNullOrEmpty(runtimeLine.TransportType)
                            && !string.IsNullOrEmpty(depot.transportType)
                            && !string.Equals(runtimeLine.TransportType, depot.transportType, StringComparison.Ordinal))
                        {
                            errors.Add("Line setting " + setting.lineId + " references a depot with a different transport type.");
                        }
                    }
                }
            }

            if (request.stagedRows != null)
            {
                HashSet<string> stagedKeys = new HashSet<string>();
                Dictionary<string, string> stagedLineKinds = new Dictionary<string, string>();
                for (int i = 0; i < request.stagedRows.Length; i++)
                {
                    DispatchWorkbenchStagedRowDto row = request.stagedRows[i];
                    if (string.IsNullOrEmpty(row.lineId))
                    {
                        errors.Add("Staged row " + row.id + " is missing lineId.");
                    }

                    if (ParseTimeMinutes(row.time) < 0)
                    {
                        errors.Add("Staged row " + row.id + " has invalid time.");
                    }

                    string stagedKey = (row.lineId ?? string.Empty)
                        + "|"
                        + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                        + "|"
                        + (row.time ?? string.Empty);
                    if (!stagedKeys.Add(stagedKey))
                    {
                        errors.Add("Staged rows contain duplicate departures for the same line and service.");
                    }

                    string normalizedKind = string.IsNullOrEmpty(row.kind) ? "local" : row.kind;
                    if (stagedLineKinds.TryGetValue(row.lineId ?? string.Empty, out string existingKind))
                    {
                        if (!string.Equals(existingKind, normalizedKind, StringComparison.Ordinal))
                        {
                            errors.Add("Staged rows cannot mix local and express departures for the same line.");
                        }
                    }
                    else
                    {
                        stagedLineKinds[row.lineId ?? string.Empty] = normalizedKind;
                    }
                }
            }

            return errors;
        }

        private void LogWorkbenchException(string scope, Exception ex)
        {
            if (ex == null)
                return;

            log.Info("[WorkbenchException] " + scope + " -> " + DescribeWorkbenchException(ex));
        }

        private static string DescribeWorkbenchException(Exception ex)
        {
            if (ex == null)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            int depth = 0;
            Exception current = ex;
            while (current != null && depth < 8)
            {
                if (depth > 0)
                {
                    sb.Append(" | inner ");
                    sb.Append(depth);
                    sb.Append(": ");
                }

                sb.Append(current.GetType().Name);
                sb.Append(": ");
                sb.Append(current.Message);
                current = current.InnerException;
                depth++;
            }

            return sb.ToString();
        }
    }
}
