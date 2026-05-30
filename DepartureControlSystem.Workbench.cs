using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Colossal.Core;
using Colossal.Serialization.Entities;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI;
using RapidTransitMod.TrackModel;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private const string StationAnchorKeyPrefix = "sak:";
        private static readonly FieldInfo s_NameTypeField =
            typeof(NameSystem.Name).GetField("m_NameType", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameIdField =
            typeof(NameSystem.Name).GetField("m_NameID", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_NameArgsField =
            typeof(NameSystem.Name).GetField("m_NameArgs", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly WorkbenchDraftStore m_WorkbenchDrafts = new WorkbenchDraftStore();
        private readonly AppliedTimetableStore m_AppliedTimetableStore = new AppliedTimetableStore();
        private readonly AppliedTimetableValidator m_AppliedTimetableValidator = new AppliedTimetableValidator();
        private readonly LineSettingsStore m_LineSettingsStore = new LineSettingsStore();
        private readonly RuntimeFeatureSettingsStore m_RuntimeFeatureSettingsStore = new RuntimeFeatureSettingsStore();
        private WorkbenchRuntimeConfigAdapter m_WorkbenchRuntimeConfig;
        private WorkbenchQueryService m_WorkbenchQuery;
        private WorkbenchSnapshotBuilder m_WorkbenchSnapshotBuilder;
        private WorkbenchPersistence m_WorkbenchPersistence;
        private WorkbenchCommandHandler m_WorkbenchCommandHandler;
        private WorkbenchSaveService m_WorkbenchSaveService;
        private readonly Dictionary<string, DispatchWorkbenchPlannerImportContractDto> m_AppliedPlannerImportContracts =
            new Dictionary<string, DispatchWorkbenchPlannerImportContractDto>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> m_WorkbenchLineOriginHoldLimits = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> m_WorkbenchLineMaxStationDwellMinutes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineAllowedDepots = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_WorkbenchLineServiceKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, AppliedWorkbenchLineState> m_AppliedWorkbenchLines = new Dictionary<string, AppliedWorkbenchLineState>(StringComparer.Ordinal);
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
        private bool m_AppliedWorkbenchPersistenceLoaded = false;
        private const int DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES = 20;
        private const int DEFAULT_MAX_STATION_DWELL_MINUTES = 10;
        private const int MIN_ORIGIN_HOLD_LIMIT_MINUTES = 5;
        private const int MAX_ORIGIN_HOLD_LIMIT_MINUTES = 120;
        private const uint CONFIGURED_ALLOWED_DEPOT_CACHE_LOG_INTERVAL_FRAMES = 3600u;
        private static readonly bool ENABLE_WORKBENCH_INTEGRITY_REPORT = true;

        private WorkbenchRuntimeConfigAdapter GetWorkbenchRuntimeConfig()
        {
            if (m_WorkbenchRuntimeConfig != null)
                return m_WorkbenchRuntimeConfig;

            m_WorkbenchRuntimeConfig = new WorkbenchRuntimeConfigAdapter(
                m_AppliedTimetableStore,
                m_LineSettingsStore,
                m_AppliedTimetableValidator,
                LineIdentityService.GetKey,
                GetWorkbenchStoreLineKey,
                LineIdentityService.GetId,
                NormalizeOriginHoldLimitMinutes,
                NormalizeMaxStationDwellMinutes,
                NormalizeWorkbenchAllowedDepotIdFromSnapshot,
                NormalizeWorkbenchServiceKind,
                BuildAppliedDepartureMinutes,
                ParseTimeMinutes,
                message => Mod.log.Info(message));
            return m_WorkbenchRuntimeConfig;
        }

        private WorkbenchQueryService GetWorkbenchQuery()
        {
            if (m_WorkbenchQuery != null)
                return m_WorkbenchQuery;

            m_WorkbenchQuery = new WorkbenchQueryService(
                m_WorkbenchDrafts,
                m_AppliedTimetableStore,
                m_AppliedWorkbenchLines,
                BuildWorkbenchLinesStable,
                BuildWorkbenchDepots,
                BuildWorkbenchStations,
                BuildRealtimeWorkbenchTrips,
                BuildWorkbenchLineDraftRowsByLineId,
                CloneWorkbenchLineRuntime,
                CloneWorkbenchDepot,
                CloneStagedRow,
                ClonePlannerImportContract,
                BuildAppliedRowNote,
                GetStoreLineId,
                MinutesToSlotString,
                (rows, draftKey) => HasWorkbenchRowsForDraft(rows, draftKey));
            return m_WorkbenchQuery;
        }

        private WorkbenchSnapshotBuilder GetWorkbenchSnapshotBuilder()
        {
            if (m_WorkbenchSnapshotBuilder != null)
                return m_WorkbenchSnapshotBuilder;

            m_WorkbenchSnapshotBuilder = new WorkbenchSnapshotBuilder(
                GetWorkbenchQuery(),
                m_WorkbenchDrafts,
                GetOrCreateWorkbenchDraft,
                lineId => m_WorkbenchDrafts.SetPreferredLineId(lineId),
                GetPreferredWorkbenchLineId,
                EnsureMergedViewDefaultsStable,
                CollectWorkbenchDraftRules,
                CloneStagedRow,
                line => GetWorkbenchOriginHoldLimitMinutes(line),
                line => GetWorkbenchMaxStationDwellMinutes(line),
                line => GetWorkbenchAllowedDepotId(line),
                LogWorkbenchSnapshot,
                WriteWorkbenchIntegrityReport);
            return m_WorkbenchSnapshotBuilder;
        }

        private WorkbenchPersistence GetWorkbenchPersistence()
        {
            if (m_WorkbenchPersistence != null)
                return m_WorkbenchPersistence;

            m_WorkbenchPersistence = new WorkbenchPersistence(
                EntityManager,
                () => m_CitySystem.City,
                m_WorkbenchDrafts,
                m_WorkbenchLineOriginHoldLimits,
                m_WorkbenchLineMaxStationDwellMinutes,
                m_WorkbenchLineAllowedDepots,
                m_WorkbenchLineServiceKinds,
                m_AppliedWorkbenchLines,
                ClearLineSettingsStore,
                InvalidateConfiguredAllowedDepotCache,
                ApplyRuntimeFeatureSettings,
                BuildRuntimeFeatureSettingsDto,
                ApplyWorkbenchLineSettings,
                persisted =>
                {
                    persisted.broadcastAssetDirectory = m_BroadcastAssetDirectory;
                    persisted.broadcastAssets = BuildPersistedBroadcastAssetStates();
                    persisted.broadcastDraftLineBindings = BuildPersistedBroadcastDraftLineBindingStates();
                    persisted.broadcastDraftRules = BuildPersistedBroadcastDraftRuleStates();
                    persisted.broadcastDraftPlatformAnnouncements = BuildPersistedBroadcastDraftPlatformAnnouncementStates();
                    persisted.broadcastLineBindings = BuildPersistedBroadcastLineBindingStates();
                    persisted.broadcastRules = BuildPersistedBroadcastRuleStates();
                    persisted.broadcastPlatformAnnouncements = BuildPersistedBroadcastPlatformAnnouncementStates();
                    persisted.broadcastAppliedState = BuildPersistedBroadcastAppliedState();
                    persisted.broadcastDraftVolume = m_BroadcastDraftVolumePercent;
                },
                persisted =>
                {
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
                },
                EnumerateWorkbenchLineSettingIds,
                GetWorkbenchOriginHoldLimitMinutes,
                GetWorkbenchMaxStationDwellMinutes,
                GetWorkbenchAllowedDepotId,
                GetWorkbenchConfiguredLineServiceKind,
                BuildWorkbenchRuntimeLineIdsForRestore,
                ApplyCurrentTimeWindowDefaults,
                CloneManualRow,
                CloneAutoRule,
                CloneStagedRow,
                ClonePlannerImportContract,
                DeduplicateRowsByIdLast,
                DeduplicateWorkbenchManualRowsForMigration,
                DeduplicateWorkbenchAutoRulesForMigration,
                DeduplicateWorkbenchStagedRowsForMigration,
                ParseTimeMinutes,
                SummarizeWorkbenchDraftRows,
                LogWorkbenchException);
            return m_WorkbenchPersistence;
        }

        private WorkbenchCommandHandler GetWorkbenchCommandHandler()
        {
            if (m_WorkbenchCommandHandler != null)
                return m_WorkbenchCommandHandler;

            m_WorkbenchCommandHandler = new WorkbenchCommandHandler(
                GetWorkbenchQuery(),
                m_WorkbenchDrafts,
                () => m_WorkbenchSnapshotVersion,
                AdvanceWorkbenchSnapshotVersion,
                BuildWorkbenchSnapshot,
                GetOrCreateWorkbenchDraft,
                lineId => m_WorkbenchDrafts.SetPreferredLineId(lineId),
                ApplyRuntimeFeatureSettings,
                AreRuntimeFeatureSettingsEquivalent,
                ApplyWorkbenchLineSettings,
                AreWorkbenchLineSettingsEquivalent,
                (lineKey, rows, runtimeLines) => ValidateAppliedWorkbenchCandidateRows(lineKey, rows, runtimeLines),
                ValidateWorkbenchRequest,
                EnumerateWorkbenchLineSettingIds,
                GetWorkbenchConfiguredLineServiceKind,
                CloneManualRow,
                CloneAutoRule,
                CloneStagedRow,
                ClonePlannerImportContract,
                DeduplicateRowsByIdLast,
                AreMergedViewsEquivalent,
                AreManualRowsEquivalent,
                AreAutoRulesEquivalent,
                AreStagedRowsEquivalent,
                ArePlannerImportContractsEquivalent,
                RefreshAppliedWorkbenchLineSettings,
                ApplyWorkbenchDraftRowsToAppliedRuntime,
                SeedObservationFromAppliedRows,
                InvalidateAppliedWorkbenchTrackModelState,
                SaveWorkbenchPersistence,
                SaveAppliedWorkbenchPersistence,
                WorkbenchEvents.PublishSnapshot);
            return m_WorkbenchCommandHandler;
        }

        private WorkbenchSaveService GetWorkbenchSaveService()
        {
            if (m_WorkbenchSaveService != null)
                return m_WorkbenchSaveService;

            m_WorkbenchSaveService = new WorkbenchSaveService(
                GetWorkbenchCommandHandler(),
                GetWorkbenchPersistence(),
                BuildWorkbenchSnapshot,
                () => m_WorkbenchSnapshotVersion,
                DescribeWorkbenchException,
                LogWorkbenchException,
                action => MainThreadDispatcher.RunOnMainThread(action));
            return m_WorkbenchSaveService;
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ResetWorkbenchPersistenceStateForLoad();
            TryRestoreWorkbenchPersistence();
            TryRestoreAppliedWorkbenchPersistence();
            try
            {
                WorkbenchEvents.PublishSnapshot(BuildWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
            }
            catch (Exception ex)
            {
                LogWorkbenchException("OnGameLoaded.NotifyWorkbenchSnapshotChanged", ex);
            }
        }

        private void ResetWorkbenchPersistenceStateForLoad()
        {
            GetWorkbenchSaveService().ResetForLoad();
            GetWorkbenchPersistence().ResetForLoad();
            m_WorkbenchDrafts.Clear();
            m_AppliedWorkbenchLines.Clear();
            m_AppliedPlannerImportContracts.Clear();
            m_WorkbenchLineOriginHoldLimits.Clear();
            m_WorkbenchLineMaxStationDwellMinutes.Clear();
            m_WorkbenchLineAllowedDepots.Clear();
            m_WorkbenchLineServiceKinds.Clear();
            ClearAppliedTimetableStore();
            ClearLineSettingsStore();
            m_RuntimeFeatureSettingsStore.Reset();
            m_WorkbenchLineFrameSnapshots.Clear();
            m_ConfiguredAllowedDepotCacheByLine.Clear();
            m_LastWorkbenchSnapshotLogKey = string.Empty;
            m_LastAppliedWorkbenchInspectLogKey = string.Empty;
            m_AppliedWorkbenchPersistenceLoaded = false;
        }

        public string LoadWorkbenchSnapshotJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchSnapshot(null));
        }

        public string RefreshWorkbenchSnapshotJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchSnapshot(GetPreferredWorkbenchLineId()));
        }

        public string RefreshWorkbenchMetadataJson()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return DispatchWorkbenchJson.Serialize(BuildWorkbenchMetadataSnapshot());
        }

        public string SaveWorkbenchDraftJson(string requestJson)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return GetWorkbenchSaveService().SaveWorkbenchDraftJson(requestJson);
        }

        public string StartWorkbenchSaveOperationJson(string requestJson)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            return GetWorkbenchSaveService().StartWorkbenchSaveOperationJson(requestJson);
        }

        public string GetWorkbenchSaveOperationStatusJson(string operationId)
        {
            return GetWorkbenchSaveService().GetWorkbenchSaveOperationStatusJson(operationId);
        }

        private ulong AdvanceWorkbenchSnapshotVersion()
        {
            m_WorkbenchSnapshotVersion++;
            return m_WorkbenchSnapshotVersion;
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
                OriginStationName = line.OriginStationName ?? string.Empty
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

        private static string NormalizeWorkbenchAllowedDepotIdFromSnapshot(string depotId)
        {
            return string.IsNullOrWhiteSpace(depotId) ? string.Empty : depotId;
        }

        private DispatchWorkbenchSnapshot BuildWorkbenchSnapshot(string preferredLineId)
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            DispatchWorkbenchSnapshot snapshot =
                GetWorkbenchSnapshotBuilder().Build(preferredLineId, m_WorkbenchSnapshotVersion, "game-backend");
            snapshot.featureSettings = BuildRuntimeFeatureSettingsDto();
            return snapshot;
        }

        private DispatchWorkbenchSnapshot BuildWorkbenchMetadataSnapshot()
        {
            EnsureWorkbenchPersistenceLoaded();
            EnsureAppliedWorkbenchPersistenceLoaded();
            DispatchWorkbenchSnapshot snapshot = GetWorkbenchSnapshotBuilder().BuildMetadata(
                GetPreferredWorkbenchLineId(),
                m_WorkbenchSnapshotVersion,
                "game-backend");
            snapshot.featureSettings = BuildRuntimeFeatureSettingsDto();
            return snapshot;
        }

        private void CollectWorkbenchDraftRules(
            string lineKey,
            HashSet<string> validRuntimeLineIds,
            List<DispatchWorkbenchManualRowDto> manualRows,
            List<DispatchWorkbenchAutoRuleDto> autoRules,
            HashSet<string> manualRowIds,
            HashSet<string> autoRuleIds)
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

        }

        private DispatchWorkbenchLineDraftRowsDto[] BuildWorkbenchLineDraftRowsByLineId(HashSet<string> validRuntimeLineIds)
        {
            List<DispatchWorkbenchLineDraftRowsDto> blocks = new List<DispatchWorkbenchLineDraftRowsDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_WorkbenchDrafts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string draftKey = entry.Key;
                DispatchWorkbenchDraftState draft = entry.Value;
                if (draft?.StagedRows == null || draft.StagedRows.Count == 0)
                    continue;

                List<DispatchWorkbenchStagedRowDto> rows = draft.StagedRows
                    .Where(row => row != null
                        && !string.IsNullOrEmpty(row.lineId)
                        && string.Equals(GetDraftKey(row.lineId), draftKey, StringComparison.Ordinal)
                        && (validRuntimeLineIds == null || validRuntimeLineIds.Count == 0 || validRuntimeLineIds.Contains(row.lineId)))
                    .Select(CloneStagedRow)
                    .OrderBy(row => ParseTimeMinutes(row.time))
                    .ThenBy(row => row.id, StringComparer.Ordinal)
                    .ToList();

                if (rows.Count == 0)
                    continue;

                blocks.Add(new DispatchWorkbenchLineDraftRowsDto
                {
                    lineId = rows[0].lineId,
                    lineDraftRows = DeduplicateRowsByIdLast(rows).ToArray()
                });
            }

            return blocks.ToArray();
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

        private HashSet<string> BuildWorkbenchRuntimeLineIdsForRestore()
        {
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);
            NativeArray<Entity> entities = m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity line = entities[i];
                    if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                        continue;

                    string lineId = GetWorkbenchLineId(line);
                    if (!string.IsNullOrEmpty(lineId))
                    {
                        lineIds.Add(lineId);
                    }
                }
            }
            finally
            {
                if (entities.IsCreated) entities.Dispose();
            }

            return lineIds;
        }

        internal List<WorkbenchLineRuntime> BuildWorkbenchLinesStable()
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
            return m_WorkbenchDrafts.ResolvePreferredLineId();
        }

        internal static string GetDraftKey(string lineId)
        {
            return WorkbenchDraftStore.GetKey(lineId);
        }

        private string GetStoreLineId(LineKey lineKey)
        {
            return GetWorkbenchRuntimeConfig().GetLineId(lineKey);
        }

        private LineKey GetStoreLineKey(string lineId)
        {
            return GetWorkbenchRuntimeConfig().GetLineKey(lineId);
        }

        private LineKey GetWorkbenchStoreLineKey(Entity line, string fallbackLineId = null)
        {
            string lineId = !string.IsNullOrEmpty(fallbackLineId)
                ? fallbackLineId
                : GetWorkbenchLineId(line);
            if (!string.IsNullOrEmpty(lineId))
            {
                return LineIdentityService.GetKey(lineId);
            }

            return LineIdentityService.GetKey(EntityManager, line, fallbackLineId);
        }

        private LineKey GetStoreLineKey(Entity line, string fallbackLineId = null)
        {
            return GetWorkbenchRuntimeConfig().GetLineKey(line, fallbackLineId);
        }

        internal string GetWorkbenchLineId(Entity line)
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

            return string.Empty;
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
            LineKey storeKey = GetStoreLineKey(line, lineId);
            bool timetableApplied = !storeKey.IsEmpty
                ? m_AppliedTimetableStore.IsManaged(storeKey)
                : m_AppliedWorkbenchLines.ContainsKey(lineKey);
            m_AppliedWorkbenchLines.TryGetValue(lineKey, out AppliedWorkbenchLineState appliedState);
            string configuredServiceKind = GetWorkbenchConfiguredLineServiceKind(lineId);
            string appliedServiceKind = timetableApplied
                ? GetAppliedWorkbenchLineServiceKind(storeKey, appliedState)
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
            m_WorkbenchLineSettingsVersion = Math.Max(
                m_WorkbenchLineSettingsVersion + 1,
                m_LineSettingsStore.Version);
        }

        private void ClearAppliedTimetableStore()
        {
            m_AppliedTimetableStore.Clear();
        }

        private void ClearLineSettingsStore()
        {
            m_LineSettingsStore.Clear();
            m_WorkbenchLineSettingsVersion = Math.Max(
                m_WorkbenchLineSettingsVersion + 1,
                m_LineSettingsStore.Version);
        }

        private IEnumerable<string> EnumerateWorkbenchLineSettingIds()
        {
            return GetWorkbenchRuntimeConfig().EnumerateSettingIds(
                m_WorkbenchLineOriginHoldLimits,
                m_WorkbenchLineMaxStationDwellMinutes,
                m_WorkbenchLineAllowedDepots,
                m_WorkbenchLineServiceKinds);
        }

        private void SyncLineSettingsStore()
        {
            m_WorkbenchLineSettingsVersion = GetWorkbenchRuntimeConfig().SyncSettings(
                m_WorkbenchLineOriginHoldLimits,
                m_WorkbenchLineMaxStationDwellMinutes,
                m_WorkbenchLineAllowedDepots,
                m_WorkbenchLineServiceKinds,
                m_WorkbenchLineSettingsVersion);
        }

        private void SyncAppliedTimetableStore()
        {
            GetWorkbenchRuntimeConfig().SyncApplied(m_AppliedWorkbenchLines);
        }

        private void SyncAppliedTimetableStore(string lineId, AppliedWorkbenchLineState applied)
        {
            GetWorkbenchRuntimeConfig().SyncApplied(lineId, applied);
        }

        private AppliedTimetableState BuildAppliedTimetableState(string lineId, AppliedWorkbenchLineState applied)
        {
            return GetWorkbenchRuntimeConfig().BuildAppliedState(lineId, applied);
        }

        private AppliedTimetableRow[] BuildAppliedTimetableRows(
            IEnumerable<DispatchWorkbenchStagedRowDto> rows,
            string lineId)
        {
            return GetWorkbenchRuntimeConfig().BuildAppliedRows(rows, lineId);
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
            GetWorkbenchPersistence().EnsureLoaded();
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
            return GetWorkbenchPersistence().TryRestoreWorkbenchPersistence();
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
                ClearAppliedTimetableStore();
                return false;
            }

            if (!EntityManager.HasBuffer<AppliedWorkbenchLineStateElement>(city)
                && !EntityManager.HasBuffer<AppliedWorkbenchStagedRowElement>(city))
            {
                m_AppliedWorkbenchLines.Clear();
                ClearAppliedTimetableStore();
                if (m_WorkbenchDrafts.Values.Any(draft =>
                    draft != null && draft.DraftApplied && draft.StagedRows != null && draft.StagedRows.Count > 0))
                {
                    BackfillAppliedFromDrafts();
                    SaveAppliedWorkbenchPersistence();
                    m_AppliedWorkbenchPersistenceLoaded = true;
                    return true;
                }

                m_AppliedWorkbenchPersistenceLoaded = true;
                return false;
            }

            m_AppliedWorkbenchLines.Clear();
            ClearAppliedTimetableStore();

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
                SyncAppliedTimetableStore();
                if (SyncWorkbenchDraftsFromAppliedState())
                {
                    SaveWorkbenchPersistence();
                }
                RefreshAppliedPlannerContracts();
                InvalidateWorkbenchLineFrameSnapshots();
                InvalidateAppliedWorkbenchTrackModelState();
                if (m_AppliedWorkbenchLines.Values.Any(
                    state => state != null && state.StagedRows != null && state.StagedRows.Count > 0))
                {
                    SeedObservationFromAppliedRows(GetPreferredWorkbenchLineId());
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
            GetWorkbenchPersistence().SaveWorkbenchPersistence();
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

        private void BackfillAppliedFromDrafts()
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

            SyncAppliedTimetableStore();
            RefreshAppliedPlannerContracts();
            SyncWorkbenchDraftsFromAppliedState();
            InvalidateWorkbenchLineFrameSnapshots();
            InvalidateAppliedWorkbenchTrackModelState();
        }

        private void ApplyWorkbenchDraftRowsToAppliedRuntime(
            IEnumerable<string> draftKeys,
            List<WorkbenchLineRuntime> runtimeLines)
        {
            Dictionary<string, WorkbenchLineRuntime> runtimeById = (runtimeLines ?? BuildWorkbenchLinesStable())
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .ToDictionary(line => line.Id, StringComparer.Ordinal);
            HashSet<string> keys = new HashSet<string>(
                (draftKeys ?? Array.Empty<string>())
                    .Where(key => !string.IsNullOrEmpty(key))
                    .Select(GetDraftKey),
                StringComparer.Ordinal);

            foreach (string draftKey in keys)
            {
                if (!m_WorkbenchDrafts.TryGetValue(draftKey, out DispatchWorkbenchDraftState draft)
                    || draft == null
                    || draft.StagedRows == null)
                {
                    m_AppliedWorkbenchLines.Remove(draftKey);
                    continue;
                }

                List<DispatchWorkbenchStagedRowDto> lineRows = draft.StagedRows
                    .Where(row => row != null
                        && !string.IsNullOrEmpty(row.lineId)
                        && string.Equals(GetDraftKey(row.lineId), draftKey, StringComparison.Ordinal))
                    .Select(CloneStagedRow)
                    .ToList();
                if (lineRows.Count == 0 || !runtimeById.TryGetValue(draftKey, out WorkbenchLineRuntime runtime))
                {
                    m_AppliedWorkbenchLines.Remove(draftKey);
                    continue;
                }

                AppliedWorkbenchLineState applied = new AppliedWorkbenchLineState
                {
                    LineEntity = runtime.Entity,
                    OriginHoldLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(draftKey),
                    MaxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(draftKey),
                    StagedRows = lineRows
                };
                applied.DepartureMinutesCache = BuildAppliedDepartureMinutes(applied.StagedRows, draftKey);
                m_AppliedWorkbenchLines[draftKey] = applied;
            }

            SyncAppliedTimetableStore();
            RefreshAppliedPlannerContracts();
            SyncWorkbenchDraftsFromAppliedState();
            InvalidateWorkbenchLineFrameSnapshots();
            InvalidateAppliedWorkbenchTrackModelState();
        }

        private void InvalidateAppliedWorkbenchTrackModelState()
        {
            m_TrackModel.MarkSharedIndexDirty();
            m_Bypass.ClearAll();
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

        private void RefreshAppliedPlannerContracts()
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
            return request?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>();
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

                    rowsByDraftKey[targetKey] = (block?.lineDraftRows ?? Array.Empty<DispatchWorkbenchStagedRowDto>())
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

        private static Dictionary<string, DispatchWorkbenchPlannerImportContractDto> BuildRequestPlanRefsByDraftKey(
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
                        ClonePlannerImportContract(entry?.contract);
                    string targetKey = GetDraftKeyStatic(
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
                ClonePlannerImportContract(request?.plannerImportContract);
            if (fallbackContract == null)
                return refsByDraftKey;

            IEnumerable<string> targetKeys = (fallbackContract.importedLineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Select(GetDraftKeyStatic);
            if (!targetKeys.Any())
            {
                targetKeys = (fallbackDraftKeys ?? Array.Empty<string>())
                    .Where(key => !string.IsNullOrEmpty(key))
                    .Select(GetDraftKeyStatic);
            }

            foreach (string targetKey in targetKeys
                .Where(key => !string.IsNullOrEmpty(key) && !string.Equals(key, "__default__", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract =
                    ClonePlannerImportContract(fallbackContract);
                if (contract == null)
                    continue;

                contract.draftKey = targetKey;
                refsByDraftKey[targetKey] = contract;
            }

            return refsByDraftKey;
        }

        private static DispatchWorkbenchPlannerImportContractDto ResolvePlanRef(
            string draftKey,
            DispatchWorkbenchPlannerImportContractDto currentRef,
            Dictionary<string, DispatchWorkbenchPlannerImportContractDto> requestRefsByDraftKey,
            bool draftChanged,
            bool hasRows)
        {
            if (requestRefsByDraftKey != null
                && requestRefsByDraftKey.TryGetValue(draftKey, out DispatchWorkbenchPlannerImportContractDto requestedRef))
            {
                DispatchWorkbenchPlannerImportContractDto nextRef = ClonePlannerImportContract(requestedRef);
                if (nextRef != null)
                {
                    nextRef.draftKey = draftKey;
                }
                return nextRef;
            }

            if (!hasRows)
                return null;

            return ClonePlannerImportContract(currentRef);
        }

        private static bool ShouldReturnSnapshot(DispatchWorkbenchSaveRequest request)
        {
            return request?.returnSnapshot != false;
        }

        private bool ApplyAdditionalLineDraftRowsByDraftKey(
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
                string draftKey = GetDraftKey(entry.Key);
                if (string.Equals(draftKey, activeLineKey, StringComparison.Ordinal))
                    continue;

                DispatchWorkbenchDraftState draft = GetOrCreateWorkbenchDraft(draftKey);
                List<DispatchWorkbenchStagedRowDto> nextRows =
                    DeduplicateRowsByIdLast(entry.Value?.Select(CloneStagedRow).ToList()
                    ?? new List<DispatchWorkbenchStagedRowDto>());
                bool draftRowsChanged = !AreStagedRowsEquivalent(draft.StagedRows, nextRows);
                DispatchWorkbenchPlannerImportContractDto nextRef = ResolvePlanRef(
                    draftKey,
                    draft.PlannerImportContract,
                    requestRefsByDraftKey,
                    draftRowsChanged,
                    nextRows.Count > 0);
                bool refChanged = !ArePlannerImportContractsEquivalent(draft.PlannerImportContract, nextRef);
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

        private bool SyncWorkbenchDraftsFromAppliedState()
        {
            bool changed = false;
            HashSet<string> appliedLineKeys = new HashSet<string>(
                m_AppliedWorkbenchLines
                    .Where(pair => pair.Value?.StagedRows != null && pair.Value.StagedRows.Count > 0)
                    .Select(pair => pair.Key),
                StringComparer.Ordinal);
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
                    if (draft.DraftApplied || draft.RulesApplied)
                    {
                        changed = true;
                    }
                    draft.DraftApplied = false;
                    draft.RulesApplied = false;
                }
                draft.AppliedDepartureMinutesCache.Clear();
            }

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                string lineKey = entry.Key;
                AppliedWorkbenchLineState applied = entry.Value;
                if (applied?.StagedRows == null || applied.StagedRows.Count == 0)
                    continue;

                bool draftExisted = m_WorkbenchDrafts.TryGetValue(lineKey, out DispatchWorkbenchDraftState draft);
                bool draftWasApplied = draftAppliedBeforeSync.TryGetValue(lineKey, out bool wasApplied) && wasApplied;
                if (!draftExisted)
                {
                    draft = CreateEmptyWorkbenchDraftState(lineKey);
                    draft.ManualRows.Clear();
                    draft.AutoRules.Clear();
                    m_WorkbenchDrafts[lineKey] = draft;
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
                bool canRefreshDraftFromApplied = !draftExisted || draftWasApplied;
                if (canRefreshDraftFromApplied
                    && !AreStagedRowsEquivalentIgnoringIdAndNote(draft.StagedRows, applied.StagedRows))
                {
                    draft.StagedRows = applied.StagedRows.Select(CloneStagedRow).ToList();
                    changed = true;
                }
                if (canRefreshDraftFromApplied)
                {
                    EnsureAppliedMergedViewMatchesLineKind(draft, lineKey, applied);
                }
                if (draft.DraftApplied != canRefreshDraftFromApplied)
                {
                    draft.DraftApplied = canRefreshDraftFromApplied;
                    changed = true;
                }
                if (draft.RulesApplied != canRefreshDraftFromApplied)
                {
                    draft.RulesApplied = canRefreshDraftFromApplied;
                    changed = true;
                }
                draft.AppliedDepartureMinutesCache.Clear();
                m_WorkbenchLineOriginHoldLimits[lineKey] =
                    NormalizeOriginHoldLimitMinutes(applied.OriginHoldLimitMinutes);
            }

            return changed;
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
            return GetWorkbenchQuery().BuildAppliedRows();
        }

        private DispatchWorkbenchPlanRefDto[] BuildPlanRefsSnapshot()
        {
            return GetWorkbenchQuery().BuildPlanRefs();
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
            return GetWorkbenchPersistence().BuildState();
        }

        private bool RestoreWorkbenchPersistence(DispatchWorkbenchPersistentState persisted)
        {
            return GetWorkbenchPersistence().RestoreState(persisted);
        }

        internal bool IsWorkbenchTimetableApplied(Entity line)
        {
            if (!TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot))
                return false;

            return snapshot.TimetableApplied;
        }

        internal bool IsDispatchFeatureEnabled()
        {
            return m_RuntimeFeatureSettingsStore.DispatchEnabled;
        }

        internal bool IsBypassFeatureEnabled()
        {
            return m_RuntimeFeatureSettingsStore.BypassEnabled;
        }

        internal bool IsBroadcastFeatureEnabled()
        {
            return m_RuntimeFeatureSettingsStore.BroadcastEnabled;
        }

        internal bool IsDepotLockFeatureEnabled()
        {
            return m_RuntimeFeatureSettingsStore.DepotLockEnabled;
        }

        internal bool IsDispatchRuntimeManagedLine(Entity line)
        {
            return IsDispatchFeatureEnabled() && IsWorkbenchTimetableApplied(line);
        }

        private bool IsBypassRuntimeFeatureEnabled()
        {
            return m_Bypass.RuntimeEnabled() && IsBypassFeatureEnabled();
        }

        internal int[] GetAppliedWorkbenchDepartureMinutes(Entity line)
        {
            if (!TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                || !snapshot.TimetableApplied)
                return Array.Empty<int>();

            LineKey storeKey = GetStoreLineKey(line, snapshot.LineId);
            if (!storeKey.IsEmpty && m_AppliedTimetableStore.TryGet(storeKey, out AppliedTimetableState appliedState))
            {
                return appliedState.DepartureMinutes ?? Array.Empty<int>();
            }

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

        internal void LogAppliedWorkbenchLineState(Entity line, int nowMin, int nextSlot)
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

            LineKey storeKey = GetStoreLineKey(line, snapshot.LineId);
            if (!string.IsNullOrEmpty(snapshot.EffectiveServiceKind))
                return snapshot.EffectiveServiceKind;

            if (!storeKey.IsEmpty
                && m_AppliedTimetableStore.TryGet(storeKey, out AppliedTimetableState appliedState))
            {
                return appliedState.ServiceKind ?? string.Empty;
            }

            if (!m_AppliedWorkbenchLines.TryGetValue(snapshot.LineKey, out AppliedWorkbenchLineState state))
                return string.Empty;

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

        private string GetAppliedWorkbenchLineServiceKind(LineKey lineKey, AppliedWorkbenchLineState fallbackState)
        {
            if (!lineKey.IsEmpty
                && m_AppliedTimetableStore.TryGet(lineKey, out AppliedTimetableState state))
            {
                return state.ServiceKind ?? string.Empty;
            }

            return GetAppliedWorkbenchLineServiceKind(fallbackState);
        }

        private string GetEffectiveWorkbenchLineServiceKind(string lineKey, AppliedWorkbenchLineState applied)
        {
            string configuredKind = GetWorkbenchConfiguredLineServiceKind(lineKey);
            if (!string.IsNullOrEmpty(configuredKind))
            {
                return configuredKind;
            }

            return GetAppliedWorkbenchLineServiceKind(GetStoreLineKey(lineKey), applied);
        }

        private string GetEffectiveWorkbenchLineServiceKind(Entity line, AppliedWorkbenchLineState applied)
        {
            string configuredKind = GetWorkbenchConfiguredLineServiceKind(line);
            if (!string.IsNullOrEmpty(configuredKind))
            {
                return configuredKind;
            }

            return GetAppliedWorkbenchLineServiceKind(GetStoreLineKey(line), applied);
        }

        internal bool IsAppliedWorkbenchLocalLine(Entity line)
        {
            return TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                && snapshot.TimetableApplied
                && string.Equals(snapshot.EffectiveServiceKind, "local", StringComparison.Ordinal);
        }

        internal bool IsAppliedWorkbenchExpressLine(Entity line)
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
            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            NormalizeMergedViewLineKinds(draft.MergedView, lines, null, activeRuntime ?? lines.FirstOrDefault());
            if (string.IsNullOrEmpty(draft.MergedView.turnbackStationId))
            {
                draft.MergedView.turnbackStationId = string.Empty;
            }
            draft.MergedView.direction = string.IsNullOrEmpty(draft.MergedView.direction) ? "up" : draft.MergedView.direction;
            ApplyCurrentTimeWindowDefaults(draft.MergedView);
        }

        private void EnsureMergedViewDefaultsStable(DispatchWorkbenchDraftState draft, List<WorkbenchLineRuntime> lines, WorkbenchLineRuntime activeRuntime)
        {
            if (draft.MergedView == null)
            {
                draft.MergedView = new DispatchWorkbenchMergedView();
            }

            NormalizeMergedViewLineKinds(draft.MergedView, lines, null, activeRuntime ?? lines.FirstOrDefault());
            if (string.IsNullOrEmpty(draft.MergedView.turnbackStationId))
            {
                draft.MergedView.turnbackStationId = string.Empty;
            }
            draft.MergedView.direction = string.IsNullOrEmpty(draft.MergedView.direction) ? "up" : draft.MergedView.direction;
            ApplyCurrentTimeWindowDefaults(draft.MergedView);
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
            return WorkbenchDraftStore.GetKey(lineId);
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
                if (waypoint != Entity.Null
                    && EntityManager.Exists(waypoint)
                    && EntityManager.HasComponent<Connected>(waypoint))
                {
                    Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                    if (connected != Entity.Null && EntityManager.Exists(connected))
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

        private static string CreateBuildingId(Entity building)
        {
            if (building == Entity.Null)
            {
                return string.Empty;
            }

            return "station-building-" + building.Index.ToString();
        }

        internal static string CreateWorkbenchStationId(int order)
        {
            return "station-" + order.ToString();
        }

        internal string ResolveWorkbenchEntityName(Entity entity)
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

        private bool IsLiveWorkbenchEntity(Entity entity)
        {
            return entity != Entity.Null && EntityManager.Exists(entity);
        }

        internal Entity ResolveWorkbenchStopEntity(Entity waypoint)
        {
            if (!IsLiveWorkbenchEntity(waypoint))
            {
                return Entity.Null;
            }

            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (IsLiveWorkbenchEntity(connected))
                {
                    Entity stopEntity = FindOwnedTransportStop(connected);
                    if (stopEntity != Entity.Null)
                    {
                        return stopEntity;
                    }
                }
            }

            Entity waypointStop = FindOwnedTransportStop(waypoint);
            return waypointStop != Entity.Null ? waypointStop : Entity.Null;
        }

        private Entity ResolveWorkbenchBuilding(Entity waypoint)
        {
            if (!IsLiveWorkbenchEntity(waypoint))
            {
                return Entity.Null;
            }

            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (IsLiveWorkbenchEntity(connected))
                {
                    Entity connectedStation = FindTransportStationFromStop(connected);
                    if (connectedStation != Entity.Null)
                    {
                        return connectedStation;
                    }
                }
            }

            Entity waypointStation = FindTransportStationFromStop(waypoint);
            return waypointStation != Entity.Null ? waypointStation : Entity.Null;
        }

        private static bool IsStationAnchorKeyId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith(StationAnchorKeyPrefix, StringComparison.Ordinal);
        }

        private static string CreateStationAnchorKeyValue()
        {
            return StationAnchorKeyPrefix + Guid.NewGuid().ToString("N");
        }

        internal Entity ResolveStationAnchor(Entity waypoint)
        {
            Entity building = ResolveWorkbenchBuilding(waypoint);
            if (building != Entity.Null)
            {
                return building;
            }

            return ResolveWorkbenchStopEntity(waypoint);
        }

        internal Entity ResolveStationAnchorFromStop(Entity stopEntity)
        {
            if (!IsLiveWorkbenchEntity(stopEntity))
            {
                return Entity.Null;
            }

            Entity building = FindTransportStationFromStop(stopEntity);
            if (building != Entity.Null)
            {
                return building;
            }

            return stopEntity;
        }

        internal string GetStationAnchorKey(Entity anchor)
        {
            if (!IsLiveWorkbenchEntity(anchor)
                || !EntityManager.HasComponent<StationAnchorKey>(anchor))
            {
                return string.Empty;
            }

            return EntityManager.GetComponentData<StationAnchorKey>(anchor).Value.ToString();
        }

        internal string EnsureStationAnchorKey(Entity anchor)
        {
            if (!IsLiveWorkbenchEntity(anchor))
            {
                return string.Empty;
            }

            if (EntityManager.HasComponent<StationAnchorKey>(anchor))
            {
                string current = EntityManager.GetComponentData<StationAnchorKey>(anchor).Value.ToString();
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return current;
                }

                string repaired = CreateStationAnchorKeyValue();
                EntityManager.SetComponentData(anchor, new StationAnchorKey
                {
                    Value = repaired
                });
                return repaired;
            }

            string created = CreateStationAnchorKeyValue();
            EntityManager.AddComponentData(anchor, new StationAnchorKey
            {
                Value = created
            });
            return created;
        }

        internal StopRef ResolveStop(Entity waypoint)
        {
            Entity stopEntity = ResolveWorkbenchStopEntity(waypoint);
            if (stopEntity != Entity.Null)
            {
                return new StopRef(stopEntity, ResolvedStopKind.Stop);
            }

            Entity building = ResolveWorkbenchBuilding(waypoint);
            if (building != Entity.Null)
            {
                return new StopRef(building, ResolvedStopKind.Building);
            }

            return new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal StopRef ResolveStop(Entity waypoint, StopRef fallback)
        {
            StopRef resolved = ResolveStop(waypoint);
            if (resolved.Ent != Entity.Null)
            {
                return resolved;
            }

            return IsLiveWorkbenchEntity(fallback.Ent)
                ? fallback
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        private string ResolveStopName(Entity entity, ResolvedStopKind kind)
        {
            if (entity == Entity.Null)
            {
                return string.Empty;
            }

            if (kind == ResolvedStopKind.Building)
            {
                return ResolveWorkbenchEntityName(entity);
            }

            return ResolveWorkbenchStationName(entity);
        }

        private string CreateStopId(Entity entity, ResolvedStopKind kind)
        {
            if (entity == Entity.Null)
            {
                return string.Empty;
            }

            return kind == ResolvedStopKind.Building
                ? CreateBuildingId(entity)
                : CreateWorkbenchOriginStationId(entity);
        }

        internal string ResolveWorkbenchStationName(Entity stopEntity)
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
                if (!IsLiveWorkbenchEntity(current))
                {
                    break;
                }

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

        internal Entity FindTransportStationFromStop(Entity stop)
        {
            Entity current = stop;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (!IsLiveWorkbenchEntity(current))
                {
                    break;
                }

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

        private StopRef GetOpenStop(WorkbenchRealtimeVehicleRecord record)
        {
            if (record == null || record.Trips.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            WorkbenchRealtimeTripRecord trip = record.Trips[record.Trips.Count - 1];
            if (trip == null || trip.Stops.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            WorkbenchRealtimeStopRecord stop = trip.Stops[trip.Stops.Count - 1];
            if (stop == null || !string.IsNullOrEmpty(stop.DepartureTime))
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            return IsLiveWorkbenchEntity(stop.StopEntity)
                ? new StopRef(stop.StopEntity, stop.Kind)
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
        }

        internal StopRef GetLatestStop(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !m_WorkbenchRealtimeVehicles.TryGetValue(vehicle, out WorkbenchRealtimeVehicleRecord record))
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            if (record.Trips.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            WorkbenchRealtimeTripRecord trip = record.Trips[record.Trips.Count - 1];
            if (trip == null || trip.Stops.Count == 0)
            {
                return new StopRef(Entity.Null, ResolvedStopKind.Stop);
            }

            WorkbenchRealtimeStopRecord stop = trip.Stops[trip.Stops.Count - 1];
            return stop != null && IsLiveWorkbenchEntity(stop.StopEntity)
                ? new StopRef(stop.StopEntity, stop.Kind)
                : new StopRef(Entity.Null, ResolvedStopKind.Stop);
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

        internal void RecordWorkbenchRealtimeStopEvent(
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

            StopRef fallbackStop = boarding
                ? new StopRef(Entity.Null, ResolvedStopKind.Stop)
                : GetOpenStop(record);
            StopRef stop = ResolveStop(
                waypoints[stopWaypointIndex].m_Waypoint,
                fallbackStop);
            if (stop.Ent == Entity.Null)
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
                && stopRecord.StopEntity == stop.Ent
                && stopRecord.Kind == stop.Kind
                && ((boarding && string.IsNullOrEmpty(stopRecord.ArrivalTime))
                    || (!boarding && string.IsNullOrEmpty(stopRecord.DepartureTime)));
            if (!canReuseLastStopRecord)
            {
                stopRecord = new WorkbenchRealtimeStopRecord
                {
                    StopEntity = stop.Ent,
                    Kind = stop.Kind
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
                stop.Ent,
                stop.Kind,
                stopWaypointIndex,
                isOriginStop,
                boarding,
                nowTime,
                nowFrame);

            string stopName = ResolveStopName(stop.Ent, stop.Kind);
            if (string.IsNullOrEmpty(stopName))
            {
                stopName = "Stop " + stop.Ent.Index.ToString();
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
                + stop.Ent.Index
                + " wp="
                + stopWaypointIndex
                + " time="
                + nowTime
                + " stopCount="
                + activeTrip.Stops.Count
                + " origin="
                + (isOriginStop ? "1" : "0"));
        }

        internal void BeginWorkbenchRealtimeTripAtLaunch(
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
                Entity buildingEntity = FindTransportStationFromStop(stopEntity);
                if (buildingEntity != Entity.Null && !stationIdByStopEntity.ContainsKey(buildingEntity))
                {
                    stationIdByStopEntity[buildingEntity] = stations[stationIndex].id;
                }
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
                string recordKind = GetEffectiveWorkbenchLineServiceKind(record.Line, null);
                record.Kind = string.Equals(recordKind, "express", StringComparison.Ordinal) ? "express" : "local";

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
                    bool isRunningVehicle = m_VehicleView.TryGetState(vehicle, out VehicleState vehicleState)
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
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows)
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
            string activeRowsPreview = SummarizeStagedRowsByLine(activeLineDraftRows);
            string mergedRowsPreview = SummarizeStagedRowsByLine(combinedDraftRows);
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
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows)
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
                AppendWorkbenchRowSetReport(sb, "activeRows", activeLineDraftRows, runtimeById, provenanceByKey);
                AppendWorkbenchRowSetReport(sb, "combinedRows", combinedDraftRows, runtimeById, provenanceByKey);
                AppendWorkbenchConflictReport(sb, "combinedRows", combinedDraftRows, runtimeById, provenanceByKey);

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
                sb.AppendLine("planRefs=" + SummarizePlanRefs(request));
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

        private static string SummarizePlanRefs(DispatchWorkbenchSaveRequest request)
        {
            if (request?.planRefs != null && request.planRefs.Length > 0)
            {
                return string.Join("|", request.planRefs
                    .Where(entry => entry != null && !string.IsNullOrEmpty(entry.lineId))
                    .Select(entry => (entry.lineId ?? string.Empty)
                        + ":"
                        + (entry.contract?.importedPlanId ?? string.Empty)
                        + ":"
                        + (entry.contract?.importedObjectiveId ?? string.Empty)));
            }

            if (request?.plannerImportContract != null)
            {
                return "legacy:"
                    + (request.plannerImportContract.importedPlanId ?? string.Empty)
                    + ":"
                    + (request.plannerImportContract.importedObjectiveId ?? string.Empty);
            }

            return "-";
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

        private static List<DispatchWorkbenchStagedRowDto> DeduplicateRowsByIdLast(
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

            SyncLineSettingsStore();
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

            HashSet<string> currentLineIds = new HashSet<string>(
                EnumerateWorkbenchLineSettingIds(),
                StringComparer.Ordinal);

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

        private RuntimeFeatureSettingsDto BuildRuntimeFeatureSettingsDto()
        {
            return ToRuntimeFeatureSettingsDto(m_RuntimeFeatureSettingsStore.Get());
        }

        private bool AreRuntimeFeatureSettingsEquivalent(RuntimeFeatureSettingsDto settings)
        {
            if (settings == null)
                return true;

            RuntimeFeatureSettingsState current = m_RuntimeFeatureSettingsStore.Get();
            return current.DispatchEnabled == settings.dispatchEnabled
                && current.BypassEnabled == settings.bypassEnabled
                && current.BroadcastEnabled == settings.broadcastEnabled
                && current.DepotLockEnabled == settings.depotLockEnabled;
        }

        private void ApplyRuntimeFeatureSettings(RuntimeFeatureSettingsDto settings)
        {
            RuntimeFeatureSettingsState previous = m_RuntimeFeatureSettingsStore.Get();
            RuntimeFeatureSettingsState next = ToRuntimeFeatureSettingsState(settings);
            m_RuntimeFeatureSettingsStore.Set(next);

            if (previous.BypassEnabled && !next.BypassEnabled)
            {
                m_Bypass.ClearAll();
            }

            if (previous.BroadcastEnabled && !next.BroadcastEnabled)
            {
                StopBroadcastActivityForFeatureDisable();
            }
        }

        private static RuntimeFeatureSettingsDto ToRuntimeFeatureSettingsDto(RuntimeFeatureSettingsState state)
        {
            RuntimeFeatureSettingsState normalized = state ?? RuntimeFeatureSettingsState.Default();
            return new RuntimeFeatureSettingsDto
            {
                dispatchEnabled = normalized.DispatchEnabled,
                bypassEnabled = normalized.BypassEnabled,
                broadcastEnabled = normalized.BroadcastEnabled,
                depotLockEnabled = normalized.DepotLockEnabled
            };
        }

        private static RuntimeFeatureSettingsState ToRuntimeFeatureSettingsState(RuntimeFeatureSettingsDto settings)
        {
            if (settings == null)
                return RuntimeFeatureSettingsState.Default();

            return new RuntimeFeatureSettingsState
            {
                DispatchEnabled = settings.dispatchEnabled,
                BypassEnabled = settings.bypassEnabled,
                BroadcastEnabled = settings.broadcastEnabled,
                DepotLockEnabled = settings.depotLockEnabled
            };
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

            LineKey storeKey = GetStoreLineKey(lineId);
            if (!storeKey.IsEmpty
                && m_LineSettingsStore.TryGet(storeKey, out LineSettingsState state))
            {
                return state.OriginHoldLimitMinutes;
            }

            return m_WorkbenchLineOriginHoldLimits.TryGetValue(lineId, out int configuredMinutes)
                ? NormalizeOriginHoldLimitMinutes(configuredMinutes)
                : DEFAULT_ORIGIN_HOLD_LIMIT_MINUTES;
        }

        private string GetWorkbenchConfiguredLineServiceKind(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            LineKey storeKey = GetStoreLineKey(lineId);
            if (!storeKey.IsEmpty
                && m_LineSettingsStore.TryGet(storeKey, out LineSettingsState state))
            {
                return NormalizeWorkbenchServiceKind(state.ConfiguredServiceKind);
            }

            return m_WorkbenchLineServiceKinds.TryGetValue(lineId, out string configuredKind)
                ? NormalizeWorkbenchServiceKind(configuredKind)
                : string.Empty;
        }

        private int GetWorkbenchMaxStationDwellMinutes(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return DEFAULT_MAX_STATION_DWELL_MINUTES;

            LineKey storeKey = GetStoreLineKey(lineId);
            if (!storeKey.IsEmpty
                && m_LineSettingsStore.TryGet(storeKey, out LineSettingsState state))
            {
                return state.MaxStationDwellMinutes;
            }

            return m_WorkbenchLineMaxStationDwellMinutes.TryGetValue(lineId, out int configuredMinutes)
                ? NormalizeMaxStationDwellMinutes(configuredMinutes)
                : DEFAULT_MAX_STATION_DWELL_MINUTES;
        }

        private string GetWorkbenchAllowedDepotId(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            LineKey storeKey = GetStoreLineKey(lineId);
            if (!storeKey.IsEmpty
                && m_LineSettingsStore.TryGet(storeKey, out LineSettingsState state))
            {
                return NormalizeWorkbenchAllowedDepotIdFromSnapshot(state.AllowedDepotId);
            }

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
            return GetWorkbenchOriginHoldLimitMinutes(stableLineId);
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
            return GetWorkbenchConfiguredLineServiceKind(stableLineId);
        }

        private int GetWorkbenchMaxStationDwellMinutes(Entity line)
        {
            if (line == Entity.Null)
                return DEFAULT_MAX_STATION_DWELL_MINUTES;

            string stableLineId = TryGetWorkbenchLineFrameSnapshot(line, out WorkbenchLineFrameSnapshot snapshot)
                ? snapshot.LineId
                : GetWorkbenchLineId(line);
            return GetWorkbenchMaxStationDwellMinutes(stableLineId);
        }

        private string GetWorkbenchAllowedDepotId(Entity line)
        {
            if (line == Entity.Null)
                return string.Empty;

            string stableLineId = GetWorkbenchLineId(line);
            return GetWorkbenchAllowedDepotId(stableLineId);
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
            string depotId = GetWorkbenchAllowedDepotId(lineId);
            ulong settingsVersion = GetWorkbenchLineSettingsVersion();

            if (m_ConfiguredAllowedDepotCacheByLine.TryGetValue(line, out ConfiguredAllowedDepotCacheEntry cached)
                && cached.Line == line)
            {
                if (cached.SettingsVersion != settingsVersion)
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
                    settingsVersion);
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
                    settingsVersion);
            }
            else
            {
                m_ConfiguredAllowedDepotCacheByLine.Remove(line);
            }

            return resolvedDepot;
        }

        public ulong GetWorkbenchLineSettingsVersion()
        {
            return Math.Max(m_WorkbenchLineSettingsVersion, m_LineSettingsStore.Version);
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
            bool validateApplyOnlyConstraints,
            List<DispatchWorkbenchDepotDto> depots = null)
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
            Dictionary<string, DispatchWorkbenchDepotDto> depotById = (depots ?? BuildWorkbenchDepots())
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
                        string normalizedDepotId = depots != null
                            ? NormalizeWorkbenchAllowedDepotIdFromSnapshot(setting.allowedDepotId)
                            : NormalizeWorkbenchAllowedDepotId(setting.allowedDepotId);
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
                            int gap = GetForwardMinuteGap(ordered[i - 1].Minutes, ordered[i].Minutes);
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
                        if (ordered.Length > 1 && ordered[0].Minutes != ordered[ordered.Length - 1].Minutes)
                        {
                            int wrapGap = GetForwardMinuteGap(ordered[ordered.Length - 1].Minutes, ordered[0].Minutes);
                            if (wrapGap < minOriginDepartureGapMinutes)
                            {
                                string originLabel = !string.IsNullOrEmpty(ordered[0].OriginName)
                                    ? ordered[0].OriginName
                                    : ordered[0].OriginId;
                                errors.Add(
                                    "Staged rows depart from the same origin station too close together: "
                                    + originLabel
                                    + " "
                                    + SlotStr(ordered[ordered.Length - 1].Minutes)
                                    + " and "
                                    + SlotStr(ordered[0].Minutes)
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
            List<WorkbenchLineRuntime> runtimeLines,
            Dictionary<string, AppliedWorkbenchLineState> appliedLines = null)
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
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in appliedLines ?? m_AppliedWorkbenchLines)
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
                    int gap = GetForwardMinuteGap(ordered[i - 1].Minutes, ordered[i].Minutes);
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
                if (ordered.Length > 1 && ordered[0].Minutes != ordered[ordered.Length - 1].Minutes)
                {
                    int wrapGap = GetForwardMinuteGap(ordered[ordered.Length - 1].Minutes, ordered[0].Minutes);
                    if (wrapGap >= minOriginDepartureGapMinutes)
                        continue;

                    string originLabel = !string.IsNullOrEmpty(ordered[0].OriginName)
                        ? ordered[0].OriginName
                        : ordered[0].OriginId;
                    errors.Add(
                        "Applied timetable would depart from the same origin station too close together: "
                        + originLabel
                        + " "
                        + SlotStr(ordered[ordered.Length - 1].Minutes)
                        + " "
                        + ordered[ordered.Length - 1].LineId
                        + " and "
                        + SlotStr(ordered[0].Minutes)
                        + " "
                        + ordered[0].LineId
                        + ".");
                }
            }

            return errors;
        }

        private static int GetForwardMinuteGap(int previousMinutes, int nextMinutes)
        {
            const int dayMinutes = 24 * 60;
            int previous = ((previousMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            int next = ((nextMinutes % dayMinutes) + dayMinutes) % dayMinutes;
            return next >= previous
                ? next - previous
                : dayMinutes - previous + next;
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

        private void NormalizeRequestedMergedViewFromLineSettings(
            DispatchWorkbenchSaveRequest request,
            List<WorkbenchLineRuntime> runtimeLines,
            Dictionary<string, string> configuredKinds = null)
        {
            if (request?.mergedView == null)
                return;

            WorkbenchLineRuntime fallbackLine = runtimeLines?.FirstOrDefault();
            NormalizeMergedViewLineKinds(
                request.mergedView,
                runtimeLines,
                request.lineSettings,
                fallbackLine,
                configuredKinds);
        }

        private void NormalizeMergedViewLineKinds(
            DispatchWorkbenchMergedView mergedView,
            List<WorkbenchLineRuntime> lines,
            IEnumerable<DispatchWorkbenchLineSettingDto> requestedSettings,
            WorkbenchLineRuntime fallbackLine,
            Dictionary<string, string> configuredKinds = null)
        {
            if (mergedView == null)
                return;

            Dictionary<string, WorkbenchLineRuntime> lineById = new Dictionary<string, WorkbenchLineRuntime>(StringComparer.Ordinal);
            if (lines != null)
            {
                foreach (WorkbenchLineRuntime line in lines)
                {
                    if (line != null && !string.IsNullOrEmpty(line.Id) && !lineById.ContainsKey(line.Id))
                    {
                        lineById[line.Id] = line;
                    }
                }
            }
            Dictionary<string, string> requestedKindById = new Dictionary<string, string>(StringComparer.Ordinal);
            if (requestedSettings != null)
            {
                foreach (DispatchWorkbenchLineSettingDto setting in requestedSettings)
                {
                    if (setting == null || string.IsNullOrEmpty(setting.lineId))
                        continue;

                    string normalizedKind = NormalizeWorkbenchServiceKind(setting.serviceKind);
                    if (!string.IsNullOrEmpty(normalizedKind))
                    {
                        requestedKindById[setting.lineId] = normalizedKind;
                    }
                }
            }

            List<string> mergedLineIds = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in NormalizeLineIdList(mergedView.localLineIds, mergedView.localLineId, lines))
            {
                if (seen.Add(lineId))
                {
                    mergedLineIds.Add(lineId);
                }
            }
            foreach (string lineId in NormalizeLineIdList(mergedView.expressLineIds, mergedView.expressLineId, lines))
            {
                if (seen.Add(lineId))
                {
                    mergedLineIds.Add(lineId);
                }
            }
            if (mergedLineIds.Count == 0 && fallbackLine != null && !string.IsNullOrEmpty(fallbackLine.Id))
            {
                mergedLineIds.Add(fallbackLine.Id);
            }

            List<string> localIds = new List<string>();
            List<string> expressIds = new List<string>();
            foreach (string lineId in mergedLineIds)
            {
                string kind = ResolveMergedViewLineKind(
                    lineId,
                    lineById,
                    requestedKindById,
                    configuredKinds);
                if (string.Equals(kind, "express", StringComparison.Ordinal))
                {
                    expressIds.Add(lineId);
                }
                else
                {
                    localIds.Add(lineId);
                }
            }

            mergedView.localLineIds = localIds.ToArray();
            mergedView.expressLineIds = expressIds.ToArray();
            mergedView.localLineId = localIds.FirstOrDefault() ?? string.Empty;
            mergedView.expressLineId = expressIds.FirstOrDefault() ?? string.Empty;
        }

        private string ResolveMergedViewLineKind(
            string lineId,
            Dictionary<string, WorkbenchLineRuntime> lineById,
            Dictionary<string, string> requestedKindById,
            Dictionary<string, string> configuredKinds = null)
        {
            if (string.IsNullOrEmpty(lineId))
                return "local";

            if (requestedKindById != null
                && requestedKindById.TryGetValue(lineId, out string requestedKind)
                && !string.IsNullOrEmpty(requestedKind))
            {
                return requestedKind;
            }

            if (lineById != null
                && lineById.TryGetValue(lineId, out WorkbenchLineRuntime runtimeLine)
                && string.Equals(runtimeLine.Kind, "express", StringComparison.Ordinal))
            {
                return "express";
            }

            string configuredKind = configuredKinds != null && configuredKinds.TryGetValue(lineId, out string capturedKind)
                ? NormalizeWorkbenchServiceKind(capturedKind)
                : GetWorkbenchConfiguredLineServiceKind(lineId);
            return string.IsNullOrEmpty(configuredKind) ? "local" : configuredKind;
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
