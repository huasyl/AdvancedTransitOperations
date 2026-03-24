// fix: IsSlotExpired 无法覆盖大幅过期班次，Holding 车卡死；调度器未过滤过期槽；Running->Idle 未清 targetMin；新线路不产车；UI 标签残留；关闭线路无效；始发站压队
// - IsSlotExpired 上限由 SLOT_INTERVAL(30) 改为 SPAWN_LEAD_MIN + SLOT_GRACE_MIN(64)，覆盖所有真过期场景
// - Holding 过期处理细分：overdue > SLOT_INTERVAL 直接回库，否则释放槽等重新分配
// - 调度器槽扫描入口加 IsSlotExpired 检查，过期槽直接跳过不参与分配
// - Running->Idle 时清理 m_VehicleTargetMin，防止旧槽值残留导致下帧直接 Idle->Holding 绕过调度保护
// - PuppetMaster：D=0 时改用 iDefault 兜底；每帧清理 m_SpawningLines 中已不存在的线路 Entity 记录
// - 注册新车时清理 m_UICache，防止旧线路缓存导致 SetUILabel 去重跳过、UI 标签不刷新
// - m_LineQuery 加 Disabled 过滤，关闭线路后调度器停止处理，现有车跑完当前圈自然 Idle 超时回库
// - 新增 m_NearingTerminus：车进入最后一个 waypoint 时打标签，Idle->Holding 前检查本线路有无标签车距始发站 <= 350 米，有则回库疏解

using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI;
using Game.UI.InGame;
using Game.Vehicles;
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

    public partial class DepartureControlSystem : GameSystemBase
    {
        private struct LineTimeProfileHeader
        {
            public ulong m_Signature;
            public float m_BaseLoopFrames;
            public int m_Offset;
            public int m_Count;
        }

        private struct StopDwellObservation
        {
            public float AverageFrames;
            public int SampleCount;
        }

        private struct StopDwellSession
        {
            public Entity Line;
            public int WaypointIndex;
            public uint StartFrame;

            public StopDwellSession(Entity line, int waypointIndex, uint startFrame)
            {
                Line = line;
                WaypointIndex = waypointIndex;
                StartFrame = startFrame;
            }
        }

        private struct BypassHoldCadenceSnapshot
        {
            public Entity Line;
            public int WaypointIndex;
            public Entity CurrentBypassBuilding;
            public Entity NextBypassBuilding;
            public uint EvaluatedFrame;
            public uint ReevaluateAfterFrame;
            public bool ShouldHold;
            public bool CanClearAfterExit;
            public Entity Blocker;

            public BypassHoldCadenceSnapshot(
                Entity line,
                int waypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding,
                uint evaluatedFrame,
                uint reevaluateAfterFrame,
                bool shouldHold,
                bool canClearAfterExit,
                Entity blocker)
            {
                Line = line;
                WaypointIndex = waypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
                EvaluatedFrame = evaluatedFrame;
                ReevaluateAfterFrame = reevaluateAfterFrame;
                ShouldHold = shouldHold;
                CanClearAfterExit = canClearAfterExit;
                Blocker = blocker;
            }
        }

        private readonly struct BypassControlScope
        {
            public readonly Entity Vehicle;
            public readonly Entity Line;
            public readonly int WaypointIndex;
            public readonly Entity CurrentBypassBuilding;
            public readonly Entity NextBypassBuilding;

            public BypassControlScope(
                Entity vehicle,
                Entity line,
                int waypointIndex,
                Entity currentBypassBuilding,
                Entity nextBypassBuilding)
            {
                Vehicle = vehicle;
                Line = line;
                WaypointIndex = waypointIndex;
                CurrentBypassBuilding = currentBypassBuilding;
                NextBypassBuilding = nextBypassBuilding;
            }
        }

        private readonly struct LineRunningVehicleSnapshot
        {
            public readonly Entity Vehicle;
            public readonly int NextWaypointIndex;
            public readonly bool Boarding;
            public readonly bool HasProjection;
            public readonly float ProjectionDistanceMeters;

            public LineRunningVehicleSnapshot(
                Entity vehicle,
                int nextWaypointIndex,
                bool boarding,
                bool hasProjection,
                float projectionDistanceMeters)
            {
                Vehicle = vehicle;
                NextWaypointIndex = nextWaypointIndex;
                Boarding = boarding;
                HasProjection = hasProjection;
                ProjectionDistanceMeters = projectionDistanceMeters;
            }
        }

        private sealed class LineRunningVehicleFrameSnapshot
        {
            public uint Frame;
            public Entity Line;
            public readonly List<LineRunningVehicleSnapshot> Vehicles = new List<LineRunningVehicleSnapshot>();
        }

        private sealed class LineMileageModel
        {
            public ulong Signature;
            public float TotalDistanceMeters;
            public float[] WaypointDistances = Array.Empty<float>();
            public float[] BypassWaypointDistances = Array.Empty<float>();
            public float[] BypassStopNodeDistances = Array.Empty<float>();
            public List<CorridorNode> CorridorNodes = new List<CorridorNode>();
            public Dictionary<Entity, float> BuildingDistances = new Dictionary<Entity, float>();
        }

        private struct CorridorNode
        {
            public Entity Building;
            public float DistanceMeters;
            public bool IsStopNode;
        }

        private sealed class SharedLocalCorridorGraph
        {
            public ulong Signature;
            public Dictionary<Entity, List<SharedLocalCorridorEdge>> Adjacency = new Dictionary<Entity, List<SharedLocalCorridorEdge>>();
        }

        private struct SharedLocalCorridorEdge
        {
            public Entity ToBuilding;
            public float DistanceMeters;
        }

        private struct LineDistanceProjection
        {
            public float TotalDistanceMeters;
            public float DistanceMeters;
            public float Progress01;
            public int NextWaypointIndex;
            public float SegmentPosition;
        }

        public struct SelectedPanelSnapshot
        {
            public string Mode;
            public string EntityId;
            public string PrimaryLabelKey;
            public string PrimaryValue;
            public string PrimaryValueKind;
            public string Detail1LabelKey;
            public string Detail1Value;
            public string Detail2LabelKey;
            public string Detail2Value;
            public string Detail3LabelKey;
            public string Detail3Value;
            public string Detail4LabelKey;
            public string Detail4Value;
            public string Detail5LabelKey;
            public string Detail5Value;
            public string Detail6LabelKey;
            public string Detail6Value;
            public string Detail7LabelKey;
            public string Detail7Value;
            public string AlertText;
            public bool ShowRetireAction;
            public bool ShowReevaluateAction;
            public bool ShowLineSpawnAction;
            public bool ShowDumpTrackModelAction;
            public bool ShowBypassStationToggle;
            public bool BypassStationChecked;
        }

        public static DepartureControlSystem Instance = null!;
        private TimedLogger log = Mod.log;
        private SimulationSystem m_SimulationSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private NameSystem m_NameSystem = null!;
        private EndFrameBarrier m_EndFrameBarrier = null!;
        private ulong m_PanelDataVersion = 1;
        private uint m_LastPanelVersionBucket;
        private const uint PANEL_VERSION_REFRESH_FRAMES = 256;

        // ── 车辆状态 ──
        private NativeHashMap<Entity, VehicleState> m_VehicleState;
        private NativeHashMap<Entity, int> m_VehicleTargetMin;
        private NativeHashMap<Entity, float> m_VehicleLapStartOdometer;
        private NativeHashMap<Entity, float> m_VehicleLapDistance;
        private NativeHashMap<Entity, uint> m_VehicleLapStartFrame;
        private NativeHashMap<Entity, uint> m_VehicleLapFrames;
        private NativeHashMap<Entity, uint> m_VehicleIdleStartFrame;
        private NativeHashMap<Entity, uint> m_VehiclePreparingStartFrame;
        private NativeHashMap<Entity, int> m_VehicleCurrentSlot;
        private NativeHashMap<Entity, uint> m_VehicleLastLaunchFrame;
        private NativeHashMap<Entity, FixedString64Bytes> m_UICache;
        private NativeHashMap<Entity, bool> m_LastBoarding;
        private NativeHashMap<Entity, int> m_CachedWpIdx;
        private NativeHashSet<Entity> m_BVMisfire;
        private NativeHashMap<Entity, uint> m_BVMisfireStartFrame;
        private NativeHashMap<Entity, uint> m_ForcedMidStopBoardingGraceUntil;
        private NativeHashMap<Entity, Entity> m_VehicleLine;
        /// <summary>
        /// 已进入最后一个 waypoint 的车辆集合。
        /// Idle 转 Holding 前检查本线路是否有此标签的车距始发站 350 米内，有则回库。
        /// </summary>
        private NativeHashSet<Entity> m_NearingTerminus;
        /// <summary>
        /// 发车冷却：发车后屏蔽 boarding 变化检测的截止帧。
        /// 防止车辆物理上尚未离开始发站时原生系统触发的假进站 / 假 BV 误写。
        /// </summary>
        private NativeHashMap<Entity, uint> m_LaunchCooldownUntil;
        private NativeHashMap<Entity, uint> m_LastRetireFixLogFrame;
        private NativeHashMap<Entity, uint> m_RetireFixCooldownUntil;
        private NativeHashMap<Entity, uint> m_PreparingFixCooldownUntil;
        private NativeHashMap<Entity, byte> m_RetireFixCount;
        // [修复2] 存档恢复后的 Running 车标记。UpdateLapStats 检测到后跳过写 m_VehicleLapFrames。
        // 防止倒推假起点算出的偏低圈时污染调度 ETA，导致系统静默。
        private NativeHashSet<Entity> m_RestoredRunning;
        private NativeHashMap<Entity, uint> m_OriginArrivalCandidateSinceFrame;
        private NativeHashMap<Entity, uint> m_ForcedOriginReadyFrame;
        private NativeHashMap<Entity, uint> m_StopDwellStartFrame;
        private NativeHashMap<Entity, uint> m_VehicleDispatchRequestStartFrame;
        private NativeHashMap<Entity, Entity> m_BypassYieldBlocker;
        private readonly Dictionary<ulong, StopDwellObservation> m_WaypointStopDwellObservations = new Dictionary<ulong, StopDwellObservation>();
        private readonly Dictionary<Entity, StopDwellSession> m_StopDwellSessions = new Dictionary<Entity, StopDwellSession>();
        private readonly Dictionary<Entity, string> m_LineLastSpawnTriggerSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastVehicleRegisterSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastHoldingSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LineLastDispatchSampleSummary = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, LineMileageModel> m_LineMileageModels = new Dictionary<Entity, LineMileageModel>();
        private SharedLocalCorridorGraph m_SharedLocalCorridorGraph;
        private readonly Dictionary<Entity, string> m_BypassDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_PreparingSlotLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_HoldingSkipLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LateDispatchLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BvMisfireObserveLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_DepartureObserveLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BvWaypointMismatchLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_BvWaypointMismatchLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, BypassHoldCadenceSnapshot> m_BypassHoldCadenceSnapshots = new Dictionary<Entity, BypassHoldCadenceSnapshot>();
        private readonly Dictionary<Entity, LineRunningVehicleFrameSnapshot> m_LineRunningVehicleFrameSnapshots = new Dictionary<Entity, LineRunningVehicleFrameSnapshot>();
        private bool m_CorridorModelFaulted = false;

        // ── 线路状态 ──
        private NativeHashMap<Entity, int> m_SpawningLines;
        private NativeHashMap<Entity, uint> m_LineSpawnRequestFrame;
        private NativeHashMap<Entity, uint> m_LastSpawnBlockedLogFrame;
        private NativeHashMap<ulong, uint> m_LastScheduleDiagnosticLogFrame;
        private NativeHashMap<Entity, ulong> m_LineWaypointSignature;
        private NativeHashMap<Entity, uint> m_LineStableSinceFrame;
        private NativeHashSet<Entity> m_LineInitialAdopted;
        private NativeHashMap<Entity, LineTimeProfileHeader> m_LineTimeProfiles;
        private NativeList<float> m_LineTimeProfileSegmentFrames;
        private NativeList<float> m_LineTimeProfileStopFrames;

        // ── 圈时持久化缓存 ──
        private CitySystem m_CitySystem = null!;
        /// <summary>Buffer 已挂到 City 实体，避免每帧重复调用 HasBuffer。</summary>
        private bool m_LapCacheBufferReady = false;
        private bool m_VehicleCacheBufferReady = false;
        private bool m_DispatchCacheBufferReady = false;
        private bool m_BypassStationBufferReady = false;
        private bool m_LineMileageBufferReady = false;

        // ── 帧级保护 ──
        private NativeHashSet<Entity> m_JustLaunched;

        // ── 诊断 ──
        private NativeHashSet<Entity> m_DiagnosedLines;

        // ── 启动稳定检测（仅启动阶段执行一次，通过后永久关闭）──
        private bool m_SystemReady = false;
        private bool m_StartupRuntimeStateCleared = false;
        private int m_StableFrameCount = 0;
        private int m_LastVehicleCount = -1;
        private int m_LastPuppetMasterMinute = -1;
        private int m_LastRegisterSweepMinute = -1;
        private int m_LastSchedulerTickMinute = -1;
        private const int STABLE_FRAMES_REQUIRED = 5;

        // ── 定期写缓存 ──
        private uint m_LastVehicleCacheFlushFrame = 0;
        private const uint VEHICLE_CACHE_FLUSH_INTERVAL = 300;

        private EntityQuery m_VehicleQuery;
        private EntityQuery m_AllPublicTransportQuery;
        private EntityQuery m_LineQuery;

        private const int SLOT_INTERVAL = 30;
        private const int SPAWN_LEAD_MIN = 60;
        private const float MAINTENANCE_THRESHOLD = 0.9f;
        private const int IDLE_TIMEOUT_MIN = 2;
        private const double SIM_FRAMES_PER_MINUTE = 182.044;
        private const float AT_STOP_MAX_DIST = 300f;
        /// <summary>班次宽限分钟数：发车窗口和过期判断共用同一阈值。</summary>
        private const int SLOT_GRACE_MIN = 4;
        /// <summary>BV 误写超时 6000 帧（约 33 秒现实时间），给足自愈窗口。</summary>
        private const uint BV_MISFIRE_TIMEOUT = 6000;
        /// <summary>暂时只观察 BV 误写，不再冻结车辆或回库；保留日志追踪后续是否能自愈。</summary>
        private static bool IsBvMisfireEnforcementEnabled() => false;
        /// <summary>发车后冷却帧数：屏蔽 boarding 变化检测，防假进站</summary>
        private const uint LAUNCH_COOLDOWN_FRAMES = 600;
        private const uint FORCED_MIDSTOP_BV_GRACE_FRAMES = 600;
        private const uint SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES = 1800;
        private const uint SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES = 1800;
        private const uint RETIREFIX_LOG_COOLDOWN_FRAMES = 1800;
        private const uint RETIREFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint PREPARINGFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES = 120;
        private const uint BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES = 5;
        private const uint BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES = 3;
        private const uint BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES = 60;
        private const byte RETIREFIX_DELETE_THRESHOLD = 3;
        private const float DISPATCH_ESTIMATE_MIN_MINUTES = 2f;
        private const float DISPATCH_ESTIMATE_MAX_MINUTES = 20f;
        private const float DISPATCH_FALLBACK_SPEED_M_PER_MIN = 450f;
        private const float PROFILE_STOP_START_BUFFER_MINUTES = 3f;
        private const float ORIGIN_CONGESTION_RADIUS_METERS = 450f;
        private const float ORIGIN_FORCE_IDLE_RADIUS_METERS = 180f;
        private const float ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS = 0.92f;
        private const uint ORIGIN_FORCE_IDLE_SETTLE_FRAMES = 180;
        private const float ORIGIN_ARRIVAL_HOLD_MINUTES = 2f;
        private static readonly uint FORCED_ORIGIN_MIN_DWELL_FRAMES = (uint)math.round(3f * (float)SIM_FRAMES_PER_MINUTE);
        private const float SPAWN_TRIGGER_BUFFER_SHORT_MINUTES = 10f;
        private const float SPAWN_TRIGGER_BUFFER_LONG_MINUTES = 15f;
        private const float SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES = 20f;
        private const int YIELD_PROTECT_MINUTES = 5;
        private const int LATE_DISPATCH_WINDOW_MINUTES = 8;
        private const float ETA_SCALE_MIN = 0.5f;
        private const float ETA_SCALE_MAX = 2.0f;
        private const uint NEW_LINE_STABLE_FRAMES = 300;
        private const int DISPATCH_SAMPLE_HISTORY_LIMIT = 8;
        private const float DISPATCH_SAMPLE_OUTLIER_FACTOR = 1.5f;
        private const uint BYPASS_YIELD_DECISION_COOLDOWN_FRAMES = 30;
        private const uint PREPARING_ROUTE_FIX_GRACE_FRAMES = 300;

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

            m_VehicleState = new NativeHashMap<Entity, VehicleState>(1024, Allocator.Persistent);
            m_VehicleTargetMin = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_VehicleLapStartOdometer = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_VehicleLapDistance = new NativeHashMap<Entity, float>(1024, Allocator.Persistent);
            m_VehicleLapStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_VehicleLapFrames = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_VehicleIdleStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_VehiclePreparingStartFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_VehicleCurrentSlot = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_VehicleLastLaunchFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_UICache = new NativeHashMap<Entity, FixedString64Bytes>(1024, Allocator.Persistent);
            m_LastBoarding = new NativeHashMap<Entity, bool>(1024, Allocator.Persistent);
            m_CachedWpIdx = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_BVMisfire = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_BVMisfireStartFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            m_ForcedMidStopBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_VehicleLine = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            m_LaunchCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_LastRetireFixLogFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_RetireFixCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_PreparingFixCooldownUntil = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            m_RetireFixCount = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            m_SpawningLines = new NativeHashMap<Entity, int>(64, Allocator.Persistent);
            m_LastSpawnBlockedLogFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            m_LastScheduleDiagnosticLogFrame = new NativeHashMap<ulong, uint>(256, Allocator.Persistent);
            m_LineWaypointSignature = new NativeHashMap<Entity, ulong>(64, Allocator.Persistent);
            m_LineStableSinceFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);
            m_LineInitialAdopted = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_LineTimeProfiles = new NativeHashMap<Entity, LineTimeProfileHeader>(64, Allocator.Persistent);
            m_LineTimeProfileSegmentFrames = new NativeList<float>(256, Allocator.Persistent);
            m_LineTimeProfileStopFrames = new NativeList<float>(256, Allocator.Persistent);
            m_JustLaunched = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_DiagnosedLines = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_NearingTerminus = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_RestoredRunning = new NativeHashSet<Entity>(64, Allocator.Persistent);
            m_OriginArrivalCandidateSinceFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_ForcedOriginReadyFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_StopDwellStartFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_VehicleDispatchRequestStartFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            m_BypassYieldBlocker = new NativeHashMap<Entity, Entity>(256, Allocator.Persistent);
            m_LineSpawnRequestFrame = new NativeHashMap<Entity, uint>(64, Allocator.Persistent);

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
                    ComponentType.ReadOnly<Disabled>()
                }
            });

            log.Info("=== RapidTransit v41.1 [VehicleCache] 启动 ===");
        }

        protected override void OnDestroy()
        {
            if (ReferenceEquals(Instance, this)) Instance = null!;
            if (m_VehicleState.IsCreated) m_VehicleState.Dispose();
            if (m_VehicleTargetMin.IsCreated) m_VehicleTargetMin.Dispose();
            if (m_VehicleLapStartOdometer.IsCreated) m_VehicleLapStartOdometer.Dispose();
            if (m_VehicleLapDistance.IsCreated) m_VehicleLapDistance.Dispose();
            if (m_VehicleLapStartFrame.IsCreated) m_VehicleLapStartFrame.Dispose();
            if (m_VehicleLapFrames.IsCreated) m_VehicleLapFrames.Dispose();
            if (m_VehicleIdleStartFrame.IsCreated) m_VehicleIdleStartFrame.Dispose();
            if (m_VehiclePreparingStartFrame.IsCreated) m_VehiclePreparingStartFrame.Dispose();
            if (m_VehicleCurrentSlot.IsCreated) m_VehicleCurrentSlot.Dispose();
            if (m_VehicleLastLaunchFrame.IsCreated) m_VehicleLastLaunchFrame.Dispose();
            if (m_UICache.IsCreated) m_UICache.Dispose();
            if (m_LastBoarding.IsCreated) m_LastBoarding.Dispose();
            if (m_CachedWpIdx.IsCreated) m_CachedWpIdx.Dispose();
            if (m_BVMisfire.IsCreated) m_BVMisfire.Dispose();
            if (m_BVMisfireStartFrame.IsCreated) m_BVMisfireStartFrame.Dispose();
            if (m_ForcedMidStopBoardingGraceUntil.IsCreated) m_ForcedMidStopBoardingGraceUntil.Dispose();
            if (m_VehicleLine.IsCreated) m_VehicleLine.Dispose();
            if (m_LaunchCooldownUntil.IsCreated) m_LaunchCooldownUntil.Dispose();
            if (m_LastRetireFixLogFrame.IsCreated) m_LastRetireFixLogFrame.Dispose();
            if (m_RetireFixCooldownUntil.IsCreated) m_RetireFixCooldownUntil.Dispose();
            if (m_PreparingFixCooldownUntil.IsCreated) m_PreparingFixCooldownUntil.Dispose();
            if (m_RetireFixCount.IsCreated) m_RetireFixCount.Dispose();
            if (m_SpawningLines.IsCreated) m_SpawningLines.Dispose();
            if (m_LastSpawnBlockedLogFrame.IsCreated) m_LastSpawnBlockedLogFrame.Dispose();
            if (m_LastScheduleDiagnosticLogFrame.IsCreated) m_LastScheduleDiagnosticLogFrame.Dispose();
            if (m_LineWaypointSignature.IsCreated) m_LineWaypointSignature.Dispose();
            if (m_LineStableSinceFrame.IsCreated) m_LineStableSinceFrame.Dispose();
            if (m_LineInitialAdopted.IsCreated) m_LineInitialAdopted.Dispose();
            if (m_LineTimeProfiles.IsCreated) m_LineTimeProfiles.Dispose();
            if (m_LineTimeProfileSegmentFrames.IsCreated) m_LineTimeProfileSegmentFrames.Dispose();
            if (m_LineTimeProfileStopFrames.IsCreated) m_LineTimeProfileStopFrames.Dispose();
            if (m_JustLaunched.IsCreated) m_JustLaunched.Dispose();
            if (m_DiagnosedLines.IsCreated) m_DiagnosedLines.Dispose();
            if (m_NearingTerminus.IsCreated) m_NearingTerminus.Dispose();
            if (m_RestoredRunning.IsCreated) m_RestoredRunning.Dispose();
            if (m_OriginArrivalCandidateSinceFrame.IsCreated) m_OriginArrivalCandidateSinceFrame.Dispose();
            if (m_ForcedOriginReadyFrame.IsCreated) m_ForcedOriginReadyFrame.Dispose();
            if (m_StopDwellStartFrame.IsCreated) m_StopDwellStartFrame.Dispose();
            if (m_VehicleDispatchRequestStartFrame.IsCreated) m_VehicleDispatchRequestStartFrame.Dispose();
            if (m_BypassYieldBlocker.IsCreated) m_BypassYieldBlocker.Dispose();
            if (m_LineSpawnRequestFrame.IsCreated) m_LineSpawnRequestFrame.Dispose();
            m_LineMileageModels.Clear();
            m_SharedLocalCorridorGraph = null;
            base.OnDestroy();
        }

        public bool DisplayDebugFor(Entity entity, Entity prefab)
        {
            if (entity == Entity.Null) return false;
            if (m_VehicleState.ContainsKey(entity)) return true;
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity)) return true;
            return false;
        }

        public string GetCurrentGameTimeLabel()
        {
            if (m_TimeSystem == null) return string.Empty;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            if (nowMin < 0) nowMin += 1440;
            return "[游戏时间 " + SlotStr(nowMin) + "]";
        }

        public void FillDebugInfo(Entity entity, InfoList list)
        {
            if (entity == Entity.Null) return;
            if (m_VehicleState.ContainsKey(entity))
            {
                FillVehicleDebugInfo(entity, list);
                return;
            }
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity))
                FillLineDebugInfo(entity, list);
        }

        public bool ShouldDisplaySelectedLineInfo(Entity entity, Entity preferredRoute = default)
        {
            return ResolveSelectedLineEntity(entity, preferredRoute) != Entity.Null;
        }

        public bool ShouldDisplaySelectedVehicleInfo(Entity entity)
        {
            return ResolveSelectedVehicleEntity(entity) != Entity.Null;
        }

        public bool IsManagedVehicle(Entity entity)
        {
            Entity resolvedVehicle = ResolveSelectedVehicleEntity(entity);
            return resolvedVehicle != Entity.Null && m_VehicleState.ContainsKey(resolvedVehicle);
        }

        public bool CanConfigureBypassStation(Entity entity)
        {
            return ResolvePassingStationBuilding(entity) != Entity.Null;
        }

        public bool IsBypassStation(Entity entity)
        {
            Entity building = ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            EnsureBypassStationBuffer();
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

        public bool RequestSetBypassStation(Entity entity, bool enabled)
        {
            Entity building = ResolvePassingStationBuilding(entity);
            if (building == Entity.Null)
                return false;

            EnsureBypassStationBuffer();
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<BypassStationSettingElement>(city))
                return false;

            DynamicBuffer<BypassStationSettingElement> buf = EntityManager.GetBuffer<BypassStationSettingElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_BuildingEntity != building)
                    continue;

                buf[i] = new BypassStationSettingElement
                {
                    m_BuildingEntity = building,
                    m_IsBypassStation = enabled ? (byte)1 : (byte)0
                };
                InvalidatePanelData();
                return true;
            }

            buf.Add(new BypassStationSettingElement
            {
                m_BuildingEntity = building,
                m_IsBypassStation = enabled ? (byte)1 : (byte)0
            });
            InvalidatePanelData();
            return true;
        }

        public ulong PanelDataVersion => m_PanelDataVersion;

        private void UpdatePanelDataVersionBucket()
        {
            uint bucket = m_SimulationSystem.frameIndex / PANEL_VERSION_REFRESH_FRAMES;
            if (bucket == m_LastPanelVersionBucket)
                return;

            m_LastPanelVersionBucket = bucket;
            m_PanelDataVersion++;
        }

        private void InvalidatePanelData()
        {
            m_PanelDataVersion++;
        }

        private Entity ResolveVehicleLine(Entity vehicle)
        {
            if (m_VehicleLine.TryGetValue(vehicle, out Entity mappedLine) && mappedLine != Entity.Null)
                return mappedLine;

            if (EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                if (route != Entity.Null && EntityManager.HasComponent<TransportLine>(route))
                    return route;
            }

            return Entity.Null;
        }

        private Entity ResolveSelectedLineEntity(Entity entity)
        {
            return ResolveSelectedLineEntity(entity, Entity.Null);
        }

        private Entity ResolveSelectedLineEntity(Entity entity, Entity preferredRoute)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;

            if (preferredRoute != Entity.Null
                && EntityManager.Exists(preferredRoute)
                && EntityManager.HasComponent<TransportLine>(preferredRoute))
            {
                return preferredRoute;
            }

            if (EntityManager.HasComponent<TransportLine>(entity))
                return entity;

            if (EntityManager.HasComponent<CurrentRoute>(entity))
            {
                Entity currentRoute = EntityManager.GetComponentData<CurrentRoute>(entity).m_Route;
                if (currentRoute != Entity.Null && EntityManager.HasComponent<TransportLine>(currentRoute))
                    return currentRoute;
            }

            var routes = new List<Entity>(4);
            TryGetStationRoutes(entity, routes);
            if (routes.Count == 0)
                TryGetStopRoutes(entity, routes);

            Entity bestRoute = Entity.Null;
            for (int i = 0; i < routes.Count; i++)
            {
                Entity route = routes[i];
                if (route == Entity.Null || !EntityManager.Exists(route) || !EntityManager.HasComponent<TransportLine>(route))
                    continue;
                if (bestRoute == Entity.Null || route.Index < bestRoute.Index)
                    bestRoute = route;
            }

            return bestRoute;
        }

        private bool TryGetStationRoutes(Entity entity, List<Entity> routes)
        {
            bool found = false;
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<Game.Objects.SubObject> subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                for (int i = 0; i < subObjects.Length; i++)
                    found |= TryGetStopRoutes(subObjects[i].m_SubObject, routes);
            }

            if (EntityManager.HasBuffer<InstalledUpgrade>(entity))
            {
                DynamicBuffer<InstalledUpgrade> upgrades = EntityManager.GetBuffer<InstalledUpgrade>(entity, true);
                for (int i = 0; i < upgrades.Length; i++)
                    found |= TryGetStationRoutes(upgrades[i].m_Upgrade, routes);
            }

            return found;
        }

        private bool TryGetStopRoutes(Entity entity, List<Entity> routes)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return false;
            if (!EntityManager.HasBuffer<ConnectedRoute>(entity))
                return false;
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(entity) && !EntityManager.HasComponent<Game.Routes.WorkStop>(entity))
                return false;
            if (EntityManager.HasComponent<TaxiStand>(entity))
                return false;

            DynamicBuffer<ConnectedRoute> connectedRoutes = EntityManager.GetBuffer<ConnectedRoute>(entity, true);
            bool found = false;
            for (int i = 0; i < connectedRoutes.Length; i++)
            {
                ConnectedRoute connectedRoute = connectedRoutes[i];
                if (!EntityManager.HasComponent<Owner>(connectedRoute.m_Waypoint))
                    continue;
                Entity owner = EntityManager.GetComponentData<Owner>(connectedRoute.m_Waypoint).m_Owner;
                if (owner == Entity.Null || !EntityManager.HasComponent<TransportLine>(owner))
                    continue;
                if (!routes.Contains(owner))
                    routes.Add(owner);
                found = true;
            }

            return found;
        }

        private Entity ResolvePassingStationBuilding(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;
            if (EntityManager.HasComponent<Building>(entity))
                return entity;

            Entity stopEntity = ResolveWorkbenchStopEntity(entity);
            if (stopEntity != Entity.Null)
            {
                Entity stationEntity = FindTransportStationFromStop(stopEntity);
                if (stationEntity != Entity.Null)
                    return stationEntity;
            }

            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Building>(current))
                    return current;
                if (!EntityManager.HasComponent<Owner>(current))
                    break;
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        private Entity ResolveSelectedVehicleEntity(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;

            Entity original = entity;
            Entity current = entity;
            Entity fallbackVehicle = Entity.Null;
            int guard = 0;
            while (current != Entity.Null && EntityManager.Exists(current) && guard++ < 16)
            {
                if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(current))
                    fallbackVehicle = current;

                if (m_VehicleState.ContainsKey(current))
                    return current;

                if (EntityManager.HasComponent<Controller>(current))
                {
                    Entity controller = EntityManager.GetComponentData<Controller>(current).m_Controller;
                    if (controller != Entity.Null && controller != current)
                    {
                        current = controller;
                        continue;
                    }
                }

                if (!EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            Entity layoutResolved = ResolveManagedVehicleFromLayout(original, fallbackVehicle);
            if (layoutResolved != Entity.Null)
                return layoutResolved;

            return fallbackVehicle;
        }

        private Entity ResolveManagedVehicleFromLayout(Entity original, Entity fallbackVehicle)
        {
            if (m_VehicleState.Count == 0)
                return Entity.Null;

            var managedVehicles = m_VehicleState.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < managedVehicles.Length; i++)
                {
                    Entity managedVehicle = managedVehicles[i];
                    if (!EntityManager.Exists(managedVehicle) || !EntityManager.HasBuffer<LayoutElement>(managedVehicle))
                        continue;

                    DynamicBuffer<LayoutElement> layout = EntityManager.GetBuffer<LayoutElement>(managedVehicle, true);
                    for (int j = 0; j < layout.Length; j++)
                    {
                        Entity layoutVehicle = layout[j].m_Vehicle;
                        if (layoutVehicle == original || (fallbackVehicle != Entity.Null && layoutVehicle == fallbackVehicle))
                            return managedVehicle;
                    }
                }
            }
            finally
            {
                managedVehicles.Dispose();
            }

            return Entity.Null;
        }

        private static string DescribeNativeVehicleState(PublicTransportFlags flags)
        {
            if ((flags & PublicTransportFlags.Disabled) != 0)
                return "Disabled";
            if ((flags & PublicTransportFlags.Returning) != 0)
                return "Returning";
            if ((flags & PublicTransportFlags.Arriving) != 0)
                return "Arriving";
            if ((flags & PublicTransportFlags.Boarding) != 0)
                return "Boarding";
            if ((flags & PublicTransportFlags.EnRoute) != 0)
                return "EnRoute";
            if ((flags & PublicTransportFlags.Launched) != 0)
                return "Launched";
            return "Assigned";
        }

        public void FillSelectedLineSummary(Entity line, out string summaryLabel, out string summaryValue)
        {
            if (!IsWorkbenchTimetableApplied(line))
            {
                summaryLabel = LocalizedDispatchLabel();
                summaryValue = LocalizedOfficialDispatchValue();
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            summaryLabel = LocalizedNextSlotLabel();
            int nextTarget = GetNextManagedDispatchTarget(line, nowMin);
            summaryValue = nextTarget >= 0 ? SlotStr(nextTarget) : "-";
        }

        public void FillSelectedVehicleSummary(Entity vehicle, out string summaryLabel, out string summaryValue)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            summaryLabel = "State";
            summaryValue = m_VehicleState.TryGetValue(vehicle, out var vehicleState) ? vehicleState.ToString() : "Unknown";
        }

        public void FillSelectedLineInfo(Entity line, InfoList list)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null)
                return;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int nearingTerminus = 0;
            int targetingNextSlot = 0;
            int occupyingNextSlot = 0;
            int total = 0;
            int spawning = 0;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var routeVehicles))
            {
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity vehicle = routeVehicles[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_NearingTerminus.Contains(vehicle))
                        nearingTerminus++;
                    if (m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) && targetSlot == nextSlot)
                        targetingNextSlot++;
                    if (m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot == nextSlot)
                        occupyingNextSlot++;

                    if (!m_VehicleState.TryGetValue(vehicle, out var state))
                        continue;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            preparing++;
                            break;
                        case VehicleState.Holding:
                            holding++;
                            break;
                        case VehicleState.Running:
                            running++;
                            break;
                        case VehicleState.Idle:
                            idle++;
                            break;
                        case VehicleState.Retiring:
                            retiring++;
                            break;
                    }
                }
            }

            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawning);
            string spawnTarget = isManagedLine ? spawning.ToString() : "-";
            string slotCoverage = isManagedLine
                ? ((targetingNextSlot + occupyingNextSlot) > 0 ? "Occupied" : "Gap")
                : LocalizedOfficialDispatchValue();
            string spawnTriggerSummary = m_LineLastSpawnTriggerSummary.TryGetValue(line, out string spawnTriggerText) ? spawnTriggerText : "-";
            string registerSummary = m_LineLastVehicleRegisterSummary.TryGetValue(line, out string registerText) ? registerText : "-";
            string holdingSummary = m_LineLastHoldingSummary.TryGetValue(line, out string holdingText) ? holdingText : "-";
            string dispatchSampleSummary = m_LineLastDispatchSampleSummary.TryGetValue(line, out string dispatchSampleText) ? dispatchSampleText : "-";
            string anomalies = BuildLineAlertSummary(
                line,
                isManagedLine ? (targetingNextSlot + occupyingNextSlot) : 0,
                nearingTerminus,
                lapCacheFrames,
                dispatchCacheFrames,
                spawning);

            AddDebugItem(list, "线路", "Line", line.Index.ToString());
            AddDebugItem(list, "当前时间", "Time", SlotStr(nowMin));
            if (isManagedLine)
                AddDebugItem(list, LocalizedNextSlotLabel(), "Next Slot", SlotStr(nextSlot));
            else
                AddDebugItem(list, LocalizedDispatchLabel(), "Dispatch", LocalizedOfficialDispatchValue());
            AddDebugItem(list, "车辆概览", "Fleet", total + " total / " + running + " running / " + holding + " holding");
            AddDebugItem(list, "状态分布", "States", "prep " + preparing + " / idle " + idle + " / retire " + retiring);
            if (isManagedLine)
                AddDebugItem(list, LocalizedNextSlotCoverageLabel(), "Next Slot Coverage", slotCoverage + " (" + targetingNextSlot + " target / " + occupyingNextSlot + " active)");
            else
                AddDebugItem(list, LocalizedNextSlotCoverageLabel(), "Next Slot Coverage", slotCoverage);
            AddDebugItem(list, "产车目标", "Spawn Target", spawnTarget);
            AddDebugItem(list, "圈时缓存", "Lap Cache", lapCache);
            AddDebugItem(list, LocalizedDispatchCacheLabel(), "Dispatch Cache", dispatchCache);
            AddDebugItem(list, "真实产车命令", "Spawn Command", spawnTriggerSummary);
            AddDebugItem(list, "新车注册", "Vehicle Register", registerSummary);
            AddDebugItem(list, "到站候车", "Arrival Holding", holdingSummary);
            AddDebugItem(list, "出库用时", "Dispatch Sample", dispatchSampleSummary);
            AddDebugItem(list, "关键异常", "Alerts", anomalies);
        }

        public void FillSelectedLineCard(
            Entity line,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null)
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Line: -";
                meta2 = "Selection is not a transport line";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
            int spawnPending = 0;
            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawnPending);
            bool hasWaypointData = EntityManager.HasBuffer<RouteWaypoint>(line);

            summaryLabel = isManagedLine ? LocalizedNextSlotLabel() : LocalizedDispatchLabel();
            summaryValue = isManagedLine ? SlotStr(nextSlot) : LocalizedOfficialDispatchValue();
            meta1 = "Time: " + SlotStr(nowMin);
            meta2 = isManagedLine
                ? "Lap: " + lapCache + " / Managed: " + BoolDebugStr(isManagedLine)
                : "Lap: - / Managed: " + BoolDebugStr(false);
            meta3 = isManagedLine
                ? (hasWaypointData
                    ? (IsChineseLocale() ? "出库：" : "Dispatch: ") + dispatchCache
                    : (IsChineseLocale() ? "出库：- / 路点缺失" : "Dispatch: - / Waypoints missing"))
                : (IsChineseLocale() ? "发车：官方调度" : "Dispatch: official dispatch");
            alertText = BuildLineAlertSummary(
                line,
                isManagedLine && spawnPending > 0 ? 1 : 0,
                0,
                lapCacheFrames,
                dispatchCacheFrames,
                spawnPending);
        }

        public void FillSelectedVehicleInfo(Entity vehicle, InfoList list)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
                return;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            string state = m_VehicleState.TryGetValue(vehicle, out var vehicleState)
                ? GetVehiclePanelStateCode(vehicle, vehicleState)
                : "Unknown";
            Entity line = ResolveVehicleLine(vehicle);
            string lineStr = line != Entity.Null ? line.Index.ToString() : "-";
            string targetStr = m_VehicleTargetMin.TryGetValue(vehicle, out int targetMin) && targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string progress = BuildVehicleProgressSummary(vehicle);
            string eta = EstimateVehicleEtaText(vehicle, line, vehicleState);
            string alerts = BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin);

            AddDebugItem(list, "车辆", "Vehicle", vehicle.Index.ToString());
            AddDebugItem(list, "状态", "State", state);
            AddDebugItem(list, "所属线路", "Line", lineStr);
            AddDebugItem(list, "目标班次", "Target Slot", targetStr);
            AddDebugItem(list, "当前班次", "Current Slot", currentStr);
            AddDebugItem(list, "到始发ETA", "ETA To Origin", eta);
            AddDebugItem(list, "运行进度", "Progress", progress);
            AddDebugItem(list, "关键异常", "Alerts", alerts);
            AddDebugItem(list, "可用控制", "Controls", "Retire and Re-evaluate are wired in backend");
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, out SelectedPanelSnapshot snapshot)
        {
            return TryBuildSelectedLineSnapshot(line, Entity.Null, out snapshot);
        }

        public bool TryBuildSelectedLineSnapshot(Entity line, Entity preferredRoute, out SelectedPanelSnapshot snapshot)
        {
            snapshot = default;
            Entity selectedEntity = line;
            line = ResolveSelectedLineEntity(line, preferredRoute);
            if (line == Entity.Null)
                return false;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : -1;
            if (isManagedLine)
                LogAppliedWorkbenchLineState(line, nowMin, nextSlot);
            float lapCacheFrames = isManagedLine ? ReadLineLapCache(line) : 0f;
            float dispatchCacheFrames = isManagedLine ? ReadDispatchCache(line) : 0f;
            int total = 0;
            int running = 0;
            int holding = 0;
            int idle = 0;
            int retiring = 0;
            int nextSlotOccupancy = 0;
            int nearingTerminus = 0;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var routeVehicles))
            {
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity vehicle = routeVehicles[i].m_Vehicle;
                    if (!EntityManager.Exists(vehicle))
                        continue;

                    total++;
                    if (m_NearingTerminus.Contains(vehicle))
                        nearingTerminus++;
                    if (isManagedLine && m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) && targetSlot == nextSlot)
                        nextSlotOccupancy++;
                    if (isManagedLine && m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot == nextSlot)
                        nextSlotOccupancy++;

                    if (!m_VehicleState.TryGetValue(vehicle, out var state))
                        continue;

                    switch (state)
                    {
                        case VehicleState.Running:
                            running++;
                            break;
                        case VehicleState.Holding:
                            holding++;
                            break;
                        case VehicleState.Idle:
                            idle++;
                            break;
                        case VehicleState.Retiring:
                            retiring++;
                            break;
                    }
                }
            }

            int spawnPending = 0;
            if (isManagedLine)
                m_SpawningLines.TryGetValue(line, out spawnPending);
            string spawnTriggerSummary = m_LineLastSpawnTriggerSummary.TryGetValue(line, out string spawnTriggerText) ? spawnTriggerText : "-";
            string registerSummary = m_LineLastVehicleRegisterSummary.TryGetValue(line, out string registerText) ? registerText : "-";
            string holdingSummary = m_LineLastHoldingSummary.TryGetValue(line, out string holdingText) ? holdingText : "-";
            string dispatchSampleSummary = m_LineLastDispatchSampleSummary.TryGetValue(line, out string dispatchSampleText) ? dispatchSampleText : "-";
            snapshot.Mode = "line";
            snapshot.EntityId = line.Index.ToString();
            snapshot.PrimaryLabelKey = isManagedLine ? "nextSlot" : "dispatch";
            snapshot.PrimaryValue = isManagedLine ? SlotStr(nextSlot) : LocalizedOfficialDispatchValue();
            snapshot.PrimaryValueKind = isManagedLine ? "slot" : "text";
            snapshot.Detail1LabelKey = IsChineseLocale() ? "线路编号" : "Line ID";
            snapshot.Detail1Value = line.Index.ToString();
            snapshot.Detail2LabelKey = IsChineseLocale() ? "当前时间" : "Time";
            snapshot.Detail2Value = SlotStr(nowMin);
            snapshot.Detail3LabelKey = IsChineseLocale() ? "真实产车命令" : "Spawn Command";
            snapshot.Detail3Value = spawnTriggerSummary;
            snapshot.Detail4LabelKey = IsChineseLocale() ? "新车注册" : "Vehicle Register";
            snapshot.Detail4Value = registerSummary;
            snapshot.Detail5LabelKey = IsChineseLocale() ? "到站候车" : "Arrival Holding";
            snapshot.Detail5Value = holdingSummary;
            snapshot.Detail6LabelKey = IsChineseLocale() ? "出库用时" : "Dispatch Sample";
            snapshot.Detail6Value = dispatchSampleSummary;
            snapshot.Detail7LabelKey = IsChineseLocale() ? "车辆概览" : "Fleet";
            snapshot.Detail7Value = total + " / " + running + " / " + holding;
            snapshot.AlertText = BuildLineAlertSummary(
                line,
                nextSlotOccupancy,
                nearingTerminus,
                lapCacheFrames,
                dispatchCacheFrames,
                spawnPending);
            snapshot.ShowLineSpawnAction = isManagedLine;
            snapshot.ShowDumpTrackModelAction = true;
            snapshot.ShowBypassStationToggle = CanConfigureBypassStation(selectedEntity);
            snapshot.BypassStationChecked = snapshot.ShowBypassStationToggle && IsBypassStation(selectedEntity);
            return true;
        }

        public bool TryBuildSelectedVehicleSnapshot(Entity vehicle, out SelectedPanelSnapshot snapshot)
        {
            snapshot = default;
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
                return false;

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedVehicle = m_VehicleState.TryGetValue(vehicle, out var vehicleState);
            PublicTransportFlags nativeFlags = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                : 0;
            Entity line = ResolveVehicleLine(vehicle);
            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) ? targetSlot : -1;
            int currentMin = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) ? currentSlot : -1;
            string state = isManagedVehicle ? GetVehiclePanelStateCode(vehicle, vehicleState) : DescribeNativeVehicleState(nativeFlags);
            string alertText = isManagedVehicle
                ? BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin)
                : (line != Entity.Null ? "using-native-fallback" : "vehicle-not-tracked");

            snapshot.Mode = "vehicle";
            snapshot.EntityId = vehicle.Index.ToString();
            snapshot.PrimaryLabelKey = "state";
            snapshot.PrimaryValue = state;
            snapshot.PrimaryValueKind = "state";
            snapshot.Detail1LabelKey = "line";
            snapshot.Detail1Value = line != Entity.Null ? line.Index.ToString() : "-";
            snapshot.Detail2LabelKey = "managed";
            snapshot.Detail2Value = isManagedVehicle ? "yes" : "no";
            snapshot.Detail3LabelKey = "currentSlot";
            snapshot.Detail3Value = currentMin >= 0 ? SlotStr(currentMin) : "-";
            snapshot.Detail4LabelKey = "targetSlot";
            snapshot.Detail4Value = targetMin >= 0 ? SlotStr(targetMin) : "-";
            snapshot.Detail5LabelKey = "stopDwell";
            snapshot.Detail5Value = BuildVehicleStopDwellValue(vehicle);
            snapshot.Detail6LabelKey = "inboundTime";
            snapshot.Detail6Value = BuildVehicleInboundTimeValue(vehicle);
            snapshot.AlertText = alertText;
            snapshot.ShowRetireAction = isManagedVehicle;
            snapshot.ShowReevaluateAction = isManagedVehicle;
            snapshot.ShowDumpTrackModelAction = true;
            return true;
        }

        public void FillSelectedVehicleCard(
            Entity vehicle,
            out string summaryLabel,
            out string summaryValue,
            out string meta1,
            out string meta2,
            out string meta3,
            out string alertText)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!ShouldDisplaySelectedVehicleInfo(vehicle))
            {
                summaryLabel = "State";
                summaryValue = "Unavailable";
                meta1 = "Vehicle: -";
                meta2 = "Selection is not a public transport vehicle";
                meta3 = string.Empty;
                alertText = "None";
                return;
            }

            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedVehicle = m_VehicleState.TryGetValue(vehicle, out var vehicleState);
            PublicTransportFlags nativeFlags = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State
                : 0;
            string state = isManagedVehicle ? GetVehiclePanelStateCode(vehicle, vehicleState) : DescribeNativeVehicleState(nativeFlags);
            Entity line = ResolveVehicleLine(vehicle);
            string lineStr = line != Entity.Null ? "#" + line.Index : "-";
            int targetMin = m_VehicleTargetMin.TryGetValue(vehicle, out int targetSlot) ? targetSlot : -1;
            string targetStr = targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(vehicle, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string stopDwell = BuildVehicleStopDwellValue(vehicle);
            string inboundTime = BuildVehicleInboundTimeValue(vehicle);

            summaryLabel = "State";
            summaryValue = state;
            meta1 = "Line: " + lineStr + " / Managed: " + BoolDebugStr(isManagedVehicle);
            meta2 = isManagedVehicle
                ? "Slot: " + currentStr + " -> " + targetStr
                : "Native: " + DescribeNativeVehicleState(nativeFlags);
            meta3 = IsChineseLocale()
                ? "停站计时：" + stopDwell + " / 入站时间：" + inboundTime
                : "Stop dwell: " + stopDwell + " / Inbound: " + inboundTime;
            alertText = isManagedVehicle
                ? BuildVehicleAlertSummary(vehicle, line, nowMin, targetMin)
                : (line != Entity.Null ? "Using native route fallback" : "Vehicle is not currently tracked by RapidTransit");
        }

        public bool RequestVehicleRetire(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
                return false;
            if (!EntityManager.HasComponent<Target>(vehicle))
                return false;

            Game.Vehicles.PublicTransport publicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);
            Target target = EntityManager.GetComponentData<Target>(vehicle);

            DoRetire(
                vehicle,
                publicTransport,
                target,
                m_EndFrameBarrier.CreateCommandBuffer(),
                "UI请求");
            InvalidatePanelData();
            return true;
        }

        public bool RequestVehicleReevaluate(Entity vehicle)
        {
            vehicle = ResolveSelectedVehicleEntity(vehicle);
            if (!IsManagedVehicle(vehicle))
                return false;

            m_VehicleTargetMin[vehicle] = -1;
            m_VehiclePreparingStartFrame.Remove(vehicle);
            m_VehicleDispatchRequestStartFrame.Remove(vehicle);
            m_VehicleIdleStartFrame.Remove(vehicle);
            InvalidatePanelData();
            return true;
        }

        private string GetVehiclePanelStateCode(Entity vehicle, VehicleState vehicleState)
        {
            if (m_BypassYieldBlocker.ContainsKey(vehicle)
                && (vehicleState == VehicleState.Holding || vehicleState == VehicleState.Running))
            {
                return "Yielding";
            }

            if (vehicleState == VehicleState.Holding
                && (!m_VehicleTargetMin.TryGetValue(vehicle, out int holdingTarget) || holdingTarget < 0))
            {
                return "Idle";
            }

            return vehicleState.ToString();
        }

        protected override void OnUpdate()
        {
            if (GameManager.instance.gameMode != GameMode.Game) return;
            UpdatePanelDataVersionBucket();

            if (Input.GetKey(KeyCode.LeftControl) &&
                Input.GetKey(KeyCode.LeftAlt) &&
                Input.GetKey(KeyCode.X))
            {
                SafeClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                TestSpawnRequest();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                SafeClearAll();
                return;
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                ForceRetireOne(m_EndFrameBarrier.CreateCommandBuffer());
                return;
            }

            if (!m_SystemReady)
            {
                if (!m_StartupRuntimeStateCleared)
                {
                    ClearRuntimeTrackingState();
                    m_StartupRuntimeStateCleared = true;
                }
                var rvBuffers0 = GetBufferLookup<RouteVehicle>(true);
                var lines0 = m_LineQuery.ToEntityArray(Allocator.Temp);
                int totalVehicles = 0;
                foreach (var line0 in lines0)
                {
                    if (rvBuffers0.TryGetBuffer(line0, out var rvs0))
                        totalVehicles += rvs0.Length;
                }
                lines0.Dispose();
                if (totalVehicles != m_LastVehicleCount)
                {
                    m_LastVehicleCount = totalVehicles;
                    m_StableFrameCount = 0;
                    return;
                }
                m_StableFrameCount++;
                if (m_StableFrameCount < STABLE_FRAMES_REQUIRED) return;
                m_SystemReady = true;
                log.Info("[启动] 稳定检测通过，系统就绪(车辆数=" + totalVehicles + ")");
            }

            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;

            EnsureLapCacheBuffer();
            EnsureVehicleCacheBuffer();
            EnsureDispatchCacheBuffer();
            bool runFullRegisterSweep = nowMin != m_LastRegisterSweepMinute;
            try
            {
                RegisterNewVehicles(runFullRegisterSweep);
                if (runFullRegisterSweep)
                    m_LastRegisterSweepMinute = nowMin;
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] RegisterNewVehicles -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            try
            {
                DriveStateMachine(ecb, nowMin);
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] DriveStateMachine -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            if (nowMin != m_LastPuppetMasterMinute)
            {
                try
                {
                    PuppetMasterControl(nowMin);
                    m_LastPuppetMasterMinute = nowMin;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] PuppetMasterControl -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
            if (nowMin != m_LastSchedulerTickMinute)
            {
                try
                {
                    SchedulerTick(ecb, nowMin);
                    m_LastSchedulerTickMinute = nowMin;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] SchedulerTick -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (nowFrame - m_LastVehicleCacheFlushFrame >= VEHICLE_CACHE_FLUSH_INTERVAL)
            {
                FlushAllVehicleStates();
                m_LastVehicleCacheFlushFrame = nowFrame;
            }
        }

        private void SafeClearAll()
        {
            var ecb = m_EndFrameBarrier.CreateCommandBuffer();
            var ents = m_AllPublicTransportQuery.ToEntityArray(Allocator.Temp);
            foreach (var e in ents)
            {
                if (!EntityManager.HasComponent<Game.Prefabs.PrefabData>(e))
                    ecb.AddComponent<Deleted>(e);
            }
            ents.Dispose();
            m_VehicleState.Clear();
            m_VehicleTargetMin.Clear();
            m_VehicleLapStartOdometer.Clear();
            m_VehicleLapDistance.Clear();
            m_VehicleLapStartFrame.Clear();
            m_VehicleLapFrames.Clear();
            m_VehicleIdleStartFrame.Clear();
            m_VehiclePreparingStartFrame.Clear();
            m_VehicleDispatchRequestStartFrame.Clear();
            m_VehicleCurrentSlot.Clear();
            m_VehicleLastLaunchFrame.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_VehicleLine.Clear();
            m_LaunchCooldownUntil.Clear();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_StopDwellStartFrame.Clear();
            m_StopDwellStartFrame.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_BypassHoldCadenceSnapshots.Clear();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_SystemReady = false;
            m_StartupRuntimeStateCleared = false;
            m_StableFrameCount = 0;
            m_LastVehicleCount = -1;
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            ClearLineDispatchDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[清场] 已清除所有公共交通车辆");
        }

        private void ClearRuntimeTrackingState()
        {
            m_VehicleState.Clear();
            m_VehicleTargetMin.Clear();
            m_VehicleLapStartOdometer.Clear();
            m_VehicleLapDistance.Clear();
            m_VehicleLapStartFrame.Clear();
            m_VehicleLapFrames.Clear();
            m_VehicleIdleStartFrame.Clear();
            m_VehiclePreparingStartFrame.Clear();
            m_VehicleDispatchRequestStartFrame.Clear();
            m_VehicleCurrentSlot.Clear();
            m_VehicleLastLaunchFrame.Clear();
            m_UICache.Clear();
            m_LastBoarding.Clear();
            m_CachedWpIdx.Clear();
            m_BVMisfire.Clear();
            m_BVMisfireStartFrame.Clear();
            m_ForcedMidStopBoardingGraceUntil.Clear();
            m_VehicleLine.Clear();
            m_LaunchCooldownUntil.Clear();
            m_LastRetireFixLogFrame.Clear();
            m_RetireFixCooldownUntil.Clear();
            m_PreparingFixCooldownUntil.Clear();
            m_RetireFixCount.Clear();
            m_SpawningLines.Clear();
            m_LineSpawnRequestFrame.Clear();
            m_LastSpawnBlockedLogFrame.Clear();
            m_LastScheduleDiagnosticLogFrame.Clear();
            ClearLineTimeProfiles();
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_NearingTerminus.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_BypassHoldCadenceSnapshots.Clear();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            ClearLineDispatchDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[启动] 已清空跨档运行态缓存");
        }

        private void ClearLineDispatchDebugSummaries()
        {
            m_LineLastSpawnTriggerSummary.Clear();
            m_LineLastVehicleRegisterSummary.Clear();
            m_LineLastHoldingSummary.Clear();
            m_LineLastDispatchSampleSummary.Clear();
        }

        private void RecordLineSpawnTriggerSummary(Entity line, int nowMin, int slot, int actualCount)
        {
            if (line == Entity.Null)
                return;

            m_LineLastSpawnTriggerSummary[line] = SlotStr(nowMin)
                + " 班次" + SlotStr(slot)
                + " 真实产车命令 当前=" + actualCount;
        }

        private void RecordLineVehicleRegisterSummary(Entity line, int nowMin, Entity vehicle, VehicleState finalState)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            string depotSummary = DescribeVehicleOwnerDepot(vehicle);
            m_LineLastVehicleRegisterSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 注册 -> " + finalState
                + " depot=" + depotSummary;
        }

        private void RecordLineHoldingSummary(Entity line, int nowMin, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || vehicle == Entity.Null)
                return;

            m_LineLastHoldingSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 到站/Holding"
                + (targetMin >= 0 ? " " + SlotStr(targetMin) : " 等待调度");
        }

        private void RecordLineDispatchSampleSummary(Entity line, int nowMin, Entity vehicle, float sampleMinutes)
        {
            if (line == Entity.Null || vehicle == Entity.Null || sampleMinutes <= 0f)
                return;

            m_LineLastDispatchSampleSummary[line] = SlotStr(nowMin)
                + " 车辆" + vehicle.Index
                + " 出库用时=" + sampleMinutes.ToString("F1") + "分钟";
        }

        private void TestSpawnRequest()
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    int actualCount = CountActiveVehicles(line, rvBuffers);
                    if (!m_SpawningLines.ContainsKey(line))
                    {
                        m_SpawningLines[line] = actualCount + 1;
                        m_LineSpawnRequestFrame[line] = m_SimulationSystem.frameIndex;
                        log.Info("[F8] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ")");
                    }
                    break;
                }
            }
            finally { lines.Dispose(); }
        }

        public bool RequestSpawnForLine(Entity line)
        {
            line = ResolveSelectedLineEntity(line);
            if (line == Entity.Null || !EntityManager.Exists(line) || !IsWorkbenchTimetableApplied(line))
                return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            int actualCount = CountActiveVehicles(line, rvBuffers);
            int pendingTarget = actualCount;
            if (m_SpawningLines.TryGetValue(line, out int existingTarget))
            {
                pendingTarget = math.max(existingTarget, actualCount);
            }

            int nextTarget = pendingTarget + 1;
            m_SpawningLines[line] = nextTarget;
            m_LineSpawnRequestFrame[line] = m_SimulationSystem.frameIndex;
            m_LineLastSpawnTriggerSummary[line] = SlotStr((int)(m_TimeSystem.normalizedTime * 1440f) % 1440)
                + " 手动发车 -> "
                + nextTarget.ToString();
            log.Info("[面板发车] 线路" + line.Index + " 触发产车+1 (当前=" + actualCount + ", 目标=" + nextTarget + ")");
            return true;
        }

        private string DescribeVehicleOwnerDepot(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Owner>(vehicle))
            {
                return "-";
            }

            Entity depot = EntityManager.GetComponentData<Owner>(vehicle).m_Owner;
            if (depot == Entity.Null || !EntityManager.Exists(depot))
                return "-";

            string name = m_NameSystem.GetRenderedLabelName(depot);
            return string.IsNullOrEmpty(name)
                ? "#" + depot.Index
                : ("#" + depot.Index + "[" + name + "]");
        }

        private void ForceRetireOne(EntityCommandBuffer ecb)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                        if (st == VehicleState.Retiring) continue;
                        var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        var tgt = EntityManager.GetComponentData<Target>(v);
                        log.Info("[F7] 线路" + line.Index + " 强制回库车辆" + v.Index + " (状态=" + st + ")");
                        DoRetire(v, pt, tgt, ecb, "F7强制");
                        return;
                    }
                    break;
                }
            }
            finally { lines.Dispose(); }
        }

        private void PuppetMasterControl(int nowMin)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var modBuffers = GetBufferLookup<RouteModifier>(false);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);

            try
            {
                var spawnKeys = m_SpawningLines.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < spawnKeys.Length; i++)
                {
                    if (!EntityManager.Exists(spawnKeys[i]))
                    {
                        log.Info("[PuppetMaster] 清理失效产车记录 线路" + spawnKeys[i].Index);
                        m_SpawningLines.Remove(spawnKeys[i]);
                        m_LineSpawnRequestFrame.Remove(spawnKeys[i]);
                        m_LastSpawnBlockedLogFrame.Remove(spawnKeys[i]);
                    }
                }
                spawnKeys.Dispose();

                var lineStableKeys = m_LineWaypointSignature.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < lineStableKeys.Length; i++)
                {
                    if (EntityManager.Exists(lineStableKeys[i])) continue;
                    m_LineWaypointSignature.Remove(lineStableKeys[i]);
                    m_LineStableSinceFrame.Remove(lineStableKeys[i]);
                    m_LineInitialAdopted.Remove(lineStableKeys[i]);
                    m_DiagnosedLines.Remove(lineStableKeys[i]);
                    InvalidateTrackModel(lineStableKeys[i]);
                    ClearLineTimeProfiles();
                }
                lineStableKeys.Dispose();

                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    if (!EntityManager.HasComponent<PrefabRef>(line)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                    if (!EntityManager.HasComponent<TransportLineData>(prefab)) continue;
                    if (!modBuffers.TryGetBuffer(line, out var mods)) continue;

                    float iDefault = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultVehicleInterval;
                    float D = CalculateLineDuration(line);
                    if (D <= 0f) D = iDefault;
                    if (D <= 0f) continue;

                    int actualCount = CountActiveVehicles(line, rvBuffers);
                    int nTarget = actualCount;

                    if (m_SpawningLines.TryGetValue(line, out int spawnTarget))
                    {
                        if (actualCount >= spawnTarget)
                        {
                            m_SpawningLines.Remove(line);
                            m_LineSpawnRequestFrame.Remove(line);
                            log.Info("[PuppetMaster] 线路" + line.Index + " 产车完成 actualCount=" + actualCount);
                        }
                        else
                        {
                            nTarget = spawnTarget;
                        }
                    }

                    float targetInterval;
                    if (nTarget <= 0)
                    {
                        // Allow true zero-vehicle lines by stretching the native interval far beyond
                        // a normal lap, instead of forcing the game to keep one vehicle alive.
                        targetInterval = math.max(iDefault, D) * 64f;
                    }
                    else
                    {
                        targetInterval = D / nTarget;
                    }
                    float delta = targetInterval - iDefault;
                    InjectModifier(mods, delta);
                }
            }
            finally { lines.Dispose(); }
        }

        private void SchedulerTick(EntityCommandBuffer ecb, int nowMin)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);

            try
            {
                foreach (var line in lines)
                {
                    if (!EntityManager.Exists(line)) continue;
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    bool useWorkbenchSchedule = IsWorkbenchTimetableApplied(line);
                    int[] appliedTargets = useWorkbenchSchedule
                        ? GetAppliedWorkbenchDepartureMinutes(line)
                        : null;
                    if (useWorkbenchSchedule && (appliedTargets == null || appliedTargets.Length == 0))
                        continue;
                    int originHoldLimitMinutes = useWorkbenchSchedule
                        ? GetWorkbenchOriginHoldLimitMinutes(line)
                        : SPAWN_LEAD_MIN;

                    uint nowFrame = m_SimulationSystem.frameIndex;
                    string lineTag = "线路" + line.Index;
                    float cachedLapFrames = ReadLineLapCache(line);

                    bool lineHasHistory = false;
                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v0 = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v0)) continue;
                        if (m_VehicleLapFrames.TryGetValue(v0, out uint lf0) && lf0 > 0)
                        {
                            lineHasHistory = true;
                            break;
                        }
                    }
                    if (!lineHasHistory && cachedLapFrames > 0f)
                        lineHasHistory = true;

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                        if (st != VehicleState.Idle && st != VehicleState.Holding) continue;
                        if (!NeedsMaintenance(v) && CanFinishNextLap(v)) continue;
                        var pt2 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        var tgt2 = EntityManager.GetComponentData<Target>(v);
                        DoRetire(v, pt2, tgt2, ecb, "在站维护/里程不足");
                    }

                    int slot = useWorkbenchSchedule && appliedTargets.Length > 0 ? appliedTargets[0] : NextSlotMin(nowMin);
                    int maxSlots = useWorkbenchSchedule
                        ? appliedTargets.Length
                        : SPAWN_LEAD_MIN / SLOT_INTERVAL + 1;
                    int dispatchCycleMinutes = useWorkbenchSchedule
                        ? GetScheduledHeadwayMinutes(appliedTargets)
                        : SLOT_INTERVAL;
                    int nextAppliedTargetIndex = useWorkbenchSchedule
                        ? GetNextScheduledTargetIndex(nowMin, appliedTargets)
                        : -1;
                    int previousAppliedTarget = useWorkbenchSchedule
                        ? GetPreviousScheduledTargetMin(nowMin, appliedTargets)
                        : -1;

                    float lineDurationFrames = 0f;
                    {
                        float maxLapFrames = 0f;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v0 = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v0)) continue;
                            if (m_VehicleLapFrames.TryGetValue(v0, out uint lf0) && lf0 > maxLapFrames)
                                maxLapFrames = lf0;
                        }
                        if (maxLapFrames > 0)
                        {
                            lineDurationFrames = maxLapFrames;
                        }
                        else
                        {
                            if (cachedLapFrames > 0f)
                            {
                                lineDurationFrames = cachedLapFrames;
                                for (int i = 0; i < rvs.Length; i++)
                                {
                                    Entity v0 = rvs[i].m_Vehicle;
                                    if (!EntityManager.Exists(v0)) continue;
                                    if (!m_VehicleLapFrames.TryGetValue(v0, out uint lf0) || lf0 == 0)
                                        m_VehicleLapFrames[v0] = (uint)cachedLapFrames;
                                }
                            }
                            else
                            {
                                lineDurationFrames = CalculateLineDuration(line) * 60f;
                            }
                        }
                    }

                    for (int s = useWorkbenchSchedule ? -1 : 0; s < maxSlots; s++)
                    {
                        if (useWorkbenchSchedule)
                        {
                            if (s < 0)
                            {
                                slot = previousAppliedTarget;
                                if (slot < 0)
                                    continue;
                            }
                            else
                            {
                                int slotIndex = (nextAppliedTargetIndex + s) % appliedTargets.Length;
                                slot = appliedTargets[slotIndex];
                                if (slot == previousAppliedTarget)
                                    continue;
                            }
                        }

                        int minsToSlot = useWorkbenchSchedule
                            ? GetDispatchLeadMinutes(nowMin, slot)
                            : MinutesUntil(nowMin, slot);
                        if (IsSlotExpired(nowMin, slot))
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (minsToSlot > originHoldLimitMinutes)
                        {
                            if (useWorkbenchSchedule && s >= 0)
                                break;
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        Entity currentOccupier = Entity.Null;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleCurrentSlot.TryGetValue(v, out int vcs) || vcs != slot) continue;
                            if (!m_VehicleState.TryGetValue(v, out var currentState)) continue;
                            if (currentState != VehicleState.Running) continue;
                            currentOccupier = v;
                            break;
                        }
                        if (currentOccupier != Entity.Null)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        Entity currentHolder = Entity.Null;
                        int currentHolderTier = 99;
                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleTargetMin.TryGetValue(v, out int vtm) || vtm != slot) continue;
                            currentHolder = v;
                            if (m_VehicleState.TryGetValue(v, out var hst) &&
                                (hst == VehicleState.Idle || hst == VehicleState.Holding))
                                currentHolderTier = 0;
                            else
                                currentHolderTier = 1;
                            break;
                        }
                        if (currentHolder != Entity.Null && currentHolderTier == 0)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }
                        if (currentHolder != Entity.Null && currentHolderTier == 1
                            && m_VehicleState.TryGetValue(currentHolder, out var holderState)
                            && holderState == VehicleState.Running)
                        {
                            float holderEta = EstimateRunningArrivalFrames(currentHolder, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                            if (ShouldHoldSpawnForNearestRunningCandidate(currentHolder, holderState, holderEta, wps))
                            {
                                TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                slot = (slot + SLOT_INTERVAL) % 1440;
                                continue;
                            }
                        }

                        float slotFramesAway = minsToSlot * (float)SIM_FRAMES_PER_MINUTE;
                        if (useWorkbenchSchedule)
                            slotFramesAway = GetDispatchLeadMinutes(nowMin, slot) * (float)SIM_FRAMES_PER_MINUTE;

                        Entity bestVehicle = Entity.Null;
                        int bestTier = 99;
                        float bestETA = float.MaxValue;
                        float bestRemaining = -1f;
                        int bestPrevTarget = -1;
                        Entity nearestVehicle = Entity.Null;
                        VehicleState nearestState = VehicleState.Preparing;
                        float nearestETA = float.MaxValue;
                        string nearestReason = "none";

                        for (int i = 0; i < rvs.Length; i++)
                        {
                            Entity v = rvs[i].m_Vehicle;
                            if (!EntityManager.Exists(v)) continue;
                            if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                            if (st == VehicleState.Retiring) continue;
                            if (m_BVMisfire.Contains(v)) continue;
                            int assignedTarget = -1;
                            if (m_VehicleTargetMin.TryGetValue(v, out int vtm) && vtm >= 0)
                            {
                                assignedTarget = vtm;
                                if (vtm != slot)
                                {
                                    if (IsCurrentOrRecentDispatchableSlot(nowMin, vtm))
                                        continue;
                                    int minsToAssigned = MinutesUntil(nowMin, vtm);
                                    if (minsToAssigned <= minsToSlot)
                                        continue;
                                }
                            }
                            if (st == VehicleState.Running
                                && assignedTarget >= 0
                                && assignedTarget != slot
                                && IsCurrentOrRecentDispatchableSlot(nowMin, assignedTarget)
                                && IsBorderlineOriginArrivalCandidate(v, wps))
                            {
                                continue;
                            }
                            if (NeedsMaintenance(v) || !CanFinishNextLap(v)) continue;

                            int tier = 99;
                            float eta = float.MaxValue;

                            if (st == VehicleState.Idle || st == VehicleState.Holding)
                            {
                                int cachedIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                                if (cachedIdx != 0)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = 0f;
                                        nearestReason = "cachedWp=" + cachedIdx;
                                    }
                                    continue;
                                }
                                tier = 0;
                                eta = 0f;
                            }
                            else if (st == VehicleState.Running)
                            {
                                float etaFrames = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = float.MaxValue;
                                        nearestReason = "no-running-eta";
                                    }
                                    continue;
                                }
                                tier = 1;
                                eta = etaFrames;
                            }
                            else if (st == VehicleState.Preparing)
                            {
                                float etaFrames = EstimatePreparingArrivalFrames(v, line, nowFrame, lineDurationFrames);
                                if (etaFrames == float.MaxValue)
                                {
                                    if (nearestVehicle == Entity.Null)
                                    {
                                        nearestVehicle = v;
                                        nearestState = st;
                                        nearestETA = float.MaxValue;
                                        nearestReason = "no-preparing-eta";
                                    }
                                    continue;
                                }
                                tier = 1;
                                eta = etaFrames;
                            }
                            else
                            {
                                continue;
                            }

                            if (eta > slotFramesAway)
                            {
                                if (eta < nearestETA || nearestVehicle == Entity.Null)
                                {
                                    nearestVehicle = v;
                                    nearestState = st;
                                    nearestETA = eta;
                                    nearestReason = "late-for-slot";
                                }
                                continue;
                            }

                            float remaining = GetRemainingRange(v);
                            bool better = (tier < bestTier)
                                       || (tier == bestTier && eta < bestETA)
                                       || (tier == bestTier && eta == bestETA && remaining > bestRemaining);
                            if (better)
                            {
                                bestVehicle = v;
                                bestTier = tier;
                                bestETA = eta;
                                bestRemaining = remaining;
                                bestPrevTarget = assignedTarget;
                            }
                        }

                        if (currentHolder != Entity.Null && bestVehicle == currentHolder)
                        {
                            slot = (slot + SLOT_INTERVAL) % 1440;
                            continue;
                        }

                        if (bestVehicle != Entity.Null)
                        {
                            var bst = m_VehicleState[bestVehicle];
                            if (bst == VehicleState.Idle || bst == VehicleState.Holding)
                            {
                                if (bestPrevTarget < 0)
                                {
                                    int holdingCount = 0;
                                    for (int i = 0; i < rvs.Length; i++)
                                    {
                                        Entity rv = rvs[i].m_Vehicle;
                                        if (!EntityManager.Exists(rv)) continue;
                                        if (!m_VehicleState.TryGetValue(rv, out var rst)) continue;
                                        if (rst == VehicleState.Holding) { holdingCount++; continue; }
                                        if (rst == VehicleState.Idle
                                            && m_VehicleTargetMin.TryGetValue(rv, out int rvm) && rvm >= 0)
                                            holdingCount++;
                                    }
                                    int holdingCap = Math.Max(1, originHoldLimitMinutes / dispatchCycleMinutes);
                                    if (holdingCount >= holdingCap)
                                    {
                                        slot = (slot + SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }
                                if (currentHolder != Entity.Null)
                                    m_VehicleTargetMin[currentHolder] = -1;
                                AssignSlot(bestVehicle, slot, ecb);
                                m_VehicleIdleStartFrame.Remove(bestVehicle);
                            }
                            else
                            {
                                if (currentHolder != Entity.Null)
                                    m_VehicleTargetMin[currentHolder] = -1;
                                m_VehicleTargetMin[bestVehicle] = slot;
                                log.Info("[调度候选] " + lineTag + " 班次" + SlotStr(slot)
                                    + " 选择车辆" + bestVehicle.Index
                                    + " state=" + bst
                                    + " eta=" + (bestETA / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                                    + " prevTarget=" + (bestPrevTarget >= 0 ? SlotStr(bestPrevTarget) : "-"));
                            }
                        }
                        else
                        {
                            bool hasIdleOrHoldingUnassigned = false;
                            for (int i = 0; i < rvs.Length; i++)
                            {
                                Entity v = rvs[i].m_Vehicle;
                                if (!EntityManager.Exists(v)) continue;
                                if (!m_VehicleState.TryGetValue(v, out var st2)) continue;
                                if (st2 != VehicleState.Holding && st2 != VehicleState.Idle) continue;
                                if (m_VehicleTargetMin.TryGetValue(v, out int vtm2) && vtm2 >= 0) continue;
                                hasIdleOrHoldingUnassigned = true;
                                break;
                            }
                            if (hasIdleOrHoldingUnassigned)
                            {
                                slot = (slot + SLOT_INTERVAL) % 1440;
                                continue;
                            }

                            int canMakeItCount = 0;
                            int inTransitCount = 0;

                            for (int i = 0; i < rvs.Length; i++)
                            {
                                Entity v = rvs[i].m_Vehicle;
                                if (!EntityManager.Exists(v)) continue;
                                if (!m_VehicleState.TryGetValue(v, out var st2)) continue;
                                if (st2 != VehicleState.Preparing && st2 != VehicleState.Running) continue;
                                inTransitCount++;
                                if (m_VehicleTargetMin.TryGetValue(v, out int vtm2) && vtm2 >= 0) continue;

                                float etaF = float.MaxValue;
                                if (st2 == VehicleState.Running)
                                {
                                    etaF = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                                    if (etaF == float.MaxValue)
                                        continue;
                                }
                                else
                                {
                                    etaF = EstimatePreparingArrivalFrames(v, line, nowFrame, lineDurationFrames);
                                    if (etaF == float.MaxValue)
                                        continue;
                                }

                                if (etaF <= slotFramesAway)
                                    canMakeItCount++;
                            }

                            if (canMakeItCount == 0 && !m_SpawningLines.ContainsKey(line))
                            {
                                if (nearestVehicle != Entity.Null)
                                {
                                    string etaText = nearestETA == float.MaxValue
                                        ? "?"
                                        : (nearestETA / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟";
                                    TryLogScheduleDiagnostic(line, lineTag, slot, nearestVehicle, nearestState, etaText, nearestReason);
                                }
                                if (ShouldHoldSpawnForNearestRunningCandidate(nearestVehicle, nearestState, nearestETA, wps))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (HasInboundVehicleNearOrigin(line, wps, Entity.Null, ORIGIN_CONGESTION_RADIUS_METERS))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }
                                if (HasBorderlineOriginArrivalCandidate(line, wps, slotFramesAway, lineDurationFrames, lineHasHistory))
                                {
                                    TryLogSpawnBlocked(line, lineTag, nowMin, slot);
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                float spawnLeadFrames = EstimateSpawnLeadFrames(line, lineDurationFrames);
                                float spawnTriggerFrames = spawnLeadFrames
                                    + GetSpawnTriggerBufferMinutes(spawnLeadFrames) * (float)SIM_FRAMES_PER_MINUTE;
                                if (slotFramesAway > spawnTriggerFrames)
                                {
                                    slot = (slot + SLOT_INTERVAL) % 1440;
                                    continue;
                                }

                                if (lineDurationFrames > 0f)
                                {
                                    int theoreticalCount = (int)math.ceil(
                                        lineDurationFrames / (dispatchCycleMinutes * (float)SIM_FRAMES_PER_MINUTE));
                                    int actualCountForCap = CountActiveVehicles(line, rvBuffers);
                                    if (actualCountForCap >= theoreticalCount)
                                    {
                                        slot = (slot + SLOT_INTERVAL) % 1440;
                                        continue;
                                    }
                                }

                                int actualCount = CountActiveVehicles(line, rvBuffers);
                                m_SpawningLines[line] = actualCount + 1;
                                m_LineSpawnRequestFrame[line] = nowFrame;
                                RecordLineSpawnTriggerSummary(line, nowMin, slot, actualCount);
                                log.Info("[调度] " + lineTag + " 班次" + SlotStr(slot)
                                    + " 无候选，触发产车+1 (当前=" + actualCount
                                    + " 圈时=" + (lineDurationFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟)");
                            }
                        }

                        slot = (slot + SLOT_INTERVAL) % 1440;
                    }
                }
            }
            finally { lines.Dispose(); }
        }

        private void RegisterNewVehicles(bool fullSweep)
        {
            var lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            try
            {
                foreach (var line in lines)
                {
                    if (!rvBuffers.TryGetBuffer(line, out var rvs)) continue;
                    if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length < 2) continue;
                    if (!IsLineStable(line, wps)) continue;
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    bool adoptExistingVehicles = !m_LineInitialAdopted.Contains(line);
                    bool isHotLine = adoptExistingVehicles || m_SpawningLines.ContainsKey(line);
                    if (!fullSweep && !isHotLine) continue;

                    string lineTag = "线路" + line.Index;

                    if (!m_DiagnosedLines.Contains(line))
                    {
                        m_DiagnosedLines.Add(line);
                        LogLineTrackChainDiagnostics(line);
                        string lineName = m_NameSystem.GetRenderedLabelName(line);
                        log.Info("[诊断] " + lineTag + " (" + lineName + ") waypoint数=" + wps.Length);
                    }

                    for (int i = 0; i < rvs.Length; i++)
                    {
                        Entity v = rvs[i].m_Vehicle;
                        if (!EntityManager.Exists(v)) continue;
                        if (m_VehicleState.ContainsKey(v)) continue;

                        var pt0 = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        bool boarding0 = (pt0.m_State & PublicTransportFlags.Boarding) != 0;

                        int initWpIdx = boarding0 ? ComputeWpIndex(v, wps) : -1;
                        bool atA0 = (initWpIdx == 0);

                        string initReason;
                        var initState = InferInitialVehicleState(v, wps, pt0, boarding0, initWpIdx, adoptExistingVehicles, out initReason);
                        m_VehicleState[v] = initState;
                        m_VehicleTargetMin[v] = -1;
                        m_VehicleLapDistance[v] = -1f;
                        if (initState == VehicleState.Preparing)
                            m_VehiclePreparingStartFrame[v] = m_SimulationSystem.frameIndex;
                        else
                            m_VehiclePreparingStartFrame.Remove(v);
                        if (!adoptExistingVehicles
                            && m_LineSpawnRequestFrame.TryGetValue(line, out uint spawnRequestFrame))
                        {
                            m_VehicleDispatchRequestStartFrame[v] = spawnRequestFrame;
                            m_LineSpawnRequestFrame.Remove(line);
                        }
                        else
                        {
                            m_VehicleDispatchRequestStartFrame.Remove(v);
                        }
                        m_LastBoarding[v] = boarding0;
                        m_CachedWpIdx[v] = initWpIdx;
                        m_VehicleLine[v] = line;
                        m_UICache.Remove(v);
                        ClearVehicleProgressSuspect(v, "register-reset");
                        if (initReason == "boarding-midway")
                            MarkVehicleProgressSuspect(v, initReason);

                        if (boarding0 && initWpIdx < 0)
                        {
                            ObserveBvMisfireCandidate(
                                v,
                                "线路" + line.Index,
                                "register",
                                "boarding-without-waypoint",
                                m_SimulationSystem.frameIndex);
                        }

                        bool preferOriginHolding = initState == VehicleState.Holding
                            && (initReason == "at-origin"
                                || initReason == "boarding-origin-fallback"
                                || initReason.StartsWith("route-progress-origin-fallback"));
                        bool restored = TryRestoreVehicleState(v, line, !preferOriginHolding);
                        if (!restored && initState == VehicleState.Running)
                            restored = RestoreRunningContextFromProgress(v, line, wps, initReason);
                        VehicleState finalState = m_VehicleState[v];
                        int finalTarget = m_VehicleTargetMin.TryGetValue(v, out int ft) ? ft : -1;
                        if (finalState == VehicleState.Holding)
                            TryRecordPreparingArrivalSample(v, line, m_SimulationSystem.frameIndex);

                        if (finalState == VehicleState.Running)
                            SetUILabel(v, "运行中" + (finalTarget >= 0 ? " " + SlotStr(finalTarget) : ""));
                        else if (finalState == VehicleState.Holding)
                            SetUILabel(v, finalTarget >= 0 ? "候车 " + SlotStr(finalTarget) : "候车 等待调度");
                        else
                            SetUILabel(v, atA0 ? "候车 等待调度" : "前往始发站");

                        log.Info("[注册] " + lineTag + " 车辆" + v.Index
                            + " 初始:" + initState + " 最终:" + finalState
                            + (restored ? "(缓存恢复)" : "")
                            + " targetMin=" + finalTarget
                            + " initReason=" + initReason
                            + " depot=" + DescribeVehicleOwnerDepot(v));
                        if (!adoptExistingVehicles)
                        {
                            log.Info("[OfficialSpawnResult] line=" + line.Index
                                + " vehicle=" + v.Index
                                + " state=" + finalState
                                + " targetMin=" + finalTarget
                                + " initReason=" + initReason
                                + " depot=" + DescribeVehicleOwnerDepot(v));
                        }
                        if (!adoptExistingVehicles)
                            RecordLineVehicleRegisterSummary(line, (int)(m_TimeSystem.normalizedTime * 1440f) % 1440, v, finalState);
                    }
                    if (adoptExistingVehicles)
                        m_LineInitialAdopted.Add(line);
                }
            }
            finally { lines.Dispose(); }
        }

        private void DriveStateMachine(EntityCommandBuffer ecb, int nowMin)
        {
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            var vehicles = m_VehicleQuery.ToEntityArray(Allocator.Temp);

            try
            {
                foreach (var v in vehicles)
                {
                    if (!EntityManager.Exists(v)) continue;
                    Entity line = ResolveVehicleLine(v);
                    if (!IsWorkbenchTimetableApplied(line)) continue;
                    if (!m_VehicleState.TryGetValue(v, out var state)) continue;

                    var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                    var tgt = EntityManager.GetComponentData<Target>(v);
                    var cr = EntityManager.GetComponentData<CurrentRoute>(v);

                    if (!wpBuffers.TryGetBuffer(cr.m_Route, out var wps) || wps.Length < 2) continue;

                    bool boarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
                    int targetMin = m_VehicleTargetMin.TryGetValue(v, out int tm) ? tm : -1;
                    uint nowFrame = m_SimulationSystem.frameIndex;
                    bool suppressForcedMidStopBoardingGhost = state == VehicleState.Running
                        && boarding
                        && IsSuppressedForcedMidStopBoardingGhost(v, tgt, wps, nowFrame, out _);
                    if (suppressForcedMidStopBoardingGhost)
                    {
                        boarding = false;
                        m_BVMisfire.Remove(v);
                        m_BVMisfireStartFrame.Remove(v);
                    }

                    string lineTag = m_VehicleLine.TryGetValue(v, out Entity lineEnt)
                        ? "线路" + lineEnt.Index : "线路?";
                    bool allowOriginHoldingBoardingGhost = state == VehicleState.Holding
                        && targetMin >= 0
                        && GetDistanceToOriginMeters(v, wps) <= ORIGIN_FORCE_IDLE_RADIUS_METERS;

                    if (!IsBvMisfireEnforcementEnabled() && m_BVMisfire.Contains(v))
                    {
                        m_BVMisfire.Remove(v);
                        m_BVMisfireStartFrame.Remove(v);
                    }

                    if (m_BVMisfire.Contains(v))
                    {
                        if (allowOriginHoldingBoardingGhost)
                        {
                            m_BVMisfire.Remove(v);
                            m_BVMisfireStartFrame.Remove(v);
                            m_LastBoarding[v] = false;
                            m_CachedWpIdx[v] = 0;
                            boarding = false;
                        }
                    }

                    if (m_BVMisfire.Contains(v))
                    {
                        if (m_BVMisfireStartFrame.TryGetValue(v, out uint misfireStart)
                            && (nowFrame - misfireStart) > BV_MISFIRE_TIMEOUT)
                        {
                            if (targetMin >= 0)
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index
                                    + " 超时，释放班次" + SlotStr(targetMin) + " 并回库");
                                m_VehicleTargetMin[v] = -1;
                            }
                            else
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index + " 超时，回库");
                            }
                            m_BVMisfire.Remove(v);
                            m_BVMisfireStartFrame.Remove(v);
                            DoRetire(v, pt, tgt, ecb, "BVMisfire超时");
                            continue;
                        }
                        string misfireLabel = m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint forcedDepartGraceUntil)
                            && nowFrame < forcedDepartGraceUntil
                            ? "强制发车中 #" + v.Index
                            : "寻路异常 #" + v.Index;
                        SetUILabel(v, misfireLabel);
                        continue;
                    }

                    bool inCooldown = m_LaunchCooldownUntil.TryGetValue(v, out uint cooldownUntil)
                        && nowFrame < cooldownUntil;

                    bool lastBoarding = m_LastBoarding.TryGetValue(v, out bool lb) ? lb : false;
                    bool boardingChanged = !inCooldown && (boarding != lastBoarding);
                    int curWpIdx;
                    int previousCachedWpIdx = m_CachedWpIdx.TryGetValue(v, out int prevCached) ? prevCached : -1;

                    if (boardingChanged && state != VehicleState.Idle)
                    {
                        if (!boarding)
                        {
                            bool suppressBypassDepartureBounce = false;
                            if (state == VehicleState.Running && previousCachedWpIdx > 0)
                            {
                                TryGetCadencedBypassHoldDecision(
                                    v,
                                    lineEnt,
                                    wps,
                                    previousCachedWpIdx,
                                    nowFrame,
                                    out bool shouldHoldBypass,
                                    out _,
                                    out bool canClearAfterExit);
                                suppressBypassDepartureBounce = shouldHoldBypass && !canClearAfterExit;
                            }

                            if (suppressBypassDepartureBounce)
                            {
                                curWpIdx = previousCachedWpIdx;
                                m_CachedWpIdx[v] = previousCachedWpIdx;
                                // Keep the station context latched while bypass hold is still active;
                                // a transient boarding=false pulse should not unlock a station-side hold.
                                m_LastBoarding[v] = true;
                            }
                            else
                            {
                                TryRecordObservedStopDwellOnBoardingEnd(v, lineEnt, previousCachedWpIdx, nowFrame);
                                RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, false, -1, previousCachedWpIdx);
                                if (previousCachedWpIdx >= 0)
                                {
                                    Entity departedStop = GetStationBuildingForWaypoint(wps, previousCachedWpIdx);
                                    string departedStopName = ResolveWorkbenchEntityName(departedStop);
                                    if (string.IsNullOrWhiteSpace(departedStopName))
                                    {
                                        departedStopName = "stop#" + departedStop.Index;
                                    }

                                    int nextWaypointIndex = previousCachedWpIdx + 1 < wps.Length
                                        ? previousCachedWpIdx + 1
                                        : -1;
                                    Entity nextStop = nextWaypointIndex >= 0
                                        ? GetStationBuildingForWaypoint(wps, nextWaypointIndex)
                                        : Entity.Null;
                                    string nextStopName = nextStop != Entity.Null
                                        ? ResolveWorkbenchEntityName(nextStop)
                                        : string.Empty;
                                    if (nextStop != Entity.Null && string.IsNullOrWhiteSpace(nextStopName))
                                    {
                                        nextStopName = "stop#" + nextStop.Index;
                                    }

                                    string departureKey = SlotStr((int)(nowFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440)
                                        + "|wp=" + previousCachedWpIdx.ToString()
                                        + "|next=" + nextWaypointIndex.ToString();
                                    LogVehicleStateOnce(
                                        m_DepartureObserveLogCache,
                                        v,
                                        departureKey,
                                        "[离站观察] " + lineTag
                                        + " 车辆" + v.Index
                                        + " 从\"" + departedStopName + "\"离站"
                                        + (nextWaypointIndex >= 0 ? " next=\"" + nextStopName + "\"" : " next=\"-\"")
                                        + " state=" + state.ToString());
                                }
                                TryClearVehicleProgressSuspectOnStableDeparture(v, previousCachedWpIdx);
                                curWpIdx = -1;
                                m_CachedWpIdx[v] = -1;
                                m_LastBoarding[v] = false;
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                m_ForcedMidStopBoardingGraceUntil.Remove(v);
                                m_StopDwellStartFrame.Remove(v);
                                m_StopDwellSessions.Remove(v);
                            }
                        }
                        else
                        {
                            curWpIdx = ComputeWpIndex(v, wps);
                            m_CachedWpIdx[v] = curWpIdx;

                            if (curWpIdx >= 0)
                            {
                                BeginObservedStopDwellSession(v, lineEnt, curWpIdx, nowFrame);
                                RecordWorkbenchRealtimeStopEvent(v, lineEnt, wps, true, curWpIdx, previousCachedWpIdx);
                                m_LastBoarding[v] = true;
                                NoteVehicleProgressSuspectRecoveryBoarding(v, curWpIdx);
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                m_ForcedMidStopBoardingGraceUntil.Remove(v);
                            }
                            else
                            {
                                m_LastBoarding[v] = true;
                                ObserveBvMisfireCandidate(
                                    v,
                                    lineTag,
                                    "boarding-change",
                                    "boarding-without-waypoint",
                                    nowFrame);
                            }
                        }

                        if (state == VehicleState.Running)
                        {
                            if (curWpIdx >= 0)
                            {
                                if (curWpIdx == wps.Length - 1)
                                    m_NearingTerminus.Add(v);
                            }
                            else if (boarding)
                            {
                                log.Info("[boarding变化] " + lineTag + " 车辆" + v.Index + " BV误写，标记misfire");
                            }
                        }
                    }
                    else
                    {
                        curWpIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                    }

                    if (state == VehicleState.Preparing)
                    {
                        ClearBypassYieldState(v);
                        int liveWpIdx = ComputeWpIndex(v, wps);
                        if (liveWpIdx >= 0 && liveWpIdx != curWpIdx)
                        {
                            curWpIdx = liveWpIdx;
                            m_CachedWpIdx[v] = liveWpIdx;
                        }
                    }

                    if (inCooldown)
                        m_LastBoarding[v] = boarding;
                    if (state == VehicleState.Idle)
                        m_LastBoarding[v] = boarding;

                    bool atA = state == VehicleState.Preparing
                        ? HasPreparingVehicleReachedOrigin(v, wps, boarding, curWpIdx)
                        : (curWpIdx == 0);
                    bool midStopBoarding = state == VehicleState.Running
                        && boarding
                        && curWpIdx > 0
                        && curWpIdx < wps.Length - 1;
                    uint midStopDwellSinceFrame = 0;
                    uint midStopDwellDeadlineFrame = 0;
                    int maxStationDwellMinutes = 0;
                    bool midStopDwellTimedOut = state == VehicleState.Running
                        && ShouldForceMidStopDwellTimeout(
                            v,
                            lineEnt,
                            curWpIdx,
                            boarding,
                            nowFrame,
                            wps.Length,
                            out midStopDwellSinceFrame,
                            out midStopDwellDeadlineFrame,
                            out maxStationDwellMinutes);
                    string vTag = " #" + v.Index;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            if (targetMin >= 0 && IsSlotSoftExpired(nowMin, targetMin) && !CanLateDispatchSlot(nowMin, targetMin))
                            {
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                LogVehicleStateOnce(
                                    m_PreparingSlotLogCache,
                                    v,
                                    "PreparingSlot|" + targetMin + "|" + overdue,
                                    "[PreparingSlot] " + lineTag + " 车辆" + v.Index
                                        + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_VehicleTargetMin[v] = -1;
                                targetMin = -1;
                            }

                            if (atA)
                            {
                                if (targetMin < 0 && TryAssignUpcomingScheduledTargetToWaitingVehicle(
                                    cr.m_Route,
                                    v,
                                    nowMin,
                                    lineTag,
                                    "Preparing",
                                    ecb,
                                    out int preparingAssignedTarget))
                                {
                                    targetMin = preparingAssignedTarget;
                                }

                                if (ShouldRetireWaitingVehicleForFarFutureTarget(cr.m_Route, nowMin, targetMin))
                                {
                                    DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(cr.m_Route, nowMin, targetMin));
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Holding;
                                TryRecordPreparingArrivalSample(v, lineEnt, nowFrame);
                                RecordLineHoldingSummary(lineEnt, nowMin, v, targetMin);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                if (targetMin >= 0)
                                {
                                    SetUILabel(v, "候车 " + SlotStr(targetMin) + vTag);
                                    log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，预分配 " + SlotStr(targetMin));
                                }
                                else
                                {
                                    SetUILabel(v, "候车 等待调度" + vTag);
                                    log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，等待调度");
                                }
                            }
                            else
                            {
                                EnsurePreparingRoute(v, ref pt, ref tgt, wps, curWpIdx, boarding, ecb);
                                SetUILabel(v, "前往始发站" + (targetMin >= 0 ? " " + SlotStr(targetMin) : "") + vTag);
                            }
                            break;

                        case VehicleState.Holding:
                            if (!atA)
                            {
                                if (IsWaitingForcedOriginDwell(v, nowFrame))
                                {
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, targetMin >= 0 ? "候车 " + SlotStr(targetMin) + vTag : "候车 等待调度" + vTag);
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Running;
                                m_VehiclePreparingStartFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                RecordLapStart(v, "Holding异常离站");
                                SetUILabel(v, "运行中(异常)" + vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Holding 时意外离站");
                                break;
                            }
                            if (targetMin < 0)
                            {
                                int lateSlot = -1;
                                int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(cr.m_Route);
                                bool assigned = appliedTargets.Length > 0
                                    ? TryAssignCurrentOrLateScheduledTargetToWaitingVehicle(
                                        cr.m_Route,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        appliedTargets,
                                        out lateSlot)
                                    : TryAssignCurrentOrLateSlotToWaitingVehicle(
                                        cr.m_Route,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Holding",
                                        out lateSlot);
                                if (assigned)
                                {
                                    targetMin = lateSlot;
                                }
                                else if (TryAssignUpcomingScheduledTargetToWaitingVehicle(
                                    cr.m_Route,
                                    v,
                                    nowMin,
                                    lineTag,
                                    "Holding",
                                    ecb,
                                    out int upcomingTarget))
                                {
                                    targetMin = upcomingTarget;
                                }
                                else
                                {
                                    m_VehicleState[v] = VehicleState.Idle;
                                    if (!m_VehicleIdleStartFrame.ContainsKey(v))
                                        m_VehicleIdleStartFrame[v] = nowFrame;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, "等待调度" + vTag);
                                    break;
                                }
                            }

                            if (ShouldRetireWaitingVehicleForFarFutureTarget(cr.m_Route, nowMin, targetMin))
                            {
                                DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(cr.m_Route, nowMin, targetMin));
                                ClearBypassYieldState(v);
                                break;
                            }

                            if (IsTimeReached(nowMin, targetMin) || CanLateDispatchSlot(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v, "始发候车不参与待避");
                                if (IsDispatchTargetAlreadyOccupied(cr.m_Route, v, targetMin))
                                {
                                    m_VehicleTargetMin[v] = -1;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, "候车 等待调度" + vTag);
                                    LogVehicleStateOnce(
                                        m_HoldingSkipLogCache,
                                        v,
                                        "HoldingSkip|" + targetMin,
                                        "[HoldingSkip] " + lineTag + " 车辆" + v.Index
                                            + " 班次" + SlotStr(targetMin) + " 已被其他车辆占用，释放重调度");
                                    break;
                                }

                                if (IsWaitingForcedOriginDwell(v, nowFrame))
                                {
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, CanLateDispatchSlot(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                                    break;
                                }
                                bool isLateDispatch = CanLateDispatchSlot(nowMin, targetMin);
                                int overdue = isLateDispatch ? GetSlotOverdueMinutes(nowMin, targetMin) : 0;
                                LaunchVehicle(v, pt, tgt, wps, ecb);
                                m_VehicleState[v] = VehicleState.Running;
                                m_JustLaunched.Add(v);
                                m_VehicleLastLaunchFrame[v] = nowFrame;
                                RecordLapStart(v, isLateDispatch ? "补发" : "计划发车");
                                m_LastBoarding[v] = false;
                                m_CachedWpIdx[v] = -1;
                                m_BVMisfire.Remove(v);
                                m_BVMisfireStartFrame.Remove(v);
                                m_LaunchCooldownUntil[v] = nowFrame + LAUNCH_COOLDOWN_FRAMES;
                                m_VehicleCurrentSlot[v] = targetMin;
                                m_VehicleTargetMin[v] = -1;
                                m_OriginArrivalCandidateSinceFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                BeginWorkbenchRealtimeTripAtLaunch(v, lineEnt, wps);
                                SetUILabel(v, (isLateDispatch ? "运行中 补发 " : "运行中 ") + SlotStr(targetMin) + vTag);
                                if (isLateDispatch)
                                {
                                    LogVehicleStateOnce(
                                        m_LateDispatchLogCache,
                                        v,
                                        "LateDispatchLaunch|" + targetMin,
                                        "[补发] " + lineTag + " 车辆" + v.Index
                                            + " 于 " + SlotStr(nowMin) + " 补发（班次 " + SlotStr(targetMin) + "）"
                                            + " 已过期" + overdue + "分钟"
                                            + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES));
                                }
                                else
                                {
                                    log.Info("[发车] " + lineTag + " 车辆" + v.Index
                                        + " 于 " + SlotStr(nowMin) + " 发车（班次 " + SlotStr(targetMin) + "）"
                                        + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES));
                                }
                            }
                            else if (IsSlotHardExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 大幅过期(" + overdue + "分钟)，直接回库");
                                DoRetire(v, pt, tgt, ecb, "班次大幅过期" + overdue + "分钟");
                            }
                            else if (IsSlotSoftExpired(nowMin, targetMin))
                            {
                                ClearBypassYieldState(v);
                                int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
                                log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                    + " 班次" + SlotStr(targetMin) + " 已过期(" + overdue + "分钟)，释放重新调度");
                                m_VehicleTargetMin[v] = -1;
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetUILabel(v, "候车 等待调度" + vTag);
                            }
                            else
                            {
                                ClearBypassYieldState(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetUILabel(v,
                                    CanLateDispatchSlot(nowMin, targetMin)
                                        ? "候车 补发 " + SlotStr(targetMin) + vTag
                                        : "候车 " + SlotStr(targetMin) + vTag);
                            }
                            break;

                        case VehicleState.Running:
                            int bypassControlWaypointIndex = curWpIdx >= 0 ? curWpIdx : previousCachedWpIdx;
                            bool runningShouldHoldBypass = false;
                            bool runningCanClearAfterExit = true;
                            Entity runningBypassBlocker = Entity.Null;
                            bool runningBypassLatched = m_BypassYieldBlocker.ContainsKey(v);
                            if (bypassControlWaypointIndex > 0
                                && (boarding || runningBypassLatched))
                            {
                                TryGetCadencedBypassHoldDecision(
                                    v,
                                    cr.m_Route,
                                    wps,
                                    bypassControlWaypointIndex,
                                    nowFrame,
                                    out runningShouldHoldBypass,
                                    out runningBypassBlocker,
                                    out runningCanClearAfterExit);
                            }

                            if (midStopDwellTimedOut && !runningShouldHoldBypass)
                            {
                                TryRecordObservedStopDwellOnBoardingEnd(v, line, curWpIdx, midStopDwellDeadlineFrame > 0 ? midStopDwellDeadlineFrame : nowFrame);
                                ForceDepartMidStop(v, pt, tgt, wps, curWpIdx, ecb);
                                m_LastBoarding[v] = false;
                                m_StopDwellStartFrame.Remove(v);
                                m_StopDwellSessions.Remove(v);
                                ClearBypassYieldState(v);
                                SetUILabel(v, "停站超时" + vTag);
                                log.Info("[停站超时] " + lineTag + " 车辆" + v.Index
                                    + " 中途停站超时" + maxStationDwellMinutes + "分钟"
                                    + " sinceFrame=" + midStopDwellSinceFrame
                                    + " deadlineFrame=" + midStopDwellDeadlineFrame
                                    + " curWpIdx=" + curWpIdx
                                    + " nextTargetWp=" + (curWpIdx + 1 < wps.Length ? (curWpIdx + 1).ToString() : "-"));
                                break;
                            }

                            if (bypassControlWaypointIndex > 0
                                && runningShouldHoldBypass)
                            {
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                SetBypassYieldState(v, runningBypassBlocker, lineTag, "运行中");
                                SetUILabel(v, "待避快车" + vTag);
                                break;
                            }

                            string runningReleaseReason = null;
                            if (runningBypassLatched && !runningShouldHoldBypass)
                            {
                                runningReleaseReason = runningCanClearAfterExit
                                    ? "已越过当前待避站出口"
                                    : "待避条件消失";
                            }
                            if (runningBypassLatched && !runningShouldHoldBypass)
                            {
                                ClearBypassYieldState(v, runningReleaseReason);
                            }
                            bool settleAtOrigin = !inCooldown && ShouldSettleRunningAtOrigin(
                                v,
                                wps,
                                nowFrame,
                                atA,
                                boarding,
                                lastBoarding,
                                targetMin);
                            bool forcedAtOrigin = settleAtOrigin && !atA;
                            if ((atA || forcedAtOrigin) && !inCooldown)
                            {
                                bool hasLapStartOdo = m_VehicleLapStartOdometer.TryGetValue(v, out float ls);
                                bool hasLapStartFrame = m_VehicleLapStartFrame.TryGetValue(v, out uint lapStartFrame);
                                bool lapStartValid = hasLapStartOdo && !float.IsNaN(ls) && !float.IsInfinity(ls) && ls >= 0f;
                                bool brokenRecoveredRunning = hasLapStartFrame && !lapStartValid;
                                float lapStart = hasLapStartOdo ? ls : -1f;
                                float nowOdo = EntityManager.HasComponent<Odometer>(v)
                                    ? EntityManager.GetComponentData<Odometer>(v).m_Distance : -1f;
                                const float LAP_MOVED_MIN = 500f;
                                bool hasMoved = (nowOdo >= 0f && lapStartValid && (nowOdo - lapStart) > LAP_MOVED_MIN);
                                float ld = 0f;
                                m_VehicleLapDistance.TryGetValue(v, out ld);
                                if (brokenRecoveredRunning)
                                {
                                    m_VehicleState[v] = VehicleState.Idle;
                                    m_VehicleCurrentSlot.Remove(v);
                                    m_VehicleLapStartFrame.Remove(v);
                                    m_VehicleLapFrames.Remove(v);
                                    m_RestoredRunning.Remove(v);
                                    m_NearingTerminus.Remove(v);
                                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                                    m_ForcedOriginReadyFrame.Remove(v);
                                    m_CachedWpIdx[v] = 0;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    SetUILabel(v, targetMin >= 0 ? "候车 " + SlotStr(targetMin) + vTag : "等待调度" + vTag);
                                    log.Info("[恢复兜底] " + lineTag + " 车辆" + v.Index
                                        + " Running圈起点无效，回站后转Idle"
                                        + " lapStartFrame=" + lapStartFrame
                                        + " lapStartValid=" + lapStartValid
                                        + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                        + " target=" + (targetMin >= 0 ? SlotStr(targetMin) : "-")
                                        + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?"));
                                    break;
                                }
                                if (!hasMoved)
                                {
                                    if (settleAtOrigin)
                                    {
                                        uint originSinceFrame = m_OriginArrivalCandidateSinceFrame.TryGetValue(v, out uint sinceFrame)
                                            ? sinceFrame
                                            : nowFrame;
                                        bool keepAssignedTarget = targetMin >= 0 && IsCurrentOrRecentDispatchableSlot(nowMin, targetMin);
                                        bool recoverToHolding = keepAssignedTarget;

                                        m_VehicleState[v] = recoverToHolding ? VehicleState.Holding : VehicleState.Idle;
                                        if (!recoverToHolding)
                                        {
                                            m_VehicleTargetMin[v] = -1;
                                            m_VehicleIdleStartFrame[v] = nowFrame;
                                        }
                                        else
                                        {
                                            m_VehicleIdleStartFrame.Remove(v);
                                        }

                                        m_VehicleCurrentSlot.Remove(v);
                                        m_VehicleLastLaunchFrame.Remove(v);
                                        m_LaunchCooldownUntil.Remove(v);
                                        m_NearingTerminus.Remove(v);
                                        m_OriginArrivalCandidateSinceFrame.Remove(v);
                                        m_ForcedOriginReadyFrame.Remove(v);
                                        m_CachedWpIdx[v] = 0;
                                        pt.m_DepartureFrame = nowFrame + 9999;
                                        ecb.SetComponent(v, pt);

                                        if (recoverToHolding)
                                        {
                                            bool isLateRecoveredTarget = CanLateDispatchSlot(nowMin, targetMin);
                                            SetUILabel(v, (isLateRecoveredTarget ? "候车 补发 " : "候车 ") + SlotStr(targetMin) + vTag);
                                            log.Info("[Running->Holding兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为候车"
                                                + " target=" + SlotStr(targetMin)
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        else
                                        {
                                            SetUILabel(v, "等待调度" + vTag);
                                            log.Info("[Running->Idle兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为Idle"
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        break;
                                    }

                                    if (!settleAtOrigin)
                                        m_OriginArrivalCandidateSinceFrame.Remove(v);
                                    pt.m_DepartureFrame = midStopBoarding && midStopDwellDeadlineFrame > 0
                                        ? midStopDwellDeadlineFrame
                                        : nowFrame + 9999;
                                    ecb.SetComponent(v, pt);
                                    string curSlot1 = m_VehicleCurrentSlot.TryGetValue(v, out int cs1) ? SlotStr(cs1) : "?";
                                    string nxtSlot1 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                    SetUILabel(v, "运行中" + curSlot1 + nxtSlot1 + vTag);
                                    if (nowFrame % 1800 == 0)
                                    {
                                        uint lastLaunchFrame = m_VehicleLastLaunchFrame.TryGetValue(v, out uint llf) ? llf : 0;
                                        uint lapStartFrameDbg = m_VehicleLapStartFrame.TryGetValue(v, out uint lsfDbg) ? lsfDbg : 0;
                                        string curSlotDbg = m_VehicleCurrentSlot.TryGetValue(v, out int csDbg) ? SlotStr(csDbg) : "?";
                                        string targetSlotDbg = targetMin >= 0 ? SlotStr(targetMin) : "-";
                                        int cachedWpDbg = m_CachedWpIdx.TryGetValue(v, out int cwDbg) ? cwDbg : -1;
                                        log.Info("[心跳-卡站] " + lineTag + " 车辆" + v.Index
                                            + " atA=true hasMoved=false"
                                            + " traveled=" + (nowOdo >= 0f && lapStart >= 0f
                                                ? ((nowOdo - lapStart) / 1000f).ToString("F2") + "km" : "?")
                                            + " threshold=" + (LAP_MOVED_MIN / 1000f).ToString("F2") + "km"
                                            + " lapDist=" + (ld > 0f ? (ld / 1000f).ToString("F2") + "km" : "未知")
                                            + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                            + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                            + " lapStartValid=" + lapStartValid
                                            + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                            + " lapStartFrame=" + (lapStartFrameDbg > 0 ? lapStartFrameDbg.ToString() : "?")
                                            + " lastLaunchFrame=" + (lastLaunchFrame > 0 ? lastLaunchFrame.ToString() : "?")
                                            + " sinceLaunch=" + (lastLaunchFrame > 0 ? (nowFrame - lastLaunchFrame).ToString() : "?")
                                            + " curSlot=" + curSlotDbg
                                            + " targetSlot=" + targetSlotDbg
                                            + " cachedWp=" + cachedWpDbg
                                            + " curWpIdx=" + curWpIdx
                                            + " boarding=" + boarding
                                            + " lastBoarding=" + lastBoarding);
                                    }
                                    break;
                                }
                                UpdateLapStats(v);
                                m_VehicleState[v] = VehicleState.Idle;
                                if (targetMin >= 0)
                                {
                                    if (IsCurrentOrRecentDispatchableSlot(nowMin, targetMin))
                                        m_VehicleTargetMin[v] = targetMin;
                                    else
                                        m_VehicleTargetMin[v] = -1;
                                }
                                else
                                {
                                    m_VehicleTargetMin[v] = -1;
                                }
                                m_CachedWpIdx[v] = 0;
                                m_NearingTerminus.Remove(v);
                                m_OriginArrivalCandidateSinceFrame.Remove(v);
                                if (forcedAtOrigin)
                                    m_ForcedOriginReadyFrame[v] = nowFrame + FORCED_ORIGIN_MIN_DWELL_FRAMES;
                                else
                                    m_ForcedOriginReadyFrame.Remove(v);
                                SetUILabel(v, "等待调度" + vTag);
                                log.Info("[Running->Idle] " + lineTag + " 车辆" + v.Index
                                    + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                    + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                    + " curWpIdx=" + curWpIdx
                                    + (targetMin >= 0 && IsCurrentOrRecentDispatchableSlot(nowMin, targetMin)
                                        ? " keptTarget=" + SlotStr(targetMin)
                                        : "")
                                    + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                            }
                            else
                            {
                                if (!m_VehicleLapStartOdometer.ContainsKey(v) && !inCooldown)
                                    RecordLapStart(v, "Running缺少圈起点自愈");
                                string curSlot2 = m_VehicleCurrentSlot.TryGetValue(v, out int cs2) ? SlotStr(cs2) : "?";
                                string nxtSlot2 = targetMin >= 0 ? ("->" + SlotStr(targetMin)) : "";
                                SetUILabel(v, "运行中" + curSlot2 + nxtSlot2 + vTag);
                            }
                            break;

                        case VehicleState.Idle:
                            ClearBypassYieldState(v);
                            if (!atA)
                            {
                                m_VehicleState[v] = VehicleState.Running;
                                m_VehiclePreparingStartFrame.Remove(v);
                                m_ForcedOriginReadyFrame.Remove(v);
                                RecordLapStart(v, "Idle异常离站");
                                m_VehicleIdleStartFrame.Remove(v);
                                SetUILabel(v, "运行中(异常离站)" + vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Idle 时意外离站");
                                break;
                            }

                            if (targetMin < 0)
                            {
                                int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(cr.m_Route);
                                int lateTarget = -1;
                                bool assignedLateTarget = appliedTargets.Length > 0
                                    ? TryAssignCurrentOrLateScheduledTargetToWaitingVehicle(
                                        cr.m_Route,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Idle",
                                        appliedTargets,
                                        out lateTarget)
                                    : TryAssignCurrentOrLateSlotToWaitingVehicle(
                                        cr.m_Route,
                                        v,
                                        nowMin,
                                        lineTag,
                                        "Idle",
                                        out lateTarget);
                                if (assignedLateTarget)
                                    targetMin = lateTarget;
                            }

                            if (HasInboundVehicleNearOrigin(cr.m_Route, wps, v, ORIGIN_CONGESTION_RADIUS_METERS, includePreparingVehicles: false))
                            {
                                if (ShouldProtectIdleFromYield(cr.m_Route, v, nowMin))
                                {
                                    if (m_VehicleTargetMin.TryGetValue(v, out int ptm) && ptm >= 0 && CanLateDispatchSlot(nowMin, ptm))
                                    {
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipLate|" + ptm,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 班次" + SlotStr(ptm)
                                                + " 已过期" + GetSlotOverdueMinutes(nowMin, ptm) + "分钟，保留补发");
                                    }
                                    else
                                    {
                                        int protectTarget = m_VehicleTargetMin.TryGetValue(v, out int ptm2) && ptm2 >= 0
                                            ? ptm2
                                            : GetFallbackProtectTarget(cr.m_Route, nowMin);
                                        LogVehicleStateOnce(
                                            m_YieldSkipLogCache,
                                            v,
                                            "YieldSkipProtect|" + protectTarget,
                                            "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                + " 最近班次" + SlotStr(protectTarget)
                                                + " 仅剩" + MinutesUntil(nowMin, protectTarget) + "分钟，保留待避");
                                    }
                                    break;
                                }
                                log.Info("[Yield] " + lineTag + " 车辆" + v.Index + " 始发站有回流车压队，回库疏解");
                                DoRetire(v, pt, tgt, ecb, "始发站压队疏解");
                                break;
                            }

                            if (targetMin >= 0)
                            {
                                if (ShouldRetireWaitingVehicleForFarFutureTarget(cr.m_Route, nowMin, targetMin))
                                {
                                    DoRetire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(cr.m_Route, nowMin, targetMin));
                                    break;
                                }
                                m_VehicleState[v] = VehicleState.Holding;
                                m_VehicleIdleStartFrame.Remove(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                ecb.SetComponent(v, pt);
                                bool isLateTarget = CanLateDispatchSlot(nowMin, targetMin);
                                SetUILabel(v,
                                    (isLateTarget ? "候车 补发 " : "候车 ") + SlotStr(targetMin) + vTag);
                                LogVehicleStateOnce(
                                    isLateTarget ? m_LateDispatchLogCache : m_HoldingSkipLogCache,
                                    v,
                                    (isLateTarget ? "LateDispatchClaim|" : "IdleHoldingAssign|") + targetMin,
                                    (isLateTarget ? "[补发认领] " : "[Idle->Holding] ")
                                        + lineTag + " 车辆" + v.Index
                                        + (isLateTarget
                                            ? " 认领补发班次" + SlotStr(targetMin) + " 于 " + SlotStr(nowMin)
                                            : " 进入候车班次" + SlotStr(targetMin)));
                                break;
                            }

                            if (!m_VehicleIdleStartFrame.ContainsKey(v))
                                m_VehicleIdleStartFrame[v] = nowFrame;

                            if (m_VehicleIdleStartFrame.TryGetValue(v, out uint idleStart))
                            {
                                float idleMin = (nowFrame - idleStart) / (float)SIM_FRAMES_PER_MINUTE;
                                if (idleMin > IDLE_TIMEOUT_MIN)
                                {
                                    m_VehicleIdleStartFrame.Remove(v);
                                    DoRetire(v, pt, tgt, ecb, "闲置" + idleMin.ToString("F1") + "分钟");
                                    break;
                                }
                            }

                            pt.m_DepartureFrame = nowFrame + 9999;
                            ecb.SetComponent(v, pt);
                            SetUILabel(v, "等待调度" + vTag);
                            break;

                        case VehicleState.Retiring:
                            ClearBypassYieldState(v);
                            SetUILabel(v, "回库中" + vTag);
                            EnsureRetiringRoute(v, ref pt, ref tgt, ecb);
                            break;
                    }
                }

                var deadKeys = new NativeList<Entity>(Allocator.Temp);
                foreach (var kv in m_VehicleState)
                {
                    if (!EntityManager.Exists(kv.Key)) deadKeys.Add(kv.Key);
                }
                foreach (var dead in deadKeys)
                {
                    m_VehicleState.Remove(dead);
                    m_VehicleTargetMin.Remove(dead);
                    m_VehicleLapStartOdometer.Remove(dead);
                    m_VehicleLapDistance.Remove(dead);
                    m_VehicleLapStartFrame.Remove(dead);
                    m_VehicleLapFrames.Remove(dead);
                    m_VehicleIdleStartFrame.Remove(dead);
                    m_VehiclePreparingStartFrame.Remove(dead);
                    m_VehicleDispatchRequestStartFrame.Remove(dead);
                    m_VehicleCurrentSlot.Remove(dead);
                    m_VehicleLastLaunchFrame.Remove(dead);
                    m_UICache.Remove(dead);
                    m_LastBoarding.Remove(dead);
                    m_CachedWpIdx.Remove(dead);
                    m_BVMisfire.Remove(dead);
                    m_BVMisfireStartFrame.Remove(dead);
                    m_BypassHoldCadenceSnapshots.Remove(dead);
                    ClearVehicleProgressSuspect(dead, "vehicle-removed");
                    m_ForcedMidStopBoardingGraceUntil.Remove(dead);
                    m_VehicleLine.Remove(dead);
                    m_LaunchCooldownUntil.Remove(dead);
                    m_LastRetireFixLogFrame.Remove(dead);
                    m_RetireFixCooldownUntil.Remove(dead);
                    m_PreparingFixCooldownUntil.Remove(dead);
                    m_RetireFixCount.Remove(dead);
                    m_OriginArrivalCandidateSinceFrame.Remove(dead);
                    m_ForcedOriginReadyFrame.Remove(dead);
                    m_StopDwellStartFrame.Remove(dead);
                    m_BypassYieldBlocker.Remove(dead);
                    m_BvWaypointMismatchLogCache.Remove(dead);
                    m_BvWaypointMismatchLastLogFrame.Remove(dead);
                    log.Info("[清理] 车辆" + dead.Index + " 消失");
                }
                if (deadKeys.Length > 0)
                    m_LineRunningVehicleFrameSnapshots.Clear();
                deadKeys.Dispose();
            }
            finally { vehicles.Dispose(); }
        }

        private int ComputeWpIndex(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(v)) return -1;
            float3 vPos = EntityManager.GetComponentData<Game.Objects.Transform>(v).m_Position;

            int bvWi = -1;
            float bvDist = float.MaxValue;
            int closestWi = -1;
            float closestDist = AT_STOP_MAX_DIST;

            for (int wi = 0; wi < wps.Length; wi++)
            {
                Entity wp = wps[wi].m_Waypoint;
                Entity stop = EntityManager.HasComponent<Connected>(wp)
                    ? EntityManager.GetComponentData<Connected>(wp).m_Connected : Entity.Null;
                if (stop == Entity.Null) continue;
                if (!EntityManager.HasComponent<Game.Objects.Transform>(stop)) continue;

                float3 sPos = EntityManager.GetComponentData<Game.Objects.Transform>(stop).m_Position;
                float dist = math.distance(vPos, sPos);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestWi = wi;
                }

                if (!EntityManager.HasComponent<BoardingVehicle>(stop)) continue;
                if (EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != v) continue;
                bvWi = wi;
                bvDist = dist;
            }

            if (bvWi >= 0 && bvDist <= AT_STOP_MAX_DIST) return bvWi;

            if (bvWi >= 0)
            {
                if (EntityManager.HasComponent<Target>(v))
                {
                    Target target = EntityManager.GetComponentData<Target>(v);
                    if (IsSuppressedForcedMidStopBoardingGhost(v, target, wps, m_SimulationSystem.frameIndex, out _))
                        return closestWi >= 0 ? closestWi : -1;
                }
                uint nowFrame = m_SimulationSystem.frameIndex;
                string mismatchKey = "wps[" + bvWi + "]|closest[" + closestWi + "]";
                if (ShouldEmitVehicleLogWithCooldown(
                        m_BvWaypointMismatchLogCache,
                        m_BvWaypointMismatchLastLogFrame,
                        v,
                        mismatchKey,
                        nowFrame,
                        BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES))
                {
                    log.Info("[定位] 车辆" + v.Index
                        + " BV误写 wps[" + bvWi + "] dist=" + bvDist.ToString("F0") + "m > " + AT_STOP_MAX_DIST + "m");
                }
                return -1;
            }

            if (closestWi >= 0)
                return closestWi;

            return -1;
        }

        private bool IsSuppressedForcedMidStopBoardingGhost(
            Entity vehicle,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out int targetWaypointIndex)
        {
            targetWaypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil))
            {
                return false;
            }

            if (nowFrame >= graceUntil)
            {
                m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
                return false;
            }

            if (!EntityManager.HasComponent<Waypoint>(target.m_Target))
                return false;

            targetWaypointIndex = EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypointIndex < 0 || targetWaypointIndex >= waypoints.Length)
                return false;

            Entity targetStop = GetConnectedStopForWaypoint(waypoints[targetWaypointIndex].m_Waypoint);
            if (targetStop == Entity.Null
                || !EntityManager.HasComponent<BoardingVehicle>(targetStop)
                || EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle != vehicle
                || !EntityManager.HasComponent<Game.Objects.Transform>(targetStop)
                || !EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            float3 vehiclePosition = EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPosition = EntityManager.GetComponentData<Game.Objects.Transform>(targetStop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > AT_STOP_MAX_DIST;
        }

        private Entity GetConnectedStopForWaypoint(Entity waypoint)
        {
            if (waypoint == Entity.Null || !EntityManager.HasComponent<Connected>(waypoint))
                return Entity.Null;

            return EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
        }

        private static void InjectModifier(DynamicBuffer<RouteModifier> mods, float delta)
        {
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(new RouteModifier { m_Delta = float2.zero });
            var m = mods[idx];
            m.m_Delta = new float2(delta, 0f);
            mods[idx] = m;
        }

        private float CalculateLineDuration(Entity line)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line)) return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return 0f;
            float stopDuration = EntityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration;

            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            var segBuffers = GetBufferLookup<RouteSegment>(true);
            var pathInfoLookup = GetComponentLookup<PathInformation>(true);
            var vehicleTimingLookup = GetComponentLookup<VehicleTiming>(true);

            if (!wpBuffers.TryGetBuffer(line, out var waypoints)) return 0f;
            if (!segBuffers.TryGetBuffer(line, out var segments)) return 0f;
            if (waypoints.Length == 0 || segments.Length == 0) return 0f;

            int firstWaypoint = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (vehicleTimingLookup.HasComponent(waypoints[w].m_Waypoint))
                {
                    firstWaypoint = w;
                    break;
                }
            }

            float duration = 0f;
            for (int i = 0; i < waypoints.Length; i++)
            {
                int wi = (firstWaypoint + i) % waypoints.Length;
                int wi1 = (wi + 1) % waypoints.Length;
                Entity seg = segments[wi].m_Segment;
                if (pathInfoLookup.TryGetComponent(seg, out var pathInfo))
                    duration += pathInfo.m_Duration;
                if (vehicleTimingLookup.HasComponent(waypoints[wi1].m_Waypoint))
                    duration += stopDuration;
            }
            return duration;
        }

        private void AssignSlot(Entity v, int slot, EntityCommandBuffer ecb)
        {
            m_VehicleTargetMin[v] = slot;
            var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex + 9999;
            ecb.SetComponent(v, pt);
            SetUILabel(v, "候车 " + SlotStr(slot));
        }

        private void TryLogSpawnBlocked(Entity line, string lineTag, int nowMin, int slot)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_LastSpawnBlockedLogFrame.TryGetValue(line, out uint lastFrame)
                && (nowFrame - lastFrame) < SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES)
                return;

            m_LastSpawnBlockedLogFrame[line] = nowFrame;
            log.Info("[SpawnBlocked] " + lineTag + " 班次" + SlotStr(slot)
                + " 始发站附近已有回流车，跳过产车");
        }

        private void TryLogScheduleDiagnostic(
            Entity line,
            string lineTag,
            int slot,
            Entity nearestVehicle,
            VehicleState nearestState,
            string etaText,
            string nearestReason)
        {
            uint nowFrame = m_SimulationSystem.frameIndex;
            ulong key = MakeLineSlotKey(line, slot);
            if (m_LastScheduleDiagnosticLogFrame.TryGetValue(key, out uint lastFrame)
                && (nowFrame - lastFrame) < SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES)
                return;

            m_LastScheduleDiagnosticLogFrame[key] = nowFrame;
            log.Info("[调度诊断] " + lineTag + " 班次" + SlotStr(slot)
                + " 最近候选车辆" + nearestVehicle.Index
                + " state=" + nearestState
                + " eta=" + etaText
                + " reason=" + nearestReason);
        }

        private static ulong MakeLineSlotKey(Entity line, int slot)
        {
            return ((ulong)(uint)line.Index << 32) | (uint)(slot & 0xFFFF);
        }

        private void DoRetire(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb,
            string reason = "")
        {
            m_VehicleState[v] = VehicleState.Retiring;
            m_VehicleTargetMin[v] = -1;
            m_VehicleIdleStartFrame.Remove(v);
            m_VehiclePreparingStartFrame.Remove(v);
            m_VehicleDispatchRequestStartFrame.Remove(v);
            m_BVMisfire.Remove(v);
            m_BVMisfireStartFrame.Remove(v);
            m_ForcedMidStopBoardingGraceUntil.Remove(v);
            m_LaunchCooldownUntil.Remove(v);
            m_PreparingFixCooldownUntil.Remove(v);
            m_NearingTerminus.Remove(v);
            m_OriginArrivalCandidateSinceFrame.Remove(v);
            m_ForcedOriginReadyFrame.Remove(v);
            m_RetireFixCount[v] = 0;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "线路" + le.Index : "线路?";
            SetUILabel(v, "回库中" + (reason.Length > 0 ? "(" + reason + ")" : ""));
            log.Info("[回库] " + lineTag + " 车辆" + v.Index
                + (reason.Length > 0 ? " 原因:" + reason : "") + " -> 车库");

            if (!EntityManager.HasComponent<Owner>(v))
            {
                log.Info("[回库] " + lineTag + " 车辆" + v.Index + " 无Owner，标记删除");
                ecb.AddComponent<Deleted>(v);
                return;
            }
            tgt.m_Target = EntityManager.GetComponentData<Owner>(v).m_Owner;
            pt.m_DepartureFrame = 0;
            pt.m_State &= ~PublicTransportFlags.Boarding;
            RepathVehicle(v, pt, tgt, ecb);
            m_RetireFixCooldownUntil[v] = m_SimulationSystem.frameIndex + RETIREFIX_REPATH_COOLDOWN_FRAMES;
        }

        private void LaunchVehicle(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            EntityCommandBuffer ecb)
        {
            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex - 1;
            tgt.m_Target = wps[1].m_Waypoint;
            RepathVehicle(v, pt, tgt, ecb);
        }

        private void ForceDepartMidStop(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            int currentWaypointIndex,
            EntityCommandBuffer ecb)
        {
            int nextWaypointIndex = -1;
            if (EntityManager.HasComponent<Waypoint>(tgt.m_Target))
            {
                int currentTargetIndex = EntityManager.GetComponentData<Waypoint>(tgt.m_Target).m_Index;
                nextWaypointIndex = currentTargetIndex + 1;
                if (nextWaypointIndex >= wps.Length)
                    nextWaypointIndex = 0;
            }

            if (nextWaypointIndex < 0)
                nextWaypointIndex = currentWaypointIndex + 1;

            if (nextWaypointIndex < 0 || nextWaypointIndex >= wps.Length)
            {
                pt.m_State &= ~PublicTransportFlags.Boarding;
                pt.m_DepartureFrame = m_SimulationSystem.frameIndex - 1;
                m_ForcedMidStopBoardingGraceUntil[v] = m_SimulationSystem.frameIndex + FORCED_MIDSTOP_BV_GRACE_FRAMES;
                RepathVehicle(v, pt, tgt, ecb);
                return;
            }

            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = m_SimulationSystem.frameIndex - 1;
            tgt.m_Target = wps[nextWaypointIndex].m_Waypoint;
            m_ForcedMidStopBoardingGraceUntil[v] = m_SimulationSystem.frameIndex + FORCED_MIDSTOP_BV_GRACE_FRAMES;
            RepathVehicle(v, pt, tgt, ecb);
        }

        private void CommitVehicleDepartureState(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb)
        {
            ecb.SetComponent(v, tgt);
            ecb.SetComponent(v, pt);
            ecb.AddComponent<Updated>(v);
        }

        private void EnsurePreparingRoute(
            Entity v,
            ref Game.Vehicles.PublicTransport pt,
            ref Target tgt,
            DynamicBuffer<RouteWaypoint> wps,
            int curWpIdx,
            bool boarding,
            EntityCommandBuffer ecb)
        {
            Entity stationA = wps[0].m_Waypoint;
            bool wrongTarget = tgt.m_Target != stationA;
            bool driftedToMidStop = boarding && curWpIdx > 0;
            if (!wrongTarget && !driftedToMidStop) return;
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (wrongTarget
                && !driftedToMidStop
                && IsFreshDispatchedPreparingVehicle(v, nowFrame))
                return;
            if (m_PreparingFixCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity lineEnt)
                ? "线路" + lineEnt.Index : "线路?";
            string why = driftedToMidStop
                ? (curWpIdx >= 0 ? ("偏航到 wp=" + curWpIdx) : "偏航 boarding")
                : "目标不是始发站";

            m_VehiclePreparingStartFrame[v] = nowFrame;
            m_CachedWpIdx[v] = -1;
            m_LastBoarding[v] = false;
            m_BVMisfire.Remove(v);
            m_BVMisfireStartFrame.Remove(v);
            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = nowFrame + 9999;
            tgt.m_Target = stationA;
            RepathVehicle(v, pt, tgt, ecb);
            m_PreparingFixCooldownUntil[v] = nowFrame + PREPARINGFIX_REPATH_COOLDOWN_FRAMES;

            log.Info("[PreparingFix] " + lineTag + " 车辆" + v.Index
                + " " + why + "，重置去始发站 wp0=" + stationA.Index);
        }

        private void EnsureRetiringRoute(
            Entity v,
            ref Game.Vehicles.PublicTransport pt,
            ref Target tgt,
            EntityCommandBuffer ecb)
        {
            if (!EntityManager.HasComponent<Owner>(v)) return;

            Entity depot = EntityManager.GetComponentData<Owner>(v).m_Owner;
            bool wrongTarget = tgt.m_Target != depot;
            bool stillBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            if (!wrongTarget && !stillBoarding) return;
            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_RetireFixCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return;

            string lineTag = m_VehicleLine.TryGetValue(v, out Entity lineEnt)
                ? "线路" + lineEnt.Index : "线路?";

            pt.m_State &= ~PublicTransportFlags.Boarding;
            pt.m_DepartureFrame = 0;
            tgt.m_Target = depot;
            RepathVehicle(v, pt, tgt, ecb);
            m_RetireFixCooldownUntil[v] = nowFrame + RETIREFIX_REPATH_COOLDOWN_FRAMES;
            byte fixCount = m_RetireFixCount.TryGetValue(v, out byte oldCount)
                ? (byte)(oldCount + 1)
                : (byte)1;
            m_RetireFixCount[v] = fixCount;

            if (!m_LastRetireFixLogFrame.TryGetValue(v, out uint lastLogFrame)
                || (nowFrame - lastLogFrame) >= RETIREFIX_LOG_COOLDOWN_FRAMES)
            {
                m_LastRetireFixLogFrame[v] = nowFrame;
                log.Info("[RetireFix] " + lineTag + " 车辆" + v.Index
                    + " 回库目标异常，重新指向车库 depot=" + depot.Index);
            }

            if (fixCount >= RETIREFIX_DELETE_THRESHOLD)
            {
                log.Info("[RetireAbandon] " + lineTag + " 车辆" + v.Index
                    + " 连续回库修正" + fixCount + "次，视为失效运力并删除");
                ecb.AddComponent<Deleted>(v);
            }
        }

        private void RepathVehicle(
            Entity v,
            Game.Vehicles.PublicTransport pt,
            Target tgt,
            EntityCommandBuffer ecb)
        {
            if (EntityManager.HasComponent<PathOwner>(v))
            {
                var po = EntityManager.GetComponentData<PathOwner>(v);
                po.m_State = PathFlags.Obsolete;
                po.m_ElementIndex = 0;
                ecb.SetComponent(v, po);
            }
            ecb.SetBuffer<PathElement>(v).Clear();
            ecb.SetComponent(v, tgt);
            ecb.SetComponent(v, pt);
            ecb.AddComponent<PathfindUpdated>(v);
            ecb.AddComponent<Updated>(v);
        }

        private bool HasInboundVehicleNearOrigin(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            Entity ignoreVehicle,
            float radiusMeters,
            bool includePreparingVehicles = true)
        {
            Entity stationA = wps[0].m_Waypoint;
            Entity stopA = EntityManager.HasComponent<Connected>(stationA)
                ? EntityManager.GetComponentData<Connected>(stationA).m_Connected
                : Entity.Null;
            if (stopA == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                return false;

            float3 stationPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
            float radiusSq = radiusMeters * radiusMeters;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity nearV = rvs[i].m_Vehicle;
                if (nearV == ignoreVehicle) continue;
                if (!EntityManager.Exists(nearV)) continue;
                if (!m_VehicleState.TryGetValue(nearV, out var nearState)) continue;
                bool isPreparing = nearState == VehicleState.Preparing;
                if (isPreparing && !includePreparingVehicles) continue;
                bool isTaggedInbound = m_NearingTerminus.Contains(nearV);
                if (!isPreparing && !isTaggedInbound) continue;
                if (!EntityManager.HasComponent<Game.Objects.Transform>(nearV)) continue;

                float3 nearPos = EntityManager.GetComponentData<Game.Objects.Transform>(nearV).m_Position;
                float3 delta = nearPos - stationPos;
                float distSq = math.lengthsq(delta);
                if (distSq <= radiusSq)
                    return true;
            }

            return false;
        }

        private bool IsFreshDispatchedPreparingVehicle(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleDispatchRequestStartFrame.TryGetValue(vehicle, out uint dispatchStartFrame))
                return false;

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= PREPARING_ROUTE_FIX_GRACE_FRAMES;
        }

        private int CountActiveVehicles(Entity line, BufferLookup<RouteVehicle> rvBuffers)
        {
            int count = 0;
            if (!rvBuffers.TryGetBuffer(line, out var rvs)) return 0;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!EntityManager.Exists(v)) continue;
                if (m_VehicleState.TryGetValue(v, out var st) && st == VehicleState.Retiring) continue;
                count++;
            }
            return count;
        }

        private float GetRemainingRange(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v)) return float.MaxValue;
            if (!EntityManager.HasComponent<PrefabRef>(v)) return float.MaxValue;
            float cur = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(pref)) return float.MaxValue;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
            return (range > 0f) ? (range - cur) : float.MaxValue;
        }

        private bool NeedsMaintenance(Entity v)
        {
            var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
            if ((pt.m_State & PublicTransportFlags.RequiresMaintenance) != 0) return true;
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return false;
            float dist = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return false;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            return range > 0f && (dist / range) >= MAINTENANCE_THRESHOLD;
        }

        private bool CanFinishNextLap(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v) || !EntityManager.HasComponent<PrefabRef>(v)) return true;
            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
            if (!EntityManager.HasComponent<PublicTransportVehicleData>(prefab)) return true;
            float range = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_MaintenanceRange;
            if (range <= 0f) return true;
            float remaining = range - current;
            if (m_VehicleLapDistance.TryGetValue(v, out float lapDist) && lapDist > 0f)
                return remaining >= lapDist;
            return (current / range) < MAINTENANCE_THRESHOLD;
        }

        private void RecordLapStart(Entity v, string reason = "")
        {
            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "线路" + le.Index : "线路?";
            if (!EntityManager.HasComponent<Odometer>(v))
            {
                log.Info("[圈起点] " + lineTag + " 车辆" + v.Index
                    + " reason=" + (reason.Length > 0 ? reason : "未注明")
                    + " 无Odometer，跳过记录");
                return;
            }

            float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            uint nowFrame = m_SimulationSystem.frameIndex;
            m_VehicleLapStartOdometer[v] = currentOdo;
            m_VehicleLapStartFrame[v] = nowFrame;
            string curSlot = m_VehicleCurrentSlot.TryGetValue(v, out int cs) ? SlotStr(cs) : "-";
            int cachedWp = m_CachedWpIdx.TryGetValue(v, out int cw) ? cw : -1;
            log.Info("[圈起点] " + lineTag + " 车辆" + v.Index
                + " reason=" + (reason.Length > 0 ? reason : "未注明")
                + " frame=" + nowFrame
                + " odo=" + currentOdo.ToString("F1")
                + " curSlot=" + curSlot
                + " cachedWp=" + cachedWp);
        }

        private void UpdateLapStats(Entity v)
        {
            if (!EntityManager.HasComponent<Odometer>(v)) return;
            if (!m_VehicleLapStartOdometer.TryGetValue(v, out float startOdo)) return;
            float current = EntityManager.GetComponentData<Odometer>(v).m_Distance;
            float lapDist = current - startOdo;
            string lineTag = m_VehicleLine.TryGetValue(v, out Entity le) ? "线路" + le.Index : "线路?";

            if (m_RestoredRunning.Contains(v))
            {
                m_RestoredRunning.Remove(v);
                if (lapDist > 0f)
                    m_VehicleLapDistance[v] = lapDist;
                log.Info("[圈统计-跳过] " + lineTag + " 车辆" + v.Index
                    + " 恢复首圈，圈距" + (lapDist / 1000f).ToString("F2") + "km，圈时不可信，跳过写入");
                return;
            }

            if (lapDist > 0f)
            {
                m_VehicleLapDistance[v] = lapDist;
                float maintenanceRange = 0f;
                if (EntityManager.HasComponent<PrefabRef>(v))
                {
                    Entity pref = EntityManager.GetComponentData<PrefabRef>(v).m_Prefab;
                    if (EntityManager.HasComponent<PublicTransportVehicleData>(pref))
                        maintenanceRange = EntityManager.GetComponentData<PublicTransportVehicleData>(pref).m_MaintenanceRange;
                }
                float remaining = maintenanceRange > 0f ? (maintenanceRange - current) : -1f;
                string maintStr = maintenanceRange > 0f
                    ? (" 维护=" + (maintenanceRange / 1000f).ToString("F1") + "km 剩余=" + (remaining / 1000f).ToString("F1") + "km")
                    : " 无维护限制";
                log.Info("[里程] " + lineTag + " 车辆" + v.Index
                    + " 本圈 " + (lapDist / 1000f).ToString("F2") + "km" + maintStr);
            }
            if (m_VehicleLapStartFrame.TryGetValue(v, out uint startFrame))
            {
                uint framesDelta = m_SimulationSystem.frameIndex - startFrame;
                m_VehicleLapFrames[v] = framesDelta;
                float realMin = framesDelta / (float)SIM_FRAMES_PER_MINUTE;
                log.Info("[圈统计] " + lineTag + " 车辆" + v.Index
                    + " 本圈 " + realMin.ToString("F1") + "游戏分钟/" + framesDelta + "帧");

                if (m_VehicleLine.TryGetValue(v, out Entity lapLine))
                    FlushLineLapCache(lapLine);
            }
        }

        private static int NextSlotMin(int nowMin)
            => ((nowMin / SLOT_INTERVAL) + 1) * SLOT_INTERVAL % 1440;

        private static int MinutesUntil(int nowMin, int targetMin)
        {
            int diff = targetMin - nowMin;
            if (diff <= 0) diff += 1440;
            return diff;
        }

        private static bool IsTimeReached(int nowMin, int targetMin)
            => ((nowMin - targetMin + 1440) % 1440) <= SLOT_GRACE_MIN;

        private static int GetEffectiveLateDispatchWindowMinutes()
            => math.clamp(LATE_DISPATCH_WINDOW_MINUTES, 0, SLOT_INTERVAL);

        private static bool LateDispatchEnabled()
            => GetEffectiveLateDispatchWindowMinutes() > 0;

        private static int GetPreviousSlotMin(int nowMin)
            => ((nowMin / SLOT_INTERVAL) * SLOT_INTERVAL) % 1440;

        private static bool IsCurrentOrRecentDispatchableSlot(int nowMin, int targetMin)
            => IsTimeReached(nowMin, targetMin) || CanLateDispatchSlot(nowMin, targetMin);

        private static int GetSlotOverdueMinutes(int nowMin, int targetMin)
            => (nowMin - targetMin + 1440) % 1440;

        private static bool CanLateDispatchSlot(int nowMin, int targetMin)
        {
            if (!LateDispatchEnabled()) return false;
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            int lateWindow = GetEffectiveLateDispatchWindowMinutes();
            return overdue > SLOT_GRACE_MIN && overdue <= lateWindow;
        }

        private static bool IsSlotSoftExpired(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            int releaseAfter = math.max(SLOT_GRACE_MIN, GetEffectiveLateDispatchWindowMinutes());
            return overdue > releaseAfter && overdue <= SLOT_INTERVAL;
        }

        private bool HasPreparingVehicleReachedOrigin(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            int currentWaypointIndex)
        {
            if (vehicle == Entity.Null || waypoints.Length == 0 || currentWaypointIndex != 0)
                return false;

            float originDistance = GetDistanceToOriginMeters(vehicle, waypoints);
            if (originDistance > ORIGIN_FORCE_IDLE_RADIUS_METERS)
                return false;

            if (TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
            {
                bool settlingIntoOrigin = nextWaypointIndex == 0
                    && segmentPosition >= ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS;
                bool dwellingAtOrigin = boarding
                    && (nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f));
                return settlingIntoOrigin || dwellingAtOrigin;
            }

            return boarding;
        }

        private static bool IsSlotHardExpired(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            return overdue > SLOT_INTERVAL && overdue <= SPAWN_LEAD_MIN + SLOT_GRACE_MIN;
        }

        private static bool IsSlotExpired(int nowMin, int targetMin)
        {
            int overdue = (nowMin - targetMin + 1440) % 1440;
            return overdue > SLOT_GRACE_MIN && overdue <= SPAWN_LEAD_MIN + SLOT_GRACE_MIN;
        }

        private static string SlotStr(int min)
        {
            min = ((min % 1440) + 1440) % 1440;
            int h = min / 60 % 24;
            int m = min % 60;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

        private void SetUILabel(Entity v, string msg)
        {
            var fs = new FixedString64Bytes(msg);
            if (!m_UICache.TryGetValue(v, out var cached) || cached != fs)
            {
                m_NameSystem.SetCustomName(v, msg);
                m_UICache[v] = fs;
            }
        }

        private void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (vehicle == Entity.Null)
            {
                log.Info(message);
                return;
            }

            if (cache.TryGetValue(vehicle, out string previous) && previous == key)
                return;

            cache[vehicle] = key;
            log.Info(message);
        }

        private bool ShouldEmitVehicleLogWithCooldown(
            Dictionary<Entity, string> keyCache,
            Dictionary<Entity, uint> lastLogFrameCache,
            Entity vehicle,
            string key,
            uint nowFrame,
            uint cooldownFrames)
        {
            if (vehicle == Entity.Null)
                return true;

            bool keyChanged = !keyCache.TryGetValue(vehicle, out string previousKey) || previousKey != key;
            if (keyChanged)
            {
                keyCache[vehicle] = key;
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            if (!lastLogFrameCache.TryGetValue(vehicle, out uint lastLogFrame)
                || nowFrame >= lastLogFrame + cooldownFrames)
            {
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            return false;
        }

        private void ObserveBvMisfireCandidate(
            Entity vehicle,
            string lineTag,
            string phase,
            string detail,
            uint nowFrame)
        {
            LogVehicleStateOnce(
                m_BvMisfireObserveLogCache,
                vehicle,
                phase + "|" + detail,
                "[BVObserve] " + lineTag + " 车辆" + vehicle.Index
                    + " phase=" + phase
                    + " detail=" + detail
                    + " enforcement=" + (IsBvMisfireEnforcementEnabled() ? "on" : "off")
                    + " frame=" + nowFrame);

            if (IsBvMisfireEnforcementEnabled())
            {
                m_BVMisfire.Add(vehicle);
                m_BVMisfireStartFrame[vehicle] = nowFrame;
            }
            else
            {
                m_BVMisfire.Remove(vehicle);
                m_BVMisfireStartFrame.Remove(vehicle);
            }
        }

        private void AddDebugItem(InfoList list, string labelCn, string labelEn, string value)
        {
            list.Add(new InfoList.Item(labelCn + " / " + labelEn + ": " + value));
        }

        private string BuildLineAlertSummary(
            Entity line,
            int nextSlotOccupancy,
            int nearingTerminus,
            float lapCacheFrames,
            float dispatchCacheFrames,
            int spawning)
        {
            if (!IsWorkbenchTimetableApplied(line))
            {
                if (EntityManager.HasComponent<Disabled>(line))
                    return "line-disabled";
                return IsChineseLocale() ? "官方调度" : "Official dispatch";
            }

            string alerts = string.Empty;
            if (EntityManager.HasComponent<Disabled>(line))
                alerts = AppendAlert(alerts, "line-disabled");
            if (nextSlotOccupancy <= 0)
                alerts = AppendAlert(alerts, "next-slot-gap");
            if (spawning > 0)
                alerts = AppendAlert(alerts, "spawn-pending:" + spawning);
            if (nearingTerminus > 0)
                alerts = AppendAlert(alerts, "yield-guard:" + nearingTerminus);
            if (lapCacheFrames <= 0f)
                alerts = AppendAlert(alerts, "no-lap-cache");
            if (dispatchCacheFrames <= 0f)
                alerts = AppendAlert(alerts, "no-dispatch-cache");
            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleAlertSummary(Entity vehicle, Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return IsChineseLocale() ? "官方调度" : "Official dispatch";

            string alerts = string.Empty;
            if (m_BypassYieldBlocker.TryGetValue(vehicle, out Entity blockerVehicle) && blockerVehicle != Entity.Null)
                alerts = AppendAlert(alerts, "yielding-for:" + blockerVehicle.Index);
            if (m_BVMisfire.Contains(vehicle))
                alerts = AppendAlert(alerts, "bv-misfire");
            if (m_NearingTerminus.Contains(vehicle))
                alerts = AppendAlert(alerts, "nearing-terminus");
            if (m_LaunchCooldownUntil.TryGetValue(vehicle, out uint cooldownUntil) && m_SimulationSystem.frameIndex < cooldownUntil)
                alerts = AppendAlert(alerts, "launch-cooldown");
            if (targetMin >= 0 && IsSlotExpired(nowMin, targetMin))
                alerts = AppendAlert(alerts, "target-expired");
            if (line != Entity.Null
                && m_VehicleState.TryGetValue(vehicle, out var state)
                && state == VehicleState.Idle
                && ShouldProtectIdleFromYield(line, vehicle, nowMin))
                alerts = AppendAlert(alerts, "yield-protected");
            return alerts.Length > 0 ? alerts : "None";
        }

        private string BuildVehicleProgressSummary(Entity vehicle)
        {
            string cachedWaypoint = m_CachedWpIdx.TryGetValue(vehicle, out int waypointIndex) ? waypointIndex.ToString() : "-";
            string lapDistance = m_VehicleLapDistance.TryGetValue(vehicle, out float distance) && distance >= 0f
                ? (distance / 1000f).ToString("F2") + " km"
                : "-";
            return "wp " + cachedWaypoint + " / lap " + lapDistance;
        }

        private string EstimateVehicleEtaText(Entity vehicle, Entity line, VehicleState vehicleState)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return "-";

            float lineDurationFrames = ReadLineLapCache(line);
            bool lineHasHistory = lineDurationFrames > 0f;
            uint nowFrame = m_SimulationSystem.frameIndex;
            float etaFrames = float.MaxValue;

            if (vehicleState == VehicleState.Preparing)
            {
                etaFrames = EstimatePreparingArrivalFrames(vehicle, line, nowFrame, lineDurationFrames);
            }
            else if (vehicleState == VehicleState.Running)
            {
                var routeWaypoints = GetBufferLookup<RouteWaypoint>(true);
                if (routeWaypoints.TryGetBuffer(line, out var waypoints))
                    etaFrames = EstimateRunningArrivalFrames(vehicle, line, waypoints, nowFrame, lineDurationFrames, lineHasHistory);
            }
            else if (vehicleState == VehicleState.Holding)
            {
                etaFrames = 0f;
            }

            if (etaFrames == float.MaxValue)
                return "-";

            float etaMinutes = etaFrames / (float)SIM_FRAMES_PER_MINUTE;
            return etaMinutes.ToString("F1") + " min";
        }

        private string FormatFramesAsMinutes(float frames)
        {
            return frames > 0f ? (frames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min" : "-";
        }

        private string BuildVehicleStopDwellValue(Entity vehicle)
        {
            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out uint dwellSinceFrame))
                return "-";

            uint elapsedFrames = m_SimulationSystem.frameIndex > dwellSinceFrame
                ? m_SimulationSystem.frameIndex - dwellSinceFrame
                : 0u;
            return (elapsedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + " min";
        }

        private string BuildVehicleInboundTimeValue(Entity vehicle)
        {
            if (m_VehiclePreparingStartFrame.TryGetValue(vehicle, out uint prepStartFrame))
                return SlotStr((int)(prepStartFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440);

            if (m_OriginArrivalCandidateSinceFrame.TryGetValue(vehicle, out uint originSinceFrame))
                return SlotStr((int)(originSinceFrame / (uint)SIM_FRAMES_PER_MINUTE) % 1440);

            return "-";
        }

        private static string AppendAlert(string current, string alert)
        {
            return current.Length == 0 ? alert : current + ", " + alert;
        }

        private bool ShouldProtectIdleFromYield(Entity line, Entity v, int nowMin, int nextTargetMin = -1)
        {
            if (m_VehicleTargetMin.TryGetValue(v, out int targetMin) && targetMin >= 0)
            {
                if (CanLateDispatchSlot(nowMin, targetMin))
                    return true;
                return MinutesUntil(nowMin, targetMin) <= YIELD_PROTECT_MINUTES;
            }
            if (nextTargetMin >= 0)
                return MinutesUntil(nowMin, nextTargetMin) <= YIELD_PROTECT_MINUTES;

            return MinutesUntil(nowMin, GetFallbackProtectTarget(line, nowMin)) <= YIELD_PROTECT_MINUTES;
        }

        private int GetFallbackProtectTarget(Entity line, int nowMin)
        {
            if (line != Entity.Null && IsWorkbenchTimetableApplied(line))
            {
                int nextManagedTarget = GetNextManagedDispatchTarget(line, nowMin);
                if (nextManagedTarget >= 0)
                    return nextManagedTarget;
            }

            return NextSlotMin(nowMin);
        }

        private bool ShouldRetireWaitingVehicleForFarFutureTarget(Entity line, int nowMin, int targetMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line) || targetMin < 0)
                return false;
            if (IsCurrentOrRecentDispatchableSlot(nowMin, targetMin))
                return false;

            return MinutesUntil(nowMin, targetMin) > GetWorkbenchOriginHoldLimitMinutes(line);
        }

        private string BuildOriginHoldRetireReason(Entity line, int nowMin, int targetMin)
        {
            int waitMinutes = MinutesUntil(nowMin, targetMin);
            int holdLimitMinutes = GetWorkbenchOriginHoldLimitMinutes(line);
            return "下一班仍需等待" + waitMinutes + "分钟，超出候车窗口" + holdLimitMinutes + "分钟";
        }

        private void SetBypassYieldState(Entity vehicle, Entity blocker, string lineTag, string stateTag)
        {
            if (vehicle == Entity.Null || blocker == Entity.Null)
                return;

            if (m_BypassYieldBlocker.TryGetValue(vehicle, out Entity previousBlocker) && previousBlocker == blocker)
                return;

            m_BypassYieldBlocker[vehicle] = blocker;
            log.Info("[待避] " + lineTag + " 车辆" + vehicle.Index
                + " state=" + stateTag
                + " 等待快车" + blocker.Index + " 先行");
        }

        private void ClearBypassYieldState(Entity vehicle, string releaseReason = null)
        {
            if (vehicle == Entity.Null || !m_BypassYieldBlocker.TryGetValue(vehicle, out Entity blocker))
                return;

            m_BypassYieldBlocker.Remove(vehicle);
            m_BypassHoldCadenceSnapshots.Remove(vehicle);
            Entity line = ResolveVehicleLine(vehicle);
            string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
            log.Info("[待避解除] " + lineTag + " 车辆" + vehicle.Index
                + " 解除快车待避"
                + (!string.IsNullOrWhiteSpace(releaseReason) ? " reason=" + releaseReason : string.Empty)
                + (blocker != Entity.Null ? " blocker=" + blocker.Index : string.Empty));
        }

        private bool TryGetCadencedBypassHoldDecision(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out bool shouldHold,
            out Entity blockerVehicle,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blockerVehicle = Entity.Null;
            canClearAfterExit = true;

            if (!TryGetBypassControlScope(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out BypassControlScope scope,
                    out _))
            {
                m_BypassHoldCadenceSnapshots.Remove(localVehicle);
                return true;
            }

            bool hasLatchedYield = m_BypassYieldBlocker.ContainsKey(localVehicle);
            if (TryReuseBypassHoldCadenceSnapshot(scope, hasLatchedYield, nowFrame, out shouldHold, out blockerVehicle, out canClearAfterExit))
            {
                return true;
            }

            shouldHold = ShouldHoldLocalVehicleForExpressBypass(scope, localWaypoints, nowFrame, out blockerVehicle);
            canClearAfterExit = (hasLatchedYield || shouldHold)
                && CanClearBypassYieldAfterStationExit(scope.Vehicle, scope.Line, localWaypoints, scope.WaypointIndex);

            StoreBypassHoldCadenceSnapshot(
                scope,
                hasLatchedYield,
                nowFrame,
                shouldHold,
                canClearAfterExit,
                blockerVehicle);
            return true;
        }

        private bool TryGetBypassControlScope(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            out BypassControlScope scope,
            out string failureReason)
        {
            scope = default;
            failureReason = null;

            if (localVehicle == Entity.Null
                || localLine == Entity.Null
                || currentWaypointIndex < 0)
            {
                failureReason = "local-line-invalid";
                return false;
            }

            if (!TryGetBypassWaypointContext(
                    localWaypoints,
                    currentWaypointIndex,
                    out Entity currentBypassBuilding,
                    out _,
                    out Entity nextBypassBuilding))
            {
                failureReason = "bypass-context-missing";
                return false;
            }

            scope = new BypassControlScope(
                localVehicle,
                localLine,
                currentWaypointIndex,
                currentBypassBuilding,
                nextBypassBuilding);
            return true;
        }

        private bool TryReuseBypassHoldCadenceSnapshot(
            BypassControlScope scope,
            bool hasLatchedYield,
            uint nowFrame,
            out bool shouldHold,
            out Entity blockerVehicle,
            out bool canClearAfterExit)
        {
            shouldHold = false;
            blockerVehicle = Entity.Null;
            canClearAfterExit = true;

            if (m_BypassHoldCadenceSnapshots.TryGetValue(scope.Vehicle, out BypassHoldCadenceSnapshot snapshot)
                && snapshot.Line == scope.Line
                && snapshot.WaypointIndex == scope.WaypointIndex
                && snapshot.CurrentBypassBuilding == scope.CurrentBypassBuilding
                && snapshot.NextBypassBuilding == scope.NextBypassBuilding)
            {
                if (snapshot.EvaluatedFrame == nowFrame
                    || (nowFrame < snapshot.ReevaluateAfterFrame
                        && (hasLatchedYield || !snapshot.ShouldHold)))
                {
                    shouldHold = snapshot.ShouldHold;
                    blockerVehicle = snapshot.Blocker;
                    canClearAfterExit = snapshot.CanClearAfterExit;
                    return true;
                }
            }

            return false;
        }

        private void StoreBypassHoldCadenceSnapshot(
            BypassControlScope scope,
            bool hasLatchedYield,
            uint nowFrame,
            bool shouldHold,
            bool canClearAfterExit,
            Entity blockerVehicle)
        {
            uint reevaluateAfterFrame = nowFrame + 1;
            if (hasLatchedYield && shouldHold)
                reevaluateAfterFrame = nowFrame + BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES;
            else if (!hasLatchedYield && !shouldHold)
                reevaluateAfterFrame = nowFrame + BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES;

            m_BypassHoldCadenceSnapshots[scope.Vehicle] = new BypassHoldCadenceSnapshot(
                scope.Line,
                scope.WaypointIndex,
                scope.CurrentBypassBuilding,
                scope.NextBypassBuilding,
                nowFrame,
                reevaluateAfterFrame,
                shouldHold,
                canClearAfterExit,
                blockerVehicle);
        }

        private bool FinalizeBypassDecision(
            Entity localVehicle,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            bool shouldYield,
            string reason,
            Entity blockerVehicle)
        {
            if (shouldYield
                && ShouldShadowVetoLiveBypassYield(localVehicle, out string shadowReason))
            {
                shouldYield = false;
                blockerVehicle = Entity.Null;
                reason = "shadow-veto-" + shadowReason;
            }

            LogBypassTrackModelDecisionComparison(localVehicle, shouldYield);
            LogBypassDecisionOnce(localVehicle, currentBypassBuilding, nextBypassBuilding, shouldYield, reason, blockerVehicle);
            return shouldYield;
        }

        private void LogBypassDecisionOnce(
            Entity localVehicle,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            bool shouldYield,
            string reason,
            Entity blockerVehicle)
        {
            if (localVehicle == Entity.Null)
                return;

            string decisionKey =
                (shouldYield ? "Y" : "N") + "|"
                + reason + "|"
                + currentBypassBuilding.Index + "|"
                + nextBypassBuilding.Index + "|"
                + blockerVehicle.Index;

            if (m_BypassDecisionLogCache.TryGetValue(localVehicle, out string previous) && previous == decisionKey)
                return;

            m_BypassDecisionLogCache[localVehicle] = decisionKey;
            Entity line = ResolveVehicleLine(localVehicle);
            string lineTag = line != Entity.Null ? "线路" + line.Index : "线路?";
            log.Info("[待避判定] " + lineTag + " 车辆" + localVehicle.Index
                + " result=" + (shouldYield ? "yield" : "pass")
                + " current=" + FormatBypassNodeLabel(currentBypassBuilding)
                + " next=" + FormatBypassNodeLabel(nextBypassBuilding)
                + " reason=" + reason
                + (blockerVehicle != Entity.Null ? " blocker=" + blockerVehicle.Index : string.Empty)
                + GetTrackModelLiveDecisionLogSuffix(localVehicle));
        }

        private string FormatBypassNodeLabel(Entity building)
        {
            if (building == Entity.Null)
                return "-";

            try
            {
                string name = m_NameSystem.GetRenderedLabelName(building);
                if (!string.IsNullOrWhiteSpace(name))
                    return name + "#" + building.Index;
            }
            catch
            {
            }

            return "建筑#" + building.Index;
        }

        private bool ShouldHoldLocalVehicleForExpressBypass(
            Entity localVehicle,
            Entity localLine,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            int currentWaypointIndex,
            uint nowFrame,
            out Entity blockerVehicle)
        {
            blockerVehicle = Entity.Null;
            if (localLine == Entity.Null
                || !IsWorkbenchTimetableApplied(localLine)
                || !IsAppliedWorkbenchLocalLine(localLine))
            {
                return FinalizeBypassDecision(localVehicle, Entity.Null, Entity.Null, false, "local-line-invalid", Entity.Null);
            }

            if (!TryGetBypassControlScope(
                    localVehicle,
                    localLine,
                    localWaypoints,
                    currentWaypointIndex,
                    out BypassControlScope scope,
                    out string failureReason))
            {
                return FinalizeBypassDecision(localVehicle, Entity.Null, Entity.Null, false, failureReason, Entity.Null);
            }

            return ShouldHoldLocalVehicleForExpressBypass(scope, localWaypoints, nowFrame, out blockerVehicle);
        }

        private bool ShouldHoldLocalVehicleForExpressBypass(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            uint nowFrame,
            out Entity blockerVehicle)
        {
            blockerVehicle = Entity.Null;
            if (!TryGetTrackModelBypassBaseline(scope, localWaypoints, out bool shouldYield, out string trackModelReason, out Entity trackModelBlocker))
            {
                return FinalizeBypassDecision(scope.Vehicle, scope.CurrentBypassBuilding, scope.NextBypassBuilding, false, "track-model-decision-unavailable", Entity.Null);
            }

            bool hasPreviousBlockOccupied = HasLocalVehicleInPreviousBlock(scope.Line, scope.Vehicle, scope.WaypointIndex);
            if (shouldYield
                && hasPreviousBlockOccupied
                && trackModelBlocker != Entity.Null
                && !IsExpressBlockerStillWithinBypassStation(trackModelBlocker)
                && TryProjectVehicleOntoLine(trackModelBlocker, scope.Line, localWaypoints, out LineDistanceProjection expressProjection)
                && TryProjectVehicleOntoLine(scope.Vehicle, scope.Line, localWaypoints, out LineDistanceProjection localProjection)
                && HasLocalVehicleAheadOfExpressWithoutBypass(
                    scope.Line,
                    scope.Vehicle,
                    localWaypoints,
                    scope.CurrentBypassBuilding,
                    expressProjection.DistanceMeters,
                    localProjection.DistanceMeters))
            {
                blockerVehicle = Entity.Null;
                return FinalizeBypassDecision(scope.Vehicle, scope.CurrentBypassBuilding, scope.NextBypassBuilding, false, "local-ahead-of-express-without-bypass", Entity.Null);
            }

            if (!shouldYield && hasPreviousBlockOccupied)
                return FinalizeBypassDecision(scope.Vehicle, scope.CurrentBypassBuilding, scope.NextBypassBuilding, false, "previous-block-occupied", Entity.Null);

            blockerVehicle = trackModelBlocker;
            return FinalizeBypassDecision(scope.Vehicle, scope.CurrentBypassBuilding, scope.NextBypassBuilding, shouldYield, trackModelReason, trackModelBlocker);
        }

        private bool IsExpressBlockerStillWithinBypassStation(Entity blockerVehicle)
        {
            if (blockerVehicle == Entity.Null)
                return false;

            Entity blockerLine = ResolveVehicleLine(blockerVehicle);
            if (blockerLine == Entity.Null)
                return false;

            var routeWaypointBuffers = GetBufferLookup<RouteWaypoint>(true);
            if (!routeWaypointBuffers.TryGetBuffer(blockerLine, out DynamicBuffer<RouteWaypoint> blockerWaypoints))
                return false;

            int blockerWaypointIndex = ComputeWpIndex(blockerVehicle, blockerWaypoints);
            if (blockerWaypointIndex < 0 && !m_CachedWpIdx.TryGetValue(blockerVehicle, out blockerWaypointIndex))
                return false;

            return IsVehicleWithinCurrentBypassStation(blockerVehicle, blockerLine, blockerWaypoints, blockerWaypointIndex);
        }

        private bool TryGetTrackModelBypassBaseline(
            BypassControlScope scope,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            out bool shouldYield,
            out string trackModelReason,
            out Entity trackModelBlocker)
        {
            shouldYield = false;
            trackModelReason = string.Empty;
            trackModelBlocker = Entity.Null;

            LogBypassTrackModelShadowOnce(
                scope.Vehicle,
                scope.Line,
                localWaypoints,
                scope.WaypointIndex,
                scope.CurrentBypassBuilding,
                scope.NextBypassBuilding);

            return TryGetTrackModelLiveBypassDecision(
                scope.Vehicle,
                out shouldYield,
                out trackModelReason,
                out trackModelBlocker);
        }

        private bool HasLocalVehicleAheadOfExpressWithoutBypass(
            Entity localLine,
            Entity currentLocalVehicle,
            DynamicBuffer<RouteWaypoint> localWaypoints,
            Entity currentBypassBuilding,
            float expressMetersOnLocalAxis,
            float currentLocalMeters)
        {
            if (!TryGetLineMileageModel(localLine, localWaypoints, out LineMileageModel localModel)
                || localModel.TotalDistanceMeters <= 0f)
            {
                return false;
            }

            if (!localModel.BuildingDistances.TryGetValue(currentBypassBuilding, out float currentBypassMeters))
                return false;

            float expressToCurrent = ForwardDistanceOnLoop(localModel.TotalDistanceMeters, expressMetersOnLocalAxis, currentLocalMeters);
            if (!(expressToCurrent > 0f) || expressToCurrent == float.MaxValue)
                return false;

            if (!TryGetLineRunningVehicleFrameSnapshot(localLine, localWaypoints, m_SimulationSystem.frameIndex, out LineRunningVehicleFrameSnapshot snapshot))
                return false;

            for (int i = 0; i < snapshot.Vehicles.Count; i++)
            {
                LineRunningVehicleSnapshot other = snapshot.Vehicles[i];
                if (other.Vehicle == currentLocalVehicle || !other.HasProjection)
                    continue;

                float expressToOther = ForwardDistanceOnLoop(localModel.TotalDistanceMeters, expressMetersOnLocalAxis, other.ProjectionDistanceMeters);
                if (!(expressToOther > 0f) || expressToOther >= expressToCurrent)
                    continue;

                if (!HasBypassWaypointBetweenDistances(localWaypoints, localModel, other.ProjectionDistanceMeters, currentLocalMeters)
                    && !HasBypassBuildingBetweenDistances(localModel, other.ProjectionDistanceMeters, currentBypassMeters))
                    return true;
            }

            return false;
        }

        private bool HasBypassBuildingBetweenDistances(LineMileageModel model, float fromMetersExclusive, float toMetersExclusive)
        {
            if (model == null || model.BypassStopNodeDistances == null || model.BypassStopNodeDistances.Length == 0)
                return false;

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, toMetersExclusive);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            for (int i = 0; i < model.BypassStopNodeDistances.Length; i++)
            {
                float fromToNode = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, model.BypassStopNodeDistances[i]);
                if (fromToNode > 0f && fromToNode < corridorLength)
                    return true;
            }

            return false;
        }

        private bool TryBuildBypassCorridorNodeList(
            LineMileageModel model,
            Entity currentBypassBuilding,
            Entity nextBypassBuilding,
            out List<CorridorNode> corridorNodes)
        {
            corridorNodes = null;
            if (model == null
                || !model.BuildingDistances.TryGetValue(currentBypassBuilding, out float startMeters)
                || !model.BuildingDistances.TryGetValue(nextBypassBuilding, out float endMeters))
            {
                return false;
            }

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, startMeters, endMeters);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            corridorNodes = new List<CorridorNode>();
            for (int i = 0; i < model.CorridorNodes.Count; i++)
            {
                CorridorNode node = model.CorridorNodes[i];
                float distanceFromStart = ForwardDistanceOnLoop(model.TotalDistanceMeters, startMeters, node.DistanceMeters);
                if (distanceFromStart <= 0f || distanceFromStart > corridorLength)
                    continue;
                corridorNodes.Add(node);
            }

            return corridorNodes.Count > 0;
        }

        private bool TryFindSharedBypassConflictNode(
            List<CorridorNode> localCorridorNodes,
            LineMileageModel expressModel,
            float expressCurrentMeters,
            out CorridorNode conflictNode,
            out float expressTargetMeters)
        {
            conflictNode = default;
            expressTargetMeters = 0f;
            if (localCorridorNodes == null || expressModel == null)
                return false;

            for (int i = 0; i < localCorridorNodes.Count; i++)
            {
                CorridorNode localNode = localCorridorNodes[i];
                if (localNode.Building == Entity.Null)
                    continue;
                if (!expressModel.BuildingDistances.TryGetValue(localNode.Building, out float candidateExpressMeters))
                    continue;

                float forward = ForwardDistanceOnLoop(expressModel.TotalDistanceMeters, expressCurrentMeters, candidateExpressMeters);
                if (!(forward > 0f) || forward == float.MaxValue)
                    continue;

                conflictNode = localNode;
                expressTargetMeters = candidateExpressMeters;
                return true;
            }

            return false;
        }

        private float GetLineLoopFramesEstimate(Entity line, DynamicBuffer<RouteWaypoint> waypoints)
        {
            float loopFrames = ReadLineLapCache(line);
            if (loopFrames > 0f)
                return loopFrames;

            if (TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile) && profile.m_BaseLoopFrames > 0f)
                return profile.m_BaseLoopFrames;

            return 0f;
        }

        private static float EstimateFramesForForwardDistance(float loopFrames, float totalDistanceMeters, float fromMeters, float toMeters)
        {
            if (!(loopFrames > 0f) || !(totalDistanceMeters > 0f))
                return float.MaxValue;

            float forwardDistance = ForwardDistanceOnLoop(totalDistanceMeters, fromMeters, toMeters);
            if (!(forwardDistance >= 0f) || forwardDistance == float.MaxValue)
                return float.MaxValue;

            return loopFrames * (forwardDistance / totalDistanceMeters);
        }

        private bool HasBypassWaypointBetweenDistances(
            DynamicBuffer<RouteWaypoint> waypoints,
            LineMileageModel model,
            float fromMetersExclusive,
            float toMetersExclusive)
        {
            if (model == null
                || model.TotalDistanceMeters <= 0f
                || model.BypassWaypointDistances == null
                || model.BypassWaypointDistances.Length == 0)
            {
                return false;
            }

            float corridorLength = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, toMetersExclusive);
            if (!(corridorLength > 0f) || corridorLength == float.MaxValue)
                return false;

            for (int waypointIndex = 0; waypointIndex < model.BypassWaypointDistances.Length; waypointIndex++)
            {
                float anchorMeters = model.BypassWaypointDistances[waypointIndex];
                float fromToAnchor = ForwardDistanceOnLoop(model.TotalDistanceMeters, fromMetersExclusive, anchorMeters);
                if (fromToAnchor > 0f && fromToAnchor < corridorLength)
                    return true;
            }

            return false;
        }

        private bool HasLocalVehicleInPreviousBlock(Entity line, Entity localVehicle, int currentWaypointIndex)
        {
            if (line == Entity.Null || currentWaypointIndex < 0)
                return false;

            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (!TryGetLineRunningVehicleFrameSnapshot(line, waypoints, m_SimulationSystem.frameIndex, out LineRunningVehicleFrameSnapshot snapshot))
                return false;

            int previousWaypointIndex = currentWaypointIndex == 0 ? -1 : currentWaypointIndex - 1;
            for (int i = 0; i < snapshot.Vehicles.Count; i++)
            {
                LineRunningVehicleSnapshot other = snapshot.Vehicles[i];
                if (other.Vehicle == localVehicle)
                    continue;

                if (other.NextWaypointIndex == currentWaypointIndex)
                    return true;
                if (previousWaypointIndex >= 0 && other.NextWaypointIndex == previousWaypointIndex && other.Boarding)
                    return true;
            }

            return false;
        }

        private bool TryGetLineRunningVehicleFrameSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineRunningVehicleFrameSnapshot snapshot)
        {
            snapshot = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            if (m_LineRunningVehicleFrameSnapshots.TryGetValue(line, out snapshot)
                && snapshot != null
                && snapshot.Frame == nowFrame
                && snapshot.Line == line)
            {
                return true;
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            if (snapshot == null)
            {
                snapshot = new LineRunningVehicleFrameSnapshot();
                m_LineRunningVehicleFrameSnapshots[line] = snapshot;
            }

            snapshot.Frame = nowFrame;
            snapshot.Line = line;
            snapshot.Vehicles.Clear();

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = routeVehicles[i].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    continue;
                if (!m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState) || vehicleState != VehicleState.Running)
                    continue;

                bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                    && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;

                int nextWaypointIndex;
                if (!TryGetRouteProgress(vehicle, out nextWaypointIndex, out _))
                {
                    nextWaypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWp) ? cachedWp : -1;
                }

                bool hasProjection = TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection projection);
                snapshot.Vehicles.Add(new LineRunningVehicleSnapshot(
                    vehicle,
                    nextWaypointIndex,
                    boarding,
                    hasProjection,
                    hasProjection ? projection.DistanceMeters : 0f));
            }

            return true;
        }

        private bool TryGetBypassWaypointContext(
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out Entity currentBypassBuilding,
            out int nextBypassWaypointIndex,
            out Entity nextBypassBuilding)
        {
            currentBypassBuilding = Entity.Null;
            nextBypassWaypointIndex = -1;
            nextBypassBuilding = Entity.Null;

            if (currentWaypointIndex < 0 || currentWaypointIndex >= waypoints.Length)
                return false;

            currentBypassBuilding = GetBypassBuildingForWaypoint(waypoints, currentWaypointIndex);
            if (currentBypassBuilding == Entity.Null)
                return false;

            for (int candidateIndex = currentWaypointIndex + 1; candidateIndex < waypoints.Length; candidateIndex++)
            {
                Entity candidateBuilding = GetBypassBuildingForWaypoint(waypoints, candidateIndex);
                if (candidateBuilding == Entity.Null || candidateBuilding == currentBypassBuilding)
                    continue;

                nextBypassWaypointIndex = candidateIndex;
                nextBypassBuilding = candidateBuilding;
                return true;
            }

            return false;
        }

        private Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            int nextBypassWaypointIndex,
            Entity currentBypassBuilding)
        {
            Dictionary<Entity, int> result = new Dictionary<Entity, int>();
            if (waypoints.Length == 0
                || currentWaypointIndex < 0
                || currentWaypointIndex >= waypoints.Length
                || nextBypassWaypointIndex < 0
                || nextBypassWaypointIndex >= waypoints.Length)
            {
                return result;
            }

            int cursor = (currentWaypointIndex + 1) % waypoints.Length;
            int guard = 0;
            while (guard++ < waypoints.Length)
            {
                Entity building = GetStationBuildingForWaypoint(waypoints, cursor);
                if (building != Entity.Null
                    && building != currentBypassBuilding
                    && !result.ContainsKey(building))
                {
                    result[building] = cursor;
                }

                if (cursor == nextBypassWaypointIndex)
                    break;

                cursor = (cursor + 1) % waypoints.Length;
            }

            return result;
        }

        private bool TryFindFutureSharedCorridorWaypoint(
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            Dictionary<Entity, int> localCorridorWaypoints,
            int startIndexInclusive,
            int endIndexInclusive,
            out int expressWaypointIndex,
            out int localWaypointIndex)
        {
            expressWaypointIndex = -1;
            localWaypointIndex = -1;

            if (expressWaypoints.Length == 0 || localCorridorWaypoints.Count == 0)
                return false;

            int start = math.clamp(startIndexInclusive, 0, expressWaypoints.Length - 1);
            int maxScanCount = expressWaypoints.Length;
            if (endIndexInclusive >= 0 && endIndexInclusive < expressWaypoints.Length)
            {
                int stepsToEnd = CountForwardWaypointSteps(expressWaypoints.Length, start, endIndexInclusive);
                if (stepsToEnd < 0)
                    return false;
                maxScanCount = stepsToEnd + 1;
            }

            for (int offset = 0; offset < maxScanCount; offset++)
            {
                int candidateIndex = (start + offset) % expressWaypoints.Length;
                Entity building = GetStationBuildingForWaypoint(expressWaypoints, candidateIndex);
                if (building == Entity.Null || !localCorridorWaypoints.TryGetValue(building, out int localIndex))
                    continue;

                expressWaypointIndex = candidateIndex;
                localWaypointIndex = localIndex;
                return true;
            }

            return false;
        }

        private Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            if (building == Entity.Null)
                building = ResolvePassingStationBuilding(stopEntity);
            if (building == Entity.Null || !IsBypassStation(building))
                return Entity.Null;

            return building;
        }

        private Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            return building != Entity.Null ? building : ResolvePassingStationBuilding(stopEntity);
        }

        private bool TryFindWaypointIndexForBypassBuilding(
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity building,
            int startIndexInclusive,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (building == Entity.Null || waypoints.Length == 0)
                return false;

            int start = math.clamp(startIndexInclusive, 0, waypoints.Length - 1);
            for (int offset = 0; offset < waypoints.Length; offset++)
            {
                int candidateIndex = (start + offset) % waypoints.Length;
                if (GetBypassBuildingForWaypoint(waypoints, candidateIndex) != building)
                    continue;

                waypointIndex = candidateIndex;
                return true;
            }

            return false;
        }

        private static int CountForwardWaypointSteps(int waypointCount, int startIndexInclusive, int targetIndexInclusive)
        {
            if (waypointCount <= 0
                || startIndexInclusive < 0
                || startIndexInclusive >= waypointCount
                || targetIndexInclusive < 0
                || targetIndexInclusive >= waypointCount)
            {
                return -1;
            }

            return (targetIndexInclusive - startIndexInclusive + waypointCount) % waypointCount;
        }

        private float EstimateDepartureToWaypointFrames(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int fromWaypointIndex,
            int targetWaypointIndex)
        {
            if (line == Entity.Null
                || waypoints.Length == 0
                || fromWaypointIndex < 0
                || fromWaypointIndex >= waypoints.Length
                || targetWaypointIndex < 0
                || targetWaypointIndex >= waypoints.Length
                || fromWaypointIndex == targetWaypointIndex)
            {
                return float.MaxValue;
            }

            if (TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile))
                return ComputeDepartureToWaypointFramesFromProfile(profile, fromWaypointIndex, targetWaypointIndex);

            float lineDurationFrames = ReadLineLapCache(line);
            if (lineDurationFrames <= 0f)
                return float.MaxValue;

            int hopCount = (targetWaypointIndex - fromWaypointIndex + waypoints.Length) % waypoints.Length;
            if (hopCount <= 0)
                hopCount = waypoints.Length;

            return lineDurationFrames * (hopCount / (float)waypoints.Length);
        }

        private float ComputeDepartureToWaypointFramesFromProfile(
            LineTimeProfileHeader profile,
            int fromWaypointIndex,
            int targetWaypointIndex)
        {
            int count = profile.m_Count;
            if (count == 0)
                return float.MaxValue;

            float remaining = 0f;
            int cursor = fromWaypointIndex;
            int guard = 0;

            while (cursor != targetWaypointIndex && guard++ < count)
            {
                remaining += m_LineTimeProfileSegmentFrames[profile.m_Offset + cursor];
                cursor = (cursor + 1) % count;
                if (cursor != targetWaypointIndex)
                    remaining += m_LineTimeProfileStopFrames[profile.m_Offset + cursor];
            }

            return guard <= count ? math.max(0f, remaining) : float.MaxValue;
        }

        private float EstimateRunningArrivalFramesToWaypoint(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int targetWaypointIndex,
            uint nowFrame,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            if (TryGetLineTimeProfile(line, waypoints, out LineTimeProfileHeader profile))
            {
                float scale = ResolveProfileScale(vehicle, profile.m_BaseLoopFrames);
                if (TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
                {
                    float profiledFrames = ComputeRemainingFramesToWaypointFromProfile(profile, nextWaypointIndex, segmentPosition, targetWaypointIndex);
                    if (profiledFrames != float.MaxValue)
                        return profiledFrames * scale;
                }

                int cachedWaypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWp) ? cachedWp : -1;
                float cachedFrames = ComputeDepartureToWaypointFramesFromProfile(profile, cachedWaypointIndex, targetWaypointIndex);
                if (cachedFrames != float.MaxValue)
                    return cachedFrames * scale;
            }

            return float.MaxValue;
        }

        private float ComputeRemainingFramesToWaypointFromProfile(
            LineTimeProfileHeader profile,
            int nextWaypointIndex,
            float segmentPosition,
            int targetWaypointIndex)
        {
            int count = profile.m_Count;
            if (count == 0
                || nextWaypointIndex < 0
                || nextWaypointIndex >= count
                || targetWaypointIndex < 0
                || targetWaypointIndex >= count)
            {
                return float.MaxValue;
            }

            int segmentIndex = nextWaypointIndex == 0 ? count - 1 : nextWaypointIndex - 1;
            float remaining = m_LineTimeProfileSegmentFrames[profile.m_Offset + segmentIndex] * (1f - math.saturate(segmentPosition));
            int cursor = nextWaypointIndex;
            int guard = 0;

            while (cursor != targetWaypointIndex && guard++ < count)
            {
                remaining += m_LineTimeProfileStopFrames[profile.m_Offset + cursor];
                remaining += m_LineTimeProfileSegmentFrames[profile.m_Offset + cursor];
                cursor = (cursor + 1) % count;
            }

            return guard <= count ? math.max(0f, remaining) : float.MaxValue;
        }

        private bool TryAssignCurrentOrLateSlotToWaitingVehicle(
            Entity line,
            Entity v,
            int nowMin,
            string lineTag,
            string stateTag,
            out int lateSlot)
        {
            lateSlot = -1;
            int prevSlot = GetPreviousSlotMin(nowMin);
            if (!IsCurrentOrRecentDispatchableSlot(nowMin, prevSlot)) return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs)) return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == v) continue;
                if (!EntityManager.Exists(other)) continue;
                if (!m_VehicleTargetMin.TryGetValue(other, out int otherTarget) || otherTarget != prevSlot) continue;

                if (m_VehicleState.TryGetValue(other, out var otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Idle || otherState == VehicleState.Holding))
                    return false;

                m_VehicleTargetMin[other] = -1;
                LogVehicleStateOnce(
                    m_LateDispatchLogCache,
                    v,
                    "LateDispatchTakeover|" + prevSlot + "|" + other.Index,
                    "[补发接管] " + lineTag + " 车辆" + v.Index
                        + " 接管班次" + SlotStr(prevSlot)
                        + " 释放车辆" + other.Index
                        + " state=" + (m_VehicleState.TryGetValue(other, out var releasedState) ? releasedState.ToString() : "?"));
            }

            m_VehicleTargetMin[v] = prevSlot;
            lateSlot = prevSlot;
            if (CanLateDispatchSlot(nowMin, prevSlot))
            {
                LogVehicleStateOnce(
                    m_LateDispatchLogCache,
                    v,
                    "LateDispatchCandidate|" + prevSlot + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + v.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + SlotStr(prevSlot)
                        + " 已过期" + GetSlotOverdueMinutes(nowMin, prevSlot) + "分钟");
            }
            return true;
        }

        private bool TryAssignUpcomingScheduledTargetToWaitingVehicle(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            EntityCommandBuffer ecb,
            out int assignedTarget)
        {
            assignedTarget = -1;
            if (line == Entity.Null || vehicle == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return false;

            int nextTarget = GetNextManagedDispatchTarget(line, nowMin);
            if (nextTarget < 0 || IsCurrentOrRecentDispatchableSlot(nowMin, nextTarget))
                return false;

            int waitMinutes = MinutesUntil(nowMin, nextTarget);
            if (waitMinutes > GetWorkbenchOriginHoldLimitMinutes(line))
                return false;
            if (IsDispatchTargetAlreadyOccupied(line, vehicle, nextTarget))
                return false;

            AssignSlot(vehicle, nextTarget, ecb);
            assignedTarget = nextTarget;
            LogVehicleStateOnce(
                m_LateDispatchLogCache,
                vehicle,
                "UpcomingTarget|" + nextTarget + "|" + stateTag,
                "[预分配] " + lineTag + " 车辆" + vehicle.Index
                    + " state=" + stateTag
                    + " 预分配未来班次" + SlotStr(nextTarget)
                    + " 距今" + waitMinutes + "分钟");
            return true;
        }

        private static int GetDispatchLeadMinutes(int nowMin, int targetMin)
        {
            int overdue = GetSlotOverdueMinutes(nowMin, targetMin);
            if (overdue <= SLOT_GRACE_MIN)
                return 0;

            return MinutesUntil(nowMin, targetMin);
        }

        private static int GetPreviousScheduledTargetMin(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int previous = -1;
            for (int i = 0; i < targets.Count; i++)
            {
                int target = targets[i];
                if (target <= nowMin)
                    previous = target;
                else
                    break;
            }

            return previous >= 0 ? previous : targets[targets.Count - 1];
        }

        private static int GetNextScheduledTargetMin(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            int bestTarget = targets[0];
            int bestDistance = MinutesUntil(nowMin, bestTarget);

            for (int i = 1; i < targets.Count; i++)
            {
                int target = targets[i];
                int distance = MinutesUntil(nowMin, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                }
            }

            return bestTarget;
        }

        private static int GetNextScheduledTargetIndex(int nowMin, IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count == 0)
                return -1;

            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i] >= nowMin)
                    return i;
            }

            return 0;
        }

        private static int GetScheduledHeadwayMinutes(IReadOnlyList<int> targets)
        {
            if (targets == null || targets.Count <= 1)
                return SLOT_INTERVAL;

            int bestGap = 1440;
            for (int i = 0; i < targets.Count; i++)
            {
                int current = targets[i];
                int next = targets[(i + 1) % targets.Count];
                int gap = (next - current + 1440) % 1440;
                if (gap <= 0)
                    continue;
                if (gap < bestGap)
                    bestGap = gap;
            }

            return bestGap < 1440 ? bestGap : SLOT_INTERVAL;
        }

        private int GetNextManagedDispatchTarget(Entity line, int nowMin)
        {
            if (line == Entity.Null || !IsWorkbenchTimetableApplied(line))
                return -1;

            int[] appliedTargets = GetAppliedWorkbenchDepartureMinutes(line);
            return GetNextScheduledTargetMin(nowMin, appliedTargets);
        }

        private bool TryAssignCurrentOrLateScheduledTargetToWaitingVehicle(
            Entity line,
            Entity v,
            int nowMin,
            string lineTag,
            string stateTag,
            IReadOnlyList<int> targets,
            out int lateTarget)
        {
            lateTarget = -1;
            int prevTarget = GetPreviousScheduledTargetMin(nowMin, targets);
            if (prevTarget < 0 || !IsCurrentOrRecentDispatchableSlot(nowMin, prevTarget))
                return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == v) continue;
                if (!EntityManager.Exists(other)) continue;
                if (!m_VehicleTargetMin.TryGetValue(other, out int otherTarget) || otherTarget != prevTarget) continue;

                if (m_VehicleState.TryGetValue(other, out var otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Idle || otherState == VehicleState.Holding))
                    return false;

                m_VehicleTargetMin[other] = -1;
                LogVehicleStateOnce(
                    m_LateDispatchLogCache,
                    v,
                    "LateDispatchTakeover|" + prevTarget + "|" + other.Index,
                    "[补发接管] " + lineTag + " 车辆" + v.Index
                        + " 接管班次" + SlotStr(prevTarget)
                        + " 释放车辆" + other.Index
                        + " state=" + (m_VehicleState.TryGetValue(other, out var releasedState) ? releasedState.ToString() : "?"));
            }

            m_VehicleTargetMin[v] = prevTarget;
            lateTarget = prevTarget;
            if (CanLateDispatchSlot(nowMin, prevTarget))
            {
                LogVehicleStateOnce(
                    m_LateDispatchLogCache,
                    v,
                    "LateDispatchCandidate|" + prevTarget + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + v.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + SlotStr(prevTarget)
                        + " 已过期" + GetSlotOverdueMinutes(nowMin, prevTarget) + "分钟");
            }

            return true;
        }

        private bool IsDispatchTargetAlreadyOccupied(Entity line, Entity vehicle, int targetMin)
        {
            if (line == Entity.Null || targetMin < 0)
                return false;

            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = rvs[i].m_Vehicle;
                if (other == vehicle || !EntityManager.Exists(other))
                    continue;

                if (m_VehicleCurrentSlot.TryGetValue(other, out int currentSlot) && currentSlot == targetMin)
                    return true;

                if (m_VehicleTargetMin.TryGetValue(other, out int targetSlot)
                    && targetSlot == targetMin
                    && m_VehicleState.TryGetValue(other, out var otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Holding || otherState == VehicleState.Idle))
                {
                    return true;
                }
            }

            return false;
        }

        private VehicleState InferInitialVehicleState(
            Entity v,
            DynamicBuffer<RouteWaypoint> wps,
            Game.Vehicles.PublicTransport pt0,
            bool boarding0,
            int initWpIdx,
            bool adoptExistingVehicles,
            out string reason)
        {
            bool arriving0 = (pt0.m_State & PublicTransportFlags.Arriving) != 0;

            if (initWpIdx == 0)
            {
                reason = "at-origin";
                return VehicleState.Holding;
            }
            if (boarding0)
            {
                float originDistAtBoarding = GetDistanceToOriginMeters(v, wps);
                if (originDistAtBoarding <= ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    if (!TryGetRouteProgress(v, out int nearOriginWp, out float nearOriginSeg)
                        || (nearOriginWp == 1 && nearOriginSeg <= 0.10f)
                        || nearOriginWp == 0)
                    {
                        reason = "boarding-origin-fallback";
                        return VehicleState.Holding;
                    }
                }
            }
            if (boarding0 && initWpIdx > 0)
            {
                reason = "boarding-midway";
                return VehicleState.Running;
            }
            if (!adoptExistingVehicles)
            {
                reason = "new-vehicle-default";
                return VehicleState.Preparing;
            }

            if ((pt0.m_State & PublicTransportFlags.Returning) != 0)
            {
                reason = "returning";
                return VehicleState.Retiring;
            }

            if (TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
            {
                float nearOriginDist = GetDistanceToOriginMeters(v, wps);
                bool nearOriginProgress = nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.05f);
                bool targetOriginLike = false;
                if (EntityManager.HasComponent<Target>(v))
                {
                    Entity routeTarget = EntityManager.GetComponentData<Target>(v).m_Target;
                    targetOriginLike = routeTarget == Entity.Null || routeTarget == wps[0].m_Waypoint;
                }
                if (nearOriginProgress && nearOriginDist <= ORIGIN_FORCE_IDLE_RADIUS_METERS
                    && (boarding0 || arriving0 || targetOriginLike))
                {
                    reason = "route-progress-origin-fallback wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                    return VehicleState.Holding;
                }
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return (boarding0 && nextWaypointIndex == 0) ? VehicleState.Holding : VehicleState.Running;
            }

            float originDist = GetDistanceToOriginMeters(v, wps);
            if (originDist > ORIGIN_CONGESTION_RADIUS_METERS)
            {
                reason = "far-from-origin " + originDist.ToString("F0") + "m";
                return VehicleState.Running;
            }

            if (!EntityManager.HasComponent<Target>(v))
            {
                reason = "no-target";
                return VehicleState.Preparing;
            }

            Entity target = EntityManager.GetComponentData<Target>(v).m_Target;
            if (target == Entity.Null || target == wps[0].m_Waypoint)
            {
                reason = "target-origin";
                return VehicleState.Preparing;
            }
            reason = "non-origin-target";
            return VehicleState.Running;
        }

        private float GetDistanceToOriginMeters(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (!EntityManager.HasComponent<Game.Objects.Transform>(v)) return float.MaxValue;
            Entity stopA = wps[0].m_Waypoint;
            if (EntityManager.HasComponent<Connected>(stopA))
                stopA = EntityManager.GetComponentData<Connected>(stopA).m_Connected;
            if (stopA == Entity.Null || !EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                return float.MaxValue;

            float3 vehiclePos = EntityManager.GetComponentData<Game.Objects.Transform>(v).m_Position;
            float3 stopPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
            return math.distance(vehiclePos, stopPos);
        }

        private bool ShouldSettleRunningAtOrigin(
            Entity v,
            DynamicBuffer<RouteWaypoint> wps,
            uint nowFrame,
            bool atA,
            bool boarding,
            bool lastBoarding,
            int targetMin)
        {
            bool waitingAtOrigin = atA
                && (boarding
                    || lastBoarding
                    || targetMin >= 0
                    || m_NearingTerminus.Contains(v)
                    || m_VehicleCurrentSlot.ContainsKey(v));

            if (!waitingAtOrigin)
            {
                if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }

                if (nextWaypointIndex != 0 || segmentPosition < ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS)
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }

                float originDist = GetDistanceToOriginMeters(v, wps);
                if (originDist > ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    return false;
                }
            }

            if (!m_OriginArrivalCandidateSinceFrame.TryGetValue(v, out uint sinceFrame))
            {
                m_OriginArrivalCandidateSinceFrame[v] = nowFrame;
                return false;
            }

            if ((nowFrame - sinceFrame) < ORIGIN_FORCE_IDLE_SETTLE_FRAMES)
                return false;

            return true;
        }

        private bool IsBorderlineOriginArrivalCandidate(Entity v, DynamicBuffer<RouteWaypoint> wps)
        {
            if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                return false;
            if (nextWaypointIndex != 0 || segmentPosition < ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS)
                return false;
            return GetDistanceToOriginMeters(v, wps) <= ORIGIN_FORCE_IDLE_RADIUS_METERS;
        }

        private bool HasBorderlineOriginArrivalCandidate(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            float slotFramesAway,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            float waitFrames = ORIGIN_ARRIVAL_HOLD_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out var rvs))
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            for (int i = 0; i < rvs.Length; i++)
            {
                Entity v = rvs[i].m_Vehicle;
                if (!EntityManager.Exists(v)) continue;
                if (!m_VehicleState.TryGetValue(v, out var state) || state != VehicleState.Running) continue;
                if (m_VehicleTargetMin.TryGetValue(v, out int target) && target >= 0) continue;
                if (m_LaunchCooldownUntil.TryGetValue(v, out uint cooldownUntil) && nowFrame < cooldownUntil) continue;
                if (!IsBorderlineOriginArrivalCandidate(v, wps)) continue;

                float eta = EstimateRunningArrivalFrames(v, line, wps, nowFrame, lineDurationFrames, lineHasHistory);
                if (eta == float.MaxValue) continue;
                if (eta <= slotFramesAway + waitFrames)
                    return true;
            }

            return false;
        }

        private bool ShouldHoldSpawnForNearestRunningCandidate(
            Entity nearestVehicle,
            VehicleState nearestState,
            float nearestETA,
            DynamicBuffer<RouteWaypoint> wps)
        {
            if (nearestVehicle == Entity.Null || nearestState != VehicleState.Running)
                return false;
            if (nearestETA == float.MaxValue)
                return false;

            float waitFrames = ORIGIN_ARRIVAL_HOLD_MINUTES * (float)SIM_FRAMES_PER_MINUTE;
            if (nearestETA > waitFrames)
                return false;

            uint nowFrame = m_SimulationSystem.frameIndex;
            if (m_LaunchCooldownUntil.TryGetValue(nearestVehicle, out uint cooldownUntil) && nowFrame < cooldownUntil)
                return false;

            if (GetDistanceToOriginMeters(nearestVehicle, wps) <= ORIGIN_CONGESTION_RADIUS_METERS)
                return true;

            if (TryGetRouteProgress(nearestVehicle, out int nextWaypointIndex, out float segmentPosition))
                return nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.10f);

            return false;
        }

        private bool IsWaitingForcedOriginDwell(Entity v, uint nowFrame)
        {
            return m_ForcedOriginReadyFrame.TryGetValue(v, out uint readyFrame) && nowFrame < readyFrame;
        }

        private bool ShouldForceMidStopDwellTimeout(
            Entity vehicle,
            Entity line,
            int currentWaypointIndex,
            bool boarding,
            uint nowFrame,
            int waypointCount,
            out uint dwellSinceFrame,
            out uint dwellDeadlineFrame,
            out int maxDwellMinutes)
        {
            dwellSinceFrame = 0;
            dwellDeadlineFrame = 0;
            maxDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line);
            if (!boarding || currentWaypointIndex <= 0 || currentWaypointIndex >= waypointCount - 1)
            {
                if (m_StopDwellStartFrame.Remove(vehicle))
                {
                    log.Info("[停站计时结束] 线路" + line.Index
                        + " 车辆" + vehicle.Index
                        + " boarding=" + boarding
                        + " wp=" + currentWaypointIndex
                        + "/" + (waypointCount - 1)
                        + " nowFrame=" + nowFrame);
                }
                return false;
            }

            if (maxDwellMinutes <= 0)
                return false;

            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out dwellSinceFrame))
            {
                dwellSinceFrame = nowFrame;
                m_StopDwellStartFrame[vehicle] = dwellSinceFrame;
                dwellDeadlineFrame = dwellSinceFrame + (uint)(maxDwellMinutes * SIM_FRAMES_PER_MINUTE);
                log.Info("[停站计时开始] 线路" + line.Index
                    + " 车辆" + vehicle.Index
                    + " wp=" + currentWaypointIndex
                    + "/" + (waypointCount - 1)
                    + " 限时=" + maxDwellMinutes + "分钟"
                    + " deadlineFrame=" + dwellDeadlineFrame);
                return false;
            }

            dwellDeadlineFrame = dwellSinceFrame + (uint)(maxDwellMinutes * SIM_FRAMES_PER_MINUTE);
            return nowFrame >= dwellDeadlineFrame;
        }

        private bool TryGetRouteProgress(Entity transportVehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            if (EntityManager.HasComponent<CurrentRoute>(transportVehicle))
            {
                var currentRoute = EntityManager.GetComponentData<CurrentRoute>(transportVehicle);
                if (EntityManager.HasComponent<PathInformation>(transportVehicle))
                {
                    var pathInfo = EntityManager.GetComponentData<PathInformation>(transportVehicle);
                    if (EntityManager.HasComponent<Waypoint>(pathInfo.m_Destination)
                        && EntityManager.HasBuffer<RouteSegment>(currentRoute.m_Route))
                    {
                        var destinationWp = EntityManager.GetComponentData<Waypoint>(pathInfo.m_Destination);
                        DynamicBuffer<RouteSegment> routeSegments = EntityManager.GetBuffer<RouteSegment>(currentRoute.m_Route, true);
                        if (routeSegments.Length > 0)
                        {
                            nextWaypointIndex = destinationWp.m_Index;
                            int index = math.select(nextWaypointIndex - 1, routeSegments.Length - 1, nextWaypointIndex == 0);
                            RouteSegment routeSegment = routeSegments[index];
                            if (EntityManager.HasBuffer<PathElement>(routeSegment.m_Segment))
                            {
                                DynamicBuffer<PathElement> segmentPath = EntityManager.GetBuffer<PathElement>(routeSegment.m_Segment, true);
                                if (segmentPath.Length != 0)
                                {
                                    int remaining = 0;
                                    if (EntityManager.HasComponent<PathOwner>(transportVehicle)
                                        && EntityManager.HasBuffer<PathElement>(transportVehicle))
                                    {
                                        var pathOwner = EntityManager.GetComponentData<PathOwner>(transportVehicle);
                                        DynamicBuffer<PathElement> vehiclePath = EntityManager.GetBuffer<PathElement>(transportVehicle, true);
                                        remaining += math.max(0, vehiclePath.Length - pathOwner.m_ElementIndex);
                                    }
                                    if (EntityManager.HasBuffer<CarNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<CarNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<TrainNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<TrainNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<WatercraftNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<WatercraftNavigationLane>(transportVehicle, true).Length;
                                    else if (EntityManager.HasBuffer<AircraftNavigationLane>(transportVehicle))
                                        remaining += EntityManager.GetBuffer<AircraftNavigationLane>(transportVehicle, true).Length;

                                    segmentPosition = math.saturate((float)(segmentPath.Length - remaining) / (float)segmentPath.Length);
                                    return true;
                                }
                            }
                        }
                    }
                }

                if (EntityManager.HasComponent<Target>(transportVehicle))
                {
                    var target = EntityManager.GetComponentData<Target>(transportVehicle);
                    if (EntityManager.HasComponent<Waypoint>(target.m_Target)
                        && EntityManager.HasBuffer<RouteWaypoint>(currentRoute.m_Route))
                    {
                        var targetWp = EntityManager.GetComponentData<Waypoint>(target.m_Target);
                        DynamicBuffer<RouteWaypoint> routeWaypoints = EntityManager.GetBuffer<RouteWaypoint>(currentRoute.m_Route, true);
                        if (routeWaypoints.Length > 0)
                        {
                            nextWaypointIndex = targetWp.m_Index;
                            int index2 = math.select(nextWaypointIndex - 1, routeWaypoints.Length - 1, nextWaypointIndex == 0);
                            RouteWaypoint previousWaypoint = routeWaypoints[index2];
                            if (EntityManager.HasComponent<Game.Objects.Transform>(transportVehicle)
                                && EntityManager.HasComponent<Position>(previousWaypoint.m_Waypoint)
                                && EntityManager.HasComponent<Position>(target.m_Target))
                            {
                                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(transportVehicle);
                                var prevPos = EntityManager.GetComponentData<Position>(previousWaypoint.m_Waypoint);
                                var targetPos = EntityManager.GetComponentData<Position>(target.m_Target);
                                float toTarget = math.distance(transform.m_Position, targetPos.m_Position);
                                float segmentLength = math.max(1f, math.distance(prevPos.m_Position, targetPos.m_Position));
                                segmentPosition = math.saturate((segmentLength - toTarget) / segmentLength);
                                return true;
                            }
                        }
                    }
                }
            }

            nextWaypointIndex = 0;
            segmentPosition = 0f;
            return false;
        }

        private static ulong MixLineSignature(ulong hash, int value)
        {
            return (hash ^ (uint)value) * 1099511628211UL;
        }

        private ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> wps)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, wps.Length);
            for (int i = 0; i < wps.Length; i++)
            {
                hash = MixLineSignature(hash, wps[i].m_Waypoint.Index);
            }
            return hash;
        }

        private ulong ComputeLineMileageSignature(Entity line, DynamicBuffer<RouteWaypoint> waypoints, DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = ComputeLineWaypointSignature(waypoints);
            hash = MixLineSignature(hash, line.Index);
            hash = MixLineSignature(hash, segments.Length);
            for (int i = 0; i < segments.Length; i++)
            {
                hash = MixLineSignature(hash, segments[i].m_Segment.Index);
            }
            return hash;
        }

        private ulong ComputeSharedLocalCorridorSignature()
        {
            ulong hash = 1469598103934665603UL;
            List<Entity> localLines = new List<Entity>();
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !EntityManager.HasBuffer<RouteSegment>(line)
                    || !IsAppliedWorkbenchLocalLine(line))
                {
                    continue;
                }

                if (!localLines.Contains(line))
                    localLines.Add(line);
            }

            localLines.Sort((a, b) => a.Index.CompareTo(b.Index));
            hash = MixLineSignature(hash, localLines.Count);
            for (int i = 0; i < localLines.Count; i++)
            {
                Entity line = localLines[i];
                hash = MixLineSignature(hash, line.Index);
                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                for (int j = 0; j < waypoints.Length; j++)
                {
                    Entity building = GetStationBuildingForWaypoint(waypoints, j);
                    hash = MixLineSignature(hash, building.Index);
                }
            }

            return hash;
        }

        private bool TryGetSharedLocalCorridorGraph(out SharedLocalCorridorGraph graph)
        {
            ulong signature = ComputeSharedLocalCorridorSignature();
            if (m_SharedLocalCorridorGraph != null
                && m_SharedLocalCorridorGraph.Signature == signature)
            {
                graph = m_SharedLocalCorridorGraph;
                return graph.Adjacency.Count > 0;
            }

            graph = BuildSharedLocalCorridorGraph(signature);
            m_SharedLocalCorridorGraph = graph;
            return graph != null && graph.Adjacency.Count > 0;
        }

        private SharedLocalCorridorGraph BuildSharedLocalCorridorGraph(ulong signature)
        {
            SharedLocalCorridorGraph graph = new SharedLocalCorridorGraph
            {
                Signature = signature
            };

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !EntityManager.HasBuffer<RouteSegment>(line)
                    || !IsAppliedWorkbenchLocalLine(line))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
                DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
                if (!TryBuildWaypointDistanceData(waypoints, segments, out Entity[] waypointBuildings, out float[] waypointDistances, out _))
                    continue;

                for (int i = 0; i < waypointBuildings.Length; i++)
                {
                    int nextIndex = (i + 1) % waypointBuildings.Length;
                    Entity startBuilding = waypointBuildings[i];
                    Entity endBuilding = waypointBuildings[nextIndex];
                    if (startBuilding == Entity.Null || endBuilding == Entity.Null || startBuilding == endBuilding)
                        continue;

                    float startDistance = waypointDistances[i];
                    float endDistance = nextIndex == 0 ? waypointDistances[i] + ReadRouteSegmentDistanceMeters(segments[i].m_Segment, waypoints, i) : waypointDistances[nextIndex];
                    float segmentDistance = math.max(1f, endDistance - startDistance);
                    AddSharedLocalCorridorEdge(graph, startBuilding, endBuilding, segmentDistance);
                }
            }

            return graph;
        }

        private static void AddSharedLocalCorridorEdge(SharedLocalCorridorGraph graph, Entity fromBuilding, Entity toBuilding, float distanceMeters)
        {
            if (fromBuilding == Entity.Null || toBuilding == Entity.Null || distanceMeters <= 0f)
                return;

            if (!graph.Adjacency.TryGetValue(fromBuilding, out List<SharedLocalCorridorEdge> edges))
            {
                edges = new List<SharedLocalCorridorEdge>();
                graph.Adjacency[fromBuilding] = edges;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].ToBuilding != toBuilding)
                    continue;

                if (distanceMeters < edges[i].DistanceMeters)
                    edges[i] = new SharedLocalCorridorEdge { ToBuilding = toBuilding, DistanceMeters = distanceMeters };
                return;
            }

            edges.Add(new SharedLocalCorridorEdge
            {
                ToBuilding = toBuilding,
                DistanceMeters = distanceMeters
            });
        }

        private bool TryFindShortestLocalCorridorPath(
            SharedLocalCorridorGraph graph,
            Entity startBuilding,
            Entity endBuilding,
            out List<Entity> pathBuildings,
            out List<float> pathEdgeDistances)
        {
            pathBuildings = null;
            pathEdgeDistances = null;
            if (graph == null
                || startBuilding == Entity.Null
                || endBuilding == Entity.Null
                || startBuilding == endBuilding
                || !graph.Adjacency.ContainsKey(startBuilding))
            {
                return false;
            }

            Dictionary<Entity, float> distances = new Dictionary<Entity, float>();
            Dictionary<Entity, Entity> previous = new Dictionary<Entity, Entity>();
            Dictionary<Entity, float> previousEdgeDistance = new Dictionary<Entity, float>();
            List<Entity> open = new List<Entity> { startBuilding };
            distances[startBuilding] = 0f;

            while (open.Count > 0)
            {
                int bestIndex = 0;
                float bestDistance = distances[open[0]];
                for (int i = 1; i < open.Count; i++)
                {
                    float candidateDistance = distances[open[i]];
                    if (candidateDistance < bestDistance)
                    {
                        bestDistance = candidateDistance;
                        bestIndex = i;
                    }
                }

                Entity current = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (current == endBuilding)
                    break;

                if (!graph.Adjacency.TryGetValue(current, out List<SharedLocalCorridorEdge> edges))
                    continue;

                for (int i = 0; i < edges.Count; i++)
                {
                    SharedLocalCorridorEdge edge = edges[i];
                    float nextDistance = bestDistance + edge.DistanceMeters;
                    if (distances.TryGetValue(edge.ToBuilding, out float knownDistance) && knownDistance <= nextDistance)
                        continue;

                    distances[edge.ToBuilding] = nextDistance;
                    previous[edge.ToBuilding] = current;
                    previousEdgeDistance[edge.ToBuilding] = edge.DistanceMeters;
                    if (!open.Contains(edge.ToBuilding))
                        open.Add(edge.ToBuilding);
                }
            }

            if (!distances.ContainsKey(endBuilding))
                return false;

            pathBuildings = new List<Entity>();
            pathEdgeDistances = new List<float>();
            Entity cursor = endBuilding;
            while (cursor != startBuilding)
            {
                pathBuildings.Add(cursor);
                if (!previous.TryGetValue(cursor, out Entity prev))
                    return false;
                pathEdgeDistances.Add(previousEdgeDistance[cursor]);
                cursor = prev;
            }
            pathBuildings.Add(startBuilding);
            pathBuildings.Reverse();
            pathEdgeDistances.Reverse();
            return true;
        }

        private bool TryBuildWaypointDistanceData(
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            out Entity[] waypointBuildings,
            out float[] waypointDistances,
            out float totalDistanceMeters)
        {
            int count = waypoints.Length;
            waypointBuildings = Array.Empty<Entity>();
            waypointDistances = Array.Empty<float>();
            totalDistanceMeters = 0f;
            if (count == 0 || segments.Length != count)
                return false;

            waypointBuildings = new Entity[count];
            waypointDistances = new float[count];
            float cumulative = 0f;
            waypointDistances[0] = 0f;
            waypointBuildings[0] = GetStationBuildingForWaypoint(waypoints, 0);
            for (int waypointIndex = 1; waypointIndex < count; waypointIndex++)
            {
                cumulative += ReadRouteSegmentDistanceMeters(segments[waypointIndex - 1].m_Segment, waypoints, waypointIndex - 1);
                waypointDistances[waypointIndex] = cumulative;
                waypointBuildings[waypointIndex] = GetStationBuildingForWaypoint(waypoints, waypointIndex);
            }

            cumulative += ReadRouteSegmentDistanceMeters(segments[count - 1].m_Segment, waypoints, count - 1);
            totalDistanceMeters = math.max(1f, cumulative);
            return true;
        }

        private bool TryGetLineMileageModel(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineMileageModel model)
        {
            model = null;
            if (m_CorridorModelFaulted)
                return false;

            try
            {
            if (line == Entity.Null
                || waypoints.Length == 0
                || !EntityManager.HasBuffer<RouteSegment>(line))
            {
                return false;
            }

            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, true);
            if (segments.Length != waypoints.Length)
                return false;

            ulong signature = ComputeLineMileageSignature(line, waypoints, segments);
            if (TryGetSharedLocalCorridorGraph(out SharedLocalCorridorGraph sharedGraph) && sharedGraph != null)
            {
                signature = MixLineSignature(signature, (int)(sharedGraph.Signature & 0x7FFFFFFF));
                signature = MixLineSignature(signature, (int)((sharedGraph.Signature >> 32) & 0x7FFFFFFF));
            }
            if (m_LineMileageModels.TryGetValue(line, out model)
                && model != null
                && model.Signature == signature
                && model.WaypointDistances.Length == waypoints.Length
                && model.TotalDistanceMeters > 0f)
            {
                return true;
            }

            if (TryReadLineMileageModelFromBuffer(line, signature, waypoints.Length, out model))
            {
                m_LineMileageModels[line] = model;
                return true;
            }

            model = BuildLineMileageModel(line, waypoints, segments, signature, sharedGraph);
            if (model == null || model.TotalDistanceMeters <= 0f)
                return false;

            m_LineMileageModels[line] = model;
            LogLineCorridorModel(line, model);
            WriteLineMileageModelToBuffer(line, model);
            return true;
            }
            catch (Exception ex)
            {
                m_CorridorModelFaulted = true;
                m_LineMileageModels.Clear();
                m_SharedLocalCorridorGraph = null;
                log.Info("[走廊模型异常] 已停用走廊图建模: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private bool TryReadLineMileageModelFromBuffer(Entity line, ulong signature, int waypointCount, out LineMileageModel model)
        {
            model = null;
            return false;
        }

        private LineMileageModel BuildLineMileageModel(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ulong signature,
            SharedLocalCorridorGraph sharedGraph)
        {
            int count = waypoints.Length;
            if (count == 0 || segments.Length != count)
                return null;

            if (!TryBuildWaypointDistanceData(waypoints, segments, out Entity[] waypointBuildings, out _, out _))
                return null;

            float[] anchors = new float[count];
            List<CorridorNode> corridorNodes = new List<CorridorNode>(count * 2);
            Dictionary<Entity, float> buildingDistances = new Dictionary<Entity, float>();
            float cumulative = 0f;
            anchors[0] = 0f;
            if (waypointBuildings[0] != Entity.Null)
            {
                CorridorNode startNode = new CorridorNode
                {
                    Building = waypointBuildings[0],
                    DistanceMeters = 0f,
                    IsStopNode = true
                };
                corridorNodes.Add(startNode);
                buildingDistances[startNode.Building] = 0f;
            }

            for (int waypointIndex = 0; waypointIndex < count; waypointIndex++)
            {
                int nextWaypointIndex = (waypointIndex + 1) % count;
                Entity startBuilding = waypointBuildings[waypointIndex];
                Entity endBuilding = waypointBuildings[nextWaypointIndex];
                float fallbackDistance = ReadRouteSegmentDistanceMeters(segments[waypointIndex].m_Segment, waypoints, waypointIndex);

                bool expanded = false;
                if (startBuilding != Entity.Null
                    && endBuilding != Entity.Null
                    && startBuilding != endBuilding
                    && sharedGraph != null
                    && TryFindShortestLocalCorridorPath(sharedGraph, startBuilding, endBuilding, out List<Entity> pathBuildings, out List<float> pathEdges)
                    && pathBuildings.Count >= 2)
                {
                    for (int pathIndex = 1; pathIndex < pathBuildings.Count; pathIndex++)
                    {
                        cumulative += pathEdges[pathIndex - 1];
                        bool isStopNode = pathIndex == pathBuildings.Count - 1;
                        CorridorNode node = new CorridorNode
                        {
                            Building = pathBuildings[pathIndex],
                            DistanceMeters = cumulative,
                            IsStopNode = isStopNode
                        };

                        if (nextWaypointIndex != 0 || pathIndex != pathBuildings.Count - 1)
                        {
                            if (corridorNodes.Count == 0 || corridorNodes[corridorNodes.Count - 1].Building != node.Building)
                                corridorNodes.Add(node);
                            else if (node.IsStopNode)
                            {
                                CorridorNode lastNode = corridorNodes[corridorNodes.Count - 1];
                                lastNode.IsStopNode = true;
                                lastNode.DistanceMeters = node.DistanceMeters;
                                corridorNodes[corridorNodes.Count - 1] = lastNode;
                            }

                            if (node.Building != Entity.Null && !buildingDistances.ContainsKey(node.Building))
                                buildingDistances[node.Building] = node.DistanceMeters;
                        }

                        if (isStopNode && nextWaypointIndex != 0)
                            anchors[nextWaypointIndex] = cumulative;
                    }

                    expanded = true;
                }

                if (!expanded)
                {
                    cumulative += fallbackDistance;
                    if (nextWaypointIndex != 0)
                        anchors[nextWaypointIndex] = cumulative;

                    if (endBuilding != Entity.Null && nextWaypointIndex != 0)
                    {
                        CorridorNode node = new CorridorNode
                        {
                            Building = endBuilding,
                            DistanceMeters = cumulative,
                            IsStopNode = true
                        };
                        if (corridorNodes.Count == 0 || corridorNodes[corridorNodes.Count - 1].Building != node.Building)
                            corridorNodes.Add(node);
                        if (!buildingDistances.ContainsKey(node.Building))
                            buildingDistances[node.Building] = node.DistanceMeters;
                    }
                }
            }

            cumulative = math.max(1f, cumulative);
            float[] bypassWaypointDistances = BuildBypassWaypointDistanceCache(waypoints, anchors);
            float[] bypassStopNodeDistances = BuildBypassStopNodeDistanceCache(corridorNodes);

            return new LineMileageModel
            {
                Signature = signature,
                TotalDistanceMeters = cumulative,
                WaypointDistances = anchors,
                BypassWaypointDistances = bypassWaypointDistances,
                BypassStopNodeDistances = bypassStopNodeDistances,
                CorridorNodes = corridorNodes,
                BuildingDistances = buildingDistances
            };
        }

        private float[] BuildBypassWaypointDistanceCache(DynamicBuffer<RouteWaypoint> waypoints, float[] waypointDistances)
        {
            if (waypoints.Length == 0 || waypointDistances == null || waypointDistances.Length != waypoints.Length)
                return Array.Empty<float>();

            List<float> distances = new List<float>(waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (GetBypassBuildingForWaypoint(waypoints, i) == Entity.Null)
                    continue;

                distances.Add(waypointDistances[i]);
            }

            return distances.Count > 0 ? distances.ToArray() : Array.Empty<float>();
        }

        private float[] BuildBypassStopNodeDistanceCache(List<CorridorNode> corridorNodes)
        {
            if (corridorNodes == null || corridorNodes.Count == 0)
                return Array.Empty<float>();

            List<float> distances = new List<float>(corridorNodes.Count);
            for (int i = 0; i < corridorNodes.Count; i++)
            {
                CorridorNode node = corridorNodes[i];
                if (!node.IsStopNode || node.Building == Entity.Null || !IsBypassStation(node.Building))
                    continue;

                distances.Add(node.DistanceMeters);
            }

            return distances.Count > 0 ? distances.ToArray() : Array.Empty<float>();
        }

        private void LogLineCorridorModel(Entity line, LineMileageModel model)
        {
            if (line == Entity.Null || model == null || model.CorridorNodes.Count == 0)
                return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[走廊模型] 线路").Append(line.Index)
              .Append(" 总长=").Append((model.TotalDistanceMeters / 1000f).ToString("F2")).Append("km")
              .Append(" 节点=");

            for (int i = 0; i < model.CorridorNodes.Count; i++)
            {
                CorridorNode node = model.CorridorNodes[i];
                if (i > 0)
                    sb.Append(" -> ");

                string label = "建筑" + node.Building.Index;
                if (node.Building != Entity.Null)
                {
                    try
                    {
                        string name = m_NameSystem.GetRenderedLabelName(node.Building);
                        if (!string.IsNullOrWhiteSpace(name))
                            label = name;
                    }
                    catch
                    {
                    }
                }

                sb.Append(label)
                  .Append("@")
                  .Append((node.DistanceMeters / 1000f).ToString("F2"))
                  .Append("km");

                if (node.IsStopNode)
                    sb.Append("[停]");
            }

            log.Info(sb.ToString());
        }

        private float ReadRouteSegmentDistanceMeters(Entity segmentEntity, DynamicBuffer<RouteWaypoint> waypoints, int segmentIndex)
        {
            if (segmentEntity != Entity.Null
                && EntityManager.HasComponent<PathInformation>(segmentEntity))
            {
                float distance = EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Distance;
                if (distance > 1f)
                    return distance;
            }

            if (waypoints.Length == 0)
                return 1f;

            int count = waypoints.Length;
            int startWaypointIndex = segmentIndex;
            int endWaypointIndex = (segmentIndex + 1) % count;
            Entity startWaypoint = waypoints[startWaypointIndex].m_Waypoint;
            Entity endWaypoint = waypoints[endWaypointIndex].m_Waypoint;
            if (EntityManager.HasComponent<Position>(startWaypoint) && EntityManager.HasComponent<Position>(endWaypoint))
            {
                float distance = math.distance(
                    EntityManager.GetComponentData<Position>(startWaypoint).m_Position,
                    EntityManager.GetComponentData<Position>(endWaypoint).m_Position);
                if (distance > 1f)
                    return distance;
            }

            return 1f;
        }

        private void WriteLineMileageModelToBuffer(Entity line, LineMileageModel model)
        {
            return;
        }

        private bool TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineDistanceProjection projection)
        {
            projection = default;
            if (!TryGetLineMileageModel(line, waypoints, out LineMileageModel model)
                || model.TotalDistanceMeters <= 0f
                || model.WaypointDistances.Length != waypoints.Length)
            {
                return false;
            }

            if (!TryGetRouteProgress(vehicle, out int nextWaypointIndex, out float segmentPosition))
            {
                if (!m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypointIndex)
                    || cachedWaypointIndex < 0
                    || cachedWaypointIndex >= waypoints.Length)
                {
                    return false;
                }

                nextWaypointIndex = cachedWaypointIndex;
                segmentPosition = 0f;
            }

            nextWaypointIndex = math.clamp(nextWaypointIndex, 0, waypoints.Length - 1);
            int previousWaypointIndex = nextWaypointIndex == 0 ? waypoints.Length - 1 : nextWaypointIndex - 1;
            float previousMeters = model.WaypointDistances[previousWaypointIndex];
            float nextMeters = model.WaypointDistances[nextWaypointIndex];
            float segmentMeters = nextWaypointIndex == 0
                ? math.max(1f, model.TotalDistanceMeters - previousMeters)
                : math.max(1f, nextMeters - previousMeters);

            float distanceMeters = previousMeters + segmentMeters * math.saturate(segmentPosition);

            projection = new LineDistanceProjection
            {
                TotalDistanceMeters = model.TotalDistanceMeters,
                DistanceMeters = math.clamp(distanceMeters, 0f, math.max(0f, model.TotalDistanceMeters - 0.01f)),
                Progress01 = math.saturate(distanceMeters / model.TotalDistanceMeters),
                NextWaypointIndex = nextWaypointIndex,
                SegmentPosition = math.saturate(segmentPosition)
            };
            return true;
        }

        private static float ForwardDistanceOnLoop(float totalDistanceMeters, float fromMeters, float toMeters)
        {
            if (totalDistanceMeters <= 0f)
                return float.MaxValue;

            float forward = toMeters - fromMeters;
            if (forward < 0f)
                forward += totalDistanceMeters;
            return forward;
        }

        private ulong ComputeLineProfileSignature(DynamicBuffer<RouteWaypoint> wps, DynamicBuffer<RouteSegment> segs)
        {
            ulong hash = 1469598103934665603UL;
            hash = MixLineSignature(hash, wps.Length);
            hash = MixLineSignature(hash, segs.Length);
            int count = math.min(wps.Length, segs.Length);
            for (int i = 0; i < count; i++)
            {
                hash = MixLineSignature(hash, wps[i].m_Waypoint.Index);
                hash = MixLineSignature(hash, segs[i].m_Segment.Index);
            }
            return hash;
        }

        private bool IsLineStable(Entity line, DynamicBuffer<RouteWaypoint> wps)
        {
            ulong signature = ComputeLineWaypointSignature(wps);
            uint nowFrame = m_SimulationSystem.frameIndex;

            if (!m_LineWaypointSignature.TryGetValue(line, out ulong oldSignature) || oldSignature != signature)
            {
                ClearLineTimeProfiles();
                m_LineWaypointSignature[line] = signature;
                m_LineStableSinceFrame[line] = nowFrame;
                m_DiagnosedLines.Remove(line);
                return false;
            }

            if (!m_LineStableSinceFrame.TryGetValue(line, out uint stableSince))
            {
                m_LineStableSinceFrame[line] = nowFrame;
                return false;
            }

            return nowFrame - stableSince >= NEW_LINE_STABLE_FRAMES;
        }

        private void ClearLineTimeProfiles()
        {
            if (m_LineTimeProfiles.IsCreated) m_LineTimeProfiles.Clear();
            if (m_LineTimeProfileSegmentFrames.IsCreated) m_LineTimeProfileSegmentFrames.Clear();
            if (m_LineTimeProfileStopFrames.IsCreated) m_LineTimeProfileStopFrames.Clear();
        }

        private bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> wps, out LineTimeProfileHeader profile)
        {
            profile = default;
            var segBuffers = GetBufferLookup<RouteSegment>(true);
            if (!segBuffers.TryGetBuffer(line, out var segs)) return false;
            if (wps.Length == 0 || segs.Length != wps.Length) return false;

            ulong signature = ComputeLineProfileSignature(wps, segs);
            if (m_LineTimeProfiles.TryGetValue(line, out var cached) && cached.m_Signature == signature)
            {
                profile = cached;
                return true;
            }

            if (!EntityManager.HasComponent<PrefabRef>(line)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return false;
            TransportLineData prefabLineData = EntityManager.GetComponentData<TransportLineData>(prefab);

            int count = wps.Length;
            float baseLoopFrames = 0f;
            int offset = m_LineTimeProfileSegmentFrames.Length;

            for (int i = 0; i < count; i++)
            {
                m_LineTimeProfileSegmentFrames.Add(0f);
                m_LineTimeProfileStopFrames.Add(0f);
            }

            for (int i = 0; i < count; i++)
            {
                Entity segment = segs[i].m_Segment;
                float segmentFrames = 0f;
                if (EntityManager.HasComponent<PathInformation>(segment))
                {
                    PathInformation pathInfo = EntityManager.GetComponentData<PathInformation>(segment);
                    segmentFrames = math.max(0f, pathInfo.m_Duration * 60f);
                }
                m_LineTimeProfileSegmentFrames[offset + i] = segmentFrames;
                m_LineTimeProfileStopFrames[offset + i] = GetProfileWaypointStopFrames(line, wps, i, prefabLineData);
            }

            for (int i = 0; i < count; i++)
            {
                baseLoopFrames += m_LineTimeProfileSegmentFrames[offset + i];
                int nextWaypointIndex = (i + 1) % count;
                if (nextWaypointIndex != 0)
                    baseLoopFrames += m_LineTimeProfileStopFrames[offset + nextWaypointIndex];
            }

            if (baseLoopFrames <= 0f) return false;

            profile = new LineTimeProfileHeader
            {
                m_Signature = signature,
                m_BaseLoopFrames = baseLoopFrames,
                m_Offset = offset,
                m_Count = count
            };
            m_LineTimeProfiles[line] = profile;
            return true;
        }

        private float GetWaypointStopFrames(Entity waypoint, TransportLineData prefabLineData)
        {
            if (!EntityManager.HasComponent<VehicleTiming>(waypoint))
                return 0f;

            float stopDuration = prefabLineData.m_StopDuration;
            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connectedStop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connectedStop != Entity.Null && EntityManager.HasComponent<Game.Routes.TransportStop>(connectedStop))
                    stopDuration = RouteUtils.GetStopDuration(prefabLineData, EntityManager.GetComponentData<Game.Routes.TransportStop>(connectedStop));
            }
            return math.max(0f, stopDuration * 60f);
        }

        private float GetProfileWaypointStopFrames(
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            int waypointIndex,
            TransportLineData prefabLineData)
        {
            if (waypointIndex < 0 || waypointIndex >= wps.Length)
                return 0f;

            float configuredStopFrames = GetWaypointStopFrames(wps[waypointIndex].m_Waypoint, prefabLineData);
            if (!(configuredStopFrames > 0f))
                return 0f;

            float dwellFrames = 0f;
            if (!TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames))
            {
                int maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line);
                if (maxStationDwellMinutes > 0)
                    dwellFrames = maxStationDwellMinutes * (float)SIM_FRAMES_PER_MINUTE;
                else
                    dwellFrames = configuredStopFrames;
            }

            return math.max(0f, dwellFrames + PROFILE_STOP_START_BUFFER_MINUTES * (float)SIM_FRAMES_PER_MINUTE);
        }

        private static ulong MakeLineWaypointStopObservationKey(Entity line, int waypointIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)math.max(0, waypointIndex);
            }
        }

        private bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
        {
            dwellFrames = 0f;
            if (line == Entity.Null || waypointIndex < 0)
                return false;

            return m_WaypointStopDwellObservations.TryGetValue(MakeLineWaypointStopObservationKey(line, waypointIndex), out StopDwellObservation observation)
                && observation.AverageFrames > 0f
                && (dwellFrames = observation.AverageFrames) > 0f;
        }

        private void BeginObservedStopDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || waypointIndex < 0)
                return;

            m_StopDwellSessions[vehicle] = new StopDwellSession(line, waypointIndex, nowFrame);
        }

        private void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null || line == Entity.Null)
                return;
            if (!m_StopDwellSessions.TryGetValue(vehicle, out StopDwellSession session))
                return;

            m_StopDwellSessions.Remove(vehicle);

            int waypointIndex = session.WaypointIndex >= 0 ? session.WaypointIndex : fallbackWaypointIndex;
            if (waypointIndex < 0 || session.Line != line || nowFrame <= session.StartFrame)
                return;

            uint sampleFrames = nowFrame - session.StartFrame;
            if (sampleFrames == 0)
                return;

            const float maxObservedMinutes = 30f;
            float sampleMinutes = sampleFrames / (float)SIM_FRAMES_PER_MINUTE;
            if (sampleMinutes <= 0f || sampleMinutes > maxObservedMinutes)
                return;

            ulong key = MakeLineWaypointStopObservationKey(line, waypointIndex);
            if (m_WaypointStopDwellObservations.TryGetValue(key, out StopDwellObservation existing))
            {
                int sampleCount = math.min(existing.SampleCount + 1, 8);
                float averageFrames = existing.SampleCount <= 0
                    ? sampleFrames
                    : ((existing.AverageFrames * existing.SampleCount) + sampleFrames) / (existing.SampleCount + 1);
                m_WaypointStopDwellObservations[key] = new StopDwellObservation
                {
                    AverageFrames = averageFrames,
                    SampleCount = sampleCount
                };
                ClearLineTimeProfiles();
                InvalidateTrackModel(line);
                return;
            }

            m_WaypointStopDwellObservations[key] = new StopDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1
            };
            ClearLineTimeProfiles();
            InvalidateTrackModel(line);
        }

        private float ResolveProfileScale(Entity v, float baseLoopFrames)
        {
            if (baseLoopFrames <= 0f) return 1f;
            if (!m_VehicleLapFrames.TryGetValue(v, out uint observedLoopFrames) || observedLoopFrames == 0)
                return 1f;

            float rawScale = observedLoopFrames / baseLoopFrames;
            if (rawScale < ETA_SCALE_MIN || rawScale > ETA_SCALE_MAX)
                return 1f;
            return rawScale;
        }

        private float ComputeRemainingFramesFromProfile(LineTimeProfileHeader profile, int nextWaypointIndex, float segmentPosition)
        {
            int count = profile.m_Count;
            if (count == 0) return float.MaxValue;
            if (nextWaypointIndex < 0 || nextWaypointIndex >= count) return float.MaxValue;

            int segmentIndex = nextWaypointIndex == 0 ? count - 1 : nextWaypointIndex - 1;
            float remaining = m_LineTimeProfileSegmentFrames[profile.m_Offset + segmentIndex] * (1f - math.saturate(segmentPosition));

            for (int waypointIndex = nextWaypointIndex; waypointIndex != 0; waypointIndex = (waypointIndex + 1) % count)
            {
                remaining += m_LineTimeProfileStopFrames[profile.m_Offset + waypointIndex];
                remaining += m_LineTimeProfileSegmentFrames[profile.m_Offset + waypointIndex];
            }
            return math.max(0f, remaining);
        }

        private float ComputeRemainingFramesFromCachedWaypoint(LineTimeProfileHeader profile, int cachedWpIdx)
        {
            int count = profile.m_Count;
            if (count == 0) return float.MaxValue;
            if (cachedWpIdx < 0 || cachedWpIdx >= count) return float.MaxValue;

            float remaining = m_LineTimeProfileSegmentFrames[profile.m_Offset + cachedWpIdx];
            int nextWaypointIndex = (cachedWpIdx + 1) % count;
            for (int waypointIndex = nextWaypointIndex; waypointIndex != 0; waypointIndex = (waypointIndex + 1) % count)
            {
                remaining += m_LineTimeProfileStopFrames[profile.m_Offset + waypointIndex];
                remaining += m_LineTimeProfileSegmentFrames[profile.m_Offset + waypointIndex];
            }
            return math.max(0f, remaining);
        }

        private static string BoolDebugStr(bool value)
        {
            return value ? "是 / Yes" : "否 / No";
        }

        private static bool IsChineseLocale()
        {
            string locale = GameManager.instance?.localizationManager?.activeLocaleId ?? string.Empty;
            return locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        private static string LocalizedDispatchLabel()
        {
            return IsChineseLocale() ? "发车模式" : "Dispatch";
        }

        private static string LocalizedNextSlotLabel()
        {
            return IsChineseLocale() ? "下一班次" : "Next Slot";
        }

        private static string LocalizedNextSlotCoverageLabel()
        {
            return IsChineseLocale() ? "下一班次占用" : "Next Slot Coverage";
        }

        private static string LocalizedDispatchCacheLabel()
        {
            return IsChineseLocale() ? "出库缓存" : "Dispatch Cache";
        }

        private static string LocalizedOfficialDispatchValue()
        {
            return IsChineseLocale() ? "官方调度" : "Official dispatch";
        }

        private void FillVehicleDebugInfo(Entity v, InfoList list)
        {
            string state = m_VehicleState.TryGetValue(v, out var st) ? st.ToString() : "Unknown";
            string lineStr = m_VehicleLine.TryGetValue(v, out Entity line) ? line.Index.ToString() : "-";
            string targetStr = m_VehicleTargetMin.TryGetValue(v, out int targetMin) && targetMin >= 0 ? SlotStr(targetMin) : "-";
            string currentStr = m_VehicleCurrentSlot.TryGetValue(v, out int currentSlot) && currentSlot >= 0 ? SlotStr(currentSlot) : "-";
            string cachedWp = m_CachedWpIdx.TryGetValue(v, out int wp) ? wp.ToString() : "-";
            string tagged = BoolDebugStr(m_NearingTerminus.Contains(v));
            string cooldown = BoolDebugStr(m_LaunchCooldownUntil.TryGetValue(v, out uint cd) && m_SimulationSystem.frameIndex < cd);
            string misfire = BoolDebugStr(m_BVMisfire.Contains(v));
            string lapStartFrame = m_VehicleLapStartFrame.TryGetValue(v, out uint lsf) ? lsf.ToString() : "-";
            string lapFrames = m_VehicleLapFrames.TryGetValue(v, out uint lf) ? lf.ToString() : "-";
            string lapDistance = m_VehicleLapDistance.TryGetValue(v, out float ld) && ld >= 0f ? (ld / 1000f).ToString("F2") + "km" : "-";
            string prepStart = m_VehiclePreparingStartFrame.TryGetValue(v, out uint psf) ? psf.ToString() : "-";
            string idleStart = m_VehicleIdleStartFrame.TryGetValue(v, out uint isf) ? isf.ToString() : "-";

            AddDebugItem(list, "车辆", "Vehicle", v.Index.ToString());
            AddDebugItem(list, "状态", "State", state);
            AddDebugItem(list, "线路", "Line", lineStr);
            AddDebugItem(list, "目标班次", "Target Slot", targetStr);
            AddDebugItem(list, "当前班次", "Current Slot", currentStr);
            AddDebugItem(list, "缓存路点", "Cached Waypoint", cachedWp);
            AddDebugItem(list, "回流标签", "Nearing Terminus", tagged);
            AddDebugItem(list, "发车冷却", "Launch Cooldown", cooldown);
            AddDebugItem(list, "BV异常", "BV Misfire", misfire);
            AddDebugItem(list, "圈起点帧", "Lap Start Frame", lapStartFrame);
            AddDebugItem(list, "本圈帧数", "Lap Frames", lapFrames);
            AddDebugItem(list, "本圈距离", "Lap Distance", lapDistance);
            AddDebugItem(list, "出库起始帧", "Preparing Start Frame", prepStart);
            AddDebugItem(list, "闲置起始帧", "Idle Start Frame", idleStart);
        }

        private void FillLineDebugInfo(Entity line, InfoList list)
        {
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            bool isManagedLine = IsWorkbenchTimetableApplied(line);
            int nextSlot = isManagedLine ? GetNextManagedDispatchTarget(line, nowMin) : NextSlotMin(nowMin);
            float lapCacheFrames = ReadLineLapCache(line);
            float dispatchCacheFrames = ReadDispatchCache(line);
            string lapCache = lapCacheFrames > 0f ? (lapCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string dispatchCache = dispatchCacheFrames > 0f ? (dispatchCacheFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "min" : "-";
            string spawning = m_SpawningLines.TryGetValue(line, out int spawnTarget) ? spawnTarget.ToString() : "-";

            int preparing = 0;
            int holding = 0;
            int running = 0;
            int idle = 0;
            int retiring = 0;
            int tagged = 0;
            int total = 0;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(v)) continue;
                    total++;
                    if (m_NearingTerminus.Contains(v)) tagged++;
                    if (!m_VehicleState.TryGetValue(v, out var st)) continue;
                    switch (st)
                    {
                        case VehicleState.Preparing: preparing++; break;
                        case VehicleState.Holding: holding++; break;
                        case VehicleState.Running: running++; break;
                        case VehicleState.Idle: idle++; break;
                        case VehicleState.Retiring: retiring++; break;
                    }
                }
            }

            AddDebugItem(list, "线路", "Line", line.Index.ToString());
            AddDebugItem(list, "时间", "Time", SlotStr(nowMin));
            AddDebugItem(list, isManagedLine ? LocalizedNextSlotLabel() : "下一班次", "Next Slot", SlotStr(nextSlot));
            AddDebugItem(list, "总车数", "Total Vehicles", total.ToString());
            AddDebugItem(list, "预备数", "Preparing Count", preparing.ToString());
            AddDebugItem(list, "候车数", "Holding Count", holding.ToString());
            AddDebugItem(list, "运行数", "Running Count", running.ToString());
            AddDebugItem(list, "待调度数", "Idle Count", idle.ToString());
            AddDebugItem(list, "回库数", "Retiring Count", retiring.ToString());
            AddDebugItem(list, "回流标签数", "Nearing Terminus Count", tagged.ToString());
            AddDebugItem(list, "产车目标", "Spawn Target", spawning);
            AddDebugItem(list, "圈时缓存", "Lap Cache", lapCache);
            AddDebugItem(list, "出库缓存", "Dispatch Cache", dispatchCache);
        }

        private float EstimatePreparingArrivalFrames(Entity v, Entity line, uint nowFrame, float lineDurationFrames)
        {
            if (!m_VehiclePreparingStartFrame.TryGetValue(v, out uint prepStart))
                return float.MaxValue;
            float elapsedFrames = nowFrame - prepStart;
            float cachedFrames = ReadDispatchCache(line);
            if (cachedFrames <= 0f)
                cachedFrames = EstimateDispatchFallbackFrames(v, line, lineDurationFrames);
            if (cachedFrames <= 0f)
                return float.MaxValue;
            return math.max(0f, cachedFrames - elapsedFrames);
        }

        private float EstimateRunningArrivalFrames(
            Entity v,
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            uint nowFrame,
            float lineDurationFrames,
            bool lineHasHistory)
        {
            if (TryGetLineTimeProfile(line, wps, out var profile))
            {
                float scale = ResolveProfileScale(v, profile.m_BaseLoopFrames);

                if (TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition))
                    return ComputeRemainingFramesFromProfile(profile, nextWaypointIndex, segmentPosition) * scale;

                int cachedWpIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                float cachedWaypointEstimate = ComputeRemainingFramesFromCachedWaypoint(profile, cachedWpIdx);
                if (cachedWaypointEstimate != float.MaxValue)
                    return cachedWaypointEstimate * scale;
            }

            float lapFrames = m_VehicleLapFrames.TryGetValue(v, out uint vehicleLapFrames) && vehicleLapFrames > 0
                ? vehicleLapFrames
                : 0f;
            if (lapFrames <= 0f && lineHasHistory && lineDurationFrames > 0f)
                lapFrames = lineDurationFrames;

            if (lapFrames <= 0f)
                return float.MaxValue;

            if (m_VehicleLapStartFrame.TryGetValue(v, out uint lapStartFrame))
                return math.max(0f, lapFrames - (float)(nowFrame - lapStartFrame));

            return float.MaxValue;
        }

        private float EstimateSpawnLeadFrames(Entity line, float lineDurationFrames)
        {
            float cachedFrames = ReadDispatchCache(line);
            if (cachedFrames > 0f)
                return cachedFrames;

            float estimateMinutes = 0f;
            if (lineDurationFrames > 0f)
                estimateMinutes = (lineDurationFrames / (float)SIM_FRAMES_PER_MINUTE) * 0.2f;
            if (estimateMinutes <= 0f)
                estimateMinutes = 6f;

            estimateMinutes = math.clamp(estimateMinutes, DISPATCH_ESTIMATE_MIN_MINUTES, DISPATCH_ESTIMATE_MAX_MINUTES);
            return estimateMinutes * (float)SIM_FRAMES_PER_MINUTE;
        }

        private float GetSpawnTriggerBufferMinutes(float spawnLeadFrames)
        {
            float spawnLeadMinutes = spawnLeadFrames / (float)SIM_FRAMES_PER_MINUTE;
            return spawnLeadMinutes < SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES
                ? SPAWN_TRIGGER_BUFFER_SHORT_MINUTES
                : SPAWN_TRIGGER_BUFFER_LONG_MINUTES;
        }

        private float EstimateDispatchFallbackFrames(Entity v, Entity line, float lineDurationFrames)
        {
            float estimateMinutes = 0f;
            if (EntityManager.HasComponent<Game.Objects.Transform>(v))
            {
                float3 vehiclePos = EntityManager.GetComponentData<Game.Objects.Transform>(v).m_Position;
                Entity stopA = GetFirstStop(line);
                if (stopA != Entity.Null && EntityManager.HasComponent<Game.Objects.Transform>(stopA))
                {
                    float3 stopPos = EntityManager.GetComponentData<Game.Objects.Transform>(stopA).m_Position;
                    float distance = math.distance(vehiclePos, stopPos);
                    estimateMinutes = distance / DISPATCH_FALLBACK_SPEED_M_PER_MIN;
                }
            }
            if (estimateMinutes <= 0f && lineDurationFrames > 0f)
                estimateMinutes = (lineDurationFrames / (float)SIM_FRAMES_PER_MINUTE) * 0.2f;
            if (estimateMinutes <= 0f)
                estimateMinutes = 6f;
            estimateMinutes = math.clamp(estimateMinutes, DISPATCH_ESTIMATE_MIN_MINUTES, DISPATCH_ESTIMATE_MAX_MINUTES);
            return estimateMinutes * (float)SIM_FRAMES_PER_MINUTE;
        }

        private Entity GetFirstStop(Entity line)
        {
            var wpBuffers = GetBufferLookup<RouteWaypoint>(true);
            if (!wpBuffers.TryGetBuffer(line, out var wps) || wps.Length == 0)
                return Entity.Null;
            Entity stationA = wps[0].m_Waypoint;
            if (!EntityManager.HasComponent<Connected>(stationA))
                return Entity.Null;
            return EntityManager.GetComponentData<Connected>(stationA).m_Connected;
        }

        private void TryRecordPreparingArrivalSample(Entity v, Entity line, uint nowFrame)
        {
            if (line == Entity.Null)
            {
                m_VehiclePreparingStartFrame.Remove(v);
                m_VehicleDispatchRequestStartFrame.Remove(v);
                return;
            }

            uint frames = 0;
            bool hasSample = false;
            if (m_VehicleDispatchRequestStartFrame.TryGetValue(v, out uint dispatchRequestStart))
            {
                frames = nowFrame - dispatchRequestStart;
                hasSample = true;
            }
            else if (m_VehiclePreparingStartFrame.TryGetValue(v, out uint prepStart))
            {
                frames = nowFrame - prepStart;
                hasSample = true;
            }

            m_VehiclePreparingStartFrame.Remove(v);
            m_VehicleDispatchRequestStartFrame.Remove(v);
            if (!hasSample)
                return;
            if (frames == 0)
                return;
            float sampleMinutes = frames / (float)SIM_FRAMES_PER_MINUTE;
            if (sampleMinutes < DISPATCH_ESTIMATE_MIN_MINUTES || sampleMinutes > DISPATCH_ESTIMATE_MAX_MINUTES)
            {
                log.Info("[出库样本] 线路" + line.Index + " 车辆" + v.Index
                    + " 样本=" + sampleMinutes.ToString("F1") + "分钟，超出范围，忽略");
                return;
            }
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            RecordLineDispatchSampleSummary(line, nowMin, v, sampleMinutes);
            UpdateDispatchCache(line, frames);
        }

        private void EnsureDispatchCacheBuffer()
        {
            if (m_DispatchCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city))
                EntityManager.AddBuffer<LineDispatchCacheElement>(city);
            if (!EntityManager.HasBuffer<LineDispatchHistoryElement>(city))
                EntityManager.AddBuffer<LineDispatchHistoryElement>(city);
            m_DispatchCacheBufferReady = true;
        }

        private void EnsureBypassStationBuffer()
        {
            if (m_BypassStationBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<BypassStationSettingElement>(city))
                EntityManager.AddBuffer<BypassStationSettingElement>(city);
            m_BypassStationBufferReady = true;
        }

        private void EnsureLineMileageBuffers()
        {
            if (m_LineMileageBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineMileageModelStateElement>(city))
                EntityManager.AddBuffer<LineMileageModelStateElement>(city);
            if (!EntityManager.HasBuffer<LineMileageAnchorElement>(city))
                EntityManager.AddBuffer<LineMileageAnchorElement>(city);
            if (!EntityManager.HasBuffer<LineCorridorStateElement>(city))
                EntityManager.AddBuffer<LineCorridorStateElement>(city);
            if (!EntityManager.HasBuffer<LineCorridorNodeElement>(city))
                EntityManager.AddBuffer<LineCorridorNodeElement>(city);
            m_LineMileageBufferReady = true;
        }

        private float ReadDispatchCache(Entity line)
        {
            if (!m_DispatchCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineDispatchCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_DepotToOriginFrames;
            }
            return 0f;
        }

        private void UpdateDispatchCache(Entity line, uint sampleFrames)
        {
            if (!m_DispatchCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineDispatchCacheElement>(city)) return;
            if (!EntityManager.HasBuffer<LineDispatchHistoryElement>(city)) return;

            var buf = EntityManager.GetBuffer<LineDispatchCacheElement>(city);
            var historyBuf = EntityManager.GetBuffer<LineDispatchHistoryElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity != line) continue;
                uint oldFrames = buf[i].m_DepotToOriginFrames;
                LineDispatchHistoryElement history = GetDispatchHistoryElement(historyBuf, line);
                LineDispatchHistoryElement updatedHistory = AppendDispatchSample(history, sampleFrames);
                uint newFrames = ComputeDispatchSampleAverage(ReadDispatchSamples(updatedHistory));
                buf[i] = new LineDispatchCacheElement
                {
                    m_LineEntity = line,
                    m_DepotToOriginFrames = newFrames
                };
                UpsertDispatchHistory(historyBuf, updatedHistory);
                float oldMinutes = oldFrames / (float)SIM_FRAMES_PER_MINUTE;
                float newMinutes = newFrames / (float)SIM_FRAMES_PER_MINUTE;
                log.Info("[出库缓存] 线路" + line.Index
                    + " 样本=" + (sampleFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                    + " 最近" + updatedHistory.m_SampleCount + "条均值" + newMinutes.ToString("F1") + "分钟"
                    + (oldFrames > 0 ? " 旧值" + oldMinutes.ToString("F1") + "分钟" : ""));
                return;
            }

            LineDispatchHistoryElement createdHistory = AppendDispatchSample(new LineDispatchHistoryElement
            {
                m_LineEntity = line
            }, sampleFrames);
            uint createdFrames = ComputeDispatchSampleAverage(ReadDispatchSamples(createdHistory));
            buf.Add(new LineDispatchCacheElement
            {
                m_LineEntity = line,
                m_DepotToOriginFrames = createdFrames
            });
            UpsertDispatchHistory(historyBuf, createdHistory);
            log.Info("[出库缓存新增] 线路" + line.Index
                + " 最近" + createdHistory.m_SampleCount + "条均值"
                + (createdFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟");
        }

        private static LineDispatchHistoryElement GetDispatchHistoryElement(DynamicBuffer<LineDispatchHistoryElement> historyBuf, Entity line)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity == line)
                    return historyBuf[i];
            }
            return new LineDispatchHistoryElement
            {
                m_LineEntity = line
            };
        }

        private static void UpsertDispatchHistory(DynamicBuffer<LineDispatchHistoryElement> historyBuf, LineDispatchHistoryElement history)
        {
            for (int i = 0; i < historyBuf.Length; i++)
            {
                if (historyBuf[i].m_LineEntity != history.m_LineEntity) continue;
                historyBuf[i] = history;
                return;
            }
            historyBuf.Add(history);
        }

        private LineDispatchHistoryElement AppendDispatchSample(LineDispatchHistoryElement element, uint sampleFrames)
        {
            var samples = ReadDispatchSamples(element);
            samples.Add(sampleFrames);
            if (samples.Count > DISPATCH_SAMPLE_HISTORY_LIMIT)
                samples.RemoveAt(0);

            WriteDispatchSamples(ref element, samples);
            return element;
        }

        private static List<uint> ReadDispatchSamples(LineDispatchHistoryElement element)
        {
            var samples = new List<uint>(DISPATCH_SAMPLE_HISTORY_LIMIT);
            AddDispatchSampleIfValid(samples, element.m_Sample0);
            AddDispatchSampleIfValid(samples, element.m_Sample1);
            AddDispatchSampleIfValid(samples, element.m_Sample2);
            AddDispatchSampleIfValid(samples, element.m_Sample3);
            AddDispatchSampleIfValid(samples, element.m_Sample4);
            AddDispatchSampleIfValid(samples, element.m_Sample5);
            AddDispatchSampleIfValid(samples, element.m_Sample6);
            AddDispatchSampleIfValid(samples, element.m_Sample7);
            if (samples.Count > element.m_SampleCount)
                samples.RemoveRange((int)element.m_SampleCount, samples.Count - (int)element.m_SampleCount);
            return samples;
        }

        private static void AddDispatchSampleIfValid(List<uint> samples, uint value)
        {
            if (value > 0)
                samples.Add(value);
        }

        private static void WriteDispatchSamples(ref LineDispatchHistoryElement element, List<uint> samples)
        {
            element.m_SampleCount = (byte)math.min(samples.Count, DISPATCH_SAMPLE_HISTORY_LIMIT);
            element.m_Sample0 = samples.Count > 0 ? samples[0] : 0;
            element.m_Sample1 = samples.Count > 1 ? samples[1] : 0;
            element.m_Sample2 = samples.Count > 2 ? samples[2] : 0;
            element.m_Sample3 = samples.Count > 3 ? samples[3] : 0;
            element.m_Sample4 = samples.Count > 4 ? samples[4] : 0;
            element.m_Sample5 = samples.Count > 5 ? samples[5] : 0;
            element.m_Sample6 = samples.Count > 6 ? samples[6] : 0;
            element.m_Sample7 = samples.Count > 7 ? samples[7] : 0;
        }

        private static uint ComputeDispatchSampleAverage(List<uint> samples)
        {
            if (samples.Count == 0)
                return 0;

            double sum = 0d;
            for (int i = 0; i < samples.Count; i++)
                sum += samples[i];
            double mean = sum / samples.Count;
            double maxAccepted = mean * DISPATCH_SAMPLE_OUTLIER_FACTOR;

            double filteredSum = 0d;
            int filteredCount = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] > maxAccepted)
                    continue;
                filteredSum += samples[i];
                filteredCount++;
            }

            if (filteredCount == 0)
                return (uint)math.round((float)mean);

            return (uint)math.round((float)(filteredSum / filteredCount));
        }

        private void EnsureLapCacheBuffer()
        {
            if (m_LapCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city))
                EntityManager.AddBuffer<LineLapCacheElement>(city);
            m_LapCacheBufferReady = true;
        }

        private void FlushLineLapCache(Entity line)
        {
            if (!m_LapCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return;

            uint bestFrames = 0;
            float bestDist = 0f;
            var rvBuffers = GetBufferLookup<RouteVehicle>(true);
            if (rvBuffers.TryGetBuffer(line, out var rvs))
            {
                for (int i = 0; i < rvs.Length; i++)
                {
                    Entity v0 = rvs[i].m_Vehicle;
                    if (!EntityManager.Exists(v0)) continue;
                    if (m_VehicleLapFrames.TryGetValue(v0, out uint lf) && lf > bestFrames)
                    {
                        bestFrames = lf;
                        m_VehicleLapDistance.TryGetValue(v0, out bestDist);
                    }
                }
            }
            if (bestFrames == 0) return;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                {
                    if (bestFrames > buf[i].m_MaxLapFrames)
                    {
                        buf[i] = new LineLapCacheElement
                        {
                            m_LineEntity = line,
                            m_MaxLapFrames = bestFrames,
                            m_MaxLapDistance = bestDist
                        };
                        log.Info("[缓存写入] 线路" + line.Index
                            + " 圈时=" + (bestFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟");
                    }
                    return;
                }
            }

            buf.Add(new LineLapCacheElement
            {
                m_LineEntity = line,
                m_MaxLapFrames = bestFrames,
                m_MaxLapDistance = bestDist
            });
            log.Info("[缓存新增] 线路" + line.Index
                + " 圈时=" + (bestFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "游戏分钟");
        }

        private float ReadLineLapCache(Entity line)
        {
            if (!m_LapCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapFrames;
            }
            return 0f;
        }

        private float ReadLineLapDistance(Entity line)
        {
            if (!m_LapCacheBufferReady) return 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return 0f;
            if (!EntityManager.HasBuffer<LineLapCacheElement>(city)) return 0f;

            var buf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_LineEntity == line)
                    return buf[i].m_MaxLapDistance;
            }
            return 0f;
        }

        private void EnsureVehicleCacheBuffer()
        {
            if (m_VehicleCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city))
            {
                EntityManager.AddBuffer<VehicleStateCacheElement>(city);
                log.Info("[缓存] 已在城市实体上创建 VehicleStateCacheElement Buffer");
            }
            m_VehicleCacheBufferReady = true;
        }

        private void FlushAllVehicleStates()
        {
            if (!m_VehicleCacheBufferReady) return;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return;

            var buf = EntityManager.GetBuffer<VehicleStateCacheElement>(city);
            buf.Clear();

            var keys = m_VehicleState.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                Entity v = keys[i];
                VehicleState st = m_VehicleState[v];
                if (st == VehicleState.Retiring) continue;

                int targetMin = m_VehicleTargetMin.TryGetValue(v, out int tm) ? tm : -1;
                buf.Add(new VehicleStateCacheElement
                {
                    m_VehicleEntity = v,
                    m_State = st,
                    m_TargetMin = targetMin
                });
            }
            keys.Dispose();
        }

        private bool TryRestoreVehicleState(Entity v, Entity line, bool allowRunningRestore = true)
        {
            if (!m_VehicleCacheBufferReady) return false;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null) return false;
            if (!EntityManager.HasBuffer<VehicleStateCacheElement>(city)) return false;

            var buf = EntityManager.GetBuffer<VehicleStateCacheElement>(city, true);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_VehicleEntity != v) continue;

                VehicleState cachedState = buf[i].m_State;
                int cachedTarget = buf[i].m_TargetMin;

                if (cachedState == VehicleState.Holding)
                {
                    m_VehicleState[v] = VehicleState.Holding;
                    m_VehicleTargetMin[v] = cachedTarget;

                    if (EntityManager.HasComponent<Game.Vehicles.PublicTransport>(v))
                    {
                        var pt = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(v);
                        pt.m_DepartureFrame = m_SimulationSystem.frameIndex + 99999;
                        EntityManager.SetComponentData(v, pt);
                    }
                    log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                        + " Holding target=" + (cachedTarget >= 0 ? SlotStr(cachedTarget) : "-"));
                    return true;
                }

                if (cachedState == VehicleState.Running)
                {
                    if (!allowRunningRestore)
                        return false;

                    m_VehicleState[v] = VehicleState.Running;
                    m_VehicleTargetMin[v] = -1;
                    m_VehicleCurrentSlot.Remove(v);

                    float cachedLapDist = 0f;
                    if (m_LapCacheBufferReady && EntityManager.HasBuffer<LineLapCacheElement>(city))
                    {
                        var lapBuf = EntityManager.GetBuffer<LineLapCacheElement>(city, true);
                        for (int j = 0; j < lapBuf.Length; j++)
                        {
                            if (lapBuf[j].m_LineEntity == line)
                            {
                                cachedLapDist = lapBuf[j].m_MaxLapDistance;
                                break;
                            }
                        }
                    }
                    bool restoredLapStart = false;
                    if (EntityManager.HasComponent<Odometer>(v))
                    {
                        float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
                        m_VehicleLapStartOdometer[v] = cachedLapDist > 0f
                            ? currentOdo - cachedLapDist
                            : currentOdo;
                        m_VehicleLapStartFrame[v] = m_SimulationSystem.frameIndex;
                        restoredLapStart = true;
                    }
                    else
                    {
                        m_VehicleLapStartOdometer.Remove(v);
                        m_VehicleLapStartFrame.Remove(v);
                    }
                    m_VehicleLapFrames[v] = 0;
                    m_VehicleLastLaunchFrame.Remove(v);
                    m_LaunchCooldownUntil.Remove(v);
                    m_BVMisfire.Remove(v);
                    m_BVMisfireStartFrame.Remove(v);
                    m_OriginArrivalCandidateSinceFrame.Remove(v);
                    m_RestoredRunning.Add(v);
                    log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                        + " Running lapDist=" + cachedLapDist.ToString("F1")
                        + " lapStart=" + (restoredLapStart ? "ok" : "missing-odometer")
                        + " startFrame=" + (restoredLapStart ? m_SimulationSystem.frameIndex.ToString() : "-"));

                    return true;
                }

                return false;
            }
            return false;
        }

        private bool RestoreRunningContextFromProgress(Entity v, Entity line, DynamicBuffer<RouteWaypoint> wps, string initReason)
        {
            float cachedLapFrames = ReadLineLapCache(line);
            if (cachedLapFrames <= 0f) return false;
            if (!TryGetRouteProgress(v, out int nextWaypointIndex, out float segmentPosition)) return false;

            m_VehicleCurrentSlot.Remove(v);
            m_VehicleTargetMin[v] = -1;
            m_VehicleLastLaunchFrame.Remove(v);
            m_LaunchCooldownUntil.Remove(v);
            m_OriginArrivalCandidateSinceFrame.Remove(v);

            float segmentBase = nextWaypointIndex == 0 ? (wps.Length - 1) : (nextWaypointIndex - 1);
            float progress = (segmentBase + math.saturate(segmentPosition)) / math.max(1, wps.Length);
            progress = math.clamp(progress, 0f, 0.999f);

            uint nowFrame = m_SimulationSystem.frameIndex;
            uint estimatedStartFrame = nowFrame > (uint)(cachedLapFrames * progress)
                ? nowFrame - (uint)math.round(cachedLapFrames * progress)
                : 0u;
            m_VehicleLapStartFrame[v] = estimatedStartFrame;
            m_VehicleLapFrames[v] = (uint)cachedLapFrames;

            if (EntityManager.HasComponent<Odometer>(v))
            {
                float currentOdo = EntityManager.GetComponentData<Odometer>(v).m_Distance;
                float cachedLapDistance = ReadLineLapDistance(line);
                if (cachedLapDistance > 0f)
                    m_VehicleLapStartOdometer[v] = currentOdo - cachedLapDistance * progress;
                else
                    m_VehicleLapStartOdometer[v] = currentOdo;
            }

            log.Info("[恢复] 线路" + line.Index + " 车辆" + v.Index
                + " Running进度恢复"
                + " progress=" + progress.ToString("F2")
                + " wp=" + nextWaypointIndex
                + " seg=" + segmentPosition.ToString("F2")
                + " lapFrames=" + ((uint)cachedLapFrames).ToString()
                + " startFrame=" + estimatedStartFrame
                + " from=" + initReason);
            return true;
        }
    }
}
