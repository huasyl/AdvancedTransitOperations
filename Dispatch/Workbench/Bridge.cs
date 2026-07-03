using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Colossal.Core;
using Game.Buildings;
using Game.Common;
using Game.UI;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using static RapidTransitMod.Dispatch.Workbench.Rows;
using ObsStops = RapidTransitMod.Dispatch.Observation.Stops;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class Bridge
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly DraftStore m_Drafts = new DraftStore();
        private readonly AppliedTimetableStore m_AppliedStore = new AppliedTimetableStore();
        private readonly AppliedTimetableValidator m_Validator = new AppliedTimetableValidator();
        private readonly LineConfigStore m_LineStore = new LineConfigStore();
        private LineConfig m_LineCfg;
        private LineIds m_LineIds;
        private Names m_Names;
        private global::RapidTransitMod.Stops m_Stops;
        private DepotResolver m_Depots;
        private Config m_Config;
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

        internal Bridge(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        internal AppliedTimetableStore AppliedStore => m_AppliedStore;
        internal IReadOnlyDictionary<string, AppliedLine> AppliedLines => Applied().Lines;
        internal DraftStore DraftStore => m_Drafts;
        internal ulong Version => m_Version;

        internal LineIds Ids()
        {
            return m_LineIds ?? (m_LineIds = new LineIds(m_Runtime.EntityManager));
        }

        internal Names NameSvc()
        {
            return m_Names ?? (m_Names = new Names(m_Runtime.m_NameSystem));
        }

        internal global::RapidTransitMod.Stops StopSvc()
        {
            return m_Stops ?? (m_Stops = new global::RapidTransitMod.Stops(
                m_Runtime.EntityManager,
                NameSvc(),
                Live));
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
                Ids().Key,
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

        internal LineConfig LineCfg()
        {
            if (m_LineCfg != null)
                return m_LineCfg;

            m_LineCfg = new LineConfig(
                m_LineStore,
                Ids().Key,
                Ids().Key,
                Ids().Id,
                RuntimeConfigStoreDefaults.Hold,
                RuntimeConfigStoreDefaults.Dwell,
                NormDepot,
                RuntimeConfigStoreDefaults.NormalizeConfiguredServiceKind);
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
                Ids().Get,
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
                Ids().Key,
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
                () => m_Version,
                () => HostState().IsParked,
                () => Snapshot().Build(
                    string.IsNullOrEmpty(HostState().SelectedLineId)
                        ? Drafts().Preferred()
                        : HostState().SelectedLineId,
                    HostState().TransitMode,
                    m_Version,
                    "game-backend"));
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
                    Ids().Get,
                    line => m_Runtime.m_LineView.Kind(line, null),
                    m_Runtime.m_Observation.Stop,
                    DispatchRuntimeSystem.IsTripTraceLoggingEnabled,
                    evt => TraceLog.Write(message => Mod.log.Info(message), evt)));
            return m_ObsStops;
        }

        internal Trips Trips()
        {
            if (m_Trips != null)
                return m_Trips;

            m_Trips = new Trips(
                new TripPort(
                    m_Runtime.EntityManager,
                    m_Runtime.m_Obs.Vehicles,
                    StopSvc().Stop,
                    StopSvc().Station,
                    Ids().Get,
                    line => m_Runtime.m_LineView.Kind(line, null),
                    Time.Parse,
                    Clock().Now,
                    m_Runtime.m_RouteProgress.Try,
                    (Entity vehicle, out VehicleState state) => m_Runtime.m_VehicleView.TryGetState(vehicle, out state)));
            return m_Trips;
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
                    Ids().Get,
                    RapidTransitMod.Dispatch.Workbench.Drafts.Key,
                    lineId => m_Runtime.m_LineView.Hold(lineId),
                    lineId => m_Runtime.m_LineView.Dwell(lineId),
                    CopyRow,
                    Time.Parse,
                    Time.Slot,
                    Rows.Note,
                    Rows.Times,
                    Save,
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
                    waypoint => m_Runtime.m_Resolve.Stop(waypoint)));
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
                () => CatalogCache().MarkDirty());
            m_Run = m_RunHooks.Port();
            return m_Run;
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
                KeepRows);
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
                Persist());
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
            Root().Reset();
        }

        internal void Clear()
        {
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
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "loadSnapshot");
            return Root().Load(scope);
        }

        internal string Refresh(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "refreshSnapshot");
            string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
            return Root().Refresh(scope, preferredLineId);
        }

        internal string Meta(string requestJson)
        {
            ModeScope scope = Workbenches.ModeRequest.ReadScope(requestJson, "refreshMetadata");
            string preferredLineId = scope.NormalizeLineId(Workbenches.ModeRequest.ReadPreferredLine(requestJson));
            return Root().Meta(scope, preferredLineId);
        }

        internal string Save(string requestJson)
        {
            return Root().Save(requestJson);
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
