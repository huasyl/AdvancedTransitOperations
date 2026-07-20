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
using RapidTransitMod.Dispatch.Workbench;
using RapidTransitMod.Core;
using RapidTransitMod.Planner;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using WorkbenchTime = RapidTransitMod.Dispatch.Workbench.Time;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
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

    public partial class DispatchRuntimeSystem : GameSystemBase, IPreSerialize
    {
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

        public static DispatchRuntimeSystem Instance = null!;
        internal TimedLogger log = Mod.log;
        internal SimulationSystem m_SimulationSystem = null!;
        internal TimeSystem m_TimeSystem = null!;
        internal SimClock m_SimClock = null!;
        internal NameSystem m_NameSystem = null!;
        internal EndFrameBarrier m_EndFrameBarrier = null!;

        // ── 车辆状态 ──
        internal VehicleStateStore m_VehicleStateStore = null!;
        internal VehicleRegistry m_VehicleRegistry = null!;
        internal VehicleView m_VehicleView = null!;
        internal LineView m_LineView = null!;
        internal FeatureGate m_Features = null!;
        internal RapidTransitMod.Overview.FeatureSettingsPersist m_OverviewFeatureSettingsPersist = null!;
        internal DispatchRuntimeController m_RuntimeController = null!;
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
        internal RuntimeShell m_RuntimeShell = null!;
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
        internal NativeHashMap<Entity, byte> m_LastEffectiveBoardingState;
        internal NativeHashMap<Entity, byte> m_LastOfficialBoardingState;
        internal NativeHashMap<Entity, byte> m_BoardingFirstFrameGuardState;
        internal NativeHashMap<Entity, Entity> m_StopSessionLine;
        internal NativeHashMap<Entity, int> m_StopSessionWaypointIndex;
        internal NativeHashMap<Entity, uint> m_StopSessionArrivalFrame;
        internal NativeHashMap<Entity, uint> m_StopSessionBoardingChangeCount;
        internal NativeHashMap<Entity, uint> m_DeparturePendingSinceFrame;
        internal NativeHashMap<Entity, int> m_CachedWpIdx;
        internal NativeHashSet<Entity> m_InvalidatedMidStopRecoveryPending;
        internal NativeHashSet<Entity> m_BVMisfire;
        internal NativeHashMap<Entity, uint> m_BVMisfireStartFrame;
        internal NativeHashMap<Entity, uint> m_ForcedMidStopBoardingGraceUntil;
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

        internal EntityQuery m_VehicleQuery;
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
        /// <summary>BV 误写超时 6000 帧（约 33 秒现实时间），给足自愈窗口。</summary>
        internal const uint BV_MISFIRE_TIMEOUT = 6000;
        /// <summary>暂时只观察 BV 误写，不再冻结车辆或回库；保留日志追踪后续是否能自愈。</summary>
        internal static bool IsBvMisfireEnforcementEnabled() => false;
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
        private const uint BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES = 8;
        private const uint BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES = 60;
        internal const float BOARDING_CLOSE_BYPASS_MIN_WAITING_DISTANCE_SENTINEL = -1f;
        private const uint BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES = 6;
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

            m_VehicleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] {
                    ComponentType.ReadWrite<Game.Vehicles.PublicTransport>(),
                    ComponentType.ReadWrite<Target>(),
                    ComponentType.ReadOnly<CurrentRoute>()
                },
                None = new ComponentType[] { ComponentType.ReadOnly<Deleted>() }
            });

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
            m_SimClock.RefreshIfDue(simulationFrame);
            Dependency = m_RailEtaService?.TickHot(simulationFrame, Dependency) ?? Dependency;
            m_RuntimeShell.Tick();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_RuntimeShell.Loaded(serializationContext);
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
                if (m_UICache.IsCreated) m_UICache.Dispose();
                if (m_LastEffectiveBoardingState.IsCreated) m_LastEffectiveBoardingState.Dispose();
                if (m_LastOfficialBoardingState.IsCreated) m_LastOfficialBoardingState.Dispose();
                if (m_BoardingFirstFrameGuardState.IsCreated) m_BoardingFirstFrameGuardState.Dispose();
                if (m_StopSessionLine.IsCreated) m_StopSessionLine.Dispose();
                if (m_StopSessionWaypointIndex.IsCreated) m_StopSessionWaypointIndex.Dispose();
                if (m_StopSessionArrivalFrame.IsCreated) m_StopSessionArrivalFrame.Dispose();
                if (m_StopSessionBoardingChangeCount.IsCreated) m_StopSessionBoardingChangeCount.Dispose();
                if (m_DeparturePendingSinceFrame.IsCreated) m_DeparturePendingSinceFrame.Dispose();
                if (m_CachedWpIdx.IsCreated) m_CachedWpIdx.Dispose();
                if (m_InvalidatedMidStopRecoveryPending.IsCreated) m_InvalidatedMidStopRecoveryPending.Dispose();
                if (m_BVMisfire.IsCreated) m_BVMisfire.Dispose();
                if (m_BVMisfireStartFrame.IsCreated) m_BVMisfireStartFrame.Dispose();
                if (m_ForcedMidStopBoardingGraceUntil.IsCreated) m_ForcedMidStopBoardingGraceUntil.Dispose();
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
