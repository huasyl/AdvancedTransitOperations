// fix: IsSlotExpired 无法覆盖大幅过期班次，Holding 车卡死；调度器未过滤过期槽；Running->Idle 未清 targetMinute；新线路不产车；UI 标签残留；关闭线路无效；始发站压队
// - IsSlotExpired 上限由 SLOT_INTERVAL_MINUTES(30) 改为 SPAWN_LEAD_MINUTES + SLOT_GRACE_MINUTES(64)，覆盖所有真过期场景
// - Holding 过期处理细分：overdue > SLOT_INTERVAL_MINUTES 直接回库，否则释放槽等重新分配
// - 调度器槽扫描入口加 IsSlotExpired 检查，过期槽直接跳过不参与分配
// - Running->Idle 时清理旧的 targetMinute，防止旧槽值残留导致下帧直接 Idle->Holding 绕过调度保护
// - PuppetMaster：D=0 时改用 iDefault 兜底；每帧清理 m_SpawningLines 中已不存在的线路 Entity 记录
// - 注册新车时清理 m_UICache，防止旧线路缓存导致 SetUILabel 去重跳过、UI 标签不刷新
// - m_LineQuery 加 Disabled 过滤，关闭线路后调度器停止处理，现有车跑完当前圈自然 Idle 超时回库
// - 新增末端入站标签：车进入最后一个 waypoint 时打标签，Idle->Holding 前检查本线路有无标签车距始发站 <= 350 米，有则回库疏解

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Creatures;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.SceneFlow;
using Game.Serialization;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using RapidTransitMod.Dispatch.Scheduling;
using Game.UI.InGame;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Diagnostics;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.Core;
using RapidTransitMod.Planner;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using WorkbenchTime = RapidTransitMod.Dispatch.Workbench.Time;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace RapidTransitMod
{
    // ============================================================
    //  车辆状态枚举
    // ============================================================
    public enum VehicleState
    {
        /// <summary>车辆已产出，正在前往始发站。</summary>
        Preparing = 1,
        /// <summary>在始发站等待发车时刻。</summary>
        Holding = 2,
        /// <summary>已发车，线路运行中。</summary>
        Running = 3,
        /// <summary>跑完一圈回到始发站，等待调度分配。</summary>
        Idle = 4,
        /// <summary>已发出回库指令，等待原生系统处理。</summary>
        Retiring = 5,
    }

    public partial class ModRuntimeHostSystem : GameSystemBase, IPreSerialize
    {
        private static readonly ProfilerMarker s_RailEtaMarker = new ProfilerMarker("ATO.Runtime.RailEta");
        private static readonly ProfilerMarker s_SourceMarker = new ProfilerMarker("ATO.Runtime.Source");
        private static readonly ProfilerMarker s_RegisterMarker = new ProfilerMarker("ATO.Runtime.Register");
        private static readonly ProfilerMarker s_StopMarker = new ProfilerMarker("ATO.Runtime.Stop");
        private static readonly ProfilerMarker s_BypassMarker = new ProfilerMarker("ATO.Runtime.Bypass");
        private static readonly ProfilerMarker s_DispatchMarker = new ProfilerMarker("ATO.Runtime.Dispatch");
        private static readonly ProfilerMarker s_SchedulerMarker = new ProfilerMarker("ATO.Runtime.Scheduler");
        private static readonly ProfilerMarker s_SliceMarker = new ProfilerMarker("ATO.Runtime.Slice");

        private readonly struct RescueCandidate
        {
            public readonly Entity Express;
            public readonly Entity Local;
            public readonly Entity Line;

            public RescueCandidate(Entity express, Entity local, Entity line)
            {
                Express = express;
                Local = local;
                Line = line;
            }
        }

        private static readonly Comparison<RescueCandidate> s_RescueCandidateComparison = CompareRescueCandidates;

        internal const float LOCAL_BYPASS_EXIT_RELEASE_ATOMS = 3f;
        internal CameraUpdateSystem m_CameraUpdateSystem;
        internal Broadcasting.WorkbenchBackend.Workbench m_AnnouncementWorkbench;
        internal Broadcasting.Runtime m_Announcements;
        internal TrackModelService m_TrackModel = null!;
        internal TrackProjectionService m_TrackProjection = null!;
        internal LineStructureInvalidator m_LineStructureInvalidator = null!;
        internal RuntimeFacade m_Bypass = null!;
        internal SharedCorridorSupport m_SharedCorridor = null!;
        internal CatalogCache m_WorkbenchCatalogCache = null!;
        internal CatalogDirty m_WorkbenchCatalogDirty = null!;
        internal LineAnchorCatalog m_LineAnchorCatalog = null!;

        internal IReadOnlyDictionary<string, AppliedLine> AppliedLines => m_WorkbenchBridge.AppliedLines;

        internal Dispatch.AppliedTimetable Applied()
            => m_WorkbenchBridge.Applied();

        internal Drafts DraftStore()
            => m_WorkbenchBridge.Drafts();

        internal float VehicleMaintenanceRange(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.HasComponent<PrefabRef>(vehicle))
            {
                return 0f;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            return prefab != Entity.Null && EntityManager.HasComponent<PublicTransportVehicleData>(prefab)
                ? EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange
                : 0f;
        }

        internal RapidTransitMod.Dispatch.Workbench.Trips Trips()
            => m_WorkbenchBridge.Trips();

        internal TrackModelService TrackModel => m_TrackModel;
        internal TrackProjectionService TrackProjection => m_TrackProjection;
        internal RuntimeFacade Bypass => m_Bypass;

        internal List<WorkbenchLineRuntime> Lines()
            => m_WorkbenchBridge.Lines();

        internal WorkbenchLineRuntime ActiveLine(List<WorkbenchLineRuntime> lines, string preferredLineId)
            => m_WorkbenchBridge.ActiveLine(lines, preferredLineId);

        internal void LoadWorkbench()
            => m_WorkbenchBridge.LoadPersist();

        internal void LoadApplied()
            => m_WorkbenchBridge.LoadApplied();

        internal void SaveWorkbench()
            => m_WorkbenchBridge.Save();

        internal string LineStableId(Entity line)
            => m_WorkbenchBridge.Ids().StableId(line);

        internal string EntityName(Entity entity)
            => m_WorkbenchBridge.Name(entity);

        internal bool IsVehicleBoarding(Entity vehicle)
        {
            return vehicle != Entity.Null
                && EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & Game.Vehicles.PublicTransportFlags.Boarding) != 0;
        }

        public Entity GetDepot(Entity line)
            => m_WorkbenchBridge.GetDepot(line);

        public Entity CanonDepot(Entity depot)
            => m_WorkbenchBridge.CanonDepot(depot);

        internal string DepotId(Entity depot)
            => m_WorkbenchBridge.DepotId(depot);

        internal string GetKind(string lineId) => m_LineView.Kind(lineId);
        internal string GetKind(Entity line) => m_LineView.Kind(line);
        internal int GetHold(string lineId) => m_LineView.Hold(lineId);
        internal int GetHold(Entity line) => m_LineView.Hold(line);
        internal int GetDwell(string lineId) => m_LineView.Dwell(lineId);
        internal int GetDwell(Entity line) => m_LineView.Dwell(line);
        internal string GetDepotId(string lineId) => m_LineView.DepotId(lineId);
        internal string GetDepotId(Entity line) => m_LineView.DepotId(line);

        internal static string DescribeError(Exception ex)
            => RapidTransitMod.Dispatch.Workbench.Bridge.Describe(ex);

        public static ModRuntimeHostSystem Instance = null!;
        internal TimedLogger log = Mod.log;
        internal SimulationSystem m_SimulationSystem = null!;
        internal TimeSystem m_TimeSystem = null!;
        internal SimClock m_SimClock = null!;
        internal NameSystem m_NameSystem = null!;
        internal EndFrameBarrier m_EndFrameBarrier = null!;

        // ── 车辆状态 ──
        internal VehicleStateStore m_VehicleStateStore = null!;
        internal FrameEvents m_FrameEvents = null!;
        internal RailEventSource m_RailEventSource = null!;
        internal RoadEventSource m_RoadEventSource = null!;
        internal RuntimeFramePlan m_RuntimeFramePlan = null!;
        internal VehicleWorksets m_VehicleWorksets = null!;
        internal StopRuntimeState m_StopRuntimeState = null!;
        internal StopRuntime m_StopRuntime = null!;
        private readonly List<StopInput> m_StopInputs = new List<StopInput>();
        private readonly List<DispatchInput> m_DispatchInputs = new List<DispatchInput>();
        private readonly Dictionary<Entity, uint> m_LifecycleResolveLogFrames = new Dictionary<Entity, uint>();
        private Func<Entity, bool> m_HasOpenStopSession = null!;
        private Func<Entity, bool> m_HasInvalidatedRecovery = null!;
        private Func<Entity, bool> m_IsDeparturePending = null!;
        private Func<Entity, uint, bool> m_IsForcedMidStopGraceActive = null!;
        private readonly Dictionary<Entity, BypassControlResult> m_BypassControls = new Dictionary<Entity, BypassControlResult>();
        private readonly HashSet<Entity> m_BypassAttempts = new HashSet<Entity>();
        private readonly List<RescueCandidate> m_RescueCandidates = new List<RescueCandidate>();
        private readonly HashSet<Entity> m_RescueLocalVehicles = new HashSet<Entity>();
        private readonly HashSet<Entity> m_BypassReleaseConsumers = new HashSet<Entity>();
        private readonly Dictionary<Entity, FrameLabelAction> m_FrameLabelActions = new Dictionary<Entity, FrameLabelAction>();
        private readonly List<Entity> m_FrameLabelOrder = new List<Entity>();
        internal VehicleRegistry m_VehicleRegistry = null!;
        internal VehicleView m_VehicleView = null!;
        internal LineView m_LineView = null!;
        internal FeatureGate m_Features = null!;
        internal RapidTransitMod.Overview.FeatureSettingsPersist m_OverviewFeatureSettingsPersist = null!;
        internal DispatchEngine m_RuntimeEngine = null!;
        internal LineSpawnControl m_LineSpawnControl = null!;
        internal RuntimeVehicleCleanup m_RuntimeVehicleCleanup = null!;
        internal SchedulerApply m_SchedulerApply = null!;
        internal VehicleRegistrar m_VehicleRegistrar = null!;
        internal RuntimeVehicleLabels m_VehicleLabels = null!;
        internal RuntimeResolve m_Resolve = null!;
        internal SelectPort m_SelectPort = null!;
        internal SelectPanel m_SelectPanel = null!;
        internal VehicleStationContextQuery m_StationContextQuery = null!;
        internal StationAnchorDiagnostics m_StationAnchorDiagnostics = null!;
        internal RapidTransitMod.Overview.FeatureSettingsOperations m_OverviewFeatureSettingsOperations = null!;
        internal RapidTransitMod.Dispatch.Workbench.Bridge m_WorkbenchBridge = null!;
        internal PlannerApi m_PlannerApi = null!;
        internal PlannerPort m_PlannerPort = null!;
        internal PlannerExport m_PlannerExport = null!;
        internal PlannerJobs m_PlannerJobs = null!;
        internal DispatchCache m_DispatchCache = null!;
        internal LapCache m_LapCache = null!;
        internal VehicleCache m_VehicleCache = null!;
        internal RuntimeCache m_RuntimeCache = null!;
        internal MileageStore m_MileageStore = null!;
        internal BypassStore m_BypassStore = null!;
        internal LapStore m_Laps = null!;
        internal DispatchCommandApplier m_CommandApplier = null!;
        internal DispatchScheduler m_DispatchScheduler = null!;
        internal SpawnLeadTheory m_SpawnLeadTheory = null!;
        internal DwellStore m_Dwell = null!;
        internal SliceStore m_Slices = null!;
        internal SliceAdmission m_SliceAdmission = null!;
        internal TraceStore m_Obs = null!;
        internal Recorder m_ObsRecorder = null!;
        internal Capture m_ObsCapture = null!;
        internal ObservationPort m_Observation = null!;
        internal Buffers m_ObsBuffers = null!;
        internal LineRange m_LineRange = null!;
        internal LineProfile m_LineProfile = null!;
        internal RuntimeLog m_RuntimeLog = null!;
        internal SpawnIntentTrace m_SpawnIntentTrace = null!;
        internal RuntimeHotPathProbe m_RuntimeHotPathProbe = null!;
        internal RuntimeLifecycleHost m_RuntimeLifecycleHost = null!;
        internal RailEtaHost.RailEtaBridgeService m_RailEtaService = null!;
        internal RailEtaHost.RailEtaHotRuntime m_RailEtaHotRuntime = null!;
        internal LineTimes m_LineTimes = null!;
        internal LineMileage m_LineMileage = null!;
        internal LineVehicles m_LineVehicles = null!;
        internal RouteProgress m_RouteProgress = null!;
        internal WaypointIndex m_WaypointIndex = null!;
        internal RapidTransitMod.Dispatch.Observation.Query m_ObsQuery = null!;
        internal RapidTransitMod.Dispatch.Observation.Persist m_ObsPersist = null!;
        internal NativeHashMap<Entity, FixedString64Bytes> m_UICache;
        internal NativeHashMap<Entity, byte> m_BoardingFirstFrameGuardState;
        internal NativeHashMap<Entity, int> m_CachedWpIdx;
        /// <summary>
        /// 已进入最后一个 waypoint 的车辆集合。
        /// Idle 转 Holding 前检查本线路是否有此标签的车距始发站 350 米内，有则回库。
        /// </summary>
        /// <summary>
        /// 发车冷却：发车后屏蔽 boarding 变化检测的截止帧。
        /// 防止车辆物理上尚未离开始发站时原生系统触发的假进站 / 假 BV 误写。
        /// </summary>
        internal NativeHashMap<Entity, uint> m_PreparingFixCooldownUntil;
        private const bool ENABLE_TRACK_WAYPOINT_ANCHORING = true;
        internal static bool IsTraversalSliceObservationPersistenceEnabled() => true;
        internal static bool IsDwellObservationPersistenceEnabled() => false;
        internal static bool IsStationDwellObservationPersistenceEnabled() => true;
        internal static bool IsBusSegObservationPersistenceEnabled() => true;
        internal ulong m_PerfProbeOriginSettleCalls;
        internal ulong m_PerfProbeOriginSettleFastPathHits;
        internal ulong m_PerfProbeOriginSettleSlowPathEntered;
        internal ulong m_PerfProbeOriginSettlePreSnapshotMisses;
        internal ulong m_PerfProbeOriginSettleWindowHits;
        // ── 线路状态 ──
        internal NativeHashMap<Entity, int> m_SpawningLines;
        internal NativeHashMap<Entity, uint> m_LineSpawnRequestFrame;
        internal NativeHashMap<Entity, uint> m_LastSpawnBlockedLogFrame;
        internal NativeHashMap<ulong, uint> m_LastScheduleDiagnosticLogFrame;
        internal NativeHashSet<Entity> m_LineInitialAdopted;
        // ── 圈时持久化缓存 ──
        internal CitySystem m_CitySystem = null!;
        /// <summary>Buffer 已挂到 City 实体，避免每帧重复调用 HasBuffer。</summary>
        internal bool m_LapCacheBufferReady = false;
        internal bool m_TraversalSliceObservationBufferReady = false;
        internal bool m_TraversalSliceObservationCacheLoaded = false;
        internal bool m_DwellObservationBufferReady = false;
        internal bool m_DwellObservationCacheLoaded = false;
        internal bool m_StationDwellObservationBufferReady = false;
        internal bool m_StationDwellObservationCacheLoaded = false;
        internal bool m_BusSegObservationBufferReady = false;
        internal bool m_BusSegObservationCacheLoaded = false;
        internal int m_LastStationStopDwellLegacyBufferCount = 0;
        internal int m_LastStationStopDwellLegacyRestoredCount = 0;
        internal int m_LastStationStopDwellAnchorBufferCount = 0;
        internal int m_LastStationStopDwellAnchorRestoredCount = 0;
        internal uint m_StationAnchorObservationDiagLastLogFrame = 0;
        internal ulong m_StationAnchorDiagAcceptedSamples = 0;
        internal ulong m_StationAnchorDiagLegacyWritten = 0;
        internal ulong m_StationAnchorDiagAnchorWritten = 0;
        internal ulong m_StationAnchorDiagAnchorMissing = 0;
        internal ulong m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
        internal ulong m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
        internal ulong m_StationAnchorDiagSuspiciousLongDwell = 0;
        internal ulong m_StationAnchorDiagTotalAnchorMissing = 0;
        internal ulong m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal = 0;
        internal ulong m_StationAnchorDiagTotalSuspiciousOriginOrTerminal = 0;
        internal ulong m_StationAnchorDiagTotalSuspiciousLongDwell = 0;
        internal bool m_VehicleCacheBufferReady = false;
        internal bool m_DispatchCacheBufferReady = false;
        internal bool m_BypassStationBufferReady = false;
        internal bool m_LineMileageBufferReady = false;

        // ── 帧级保护 ──
        internal NativeHashSet<Entity> m_JustLaunched;

        internal bool IsRuntimeReadyForOriginArrivingRepair()
        {
            return m_SystemReady;
        }

        internal bool TryGetRuntimeVehicleState(Entity vehicle, out VehicleState state)
        {
            state = default;
            return m_VehicleStateStore.State.IsCreated && m_VehicleView.TryGetState(vehicle, out state);
        }

        // ── 启动稳定检测（仅启动阶段执行一次，通过后永久关闭）──
        internal bool m_SystemReady = false;
        internal bool m_StartupRuntimeStateCleared = false;
        internal int m_StableFrameCount = 0;
        internal int m_LastVehicleCount = -1;
        internal int m_LastPuppetMasterMinute = -1;
        internal int m_LastRegisterSweepMinute = -1;
        internal int m_LastSchedulerTickMinute = -1;
        internal const int STABLE_FRAMES_REQUIRED = 5;

        // ── 定期写缓存 ──
        internal uint m_LastVehicleCacheFlushFrame = 0;
        internal const uint VEHICLE_CACHE_FLUSH_INTERVAL = 300;

        internal EntityQuery m_AllPublicTransportQuery;
        internal EntityQuery m_LineQuery;
        internal const int SLOT_INTERVAL_MINUTES = 30;
        internal const int SPAWN_LEAD_MINUTES = 60;
        internal const float MAINTENANCE_THRESHOLD = 0.9f;
        internal const int IDLE_TIMEOUT_MINUTES = 2;
        internal const double SIM_FRAMES_PER_MINUTE = 182.044;
        internal const float EARLY_STOP_DWELL_CLOSE_MAX_MINUTES = 3f;
        internal const float AT_STOP_MAX_DIST = 300f;
        /// <summary>班次宽限分钟数：发车窗口和过期判断共用同一阈值。</summary>
        internal const int SLOT_GRACE_MINUTES = 4;
        /// <summary>发车后冷却帧数：屏蔽 boarding 变化检测，防假进站</summary>
        internal const uint LAUNCH_COOLDOWN_FRAMES = 600;
        internal const uint FORCED_MIDSTOP_BV_GRACE_FRAMES = 180;
        internal const uint OFFICIAL_BOARDING_CLOSE_TIMEOUT_FRAMES = 1800;
        internal const uint SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES = 1800;
        internal const uint SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES = 1800;
        internal const uint RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES = 180;
        internal const uint ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES = 1800;
        internal const uint PREPARINGFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES = 120;
        private const uint LIFECYCLE_RESOLVE_LOG_COOLDOWN_FRAMES = 1800;
        private const uint BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES = 60;
        internal const float BOARDING_CLOSE_BYPASS_MIN_WAITING_DISTANCE_SENTINEL = -1f;
        private const uint BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES = 60;
        private const uint BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES = 3600;
        internal const float DISPATCH_ESTIMATE_MIN_FRAMES = 364.088f;
        internal const float DISPATCH_ESTIMATE_DEFAULT_FRAMES = 1092.264f;
        internal const float DISPATCH_ESTIMATE_MAX_FRAMES = 3640.88f;
        internal const float DISPATCH_FALLBACK_SPEED_METERS_PER_MINUTE = 450f;
        internal const float DISPATCH_FALLBACK_FRAMES_PER_METER =
            (float)SIM_FRAMES_PER_MINUTE / DISPATCH_FALLBACK_SPEED_METERS_PER_MINUTE;
        internal const float PROFILE_STOP_START_BUFFER_MINUTES = 3f;
        internal const float ORIGIN_CONGESTION_RADIUS_METERS = 450f;
        internal const float ORIGIN_FORCE_IDLE_RADIUS_METERS = 180f;
        internal const float ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS = 0.92f;
        private const uint ORIGIN_FORCE_IDLE_SETTLE_FRAMES = 180;
        private const uint DIRECTION_COMPARE_PROBE_LOG_INTERVAL_FRAMES = 3600;
        internal const uint STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES = 3600;
        private const uint DIRECTION_COMPARE_LOG_COOLDOWN_FRAMES = 1800;
        private const int TURNBACK_REPEAT_MIN_PRIMARY_ATOMS = 3;
        private const int TURNBACK_REPEAT_MIN_UNIQUE_LANES = 2;
        private const int TURNBACK_ADJACENT_SEGMENT_MAX_EDGE_SKIP = 2;
        internal static bool IsBypassRuntimeLoggingEnabled() => RtLog.VerboseEnabled;
        internal static bool IsLineOrderedRuntimeLoggingEnabled() => RtLog.VerboseEnabled;
        internal static bool IsTripTraceLoggingEnabled() => false;
        internal static bool IsDepartureObserveLoggingEnabled() => false;
        private static bool IsTrackModelTurnbackBuildLoggingEnabled() => RtLog.VerboseEnabled;
        private const uint PERF_PROBE_SCENE_EXPRESS_LINE_RECENT_WINDOW_FRAMES = 30;
        internal const uint RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES = 30;
        internal const int RETIRE_SHADOW_HISTORY_LIMIT = 4;
        internal const uint RETIRE_HANDOFF_MAX_AGE_FRAMES = 546;
        private const float ORIGIN_ARRIVAL_HOLD_MINUTES = 2f;
        internal const double FORCED_ORIGIN_MIN_DWELL_MINUTES = 3d;
        internal const double PREPARING_ORIGIN_SETTLE_MINUTES = 2d;
        internal const float SPAWN_TRIGGER_BUFFER_SHORT_FRAMES = 1820.44f;
        internal const float SPAWN_TRIGGER_BUFFER_LONG_FRAMES = 2730.66f;
        internal const float SPAWN_TRIGGER_BUFFER_THRESHOLD_FRAMES = 3640.88f;
        private const uint TRAVERSAL_SLICE_SAMPLE_INTERVAL_MEDIUM_FRAMES = 20;
        private const uint TRAVERSAL_SLICE_SAMPLE_INTERVAL_LOW_FRAMES = 60;
        private const float TRAVERSAL_SLICE_SAMPLE_HIGH_THRESHOLD = 0.03f;
        private const float TRAVERSAL_SLICE_SAMPLE_MEDIUM_THRESHOLD = 0.05f;
        internal const int YIELD_PROTECT_MINUTES = 5;
        internal const int LATE_DISPATCH_WINDOW_MINUTES = 8;
        internal const float ETA_SCALE_MIN = 0.5f;
        internal const float ETA_SCALE_MAX = 2.0f;
        internal const uint NEW_LINE_STABLE_FRAMES = 300;
        private const int DISPATCH_SAMPLE_HISTORY_LIMIT = 8;
        private const float DISPATCH_SAMPLE_OUTLIER_FACTOR = 1.5f;
        private const float DISPATCH_FAST_SAMPLE_MARGIN = 0.98f;
        private const float DISPATCH_SLOW_SAMPLE_BLEND = 0.5f;
        private const float DISPATCH_SLOW_SAMPLE_MAX_STEP_MINUTES = 4f;
        private const uint BYPASS_YIELD_DECISION_COOLDOWN_FRAMES = 30;
        internal const uint PREPARING_ROUTE_FIX_GRACE_FRAMES = 300;
        internal const bool ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS = false;

        // ============================================================
        //  生命周期
        // ============================================================

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();

            m_AllPublicTransportQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Vehicles.PublicTransport>());

            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] {
                    ComponentType.ReadWrite<TransportLine>(),
                    ComponentType.ReadOnly<RouteWaypoint>()
                },
                None = new ComponentType[] {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Disabled>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            RuntimeRoot.Build(this);
            m_HasOpenStopSession = m_StopRuntime.HasOpenStopSession;
            m_HasInvalidatedRecovery = m_StopRuntime.HasInvalidatedRecovery;
            m_IsDeparturePending = m_StopRuntime.IsDeparturePending;
            m_IsForcedMidStopGraceActive = m_StopRuntime.IsForcedMidStopGraceActive;

            log.Info("=== RapidTransit v41.1 [VehicleCache] 启动 ===");
        }

        internal bool IsBypassStationSetting(Entity entity)
        {
            Entity building = m_Resolve.PassingStation(entity);
            if (building == Entity.Null)
                return false;

            m_BypassStore.Ensure();
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BypassStationSettingElement>(city))
                return false;

            DynamicBuffer<BypassStationSettingElement> buf = EntityManager.GetBuffer<BypassStationSettingElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_BuildingEntity == building)
                    return buf[i].m_IsBypassStation != 0;
            }

            return false;
        }

        protected override void OnUpdate()
        {
            uint simulationFrame = m_SimulationSystem.frameIndex;
            RuntimeCostFrame runtimeCost = m_RuntimeHotPathProbe.BeginCost(
                simulationFrame,
                m_SystemReady,
                m_SimClock.Snapshot.ToFramesCeil(30d));
            using (s_RailEtaMarker.Auto())
            {
                m_SimClock.RefreshIfDue(simulationFrame);
                Dependency = m_RailEtaService?.TickHot(simulationFrame, Dependency) ?? Dependency;
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.RailEta);
            if (GameManager.instance.gameMode != GameMode.Game) return;
            m_SelectPanel.UpdateVersionBucket();

#if RT_DEBUG_TOOLS
            if (Input.GetKey(KeyCode.LeftControl)
                && Input.GetKey(KeyCode.LeftAlt)
                && Input.GetKey(KeyCode.X))
            {
                m_RuntimeLifecycleHost.ClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                m_RuntimeLifecycleHost.SpawnTest();
                return;
            }

            if (m_Bypass.ToggleKey(Input.GetKey(KeyCode.F5)))
                return;

            if (Input.GetKeyDown(KeyCode.F6))
            {
                m_RuntimeLifecycleHost.ClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                m_CommandApplier.ForceRetireOne();
                return;
            }
#endif

            bool startupActivation = false;
            if (!m_SystemReady)
            {
                if (!m_VehicleRegistrar.StartupGateActive)
                {
                    if (!m_StartupRuntimeStateCleared)
                    {
                        m_RuntimeLifecycleHost.ClearTracking();
                        m_StartupRuntimeStateCleared = true;
                    }

                    BufferLookup<RouteVehicle> routeVehicles = GetBufferLookup<RouteVehicle>(true);
                    NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
                    int totalVehicles = 0;
                    foreach (Entity line in lines)
                    {
                        if (routeVehicles.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> vehicles))
                            totalVehicles += vehicles.Length;
                    }

                    lines.Dispose();
                    if (totalVehicles != m_LastVehicleCount)
                    {
                        m_LastVehicleCount = totalVehicles;
                        m_StableFrameCount = 0;
                        return;
                    }

                    m_StableFrameCount++;
                    if (m_StableFrameCount < STABLE_FRAMES_REQUIRED)
                        return;

                    m_VehicleRegistrar.BeginStartupGate();
                    log.Info("[启动] 稳定检测通过，进入静默接管(车辆数=" + totalVehicles + ")");
                }

                if (!m_VehicleRegistrar.IsStartupActivationFrame(simulationFrame))
                {
                    m_VehicleCache.Ensure();
                    m_VehicleRegistrar.TickStartupGate();
                    return;
                }

                startupActivation = true;
            }

            m_FrameEvents.BeginFrame();
            m_RailEventSource.BeginFrame();
            m_RoadEventSource.BeginFrame();
            bool railSourceFrame = false;
            m_RuntimeFramePlan.BeginFrame();
            ClearFrameBuffers();
            m_SchedulerApply.BeginFrame();
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            ClockSnapshot clockSnapshot = m_SimClock.Snapshot;
            int nowMinute = clockSnapshot.NowMinute;

            m_LapCache.Ensure();
            m_VehicleCache.Ensure();
            m_DispatchCache.Ensure();
            if (IsStationDwellObservationPersistenceEnabled())
            {
                m_ObsBuffers.EnsureStationDwell();
                m_RuntimeCache.LoadStationDwell();
            }

            if (IsTraversalSliceObservationPersistenceEnabled())
            {
                m_ObsBuffers.EnsureSlice();
                m_RuntimeCache.LoadSlice();
            }

            if (IsBusSegObservationPersistenceEnabled())
            {
                m_ObsBuffers.EnsureBusSeg();
                m_RuntimeCache.LoadBusSeg();
            }

            m_LineStructureInvalidator.Drain();

            if (startupActivation)
            {
                bool activated = m_VehicleRegistrar.TryActivateStartup(simulationFrame);
                if (!activated)
                {
                    return;
                }

                m_SystemReady = true;
                m_LastRegisterSweepMinute = nowMinute;
                log.Info("[启动] 静默接管完成，系统就绪");
            }

            if (!startupActivation)
            {
                m_CommandApplier.ReconcileRetireDispatchLocksOnReady();
                m_RuntimeFramePlan.DrainUiCommands();
                ApplyUiCommands(commandBuffer, clockSnapshot);
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Setup);

            if (!startupActivation)
            {
                using (s_SourceMarker.Auto())
                {
                    m_RailEventSource.CollectIfDue(simulationFrame);
                    m_RoadEventSource.Collect(simulationFrame);
                }
                m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.SourceCollect);

                bool runFullRegisterSweep = nowMinute != m_LastRegisterSweepMinute;
                bool registerSourceFrame = m_RailEventSource.CollectedThisFrame(simulationFrame);
                bool scanSpawnLines = (simulationFrame & 15u) == 4u;
                if (runFullRegisterSweep || registerSourceFrame || scanSpawnLines)
                {
                    try
                    {
                        using (s_RegisterMarker.Auto())
                            m_VehicleRegistrar.Register(runFullRegisterSweep, scanSpawnLines);
                        if (runFullRegisterSweep)
                            m_LastRegisterSweepMinute = nowMinute;
                    }
                    catch (Exception ex)
                    {
                        log.Info("[运行异常] VehicleRegistrar -> " + ex.GetType().Name + ": " + ex.Message);
                        throw;
                    }
                }
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Register);

            railSourceFrame = m_RailEventSource.CollectedThisFrame(simulationFrame);
            m_LineStructureInvalidator.Drain();
            if (!startupActivation)
                DrainDisabledLineLateSpawnRetireQueue();
            bool fullMinuteSweep = nowMinute != m_LastSchedulerTickMinute;
            if (fullMinuteSweep)
                m_SchedulerApply.MarkAllDirty();
            m_RuntimeFramePlan.CollectDueDeadlines(simulationFrame);
            m_RuntimeHotPathProbe.CountDueDeadlines(m_RuntimeFramePlan.DueDeadlines.Count);
            RouteDueDeadlines();
            m_RailEventSource.CompileSourceRows(m_RuntimeFramePlan, simulationFrame);
            m_RoadEventSource.CompileSourceRows(m_RuntimeFramePlan, simulationFrame);
            if (startupActivation)
            {
                ApplyStartupActivationState(m_RuntimeFramePlan.Entries, simulationFrame);
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.SourceRoute);
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Stop);
            IReadOnlyList<FramePlanEntry> stopEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Stop);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Stop, stopEntries.Count);
            m_StopInputs.Clear();
            for (int i = 0; i < stopEntries.Count; i++)
            {
                FramePlanEntry entry = stopEntries[i];
                if (!TryResolveEntryLifecycle(entry.Vehicle, simulationFrame, out LifecycleKind lifecycle))
                    continue;

                StopInput input = default;
                bool built = lifecycle == LifecycleKind.Rail
                    ? m_RailEventSource.TryBuildStopInput(
                        m_RuntimeFramePlan,
                        entry,
                        simulationFrame,
                        m_HasOpenStopSession,
                        m_HasInvalidatedRecovery,
                        m_IsDeparturePending,
                        m_IsForcedMidStopGraceActive,
                        out input)
                    : m_RoadEventSource.TryBuildStopInput(
                        m_RuntimeFramePlan,
                        entry,
                        simulationFrame,
                        m_HasOpenStopSession,
                        m_HasInvalidatedRecovery,
                        m_IsDeparturePending,
                        m_IsForcedMidStopGraceActive,
                        out input);
                if (built)
                    m_StopInputs.Add(input);
            }
            if (RuntimeHotPathProbe.Enabled())
                m_RuntimeHotPathProbe.CountStageExecuted(RuntimeStageMask.Stop, CountValidStopInputs());
            using (s_StopMarker.Auto())
                m_StopRuntime.Process(m_StopInputs, simulationFrame);
            PublishStopFacts();
            ApplyStopControls();
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Stop);
            m_TrackProjection.ClearLineRunningVehicleSnapshots();
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Rescue);
            IReadOnlyList<FramePlanEntry> rescueEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Rescue);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Rescue, rescueEntries.Count);
            ResolveVanillaBlockerRescues(commandBuffer, rescueEntries);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Rescue);
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Bypass);
            IReadOnlyList<FramePlanEntry> bypassEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Bypass);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Bypass, bypassEntries.Count);
            using (s_BypassMarker.Auto())
                RunBypassPhase(commandBuffer, m_BypassControls, m_BypassAttempts, simulationFrame, bypassEntries);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.BypassDecision);
            m_StopRuntime.ResolveDwell(m_BypassControls, m_BypassAttempts, simulationFrame);
            CommitDwellTimeouts(commandBuffer);
            m_StopRuntime.ResolveDeparture(m_BypassControls, simulationFrame);
            CommitStopDepartures();
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.DwellDeparture);
            if (fullMinuteSweep)
                AddMinuteDispatchStages();
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Dispatch);
            IReadOnlyList<FramePlanEntry> dispatchEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Dispatch);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Dispatch, dispatchEntries.Count);
            m_DispatchInputs.Clear();
            for (int i = 0; i < dispatchEntries.Count; i++)
            {
                FramePlanEntry entry = dispatchEntries[i];
                if (!TryResolveEntryLifecycle(entry.Vehicle, simulationFrame, out LifecycleKind lifecycle))
                    continue;

                DispatchInput input = default;
                bool built = lifecycle == LifecycleKind.Rail
                    ? m_RailEventSource.TryBuildDispatchInput(
                        entry,
                        simulationFrame,
                        m_StopRuntime.FrameStates,
                        m_BypassControls,
                        out input)
                    : m_RoadEventSource.TryBuildDispatchInput(
                        entry,
                        simulationFrame,
                        m_StopRuntime.FrameStates,
                        m_BypassControls,
                        out input);
                if (built)
                    m_DispatchInputs.Add(input);
            }
            if (RuntimeHotPathProbe.Enabled())
                m_RuntimeHotPathProbe.CountStageExecuted(RuntimeStageMask.Dispatch, CountValidDispatchInputs());
            ApplyPreparingRepairs(m_DispatchInputs, commandBuffer, simulationFrame);
            ApplyRoadOriginStops(m_DispatchInputs, commandBuffer);

            try
            {
                using (s_DispatchMarker.Auto())
                {
                    m_RuntimeEngine.ProcessFrame(
                        commandBuffer,
                        clockSnapshot,
                        m_RuntimeFramePlan,
                        m_FrameEvents,
                        m_DispatchInputs);
                }
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] DispatchEngine.ProcessFrame -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            ApplyWaypointCommits();
            ApplyLaunchCommits();
            ApplyRunningCommits();
            m_CommandApplier.FinalizeRetireDispatchLockTerminals();
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Dispatch);
            if ((simulationFrame & 15u) == 3u)
                m_RuntimeVehicleCleanup.Tick();
            TickLineSpawnControl(nowMinute);
            if (fullMinuteSweep)
                m_SchedulerApply.MarkAllDirty();
            m_SchedulerApply.SealDirtyLines();
            if (!fullMinuteSweep)
                m_RuntimeHotPathProbe.CountSchedulerExternalDirty(m_SchedulerApply.ResolvedDirtyLines.Count);
            using (s_SchedulerMarker.Auto())
            {
                m_SchedulerApply.Tick(
                    commandBuffer,
                    clockSnapshot,
                    m_SchedulerApply.ResolvedDirtyLines,
                    fullMinuteSweep);
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Scheduler);
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Retire);
            IReadOnlyList<FramePlanEntry> retireEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Retire);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Retire, retireEntries.Count);
            m_CommandApplier.TickRetireHandoffStages(
                simulationFrame,
                retireEntries);
            m_RailEventSource.BeginSliceBufferEpoch();
            m_RuntimeFramePlan.Freeze(RuntimeStageMask.Slice);
            IReadOnlyList<FramePlanEntry> sliceEntries = m_RuntimeFramePlan.ForStage(RuntimeStageMask.Slice);
            m_RuntimeHotPathProbe.CountStagePlan(RuntimeStageMask.Slice, sliceEntries.Count);
            using (s_SliceMarker.Auto())
                RunSlicePhase(simulationFrame, sliceEntries);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.RetireSlice);
            PublishPreparingNotices(simulationFrame);
            PublishRunningNotices();
            ClearRunningExitBypass();
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Notices);
            ConsumeFrameEvents();
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Events);
            m_Announcements.Tick(simulationFrame, railSourceFrame);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.Announcements);

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (nowFrame - m_LastVehicleCacheFlushFrame >= VEHICLE_CACHE_FLUSH_INTERVAL)
            {
                m_VehicleCache.Save();
                m_LastVehicleCacheFlushFrame = nowFrame;
            }
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.VehicleCache);

            m_WorkbenchCatalogDirty.Check(nowFrame);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.CatalogCheck);
            m_WorkbenchCatalogCache.Tick(nowFrame);
            m_RuntimeHotPathProbe.MarkCost(ref runtimeCost, RuntimeCostPhase.CatalogTick);

            m_RuntimeHotPathProbe.FinishCost(ref runtimeCost, new RuntimeCostContext
            {
                SourceFrame = railSourceFrame,
                FullMinuteSweep = fullMinuteSweep,
                Stop = stopEntries.Count,
                Rescue = rescueEntries.Count,
                Bypass = bypassEntries.Count,
                Dispatch = dispatchEntries.Count,
                Retire = retireEntries.Count,
                Slice = sliceEntries.Count,
                DirtyLines = m_SchedulerApply.ResolvedDirtyLines.Count
            });

            m_Bypass.FlushProbeLogs(nowFrame);
            m_RuntimeHotPathProbe.FlushIfDue(nowFrame);
        }

        private void ApplyUiCommands(EntityCommandBuffer commandBuffer, ClockSnapshot clockSnapshot)
        {
            bool hadCommands = m_RuntimeFramePlan.UiCommands.Count > 0;
            for (int i = 0; i < m_RuntimeFramePlan.UiCommands.Count; i++)
            {
                UiCommand command = m_RuntimeFramePlan.UiCommands[i];
                switch (command.Kind)
                {
                    case UiCommandKind.Retire:
                        m_RuntimeFramePlan.AddStage(command.Entity, RuntimeStageMask.Retire);
                        ApplyRetireCommand(new RetireCommand(command.Entity));
                        break;
                    case UiCommandKind.Recheck:
                        ApplyRecheckCommand(new RecheckCommand(command.Entity));
                        break;
                    case UiCommandKind.Depart:
                        if (CanVehicleBypass(command.Entity))
                            m_RuntimeFramePlan.AddStage(command.Entity, RuntimeStageMask.Bypass);
                        ApplyDepartCommand(new DepartCommand(command.Entity), commandBuffer);
                        break;
                    case UiCommandKind.Spawn:
                        ApplySpawnCommand(new SpawnCommand(command.Entity), clockSnapshot);
                        break;
                }
            }

            if (hadCommands)
                m_SelectPanel.Invalidate();
        }

        private void RouteDueDeadlines()
        {
            IReadOnlyList<DeadlineEntry> due = m_RuntimeFramePlan.DueDeadlines;
            for (int i = 0; i < due.Count; i++)
            {
                DeadlineEntry entry = due[i];
                switch (entry.Kind)
                {
                    case DeadlineKind.Dwell:
                        m_StopRuntime.QueueDwellTimeout(entry.Vehicle);
                        m_RuntimeFramePlan.AddStage(
                            entry.Vehicle,
                            CanVehicleBypass(entry.Vehicle)
                                ? RuntimeStageMask.Stop | RuntimeStageMask.Bypass
                                : RuntimeStageMask.Stop);
                        break;
                    case DeadlineKind.ForcedMidStopBoardingGrace:
                        m_StopRuntime.QueueDwellTimeout(entry.Vehicle);
                        m_RuntimeFramePlan.AddStage(
                            entry.Vehicle,
                            CanVehicleBypass(entry.Vehicle)
                                ? RuntimeStageMask.Stop | RuntimeStageMask.Bypass
                                : RuntimeStageMask.Stop);
                        break;
                    case DeadlineKind.RetireBoundary:
                    case DeadlineKind.RetireHardAck:
                        m_RuntimeFramePlan.AddStage(entry.Vehicle, RuntimeStageMask.Retire);
                        break;
                    case DeadlineKind.RescueProbe:
                    case DeadlineKind.RescueStall:
                    case DeadlineKind.RescueRecheck:
                        m_RuntimeFramePlan.AddStage(entry.Vehicle, RuntimeStageMask.Rescue);
                        break;
                    default:
                        m_RuntimeFramePlan.AddStage(entry.Vehicle, RuntimeStageMask.Dispatch);
                        break;
                }
            }
        }

        private bool CanVehicleBypass(Entity vehicle)
        {
            return m_VehicleView.TryGetLine(vehicle, out Entity line) && CanLineBypass(line);
        }

        private bool CanLineBypass(Entity line)
        {
            return line != Entity.Null
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(EntityManager, line)).CanBypass;
        }

        private bool TryResolveEntryLifecycle(
            Entity vehicle,
            uint frame,
            out LifecycleKind lifecycle)
        {
            if (RuntimePorts.TryResolveVehicleLifecycle(this, vehicle, out lifecycle))
                return true;

            lifecycle = LifecycleKind.Unknown;
            if (m_LifecycleResolveLogFrames.TryGetValue(vehicle, out uint lastFrame)
                && frame - lastFrame < LIFECYCLE_RESOLVE_LOG_COOLDOWN_FRAMES)
            {
                return false;
            }

            m_LifecycleResolveLogFrames[vehicle] = frame;
            log.Info("[Runtime] 车辆" + vehicle.Index + " 生命周期解析失败，跳过来源输入");
            return false;
        }

        private void CommitSourceWaypoint(Entity vehicle, int waypoint)
        {
            if (!RuntimePorts.TryResolveVehicleLifecycle(this, vehicle, out LifecycleKind lifecycle))
                return;

            if (lifecycle == LifecycleKind.Rail)
                m_RailEventSource.CommitWaypoint(vehicle, waypoint);
            else if (lifecycle == LifecycleKind.Road)
                m_RoadEventSource.CommitWaypoint(vehicle, waypoint);
        }

        private void AddMinuteDispatchStages()
        {
            AddMinuteDispatchStages(VehicleState.Preparing);
            AddMinuteDispatchStages(VehicleState.Holding);
            AddMinuteDispatchStages(VehicleState.Idle);
        }

        private void AddMinuteDispatchStages(VehicleState state)
        {
            foreach (Entity vehicle in m_VehicleWorksets.State(state))
                m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        internal void PublishStopFact(StopFact fact)
        {
            if (!fact.Exists)
                return;

            m_FrameEvents.AppendStop(fact, fact.Frame);
            bool canBypass = fact.Line != Entity.Null
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(EntityManager, fact.Line)).CanBypass;
            if (canBypass)
                m_Bypass.HandleStopFact(fact);
            if (fact.Kind == StopFactKind.Departed
                || fact.Kind == StopFactKind.DwellTimedOut
                || fact.Kind == StopFactKind.StopAssistActive
                || fact.Kind == StopFactKind.BoardingCloseRequested)
                m_RuntimeFramePlan.AddStage(fact.Vehicle, RuntimeStageMask.Dispatch);
            else if ((fact.Kind == StopFactKind.Opened || fact.Kind == StopFactKind.Recovered || fact.Kind == StopFactKind.Restored || fact.Kind == StopFactKind.BoardingEnded)
                && m_VehicleView.TryGetState(fact.Vehicle, out VehicleState state)
                && state == VehicleState.Running)
            {
                RuntimeStageMask stages = RuntimeStageMask.Dispatch;
                if (fact.Kind == StopFactKind.Restored)
                    stages |= RuntimeStageMask.Stop;
                if (canBypass && fact.WaypointIndex > 0)
                    stages |= RuntimeStageMask.Bypass;
                m_RuntimeFramePlan.AddStage(fact.Vehicle, stages);
            }
        }

        private void PublishStopFacts()
        {
            IReadOnlyList<StopFact> facts = m_StopRuntime.Facts;
            for (int i = 0; i < facts.Count; i++)
                PublishStopFact(facts[i]);
        }

        private void ApplyStopControls()
        {
            IReadOnlyList<StopControlResult> controls = m_StopRuntime.Controls;
            for (int i = 0; i < controls.Count; i++)
            {
                StopControlResult control = controls[i];
                ApplyStopControl(control.Vehicle, control.WaypointIndex, control);
            }
        }

        private void CommitDwellTimeouts(EntityCommandBuffer commandBuffer)
        {
            IReadOnlyList<StopDwellTimeout> timeouts = m_StopRuntime.ResolvedDwellTimeouts;
            uint nowFrame = m_SimulationSystem.frameIndex;
            for (int i = 0; i < timeouts.Count; i++)
            {
                StopDwellTimeout timeout = timeouts[i];
                StopControlResult control = timeout.Control;
                m_CommandApplier.ForceDepart(control.Vehicle, nowFrame, commandBuffer);
                ApplyStopControl(control.Vehicle, control.WaypointIndex, control);
                if (!timeout.Fact.Exists)
                    continue;

                PublishStopFact(timeout.Fact);
                PublishStopFact(new StopFact(
                    StopFactKind.StopAssistActive,
                    timeout.Fact.Vehicle,
                    timeout.Fact.Line,
                    timeout.Fact.WaypointIndex,
                    nowFrame,
                    reason: "midstop-dwell-timeout"));
            }
        }

        private void CommitStopDepartures()
        {
            IReadOnlyList<StopDeparture> departures = m_StopRuntime.ResolvedDepartures;
            for (int i = 0; i < departures.Count; i++)
            {
                StopDeparture departure = departures[i];
                StopFact fact = departure.Fact;
                if (fact.Line == Entity.Null
                    || !EntityManager.Exists(fact.Line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(fact.Line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(fact.Line, true);
                CommitObservedDeparture(fact, waypoints, departure.Control, fact.Frame);
                m_StopRuntime.FinalizeDeparture(fact.Vehicle);
                m_Observation.ClearDwellDeadlineCache(fact.Vehicle);
                m_ObsPersist.ClearDwell(fact.Vehicle);
                PublishStopFact(fact);
            }
        }

        private void ApplyLaunchCommits()
        {
            IReadOnlyList<LaunchCommit> commits = m_RuntimeEngine.LaunchCommits;
            for (int i = 0; i < commits.Count; i++)
            {
                LaunchCommit commit = commits[i];
                StopControlResult control = m_StopRuntime.ClearStopSession(commit.Vehicle);
                ApplyStopControl(commit.Vehicle, commit.Waypoint, control);
                m_StopRuntime.SetEffectiveBoarding(commit.Vehicle, false);
                CommitSourceWaypoint(commit.Vehicle, -1);
                bool isRail = RuntimePorts.TryResolveVehicleLifecycle(this, commit.Vehicle, out LifecycleKind lifecycle)
                    && lifecycle == LifecycleKind.Rail;
                bool canBypass = CanLineBypass(commit.Line);
                if (isRail && commit.ClearRescue)
                    m_Bypass.ClearRescue(commit.Vehicle);
                if (canBypass && commit.Line != Entity.Null && commit.ArmExpressRescue)
                {
                    m_Bypass.ArmExpressRescue(commit.Vehicle, commit.Line, m_SimulationSystem.frameIndex);
                }
                if (canBypass && commit.Line != Entity.Null && commit.RefreshLine)
                {
                    m_Bypass.RequestLineOrderedRuntimeForceRefresh(commit.Line, "launch-confirmed");
                }
            }
        }

        private void ApplyWaypointCommits()
        {
            IReadOnlyList<WaypointCommit> commits = m_RuntimeEngine.WaypointCommits;
            for (int i = 0; i < commits.Count; i++)
            {
                WaypointCommit commit = commits[i];
                CommitSourceWaypoint(commit.Vehicle, commit.Waypoint);
            }
        }

        private void ApplyStartupActivationState(IReadOnlyList<FramePlanEntry> entries, uint frame)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                FramePlanEntry entry = entries[i];
                if ((entry.Stages & RuntimeStageMask.Dispatch) == 0)
                    continue;
                Entity vehicle = entry.Vehicle;
                if (!m_VehicleView.TryGetLine(vehicle, out Entity line)
                    || !m_VehicleView.TryGetState(vehicle, out VehicleState state))
                {
                    continue;
                }

                if (state == VehicleState.Running)
                    m_RuntimeEngine.CommitRunning(vehicle, line);
                else if (state == VehicleState.Holding)
                    m_Observation.Seed(vehicle, line, frame);
            }
        }

        private void ApplyRunningCommits()
        {
            IReadOnlyList<RunningCommit> commits = m_RuntimeEngine.RunningCommits;
            for (int i = 0; i < commits.Count; i++)
            {
                RunningCommit commit = commits[i];
                if (!m_VehicleView.TryGetState(commit.Vehicle, out VehicleState state)
                    || state != VehicleState.Running)
                {
                    continue;
                }
                bool isRail = RuntimePorts.TryResolveVehicleLifecycle(this, commit.Vehicle, out LifecycleKind lifecycle)
                    && lifecycle == LifecycleKind.Rail;
                bool canBypass = CanLineBypass(commit.Line);
                if (isRail && commit.ClearRescue)
                    m_Bypass.ClearRescue(commit.Vehicle);
                if (canBypass && commit.Line != Entity.Null && commit.ArmExpressRescue)
                    m_Bypass.ArmExpressRescue(commit.Vehicle, commit.Line, m_SimulationSystem.frameIndex);
                if (canBypass && commit.Line != Entity.Null && commit.RefreshLine)
                    m_Bypass.RequestLineOrderedRuntimeForceRefresh(commit.Line, "running-commit");
            }
            m_RuntimeEngine.ClearRunningCommits();
        }

        private void ApplyRoadOriginStops(
            IReadOnlyList<DispatchInput> inputs,
            EntityCommandBuffer commandBuffer)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                DispatchInput input = inputs[i];
                if (!RuntimePorts.TryResolveVehicleLifecycle(this, input.Vehicle, out LifecycleKind lifecycle)
                    || lifecycle != LifecycleKind.Road
                    || !input.InputValid
                    || !input.TargetAtOrigin
                    || !m_VehicleView.TryGetState(input.Vehicle, out VehicleState state)
                    || state != VehicleState.Running)
                {
                    continue;
                }

                m_CommandApplier.EnsureRunningOriginStop(
                    input.Vehicle,
                    input.Line,
                    commandBuffer);
            }
        }

        private void ApplyPreparingRepairs(
            IReadOnlyList<DispatchInput> inputs,
            EntityCommandBuffer commandBuffer,
            uint nowFrame)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                DispatchInput input = inputs[i];
                if (!input.PreparingRouteNeedsRepair)
                    continue;

                if (!RuntimePorts.TryResolveVehicleLifecycle(this, input.Vehicle, out LifecycleKind lifecycle))
                    continue;
                if (lifecycle == LifecycleKind.Road)
                {
                    m_CommandApplier.EnsurePreparingRoute(
                        input.Vehicle,
                        input.Line,
                        input.CurrentWaypoint,
                        commandBuffer);
                    continue;
                }

                StopCancelResult cancelled = m_StopRuntime.CancelStopSession(
                    input.Vehicle,
                    nowFrame);
                if (cancelled.Exists)
                {
                    PublishStopFact(cancelled.Fact);
                    ApplyStopControl(
                        input.Vehicle,
                        cancelled.Control.WaypointIndex,
                        cancelled.Control);
                }

                CommitSourceWaypoint(input.Vehicle, -1);
                m_VehicleRegistry.SetState(input.Vehicle, VehicleState.Preparing);
                m_VehicleRegistry.SetPreparing(input.Vehicle, nowFrame);
                m_VehicleRegistry.ClearBoardingGrace(input.Vehicle);
                m_RuntimeEngine.ClearAssistLaunchPending(input.Vehicle);

                if (m_CommandApplier.EnsurePreparingRoute(
                    input.Vehicle,
                    input.Line,
                    input.CurrentWaypoint,
                    commandBuffer))
                {
                    uint cooldownUntil = nowFrame + PREPARINGFIX_REPATH_COOLDOWN_FRAMES;
                    m_PreparingFixCooldownUntil[input.Vehicle] = cooldownUntil;
                    m_RuntimeFramePlan.SetDeadline(
                        input.Vehicle,
                        DeadlineKind.PreparingCooldown,
                        cooldownUntil);
                    log.Info("[PreparingFix] 线路" + input.Line.Index + " 车辆" + input.Vehicle.Index
                        + " 重置去始发站 wp=" + input.CurrentWaypoint);
                }
            }
        }

        internal void ApplyStopControl(Entity vehicle, int waypointIndex, StopControlResult control)
        {
            if (control.WriteCachedWaypoint)
            {
                CommitSourceWaypoint(vehicle, control.CachedWaypointIndex);
            }
            bool isRail = RuntimePorts.TryResolveVehicleLifecycle(this, vehicle, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail;
            if (isRail && control.InboundAction == StopInboundAction.Mark)
                m_VehicleRegistry.MarkInbound(vehicle);
            else if (isRail && control.InboundAction == StopInboundAction.Clear)
                m_VehicleRegistry.ClearInbound(vehicle);
            if (isRail && control.NoteProgressSuspect)
                m_TrackProjection.NoteVehicleProgressSuspectRecoveryBoarding(vehicle, waypointIndex);
            if (isRail && control.ClearProgressSuspect)
                m_TrackProjection.TryClearVehicleProgressSuspectOnStableDeparture(vehicle, waypointIndex);
            if (isRail && control.ClearBypassHoldSkipped)
                m_Bypass.ClearBypassHoldSkipped(vehicle);
            if (control.ClearForcedMidStop)
                m_StopRuntime.ClearForcedMidStop(vehicle);
        }

        internal void CommitObservedDeparture(
            StopFact fact,
            DynamicBuffer<RouteWaypoint> waypoints,
            StopControlResult control,
            uint nowFrame)
        {
            bool isRail = RuntimePorts.TryResolveVehicleLifecycle(this, fact.Vehicle, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail;
            PassengerFlow.Runtime.Current?.ConfirmDeparture(fact.Vehicle, nowFrame);
            if (m_Observation.TryRecordObservedStopDwellOnBoardingEnd(
                    fact.Vehicle,
                    fact.Line,
                    fact.WaypointIndex,
                    nowFrame,
                    out int observedWaypointIndex))
            {
                m_LineTimes.RefreshObservedStop(fact.Line, waypoints, observedWaypointIndex);
            }
            m_WorkbenchBridge.ObservationStops().Record(
                fact.Vehicle,
                fact.Line,
                waypoints,
                false,
                -1,
                fact.WaypointIndex);
            if (isRail)
            {
                m_Announcements.ServiceEnded(
                    fact.Vehicle,
                    fact.Line,
                    waypoints,
                    fact.WaypointIndex);
            }
            else if (lifecycle == LifecycleKind.Road)
            {
                m_Observation.BeginBusSeg(fact.Vehicle, fact.Line, fact.WaypointIndex, nowFrame);
                m_Announcements.BusDeparted(
                    fact.Vehicle,
                    fact.Line,
                    waypoints,
                    fact.WaypointIndex);
            }
            ApplyStopControl(fact.Vehicle, fact.WaypointIndex, control);
        }

        private void RunSlicePhase(uint nowFrame, IReadOnlyList<FramePlanEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entity vehicle = entries[i].Vehicle;
                if (!m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state != VehicleState.Running
                    || !m_VehicleView.TryGetLine(vehicle, out Entity line)
                    || line == Entity.Null
                    || !m_RailEventSource.TryGetRouteWaypoints(
                        vehicle,
                        out _,
                        out DynamicBuffer<RouteWaypoint> waypoints))
                {
                    continue;
                }

                m_RuntimeHotPathProbe.CountStageExecuted(RuntimeStageMask.Slice, 1);
                m_Observation.UpdateSlice(vehicle, line, waypoints, nowFrame);
            }
        }

        private int CountValidStopInputs()
        {
            int count = 0;
            for (int i = 0; i < m_StopInputs.Count; i++)
            {
                if (m_StopInputs[i].InputValid)
                    count++;
            }
            return count;
        }

        private int CountValidDispatchInputs()
        {
            int count = 0;
            for (int i = 0; i < m_DispatchInputs.Count; i++)
            {
                if (m_DispatchInputs[i].InputValid)
                    count++;
            }
            return count;
        }

        private void PublishRunningNotices()
        {
            for (int i = 0; i < m_RailEventSource.RunningNoticeCount; i++)
            {
                if (!m_RailEventSource.TryGetRunningNotice(
                        i,
                        out Entity vehicle,
                        out Entity line,
                        out DynamicBuffer<RouteWaypoint> waypoints,
                        out int waypointIndex,
                        out bool boarding)
                    || !m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state != VehicleState.Running
                    || !m_VehicleView.TryGetLine(vehicle, out Entity currentLine)
                    || currentLine != line)
                {
                    continue;
                }

                m_Announcements.Running(vehicle, line, waypoints, waypointIndex, boarding);
            }
        }

        private void PublishPreparingNotices(uint nowFrame)
        {
            for (int i = 0; i < m_RailEventSource.PreparingNoticeCount; i++)
            {
                if (!m_RailEventSource.TryGetPreparingNotice(
                        i,
                        out Entity vehicle,
                        out Entity line,
                        out DynamicBuffer<RouteWaypoint> waypoints,
                        out int waypointIndex,
                        out bool boarding)
                    || !m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state != VehicleState.Preparing
                    || !m_VehicleView.TryGetLine(vehicle, out Entity currentLine)
                    || currentLine != line)
                {
                    continue;
                }

                m_Announcements.Preparing(
                    vehicle,
                    line,
                    waypoints,
                    m_LineProfile.HasPreparingReachedOrigin(
                        vehicle,
                        waypoints,
                        boarding,
                        waypointIndex),
                    nowFrame);
            }
        }

        private void ResolveVanillaBlockerRescues(
            EntityCommandBuffer commandBuffer,
            IReadOnlyList<FramePlanEntry> entries)
        {
            if (entries.Count == 0)
                return;

            uint nowFrame = m_SimulationSystem.frameIndex;
            for (int i = 0; i < entries.Count; i++)
            {
                Entity express = entries[i].Vehicle;
                if (express == Entity.Null
                    || !m_VehicleView.TryGetLine(express, out Entity expressLine)
                    || !m_Bypass.TryResolveVanillaBlockerRescue(express, expressLine, nowFrame, out Entity local)
                    || local == Entity.Null
                    || !m_RescueLocalVehicles.Add(local))
                {
                    continue;
                }

                m_RescueCandidates.Add(new RescueCandidate(express, local, m_Resolve.Line(local)));
            }

            m_RescueCandidates.Sort(s_RescueCandidateComparison);

            for (int i = 0; i < m_RescueCandidates.Count; i++)
            {
                RescueCandidate candidate = m_RescueCandidates[i];
                if (!EntityManager.Exists(candidate.Local)
                    || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(candidate.Local))
                {
                    continue;
                }

                m_Bypass.CommitVanillaBlockerRescue(
                    candidate.Local,
                    candidate.Express);
                m_RuntimeHotPathProbe.CountStageExecuted(RuntimeStageMask.Rescue, 1);
                m_FrameEvents.AppendBypass(
                    new BypassFact(
                        BypassFactKind.Rescued,
                        candidate.Local,
                        m_Resolve.Line(candidate.Local),
                        candidate.Express,
                        -1,
                        false,
                        true,
                        reason: "vanilla-blocker-chain-stall"),
                    nowFrame);
                Game.Vehicles.PublicTransport publicTransport = ReadRailPublicTransport(candidate.Local);
                m_CommandApplier.ForceDepart(candidate.Local, ref publicTransport, nowFrame, commandBuffer);
                if (RtLog.VerboseEnabled)
                {
                    Entity line = m_Resolve.Line(candidate.Express);
                    log.Info("[待避防卡死放行] 线路" + line.Index
                        + " express=" + candidate.Express.Index
                        + " local=" + candidate.Local.Index
                        + " reason=vanilla-blocker-chain-stall");
                }
            }
        }

        private void RunBypassPhase(
            EntityCommandBuffer commandBuffer,
            Dictionary<Entity, BypassControlResult> bypassControls,
            HashSet<Entity> bypassAttempts,
            uint nowFrame,
            IReadOnlyList<FramePlanEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entity vehicle = entries[i].Vehicle;
                if (!m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || !m_VehicleView.TryGetLine(vehicle, out Entity line)
                    || line == Entity.Null)
                {
                    continue;
                }

                if (!CanLineBypass(line))
                    continue;

                if (state != VehicleState.Running)
                {
                    m_Bypass.ClearVehicle(vehicle);
                    continue;
                }

                bypassAttempts.Add(vehicle);
                if (!m_RailEventSource.TryGetBypassInput(
                        vehicle,
                        out Entity route,
                        out DynamicBuffer<RouteWaypoint> waypoints,
                        out Game.Vehicles.PublicTransport publicTransport))
                {
                    continue;
                }

                m_RuntimeHotPathProbe.CountStageExecuted(RuntimeStageMask.Bypass, 1);
                bool boarding = m_StopRuntime.ReadEffectiveBoarding(vehicle);
                bool cachedWaypointKnown = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex);
                int waypointIndex = cachedWaypointKnown ? cachedWaypointIndex : -1;
                int controlWaypointIndex = waypointIndex;
                bool bypassSkipped = m_Bypass.TryGetBypassHoldSkipped(vehicle, out _);
                bool bypassLatched = m_Bypass.TryGetLatchedBlocker(vehicle, out _);
                bool sceneKnown = false;
                bool sceneEligible = true;
                if (boarding && !bypassSkipped && controlWaypointIndex > 0)
                {
                    sceneEligible = m_Bypass.IsStopSceneEligible(
                        route,
                        waypoints,
                        controlWaypointIndex,
                        out sceneKnown);
                }
                m_Bypass.UpdateWatch(
                    vehicle,
                    route,
                    waypoints,
                    controlWaypointIndex,
                    boarding,
                    sceneKnown,
                    sceneEligible);
                if (m_StopRuntime.IsDepartureCandidate(vehicle)
                    && controlWaypointIndex > 0
                    && !bypassSkipped)
                {
                    BypassDecisionResult departureGate = m_Bypass.EvaluateDepartureGate(
                        vehicle,
                        route,
                        waypoints,
                        controlWaypointIndex,
                        nowFrame);
                    if (departureGate.ShouldHold && !departureGate.CanClearAfterExit)
                    {
                        m_StopRuntime.RejectDepartureCandidate(vehicle);
                        m_Bypass.LogDepartureGate(
                            vehicle,
                            "gate|suppress|" + controlWaypointIndex,
                            "[待避离站门] vehicle=" + vehicle.Index
                                + " line=" + line.Index
                                + " prevWp=" + controlWaypointIndex
                                + " action=suppress");
                    }
                }
                bool skipBypass = !boarding && !bypassLatched && !bypassSkipped;
                if (!skipBypass && boarding && !bypassLatched && !bypassSkipped && controlWaypointIndex > 0)
                {
                    skipBypass = sceneKnown && !sceneEligible;
                }

                BypassControlResult control = skipBypass
                    ? new BypassControlResult(
                        false,
                        vehicle,
                        route,
                        controlWaypointIndex,
                        false,
                        false,
                        Entity.Null,
                        true,
                        null)
                    : m_Bypass.UpdateVehicle(
                        vehicle,
                        route,
                        waypoints,
                        controlWaypointIndex,
                        boarding,
                        nowFrame);
                if (!skipBypass)
                {
                    m_Bypass.ApplyControl(
                        control,
                        boarding,
                        ref publicTransport,
                        commandBuffer,
                        waypoints,
                        "线路" + line.Index,
                        nowFrame);
                }
                bypassControls[vehicle] = control;
                if (control.ShouldHold
                    && m_Bypass.TryGetHoldCadence(vehicle, out BypassHoldCadenceSnapshot heldCadence)
                    && heldCadence.EvaluatedFrame == nowFrame)
                {
                    m_FrameEvents.AppendBypass(new BypassFact(
                        BypassFactKind.BypassHoldCadence,
                        vehicle,
                        control.Line,
                        control.Blocker,
                        control.WaypointIndex,
                        true,
                        control.CanClearAfterExit,
                        control.ReleaseReason), nowFrame);
                }
            }
        }

        private void ApplyRetireCommand(RetireCommand command)
        {
            Entity vehicle = m_Resolve.SelectedVehicle(command.Vehicle);
            if (vehicle == Entity.Null
                || !m_VehicleView.Contains(vehicle)
                || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle))
            {
                return;
            }

            m_CommandApplier.Retire(vehicle, "UI请求");
        }

        private void ApplyRecheckCommand(RecheckCommand command)
        {
            Entity vehicle = m_Resolve.SelectedVehicle(command.Vehicle);
            if (vehicle != Entity.Null && m_VehicleView.Contains(vehicle))
                m_RuntimeEngine.Reevaluate(vehicle);
        }

        private void ApplyDepartCommand(DepartCommand command, EntityCommandBuffer commandBuffer)
        {
            Entity vehicle = m_Resolve.SelectedVehicle(command.Vehicle);
            if (vehicle == Entity.Null
                || !m_VehicleView.Contains(vehicle)
                || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                || !EntityManager.HasComponent<Target>(vehicle))
            {
                return;
            }

            Entity line = m_Resolve.Line(vehicle);
            if (line == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            if ((publicTransport.m_State & PublicTransportFlags.Boarding) == 0)
                return;

            int waypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                ? cachedWaypointIndex
                : -1;
            if (CanLineBypass(line))
            {
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                waypointIndex = m_WaypointIndex.Compute(vehicle, waypoints);
                if (waypointIndex < 0)
                    waypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out cachedWaypointIndex)
                        ? cachedWaypointIndex
                        : -1;

                Entity blocker = m_Bypass.TryGetLatchedBlocker(vehicle, out Entity latchedBlocker)
                    ? latchedBlocker
                    : Entity.Null;
                m_Bypass.ClearVehicle(vehicle, "UI强制发车");
                m_Bypass.MarkBypassHoldSkipped(vehicle, blocker);
            }
            uint nowFrame = m_SimulationSystem.frameIndex;
            m_StopRuntime.SetForcedMidStopGrace(vehicle, nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES);
            m_CommandApplier.ForceDepart(vehicle, nowFrame, commandBuffer);
            m_StopRuntime.StartDeparturePending(vehicle, nowFrame);
            PublishStopFact(new StopFact(
                StopFactKind.BoardingCloseRequested,
                vehicle,
                line,
                waypointIndex,
                nowFrame,
                reason: "manual-depart"));
            log.Info("[强制发车协助] 线路" + line.Index + " 车辆" + vehicle.Index
                + " wp=" + waypointIndex);
        }

        private void ApplySpawnCommand(SpawnCommand command, ClockSnapshot clockSnapshot)
        {
            Entity line = m_Resolve.SelectedLine(command.Line, Entity.Null);
            if (line == Entity.Null || !EntityManager.Exists(line) || !m_LineView.Applied(line))
                return;

            BufferLookup<RouteVehicle> routeVehicles = GetBufferLookup<RouteVehicle>(true);
            int actualCount = m_LineVehicles.Count(line, routeVehicles);
            int pendingTarget = m_SpawningLines.TryGetValue(line, out int existingTarget)
                ? math.max(existingTarget, actualCount)
                : actualCount;
            int nextTarget = pendingTarget + 1;
            m_SpawningLines[line] = nextTarget;
            m_LineSpawnRequestFrame[line] = m_SimulationSystem.frameIndex;
            m_SelectPanel.RecordManualSpawnSummary(line, clockSnapshot.NowMinute, nextTarget);
            log.Info("[面板发车] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ", 目标=" + nextTarget + ")");
        }

        private void TickLineSpawnControl(int nowMinute)
        {
            if (nowMinute == m_LastPuppetMasterMinute)
                return;

            try
            {
                m_LineSpawnControl.Tick(nowMinute);
                m_LastPuppetMasterMinute = nowMinute;
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] PuppetMasterControl -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }

        private void ClearRunningExitBypass()
        {
            IReadOnlyList<DispatchEvent> dispatchEvents = m_FrameEvents.DispatchEvents;
            for (int i = 0; i < dispatchEvents.Count; i++)
            {
                DispatchEvent dispatchEvent = dispatchEvents[i];
                if (dispatchEvent.Kind == DispatchFactKind.State
                    && dispatchEvent.PreviousState == VehicleState.Running
                    && (dispatchEvent.CurrentState == VehicleState.Idle
                        || dispatchEvent.CurrentState == VehicleState.Holding))
                {
                    m_Bypass.ClearVehicle(dispatchEvent.Vehicle, "运行态退出");
                }
            }
        }

        private void ConsumeFrameEvents()
        {
            m_BypassReleaseConsumers.Clear();
            m_FrameLabelActions.Clear();
            m_FrameLabelOrder.Clear();
            IReadOnlyList<FrameEventRef> events = m_FrameEvents.MergeBySequence();
            for (int i = 0; i < events.Count; i++)
            {
                FrameEventRef frameEvent = events[i];
                if (frameEvent.Kind == FrameEventKind.Stop)
                {
                    RapidTransitMod.Dispatch.Runtime.StopEvent stopEvent = m_FrameEvents.StopEvents[frameEvent.Index];
                    ConsumeStopEvent(stopEvent);
                    ProjectStopLabel(stopEvent.Fact, m_FrameLabelActions, m_FrameLabelOrder);
                    continue;
                }

                if (frameEvent.Kind == FrameEventKind.Bypass)
                {
                    RapidTransitMod.Dispatch.Runtime.BypassEvent bypassEvent = m_FrameEvents.BypassEvents[frameEvent.Index];
                    ConsumeBypassEvent(bypassEvent);
                    ProjectBypassLabel(bypassEvent.Fact, m_FrameLabelActions, m_FrameLabelOrder);
                    continue;
                }

                if (frameEvent.Kind == FrameEventKind.Dispatch)
                {
                    DispatchEvent dispatchEvent = m_FrameEvents.DispatchEvents[frameEvent.Index];
                    ConsumeDispatchEvent(dispatchEvent);
                    ProjectDispatchLabel(dispatchEvent, m_FrameLabelActions, m_FrameLabelOrder);
                    continue;
                }

                if (frameEvent.Kind != FrameEventKind.Lifecycle)
                    continue;

                LifecycleEvent lifecycleEvent = m_FrameEvents.LifecycleEvents[frameEvent.Index];
                ConsumeLifecycleEvent(lifecycleEvent);
            }

            CommitFrameLabels(m_FrameLabelActions, m_FrameLabelOrder);
        }

        private void ConsumeLifecycleEvent(LifecycleEvent lifecycleEvent)
        {
            if (lifecycleEvent.Kind == LifecycleFactKind.Rebound)
            {
                m_Observation.CancelBusSeg(lifecycleEvent.Vehicle);
                m_Announcements.RemoveVehicle(lifecycleEvent.Vehicle);
                if (RuntimePorts.TryResolveLineLifecycle(this, lifecycleEvent.Line, out LifecycleKind reboundLifecycle)
                    && reboundLifecycle == LifecycleKind.Road)
                {
                    PassengerFlow.Runtime.Current?.RemoveVehicle(lifecycleEvent.Vehicle);
                }
                return;
            }

            if (lifecycleEvent.Kind == LifecycleFactKind.Removed)
            {
                if (RuntimePorts.TryResolveLineLifecycle(this, lifecycleEvent.Line, out LifecycleKind lifecycle))
                {
                    if (lifecycle == LifecycleKind.Rail)
                    {
                        m_RailEtaService?.CancelTargetRequests(lifecycleEvent.Vehicle, "Removed");
                        m_RailEventSource.RemoveVehicle(lifecycleEvent.Vehicle);
                    }
                    else if (lifecycle == LifecycleKind.Road)
                        m_RoadEventSource.RemoveVehicle(lifecycleEvent.Vehicle);
                }
                m_LifecycleResolveLogFrames.Remove(lifecycleEvent.Vehicle);
                m_CommandApplier.RemoveRoadCommandLog(lifecycleEvent.Vehicle);
                m_Observation.RemoveBusSegVehicle(lifecycleEvent.Vehicle);
                m_Announcements.RemoveVehicle(lifecycleEvent.Vehicle);
                PassengerFlow.Runtime.Current?.RemoveVehicle(lifecycleEvent.Vehicle);
                return;
            }
        }

        private void ConsumeStopEvent(RapidTransitMod.Dispatch.Runtime.StopEvent stopEvent)
        {
            StopFact fact = stopEvent.Fact;
            if (fact.Kind == StopFactKind.Removed)
                return;

            if (fact.Kind == StopFactKind.Restored
                || fact.Kind == StopFactKind.Recovered)
            {
                if (RuntimePorts.TryResolveVehicleLifecycle(this, fact.Vehicle, out LifecycleKind restoredLifecycle)
                    && (restoredLifecycle == LifecycleKind.Rail || restoredLifecycle == LifecycleKind.Road))
                {
                    PassengerFlow.Runtime.Current?.RestoreStop(
                        fact.Vehicle,
                        fact.Line,
                        fact.WaypointIndex,
                        fact.Frame);
                }
                return;
            }

            if (fact.Kind == StopFactKind.Cancelled)
            {
                m_Observation.CancelBusSeg(fact.Vehicle);
                if (RuntimePorts.TryResolveVehicleLifecycle(this, fact.Vehicle, out LifecycleKind cancelledLifecycle)
                    && (cancelledLifecycle == LifecycleKind.Rail || cancelledLifecycle == LifecycleKind.Road))
                {
                    PassengerFlow.Runtime.Current?.CancelStop(fact.Vehicle);
                }
                return;
            }

            if (fact.Kind == StopFactKind.BoardingEnded
                || fact.Kind == StopFactKind.Departed
                || fact.Kind == StopFactKind.DwellTimedOut)
            {
                return;
            }

            if (fact.Kind != StopFactKind.Opened
                || fact.Line == Entity.Null
                || !EntityManager.Exists(fact.Line)
                || !EntityManager.HasBuffer<RouteWaypoint>(fact.Line))
            {
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(fact.Line, true);
            if (RuntimePorts.TryResolveVehicleLifecycle(this, fact.Vehicle, out LifecycleKind openedLifecycle)
                && openedLifecycle == LifecycleKind.Road
                && m_Observation.TryEndBusSeg(
                    fact.Vehicle,
                    fact.Line,
                    fact.WaypointIndex,
                    fact.Frame,
                    out BusSegSample sample))
            {
                m_LineTimes.RefreshBusSeg(
                    fact.Line,
                    waypoints,
                    sample.Key.FromWaypoint,
                    sample.Key.FromStop,
                    sample.Key.ToWaypoint,
                    sample.Key.ToStop);
            }
            m_Observation.BeginObservedDwellSession(fact.Vehicle, fact.Line, fact.WaypointIndex, fact.Frame);
            m_WorkbenchBridge.ObservationStops().Record(
                fact.Vehicle,
                fact.Line,
                waypoints,
                true,
                fact.WaypointIndex,
                fact.PreviousWaypointIndex);
            m_Announcements.StopOpened(fact.Vehicle, fact.Line, waypoints, fact.WaypointIndex);
            if (RuntimePorts.TryResolveVehicleLifecycle(this, fact.Vehicle, out openedLifecycle)
                && (openedLifecycle == LifecycleKind.Rail || openedLifecycle == LifecycleKind.Road))
            {
                PassengerFlow.Runtime.Current?.OpenStop(
                    fact.Vehicle,
                    fact.Line,
                    fact.WaypointIndex,
                    fact.Frame);
            }
            if (openedLifecycle == LifecycleKind.Rail)
            {
                m_TrackProjection.NoteVehicleProgressSuspectRecoveryBoarding(
                    fact.Vehicle,
                    fact.WaypointIndex);
            }
        }

        private void ConsumeBypassEvent(RapidTransitMod.Dispatch.Runtime.BypassEvent bypassEvent)
        {
            BypassFact fact = bypassEvent.Fact;
            if (fact.Kind == BypassFactKind.BypassHoldCadence
                && fact.ShouldHold
                && fact.Line != Entity.Null
                && EntityManager.Exists(fact.Line)
                && EntityManager.HasBuffer<RouteWaypoint>(fact.Line))
            {
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(fact.Line, true);
                if (fact.WaypointIndex >= 0 && fact.WaypointIndex < waypoints.Length)
                    m_Announcements.BypassWaiting(fact.Vehicle, fact.Line, waypoints, fact.WaypointIndex);
                return;
            }

            if (fact.Kind == BypassFactKind.Held
                && fact.Line != Entity.Null
                && EntityManager.Exists(fact.Line)
                && EntityManager.HasBuffer<RouteWaypoint>(fact.Line))
            {
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(fact.Line, true);
                if (fact.WaypointIndex >= 0 && fact.WaypointIndex < waypoints.Length)
                {
                    Entity station = m_Resolve.Stop(waypoints[fact.WaypointIndex].m_Waypoint);
                    m_Observation.Hold(
                        fact.Vehicle,
                        fact.Blocker,
                        station,
                        fact.WaypointIndex,
                        bypassEvent.Frame,
                        string.IsNullOrEmpty(fact.Reason) ? "运行中" : fact.Reason);
                }
                return;
            }

            if ((fact.Kind == BypassFactKind.Released || fact.Kind == BypassFactKind.Expired)
                && m_BypassReleaseConsumers.Add(fact.Vehicle))
            {
                m_Observation.Release(
                    fact.Vehicle,
                    fact.Blocker,
                    bypassEvent.Frame,
                    string.IsNullOrEmpty(fact.Reason) ? "bypass-release" : fact.Reason);
            }
        }

        private void ConsumeDispatchEvent(DispatchEvent dispatchEvent)
        {
            if (dispatchEvent.Kind == DispatchFactKind.Target
                && dispatchEvent.CurrentValue >= 0
                && dispatchEvent.Line != Entity.Null)
            {
                m_Observation.BindTarget(
                    dispatchEvent.Line,
                    dispatchEvent.Vehicle,
                    dispatchEvent.CurrentValue,
                    dispatchEvent.Frame,
                    "dispatch-target");
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.LaunchConfirmed)
            {
                DispatchBusinessFact fact = dispatchEvent.Fact;
                bool assistLaunch = string.Equals(fact.Reason, "assist-launch", StringComparison.Ordinal);
                m_Observation.Record(
                    dispatchEvent.Vehicle,
                    assistLaunch
                        ? (fact.Late ? "协助补发确认" : "协助发车确认")
                        : (fact.Late ? "补发" : "计划发车"));
                m_Observation.Launch(
                    dispatchEvent.Line,
                    dispatchEvent.Vehicle,
                    fact.SlotMinute,
                    fact.ActualMinute,
                    dispatchEvent.Frame,
                    fact.Late);
                bool isRailOrRoad = RuntimePorts.TryResolveVehicleLifecycle(this, dispatchEvent.Vehicle, out LifecycleKind launchLifecycle)
                    && (launchLifecycle == LifecycleKind.Rail || launchLifecycle == LifecycleKind.Road);
                if (isRailOrRoad)
                    PassengerFlow.Runtime.Current?.LaunchOrigin(dispatchEvent.Vehicle, dispatchEvent.Frame);
                if (TryGetLineWaypoints(dispatchEvent.Line, out DynamicBuffer<RouteWaypoint> launchWaypoints))
                {
                    if (launchLifecycle == LifecycleKind.Road)
                    {
                        m_Observation.BeginBusSeg(
                            dispatchEvent.Vehicle,
                            dispatchEvent.Line,
                            0,
                            dispatchEvent.Frame);
                        m_Announcements.BusDeparted(
                            dispatchEvent.Vehicle,
                            dispatchEvent.Line,
                            launchWaypoints,
                            0);
                    }
                    m_WorkbenchBridge.ObservationStops().Start(dispatchEvent.Vehicle, dispatchEvent.Line, launchWaypoints);
                }
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.UnplannedRun)
            {
                bool idleRun = string.Equals(dispatchEvent.Fact.Reason, "idle-unplanned-run", StringComparison.Ordinal);
                m_Observation.Record(dispatchEvent.Vehicle, idleRun ? "Idle异常离站" : "Holding异常离站");
                return;
            }

            if (dispatchEvent.Kind != DispatchFactKind.State)
                return;

            if (RuntimePorts.TryResolveVehicleLifecycle(this, dispatchEvent.Vehicle, out LifecycleKind stateLifecycle)
                && stateLifecycle == LifecycleKind.Rail)
            {
                m_Announcements.StateChanged(
                    dispatchEvent.Vehicle,
                    dispatchEvent.PreviousState,
                    dispatchEvent.CurrentState);
            }

            if (dispatchEvent.PreviousState == VehicleState.Preparing
                && dispatchEvent.CurrentState != VehicleState.Preparing
                && RuntimePorts.TryResolveVehicleLifecycle(this, dispatchEvent.Vehicle, out LifecycleKind lifecycle)
                && lifecycle == LifecycleKind.Rail)
            {
                m_RailEventSource.ClearPreparingWaypoint(dispatchEvent.Vehicle);
            }

            if (dispatchEvent.PreviousState == VehicleState.Preparing
                && dispatchEvent.CurrentState == VehicleState.Holding
                && dispatchEvent.Line != Entity.Null)
            {
                m_Observation.Seed(dispatchEvent.Vehicle, dispatchEvent.Line, dispatchEvent.Frame);
            }

            if (dispatchEvent.PreviousState == VehicleState.Running
                && dispatchEvent.CurrentState == VehicleState.Idle)
            {
                m_Observation.Finish(dispatchEvent.Vehicle, dispatchEvent.Frame, -1, 0f);
                m_Observation.Update(dispatchEvent.Vehicle);
            }

            if (dispatchEvent.Line != Entity.Null)
                ConsumeDispatchBroadcast(dispatchEvent);
        }

        private void ConsumeDispatchBroadcast(DispatchEvent dispatchEvent)
        {
            if (!RuntimePorts.TryResolveVehicleLifecycle(this, dispatchEvent.Vehicle, out LifecycleKind lifecycle)
                || lifecycle != LifecycleKind.Rail)
            {
                return;
            }

            if (!TryGetLineWaypoints(dispatchEvent.Line, out DynamicBuffer<RouteWaypoint> waypoints))
                return;

            int waypointIndex = m_CachedWpIdx.TryGetValue(dispatchEvent.Vehicle, out int cachedWaypointIndex)
                ? cachedWaypointIndex
                : -1;
            bool boarding = m_StopRuntime.ReadEffectiveBoarding(dispatchEvent.Vehicle);
            bool atOrigin = waypointIndex == 0;
            if (dispatchEvent.CurrentState == VehicleState.Preparing)
            {
                m_Announcements.Preparing(
                    dispatchEvent.Vehicle,
                    dispatchEvent.Line,
                    waypoints,
                    atOrigin,
                    dispatchEvent.Frame);
                return;
            }

            if (dispatchEvent.CurrentState == VehicleState.Holding
                || dispatchEvent.CurrentState == VehicleState.Idle)
            {
                bool originBusy = atOrigin
                    || boarding
                    || m_VehicleStateStore.ForcedOriginReadyFrame.ContainsKey(dispatchEvent.Vehicle)
                    || waypointIndex == 0;
                m_Announcements.Origin(dispatchEvent.Line, waypoints, originBusy);
            }
        }

        private bool TryGetLineWaypoints(Entity line, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (line != Entity.Null
                && EntityManager.Exists(line)
                && EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                return waypoints.Length >= 2;
            }

            waypoints = default;
            return false;
        }

        private enum FrameLabelKind : byte { Runtime, Returning, Removed }
        private enum FrameLabelPriority : byte { Normal, StateOverride, StopControl, Fault, Returning, Removed }

        private readonly struct FrameLabelAction
        {
            public readonly Entity Vehicle;
            public readonly FrameLabelKind Kind;
            public readonly FrameLabelPriority Priority;
            public readonly VehicleLabelType Type;
            public readonly int CurrentSlotMinute;
            public readonly int NextSlotMinute;
            public readonly bool Late;
            public readonly bool Abnormal;
            public readonly bool IncludeHoldingInWaiting;
            public readonly string Reason;

            private FrameLabelAction(
                Entity vehicle,
                FrameLabelKind kind,
                FrameLabelPriority priority,
                VehicleLabelType type = default,
                int currentSlotMinute = -1,
                int nextSlotMinute = -1,
                bool late = false,
                bool abnormal = false,
                bool includeHoldingInWaiting = true,
                string reason = null)
            {
                Vehicle = vehicle;
                Kind = kind;
                Priority = priority;
                Type = type;
                CurrentSlotMinute = currentSlotMinute;
                NextSlotMinute = nextSlotMinute;
                Late = late;
                Abnormal = abnormal;
                IncludeHoldingInWaiting = includeHoldingInWaiting;
                Reason = reason;
            }

            public static FrameLabelAction Runtime(
                Entity vehicle,
                VehicleLabelType type,
                FrameLabelPriority priority,
                int currentSlotMinute = -1,
                int nextSlotMinute = -1,
                bool late = false,
                bool abnormal = false,
                bool includeHoldingInWaiting = true)
            {
                return new FrameLabelAction(
                    vehicle,
                    FrameLabelKind.Runtime,
                    priority,
                    type,
                    currentSlotMinute,
                    nextSlotMinute,
                    late,
                    abnormal,
                    includeHoldingInWaiting);
            }

            public static FrameLabelAction Returning(Entity vehicle, string reason)
            {
                return new FrameLabelAction(
                    vehicle,
                    FrameLabelKind.Returning,
                    FrameLabelPriority.Returning,
                    reason: reason);
            }

            public static FrameLabelAction Removed(Entity vehicle)
            {
                return new FrameLabelAction(
                    vehicle,
                    FrameLabelKind.Removed,
                    FrameLabelPriority.Removed);
            }

            public bool IsBypass => Kind == FrameLabelKind.Runtime && Type == VehicleLabelType.BypassExpress;
        }

        private void RecordFrameLabel(
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder,
            FrameLabelAction action,
            bool clearBypass = false)
        {
            if (action.Vehicle == Entity.Null)
                return;

            if (!labels.TryGetValue(action.Vehicle, out FrameLabelAction current))
            {
                labels[action.Vehicle] = action;
                labelOrder.Add(action.Vehicle);
                return;
            }

            if (!clearBypass || !current.IsBypass)
            {
                if (current.Priority > action.Priority)
                {
                    return;
                }
            }

            labels[action.Vehicle] = action;
        }

        private void ProjectStopLabel(
            StopFact fact,
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder)
        {
            if (fact.Kind == StopFactKind.Removed)
            {
                RecordFrameLabel(labels, labelOrder, FrameLabelAction.Removed(fact.Vehicle));
                return;
            }

            if (fact.Kind == StopFactKind.Opened
                || fact.Kind == StopFactKind.Restored
                || fact.Kind == StopFactKind.Recovered)
            {
                ProjectCurrentStateLabel(fact.Vehicle, labels, labelOrder);
                return;
            }

            if (fact.Kind == StopFactKind.Departed)
            {
                ProjectCurrentStateLabel(
                    fact.Vehicle,
                    labels,
                    labelOrder,
                    priority: FrameLabelPriority.StateOverride);
                return;
            }

            if (fact.Kind == StopFactKind.BoardingEnded
                || fact.Kind == StopFactKind.BoardingCloseRequested)
            {
                int targetMinute = m_VehicleView.TryGetTarget(fact.Vehicle, out int target) ? target : -1;
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        fact.Vehicle,
                        VehicleLabelType.BoardingEnd,
                        FrameLabelPriority.StateOverride,
                        currentSlotMinute: targetMinute));
                return;
            }

            if (fact.Kind == StopFactKind.StopAssistActive)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        fact.Vehicle,
                        VehicleLabelType.StopTimeoutAssist,
                        FrameLabelPriority.Fault));
                return;
            }

            if (fact.Kind == StopFactKind.DwellTimedOut)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        fact.Vehicle,
                        VehicleLabelType.StopTimeout,
                        FrameLabelPriority.StopControl));
            }
        }

        private void ProjectBypassLabel(
            BypassFact fact,
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder)
        {
            if (fact.Kind == BypassFactKind.Held || fact.Kind == BypassFactKind.BypassHoldCadence)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        fact.Vehicle,
                        VehicleLabelType.BypassExpress,
                        FrameLabelPriority.StopControl));
                return;
            }

            if (fact.Kind == BypassFactKind.Released
                || fact.Kind == BypassFactKind.Cleared
                || fact.Kind == BypassFactKind.Expired
                || fact.Kind == BypassFactKind.Rescued)
            {
                ProjectCurrentStateLabel(fact.Vehicle, labels, labelOrder, clearBypass: true);
            }
        }

        private void ProjectDispatchLabel(
            DispatchEvent dispatchEvent,
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder)
        {
            if (dispatchEvent.Kind == DispatchFactKind.RetireRequested)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Returning(dispatchEvent.Vehicle, dispatchEvent.Fact.Reason));
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.PathFault)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        dispatchEvent.Vehicle,
                        VehicleLabelType.PathFault,
                        FrameLabelPriority.Fault));
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.UnplannedRun)
            {
                bool idleRun = string.Equals(dispatchEvent.Fact.Reason, "idle-unplanned-run", StringComparison.Ordinal);
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        dispatchEvent.Vehicle,
                        idleRun ? VehicleLabelType.AbnormalDeparture : VehicleLabelType.Running,
                        FrameLabelPriority.StateOverride,
                        abnormal: true));
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.LaunchConfirmed)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        dispatchEvent.Vehicle,
                        VehicleLabelType.Running,
                        FrameLabelPriority.StateOverride,
                        nextSlotMinute: dispatchEvent.Fact.TargetMinute,
                        late: dispatchEvent.Fact.Late));
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.RunningRecovery)
            {
                RecordFrameLabel(
                    labels,
                    labelOrder,
                    FrameLabelAction.Runtime(
                        dispatchEvent.Vehicle,
                        VehicleLabelType.Holding,
                        FrameLabelPriority.StateOverride,
                        currentSlotMinute: dispatchEvent.Fact.TargetMinute,
                        includeHoldingInWaiting: false));
                return;
            }

            if (dispatchEvent.Kind == DispatchFactKind.Target
                || dispatchEvent.Kind == DispatchFactKind.Slot)
            {
                ProjectCurrentStateLabel(dispatchEvent.Vehicle, labels, labelOrder);
                return;
            }

            if (dispatchEvent.Kind != DispatchFactKind.State)
                return;

            ProjectStateLabel(
                dispatchEvent.Vehicle,
                dispatchEvent.CurrentState,
                m_VehicleView.TryGetTarget(dispatchEvent.Vehicle, out int targetMinute) ? targetMinute : -1,
                labels,
                labelOrder);
        }

        private void ProjectCurrentStateLabel(
            Entity vehicle,
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder,
            bool clearBypass = false,
            FrameLabelPriority priority = FrameLabelPriority.Normal)
        {
            if (m_VehicleView.TryGetState(vehicle, out VehicleState state))
            {
                int targetMinute = m_VehicleView.TryGetTarget(vehicle, out int target) ? target : -1;
                ProjectStateLabel(vehicle, state, targetMinute, labels, labelOrder, clearBypass, priority);
            }
        }

        private void ProjectStateLabel(
            Entity vehicle,
            VehicleState state,
            int targetMinute,
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder,
            bool clearBypass = false,
            FrameLabelPriority priority = FrameLabelPriority.Normal)
        {
            FrameLabelAction action;
            if (state == VehicleState.Retiring)
            {
                action = FrameLabelAction.Runtime(
                    vehicle,
                    VehicleLabelType.Returning,
                    priority);
            }
            else if (state == VehicleState.Running)
            {
                int currentSlotMinute = m_VehicleView.TryGetSlot(vehicle, out int slotMinute)
                    ? slotMinute
                    : int.MinValue;
                action = FrameLabelAction.Runtime(
                    vehicle,
                    VehicleLabelType.Running,
                    priority,
                    currentSlotMinute,
                    targetMinute);
            }
            else if (state == VehicleState.Holding)
            {
                action = FrameLabelAction.Runtime(
                    vehicle,
                    VehicleLabelType.Holding,
                    priority,
                    currentSlotMinute: targetMinute,
                    late: targetMinute >= 0 && ScheduleClock.CanLate(m_SimClock.Snapshot.NowMinute, targetMinute));
            }
            else if (state == VehicleState.Idle)
            {
                action = FrameLabelAction.Runtime(
                    vehicle,
                    VehicleLabelType.WaitingDispatch,
                    priority);
            }
            else if (state == VehicleState.Preparing)
            {
                action = FrameLabelAction.Runtime(
                    vehicle,
                    VehicleLabelType.GoingOrigin,
                    priority,
                    nextSlotMinute: targetMinute);
            }
            else
            {
                return;
            }

            RecordFrameLabel(labels, labelOrder, action, clearBypass);
        }

        private void CommitFrameLabels(
            Dictionary<Entity, FrameLabelAction> labels,
            List<Entity> labelOrder)
        {
            for (int i = 0; i < labelOrder.Count; i++)
            {
                Entity vehicle = labelOrder[i];
                if (!labels.TryGetValue(vehicle, out FrameLabelAction action))
                    continue;

                if (action.Kind == FrameLabelKind.Removed)
                {
                    m_VehicleLabels.Remove(vehicle);
                    continue;
                }

                if (action.Kind == FrameLabelKind.Returning)
                {
                    m_VehicleLabels.SetRuntime(
                        vehicle,
                        VehicleLabelType.Returning,
                        vehicle.Index);
                    continue;
                }

                m_VehicleLabels.SetRuntime(
                    vehicle,
                    action.Type,
                    vehicle.Index,
                    action.CurrentSlotMinute,
                    action.NextSlotMinute,
                    action.Late,
                    action.Abnormal,
                    action.IncludeHoldingInWaiting);
            }
        }

        internal void ClearFrameBuffers()
        {
            m_BypassControls.Clear();
            m_BypassAttempts.Clear();
            m_RescueCandidates.Clear();
            m_RescueLocalVehicles.Clear();
        }

        private static int CompareRescueCandidates(RescueCandidate left, RescueCandidate right)
        {
            int lineOrder = CompareEntities(left.Line, right.Line);
            return lineOrder != 0 ? lineOrder : CompareEntities(left.Local, right.Local);
        }

        private static int CompareEntities(Entity left, Entity right)
        {
            return left.Index != right.Index
                ? left.Index.CompareTo(right.Index)
                : left.Version.CompareTo(right.Version);
        }

        private Game.Vehicles.PublicTransport ReadRailPublicTransport(Entity vehicle)
        {
            return m_RailEventSource.TryGetWrittenPublicTransport(vehicle, out Game.Vehicles.PublicTransport value)
                ? value
                : EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
        }

        private void DrainDisabledLineLateSpawnRetireQueue()
        {
            IReadOnlyList<Entity> queue = m_VehicleRegistrar.DisabledLineLateSpawnRetireQueue;
            if (queue.Count == 0)
                return;

            try
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    Entity vehicle = queue[i];
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                        continue;
                    if (EntityManager.HasComponent<RtRetireDispatchLock>(vehicle))
                    {
                        continue;
                    }
                    if (EntityManager.HasComponent<Deleted>(vehicle)
                        || EntityManager.HasComponent<ParkedTrain>(vehicle))
                    {
                        continue;
                    }
                    if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                        || !EntityManager.HasComponent<Target>(vehicle)
                        || !EntityManager.HasComponent<Owner>(vehicle))
                    {
                        log.Info("[DisabledLineLateSpawnSkip] 车辆" + vehicle.Index
                            + " 缺少回库前置组件，跳过误产车回库");
                        continue;
                    }

                    m_CommandApplier.Retire(vehicle, "关闭线路误产车");
                }
            }
            finally
            {
                m_VehicleRegistrar.ClearDisabledLineLateSpawnRetireQueue();
            }
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_RuntimeLifecycleHost.Loaded(serializationContext);
        }

        public void PreSerialize(Context context)
        {
            try
            {
                m_OverviewFeatureSettingsPersist?.SaveIfDirty();
            }
            catch (Exception ex)
            {
                log.Info("[OverviewFeatureSettingsPersist] Save failed -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void OnDestroy()
        {
            try
            {
                if (ReferenceEquals(Instance, this)) Instance = null!;
                RuntimeRoot.Clear(this);
                m_LifecycleResolveLogFrames.Clear();
                if (m_UICache.IsCreated) m_UICache.Dispose();
                m_StopRuntime?.Dispose();
                m_StopRuntime = null!;
                m_StopRuntimeState?.DisposeBoardingStates();
                if (m_BoardingFirstFrameGuardState.IsCreated) m_BoardingFirstFrameGuardState.Dispose();
                m_StopRuntimeState?.DisposeStopSessions();
                if (m_CachedWpIdx.IsCreated) m_CachedWpIdx.Dispose();
                m_StopRuntimeState?.DisposeInvalidatedRecovery();
                m_StopRuntimeState?.DisposeForcedMidStopGrace();
                m_StopRuntimeState?.Dispose();
                m_StopRuntimeState = null!;
                if (m_PreparingFixCooldownUntil.IsCreated) m_PreparingFixCooldownUntil.Dispose();
                if (m_SpawningLines.IsCreated) m_SpawningLines.Dispose();
                if (m_LastSpawnBlockedLogFrame.IsCreated) m_LastSpawnBlockedLogFrame.Dispose();
                if (m_LastScheduleDiagnosticLogFrame.IsCreated) m_LastScheduleDiagnosticLogFrame.Dispose();
                if (m_LineInitialAdopted.IsCreated) m_LineInitialAdopted.Dispose();
                if (m_JustLaunched.IsCreated) m_JustLaunched.Dispose();
                if (m_LineSpawnRequestFrame.IsCreated) m_LineSpawnRequestFrame.Dispose();
                m_LineMileage?.Clear();
            }
            finally
            {
                base.OnDestroy();
            }
        }

        internal static string SlotStr(int min)
        {
            min = ((min % 1440) + 1440) % 1440;
            int h = min / 60 % 24;
            int m = min % 60;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

    }
}
