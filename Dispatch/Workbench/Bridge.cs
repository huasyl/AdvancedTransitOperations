using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Colossal.Core;
using Game.Buildings;
using Game.Common;
using Game.UI;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using static RapidTransitMod.Dispatch.Workbench.Rows;
using ObsStops = RapidTransitMod.Dispatch.Observation.Stops;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Bridge
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly DraftStore m_Drafts = new DraftStore();
        private readonly AppliedTimetableStore m_AppliedStore = new AppliedTimetableStore();
        private readonly AppliedTimetableValidator m_Validator = new AppliedTimetableValidator();
        private readonly LineConfigStore m_LineStore = new LineConfigStore();
        private LineConfig m_LineCfg;
        private LineIds m_LineIds;
        private readonly Names m_Names;
        private readonly global::RapidTransitMod.Stops m_Stops;
        private DepotResolver m_Depots;
        private Config m_Config;
        private RoutePlanQuery m_RoutePlans;
        private Catalog m_Catalog;
        private UiPort m_Ui;
        private RunPort m_Run;
        private Host m_Host;
        private Drafts m_DraftPorts;
        private DraftSync m_Sync;
        private Clock m_Clock;
        private Lines m_Lines;
        private RunHooks m_RunHooks;
        private Query m_Query;
        private FullRunTimeQuery m_RunTime;
        private RunChartSectionIndex m_RunChartIndex;
        private readonly Dictionary<string, ulong> m_LineGenerations =
            new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, DispatchWorkbenchMonitorChangedDto> m_MonitorChanges =
            new Dictionary<string, DispatchWorkbenchMonitorChangedDto>(StringComparer.Ordinal);
        private string m_MonitorAverageWaitingLineId = string.Empty;
        private Snapshot m_Snapshot;
        private Persist m_Persist;
        private Commands m_Commands;
        private Saves m_Saves;
        private ObsStops m_ObsStops;
        private Trips m_Trips;
        private Workbench m_Workbench;
        private AppliedTimetable m_Applied;
        private HostState m_HostState;
        private ulong m_Version = 1;
        private string m_LastSnapshotLogKey = string.Empty;
        private static readonly bool EnableIntegrity = true;

        internal Bridge(
            ModRuntimeHostSystem runtime,
            Names names,
            global::RapidTransitMod.Stops stops)
        {
            m_Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            m_Names = names ?? throw new ArgumentNullException(nameof(names));
            m_Stops = stops ?? throw new ArgumentNullException(nameof(stops));
        }

        internal AppliedTimetableStore AppliedStore => m_AppliedStore;
        internal LineConfigStore LineConfigStore => m_LineStore;
        internal LineConfigStore LineStore => m_LineStore;
        internal IReadOnlyDictionary<string, AppliedLine> AppliedLines => Applied().Lines;
        internal DraftStore DraftStore => m_Drafts;
        internal ulong Version => m_Version;

        internal LineIds Ids()
        {
            if (m_LineIds != null)
                return m_LineIds;

            LineAnchorCatalog catalog = m_Runtime.m_LineAnchorCatalog
                ?? throw new InvalidOperationException("LineAnchorCatalog is not ready.");
            return m_LineIds = new LineIds(m_Runtime.EntityManager, catalog);
        }

        internal LineKey StableEntityKey(Entity line, string fallbackLineId)
        {
            return Ids().StableKey(line);
        }

        internal Names NameSvc()
        {
            return m_Names;
        }

        internal global::RapidTransitMod.Stops StopSvc()
        {
            return m_Stops;
        }

        internal Config Config()
        {
            if (m_Config != null)
                return m_Config;

            m_Config = new Config(
                m_AppliedStore,
                m_LineStore,
                m_Validator,
                Ids().Key,
                StableEntityKey,
                Ids().Id,
                RuntimeConfigStoreDefaults.Hold,
                RuntimeConfigStoreDefaults.Dwell,
                SnapshotDepot,
                RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind,
                Rows.Times,
                Time.Parse,
                message => Mod.log.Info(message));
            return m_Config;
        }

        internal RoutePlanQuery RoutePlans()
        {
            return m_RoutePlans
                ?? throw new InvalidOperationException("RoutePlanQuery is not ready.");
        }

        internal void BindRoutePlans(RoutePlanQuery routePlans)
        {
            if (routePlans == null)
                throw new ArgumentNullException(nameof(routePlans));
            if (m_RoutePlans != null && !ReferenceEquals(m_RoutePlans, routePlans))
                throw new InvalidOperationException("RoutePlanQuery is already bound.");
            m_RoutePlans = routePlans;
        }

        internal LineConfig LineCfg()
        {
            if (m_LineCfg != null)
                return m_LineCfg;

            m_LineCfg = new LineConfig(
                m_LineStore,
                Ids().Key,
                StableEntityKey,
                Ids().Id,
                RuntimeConfigStoreDefaults.Hold,
                RuntimeConfigStoreDefaults.Dwell,
                NormDepot,
                RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind,
                Ids().StableKey);
            return m_LineCfg;
        }

        internal DepotResolver Depots()
        {
            if (m_Depots != null)
                return m_Depots;

            m_Depots = new DepotResolver(
                m_Runtime.EntityManager,
                () => m_Runtime.m_SimulationSystem.frameIndex,
                DepotById,
                DepotId,
                Ids().StableId,
                lineId => m_Runtime.m_LineView.DepotId(lineId),
                () => m_Runtime.m_LineView.CfgVersion(),
                message => Mod.log.Info(message));
            return m_Depots;
        }

        internal Catalog Catalog()
        {
            if (m_Catalog != null)
                return m_Catalog;

            m_Catalog = new Catalog(
                m_Runtime.EntityManager,
                () => m_Runtime.m_LineQuery,
                DepotQuery,
                NameSvc().Lookup,
                StopSvc().Stop,
                StopSvc().Anchor,
                StopSvc().Key,
                StopSvc().StationName,
                Ids().StableKey,
                Ids().Id,
                Ids().Type,
                CanonDepot);
            return m_Catalog;
        }

        internal CatalogCache CatalogCache()
        {
            if (m_Runtime.m_WorkbenchCatalogCache != null)
                return m_Runtime.m_WorkbenchCatalogCache;

            m_Runtime.m_WorkbenchCatalogCache = new CatalogCache(
                Catalog(),
                Workbenches.UiEvents.Push,
                Workbenches.UiEvents.Push,
                Workbenches.UiEvents.Push,
                reasons =>
                {
                    InvalidateRunTimeLines(reasons?.Keys);
                    return Run().CleanupConfirmedInvalidatedLines(reasons);
                },
                InvalidateRunTimeModels,
                runtimeLines =>
                {
                    Persist().Load();
                    return Run().CollectRuntimeMissingLineReasons(runtimeLines);
                },
                () => m_Version,
                () => HostState().IsParked,
                () => Snapshot().Build(
                    string.IsNullOrEmpty(HostState().SelectedLineId)
                        ? Drafts().Preferred()
                        : HostState().SelectedLineId,
                    HostState().TransitMode,
                    m_Version,
                    "game-backend"),
                nowFrame =>
                {
                    RunTime().Tick(nowFrame);
                    RunChartIndex().Tick();
                },
                lines => RunTime().SyncPrewarm(lines));
            return m_Runtime.m_WorkbenchCatalogCache;
        }

        internal HostState HostState()
        {
            return m_HostState ?? (m_HostState = new HostState());
        }

        internal ObsStops ObservationStops()
        {
            if (m_ObsStops != null)
                return m_ObsStops;

            m_ObsStops = new ObsStops(
                new StopPort(
                    m_Runtime.m_Obs.Vehicles,
                    entity => entity != Entity.Null && m_Runtime.EntityManager.Exists(entity),
                    StopSvc().Stop,
                    StopSvc().Ref,
                    Live,
                    Clock().Now,
                    () => m_Runtime.m_SimulationSystem.frameIndex,
                    StopSvc().Name,
                    Ids().StableId,
                    line => m_Runtime.m_LineView.Kind(line, null),
                    RecordObservationStop,
                    ModRuntimeHostSystem.IsTripTraceLoggingEnabled,
                    evt => TraceLog.Write(message => Mod.log.Info(message), evt)));
            return m_ObsStops;
        }

        private void RecordObservationStop(
            Entity vehicle,
            Entity line,
            Entity station,
            ResolvedStopKind kind,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            string clockTime,
            uint frame)
        {
            Entity waypoint = Entity.Null;
            if (line != Entity.Null
                && m_Runtime.EntityManager.HasBuffer<Game.Routes.RouteWaypoint>(line))
            {
                DynamicBuffer<Game.Routes.RouteWaypoint> waypoints =
                    m_Runtime.EntityManager.GetBuffer<Game.Routes.RouteWaypoint>(line, true);
                if (waypointIndex >= 0 && waypointIndex < waypoints.Length)
                    waypoint = waypoints[waypointIndex].m_Waypoint;
            }
            m_Runtime.m_Observation.Stop(
                vehicle,
                line,
                waypoint,
                station,
                kind,
                waypointIndex,
                isOrigin,
                arrival,
                clockTime,
                frame);
        }

        internal Trips Trips()
        {
            if (m_Trips != null)
                return m_Trips;

            m_Trips = new Trips(
                new TripPort(
                    () => m_Runtime.m_Observation.ActiveMonitorTrips,
                    () => m_Runtime.m_Observation.MonitorDateSlots,
                    Time.Slot,
                    MonitorDataComplete,
                    () => m_Runtime.m_ObsRecorder?.MonitorDroppedTripCount ?? 0,
                    MonitorPersistenceHealthy,
                    MonitorIssueCode,
                    () => m_Runtime.m_ObsRecorder?.MonitorIssueCount ?? 0));
            return m_Trips;
        }

        private bool MonitorPersistenceHealthy()
        {
            return m_Runtime.m_Observation != null
                && m_Runtime.m_Observation.MonitorPersistenceHealthy;
        }

        private bool MonitorDataComplete()
        {
            return m_Runtime.m_ObsRecorder != null
                && m_Runtime.m_ObsRecorder.MonitorDataComplete;
        }

        private string MonitorIssueCode()
        {
            if (m_Runtime.m_ObsRecorder == null)
                return "monitor-recorder-missing";
            return !string.IsNullOrEmpty(m_Runtime.m_Obs.MonitorIssueCode)
                ? m_Runtime.m_Obs.MonitorIssueCode
                : m_Runtime.m_ObsRecorder.MonitorOverflowReason ?? string.Empty;
        }

        internal string MonitorHeaders(string requestJson)
        {
            DispatchWorkbenchMonitorListRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorListRequestDto>(requestJson);
            ClockSnapshot clock = m_Runtime.m_SimClock.Snapshot;
            return Workbenches.Json.Write(Trips().BuildMonitorHeaders(request, clock));
        }

        internal string MonitorDetail(string requestJson)
        {
            DispatchWorkbenchMonitorDetailRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorDetailRequestDto>(requestJson);
            return Workbenches.Json.Write(Trips().BuildMonitorDetail(request));
        }

        internal string MonitorDetails(string requestJson)
        {
            DispatchWorkbenchMonitorDetailsRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorDetailsRequestDto>(requestJson);
            return Workbenches.Json.Write(Trips().BuildMonitorDetails(request));
        }

        internal Clock Clock()
        {
            return m_Clock ?? (m_Clock = new Clock(Minute));
        }

        internal AppliedTimetable Applied()
        {
            if (m_Applied != null)
                return m_Applied;

            m_Applied = new AppliedTimetable(
                m_Runtime.EntityManager,
                () => m_Runtime.m_CitySystem.City,
                m_Drafts,
                m_AppliedStore,
                Config(),
                AppliedRuntimeLines,
                new AppliedPort(
                    Ids().StableId,
                    RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                    lineId => m_Runtime.m_LineView.Hold(lineId),
                    lineId => m_Runtime.m_LineView.Dwell(lineId),
                    CopyRow,
                    Time.Parse,
                    Time.Slot,
                    Rows.Note,
                    Rows.Times,
                    Save,
                    () => Applied().Save(),
                    Sync().Sync,
                    lineId => m_Runtime.m_Observation.Seed(
                        !string.IsNullOrEmpty(Drafts().Preferred())
                            ? Drafts().Preferred()
                            : lineId),
                    () =>
                    {
                        m_Runtime.m_LineView.Clear();
                        m_Runtime.m_LineView.Dirty();
                    },
                    message => Mod.log.Info(message),
                    Fault,
                    CopyPlan,
                    lineId => m_Drafts.TryGetValue(lineId, out DispatchWorkbenchDraftState draft)
                        ? draft?.PlannerImportContract
                        : null,
                    waypoint => m_Runtime.m_Resolve.Stop(waypoint),
                    lineIds =>
                    {
                        string[] removed = LineCfg().Clear(lineIds);
                        if (removed.Length > 0)
                        {
                            Depots().Clear();
                            m_Runtime.m_LineView.Clear();
                            CatalogCache().MarkDirty();
                        }

                        return removed;
                    },
                    Ids().StableId,
                    Ids().StableKey));
            return m_Applied;
        }

        internal Query Query()
        {
            if (m_Query != null)
                return m_Query;

            m_Query = new Query(
                m_Drafts,
                m_AppliedStore,
                Applied().Lines,
                Lines,
                DepotDtos,
                Stations,
                BuildTrips,
                Drafts().RowsByLine,
                RapidTransitMod.Dispatch.Workbench.Query.CopyLine,
                RapidTransitMod.Dispatch.Workbench.Query.CopyDepot,
                CopyRow,
                CopyPlan,
                Rows.Note,
                Ids().Id,
                Time.Slot,
                Rows.Has);
            return m_Query;
        }

        internal FullRunTimeQuery RunTime()
        {
            if (m_RunTime != null)
                return m_RunTime;

            m_RunTime = new FullRunTimeQuery(
                m_Runtime.EntityManager,
                RoutePlans(),
                m_Runtime.m_Observation,
                ResolveRunChartLine,
                () => m_Runtime.m_SimClock.Snapshot.FramesPerMinute,
                LineGeneration,
                Workbenches.UiEvents.Push,
                Workbenches.UiEvents.Push);
            return m_RunTime;
        }

        internal RunChartSectionIndex RunChartIndex()
        {
            if (m_RunChartIndex != null)
                return m_RunChartIndex;
            m_RunChartIndex = new RunChartSectionIndex(
                m_Runtime.m_TrackModel,
                m_Runtime.EntityManager,
                entity => StopSvc().Key(StopSvc().Anchor(entity)),
                entity =>
                {
                    string rendered = StopSvc().StationRenderedName(entity);
                    return !string.IsNullOrEmpty(rendered)
                        ? rendered
                        : StopSvc().StationName(entity);
                },
                line => Ids().StableId(line));
            return m_RunChartIndex;
        }

        private ulong LineGeneration(string lineId)
        {
            string key = lineId ?? string.Empty;
            return m_LineGenerations.TryGetValue(key, out ulong generation) ? generation : 0UL;
        }

        private void InvalidateRunTimeLines(IEnumerable<string> lineIds)
        {
            string[] ids = (lineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 0)
                RunTime().InvalidateLines(ids);
        }

        private void InvalidateRunTimeModels(IEnumerable<string> lineIds)
        {
            string[] ids = (lineIds ?? Array.Empty<string>())
                .Where(lineId => !string.IsNullOrEmpty(lineId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 0)
                m_RunTime?.InvalidateSources(ids, new[] { "theory" }, "run-time-model-invalidated");
        }

        internal void InvalidateRunTimeClock()
        {
            m_RunTime?.InvalidateSources(
                null,
                new[] { "sliceHistoricalEstimate", "theory" },
                "run-time-clock-changed");
        }

        private void InvalidateAuthoritativeLine(string stableLineId)
        {
            string key = stableLineId ?? string.Empty;
            if (string.IsNullOrEmpty(key))
                return;

            m_LineGenerations[key] = LineGeneration(key) + 1UL;
            RunTime().InvalidateLines(new[] { key });
        }

        internal bool OnAuthoritativeLineInvalidated(
            Entity line,
            string stableLineId,
            string mode,
            string stopSig,
            string trigger,
            bool clearDetails,
            bool publishEvent)
        {
            if (string.IsNullOrEmpty(stableLineId))
                return false;

            InvalidateAuthoritativeLine(stableLineId);
            bool cleared;
            try
            {
                cleared = clearDetails
                    ? Applied().ClearDetails(line)
                    : Applied().InvalidateDetails(line, stopSig);
            }
            catch (Exception ex)
            {
                Fault("Bridge.OnAuthoritativeLineInvalidated", ex);
                return false;
            }

            if (cleared && publishEvent)
            {
                string eventTrigger = string.IsNullOrEmpty(trigger)
                    ? "stop-sig-changed"
                    : trigger;
                Workbenches.UiEvents.Push(new DispatchWorkbenchLineInvalidationEvent
                {
                    mode = mode ?? string.Empty,
                    version = m_Version.ToString(),
                    lineIds = new[] { stableLineId },
                    reasons = new[]
                    {
                        new DispatchWorkbenchCleanupReasonDto
                        {
                            lineId = stableLineId,
                            reason = "backend-applied-cleared;default-restored;trigger=" + eventTrigger
                        }
                    }
                });
            }

            return cleared;
        }

        internal UiPort Ui()
        {
            if (m_Ui != null)
                return m_Ui;

            m_Ui = new UiPort(
                Workbenches.UiEvents.Push,
                Describe,
                Fault,
                action => MainThreadDispatcher.RunOnMainThread(action));
            return m_Ui;
        }

        internal RunPort Run()
        {
            if (m_Run != null)
                return m_Run;

            m_RunHooks = m_RunHooks ?? new RunHooks(
                m_Runtime.m_Features,
                LineCfg,
                Depots,
                m_Runtime.m_LineView,
                Applied,
                () => CatalogCache().MarkDirty(),
                InvalidateDispatchTiming);
            m_Run = m_RunHooks.Port();
            return m_Run;
        }

        private void InvalidateDispatchTiming(IEnumerable<string> lineIds)
        {
            if (lineIds == null)
                return;

            HashSet<string> changed = new HashSet<string>(
                lineIds.Where(lineId => !string.IsNullOrEmpty(lineId)),
                StringComparer.Ordinal);
            if (changed.Count == 0)
                return;

            List<WorkbenchLineRuntime> runtimeLines = Catalog().RuntimeLines();
            for (int i = 0; i < runtimeLines.Count; i++)
            {
                WorkbenchLineRuntime line = runtimeLines[i];
                if (line != null && changed.Contains(line.Id))
                    m_Runtime.m_Observation.InvalidateDispatchTiming(line.Entity);
            }
        }

        internal Host Host()
        {
            if (m_Host != null)
                return m_Host;

            m_Host = new Host(
                m_Runtime.EntityManager,
                () => m_Runtime.m_CitySystem.City,
                Minute,
                message => Mod.log.Info(message),
                Name,
                () => Query().GetLines(),
                line => Query().GetStations(line),
                () => Query().GetDepots(),
                () => m_Version,
                () => NextVersion(),
                m_Runtime.m_Observation.Seed,
                () => Applied().Save(),
                Clear,
                Ui(),
                Run());
            return m_Host;
        }

        internal Drafts Drafts()
        {
            if (m_DraftPorts != null)
                return m_DraftPorts;

            m_DraftPorts = new Drafts(
                m_Drafts,
                LoadPersist,
                Clock(),
                lineId => m_Runtime.m_LineView.Kind(lineId));
            return m_DraftPorts;
        }

        internal DraftSync Sync()
        {
            if (m_Sync != null)
                return m_Sync;

            m_Sync = new DraftSync(
                m_Drafts,
                () => Applied().Lines,
                (lineId, applied) => m_Runtime.m_LineView.Kind(lineId, applied),
                Drafts().New,
                CopyRow,
                SameRowsSoft,
                LoadApplied);
            return m_Sync;
        }

        internal Snapshot Snapshot()
        {
            if (m_Snapshot != null)
                return m_Snapshot;

            m_Snapshot = new Snapshot(
                Query(),
                Drafts(),
                CopyRow,
                line => m_Runtime.m_LineView.Hold(line),
                line => m_Runtime.m_LineView.Dwell(line),
                line => m_Runtime.m_LineView.DepotId(line),
                () => Applied().CleanupDeletedOrReplacedAppliedLines(saveChanges: true),
                () => Applied().ConsumeCleanupInfo(),
                LogSnapshot,
                WriteIntegrity,
                () => m_Runtime.m_Features.Dto());
            return m_Snapshot;
        }

        internal Persist Persist()
        {
            if (m_Persist != null)
                return m_Persist;

            Host host = Host();
            m_Persist = new Persist(
                host,
                Drafts().Store,
                Applied(),
                BuildCompat,
                RestoreCompat,
                () => LineCfg().Keys(),
                lineId => m_Runtime.m_LineView.Hold(lineId),
                lineId => m_Runtime.m_LineView.Dwell(lineId),
                lineId => m_Runtime.m_LineView.DepotId(lineId),
                lineId => m_Runtime.m_LineView.Kind(lineId),
                () => Catalog().LineIds(),
                Clock().Window,
                settings => m_Runtime.m_OverviewFeatureSettingsPersist.MigrateLegacy(settings),
                Drafts().New,
                CopyManual,
                CopyRule,
                CopyRow,
                CopyPlan,
                LastById,
                KeepManual,
                KeepRules,
                KeepRows,
                m_LineStore,
                m_Runtime.m_LineAnchorCatalog);
            return m_Persist;
        }

        internal Commands Commands()
        {
            if (m_Commands != null)
                return m_Commands;

            Host host = Host();
            m_Commands = new Commands(
                host,
                Drafts(),
                Query(),
                Snapshot(),
                Persist(),
                Config().BuildAppliedState,
                m_Validator,
                () => Applied().ConsumeCleanupInfo());
            return m_Commands;
        }

        internal Saves Saves()
        {
            if (m_Saves != null)
                return m_Saves;

            Host host = Host();
            m_Saves = new Saves(
                Commands(),
                Persist(),
                (scope, lineId) => Snapshot().Build(lineId, scope.Mode, host.Version(), "game-backend"),
                host.Version,
                host.Ui.Error,
                host.Ui.Fault,
                host.Ui.Run);
            return m_Saves;
        }

        internal Workbench Root()
        {
            if (m_Workbench != null)
                return m_Workbench;

            m_Workbench = new Workbench(
                Host(),
                Drafts(),
                Sync(),
                Query(),
                Snapshot(),
                Persist(),
                Commands(),
                Saves());
            return m_Workbench;
        }

        internal void Reset()
        {
            m_RunTime?.Clear();
            m_RunChartIndex?.Clear();
            m_MonitorChanges.Clear();
            Root().Reset();
        }

        internal void Clear()
        {
            m_RunTime?.Clear();
            m_RunChartIndex?.Clear();
            m_MonitorChanges.Clear();
            m_Drafts.Clear();
            Applied().Reset();
            LineCfg().Clear();
            m_Runtime.m_Features.Reset();
            m_Runtime.m_LineView.Clear();
            Depots().Clear();
            CatalogCache().Reset();
            m_Runtime.m_WorkbenchCatalogDirty.Reset();
            m_LastSnapshotLogKey = string.Empty;
        }

        internal string Load(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScheduleScope(requestJson, "loadSnapshot");
            return Root().Load(scope);
        }

        internal string Overview(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadOverviewScope(requestJson, "loadOverviewSnapshot");
            return Root().Load(scope);
        }

        internal string Refresh(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScheduleScope(requestJson, "refreshSnapshot");
            string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
            return Root().Refresh(scope, preferredLineId);
        }

        internal string Meta(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScheduleScope(requestJson, "refreshMetadata");
            string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
            return Root().Meta(scope, preferredLineId);
        }

        internal string Save(string requestJson)
        {
            return Root().Save(requestJson);
        }

        internal string SaveScheduleBatch(string requestJson)
        {
            DispatchWorkbenchScheduleBatchRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchScheduleBatchRequestDto>(requestJson);
            DispatchWorkbenchScheduleBatchResultDto result = new DispatchWorkbenchScheduleBatchResultDto
            {
                success = false,
                editorSessionId = request?.editorSessionId ?? string.Empty,
                errors = Array.Empty<string>()
            };
            List<string> errors = new List<string>();
            if (request == null || string.IsNullOrEmpty(request.editorSessionId))
            {
                errors.Add("schedule-batch-editor-session-required");
                result.errors = errors.ToArray();
                return Workbenches.Json.Write(result);
            }

            Dictionary<string, WorkbenchLineRuntime> runtimeLines = Query().GetLines()
                .Where(line => line != null && !string.IsNullOrEmpty(line.Id))
                .GroupBy(line => line.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Dictionary<string, AppliedLine> replacements = new Dictionary<string, AppliedLine>(StringComparer.Ordinal);
            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (DispatchWorkbenchScheduleLineDto block in request.lines ?? Array.Empty<DispatchWorkbenchScheduleLineDto>())
            {
                if (block == null || string.IsNullOrEmpty(block.lineId) || !lineIds.Add(block.lineId))
                {
                    errors.Add("schedule-batch-line-duplicate-or-missing");
                    continue;
                }
                if (!runtimeLines.TryGetValue(block.lineId, out WorkbenchLineRuntime runtimeLine))
                {
                    errors.Add("schedule-batch-line-missing:" + (block.lineId ?? string.Empty));
                    continue;
                }
                if (!TryBuildScheduleLine(request.editorSessionId, block, runtimeLine, out AppliedLine applied, errors))
                    continue;
                replacements[block.lineId] = applied;
            }

            if (request.lines == null || request.lines.Length == 0)
                errors.Add("schedule-batch-lines-required");
            if (errors.Count == 0
                && !Applied().TryApplyScheduleLines(replacements, out string applyError))
                errors.Add("schedule-batch-apply-failed:" + applyError);

            if (errors.Count == 0)
            {
                result.success = true;
                Host().Dirty();
                string selected = request.lines.FirstOrDefault(line => line != null)?.lineId ?? string.Empty;
                result.snapshot = Snapshot().Build(selected, LineIdentityService.GetKey(selected).Mode, m_Version, "game-backend");
            }
            result.errors = errors.ToArray();
            return Workbenches.Json.Write(result);
        }

        private bool TryBuildScheduleLine(
            string editorSessionId,
            DispatchWorkbenchScheduleLineDto block,
            WorkbenchLineRuntime runtimeLine,
            out AppliedLine applied,
            List<string> errors)
        {
            applied = null;
            TransitMode mode = LineIdentityService.GetKey(block.lineId).Mode;
            if (mode == TransitMode.Unknown)
                mode = TransportModeResolver.Resolve(m_Runtime.EntityManager, runtimeLine.Entity);
            string stopSig = block.stopSig ?? string.Empty;
            FullRunTimeResult runtimeResult = null;
            if (!string.IsNullOrEmpty(block.runtimeResultId)
                && !RunTime().TryGetResult(editorSessionId, block.runtimeResultId, out runtimeResult))
            {
                errors.Add("schedule-batch-runtime-result-invalid:" + block.lineId);
                return false;
            }
            if (runtimeResult != null && string.IsNullOrEmpty(stopSig))
                stopSig = runtimeResult.StopSig;
            if (runtimeResult != null
                && (!string.Equals(runtimeResult.LineId, block.lineId, StringComparison.Ordinal)
                    || runtimeResult.Line != runtimeLine.Entity
                    || !string.Equals(runtimeResult.StopSig, stopSig, StringComparison.Ordinal)))
            {
                errors.Add("schedule-batch-runtime-stop-sig-invalid:" + block.lineId);
                return false;
            }
            LifecycleKind lifecycle = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Runtime.EntityManager, runtimeLine.Entity)).Lifecycle;
            if (runtimeResult != null
                && !AllowsRuntimeSource(lifecycle, runtimeResult.Source))
            {
                errors.Add("schedule-batch-runtime-source-invalid:" + block.lineId);
                return false;
            }

            List<DispatchWorkbenchStagedRowDto> rows = new List<DispatchWorkbenchStagedRowDto>();
            int blockErrorStart = errors.Count;
            HashSet<string> rowIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<int> slots = new HashSet<int>();
            foreach (DispatchWorkbenchScheduleRowDto row in block.rows ?? Array.Empty<DispatchWorkbenchScheduleRowDto>())
            {
                if (row == null || string.IsNullOrEmpty(row.rowId) || !rowIds.Add(row.rowId))
                {
                    errors.Add("schedule-batch-row-duplicate-or-missing:" + block.lineId);
                    continue;
                }
                if (row.slotMinute < 0 || row.slotMinute >= 24 * 60 || !slots.Add(row.slotMinute))
                {
                    errors.Add("schedule-batch-slot-invalid:" + block.lineId + ":" + row.rowId);
                    continue;
                }
                DispatchWorkbenchTimedStopDto[] timedStops = CopyBatchStops(row.timedStops, out bool hadNull);
                if (hadNull)
                {
                    errors.Add("schedule-batch-timed-stop-null:" + block.lineId + ":" + row.rowId);
                    continue;
                }
                if (row.truncateFromStopIndex >= 0)
                {
                    if (row.truncateFromStopIndex > timedStops.Length)
                    {
                        errors.Add("schedule-batch-truncate-invalid:" + block.lineId + ":" + row.rowId);
                        continue;
                    }
                    timedStops = timedStops.Take(row.truncateFromStopIndex).ToArray();
                }
                if (timedStops.Length > 0
                    && !ValidateBatchTimedStops(runtimeResult, row.slotMinute, timedStops, block.lineId, row.rowId, errors))
                    continue;
                rows.Add(new DispatchWorkbenchStagedRowDto
                {
                    id = row.rowId,
                    lineId = block.lineId,
                    time = Time.Slot(row.slotMinute),
                    kind = row.kind ?? string.Empty,
                    source = row.source ?? string.Empty,
                    stopSig = stopSig,
                    timedStops = timedStops
                });
            }
            if (errors.Count > blockErrorStart)
                return false;
            string appliedKey = LineIdentityService.GetId(LineIdentityService.GetKey(block.lineId, mode));
            Applied().Lines.TryGetValue(appliedKey, out AppliedLine previous);
            applied = new AppliedLine
            {
                LineEntity = runtimeLine.Entity,
                StopSig = stopSig,
                OriginHoldLimitMinutes = previous?.OriginHoldLimitMinutes
                    ?? RuntimeConfigStoreDefaults.DefaultOriginHoldLimitMinutes,
                MaxStationDwellMinutes = previous?.MaxStationDwellMinutes
                    ?? RuntimeConfigStoreDefaults.DefaultMaxStationDwellMinutes,
                StagedRows = rows
            };
            AppliedTimetableValidationResult validation = m_Validator.Validate(
                LineIdentityService.GetKey(block.lineId, mode),
                Config().BuildAppliedState(block.lineId, applied));
            if (!validation.IsValid)
            {
                errors.AddRange(validation.Errors.Select(error => "schedule-batch:" + error));
                applied = null;
                return false;
            }
            return true;
        }

        private static bool AllowsRuntimeSource(LifecycleKind lifecycle, string source)
        {
            if (lifecycle == LifecycleKind.Road)
                return string.Equals(source, "busHistorical", StringComparison.Ordinal);
            return lifecycle == LifecycleKind.Rail
                && (string.Equals(source, "theory", StringComparison.Ordinal)
                    || string.Equals(source, "monitorAverage", StringComparison.Ordinal));
        }

        private static DispatchWorkbenchTimedStopDto[] CopyBatchStops(
            DispatchWorkbenchTimedStopDto[] source,
            out bool hadNull)
        {
            hadNull = false;
            if (source == null)
                return Array.Empty<DispatchWorkbenchTimedStopDto>();
            DispatchWorkbenchTimedStopDto[] result = new DispatchWorkbenchTimedStopDto[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                DispatchWorkbenchTimedStopDto stop = source[i];
                if (stop == null)
                {
                    hadNull = true;
                    continue;
                }
                result[i] = new DispatchWorkbenchTimedStopDto
                {
                    stopKey = stop.stopKey ?? string.Empty,
                    arrive = stop.arrive,
                    depart = stop.depart
                };
            }
            return result;
        }

        private static bool ValidateBatchTimedStops(
            FullRunTimeResult runtimeResult,
            int slotMinute,
            DispatchWorkbenchTimedStopDto[] stops,
            string lineId,
            string rowId,
            List<string> errors)
        {
            if (runtimeResult == null || stops.Length < 2 || stops.Length > runtimeResult.StopKeys.Length + 1
                || runtimeResult.Segments.Length < stops.Length - 1)
            {
                errors.Add("schedule-batch-runtime-result-required:" + lineId + ":" + rowId);
                return false;
            }
            for (int i = 0; i < stops.Length; i++)
            {
                string expectedStopKey = i == runtimeResult.StopKeys.Length
                    ? runtimeResult.StopKeys[0]
                    : runtimeResult.StopKeys[i];
                if (stops[i] == null || string.IsNullOrEmpty(stops[i].stopKey)
                    || !string.Equals(stops[i].stopKey, expectedStopKey, StringComparison.Ordinal))
                {
                    errors.Add("schedule-batch-stop-order-invalid:" + lineId + ":" + rowId);
                    return false;
                }
                if ((stops[i].arrive.HasValue && stops[i].arrive.Value < 0)
                    || (stops[i].depart.HasValue && stops[i].depart.Value < 0))
                {
                    errors.Add("schedule-batch-negative-time:" + lineId + ":" + rowId);
                    return false;
                }
            }
            if (stops[0].arrive.HasValue || !stops[0].depart.HasValue || stops[0].depart.Value != slotMinute)
            {
                errors.Add("schedule-batch-origin-time-invalid:" + lineId + ":" + rowId);
                return false;
            }
            for (int i = 1; i < stops.Length; i++)
            {
                if (!stops[i].arrive.HasValue)
                {
                    errors.Add("schedule-batch-arrive-required:" + lineId + ":" + rowId);
                    return false;
                }
                RunChartSegment segment = runtimeResult.Segments[i - 1];
                if (!string.Equals(segment.FromStopKey, stops[i - 1].stopKey, StringComparison.Ordinal)
                    || !string.Equals(segment.ToStopKey, stops[i].stopKey, StringComparison.Ordinal))
                {
                    errors.Add("schedule-batch-runtime-segment-invalid:" + lineId + ":" + rowId);
                    return false;
                }
                int expected = stops[i - 1].depart.HasValue
                    ? stops[i - 1].depart.Value + segment.Minutes
                    : -1;
                if (expected < 0 || stops[i].arrive.Value != expected)
                {
                    errors.Add("schedule-batch-arrive-not-from-result:" + lineId + ":" + rowId);
                    return false;
                }
                if (stops[i].depart.HasValue)
                {
                    if (i == stops.Length - 1 || stops[i].depart.Value - stops[i].arrive.Value < 5)
                    {
                        errors.Add("schedule-batch-depart-chain-invalid:" + lineId + ":" + rowId);
                        return false;
                    }
                }
            }
            if (stops[stops.Length - 1].depart.HasValue)
            {
                errors.Add("schedule-batch-last-depart-forbidden:" + lineId + ":" + rowId);
                return false;
            }
            return true;
        }

        internal string SetHostState(string requestJson)
        {
            return HostState().Update(requestJson);
        }

        internal string Start(string requestJson)
        {
            return Root().Start(requestJson);
        }

        internal string Status(string operationId)
        {
            return Root().Status(operationId);
        }

        internal string StartRunTime(string requestJson)
        {
            DispatchWorkbenchRunTimeQueryRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunTimeQueryRequestDto>(requestJson);
            return Workbenches.Json.Write(RunTime().Start(request));
        }

        internal string RunTimeStatus(string requestJson)
        {
            DispatchWorkbenchRunTimeControlDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunTimeControlDto>(requestJson);
            return Workbenches.Json.Write(RunTime().Status(
                request?.editorSessionId ?? string.Empty,
                request?.queryId));
        }

        internal string CancelRunTime(string requestJson)
        {
            DispatchWorkbenchRunTimeControlDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunTimeControlDto>(requestJson);
            return Workbenches.Json.Write(RunTime().Cancel(
                request?.editorSessionId ?? string.Empty,
                request?.queryId));
        }

        internal string LoadMonitorAverageState(string requestJson)
        {
            DispatchWorkbenchMonitorAverageRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorAverageRequestDto>(requestJson);
            string lineId = request?.lineId ?? string.Empty;
            DispatchWorkbenchMonitorAverageStateDto response = new DispatchWorkbenchMonitorAverageStateDto
            {
                lineId = lineId,
                stopSig = request?.stopSig ?? string.Empty
            };
            Entity line = ResolveRunChartLine(lineId);
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line))
            {
                response.error = "monitor-average-line-missing";
                return Workbenches.Json.Write(response);
            }
            if (m_Runtime.m_LineView.TryStopLayout(line, out string currentStopSig, out _))
                response.stopSig = currentStopSig;
            if (m_Runtime.m_Observation.TryMonitorAverageState(line, response.stopSig, out MonitorAverageState state))
            {
                response.ready = state.Ready;
                response.revision = state.Revision;
                response.stopSig = state.StopSig;
            }
            response.success = true;
            return Workbenches.Json.Write(response);
        }

        internal string QueryMonitorAverage(string requestJson)
        {
            DispatchWorkbenchMonitorAverageRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorAverageRequestDto>(requestJson);
            DispatchWorkbenchRunTimeQueryRequestDto query = new DispatchWorkbenchRunTimeQueryRequestDto
            {
                editorSessionId = request?.editorSessionId ?? string.Empty,
                lineId = request?.lineId ?? string.Empty,
                source = "monitorAverage"
            };
            return Workbenches.Json.Write(RunTime().Start(query));
        }

        internal void OnMonitorChanged(MonitorChange change)
        {
            if (!change.Changed
                || !change.MonitorAverageBecameReady
                || change.Line == Entity.Null
                || string.IsNullOrEmpty(m_MonitorAverageWaitingLineId))
                return;
            string lineId = m_Runtime.LineStableId(change.Line);
            if (string.IsNullOrEmpty(lineId)
                || !string.Equals(lineId, m_MonitorAverageWaitingLineId, StringComparison.Ordinal))
                return;
            m_MonitorChanges[lineId] = new DispatchWorkbenchMonitorChangedDto
            {
                lineId = lineId,
                monitorAverageBecameReady = true
            };
            m_MonitorAverageWaitingLineId = string.Empty;
        }

        internal void OnBusSegChanged(Entity line)
        {
            string lineId = Ids().StableId(line);
            if (!string.IsNullOrEmpty(lineId))
                m_RunTime?.RefreshBusHistorical(lineId);
        }

        internal string SetMonitorSubscription(string requestJson)
        {
            DispatchWorkbenchMonitorSubscriptionDto request =
                Workbenches.Json.Read<DispatchWorkbenchMonitorSubscriptionDto>(requestJson);
            m_MonitorAverageWaitingLineId = request?.averageWaitingLineId ?? string.Empty;
            foreach (string key in m_MonitorChanges.Keys.Where(key =>
                !string.Equals(key, m_MonitorAverageWaitingLineId, StringComparison.Ordinal)).ToArray())
            {
                m_MonitorChanges.Remove(key);
            }
            return "{}";
        }

        internal void FlushMonitorChanges()
        {
            if (m_MonitorChanges.Count == 0)
                return;

            DispatchWorkbenchMonitorChangedDto[] pending = m_MonitorChanges.Values.ToArray();
            m_MonitorChanges.Clear();
            foreach (DispatchWorkbenchMonitorChangedDto change in pending)
                Workbenches.UiEvents.Push(change);
        }

        internal string CloseRunTimeEditor(string requestJson)
        {
            DispatchWorkbenchRunTimeEditorDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunTimeEditorDto>(requestJson);
            return Workbenches.Json.Write(RunTime().CloseEditor(
                request?.editorSessionId ?? string.Empty));
        }

        internal string LoadTimetableLineLayout(string requestJson)
        {
            DispatchWorkbenchTimetableLineLayoutRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchTimetableLineLayoutRequestDto>(requestJson);
            string lineId = request?.lineId ?? string.Empty;
            DispatchWorkbenchTimetableLineLayoutDto result = new DispatchWorkbenchTimetableLineLayoutDto
            {
                lineId = lineId,
                mode = string.Empty,
                stopSig = string.Empty,
                stops = Array.Empty<DispatchWorkbenchTimetableLineStopDto>()
            };
            if (string.IsNullOrWhiteSpace(lineId))
            {
                result.error = "timetable-line-layout-line-id-required";
                return Workbenches.Json.Write(result);
            }

            LineKey key = Ids().Key(lineId);
            if (key.IsEmpty
                || m_Runtime.m_LineAnchorCatalog == null
                || !m_Runtime.m_LineAnchorCatalog.TryEntity(key, out Entity line)
                || line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line))
            {
                result.error = "timetable-line-layout-line-missing";
                return Workbenches.Json.Write(result);
            }

            TransportModeProfile profile = TransportModeProfile.GetProfile(
                TransportModeResolver.Resolve(m_Runtime.EntityManager, line));
            result.mode = profile.Token;
            if (!profile.IsSupported || profile.Lifecycle == LifecycleKind.Unknown)
            {
                result.error = "timetable-line-layout-mode-unsupported";
                return Workbenches.Json.Write(result);
            }

            if (!RoutePlans().TryGet(line, profile.Lifecycle, out RoutePlan plan))
            {
                result.error = "timetable-line-layout-route-plan-unavailable";
                return Workbenches.Json.Write(result);
            }

            if (!TryBuildTimetableStops(plan, out DispatchWorkbenchTimetableLineStopDto[] stops))
            {
                result.error = "timetable-line-layout-invalid";
                return Workbenches.Json.Write(result);
            }

            result.success = true;
            result.stopSig = plan.StopSig;
            result.stops = stops;
            return Workbenches.Json.Write(result);
        }

        private bool TryBuildTimetableStops(
            RoutePlan plan,
            out DispatchWorkbenchTimetableLineStopDto[] stops)
        {
            stops = Array.Empty<DispatchWorkbenchTimetableLineStopDto>();
            if (plan == null
                || string.IsNullOrWhiteSpace(plan.StopSig)
                || plan.Waypoints == null
                || plan.Stops == null
                || plan.Stops.Length == 0)
            {
                return false;
            }

            DispatchWorkbenchTimetableLineStopDto[] result =
                new DispatchWorkbenchTimetableLineStopDto[plan.Stops.Length];
            int previousWaypointIndex = -1;
            for (int order = 0; order < plan.Stops.Length; order++)
            {
                RouteStopRef stop = plan.Stops[order];
                if (stop.WaypointIndex <= previousWaypointIndex
                    || stop.WaypointIndex < 0
                    || stop.WaypointIndex >= plan.Waypoints.Length
                    || stop.Waypoint == Entity.Null
                    || stop.Stop == Entity.Null
                    || string.IsNullOrWhiteSpace(stop.StopKey))
                {
                    return false;
                }

                RouteWaypointRef waypoint = plan.Waypoints[stop.WaypointIndex];
                if (waypoint.WaypointIndex != stop.WaypointIndex
                    || waypoint.Waypoint != stop.Waypoint
                    || waypoint.Stop != stop.Stop
                    || !string.Equals(waypoint.StopKey, stop.StopKey, StringComparison.Ordinal))
                {
                    return false;
                }

                result[order] = new DispatchWorkbenchTimetableLineStopDto
                {
                    order = order,
                    stopKey = stop.StopKey,
                    name = StopSvc().StationName(stop.Stop),
                    waypointIndex = stop.WaypointIndex
                };
                previousWaypointIndex = stop.WaypointIndex;
            }

            stops = result;
            return true;
        }

        internal string RunChartSections(string requestJson)
        {
            DispatchWorkbenchRunChartSectionRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunChartSectionRequestDto>(requestJson);
            return Workbenches.Json.Write(RunChartIndex().Query(request));
        }

        internal string RunChartStations(string requestJson)
        {
            DispatchWorkbenchRunChartStationDirectoryRequestDto request =
                Workbenches.Json.Read<DispatchWorkbenchRunChartStationDirectoryRequestDto>(requestJson);
            return Workbenches.Json.Write(RunChartIndex().QueryStations(request));
        }

        internal ulong NextVersion()
        {
            m_Version++;
            return m_Version;
        }

        internal DispatchWorkbenchSnapshot Build(string preferredLineId)
        {
            return Snapshot().Build(preferredLineId, ModeScope.DefaultWorkbench.Mode, m_Version, "game-backend");
        }

        internal bool Restore()
        {
            return Persist().Restore();
        }

        internal void LoadPersist()
        {
            Persist().Load();
            LoadApplied();
        }

        internal void LoadApplied()
        {
            if (!Applied().Loaded)
            {
                Applied().Load();
            }
        }

        internal void Save()
        {
            Persist().Save();
        }

        internal List<WorkbenchLineRuntime> Lines()
        {
            m_Lines = m_Lines ?? new Lines(
                LoadPersist,
                LoadApplied,
                () => CatalogCache().RuntimeLines(),
                (lineId, applied) => m_Runtime.m_LineView.Kind(lineId, applied),
                Ids().Color);
            return m_Lines.All(AppliedLines);
        }

        internal WorkbenchLineRuntime ActiveLine(List<WorkbenchLineRuntime> lines, string preferredLineId)
        {
            return Query().ResolveActiveLine(lines, preferredLineId, Drafts().Preferred());
        }

        internal Entity GetDepot(Entity line)
        {
            return Depots().Get(line);
        }

        internal Entity CanonDepot(Entity depot)
        {
            return Depots().Canon(depot);
        }

        internal string DepotId(Entity depot)
        {
            return Catalog().DepotId(depot);
        }

        internal string RawDepotId(Entity depot)
        {
            return Catalog().RawDepotId(depot);
        }

        internal Entity DepotById(string depotId)
        {
            return Catalog().DepotById(depotId);
        }

        internal string Name(Entity entity)
        {
            return NameSvc().Get(entity);
        }

        internal static string Describe(Exception ex)
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

        private int Minute()
        {
            int nowMin = (int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440;
            return nowMin < 0 ? nowMin + 1440 : nowMin;
        }

        private Entity ResolveRunChartLine(string lineId)
        {
            if (string.IsNullOrEmpty(lineId))
                return Entity.Null;
            List<WorkbenchLineRuntime> lines = Catalog().RuntimeLines();
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && string.Equals(lines[i].Id, lineId, StringComparison.Ordinal))
                    return lines[i].Entity;
            return Entity.Null;
        }

        private List<DispatchWorkbenchTripDto> BuildTrips(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            return Trips().Build(activeRuntime, stations, draft);
        }

        private List<WorkbenchLineRuntime> AppliedRuntimeLines()
        {
            List<WorkbenchLineRuntime> lines = CatalogCache().RuntimeLines();
            for (int i = 0; i < lines.Count; i++)
            {
                WorkbenchLineRuntime line = lines[i];
                line.Id = RapidTransitMod.Dispatch.Workbench.Drafts.Key(line.Id);
            }

            return lines;
        }

        private void BuildCompat(DispatchWorkbenchPersistentState persisted)
        {
            Broadcasting.WorkbenchBackend.Compat.Build(m_Runtime.m_AnnouncementWorkbench, persisted);
        }

        private void RestoreCompat(DispatchWorkbenchPersistentState persisted)
        {
            Broadcasting.WorkbenchBackend.Compat.Restore(m_Runtime.m_AnnouncementWorkbench, persisted);
        }

        private List<DispatchWorkbenchStationDto> Stations(Entity line)
        {
            return CatalogCache().Stations(line);
        }

        private List<DispatchWorkbenchDepotDto> DepotDtos()
        {
            return CatalogCache().Depots();
        }

        private IEnumerable<string> CollectSavedWorkbenchLineIds()
        {
            Persist().Load();

            HashSet<string> lineIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lineId in LineCfg().Keys())
            {
                AddSavedWorkbenchLineId(lineIds, lineId);
            }

            AddSavedWorkbenchLineId(lineIds, m_Drafts.GetPreferredLineId());
            foreach (KeyValuePair<string, DispatchWorkbenchDraftState> entry in m_Drafts)
            {
                AddSavedWorkbenchLineId(lineIds, entry.Key);
                CollectSavedWorkbenchLineIds(lineIds, entry.Value);
            }

            return lineIds;
        }

        private static void CollectSavedWorkbenchLineIds(
            HashSet<string> lineIds,
            DispatchWorkbenchDraftState draft)
        {
            if (lineIds == null || draft == null)
            {
                return;
            }

            AddSavedWorkbenchLineId(lineIds, draft.SelectedLineId);
            AddSavedWorkbenchLineId(lineIds, draft.SelectedEditLine);

            DispatchWorkbenchMergedView mergedView = draft.MergedView;
            if (mergedView != null)
            {
                AddSavedWorkbenchLineId(lineIds, mergedView.localLineId);
                AddSavedWorkbenchLineId(lineIds, mergedView.expressLineId);
                AddSavedWorkbenchLineIds(lineIds, mergedView.localLineIds);
                AddSavedWorkbenchLineIds(lineIds, mergedView.expressLineIds);
            }

            AddSavedWorkbenchLineIds(lineIds, draft.StagedRows?.Select(row => row?.lineId));

            DispatchWorkbenchPlannerImportContractDto contract = draft.PlannerImportContract;
            if (contract == null)
            {
                return;
            }

            AddSavedWorkbenchLineId(lineIds, contract.draftKey);
            AddSavedWorkbenchLineIds(lineIds, contract.importedLineIds);

            DispatchPlannerRequestEchoDto echo = contract.requestEcho;
            if (echo == null)
            {
                return;
            }

            AddSavedWorkbenchLineId(lineIds, echo.draftKey);
            AddSavedWorkbenchLineId(lineIds, echo.expressLineId);
            AddSavedWorkbenchLineId(lineIds, echo.virtualExpressBaseLineId);
            AddSavedWorkbenchLineIds(lineIds, echo.localLineIds);
            AddSavedWorkbenchLineIds(lineIds, echo.adjustableLineIds);
        }

        private static void AddSavedWorkbenchLineIds(
            HashSet<string> lineIds,
            IEnumerable<string> sourceLineIds)
        {
            foreach (string lineId in sourceLineIds ?? Array.Empty<string>())
            {
                AddSavedWorkbenchLineId(lineIds, lineId);
            }
        }

        private static void AddSavedWorkbenchLineId(
            HashSet<string> lineIds,
            string lineId)
        {
            if (lineIds == null)
            {
                return;
            }

            string normalized = DraftStore.GetKey(lineId);
            if (string.IsNullOrEmpty(normalized)
                || string.Equals(normalized, "__default__", StringComparison.Ordinal)
                || string.Equals(normalized, "local", StringComparison.Ordinal)
                || string.Equals(normalized, "express", StringComparison.Ordinal))
            {
                return;
            }

            lineIds.Add(normalized);
        }

        private void LogSnapshot(
            WorkbenchLineRuntime activeRuntime,
            List<DispatchWorkbenchStationDto> stations,
            List<DispatchWorkbenchTripDto> trips,
            DispatchWorkbenchDraftState draft,
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows)
        {
            if (!RtLog.VerboseEnabled)
                return;

            Report.Snapshot(
                ref m_LastSnapshotLogKey,
                activeRuntime,
                stations,
                trips,
                draft,
                activeLineDraftRows,
                combinedDraftRows,
                message => Mod.log.Info(message));
        }

        private void WriteIntegrity(
            string reason,
            WorkbenchLineRuntime activeRuntime,
            string draftKey,
            DispatchWorkbenchDraftState activeDraft,
            List<WorkbenchLineRuntime> runtimeLines,
            List<DispatchWorkbenchStagedRowDto> activeLineDraftRows,
            List<DispatchWorkbenchStagedRowDto> combinedDraftRows)
        {
            if (!RtLog.VerboseEnabled)
                return;

            Report.Integrity(
                EnableIntegrity,
                reason,
                activeRuntime,
                draftKey,
                activeDraft,
                runtimeLines,
                activeLineDraftRows,
                combinedDraftRows,
                m_Drafts,
                AppliedLines,
                m_Runtime.m_SimulationSystem != null ? (int)m_Runtime.m_SimulationSystem.frameIndex : 0,
                Rows.Ids,
                Time.Parse,
                message => Mod.log.Info(message));
        }

        private string NormDepot(string depotId)
        {
            return Depots().NormId(depotId);
        }

        private static string SnapshotDepot(string depotId)
        {
            return string.IsNullOrWhiteSpace(depotId) ? string.Empty : depotId;
        }

        private EntityQuery DepotQuery()
        {
            return m_Runtime.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<Deleted>());
        }

        private void Fault(string scope, Exception ex)
        {
            if (ex == null)
                return;

            m_Runtime.log.Info("[WorkbenchException] " + scope + " -> " + Describe(ex));
        }

        private bool Live(Entity entity)
        {
            return entity != Entity.Null && m_Runtime.EntityManager.Exists(entity);
        }

    }
}
