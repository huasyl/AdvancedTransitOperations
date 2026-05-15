using System;
using System.Collections.Generic;
using System.IO;
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
            public DispatchWorkbenchStagedRowDto[] lineDraftRows;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] combinedDraftRows;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] appliedRows;
            [DataMember(EmitDefaultValue = false)]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember(EmitDefaultValue = false)]
            public DispatchWorkbenchStagedRowDto[] combinedStagedRows;
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
            public DispatchWorkbenchStagedRowDto[] lineDraftRows;
            [DataMember]
            public DispatchWorkbenchLineDraftRowsDto[] lineDraftRowsByLineId;
            [DataMember(EmitDefaultValue = false)]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public DispatchWorkbenchLineSettingDto[] lineSettings;
            [DataMember]
            public bool markRulesApplied;
            [DataMember]
            public bool applyDraft;
            [DataMember]
            public bool nativeScheduleWriter;
            [DataMember]
            public DispatchWorkbenchPlannerImportContractDto plannerImportContract;
        }

        [DataContract]
        public class DispatchWorkbenchLineDraftRowsDto
        {
            [DataMember]
            public string lineId;
            [DataMember]
            public DispatchWorkbenchStagedRowDto[] lineDraftRows;
            [DataMember(EmitDefaultValue = false)]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
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
            [DataMember]
            public string broadcastAssetDirectory;
            [DataMember]
            public BroadcastWorkbenchPersistedAssetState[] broadcastAssets;
            [DataMember]
            public BroadcastWorkbenchPersistedLineBindingState[] broadcastDraftLineBindings;
            [DataMember]
            public BroadcastWorkbenchPersistedRuleState[] broadcastDraftRules;
            [DataMember]
            public BroadcastWorkbenchPersistedPlatformAnnouncementState[] broadcastDraftPlatformAnnouncements;
            [DataMember]
            public BroadcastWorkbenchPersistedLineBindingState[] broadcastLineBindings;
            [DataMember]
            public BroadcastWorkbenchPersistedRuleState[] broadcastRules;
            [DataMember]
            public BroadcastWorkbenchPersistedPlatformAnnouncementState[] broadcastPlatformAnnouncements;
            [DataMember]
            public BroadcastWorkbenchPersistedAppliedState broadcastAppliedState;
            [DataMember]
            public int? broadcastDraftVolume;
        }

        [DataContract]
        public class DispatchWorkbenchPlannerImportContractDto
        {
            [DataMember]
            public string draftKey;
            [DataMember]
            public string importedFrom;
            [DataMember]
            public string importedPlanId;
            [DataMember]
            public string importedObjectiveId;
            [DataMember]
            public string[] importedLineIds;
            [DataMember]
            public DispatchPlannerRequestEchoDto requestEcho;
            [DataMember]
            public DispatchPlannerPlanDetailDto plan;
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
            public DispatchWorkbenchStagedRowDto[] lineDraftRows;
            [DataMember(EmitDefaultValue = false)]
            public DispatchWorkbenchStagedRowDto[] stagedRows;
            [DataMember]
            public bool rulesApplied;
            [DataMember]
            public bool draftApplied;
            [DataMember]
            public DispatchWorkbenchPlannerImportContractDto plannerImportContract;
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
        public class DispatchWorkbenchStationConflictDto
        {
            [DataMember]
            public string assetName;
            [DataMember]
            public string suggestedLang;
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
            [DataMember]
            public DispatchWorkbenchStationConflictDto[] conflictAssets;
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
            public DispatchWorkbenchPlannerImportContractDto PlannerImportContract;
            public readonly Dictionary<string, int[]> AppliedDepartureMinutesCache = new Dictionary<string, int[]>(StringComparer.Ordinal);
        }

        private sealed class AppliedWorkbenchLineState
        {
            public Entity LineEntity = Entity.Null;
            public int OriginHoldLimitMinutes = DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;
            public int MaxStationDwellMinutes = DEFAULT_MAX_STATION_DWELL_MINUTES;
            public List<DispatchWorkbenchStagedRowDto> AppliedRows = new List<DispatchWorkbenchStagedRowDto>();
            public List<DispatchWorkbenchStagedRowDto> StagedRows
            {
                get => AppliedRows;
                set => AppliedRows = value ?? new List<DispatchWorkbenchStagedRowDto>();
            }
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

        private readonly struct WorkbenchLineFrameSnapshot
        {
            public readonly Entity Line;
            public readonly uint Frame;
            public readonly string LineId;
            public readonly string LineKey;
            public readonly bool TimetableApplied;
            public readonly string ConfiguredServiceKind;
            public readonly string AppliedServiceKind;
            public readonly string EffectiveServiceKind;

            public WorkbenchLineFrameSnapshot(
                Entity line,
                uint frame,
                string lineId,
                string lineKey,
                bool timetableApplied,
                string configuredServiceKind,
                string appliedServiceKind,
                string effectiveServiceKind)
            {
                Line = line;
                Frame = frame;
                LineId = lineId ?? string.Empty;
                LineKey = lineKey ?? string.Empty;
                TimetableApplied = timetableApplied;
                ConfiguredServiceKind = configuredServiceKind ?? string.Empty;
                AppliedServiceKind = appliedServiceKind ?? string.Empty;
                EffectiveServiceKind = effectiveServiceKind ?? string.Empty;
            }
        }

        private readonly struct ConfiguredAllowedDepotCacheEntry
        {
            public readonly Entity Line;
            public readonly string LineId;
            public readonly string AllowedDepotId;
            public readonly Entity CanonicalDepot;
            public readonly ulong SettingsVersion;

            public ConfiguredAllowedDepotCacheEntry(
                Entity line,
                string lineId,
                string allowedDepotId,
                Entity canonicalDepot,
                ulong settingsVersion)
            {
                Line = line;
                LineId = lineId ?? string.Empty;
                AllowedDepotId = allowedDepotId ?? string.Empty;
                CanonicalDepot = canonicalDepot;
                SettingsVersion = settingsVersion;
            }
        }

        private readonly Dictionary<string, DispatchWorkbenchDraftState> m_WorkbenchDrafts = new Dictionary<string, DispatchWorkbenchDraftState>();
        private readonly Dictionary<string, DispatchWorkbenchPlannerImportContractDto> m_AppliedPlannerImportContracts =
            new Dictionary<string, DispatchWorkbenchPlannerImportContractDto>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> m_WorkbenchLineOriginHoldLimits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> m_WorkbenchLineMaxStationDwellMinutes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineAllowedDepots = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineServiceKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, AppliedWorkbenchLineState> m_AppliedWorkbenchLines = new Dictionary<string, AppliedWorkbenchLineState>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, WorkbenchLineFrameSnapshot> m_WorkbenchLineFrameSnapshots = new Dictionary<Entity, WorkbenchLineFrameSnapshot>();
        private readonly Dictionary<Entity, ConfiguredAllowedDepotCacheEntry> m_ConfiguredAllowedDepotCacheByLine = new Dictionary<Entity, ConfiguredAllowedDepotCacheEntry>();
        private readonly Dictionary<Entity, WorkbenchRealtimeVehicleRecord> m_WorkbenchRealtimeVehicles = new Dictionary<Entity, WorkbenchRealtimeVehicleRecord>();
        private ulong m_WorkbenchSnapshotVersion = 1;
        private ulong m_WorkbenchLineSettingsVersion = 1;
        private uint m_ConfiguredAllowedDepotCacheLastLogFrame;
        private int m_ConfiguredAllowedDepotCacheCalls;
        private int m_ConfiguredAllowedDepotCacheHits;
        private int m_ConfiguredAllowedDepotCacheSettingsVersionMisses;
        private int m_ConfiguredAllowedDepotCacheLineIdMisses;
        private int m_ConfiguredAllowedDepotCacheAllowedDepotIdMisses;
        private int m_ConfiguredAllowedDepotCacheEntityInvalidations;
        private int m_ConfiguredAllowedDepotCacheFallbackResolves;
        private string m_LastWorkbenchSnapshotLogKey = string.Empty;
        private string m_LastAppliedWorkbenchInspectLogKey = string.Empty;
        private string m_WorkbenchPreferredLineId = string.Empty;
        private bool m_WorkbenchPersistenceLoaded = false;
        private bool m_AppliedWorkbenchPersistenceLoaded = false;
        private const int DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES = 20;
        private const int DEFAULT_MAX_STATION_DWELL_MINUTES = 10;
        private const int MIN_ORIGIN_HOLD_LIMIT_MINUTES = 5;
        private const int MAX_ORIGIN_HOLD_LIMIT_MINUTES = 120;
        private const uint CONFIGURED_ALLOWED_DEPOT_CACHE_LOG_INTERVAL_FRAMES = 3600u;
        private static readonly bool ENABLE_WORKBENCH_INTEGRITY_REPORT = true;

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ResetWorkbenchPersistenceStateForLoad();
            TryRestoreWorkbenchPersistence();
            TryRestoreAppliedWorkbenchPersistence();
        }

        private void ResetWorkbenchPersistenceStateForLoad()
        {
            m_WorkbenchDrafts.Clear();
            m_AppliedWorkbenchLines.Clear();
            m_AppliedPlannerImportContracts.Clear();
            m_WorkbenchLineOriginHoldLimits.Clear();
            m_WorkbenchLineMaxStationDwellMinutes.Clear();
            m_WorkbenchLineAllowedDepots.Clear();
            m_WorkbenchLineServiceKinds.Clear();
            m_WorkbenchLineFrameSnapshots.Clear();
            m_ConfiguredAllowedDepotCacheByLine.Clear();
            m_WorkbenchPreferredLineId = string.Empty;
            m_LastWorkbenchSnapshotLogKey = string.Empty;
            m_LastAppliedWorkbenchInspectLogKey = string.Empty;
            m_WorkbenchPersistenceLoaded = false;
            m_AppliedWorkbenchPersistenceLoaded = false;
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
                List<WorkbenchLineRuntime> runtimeLines = BuildWorkbenchLinesStable();
                bool lineSettingsChanged = request?.lineSettings != null;
                NormalizeRequestedLineSettingsFromMergedView(request, runtimeLines);
                List<string> errors = ValidateWorkbenchRequest(
                    request,
                    runtimeLines,
                    validateApplyOnlyConstraints: request?.applyDraft == true);
                WriteWorkbenchSaveRequestReport(request, runtimeLines, errors);
                if (errors.Count > 0)
                {
                    result.errors = errors.ToArray();
                    result.snapshot = BuildWorkbenchSnapshot(request?.selectedLineId);
                    return DispatchWorkbenchJson.Serialize(result);
                }

                string lineKey = GetDraftKey(request?.selectedLineId);
                DispatchWorkbenchDraftState state = GetOrCreateWorkbenchDraft(lineKey);
                Dictionary<string, List<DispatchWorkbenchStagedRowDto>> nextLineDraftRowsByKey =
                    BuildRequestLineDraftRowsByDraftKey(request, lineKey);
                List<DispatchWorkbenchManualRowDto> nextManualRows = request.manualRows != null
                    ? request.manualRows.Select(CloneManualRow).ToList()
                    : new List<DispatchWorkbenchManualRowDto>();
                List<DispatchWorkbenchAutoRuleDto> nextAutoRules = request.autoRules != null
                    ? request.autoRules.Select(CloneAutoRule).ToList()
                    : new List<DispatchWorkbenchAutoRuleDto>();
                bool hasActiveLineDraftRows = nextLineDraftRowsByKey.TryGetValue(
                    lineKey,
                    out List<DispatchWorkbenchStagedRowDto> activeLineDraftRows);
                List<DispatchWorkbenchStagedRowDto> nextStagedRows = hasActiveLineDraftRows
                    ? DeduplicateWorkbenchStagedRowsByIdPreservingLast(activeLineDraftRows)
                    : state.StagedRows.Select(CloneStagedRow).ToList();
                if (request.applyDraft)
                {
                    List<string> appliedErrors = ValidateAppliedWorkbenchCandidateRows(
                        lineKey,
                        nextLineDraftRowsByKey.Values.SelectMany(rows => rows).ToList(),
                        runtimeLines);
                    if (appliedErrors.Count > 0)
                    {
                        result.errors = appliedErrors.ToArray();
                        result.snapshot = BuildWorkbenchSnapshot(request.selectedLineId);
                        return DispatchWorkbenchJson.Serialize(result);
                    }
                }
                DispatchWorkbenchPlannerImportContractDto nextPlannerImportContract =
                    ClonePlannerImportContract(request.plannerImportContract);
                string requestedSelectedLineId = string.IsNullOrEmpty(request.selectedLineId) ? lineKey : request.selectedLineId;
                string requestedSelectedEditLine = string.IsNullOrEmpty(request.selectedEditLine) ? "local" : request.selectedEditLine;
                bool hasAdditionalLineDraftTargets = nextLineDraftRowsByKey.Keys
                    .Any(key => !string.Equals(key, lineKey, StringComparison.Ordinal));
                if (!request.applyDraft
                    && !request.markRulesApplied
                    && !hasAdditionalLineDraftTargets
                    && string.Equals(state.SelectedLineId ?? string.Empty, requestedSelectedLineId, StringComparison.Ordinal)
                    && string.Equals(state.SelectedEditLine ?? string.Empty, requestedSelectedEditLine, StringComparison.Ordinal)
                    && AreMergedViewsEquivalent(state.MergedView, request.mergedView)
                    && AreManualRowsEquivalent(state.ManualRows, nextManualRows)
                    && AreAutoRulesEquivalent(state.AutoRules, nextAutoRules)
                    && AreStagedRowsEquivalent(state.StagedRows, nextStagedRows)
                    && ArePlannerImportContractsEquivalent(state.PlannerImportContract, nextPlannerImportContract)
                    && AreWorkbenchLineSettingsEquivalent(request.lineSettings))
                {
                    result.success = true;
                    result.version = m_WorkbenchSnapshotVersion.ToString();
                    result.snapshot = null;
                    return DispatchWorkbenchJson.Serialize(result);
                }
                if (request.lineSettings != null)
                {
                    ApplyWorkbenchLineSettings(request.lineSettings);
                }
                bool rulesChanged = !AreManualRowsEquivalent(state.ManualRows, nextManualRows)
                    || !AreAutoRulesEquivalent(state.AutoRules, nextAutoRules)
                    || !AreStagedRowsEquivalent(state.StagedRows, nextStagedRows);

                state.SelectedLineId = requestedSelectedLineId;
                state.SelectedEditLine = requestedSelectedEditLine;
                state.MergedView = request.mergedView ?? state.MergedView;
                state.ManualRows = nextManualRows;
                state.AutoRules = nextAutoRules;
                state.StagedRows = nextStagedRows;
                if (nextPlannerImportContract != null)
                {
                    if (string.IsNullOrEmpty(nextPlannerImportContract.draftKey))
                    {
                        nextPlannerImportContract.draftKey = lineKey;
                    }
                    state.PlannerImportContract = nextPlannerImportContract;
                }
                else if (rulesChanged)
                {
                    state.PlannerImportContract = null;
                }
                bool additionalDraftRowsChanged = ApplyAdditionalLineDraftRowsByDraftKey(
                    lineKey,
                    nextLineDraftRowsByKey,
                    nextPlannerImportContract);
                if (additionalDraftRowsChanged)
                {
                    rulesChanged = true;
                }
                RemoveWorkbenchRowsFromOtherDrafts(
                    new HashSet<string>(nextLineDraftRowsByKey.Keys.Concat(new[] { lineKey }), StringComparer.Ordinal),
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
                    SeedRuntimeObservationFromAppliedWorkbenchState(state.SelectedLineId);
                }
                else
                {
                    RefreshAppliedWorkbenchLineSettings();
                    if (lineSettingsChanged)
                    {
                        InvalidateAppliedWorkbenchTrackModelState();
                    }
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
            List<DispatchWorkbenchStagedRowDto> activeStagedRows = new List<DispatchWorkbenchStagedRowDto>();
            List<DispatchWorkbenchStagedRowDto> mergedStagedRows = new List<DispatchWorkbenchStagedRowDto>();
            HashSet<string> mergedManualRowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mergedAutoRuleIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> mergedStagedRowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> activeStagedRowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> validRuntimeLineIds = new HashSet<string>(
                runtimeLines.Select(line => line.Id),
                StringComparer.Ordinal);
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
            CollectWorkbenchDraftRows(draftKey, validRuntimeLineIds, mergedManualRows, mergedAutoRules, activeStagedRows, mergedManualRowIds, mergedAutoRuleIds, activeStagedRowIds);
            CollectWorkbenchDraftRows(draftKey, validRuntimeLineIds, new List<DispatchWorkbenchManualRowDto>(), new List<DispatchWorkbenchAutoRuleDto>(), mergedStagedRows, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), mergedStagedRowIds);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.Equals(entry.Key, draftKey, StringComparison.Ordinal))
                    continue;
                CollectWorkbenchDraftRows(entry.Key, validRuntimeLineIds, mergedManualRows, mergedAutoRules, mergedStagedRows, mergedManualRowIds, mergedAutoRuleIds, mergedStagedRowIds);
            }
            List<DispatchWorkbenchTripDto> trips = activeRuntime != null
                ? BuildRealtimeWorkbenchTrips(activeRuntime, stations, draft)
                : new List<DispatchWorkbenchTripDto>();
            List<DispatchWorkbenchDepotDto> depots = BuildWorkbenchDepots();

            LogWorkbenchSnapshot(activeRuntime, stations, trips, draft, activeStagedRows, mergedStagedRows);
            WriteWorkbenchIntegrityReport(
                "snapshot",
                activeRuntime,
                draftKey,
                draft,
                runtimeLines,
                activeStagedRows,
                mergedStagedRows);

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
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(line.Entity),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line.Entity),
                    transportType = line.TransportType,
                    allowedDepotId = GetWorkbenchAllowedDepotId(line.Entity)
                }).ToArray(),
                depots = depots.ToArray(),
                stations = stations.ToArray(),
                trips = trips.ToArray(),
                manualRows = mergedManualRows.ToArray(),
                autoRules = mergedAutoRules.ToArray(),
                lineDraftRows = activeStagedRows.ToArray(),
                combinedDraftRows = mergedStagedRows.ToArray(),
                appliedRows = BuildAppliedWorkbenchRowsSnapshot(),
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
                    originHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(line.Entity),
                    maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line.Entity),
                    transportType = line.TransportType,
                    allowedDepotId = GetWorkbenchAllowedDepotId(line.Entity)
                }).ToArray(),
                depots = depots.ToArray(),
                stations = Array.Empty<DispatchWorkbenchStationDto>(),
                trips = Array.Empty<DispatchWorkbenchTripDto>(),
                manualRows = Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                lineDraftRows = Array.Empty<DispatchWorkbenchStagedRowDto>(),
                combinedDraftRows = Array.Empty<DispatchWorkbenchStagedRowDto>(),
                appliedRows = BuildAppliedWorkbenchRowsSnapshot(),
                version = m_WorkbenchSnapshotVersion.ToString(),
                sourceMode = "game-backend",
                rulesApplied = false,
                draftApplied = false
            };
        }

        private void CollectWorkbenchDraftRows(
            string lineKey,
            HashSet<string> validRuntimeLineIds,
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
                    if (row != null
                        && validRuntimeLineIds.Contains(row.lineId ?? string.Empty)
                        && manualRowIds.Add(row.id ?? string.Empty))
                    {
                        manualRows.Add(CloneManualRow(row));
                    }
                }
            }

            if (draft.AutoRules != null)
            {
                foreach (DispatchWorkbenchAutoRuleDto rule in draft.AutoRules)
                {
                    if (rule != null
                        && validRuntimeLineIds.Contains(rule.lineId ?? string.Empty)
                        && autoRuleIds.Add(rule.id ?? string.Empty))
                    {
                        autoRules.Add(CloneAutoRule(rule));
                    }
                }
            }

            if (draft.StagedRows != null)
            {
                HashSet<string> stagedRowSemanticKeys = new HashSet<string>(
                    stagedRows.Select(BuildWorkbenchStagedRowSemanticKey),
                    StringComparer.Ordinal);
                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    if (row != null
                        && validRuntimeLineIds.Contains(row.lineId ?? string.Empty)
                        && stagedRowIds.Add(row.id ?? string.Empty)
                        && stagedRowSemanticKeys.Add(BuildWorkbenchStagedRowSemanticKey(row)))
                    {
                        stagedRows.Add(CloneStagedRow(row));
                    }
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

                    string lineId = GetWorkbenchLineId(line);
                    string kind = GetEffectiveWorkbenchLineServiceKind(lineId, null);
                    if (string.IsNullOrEmpty(kind))
                    {
                        kind = "local";
                    }

                    lines.Add(new WorkbenchLineRuntime
                    {
                        Entity = line,
                        Id = lineId,
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

                    string lineKey = GetDraftKey(GetWorkbenchLineId(line));
                    m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState appliedState);
                    string kind = GetEffectiveWorkbenchLineServiceKind(line, appliedState);
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

        private string GetWorkbenchLineId(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line))
                return string.Empty;

            string transportType = ResolveWorkbenchLineTransportType(line);
            int routeNumber = int.MaxValue;
            if (EntityManager.HasComponent<RouteNumber>(line))
            {
                routeNumber = EntityManager.GetComponentData<RouteNumber>(line).m_Number;
            }

            if (routeNumber != int.MaxValue)
            {
                string prefix = string.IsNullOrEmpty(transportType)
                    ? "line"
                    : transportType.ToLowerInvariant();
                return prefix + ":" + routeNumber.ToString();
            }

            return line.Index.ToString();
        }

        private bool TryGetWorkbenchLineFrameSnapshot(Entity line, out WorkbenchLineFrameSnapshot snapshot)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            snapshot = default;
            if (line == Entity.Null || !EntityManager.Exists(line))
            {
                m_WorkbenchLineFrameSnapshots.Remove(line);
                return false;
            }

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_WorkbenchLineFrameSnapshots.TryGetValue(line, out snapshot)
                && snapshot.Line == line
                && snapshot.Frame == nowFrame)
            {
                m_PerfProbeWorkbenchLineFrameSnapshotHits++;
                return true;
            }

            m_PerfProbeWorkbenchLineFrameSnapshotMisses++;
            string lineId = GetWorkbenchLineId(line);
            string lineKey = GetDraftKey(lineId);
            bool timetableApplied = m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState appliedState);
            string configuredServiceKind = GetWorkbenchConfiguredLineServiceKind(lineId);
            string appliedServiceKind = timetableApplied
                ? GetAppliedWorkbenchLineServiceKind(appliedState)
                : string.Empty;
            string effectiveServiceKind = !string.IsNullOrEmpty(configuredServiceKind)
                ? configuredServiceKind
                : appliedServiceKind;

            snapshot = new WorkbenchLineFrameSnapshot(
                line,
                nowFrame,
                lineId,
                lineKey,
                timetableApplied,
                configuredServiceKind,
                appliedServiceKind,
                effectiveServiceKind);
            m_WorkbenchLineFrameSnapshots[line] = snapshot;
            return true;
        }

        private void InvalidateWorkbenchLineFrameSnapshots()
        {
            m_WorkbenchLineFrameSnapshots.Clear();
        }

        private void InvalidateConfiguredAllowedDepotCache()
        {
            m_ConfiguredAllowedDepotCacheByLine.Clear();
            m_WorkbenchLineSettingsVersion++;
        }

        private void MaybeLogConfiguredAllowedDepotCacheStats(uint nowFrame)
        {
            if (m_ConfiguredAllowedDepotCacheLastLogFrame != 0
                && (nowFrame - m_ConfiguredAllowedDepotCacheLastLogFrame) < CONFIGURED_ALLOWED_DEPOT_CACHE_LOG_INTERVAL_FRAMES)
            {
                return;
            }

            if (m_ConfiguredAllowedDepotCacheCalls > 0)
            {
                Mod.log.Info(
                    "[ConfiguredDepotCache] intervalFrames=" + CONFIGURED_ALLOWED_DEPOT_CACHE_LOG_INTERVAL_FRAMES
                    + " calls=" + m_ConfiguredAllowedDepotCacheCalls
                    + " hits=" + m_ConfiguredAllowedDepotCacheHits
                    + " settingsMiss=" + m_ConfiguredAllowedDepotCacheSettingsVersionMisses
                    + " lineIdMiss=" + m_ConfiguredAllowedDepotCacheLineIdMisses
                    + " depotIdMiss=" + m_ConfiguredAllowedDepotCacheAllowedDepotIdMisses
                    + " entityInvalid=" + m_ConfiguredAllowedDepotCacheEntityInvalidations
                    + " fallbackResolve=" + m_ConfiguredAllowedDepotCacheFallbackResolves);
            }

            m_ConfiguredAllowedDepotCacheLastLogFrame = nowFrame;
            m_ConfiguredAllowedDepotCacheCalls = 0;
            m_ConfiguredAllowedDepotCacheHits = 0;
            m_ConfiguredAllowedDepotCacheSettingsVersionMisses = 0;
            m_ConfiguredAllowedDepotCacheLineIdMisses = 0;
            m_ConfiguredAllowedDepotCacheAllowedDepotIdMisses = 0;
            m_ConfiguredAllowedDepotCacheEntityInvalidations = 0;
            m_ConfiguredAllowedDepotCacheFallbackResolves = 0;
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
                return false;
            }

            var buffer = EntityManager.GetBuffer<WorkbenchTimetableStateElement>(city, true);
            if (buffer.Length == 0)
            {
                m_WorkbenchPersistenceLoaded = true;
                return true;
            }

            try
            {
                WorkbenchTimetableStateElement[] orderedEntries = new WorkbenchTimetableStateElement[buffer.Length];
                for (int i = 0; i < buffer.Length; i++)
                {
                    orderedEntries[i] = buffer[i];
                }

                Array.Sort(orderedEntries, (left, right) => left.m_ChunkIndex.CompareTo(right.m_ChunkIndex));

                StringBuilder payloadBuilder = new StringBuilder();
                for (int i = 0; i < orderedEntries.Length; i++)
                {
                    payloadBuilder.Append(orderedEntries[i].m_PayloadChunk.ToString());
                }

                string payloadJson = payloadBuilder.ToString();

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
                    Mod.log.Info("[WorkbenchRestore] drafts=" + m_WorkbenchDrafts.Count
                        + " " + SummarizeWorkbenchDraftRows());
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

            m_AppliedPlannerImportContracts.Clear();
            Entity city = m_CitySystem.City;
            if (city == Entity.Null)
            {
                m_AppliedWorkbenchLines.Clear();
                return false;
            }

            if (!EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city)
                && !EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                m_AppliedWorkbenchLines.Clear();
                return false;
            }

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

                        string lineKey = GetDraftKey(GetWorkbenchLineId(entry.m_LineEntity));
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

                        string lineKey = GetDraftKey(GetWorkbenchLineId(row.m_LineEntity));
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
                    state.DepartureMinutesCache = BuildAppliedDepartureMinutes(state.StagedRows, GetWorkbenchLineId(state.LineEntity));
                }

                RecoverAppliedStagedRowsFromDrafts();
                SyncWorkbenchDraftsFromAppliedState();
                RefreshAppliedPlannerImportContractsFromDrafts();
                InvalidateWorkbenchLineFrameSnapshots();
                InvalidateAppliedWorkbenchTrackModelState();
                if (m_AppliedWorkbenchLines.Values.Any(
                    state => state != null && state.StagedRows != null && state.StagedRows.Count > 0))
                {
                    SeedRuntimeObservationFromAppliedWorkbenchState(GetPreferredWorkbenchLineId());
                }
                Mod.log.Info("[AppliedWorkbenchRestore] lines=" + m_AppliedWorkbenchLines.Count
                    + " " + SummarizeAppliedWorkbenchRows());
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

            RefreshAppliedPlannerImportContractsFromDrafts();
            SyncWorkbenchDraftsFromAppliedState();
            InvalidateWorkbenchLineFrameSnapshots();
            InvalidateAppliedWorkbenchTrackModelState();
        }

        private void InvalidateAppliedWorkbenchTrackModelState()
        {
            m_SharedTrackIndexDirty = true;
            ClearBypassRuntimeState();
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

        private void RecoverAppliedStagedRowsFromDrafts()
        {
            Dictionary<string, Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>> rowsByLineAndKey =
                new Dictionary<string, Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied || draft.StagedRows == null || draft.StagedRows.Count == 0)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    if (row == null || string.IsNullOrEmpty(row.lineId))
                        continue;

                    if (!rowsByLineAndKey.TryGetValue(row.lineId, out Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>> rowQueues))
                    {
                        rowQueues = new Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>>(StringComparer.Ordinal);
                        rowsByLineAndKey[row.lineId] = rowQueues;
                    }

                    string matchingKey = BuildWorkbenchStagedRowMatchingKey(row);
                    if (!rowQueues.TryGetValue(matchingKey, out Queue<DispatchWorkbenchStagedRowDto> queue))
                    {
                        queue = new Queue<DispatchWorkbenchStagedRowDto>();
                        rowQueues[matchingKey] = queue;
                    }

                    queue.Enqueue(CloneStagedRow(row));
                }
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.StagedRows == null || state.StagedRows.Count == 0)
                    continue;

                string lineId = GetWorkbenchLineId(state.LineEntity);
                if (string.IsNullOrEmpty(lineId)
                    || !rowsByLineAndKey.TryGetValue(lineId, out Dictionary<string, Queue<DispatchWorkbenchStagedRowDto>> rowQueues))
                {
                    continue;
                }

                for (int i = 0; i < state.StagedRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = state.StagedRows[i];
                    string matchingKey = BuildWorkbenchStagedRowMatchingKey(row);
                    if (!rowQueues.TryGetValue(matchingKey, out Queue<DispatchWorkbenchStagedRowDto> queue)
                        || queue.Count == 0)
                    {
                        continue;
                    }

                    DispatchWorkbenchStagedRowDto restoredRow = queue.Dequeue();
                    if (!string.IsNullOrEmpty(restoredRow.id))
                    {
                        row.id = restoredRow.id;
                    }
                    if (!string.IsNullOrEmpty(restoredRow.note))
                    {
                        row.note = restoredRow.note;
                    }
                }
            }
        }

        private void RefreshAppliedPlannerImportContractsFromDrafts()
        {
            m_AppliedPlannerImportContracts.Clear();

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft == null || !draft.DraftApplied || draft.PlannerImportContract == null)
                    continue;

                DispatchWorkbenchPlannerImportContractDto contract = ClonePlannerImportContract(draft.PlannerImportContract);
                if (contract == null)
                    continue;

                if (string.IsNullOrEmpty(contract.draftKey))
                {
                    contract.draftKey = entry.Key;
                }

                m_AppliedPlannerImportContracts[entry.Key] = contract;
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

        private static DispatchWorkbenchStagedRowDto[] GetRequestLineDraftRows(DispatchWorkbenchSaveRequest request)
        {
            return request?.lineDraftRows
                ?? request?.stagedRows
                ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
        }

        private static DispatchWorkbenchStagedRowDto[] GetLineDraftRowsBlockRows(DispatchWorkbenchLineDraftRowsDto block)
        {
            return block?.lineDraftRows
                ?? block?.stagedRows
                ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
        }

        private static Dictionary<string, List<DispatchWorkbenchStagedRowDto>> BuildRequestLineDraftRowsByDraftKey(
            DispatchWorkbenchSaveRequest request,
            string fallbackLineKey)
        {
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey =
                new Dictionary<string, List<DispatchWorkbenchStagedRowDto>>(StringComparer.Ordinal);
            string fallbackKey = GetDraftKeyStatic(fallbackLineKey);

            if (request?.lineDraftRowsByLineId != null && request.lineDraftRowsByLineId.Length > 0)
            {
                foreach (DispatchWorkbenchLineDraftRowsDto block in request.lineDraftRowsByLineId)
                {
                    string targetKey = GetDraftKeyStatic(block?.lineId);
                    if (string.IsNullOrEmpty(targetKey))
                    {
                        targetKey = fallbackKey;
                    }

                    rowsByDraftKey[targetKey] = GetLineDraftRowsBlockRows(block)
                        .Select(CloneStagedRow)
                        .ToList();
                }

                return rowsByDraftKey;
            }

            DispatchWorkbenchStagedRowDto[] rows = GetRequestLineDraftRows(request);
            if (rows.Length == 0)
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
                return rowsByDraftKey;
            }

            foreach (IGrouping<string, DispatchWorkbenchStagedRowDto> group in rows
                .Where(row => row != null)
                .GroupBy(row => GetDraftKeyStatic(string.IsNullOrEmpty(row.lineId) ? fallbackKey : row.lineId), StringComparer.Ordinal))
            {
                rowsByDraftKey[group.Key] = group.Select(CloneStagedRow).ToList();
            }

            if (!rowsByDraftKey.ContainsKey(fallbackKey))
            {
                rowsByDraftKey[fallbackKey] = new List<DispatchWorkbenchStagedRowDto>();
            }

            return rowsByDraftKey;
        }

        private bool ApplyAdditionalLineDraftRowsByDraftKey(
            string activeLineKey,
            Dictionary<string, List<DispatchWorkbenchStagedRowDto>> rowsByDraftKey,
            DispatchWorkbenchPlannerImportContractDto plannerImportContract)
        {
            if (rowsByDraftKey == null || rowsByDraftKey.Count == 0)
                return false;

            bool changed = false;
            foreach (KeyValuePair<string, List<DispatchWorkbenchStagedRowDto>> entry in rowsByDraftKey)
            {
                string draftKey = GetDraftKey(entry.Key);
                if (string.Equals(draftKey, activeLineKey, StringComparison.Ordinal))
                    continue;

                DispatchWorkbenchDraftState draft = GetOrCreateWorkbenchDraft(draftKey);
                List<DispatchWorkbenchStagedRowDto> nextRows =
                    DeduplicateWorkbenchStagedRowsByIdPreservingLast(entry.Value?.Select(CloneStagedRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>());
                bool draftRowsChanged = !AreStagedRowsEquivalent(draft.StagedRows, nextRows);
                if (!draftRowsChanged)
                    continue;

                draft.SelectedLineId = draftKey;
                draft.SelectedEditLine = draftKey == "__default__" ? string.Empty : draftKey;
                draft.StagedRows = nextRows;
                draft.RulesApplied = false;
                draft.DraftApplied = false;
                draft.AppliedDepartureMinutesCache.Clear();
                if (plannerImportContract != null)
                {
                    DispatchWorkbenchPlannerImportContractDto contract = ClonePlannerImportContract(plannerImportContract);
                    if (contract != null)
                    {
                        contract.draftKey = draftKey;
                        draft.PlannerImportContract = contract;
                    }
                }
                else
                {
                    draft.PlannerImportContract = null;
                }
                changed = true;
            }

            return changed;
        }

        private void RemoveWorkbenchRowsFromOtherDrafts(HashSet<string> skippedDraftKeys, HashSet<string> lineIds)
        {
            if (lineIds == null || lineIds.Count == 0)
                return;

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                if (skippedDraftKeys != null && skippedDraftKeys.Contains(entry.Key))
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
                draft.PlannerImportContract = null;
                draft.AppliedDepartureMinutesCache.Clear();
            }
        }

        private void SyncWorkbenchDraftsFromAppliedState()
        {
            HashSet<string> appliedLineKeys = new HashSet<string>(m_AppliedWorkbenchLines.Keys, StringComparer.Ordinal);
            Dictionary<string, bool> draftAppliedBeforeSync = m_WorkbenchDrafts
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.DraftApplied == true,
                    StringComparer.Ordinal);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> draftEntry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = draftEntry.Value;
                if (draft == null)
                    continue;

                if (!appliedLineKeys.Contains(draftEntry.Key))
                {
                    draft.DraftApplied = false;
                }
                draft.AppliedDepartureMinutesCache.Clear();
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                string lineKey = entry.Key;
                AppliedWorkbenchLineState applied = entry.Value;
                bool draftExisted = m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft);
                bool draftWasApplied = draftAppliedBeforeSync.TryGetValue(lineKey, out bool wasApplied) && wasApplied;
                if (!draftExisted)
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
                bool canRefreshDraftFromApplied = !draftExisted || draftWasApplied;
                if (canRefreshDraftFromApplied
                    && !AreStagedRowsEquivalentIgnoringIdAndNote(draft.StagedRows, applied.StagedRows))
                {
                    draft.StagedRows = applied.StagedRows.Select(CloneStagedRow).ToList();
                }
                if (canRefreshDraftFromApplied)
                {
                    EnsureAppliedMergedViewMatchesLineKind(draft, lineKey, applied);
                }
                draft.DraftApplied = canRefreshDraftFromApplied;
                if (canRefreshDraftFromApplied)
                {
                    draft.RulesApplied = true;
                }
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
            if (string.Equals(source, "planner", StringComparison.Ordinal))
                return 3;
            return 0;
        }

        private static string DecodeAppliedRowSource(byte code)
        {
            return code switch
            {
                1 => "manual",
                2 => "auto",
                3 => "planner",
                _ => string.Empty
            };
        }

        private DispatchWorkbenchStagedRowDto[] BuildAppliedWorkbenchRowsSnapshot()
        {
            if (m_AppliedWorkbenchLines.Count == 0)
            {
                return Array.Empty<DispatchWorkbenchStagedRowDto>();
            }

            return m_AppliedWorkbenchLines
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value?.StagedRows ?? new List<DispatchWorkbenchStagedRowDto>())
                .Where(row => row != null)
                .Select(CloneStagedRow)
                .ToArray();
        }

        private static string BuildAppliedRowNote(byte sourceCode)
        {
            return sourceCode switch
            {
                1 => "restored-manual",
                2 => "restored-auto",
                3 => "restored-planner",
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
                drafts = drafts.ToArray(),
                broadcastAssetDirectory = m_BroadcastAssetDirectory,
                broadcastAssets = BuildPersistedBroadcastAssetStates(),
                broadcastDraftLineBindings = BuildPersistedBroadcastDraftLineBindingStates(),
                broadcastDraftRules = BuildPersistedBroadcastDraftRuleStates(),
                broadcastDraftPlatformAnnouncements = BuildPersistedBroadcastDraftPlatformAnnouncementStates(),
                broadcastLineBindings = BuildPersistedBroadcastLineBindingStates(),
                broadcastRules = BuildPersistedBroadcastRuleStates(),
                broadcastPlatformAnnouncements = BuildPersistedBroadcastPlatformAnnouncementStates(),
                broadcastAppliedState = BuildPersistedBroadcastAppliedState(),
                broadcastDraftVolume = m_BroadcastDraftVolumePercent
            };
        }

        private void RestoreWorkbenchPersistence(DispatchWorkbenchPersistentState persisted)
        {
            m_WorkbenchDrafts.Clear();
            m_AppliedPlannerImportContracts.Clear();
            m_WorkbenchLineOriginHoldLimits.Clear();
            m_WorkbenchLineMaxStationDwellMinutes.Clear();
            m_WorkbenchLineAllowedDepots.Clear();
            m_WorkbenchLineServiceKinds.Clear();
            InvalidateConfiguredAllowedDepotCache();
            m_WorkbenchPreferredLineId = persisted?.preferredLineId ?? string.Empty;
            RestoreBroadcastWorkbenchPersistence(
                persisted?.broadcastAssetDirectory,
                persisted?.broadcastAssets,
                persisted?.broadcastDraftLineBindings,
                persisted?.broadcastDraftRules,
                persisted?.broadcastDraftPlatformAnnouncements,
                persisted?.broadcastLineBindings,
                persisted?.broadcastRules,
                persisted?.broadcastPlatformAnnouncements,
                persisted?.broadcastAppliedState,
                persisted?.broadcastDraftVolume ?? 80);

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

            MigrateRestoredWorkbenchDraftRowsByLineId();
        }

        private void MigrateRestoredWorkbenchDraftRowsByLineId()
        {
            if (m_WorkbenchDrafts.Count == 0)
                return;

            List<KeyValuePair<string, DispatchWorkbenchDraftState>> restoredDrafts =
                m_WorkbenchDrafts.ToList();
            HashSet<string> touchedDraftKeys = new HashSet<string>(StringComparer.Ordinal);
            int relocatedRows = 0;
            int createdDrafts = 0;

            for (int draftIndex = 0; draftIndex < restoredDrafts.Count; draftIndex++)
            {
                string sourceKey = restoredDrafts[draftIndex].Key;
                DispatchWorkbenchDraftState sourceDraft = restoredDrafts[draftIndex].Value;
                if (sourceDraft == null)
                    continue;

                relocatedRows += MigrateRestoredManualRowsByLineId(
                    sourceKey,
                    sourceDraft,
                    touchedDraftKeys,
                    ref createdDrafts);
                relocatedRows += MigrateRestoredAutoRulesByLineId(
                    sourceKey,
                    sourceDraft,
                    touchedDraftKeys,
                    ref createdDrafts);
                relocatedRows += MigrateRestoredStagedRowsByLineId(
                    sourceKey,
                    sourceDraft,
                    touchedDraftKeys,
                    ref createdDrafts);
            }

            if (relocatedRows == 0)
                return;

            foreach (string draftKey in touchedDraftKeys)
            {
                if (!m_WorkbenchDrafts.TryGetValue(draftKey, out DispatchWorkbenchDraftState draft)
                    || draft == null)
                {
                    continue;
                }

                draft.ManualRows = DeduplicateWorkbenchManualRowsForMigration(draft.ManualRows);
                draft.AutoRules = DeduplicateWorkbenchAutoRulesForMigration(draft.AutoRules);
                draft.StagedRows = DeduplicateWorkbenchStagedRowsForMigration(draft.StagedRows);
                NormalizeMigratedWorkbenchDraftState(draftKey, draft);
            }

            Mod.log.Info("[WorkbenchDraftMigration] relocatedRows="
                + relocatedRows
                + " touchedDrafts="
                + touchedDraftKeys.Count
                + " createdDrafts="
                + createdDrafts);
        }

        private int MigrateRestoredManualRowsByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts)
        {
            if (sourceDraft.ManualRows == null || sourceDraft.ManualRows.Count == 0)
                return 0;

            List<DispatchWorkbenchManualRowDto> retainedRows =
                new List<DispatchWorkbenchManualRowDto>(sourceDraft.ManualRows.Count);
            int movedRows = 0;
            for (int i = 0; i < sourceDraft.ManualRows.Count; i++)
            {
                DispatchWorkbenchManualRowDto row = sourceDraft.ManualRows[i];
                if (!ShouldMigrateRestoredWorkbenchRow(sourceKey, row?.lineId, out string targetKey))
                {
                    retainedRows.Add(row);
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.ManualRows.Add(CloneManualRow(row));
                if (sourceDraft.RulesApplied || sourceDraft.DraftApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRows++;
            }

            sourceDraft.ManualRows = retainedRows;
            return movedRows;
        }

        private int MigrateRestoredAutoRulesByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts)
        {
            if (sourceDraft.AutoRules == null || sourceDraft.AutoRules.Count == 0)
                return 0;

            List<DispatchWorkbenchAutoRuleDto> retainedRules =
                new List<DispatchWorkbenchAutoRuleDto>(sourceDraft.AutoRules.Count);
            int movedRules = 0;
            for (int i = 0; i < sourceDraft.AutoRules.Count; i++)
            {
                DispatchWorkbenchAutoRuleDto rule = sourceDraft.AutoRules[i];
                if (!ShouldMigrateRestoredWorkbenchRow(sourceKey, rule?.lineId, out string targetKey))
                {
                    retainedRules.Add(rule);
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.AutoRules.Add(CloneAutoRule(rule));
                if (sourceDraft.RulesApplied || sourceDraft.DraftApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRules++;
            }

            sourceDraft.AutoRules = retainedRules;
            return movedRules;
        }

        private int MigrateRestoredStagedRowsByLineId(
            string sourceKey,
            DispatchWorkbenchDraftState sourceDraft,
            HashSet<string> touchedDraftKeys,
            ref int createdDrafts)
        {
            if (sourceDraft.StagedRows == null || sourceDraft.StagedRows.Count == 0)
                return 0;

            List<DispatchWorkbenchStagedRowDto> retainedRows =
                new List<DispatchWorkbenchStagedRowDto>(sourceDraft.StagedRows.Count);
            int movedRows = 0;
            for (int i = 0; i < sourceDraft.StagedRows.Count; i++)
            {
                DispatchWorkbenchStagedRowDto row = sourceDraft.StagedRows[i];
                if (!ShouldMigrateRestoredWorkbenchRow(sourceKey, row?.lineId, out string targetKey))
                {
                    retainedRows.Add(row);
                    continue;
                }

                DispatchWorkbenchDraftState targetDraft =
                    GetOrCreateRestoredWorkbenchMigrationDraft(targetKey, ref createdDrafts);
                targetDraft.StagedRows.Add(CloneStagedRow(row));
                if (sourceDraft.DraftApplied)
                {
                    targetDraft.DraftApplied = true;
                    targetDraft.RulesApplied = true;
                }
                else if (sourceDraft.RulesApplied)
                {
                    targetDraft.RulesApplied = true;
                }
                CopyPlannerImportContractForMigratedDraft(sourceDraft, targetDraft, targetKey);
                touchedDraftKeys.Add(sourceKey);
                touchedDraftKeys.Add(targetKey);
                movedRows++;
            }

            sourceDraft.StagedRows = retainedRows;
            return movedRows;
        }

        private bool ShouldMigrateRestoredWorkbenchRow(
            string sourceKey,
            string rowLineId,
            out string targetKey)
        {
            targetKey = string.Empty;
            if (string.IsNullOrEmpty(rowLineId)
                || string.Equals(rowLineId, "local", StringComparison.Ordinal)
                || string.Equals(rowLineId, "express", StringComparison.Ordinal))
            {
                return false;
            }

            targetKey = GetDraftKey(rowLineId);
            return !string.Equals(targetKey, sourceKey ?? string.Empty, StringComparison.Ordinal);
        }

        private DispatchWorkbenchDraftState GetOrCreateRestoredWorkbenchMigrationDraft(
            string lineKey,
            ref int createdDrafts)
        {
            if (!m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft)
                || draft == null)
            {
                draft = CreateEmptyWorkbenchDraftState(lineKey);
                m_WorkbenchDrafts[lineKey] = draft;
                createdDrafts++;
            }

            return draft;
        }

        private void CopyPlannerImportContractForMigratedDraft(
            DispatchWorkbenchDraftState sourceDraft,
            DispatchWorkbenchDraftState targetDraft,
            string targetKey)
        {
            if (sourceDraft?.PlannerImportContract == null
                || targetDraft == null
                || targetDraft.PlannerImportContract != null)
            {
                return;
            }

            DispatchWorkbenchPlannerImportContractDto contract =
                ClonePlannerImportContract(sourceDraft.PlannerImportContract);
            if (contract == null)
                return;

            contract.draftKey = targetKey;
            targetDraft.PlannerImportContract = contract;
        }

        private void NormalizeMigratedWorkbenchDraftState(
            string draftKey,
            DispatchWorkbenchDraftState draft)
        {
            if (draft == null)
                return;

            if (string.IsNullOrEmpty(draft.SelectedLineId))
            {
                draft.SelectedLineId = draftKey;
            }
            if (string.IsNullOrEmpty(draft.SelectedEditLine)
                || string.Equals(draft.SelectedEditLine, "local", StringComparison.Ordinal)
                || string.Equals(draft.SelectedEditLine, "express", StringComparison.Ordinal))
            {
                draft.SelectedEditLine = draftKey == "__default__" ? string.Empty : draftKey;
            }

            if (draft.DraftApplied && !HasWorkbenchRowsForDraft(draft.StagedRows, draftKey))
            {
                draft.DraftApplied = false;
            }
            if (draft.RulesApplied
                && !HasWorkbenchRowsForDraft(draft.ManualRows, draftKey)
                && !HasWorkbenchRulesForDraft(draft.AutoRules, draftKey)
                && !HasWorkbenchRowsForDraft(draft.StagedRows, draftKey))
            {
                draft.RulesApplied = false;
            }

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

            ApplyCurrentTimeWindowDefaults(draft.MergedView);
            draft.AppliedDepartureMinutesCache.Clear();
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchManualRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(GetDraftKeyStatic(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRulesForDraft(
            List<DispatchWorkbenchAutoRuleDto> rules,
            string draftKey)
        {
            return rules != null
                && rules.Any(rule => rule != null
                    && string.Equals(GetDraftKeyStatic(rule.lineId), draftKey, StringComparison.Ordinal));
        }

        private static bool HasWorkbenchRowsForDraft(
            List<DispatchWorkbenchStagedRowDto> rows,
            string draftKey)
        {
            return rows != null
                && rows.Any(row => row != null
                    && string.Equals(GetDraftKeyStatic(row.lineId), draftKey, StringComparison.Ordinal));
        }

        private static string GetDraftKeyStatic(string lineId)
        {
            return string.IsNullOrEmpty(lineId) ? "__default__" : lineId;
        }

        private static DispatchWorkbenchPersistedDraftState CreatePersistedDraftState(string lineKey, DispatchWorkbenchDraftState draft)
        {
            return new DispatchWorkbenchPersistedDraftState
            {
                lineKey = lineKey,
                selectedLineId = draft?.SelectedLineId ?? string.Empty,
                selectedEditLine = draft?.SelectedEditLine ?? string.Empty,
                mergedView = CloneMergedViewForPersistence(draft?.MergedView),
                manualRows = draft?.ManualRows?.Select(CloneManualRow).ToArray() ?? Array.Empty<DispatchWorkbenchManualRowDto>(),
                autoRules = draft?.AutoRules?.Select(CloneAutoRule).ToArray() ?? Array.Empty<DispatchWorkbenchAutoRuleDto>(),
                lineDraftRows = draft?.StagedRows?.Select(CloneStagedRow).ToArray() ?? Array.Empty<DispatchWorkbenchStagedRowDto>(),
                rulesApplied = draft?.RulesApplied == true,
                draftApplied = draft?.DraftApplied == true,
                plannerImportContract = ClonePlannerImportContract(draft?.PlannerImportContract)
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
                StagedRows = DeduplicateWorkbenchStagedRowsByIdPreservingLast(
                    (dto.lineDraftRows ?? dto.stagedRows)?.Select(CloneStagedRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>()),
                RulesApplied = dto.rulesApplied,
                DraftApplied = dto.draftApplied,
                PlannerImportContract = ClonePlannerImportContract(dto.plannerImportContract)
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

        private static bool AreMergedViewsEquivalent(DispatchWorkbenchMergedView left, DispatchWorkbenchMergedView right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            List<string> leftLocalIds = NormalizeLineIdList(left.localLineIds, left.localLineId, null);
            List<string> rightLocalIds = NormalizeLineIdList(right.localLineIds, right.localLineId, null);
            List<string> leftExpressIds = NormalizeLineIdList(left.expressLineIds, left.expressLineId, null);
            List<string> rightExpressIds = NormalizeLineIdList(right.expressLineIds, right.expressLineId, null);

            if (left.isLoop != right.isLoop
                || !string.Equals(left.turnbackStationId ?? string.Empty, right.turnbackStationId ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.direction ?? string.Empty, right.direction ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.windowStart ?? string.Empty, right.windowStart ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(left.windowEnd ?? string.Empty, right.windowEnd ?? string.Empty, StringComparison.Ordinal)
                || leftLocalIds.Count != rightLocalIds.Count
                || leftExpressIds.Count != rightExpressIds.Count)
            {
                return false;
            }

            for (int i = 0; i < leftLocalIds.Count; i++)
            {
                if (!string.Equals(leftLocalIds[i], rightLocalIds[i], StringComparison.Ordinal))
                    return false;
            }

            for (int i = 0; i < leftExpressIds.Count; i++)
            {
                if (!string.Equals(leftExpressIds[i], rightExpressIds[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
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
            if (!TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot))
                return false;

            return snapshot.TimetableApplied;
        }

        private int[] GetAppliedWorkbenchDepartureMinutes(Entity line)
        {
            if (!TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                || !snapshot.TimetableApplied)
                return Array.Empty<int>();

            if (!m_AppliedWorkbenchLines.TryGetValue(snapshot.LineKey, out AppliedWorkbenchLineState state)
                || state.StagedRows == null
                || state.StagedRows.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (state.DepartureMinutesCache == null || state.DepartureMinutesCache.Length == 0)
            {
                state.DepartureMinutesCache = BuildAppliedDepartureMinutes(state.StagedRows, snapshot.LineId);
            }

            return state.DepartureMinutesCache;
        }

        private void LogAppliedWorkbenchLineState(Entity line, int nowMin, int nextSlot)
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return;

            string lineKey = GetDraftKey(GetWorkbenchLineId(line));
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
                GetWorkbenchLineId(line)
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
                + GetWorkbenchLineId(line)
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
            if (!TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                || !snapshot.TimetableApplied)
                return string.Empty;

            if (!m_AppliedWorkbenchLines.TryGetValue(snapshot.LineKey, out AppliedWorkbenchLineState state)
                || state.StagedRows == null
                || state.StagedRows.Count == 0)
            {
                return string.Empty;
            }

            return snapshot.EffectiveServiceKind;
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

        private string GetEffectiveWorkbenchLineServiceKind(Entity line, AppliedWorkbenchLineState applied)
        {
            string configuredKind = GetWorkbenchConfiguredLineServiceKind(line);
            if (!string.IsNullOrEmpty(configuredKind))
            {
                return configuredKind;
            }

            return GetAppliedWorkbenchLineServiceKind(applied);
        }

        private bool IsAppliedWorkbenchLocalLine(Entity line)
        {
            return TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                && snapshot.TimetableApplied
                && string.Equals(snapshot.EffectiveServiceKind, "local", StringComparison.Ordinal);
        }

        private bool IsAppliedWorkbenchExpressLine(Entity line)
        {
            return TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                && snapshot.TimetableApplied
                && string.Equals(snapshot.EffectiveServiceKind, "express", StringComparison.Ordinal);
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
            WorkbenchLineRuntime localLine = activeRuntime ?? lines.FirstOrDefault();

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
            WorkbenchLineRuntime localLine = activeRuntime ?? lines.FirstOrDefault();

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
                    id = CreateWorkbenchStationId(stations.Count),
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

                originStationId = CreateWorkbenchOriginGroupId(stopEntity);
                originStationName = ResolveWorkbenchStationName(stopEntity);
                return;
            }
        }

        private string CreateWorkbenchOriginGroupId(Entity stopEntity)
        {
            if (stopEntity == Entity.Null)
            {
                return string.Empty;
            }

            Entity buildingEntity = FindTransportStationFromStop(stopEntity);
            if (buildingEntity != Entity.Null)
            {
                return "station-building-" + buildingEntity.Index.ToString();
            }

            return CreateWorkbenchOriginStationId(stopEntity);
        }

        private static string CreateWorkbenchOriginStationId(Entity stopEntity)
        {
            if (stopEntity == Entity.Null)
            {
                return string.Empty;
            }

            return "station-" + stopEntity.Index.ToString();
        }

        private static string CreateWorkbenchStationId(int order)
        {
            return "station-" + order.ToString();
        }

        private string ResolveWorkbenchEntityName(Entity entity)
        {
            string translatedName = TryGetTranslatedWorkbenchEntityName(entity);
            if (!string.IsNullOrEmpty(translatedName))
            {
                return translatedName;
            }

            string renderedLabel = TryGetRenderedWorkbenchEntityName(entity);
            if (!string.IsNullOrEmpty(renderedLabel))
            {
                return renderedLabel;
            }

            return string.Empty;
        }

        private string TryGetTranslatedWorkbenchEntityName(Entity entity)
        {
            try
            {
                return TryTranslateName(m_NameSystem.GetName(entity));
            }
            catch
            {
                return string.Empty;
            }
        }

        private string TryGetRenderedWorkbenchEntityName(Entity entity)
        {
            try
            {
                return m_NameSystem.GetRenderedLabelName(entity);
            }
            catch
            {
                return string.Empty;
            }
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

            return TryGetTranslatedWorkbenchEntityName(stopEntity);
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
            RecordRuntimeObservationStopEvent(
                vehicle,
                line,
                stopEntity,
                stopWaypointIndex,
                isOriginStop,
                boarding,
                nowTime,
                nowFrame);

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

                string lineId = GetWorkbenchLineId(record.Line);
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
            DispatchWorkbenchDraftState draft,
            List<DispatchWorkbenchStagedRowDto> activeStagedRows,
            List<DispatchWorkbenchStagedRowDto> mergedStagedRows)
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
            string activeRowsPreview = SummarizeStagedRowsByLine(activeStagedRows);
            string mergedRowsPreview = SummarizeStagedRowsByLine(mergedStagedRows);
            string logKey = lineId
                + "|"
                + stations.Count
                + "|"
                + (trips?.Count ?? 0)
                + "|"
                + localPreview
                + "|"
                + expressPreview
                + "|"
                + activeRowsPreview
                + "|"
                + mergedRowsPreview;
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
                + "] activeRows=["
                + activeRowsPreview
                + "] combinedRows=["
                + mergedRowsPreview
                + "]");
        }

        private string SummarizeWorkbenchDraftRows()
        {
            if (m_WorkbenchDrafts.Count == 0)
                return "draftRows=[]";

            return "draftRows=["
                + string.Join("; ", m_WorkbenchDrafts
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        DispatchWorkbenchDraftState draft = pair.Value;
                        return pair.Key
                            + " sel="
                            + (draft?.SelectedLineId ?? string.Empty)
                            + " edit="
                            + (draft?.SelectedEditLine ?? string.Empty)
                            + " applied="
                            + (draft?.DraftApplied == true ? "1" : "0")
                            + " staged="
                            + SummarizeStagedRowsByLine(draft?.StagedRows);
                    }))
                + "]";
        }

        private string SummarizeAppliedWorkbenchRows()
        {
            if (m_AppliedWorkbenchLines.Count == 0)
                return "appliedRows=[]";

            return "appliedRows=["
                + string.Join("; ", m_AppliedWorkbenchLines
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        AppliedWorkbenchLineState state = pair.Value;
                        string entityText = state?.LineEntity == Entity.Null
                            ? "none"
                            : state?.LineEntity.Index.ToString();
                        return pair.Key
                            + " entity="
                            + entityText
                            + " staged="
                            + SummarizeStagedRowsByLine(state?.StagedRows);
                    }))
                + "]";
        }

        private static string SummarizeStagedRowsByLine(IEnumerable<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null)
                return "-";

            string[] parts = rows
                .Where(row => row != null)
                .GroupBy(row => string.IsNullOrEmpty(row.lineId) ? "(missing)" : row.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    string preview = string.Join(",", group
                        .Select(row => row.time ?? string.Empty)
                        .Where(time => !string.IsNullOrEmpty(time))
                        .OrderBy(time => time, StringComparer.Ordinal)
                        .Take(6));
                    return group.Key + ":" + group.Count() + "(" + preview + ")";
                })
                .ToArray();

            return parts.Length == 0 ? "-" : string.Join("|", parts);
        }

        private void WriteWorkbenchIntegrityReport(
            string reason,
            WorkbenchLineRuntime activeRuntime,
            string draftKey,
            DispatchWorkbenchDraftState activeDraft,
            List<WorkbenchLineRuntime> runtimeLines,
            List<DispatchWorkbenchStagedRowDto> activeStagedRows,
            List<DispatchWorkbenchStagedRowDto> combinedStagedRows)
        {
            if (!ENABLE_WORKBENCH_INTEGRITY_REPORT)
                return;

            try
            {
                Dictionary<string, WorkbenchLineRuntime> runtimeById = runtimeLines
                    .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                    .GroupBy(line => line.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                Dictionary<string, List<string>> provenanceByKey = BuildWorkbenchRowProvenanceMap();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("RapidTransitMod Workbench Integrity Report");
                sb.AppendLine("reason=" + (reason ?? string.Empty));
                sb.AppendLine("frame=" + (m_SimulationSystem != null ? m_SimulationSystem.frameIndex.ToString() : "0"));
                sb.AppendLine("activeLine=" + (activeRuntime?.Id ?? "none") + " name=\"" + (activeRuntime?.Name ?? "none") + "\" draftKey=" + (draftKey ?? string.Empty));
                sb.AppendLine("activeDraft selected=" + (activeDraft?.SelectedLineId ?? string.Empty)
                    + " edit=" + (activeDraft?.SelectedEditLine ?? string.Empty)
                    + " rulesApplied=" + (activeDraft?.RulesApplied == true ? "1" : "0")
                    + " draftApplied=" + (activeDraft?.DraftApplied == true ? "1" : "0")
                    + " local=[" + string.Join(",", NormalizeLineIdList(activeDraft?.MergedView?.localLineIds, activeDraft?.MergedView?.localLineId, null)) + "]"
                    + " express=[" + string.Join(",", NormalizeLineIdList(activeDraft?.MergedView?.expressLineIds, activeDraft?.MergedView?.expressLineId, null)) + "]");
                sb.AppendLine();
                AppendWorkbenchLineCatalogReport(sb, runtimeLines);
                AppendWorkbenchDraftReport(sb, runtimeById);
                AppendWorkbenchAppliedReport(sb, runtimeById);
                AppendWorkbenchRowSetReport(sb, "activeRows", activeStagedRows, runtimeById, provenanceByKey);
                AppendWorkbenchRowSetReport(sb, "combinedRows", combinedStagedRows, runtimeById, provenanceByKey);
                AppendWorkbenchConflictReport(sb, "combinedRows", combinedStagedRows, runtimeById, provenanceByKey);

                string filePath = GetWorkbenchReportPath("RapidTransitMod-workbench-integrity-latest.txt");
                File.WriteAllText(filePath, sb.ToString());
                Mod.log.Info("[WorkbenchIntegrityReport] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[WorkbenchIntegrityReport] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void WriteWorkbenchSaveRequestReport(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            List<string> errors)
        {
            if (!ENABLE_WORKBENCH_INTEGRITY_REPORT || request == null)
                return;

            try
            {
                Dictionary<string, WorkbenchLineRuntime> runtimeById = runtimeLines
                    .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                    .GroupBy(line => line.Id, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                Dictionary<string, List<string>> provenanceByKey = BuildWorkbenchRowProvenanceMap();
                List<DispatchWorkbenchStagedRowDto> rows = BuildRequestLineDraftRowsByDraftKey(
                        request,
                        GetDraftKey(request.selectedLineId))
                    .Values
                    .SelectMany(group => group)
                    .Select(CloneStagedRow)
                    .ToList();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("RapidTransitMod Workbench Save Request Report");
                sb.AppendLine("frame=" + (m_SimulationSystem != null ? m_SimulationSystem.frameIndex.ToString() : "0"));
                sb.AppendLine("selectedLineId=" + (request.selectedLineId ?? string.Empty)
                    + " selectedEditLine=" + (request.selectedEditLine ?? string.Empty)
                    + " applyDraft=" + (request.applyDraft ? "1" : "0")
                    + " markRulesApplied=" + (request.markRulesApplied ? "1" : "0")
                    + " nativeScheduleWriter=" + (request.nativeScheduleWriter ? "1" : "0"));
                sb.AppendLine("mergedView local=[" + string.Join(",", NormalizeLineIdList(request.mergedView?.localLineIds, request.mergedView?.localLineId, runtimeLines)) + "]"
                    + " express=[" + string.Join(",", NormalizeLineIdList(request.mergedView?.expressLineIds, request.mergedView?.expressLineId, runtimeLines)) + "]");
                sb.AppendLine("validationErrors=" + (errors == null || errors.Count == 0 ? "-" : string.Join(" | ", errors)));
                sb.AppendLine("manualRows=" + SummarizeManualRowsByLine(request.manualRows));
                sb.AppendLine("autoRules=" + SummarizeAutoRulesByLine(request.autoRules));
                sb.AppendLine("lineDraftRows=" + SummarizeStagedRowsByLine(rows));
                sb.AppendLine();
                AppendWorkbenchRowSetReport(sb, "request.lineDraftRows", rows, runtimeById, provenanceByKey);
                AppendWorkbenchConflictReport(sb, "request.lineDraftRows", rows, runtimeById, provenanceByKey);

                string filePath = GetWorkbenchReportPath("RapidTransitMod-workbench-save-request-latest.txt");
                File.WriteAllText(filePath, sb.ToString());
                Mod.log.Info("[WorkbenchSaveRequestReport] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[WorkbenchSaveRequestReport] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string GetWorkbenchReportPath(string fileName)
        {
            string logsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "Colossal Order",
                "Cities Skylines II",
                "Logs");
            Directory.CreateDirectory(logsDirectory);
            return Path.Combine(logsDirectory, fileName);
        }

        private void AppendWorkbenchLineCatalogReport(StringBuilder sb, List<WorkbenchLineRuntime> runtimeLines)
        {
            sb.AppendLine("== runtime lines ==");
            if (runtimeLines == null || runtimeLines.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (WorkbenchLineRuntime line in runtimeLines.OrderBy(line => line.Id, StringComparer.Ordinal))
            {
                sb.AppendLine((line.Id ?? string.Empty)
                    + " entity=" + line.Entity.Index
                    + " routeNumber=" + (line.RouteNumber == int.MaxValue ? "-" : line.RouteNumber.ToString())
                    + " kind=" + (line.Kind ?? string.Empty)
                    + " name=\"" + (line.Name ?? string.Empty) + "\""
                    + " origin=" + (line.OriginStationId ?? string.Empty)
                    + " \"" + (line.OriginStationName ?? string.Empty) + "\"");
            }
            sb.AppendLine();
        }

        private void AppendWorkbenchDraftReport(StringBuilder sb, Dictionary<string, WorkbenchLineRuntime> runtimeById)
        {
            sb.AppendLine("== drafts ==");
            if (m_WorkbenchDrafts.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                List<string> localIds = NormalizeLineIdList(draft?.MergedView?.localLineIds, draft?.MergedView?.localLineId, null);
                List<string> expressIds = NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId, null);
                HashSet<string> scope = new HashSet<string>(localIds.Concat(expressIds), StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(draft?.SelectedLineId)) scope.Add(draft.SelectedLineId);
                if (!string.IsNullOrEmpty(draft?.SelectedEditLine)) scope.Add(draft.SelectedEditLine);
                if (!string.IsNullOrEmpty(entry.Key)) scope.Add(entry.Key);
                string outOfScope = draft?.StagedRows == null
                    ? "-"
                    : string.Join(",", draft.StagedRows
                        .Where(row => row != null && !string.IsNullOrEmpty(row.lineId) && !scope.Contains(row.lineId))
                        .Select(row => row.lineId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal));
                if (string.IsNullOrEmpty(outOfScope)) outOfScope = "-";
                sb.AppendLine("draft=" + entry.Key
                    + " selected=" + (draft?.SelectedLineId ?? string.Empty)
                    + " edit=" + (draft?.SelectedEditLine ?? string.Empty)
                    + " rulesApplied=" + (draft?.RulesApplied == true ? "1" : "0")
                    + " draftApplied=" + (draft?.DraftApplied == true ? "1" : "0")
                    + " local=[" + string.Join(",", localIds) + "]"
                    + " express=[" + string.Join(",", expressIds) + "]"
                    + " staged=" + SummarizeStagedRowsByLine(draft?.StagedRows)
                    + " outOfScope=[" + outOfScope + "]");
            }
            sb.AppendLine();
        }

        private void AppendWorkbenchAppliedReport(StringBuilder sb, Dictionary<string, WorkbenchLineRuntime> runtimeById)
        {
            sb.AppendLine("== applied ==");
            if (m_AppliedWorkbenchLines.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedWorkbenchLineState state = entry.Value;
                string runtimeOrigin = runtimeById.TryGetValue(entry.Key, out WorkbenchLineRuntime runtime)
                    ? (runtime.OriginStationId + " \"" + runtime.OriginStationName + "\"")
                    : "-";
                sb.AppendLine("applied=" + entry.Key
                    + " entity=" + (state?.LineEntity == Entity.Null ? "none" : state?.LineEntity.Index.ToString())
                    + " origin=" + runtimeOrigin
                    + " staged=" + SummarizeStagedRowsByLine(state?.StagedRows));
            }
            sb.AppendLine();
        }

        private void AppendWorkbenchRowSetReport(
            StringBuilder sb,
            string title,
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            Dictionary<string, List<string>> provenanceByKey)
        {
            sb.AppendLine("== " + title + " ==");
            List<DispatchWorkbenchStagedRowDto> rowList = rows?.Where(row => row != null).ToList()
                ?? new List<DispatchWorkbenchStagedRowDto>();
            sb.AppendLine("summary=" + SummarizeStagedRowsByLine(rowList));
            foreach (DispatchWorkbenchStagedRowDto row in rowList
                .OrderBy(row => ParseTimeMinutes(row.time))
                .ThenBy(row => row.lineId, StringComparer.Ordinal)
                .Take(240))
            {
                sb.AppendLine(DescribeWorkbenchRow(row, runtimeById, provenanceByKey));
            }
            if (rowList.Count > 240)
            {
                sb.AppendLine("... truncated rows=" + (rowList.Count - 240));
            }
            sb.AppendLine();
        }

        private void AppendWorkbenchConflictReport(
            StringBuilder sb,
            string title,
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            Dictionary<string, List<string>> provenanceByKey)
        {
            const int minGapMinutes = 5;
            sb.AppendLine("== " + title + " conflicts ==");
            List<(DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName)> entries =
                (rows ?? Enumerable.Empty<DispatchWorkbenchStagedRowDto>())
                    .Where(row => row != null)
                    .Select(row =>
                    {
                        int minute = ParseTimeMinutes(row.time);
                        string originId = string.Empty;
                        string originName = string.Empty;
                        if (runtimeById.TryGetValue(row.lineId ?? string.Empty, out WorkbenchLineRuntime line))
                        {
                            originId = line.OriginStationId ?? string.Empty;
                            originName = line.OriginStationName ?? string.Empty;
                        }
                        return (Row: row, Minute: minute, OriginId: originId, OriginName: originName);
                    })
                    .Where(entry => entry.Minute >= 0 && !string.IsNullOrEmpty(entry.OriginId))
                    .OrderBy(entry => entry.Minute)
                    .ThenBy(entry => entry.Row.lineId, StringComparer.Ordinal)
                    .ToList();

            int frontendConflictCount = 0;
            for (int i = 1; i < entries.Count; i++)
            {
                var previous = entries[i - 1];
                var current = entries[i];
                if (!string.Equals(previous.OriginId, current.OriginId, StringComparison.Ordinal))
                    continue;
                int gap = current.Minute - previous.Minute;
                if (gap >= minGapMinutes)
                    continue;
                frontendConflictCount++;
                if (frontendConflictCount <= 80)
                {
                    sb.AppendLine("frontendPair gap=" + gap
                        + " origin=" + current.OriginId + " \"" + current.OriginName + "\""
                        + " left={" + DescribeWorkbenchRow(previous.Row, runtimeById, provenanceByKey) + "}"
                        + " right={" + DescribeWorkbenchRow(current.Row, runtimeById, provenanceByKey) + "}");
                }
            }
            sb.AppendLine("frontendPairCount=" + frontendConflictCount);

            int originConflictCount = 0;
            foreach (IGrouping<string, (DispatchWorkbenchStagedRowDto Row, int Minute, string OriginId, string OriginName)> group in entries.GroupBy(entry => entry.OriginId, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(entry => entry.Minute).ThenBy(entry => entry.Row.lineId, StringComparer.Ordinal).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    int gap = ordered[i].Minute - ordered[i - 1].Minute;
                    if (gap >= minGapMinutes)
                        continue;
                    originConflictCount++;
                }
            }
            sb.AppendLine("originScopedPairCount=" + originConflictCount);
            sb.AppendLine();
        }

        private string DescribeWorkbenchRow(
            DispatchWorkbenchStagedRowDto row,
            Dictionary<string, WorkbenchLineRuntime> runtimeById,
            Dictionary<string, List<string>> provenanceByKey)
        {
            if (row == null)
                return "(null)";

            string lineName = runtimeById.TryGetValue(row.lineId ?? string.Empty, out WorkbenchLineRuntime line)
                ? line.Name ?? string.Empty
                : "(unknown-line)";
            string origin = runtimeById.TryGetValue(row.lineId ?? string.Empty, out line)
                ? ((line.OriginStationId ?? string.Empty) + " \"" + (line.OriginStationName ?? string.Empty) + "\"")
                : "-";
            string key = BuildWorkbenchStagedRowSemanticKey(row);
            string provenance = provenanceByKey.TryGetValue(key, out List<string> sources)
                ? string.Join(",", sources.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                : "-";
            return "id=" + (row.id ?? string.Empty)
                + " line=" + (row.lineId ?? string.Empty)
                + " name=\"" + lineName + "\""
                + " kind=" + (row.kind ?? string.Empty)
                + " time=" + (row.time ?? string.Empty)
                + " source=" + (row.source ?? string.Empty)
                + " note=\"" + (row.note ?? string.Empty) + "\""
                + " origin=" + origin
                + " provenance=[" + provenance + "]";
        }

        private Dictionary<string, List<string>> BuildWorkbenchRowProvenanceMap()
        {
            Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts)
            {
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft?.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in draft.StagedRows)
                {
                    AddWorkbenchRowProvenance(result, row, "draft:" + entry.Key + ":applied=" + (draft.DraftApplied ? "1" : "0"));
                }
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state?.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in state.StagedRows)
                {
                    AddWorkbenchRowProvenance(result, row, "applied:" + entry.Key);
                }
            }

            return result;
        }

        private static void AddWorkbenchRowProvenance(
            Dictionary<string, List<string>> result,
            DispatchWorkbenchStagedRowDto row,
            string label)
        {
            string key = BuildWorkbenchStagedRowSemanticKey(row);
            if (string.IsNullOrEmpty(key))
                return;

            if (!result.TryGetValue(key, out List<string> sources))
            {
                sources = new List<string>();
                result[key] = sources;
            }

            sources.Add(label);
        }

        private static string SummarizeManualRowsByLine(IEnumerable<DispatchWorkbenchManualRowDto> rows)
        {
            if (rows == null)
                return "-";

            string[] parts = rows
                .Where(row => row != null)
                .GroupBy(row => string.IsNullOrEmpty(row.lineId) ? "(missing)" : row.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + ":" + group.Count())
                .ToArray();
            return parts.Length == 0 ? "-" : string.Join("|", parts);
        }

        private static string SummarizeAutoRulesByLine(IEnumerable<DispatchWorkbenchAutoRuleDto> rules)
        {
            if (rules == null)
                return "-";

            string[] parts = rules
                .Where(rule => rule != null)
                .GroupBy(rule => string.IsNullOrEmpty(rule.lineId) ? "(missing)" : rule.lineId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + ":" + group.Count())
                .ToArray();
            return parts.Length == 0 ? "-" : string.Join("|", parts);
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

        private static List<DispatchWorkbenchManualRowDto> DeduplicateWorkbenchManualRowsForMigration(
            List<DispatchWorkbenchManualRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchManualRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchManualRowDto> result =
                new List<DispatchWorkbenchManualRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchManualRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = BuildWorkbenchRowIdKey(row.lineId, row.id);
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                string semanticKey = BuildWorkbenchManualRowSemanticKey(row);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        private static List<DispatchWorkbenchAutoRuleDto> DeduplicateWorkbenchAutoRulesForMigration(
            List<DispatchWorkbenchAutoRuleDto> rules)
        {
            if (rules == null || rules.Count <= 1)
                return rules ?? new List<DispatchWorkbenchAutoRuleDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchAutoRuleDto> result =
                new List<DispatchWorkbenchAutoRuleDto>(rules.Count);
            for (int index = rules.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchAutoRuleDto rule = rules[index];
                if (rule == null)
                    continue;

                string ruleId = BuildWorkbenchRowIdKey(rule.lineId, rule.id);
                if (!string.IsNullOrEmpty(ruleId) && !seenIds.Add(ruleId))
                    continue;

                string semanticKey = BuildWorkbenchAutoRuleSemanticKey(rule);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(rule);
            }

            result.Reverse();
            return result;
        }

        private static List<DispatchWorkbenchStagedRowDto> DeduplicateWorkbenchStagedRowsForMigration(
            List<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchStagedRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchStagedRowDto> result =
                new List<DispatchWorkbenchStagedRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchStagedRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = BuildWorkbenchRowIdKey(row.lineId, row.id);
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                string semanticKey = BuildWorkbenchStagedRowSemanticKey(row);
                if (!string.IsNullOrEmpty(semanticKey) && !seenSemanticKeys.Add(semanticKey))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        private static List<DispatchWorkbenchStagedRowDto> DeduplicateWorkbenchStagedRowsByIdPreservingLast(
            List<DispatchWorkbenchStagedRowDto> rows)
        {
            if (rows == null || rows.Count <= 1)
                return rows ?? new List<DispatchWorkbenchStagedRowDto>();

            HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
            List<DispatchWorkbenchStagedRowDto> result = new List<DispatchWorkbenchStagedRowDto>(rows.Count);
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                DispatchWorkbenchStagedRowDto row = rows[index];
                if (row == null)
                    continue;

                string rowId = row.id ?? string.Empty;
                if (!string.IsNullOrEmpty(rowId) && !seenIds.Add(rowId))
                    continue;

                result.Add(row);
            }

            result.Reverse();
            return result;
        }

        private static DispatchWorkbenchPlannerImportContractDto ClonePlannerImportContract(DispatchWorkbenchPlannerImportContractDto contract)
        {
            if (contract == null)
                return null;

            string json = DispatchWorkbenchJson.Serialize(contract);
            return string.IsNullOrEmpty(json)
                ? null
                : DispatchWorkbenchJson.Deserialize<DispatchWorkbenchPlannerImportContractDto>(json);
        }

        private static string BuildWorkbenchStagedRowSemanticKey(DispatchWorkbenchStagedRowDto row)
        {
            if (row == null)
                return string.Empty;

            return (row.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                + "|"
                + (row.time ?? string.Empty);
        }

        private static string BuildWorkbenchManualRowSemanticKey(DispatchWorkbenchManualRowDto row)
        {
            if (row == null)
                return string.Empty;

            return (row.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                + "|"
                + (row.time ?? string.Empty)
                + "|"
                + (row.offsetMode ?? string.Empty)
                + "|"
                + (row.offsetMinutes ?? string.Empty);
        }

        private static string BuildWorkbenchAutoRuleSemanticKey(DispatchWorkbenchAutoRuleDto rule)
        {
            if (rule == null)
                return string.Empty;

            return (rule.lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(rule.kind) ? "local" : rule.kind)
                + "|"
                + (rule.start ?? string.Empty)
                + "|"
                + (rule.end ?? string.Empty)
                + "|"
                + rule.departuresPerHour.ToString("R")
                + "|"
                + rule.localPerHour.ToString("R")
                + "|"
                + rule.expressPerHour.ToString("R")
                + "|"
                + (rule.expressOffsetMode ?? string.Empty)
                + "|"
                + rule.expressOffsetMinutes.ToString();
        }

        private static string BuildWorkbenchRowIdKey(string lineId, string rowId)
        {
            return string.IsNullOrEmpty(rowId)
                ? string.Empty
                : ((lineId ?? string.Empty) + "|" + rowId);
        }

        private static string BuildWorkbenchStagedRowMatchingKey(DispatchWorkbenchStagedRowDto row)
        {
            if (row == null)
                return string.Empty;

            return BuildWorkbenchStagedRowSemanticKey(row);
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
                m_WorkbenchLineAllowedDepots[setting.lineId] = NormalizeWorkbenchAllowedDepotId(setting.allowedDepotId);
                string normalizedKind = NormalizeWorkbenchServiceKind(setting.serviceKind);
                if (!string.IsNullOrEmpty(normalizedKind))
                {
                    m_WorkbenchLineServiceKinds[setting.lineId] = normalizedKind;
                }
            }

            InvalidateConfiguredAllowedDepotCache();
            InvalidateWorkbenchLineFrameSnapshots();
        }

        private bool AreWorkbenchLineSettingsEquivalent(IEnumerable<DispatchWorkbenchLineSettingDto> settings)
        {
            Dictionary<string, DispatchWorkbenchLineSettingDto> requested = new Dictionary<string, DispatchWorkbenchLineSettingDto>(StringComparer.Ordinal);
            if (settings != null)
            {
                foreach (DispatchWorkbenchLineSettingDto setting in settings)
                {
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                        continue;

                    requested[setting.lineId] = setting;
                }
            }

            HashSet<string> currentLineIds = new HashSet<string>(StringComparer.Ordinal);
            currentLineIds.UnionWith(m_WorkbenchLineOriginHoldLimits.Keys);
            currentLineIds.UnionWith(m_WorkbenchLineMaxStationDwellMinutes.Keys);
            currentLineIds.UnionWith(m_WorkbenchLineAllowedDepots.Keys);
            currentLineIds.UnionWith(m_WorkbenchLineServiceKinds.Keys);

            if (requested.Count != currentLineIds.Count)
                return false;

            foreach (string lineId in currentLineIds)
            {
                if (!requested.TryGetValue(lineId, out DispatchWorkbenchLineSettingDto setting))
                    return false;

                if (NormalizeOriginHoldLimitMinutes(setting.originHoldLimitMinutes) != GetWorkbenchOriginHoldLimitMinutes(lineId)
                    || NormalizeMaxStationDwellMinutes(setting.maxStationDwellMinutes) != GetWorkbenchMaxStationDwellMinutes(lineId)
                    || !string.Equals(setting.allowedDepotId ?? string.Empty, GetWorkbenchAllowedDepotId(lineId), StringComparison.Ordinal)
                    || !string.Equals(NormalizeWorkbenchServiceKind(setting.serviceKind), GetWorkbenchConfiguredLineServiceKind(lineId), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
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
                ? NormalizeWorkbenchAllowedDepotId(configuredDepotId)
                : string.Empty;
        }

        private int GetWorkbenchOriginHoldLimitMinutes(Entity line)
        {
            if (line == Entity.Null)
                return DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;

            string stableLineId = TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                ? snapshot.LineId
                : GetWorkbenchLineId(line);
            if (!string.IsNullOrEmpty(stableLineId)
                && m_WorkbenchLineOriginHoldLimits.TryGetValue(stableLineId, out int stableMinutes))
            {
                return NormalizeOriginHoldLimitMinutes(stableMinutes);
            }

            string legacyLineId = line.Index.ToString();
            return GetWorkbenchOriginHoldLimitMinutes(legacyLineId);
        }

        private string GetWorkbenchConfiguredLineServiceKind(Entity line)
        {
            if (line == Entity.Null)
                return string.Empty;

            if (TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                && !string.IsNullOrEmpty(snapshot.ConfiguredServiceKind))
            {
                return snapshot.ConfiguredServiceKind;
            }

            string stableLineId = GetWorkbenchLineId(line);
            if (!string.IsNullOrEmpty(stableLineId)
                && m_WorkbenchLineServiceKinds.TryGetValue(stableLineId, out string stableKind))
            {
                return NormalizeWorkbenchServiceKind(stableKind);
            }

            return GetWorkbenchConfiguredLineServiceKind(line.Index.ToString());
        }

        private int GetWorkbenchMaxStationDwellMinutes(Entity line)
        {
            if (line == Entity.Null)
                return DEFAULT_MAX_STATION_DWELL_MINUTES;

            string stableLineId = TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                ? snapshot.LineId
                : GetWorkbenchLineId(line);
            if (!string.IsNullOrEmpty(stableLineId)
                && m_WorkbenchLineMaxStationDwellMinutes.TryGetValue(stableLineId, out int stableMinutes))
            {
                return NormalizeMaxStationDwellMinutes(stableMinutes);
            }

            return GetWorkbenchMaxStationDwellMinutes(line.Index.ToString());
        }

        private string GetWorkbenchAllowedDepotId(Entity line)
        {
            if (line == Entity.Null)
                return string.Empty;

            string stableLineId = GetWorkbenchLineId(line);
            if (!string.IsNullOrEmpty(stableLineId)
                && m_WorkbenchLineAllowedDepots.TryGetValue(stableLineId, out string stableDepotId))
            {
                return stableDepotId ?? string.Empty;
            }

            return GetWorkbenchAllowedDepotId(line.Index.ToString());
        }

        public Entity GetConfiguredAllowedDepot(Entity line)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            MaybeLogConfiguredAllowedDepotCacheStats(nowFrame);
            m_ConfiguredAllowedDepotCacheCalls++;

            if (line == Entity.Null || !EntityManager.Exists(line))
            {
                m_ConfiguredAllowedDepotCacheByLine.Remove(line);
                return Entity.Null;
            }

            string lineId = GetWorkbenchLineId(line);
            string depotId = !string.IsNullOrEmpty(lineId)
                && m_WorkbenchLineAllowedDepots.TryGetValue(lineId, out string stableDepotId)
                ? stableDepotId ?? string.Empty
                : GetWorkbenchAllowedDepotId(line.Index.ToString());

            if (m_ConfiguredAllowedDepotCacheByLine.TryGetValue(line, out ConfiguredAllowedDepotCacheEntry cached)
                && cached.Line == line)
            {
                if (cached.SettingsVersion != m_WorkbenchLineSettingsVersion)
                {
                    m_ConfiguredAllowedDepotCacheSettingsVersionMisses++;
                    m_ConfiguredAllowedDepotCacheByLine.Remove(line);
                }
                else if (!string.Equals(cached.LineId, lineId, StringComparison.Ordinal))
                {
                    m_ConfiguredAllowedDepotCacheLineIdMisses++;
                    m_ConfiguredAllowedDepotCacheByLine.Remove(line);
                }
                else if (!string.Equals(cached.AllowedDepotId, depotId, StringComparison.Ordinal))
                {
                    m_ConfiguredAllowedDepotCacheAllowedDepotIdMisses++;
                    m_ConfiguredAllowedDepotCacheByLine.Remove(line);
                }
                else
                {
                    if (cached.CanonicalDepot == Entity.Null)
                    {
                        m_ConfiguredAllowedDepotCacheHits++;
                        return Entity.Null;
                    }

                    if (EntityManager.Exists(cached.CanonicalDepot)
                        && EntityManager.HasComponent<Game.Buildings.TransportDepot>(cached.CanonicalDepot)
                        && !EntityManager.HasComponent<Deleted>(cached.CanonicalDepot))
                    {
                        m_ConfiguredAllowedDepotCacheHits++;
                        return cached.CanonicalDepot;
                    }

                    m_ConfiguredAllowedDepotCacheEntityInvalidations++;
                    m_ConfiguredAllowedDepotCacheByLine.Remove(line);
                }
            }

            if (string.IsNullOrEmpty(depotId))
            {
                m_ConfiguredAllowedDepotCacheByLine[line] = new ConfiguredAllowedDepotCacheEntry(
                    line,
                    lineId,
                    string.Empty,
                    Entity.Null,
                    m_WorkbenchLineSettingsVersion);
                return Entity.Null;
            }

            m_ConfiguredAllowedDepotCacheFallbackResolves++;
            Entity resolvedDepot = CanonicalizeTransportDepotEntity(ResolveWorkbenchDepotEntityById(depotId));
            if (resolvedDepot != Entity.Null)
            {
                m_ConfiguredAllowedDepotCacheByLine[line] = new ConfiguredAllowedDepotCacheEntry(
                    line,
                    lineId,
                    depotId,
                    resolvedDepot,
                    m_WorkbenchLineSettingsVersion);
            }
            else
            {
                m_ConfiguredAllowedDepotCacheByLine.Remove(line);
            }

            return resolvedDepot;
        }

        public ulong GetWorkbenchLineSettingsVersion()
        {
            return m_WorkbenchLineSettingsVersion;
        }

        private string NormalizeWorkbenchAllowedDepotId(string depotId)
        {
            if (string.IsNullOrWhiteSpace(depotId))
                return string.Empty;

            Entity depot = ResolveWorkbenchDepotEntityById(depotId);
            return depot != Entity.Null
                ? BuildWorkbenchDepotPersistentId(depot)
                : string.Empty;
        }

        public Entity CanonicalizeTransportDepotEntity(Entity depot)
        {
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return Entity.Null;

            Entity current = depot;
            Entity canonical = Entity.Null;
            for (int i = 0; i < 16 && current != Entity.Null && EntityManager.Exists(current); i++)
            {
                if (EntityManager.HasComponent<Game.Buildings.TransportDepot>(current))
                {
                    canonical = current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity next = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (next == current)
                    break;

                current = next;
            }

            return canonical != Entity.Null && EntityManager.Exists(canonical)
                ? canonical
                : Entity.Null;
        }

        private string BuildWorkbenchDepotPersistentId(Entity depot)
        {
            return BuildWorkbenchDepotPersistentIdRaw(CanonicalizeTransportDepotEntity(depot));
        }

        private string BuildWorkbenchDepotPersistentIdRaw(Entity depot)
        {
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return string.Empty;

            string transportType = string.Empty;
            int prefabIndex = 0;
            if (EntityManager.HasComponent<PrefabRef>(depot))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(depot).m_Prefab;
                prefabIndex = prefab.Index;
                if (prefab != Entity.Null && EntityManager.HasComponent<TransportDepotData>(prefab))
                {
                    transportType = EntityManager.GetComponentData<TransportDepotData>(prefab).m_TransportType.ToString();
                }
            }

            int x = 0;
            int y = 0;
            int z = 0;
            if (EntityManager.HasComponent<Transform>(depot))
            {
                float3 position = EntityManager.GetComponentData<Transform>(depot).m_Position;
                x = (int)math.round(position.x);
                y = (int)math.round(position.y);
                z = (int)math.round(position.z);
            }

            return "depot:"
                + (string.IsNullOrEmpty(transportType) ? "-" : transportType.ToLowerInvariant())
                + ":prefab-" + prefabIndex.ToString()
                + ":x" + x.ToString()
                + ":y" + y.ToString()
                + ":z" + z.ToString();
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
            HashSet<Entity> seenCanonicalDepots = new HashSet<Entity>();
            EntityQuery depotQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Buildings.ServiceUpgrade>());
            NativeArray<Entity> depotEntities = depotQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity depot = CanonicalizeTransportDepotEntity(depotEntities[i]);
                    if (depot == Entity.Null || !seenCanonicalDepots.Add(depot))
                        continue;

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
                        id = BuildWorkbenchDepotPersistentId(depot),
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

            EntityQuery rawDepotQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.Exclude<Deleted>());

            if (int.TryParse(depotId, out int legacyDepotIndex))
            {
                NativeArray<Entity> legacyDepotEntities = rawDepotQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < legacyDepotEntities.Length; i++)
                    {
                        Entity depot = legacyDepotEntities[i];
                        if (depot.Index == legacyDepotIndex)
                        {
                            return CanonicalizeTransportDepotEntity(depot);
                        }
                    }
                }
                finally
                {
                    if (legacyDepotEntities.IsCreated) legacyDepotEntities.Dispose();
                }
            }

            NativeArray<Entity> depotEntities = rawDepotQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < depotEntities.Length; i++)
                {
                    Entity rawDepot = depotEntities[i];
                    Entity canonicalDepot = CanonicalizeTransportDepotEntity(rawDepot);
                    if (string.Equals(BuildWorkbenchDepotPersistentId(canonicalDepot), depotId, StringComparison.Ordinal)
                        || string.Equals(BuildWorkbenchDepotPersistentIdRaw(rawDepot), depotId, StringComparison.Ordinal))
                    {
                        return canonicalDepot != Entity.Null ? canonicalDepot : rawDepot;
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

        private static bool ArePlannerImportContractsEquivalent(
            DispatchWorkbenchPlannerImportContractDto left,
            DispatchWorkbenchPlannerImportContractDto right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;

            string leftJson = DispatchWorkbenchJson.Serialize(left);
            string rightJson = DispatchWorkbenchJson.Serialize(right);
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        private List<string> ValidateWorkbenchRequest(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            bool validateApplyOnlyConstraints)
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
            HashSet<string> draftScopeLineIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(request.selectedLineId))
            {
                draftScopeLineIds.Add(request.selectedLineId);
            }
            if (!string.IsNullOrEmpty(request.selectedEditLine))
            {
                draftScopeLineIds.Add(request.selectedEditLine);
            }
            foreach (string lineId in localIds)
            {
                draftScopeLineIds.Add(lineId);
            }
            foreach (string lineId in expressIds)
            {
                draftScopeLineIds.Add(lineId);
            }

            if (!request.nativeScheduleWriter && localIds.Count == 0)
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
                        string normalizedDepotId = NormalizeWorkbenchAllowedDepotId(setting.allowedDepotId);
                        if (string.IsNullOrEmpty(normalizedDepotId))
                        {
                            continue;
                        }

                        if (!depotById.TryGetValue(normalizedDepotId, out DispatchWorkbenchDepotDto depot))
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

            List<DispatchWorkbenchStagedRowDto> requestLineDraftRows =
                BuildRequestLineDraftRowsByDraftKey(request, GetDraftKey(request.selectedLineId))
                    .Values
                    .SelectMany(group => group)
                    .ToList();
            if (requestLineDraftRows.Count > 0
                || request.lineDraftRows != null
                || request.stagedRows != null
                || request.lineDraftRowsByLineId != null)
            {
                const int minOriginDepartureGapMinutes = 5;
                HashSet<string> stagedKeys = new HashSet<string>();
                Dictionary<string, string> stagedLineKinds = new Dictionary<string, string>();
                List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> stagedOriginDepartures =
                    new List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)>();
                for (int i = 0; i < requestLineDraftRows.Count; i++)
                {
                    DispatchWorkbenchStagedRowDto row = requestLineDraftRows[i];
                    if (string.IsNullOrEmpty(row.lineId))
                    {
                        errors.Add("Line draft row " + row.id + " is missing lineId.");
                    }
                    else if (runtimeLineById.Count > 0 && !runtimeLineById.ContainsKey(row.lineId))
                    {
                        errors.Add("Line draft row " + row.id + " references a line that no longer exists.");
                    }

                    if (ParseTimeMinutes(row.time) < 0)
                    {
                        errors.Add("Line draft row " + row.id + " has invalid time.");
                    }

                    string stagedKey = (row.lineId ?? string.Empty)
                        + "|"
                        + (string.IsNullOrEmpty(row.kind) ? "local" : row.kind)
                        + "|"
                        + (row.time ?? string.Empty);
                    if (!stagedKeys.Add(stagedKey))
                    {
                        errors.Add("Line draft rows contain duplicate departures for the same line and service.");
                    }

                    string normalizedKind = string.IsNullOrEmpty(row.kind) ? "local" : row.kind;
                    if (stagedLineKinds.TryGetValue(row.lineId ?? string.Empty, out string existingKind))
                    {
                        if (!string.Equals(existingKind, normalizedKind, StringComparison.Ordinal))
                        {
                            errors.Add("Line draft rows cannot mix local and express departures for the same line.");
                        }
                    }
                    else
                    {
                        stagedLineKinds[row.lineId ?? string.Empty] = normalizedKind;
                    }

                    int stagedMinutes = ParseTimeMinutes(row.time);
                    if (stagedMinutes >= 0
                        && runtimeLineById.TryGetValue(row.lineId ?? string.Empty, out WorkbenchLineRuntime runtimeLine)
                        && !string.IsNullOrEmpty(runtimeLine.OriginStationId))
                    {
                        stagedOriginDepartures.Add((
                            row.id ?? string.Empty,
                            row.lineId ?? string.Empty,
                            runtimeLine.OriginStationId,
                            runtimeLine.OriginStationName ?? string.Empty,
                            stagedMinutes));
                    }
                }

                if (validateApplyOnlyConstraints)
                {
                    foreach (IGrouping<string, (string RowId, string LineId, string OriginId, string OriginName, int Minutes)> group in stagedOriginDepartures
                        .GroupBy(row => row.OriginId, StringComparer.Ordinal))
                    {
                        (string RowId, string LineId, string OriginId, string OriginName, int Minutes)[] ordered = group
                            .OrderBy(row => row.Minutes)
                            .ToArray();
                        for (int i = 1; i < ordered.Length; i++)
                        {
                            int gap = ordered[i].Minutes - ordered[i - 1].Minutes;
                            if (gap < minOriginDepartureGapMinutes)
                            {
                                string originLabel = !string.IsNullOrEmpty(ordered[i].OriginName)
                                    ? ordered[i].OriginName
                                    : ordered[i].OriginId;
                                errors.Add(
                                    "Staged rows depart from the same origin station too close together: "
                                    + originLabel
                                    + " "
                                    + SlotStr(ordered[i - 1].Minutes)
                                    + " and "
                                    + SlotStr(ordered[i].Minutes)
                                    + ".");
                            }
                        }
                    }
                }
            }

            return errors;
        }

        private List<string> ValidateAppliedWorkbenchCandidateRows(
            string activeLineKey,
            List<DispatchWorkbenchStagedRowDto> activeRows,
            List<WorkbenchLineRuntime> runtimeLines)
        {
            const int minOriginDepartureGapMinutes = 5;
            List<string> errors = new List<string>();
            Dictionary<string, WorkbenchLineRuntime> runtimeLineById = runtimeLines?
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .ToDictionary(line => line.Id, line => line, StringComparer.Ordinal)
                ?? new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            HashSet<string> replacedLineIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(activeLineKey))
            {
                replacedLineIds.Add(activeLineKey);
            }
            if (activeRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in activeRows)
                {
                    if (!string.IsNullOrEmpty(row?.lineId))
                    {
                        replacedLineIds.Add(row.lineId);
                    }
                }
            }

            List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> departures =
                new List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)>();
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                if (replacedLineIds.Contains(entry.Key))
                    continue;

                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.StagedRows == null)
                    continue;

                foreach (DispatchWorkbenchStagedRowDto row in state.StagedRows)
                {
                    AddWorkbenchOriginDeparture(row, runtimeLineById, departures);
                }
            }

            if (activeRows != null)
            {
                foreach (DispatchWorkbenchStagedRowDto row in activeRows)
                {
                    AddWorkbenchOriginDeparture(row, runtimeLineById, departures);
                }
            }

            foreach (IGrouping<string, (string RowId, string LineId, string OriginId, string OriginName, int Minutes)> group in departures
                .GroupBy(row => row.OriginId, StringComparer.Ordinal))
            {
                (string RowId, string LineId, string OriginId, string OriginName, int Minutes)[] ordered = group
                    .OrderBy(row => row.Minutes)
                    .ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    int gap = ordered[i].Minutes - ordered[i - 1].Minutes;
                    if (gap >= minOriginDepartureGapMinutes)
                        continue;

                    string originLabel = !string.IsNullOrEmpty(ordered[i].OriginName)
                        ? ordered[i].OriginName
                        : ordered[i].OriginId;
                    errors.Add(
                        "Applied timetable would depart from the same origin station too close together: "
                        + originLabel
                        + " "
                        + SlotStr(ordered[i - 1].Minutes)
                        + " "
                        + ordered[i - 1].LineId
                        + " and "
                        + SlotStr(ordered[i].Minutes)
                        + " "
                        + ordered[i].LineId
                        + ".");
                }
            }

            return errors;
        }

        private static void AddWorkbenchOriginDeparture(
            DispatchWorkbenchStagedRowDto row,
            Dictionary<string, WorkbenchLineRuntime> runtimeLineById,
            List<(string RowId, string LineId, string OriginId, string OriginName, int Minutes)> departures)
        {
            int minutes = ParseTimeMinutes(row?.time);
            if (minutes < 0)
                return;

            string lineId = row?.lineId ?? string.Empty;
            if (!runtimeLineById.TryGetValue(lineId, out WorkbenchLineRuntime runtimeLine)
                || string.IsNullOrEmpty(runtimeLine.OriginStationId))
            {
                return;
            }

            departures.Add((
                row?.id ?? string.Empty,
                lineId,
                runtimeLine.OriginStationId,
                runtimeLine.OriginStationName ?? string.Empty,
                minutes));
        }

        private void NormalizeRequestedLineSettingsFromMergedView(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines)
        {
            if (request?.nativeScheduleWriter == true)
                return;

            if (request?.mergedView == null || request.lineSettings == null || request.lineSettings.Length == 0)
                return;

            List<string> localIds = NormalizeLineIdList(
                request.mergedView.localLineIds,
                request.mergedView.localLineId,
                runtimeLines);
            List<string> expressIds = NormalizeLineIdList(
                request.mergedView.expressLineIds,
                request.mergedView.expressLineId,
                runtimeLines);

            HashSet<string> localSet = new HashSet<string>(localIds, StringComparer.Ordinal);
            HashSet<string> expressSet = new HashSet<string>(expressIds, StringComparer.Ordinal);

            for (int i = 0; i < request.lineSettings.Length; i++)
            {
                DispatchWorkbenchLineSettingDto setting = request.lineSettings[i];
                if (setting == null || string.IsNullOrEmpty(setting.lineId))
                    continue;

                bool isLocal = localSet.Contains(setting.lineId);
                bool isExpress = expressSet.Contains(setting.lineId);
                if (isLocal == isExpress)
                    continue;

                setting.serviceKind = isExpress ? "express" : "local";
            }
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
