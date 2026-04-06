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
using Game.Creatures;
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

        private struct DeferredBoardingTailIgnoreEntry
        {
            public Entity Vehicle;
            public uint ExpireFrame;
        }

        private readonly struct VehiclePhysicalTrackPosition
        {
            public readonly Entity Vehicle;
            public readonly Entity Line;
            public readonly Entity PhysicalTrackAxisId;
            public readonly float AxisCoordinate;
            public readonly float Confidence;

            public VehiclePhysicalTrackPosition(
                Entity vehicle,
                Entity line,
                Entity physicalTrackAxisId,
                float axisCoordinate,
                float confidence)
            {
                Vehicle = vehicle;
                Line = line;
                PhysicalTrackAxisId = physicalTrackAxisId;
                AxisCoordinate = axisCoordinate;
                Confidence = confidence;
            }
        }

        private readonly struct PhysicalTrackAxis
        {
            public readonly Entity AxisId;
            public readonly float DisplayLength;
            public readonly Entity[] PhysicalLaneKeys;

            public PhysicalTrackAxis(Entity axisId, float displayLength, Entity[] physicalLaneKeys)
            {
                AxisId = axisId;
                DisplayLength = displayLength;
                PhysicalLaneKeys = physicalLaneKeys ?? Array.Empty<Entity>();
            }
        }

        private sealed class PhysicalTrackNetworkSnapshot
        {
            public uint Frame;
            public readonly List<PhysicalTrackAxis> Axes = new List<PhysicalTrackAxis>();
            public readonly List<VehiclePhysicalTrackPosition> VehiclePositions = new List<VehiclePhysicalTrackPosition>();

            public PhysicalTrackNetworkSnapshot(uint frame = 0)
            {
                Frame = frame;
            }
        }

        private readonly struct LineRunningVehicleSnapshot
        {
            public readonly Entity Vehicle;
            public readonly int NextWaypointIndex;
            public readonly bool Boarding;
            public readonly bool HasProjection;
            public readonly float ProjectionDistanceMeters;
            public readonly bool HasTrackCursor;
            public readonly VehicleTrackCursor TrackCursor;
            public readonly int CurrentControlEdgeIndex;
            public readonly float OwnLineAtomCoordinate;
            public readonly int PhaseEndAtomExclusive;

            public LineRunningVehicleSnapshot(
                Entity vehicle,
                int nextWaypointIndex,
                bool boarding,
                bool hasProjection,
                float projectionDistanceMeters,
                bool hasTrackCursor,
                VehicleTrackCursor trackCursor,
                int currentControlEdgeIndex,
                float ownLineAtomCoordinate,
                int phaseEndAtomExclusive)
            {
                Vehicle = vehicle;
                NextWaypointIndex = nextWaypointIndex;
                Boarding = boarding;
                HasProjection = hasProjection;
                ProjectionDistanceMeters = projectionDistanceMeters;
                HasTrackCursor = hasTrackCursor;
                TrackCursor = trackCursor;
                CurrentControlEdgeIndex = currentControlEdgeIndex;
                OwnLineAtomCoordinate = ownLineAtomCoordinate;
                PhaseEndAtomExclusive = phaseEndAtomExclusive;
            }
        }

        private sealed class LineRunningVehicleFrameSnapshot
        {
            public uint Frame;
            public Entity Line;
            public readonly List<LineRunningVehicleSnapshot> Vehicles = new List<LineRunningVehicleSnapshot>();
        }

        private readonly struct WaypointIndexFrameSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Route;
            public readonly bool Boarding;
            public readonly int WaypointIndex;

            public WaypointIndexFrameSnapshot(uint frame, Entity route, bool boarding, int waypointIndex)
            {
                Frame = frame;
                Route = route;
                Boarding = boarding;
                WaypointIndex = waypointIndex;
            }
        }

        private readonly struct RouteProgressFrameSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Route;
            public readonly Entity Target;
            public readonly int NextWaypointIndex;
            public readonly float SegmentPosition;

            public RouteProgressFrameSnapshot(
                uint frame,
                Entity route,
                Entity target,
                int nextWaypointIndex,
                float segmentPosition)
            {
                Frame = frame;
                Route = route;
                Target = target;
                NextWaypointIndex = nextWaypointIndex;
                SegmentPosition = segmentPosition;
            }
        }

        private readonly struct TraversalSliceObservation
        {
            public readonly float AverageFrames;
            public readonly float FastBaselineFrames;
            public readonly int SampleCount;
            public readonly uint LastObservedFrame;

            public TraversalSliceObservation(float averageFrames, float fastBaselineFrames, int sampleCount, uint lastObservedFrame)
            {
                AverageFrames = averageFrames;
                FastBaselineFrames = fastBaselineFrames;
                SampleCount = sampleCount;
                LastObservedFrame = lastObservedFrame;
            }
        }

        private readonly struct VehicleTraversalSliceSession
        {
            public readonly Entity Line;
            public readonly int SliceIndex;
            public readonly uint EnterFrame;
            public readonly int EnterAtomIndex;
            public readonly float EnterAtomPosition01;

            public VehicleTraversalSliceSession(Entity line, int sliceIndex, uint enterFrame, int enterAtomIndex, float enterAtomPosition01)
            {
                Line = line;
                SliceIndex = sliceIndex;
                EnterFrame = enterFrame;
                EnterAtomIndex = enterAtomIndex;
                EnterAtomPosition01 = enterAtomPosition01;
            }
        }

        private struct TraversalSliceLapDebugAggregate
        {
            public int StartCount;
            public int FinalizeCount;
            public int MidSliceStartCount;
            public int DroppedWithoutFinalizeCount;
            public float EnterOffsetSumAtoms;
            public float MaxEnterOffsetAtoms;
            public float ObservedFramesSum;
            public float MinObservedFrames;
            public float MaxObservedFrames;

            public void RecordStart(float enterOffsetAtoms, bool midSliceStart)
            {
                StartCount++;
                EnterOffsetSumAtoms += enterOffsetAtoms;
                if (enterOffsetAtoms > MaxEnterOffsetAtoms)
                    MaxEnterOffsetAtoms = enterOffsetAtoms;
                if (midSliceStart)
                    MidSliceStartCount++;
            }

            public void RecordFinalize(float observedFrames)
            {
                FinalizeCount++;
                ObservedFramesSum += observedFrames;
                if (FinalizeCount == 1)
                {
                    MinObservedFrames = observedFrames;
                    MaxObservedFrames = observedFrames;
                    return;
                }

                if (observedFrames < MinObservedFrames)
                    MinObservedFrames = observedFrames;
                if (observedFrames > MaxObservedFrames)
                    MaxObservedFrames = observedFrames;
            }
        }

        private sealed class LineMileageModel
        {
            public ulong Signature;
            public float TotalDistanceMeters;
            public float[] WaypointDistances = Array.Empty<float>();
            public float[] BypassWaypointDistances = Array.Empty<float>();
            public float[] BypassStopNodeDistances = Array.Empty<float>();
            public int[] PreviousDistinctStationWaypointIndices = Array.Empty<int>();
            public float[] PreviousDistinctStationMeters = Array.Empty<float>();
            public float[] CurrentStationMeters = Array.Empty<float>();
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

        public static DepartureControlSystem Instance = null!;
        private TimedLogger log = Mod.log;
        private SimulationSystem m_SimulationSystem = null!;
        private TimeSystem m_TimeSystem = null!;
        private NameSystem m_NameSystem = null!;
        private EndFrameBarrier m_EndFrameBarrier = null!;

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
        private NativeHashMap<Entity, uint> m_ForcedMidStopBoardingHardCloseAfter;
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
        private NativeHashMap<Entity, uint> m_ForcedOriginBoardingGraceUntil;
        private NativeHashMap<Entity, uint> m_StopDwellStartFrame;
        private NativeHashMap<Entity, uint> m_VehicleDispatchRequestStartFrame;
        private readonly Dictionary<ulong, StopDwellObservation> m_WaypointStopDwellObservations = new Dictionary<ulong, StopDwellObservation>();
        private readonly Dictionary<Entity, StopDwellSession> m_StopDwellSessions = new Dictionary<Entity, StopDwellSession>();
        private readonly Dictionary<Entity, LineMileageModel> m_LineMileageModels = new Dictionary<Entity, LineMileageModel>();
        private SharedLocalCorridorGraph m_SharedLocalCorridorGraph;
        private readonly Dictionary<Entity, string> m_BypassDecisionLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BypassQueuedLocalOverrideLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_PreparingSlotLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_HoldingSkipLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_LateDispatchLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BvMisfireObserveLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_DepartureObserveLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, string> m_BvWaypointMismatchLogCache = new Dictionary<Entity, string>();
        private const bool ENABLE_TRACK_WAYPOINT_ANCHORING = true;
        private readonly Dictionary<Entity, string> m_BvTrackAnchorRecoveryLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, BoardingDepartureAuditSnapshot> m_LastBoardingAssistSnapshots = new Dictionary<Entity, BoardingDepartureAuditSnapshot>();
        private readonly Dictionary<Entity, DeferredBoardingTailIgnoreEntry> m_DeferredBoardingTailIgnores = new Dictionary<Entity, DeferredBoardingTailIgnoreEntry>();
        private readonly HashSet<Entity> m_DeferredBoardingHumanTailIgnores = new HashSet<Entity>();
        private readonly HashSet<Entity> m_DeferredBoardingPetTailIgnores = new HashSet<Entity>();
        private readonly List<Entity> m_DeferredBoardingTailScratch = new List<Entity>();
        private readonly Dictionary<Entity, string> m_MidStopTimeoutLogCache = new Dictionary<Entity, string>();
        private uint m_LastDeferredBoardingTailCleanupFrame;
        private static bool IsTraversalSliceObservationPersistenceEnabled() => true;
        private static bool IsStopDwellObservationPersistenceEnabled() => true;
        private readonly Dictionary<Entity, uint> m_BvWaypointMismatchLastLogFrame = new Dictionary<Entity, uint>();
        private readonly Dictionary<ulong, TraversalSliceObservation> m_TraversalRunSliceObservations = new Dictionary<ulong, TraversalSliceObservation>();
        private readonly Dictionary<Entity, VehicleTraversalSliceSession> m_VehicleTraversalSliceSessions = new Dictionary<Entity, VehicleTraversalSliceSession>();
        private readonly Dictionary<ulong, TraversalSliceLapDebugAggregate> m_VehicleTraversalSliceLapDebug = new Dictionary<ulong, TraversalSliceLapDebugAggregate>();
        private readonly Dictionary<Entity, LineRunningVehicleFrameSnapshot> m_LineRunningVehicleFrameSnapshots = new Dictionary<Entity, LineRunningVehicleFrameSnapshot>();
        private readonly Dictionary<Entity, WaypointIndexFrameSnapshot> m_WaypointIndexFrameSnapshots = new Dictionary<Entity, WaypointIndexFrameSnapshot>();
        private readonly Dictionary<Entity, RouteProgressFrameSnapshot> m_RouteProgressFrameSnapshots = new Dictionary<Entity, RouteProgressFrameSnapshot>();
        private bool m_CorridorModelFaulted = false;
        private uint m_BypassPerfProbeLastLogFrame;
        private ulong m_BypassPerfProbeCadenceCalls;
        private ulong m_BypassPerfProbeCadenceMisses;
        private ulong m_BypassPerfProbeBaselineCalls;
        private ulong m_BypassPerfProbeTrackDecisionCalls;
        private ulong m_BypassPerfProbeResolveCalls;
        private ulong m_BypassPerfProbeActiveCorridorCalls;
        private ulong m_BypassPerfProbeSceneSamples;
        private ulong m_BypassPerfProbeSceneCandidateVehicles;
        private ulong m_BypassPerfProbeSceneAdmittedCandidates;
        private ulong m_BypassPerfProbeSceneFrontiers;
        private ulong m_BypassPerfProbeSameStationCalls;
        private ulong m_BypassPerfProbeSameStationReusedCandidates;
        private ulong m_BypassPerfProbeDeepCorridorEntries;
        private ulong m_BypassPerfProbeEpisodeReuses;

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
        private bool m_TraversalSliceObservationBufferReady = false;
        private bool m_TraversalSliceObservationCacheLoaded = false;
        private bool m_StopDwellObservationBufferReady = false;
        private bool m_StopDwellObservationCacheLoaded = false;
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
        private bool m_BypassToggleKeyArmed = true;
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
        private EntityQuery m_TransportVehicleRequestQuery;

        private const int SLOT_INTERVAL = 30;
        private const int SPAWN_LEAD_MIN = 60;
        private const float MAINTENANCE_THRESHOLD = 0.9f;
        private const int IDLE_TIMEOUT_MIN = 2;
        private const double SIM_FRAMES_PER_MINUTE = 182.044;
        private const float EARLY_STOP_DWELL_CLOSE_MAX_MINUTES = 3f;
        private const float AT_STOP_MAX_DIST = 300f;
        /// <summary>班次宽限分钟数：发车窗口和过期判断共用同一阈值。</summary>
        private const int SLOT_GRACE_MIN = 4;
        /// <summary>BV 误写超时 6000 帧（约 33 秒现实时间），给足自愈窗口。</summary>
        private const uint BV_MISFIRE_TIMEOUT = 6000;
        /// <summary>暂时只观察 BV 误写，不再冻结车辆或回库；保留日志追踪后续是否能自愈。</summary>
        private static bool IsBvMisfireEnforcementEnabled() => false;
        /// <summary>发车后冷却帧数：屏蔽 boarding 变化检测，防假进站</summary>
        private const uint LAUNCH_COOLDOWN_FRAMES = 600;
        private const uint FORCED_MIDSTOP_BV_GRACE_FRAMES = 180;
        private const uint FORCED_MIDSTOP_HARD_CLOSE_FRAMES = 360;
        private const uint SPAWN_BLOCKED_LOG_COOLDOWN_FRAMES = 1800;
        private const uint SCHEDULE_DIAGNOSTIC_LOG_COOLDOWN_FRAMES = 1800;
        private const uint RETIREFIX_LOG_COOLDOWN_FRAMES = 1800;
        private const uint RETIREFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint PREPARINGFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES = 120;
        private const uint BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES = 8;
        internal const float BOARDING_CLOSE_BYPASS_MIN_WAITING_DISTANCE_SENTINEL = -1f;
        private const uint BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES = 6;
        private const uint BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES = 60;
        private const uint BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES = 3600;
        private const byte RETIREFIX_DELETE_THRESHOLD = 3;
        private const float DISPATCH_ESTIMATE_MIN_MINUTES = 2f;
        private const float DISPATCH_ESTIMATE_MAX_MINUTES = 20f;
        private const float DISPATCH_FALLBACK_SPEED_M_PER_MIN = 450f;
        private const float PROFILE_STOP_START_BUFFER_MINUTES = 3f;
        private const float ORIGIN_CONGESTION_RADIUS_METERS = 450f;
        private const float ORIGIN_FORCE_IDLE_RADIUS_METERS = 180f;
        private const float ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS = 0.92f;
        private const uint ORIGIN_FORCE_IDLE_SETTLE_FRAMES = 180;

        private static bool IsBypassRuntimeLoggingEnabled() => false;
        private static bool IsBypassPerfProbeLoggingEnabled() => false;
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
        private const uint BOARDING_TAIL_IGNORE_TTL_FRAMES = 900;
        private const uint BOARDING_TAIL_IGNORE_CLEANUP_INTERVAL_FRAMES = 256;
        private const bool ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS = false;

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
            m_ForcedMidStopBoardingHardCloseAfter = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
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
            m_ForcedOriginBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
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

            m_TransportVehicleRequestQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<TransportVehicleRequest>(),
                    ComponentType.ReadOnly<ServiceRequest>(),
                    ComponentType.ReadOnly<UpdateFrame>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>()
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
            if (m_ForcedMidStopBoardingHardCloseAfter.IsCreated) m_ForcedMidStopBoardingHardCloseAfter.Dispose();
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
            if (m_ForcedOriginBoardingGraceUntil.IsCreated) m_ForcedOriginBoardingGraceUntil.Dispose();
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

        private Entity ResolvePassingStationBuilding(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity))
                return Entity.Null;
            if (EntityManager.HasComponent<Building>(entity))
            {
                if (IsSelectableTransitStationBuilding(entity))
                    return entity;
                return Entity.Null;
            }

            Entity stopEntity = ResolveWorkbenchStopEntity(entity);
            if (stopEntity != Entity.Null)
            {
                Entity stationEntity = FindTransportStationFromStop(stopEntity);
                if (stationEntity != Entity.Null && IsSelectableTransitStationBuilding(stationEntity))
                    return stationEntity;
            }

            Entity current = entity;
            for (int i = 0; i < 8 && current != Entity.Null; i++)
            {
                if (EntityManager.HasComponent<Building>(current) && IsSelectableTransitStationBuilding(current))
                    return current;
                if (!EntityManager.HasComponent<Owner>(current))
                    break;
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
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

            if (!Input.GetKey(KeyCode.F5))
            {
                m_BypassToggleKeyArmed = true;
            }
            else if (m_BypassToggleKeyArmed)
            {
                m_BypassToggleKeyArmed = false;
                SetBypassRuntimeEnabled(!m_BypassRuntimeEnabled);
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
            CleanupDeferredBoardingTailIgnores(m_SimulationSystem.frameIndex);
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;

            EnsureLapCacheBuffer();
            EnsureVehicleCacheBuffer();
            EnsureDispatchCacheBuffer();
            if (IsStopDwellObservationPersistenceEnabled())
            {
                EnsureStopDwellObservationBuffer();
                RestoreStopDwellObservationsFromBuffer();
            }
            if (IsTraversalSliceObservationPersistenceEnabled())
            {
                EnsureTraversalSliceObservationBuffer();
                RestoreTraversalSliceObservationsFromBuffer();
            }
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

            FlushBypassPerfProbeIfDue(nowFrame);

        }

        private void FlushBypassPerfProbeIfDue(uint nowFrame)
        {
            if (!IsBypassPerfProbeLoggingEnabled())
                return;

            if (m_BypassPerfProbeLastLogFrame == 0)
            {
                m_BypassPerfProbeLastLogFrame = nowFrame;
                return;
            }

            uint elapsedFrames = nowFrame - m_BypassPerfProbeLastLogFrame;
            if (elapsedFrames < BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES)
                return;

            if (m_BypassPerfProbeCadenceCalls > 0
                || m_BypassPerfProbeCadenceMisses > 0
                || m_BypassPerfProbeBaselineCalls > 0
                || m_BypassPerfProbeTrackDecisionCalls > 0
                || m_BypassPerfProbeResolveCalls > 0
                || m_BypassPerfProbeActiveCorridorCalls > 0
                || m_BypassPerfProbeSceneSamples > 0
                || m_BypassPerfProbeSceneCandidateVehicles > 0
                || m_BypassPerfProbeSceneAdmittedCandidates > 0
                || m_BypassPerfProbeSceneFrontiers > 0
                || m_BypassPerfProbeSameStationCalls > 0
                || m_BypassPerfProbeSameStationReusedCandidates > 0
                || m_BypassPerfProbeDeepCorridorEntries > 0
                || m_BypassPerfProbeEpisodeReuses > 0)
            {
                log.Info("[待避轻量计数] frames=" + elapsedFrames
                    + " cadence=" + m_BypassPerfProbeCadenceCalls
                    + " miss=" + m_BypassPerfProbeCadenceMisses
                    + " baseline=" + m_BypassPerfProbeBaselineCalls
                    + " trackDecision=" + m_BypassPerfProbeTrackDecisionCalls
                    + " resolve=" + m_BypassPerfProbeResolveCalls
                    + " activeCorridor=" + m_BypassPerfProbeActiveCorridorCalls
                    + " scenes=" + m_BypassPerfProbeSceneSamples
                    + " cand=" + m_BypassPerfProbeSceneCandidateVehicles
                    + " admitted=" + m_BypassPerfProbeSceneAdmittedCandidates
                    + " frontiers=" + m_BypassPerfProbeSceneFrontiers
                    + " sameReuse=" + m_BypassPerfProbeSameStationReusedCandidates
                    + " sameCalls=" + m_BypassPerfProbeSameStationCalls
                    + " deepCorridor=" + m_BypassPerfProbeDeepCorridorEntries
                    + " episodeReuse=" + m_BypassPerfProbeEpisodeReuses);
            }

            m_BypassPerfProbeLastLogFrame = nowFrame;
            m_BypassPerfProbeCadenceCalls = 0;
            m_BypassPerfProbeCadenceMisses = 0;
            m_BypassPerfProbeBaselineCalls = 0;
            m_BypassPerfProbeTrackDecisionCalls = 0;
            m_BypassPerfProbeResolveCalls = 0;
            m_BypassPerfProbeActiveCorridorCalls = 0;
            m_BypassPerfProbeSceneSamples = 0;
            m_BypassPerfProbeSceneCandidateVehicles = 0;
            m_BypassPerfProbeSceneAdmittedCandidates = 0;
            m_BypassPerfProbeSceneFrontiers = 0;
            m_BypassPerfProbeSameStationCalls = 0;
            m_BypassPerfProbeSameStationReusedCandidates = 0;
            m_BypassPerfProbeDeepCorridorEntries = 0;
            m_BypassPerfProbeEpisodeReuses = 0;
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
            m_ForcedMidStopBoardingHardCloseAfter.Clear();
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
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_TraversalRunSliceObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_ForcedOriginBoardingGraceUntil.Clear();
            m_StopDwellStartFrame.Clear();
            m_StopDwellStartFrame.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            ClearBypassRuntimeState();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_WaypointIndexFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_DeferredBoardingTailIgnores.Clear();
            m_DeferredBoardingHumanTailIgnores.Clear();
            m_DeferredBoardingPetTailIgnores.Clear();
            m_DeferredBoardingTailScratch.Clear();
            m_MidStopTimeoutLogCache.Clear();
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
            m_ForcedMidStopBoardingHardCloseAfter.Clear();
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
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            m_StopDwellObservationBufferReady = false;
            m_StopDwellObservationCacheLoaded = false;
            m_TraversalRunSliceObservations.Clear();
            m_TraversalSliceObservationBufferReady = false;
            m_TraversalSliceObservationCacheLoaded = false;
            m_JustLaunched.Clear();
            m_DiagnosedLines.Clear();
            m_NearingTerminus.Clear();
            m_RestoredRunning.Clear();
            m_OriginArrivalCandidateSinceFrame.Clear();
            m_ForcedOriginReadyFrame.Clear();
            m_ForcedOriginBoardingGraceUntil.Clear();
            m_WaypointStopDwellObservations.Clear();
            m_StopDwellSessions.Clear();
            ClearBypassRuntimeState();
            m_LineRunningVehicleFrameSnapshots.Clear();
            m_RouteProgressFrameSnapshots.Clear();
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_DeferredBoardingTailIgnores.Clear();
            m_DeferredBoardingHumanTailIgnores.Clear();
            m_DeferredBoardingPetTailIgnores.Clear();
            m_DeferredBoardingTailScratch.Clear();
            m_MidStopTimeoutLogCache.Clear();
            m_LastPuppetMasterMinute = -1;
            m_LastRegisterSweepMinute = -1;
            m_LastSchedulerTickMinute = -1;
            ClearLineDispatchDebugSummaries();
            ClearDispatchLogCaches();
            log.Info("[启动] 已清空跨档运行态缓存");
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
                ClearVehicleTraversalSliceLapDebug(v);
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

                if (m_VehicleLine.TryGetValue(v, out Entity timingLine)
                    && timingLine != Entity.Null
                    && IsAppliedWorkbenchExpressLine(timingLine)
                    && EntityManager.HasBuffer<RouteWaypoint>(timingLine))
                {
                    DynamicBuffer<RouteWaypoint> timingWaypoints = EntityManager.GetBuffer<RouteWaypoint>(timingLine, true);
                    if (TryGetTraversalProfileLapTiming(
                            timingLine,
                            timingWaypoints,
                            out float profileRunFrames,
                            out float profileStopFrames,
                            out int profileStopCount,
                            out int profilePassCount))
                    {
                        float profileTotalFrames = profileRunFrames + profileStopFrames;
                        log.Info("[快车圈时] " + lineTag + " 车辆" + v.Index
                            + " 实测=" + realMin.ToString("F1") + "分钟"
                            + " profileTotal=" + (profileTotalFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                            + " run=" + (profileRunFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                            + " stop=" + (profileStopFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                            + " stopCount=" + profileStopCount
                            + " passCount=" + profilePassCount);
                        LogTraversalProfileLapSlices(v, timingLine, timingWaypoints);
                    }
                }

                if (m_VehicleLine.TryGetValue(v, out Entity lapLine))
                    FlushLineLapCache(lapLine);

                ClearVehicleTraversalSliceLapDebug(v);
            }
        }

        private bool TryGetTraversalProfileLapTiming(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out float runFrames,
            out float stopFrames,
            out int stopCount,
            out int passCount)
        {
            runFrames = 0f;
            stopFrames = 0f;
            stopCount = 0;
            passCount = 0;

            if (line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            EnsureTrackChainBypassPipelineReady(chain);
            if (chain.TraversalProfile == null)
                return false;

            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                if (TryGetEffectiveTraversalRunSliceFrames(line, chain.TraversalProfile.RunSlices[i], out float effectiveRunFrames))
                    runFrames += math.max(0f, effectiveRunFrames);
            }

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop)
                {
                    stopCount++;
                    stopFrames += math.max(0f, traversalEvent.StopFrames);
                }
                else if (traversalEvent.Kind == TraversalEventKind.Pass)
                {
                    passCount++;
                }
            }

            return runFrames > 0f || stopFrames > 0f || stopCount > 0 || passCount > 0;
        }

        private void UpdateVehicleTraversalSliceObservation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            if (!TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, out int sliceIndex, out VehicleTrackCursor cursor))
            {
                if (m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession droppedSession))
                    RecordTraversalSliceLapDebugDropped(vehicle, droppedSession.SliceIndex);
                m_VehicleTraversalSliceSessions.Remove(vehicle);
                return;
            }

            if (m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                && session.Line == line
                && session.SliceIndex == sliceIndex)
            {
                return;
            }

            FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame);
            if (TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                && chain.TraversalProfile != null
                && sliceIndex >= 0
                && sliceIndex < chain.TraversalProfile.RunSlices.Count)
            {
                RecordTraversalSliceLapDebugStart(vehicle, chain.TraversalProfile.RunSlices[sliceIndex], cursor.AtomCursorIndex, cursor.AtomPosition01);
            }

            m_VehicleTraversalSliceSessions[vehicle] = new VehicleTraversalSliceSession(line, sliceIndex, nowFrame, cursor.AtomCursorIndex, cursor.AtomPosition01);
        }

        private void FinalizeVehicleTraversalSliceObservation(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleTraversalSliceSessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session)
                || session.Line == Entity.Null
                || session.SliceIndex < 0
                || nowFrame <= session.EnterFrame)
            {
                m_VehicleTraversalSliceSessions.Remove(vehicle);
                return;
            }

            float observedFrames = nowFrame - session.EnterFrame;
            RecordTraversalSliceLapDebugFinalize(vehicle, session.SliceIndex, observedFrames);
            ulong key = MakeTraversalSliceObservationKey(session.Line, session.SliceIndex);
            if (m_TraversalRunSliceObservations.TryGetValue(key, out TraversalSliceObservation existing))
            {
                int sampleCount = existing.SampleCount + 1;
                float averageFrames = ((existing.AverageFrames * existing.SampleCount) + observedFrames) / sampleCount;
                float fastBaselineFrames = ComputeFastTraversalBaselineFrames(existing.FastBaselineFrames, observedFrames);
                TraversalSliceObservation updated = new TraversalSliceObservation(averageFrames, fastBaselineFrames, sampleCount, nowFrame);
                m_TraversalRunSliceObservations[key] = updated;
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, updated);
            }
            else
            {
                TraversalSliceObservation created = new TraversalSliceObservation(observedFrames, observedFrames, 1, nowFrame);
                m_TraversalRunSliceObservations[key] = created;
                FlushTraversalSliceObservation(session.Line, session.SliceIndex, created);
            }

            m_VehicleTraversalSliceSessions.Remove(vehicle);
        }

        private static float ComputeFastTraversalBaselineFrames(float existingFastBaselineFrames, float observedFrames)
        {
            if (!(observedFrames > 0f))
                return existingFastBaselineFrames;

            if (!(existingFastBaselineFrames > 0f))
                return observedFrames;

            const float fastFollowAlpha = 0.35f;
            const float slowFollowAlpha = 0.05f;
            float alpha = observedFrames <= existingFastBaselineFrames
                ? fastFollowAlpha
                : slowFollowAlpha;
            return math.lerp(existingFastBaselineFrames, observedFrames, alpha);
        }

        private bool TryGetCurrentTraversalRunSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            sliceIndex = -1;
            cursor = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain)
                || chain.TraversalProfile == null
                || chain.TraversalProfile.RunSlices.Count == 0
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out cursor))
            {
                return false;
            }

            int atomIndex = math.clamp(cursor.AtomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[i];
                if (atomIndex >= slice.StartAtomIndex && atomIndex < slice.EndAtomIndexExclusive)
                {
                    sliceIndex = i;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetEffectiveTraversalRunSliceFrames(
            Entity line,
            TraversalRunSlice slice,
            out float effectiveRunFrames)
        {
            effectiveRunFrames = math.max(0f, slice.RunFrames);
            if (line == Entity.Null || slice.SliceIndex < 0)
                return effectiveRunFrames > 0f;

            ulong key = MakeTraversalSliceObservationKey(line, slice.SliceIndex);
            if (m_TraversalRunSliceObservations.TryGetValue(key, out TraversalSliceObservation observation)
                && observation.SampleCount > 0
                && observation.FastBaselineFrames > 0f)
            {
                effectiveRunFrames = observation.FastBaselineFrames;
                return true;
            }

            return effectiveRunFrames > 0f;
        }

        private static ulong MakeTraversalSliceObservationKey(Entity line, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)line.Index << 32) | (uint)sliceIndex;
            }
        }

        private static ulong MakeVehicleTraversalSliceLapDebugKey(Entity vehicle, int sliceIndex)
        {
            unchecked
            {
                return ((ulong)(uint)vehicle.Index << 32) | (uint)sliceIndex;
            }
        }

        private void RecordTraversalSliceLapDebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
        {
            if (vehicle == Entity.Null || slice.SliceIndex < 0)
                return;

            float enterCoordinate = atomIndex + math.saturate(atomPosition01);
            float enterOffsetAtoms = math.max(0f, enterCoordinate - slice.StartAtomIndex);
            bool midSliceStart = enterOffsetAtoms > 0.05f;
            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, slice.SliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordStart(enterOffsetAtoms, midSliceStart);
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugDropped(Entity vehicle, int sliceIndex)
        {
            if (vehicle == Entity.Null || sliceIndex < 0)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.DroppedWithoutFinalizeCount++;
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void RecordTraversalSliceLapDebugFinalize(Entity vehicle, int sliceIndex, float observedFrames)
        {
            if (vehicle == Entity.Null || sliceIndex < 0 || observedFrames <= 0f)
                return;

            ulong key = MakeVehicleTraversalSliceLapDebugKey(vehicle, sliceIndex);
            if (!m_VehicleTraversalSliceLapDebug.TryGetValue(key, out TraversalSliceLapDebugAggregate aggregate))
                aggregate = default;

            aggregate.RecordFinalize(observedFrames);
            m_VehicleTraversalSliceLapDebug[key] = aggregate;
        }

        private void ClearVehicleTraversalSliceLapDebug(Entity vehicle)
        {
            if (vehicle == Entity.Null || m_VehicleTraversalSliceLapDebug.Count == 0)
                return;

            List<ulong> removeKeys = null;
            foreach (var kvp in m_VehicleTraversalSliceLapDebug)
            {
                if ((int)(kvp.Key >> 32) != vehicle.Index)
                    continue;

                if (removeKeys == null)
                    removeKeys = new List<ulong>();
                removeKeys.Add(kvp.Key);
            }

            if (removeKeys == null)
                return;

            for (int i = 0; i < removeKeys.Count; i++)
                m_VehicleTraversalSliceLapDebug.Remove(removeKeys[i]);
        }

        private void LogTraversalProfileLapSlices(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                return;
            }

            EnsureTrackChainBypassPipelineReady(chain);
            if (chain.TraversalProfile == null || chain.TraversalProfile.RunSlices.Count == 0)
                return;

            string lineTag = "线路" + line.Index;
            for (int i = 0; i < chain.TraversalProfile.RunSlices.Count; i++)
            {
                TraversalRunSlice slice = chain.TraversalProfile.RunSlices[i];
                string startLabel = DescribeTraversalBoundaryLabel(chain, slice.StartEventIndex, slice.StartAtomIndex);
                string endLabel = DescribeTraversalBoundaryLabel(chain, slice.EndEventIndex, slice.EndAtomIndexExclusive);
                float stopFrames = GetTraversalSliceStopFrames(chain, slice);
                float staticRunFrames = math.max(0f, slice.RunFrames);
                TryGetEffectiveTraversalRunSliceFrames(line, slice, out float effectiveRunFrames);
                ulong observationKey = MakeTraversalSliceObservationKey(line, slice.SliceIndex);
                bool hasObservation = m_TraversalRunSliceObservations.TryGetValue(observationKey, out TraversalSliceObservation observation)
                    && observation.SampleCount > 0
                    && observation.AverageFrames > 0f;
                ulong lapDebugKey = MakeVehicleTraversalSliceLapDebugKey(vehicle, slice.SliceIndex);
                bool hasLapDebug = m_VehicleTraversalSliceLapDebug.TryGetValue(lapDebugKey, out TraversalSliceLapDebugAggregate lapDebug);
                string lapDebugText = string.Empty;
                if (hasLapDebug && lapDebug.StartCount > 0)
                {
                    float avgEnterOffset = lapDebug.EnterOffsetSumAtoms / math.max(1, lapDebug.StartCount);
                    lapDebugText = " lapStart=" + lapDebug.StartCount
                        + " midStart=" + lapDebug.MidSliceStartCount
                        + " drop=" + lapDebug.DroppedWithoutFinalizeCount
                        + " enterOffsetAvg=" + avgEnterOffset.ToString("0.00")
                        + "a"
                        + " enterOffsetMax=" + lapDebug.MaxEnterOffsetAtoms.ToString("0.00")
                        + "a";
                    if (lapDebug.FinalizeCount > 0)
                    {
                        lapDebugText += " obsLapAvg=" + (lapDebug.ObservedFramesSum / lapDebug.FinalizeCount / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                            + " obsLapMin=" + (lapDebug.MinObservedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                            + " obsLapMax=" + (lapDebug.MaxObservedFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟";
                    }
                }

                log.Info("[快车圈时明细] " + lineTag + " 车辆" + vehicle.Index
                    + " slice#" + slice.SliceIndex
                    + " " + startLabel + " -> " + endLabel
                    + " run=" + (math.max(0f, effectiveRunFrames) / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                    + " staticRun=" + (staticRunFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                    + " stop=" + (stopFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                    + " total=" + ((math.max(0f, effectiveRunFrames) + stopFrames) / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                    + " atoms=" + slice.StartAtomIndex + ".." + slice.EndAtomIndexExclusive
                    + " laneKeys=" + (slice.PhysicalLaneKeys != null ? slice.PhysicalLaneKeys.Length : 0)
                    + " obsGlobal=" + (hasObservation ? observation.SampleCount.ToString() : "0")
                    + (hasObservation
                        ? " obsGlobalAvg=" + (observation.AverageFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                        : string.Empty)
                    + (hasObservation && observation.FastBaselineFrames > 0f
                        ? " obsGlobalFast=" + (observation.FastBaselineFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("0.0") + "分钟"
                        : string.Empty)
                    + lapDebugText);
            }
        }

        private static float GetTraversalSliceStopFrames(LineTrackChain chain, TraversalRunSlice slice)
        {
            if (chain?.TraversalProfile == null)
                return 0f;

            for (int i = 0; i < chain.TraversalProfile.Events.Count; i++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[i];
                if (traversalEvent.Kind == TraversalEventKind.Stop
                    && traversalEvent.StartAtomIndex == slice.StartAtomIndex
                    && traversalEvent.EndAtomIndexExclusive == slice.EndAtomIndexExclusive)
                {
                    return math.max(0f, traversalEvent.StopFrames);
                }
            }

            return 0f;
        }

        private string DescribeTraversalBoundaryLabel(LineTrackChain chain, int eventIndex, int atomIndex)
        {
            if (chain != null
                && chain.TraversalProfile != null
                && eventIndex >= 0
                && eventIndex < chain.TraversalProfile.Events.Count)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                bool eventMatchesBoundary = traversalEvent.StartAtomIndex == atomIndex
                    || traversalEvent.EndAtomIndexExclusive == atomIndex;
                if (eventMatchesBoundary)
                {
                    string buildingLabel = traversalEvent.Building != Entity.Null
                        ? FormatBypassNodeLabel(traversalEvent.Building)
                        : "atom" + atomIndex;
                    switch (traversalEvent.Kind)
                    {
                        case TraversalEventKind.Stop:
                            return "Stop(" + buildingLabel + ")";
                        case TraversalEventKind.Pass:
                            return "Pass(" + buildingLabel + ")";
                        case TraversalEventKind.ApproachSplitBoundary:
                            return "ApproachSplit(" + buildingLabel + ")";
                        case TraversalEventKind.DepartureSplitBoundary:
                            return "DepartureSplit(" + buildingLabel + ")";
                    }
                }
            }

            return "atom" + atomIndex;
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

            bool hasTrackChain = TryGetLineTrackChain(line, waypoints, out LineTrackChain trackChain);

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
                bool hasTrackCursor = false;
                VehicleTrackCursor trackCursor = default;
                int currentControlEdgeIndex = -1;
                float ownLineAtomCoordinate = 0f;
                int phaseEndAtomExclusive = -1;
                if (hasTrackChain)
                {
                    hasTrackCursor = TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                        vehicle,
                        line,
                        waypoints,
                        trackChain,
                        out trackCursor,
                        out currentControlEdgeIndex,
                        out ownLineAtomCoordinate,
                        out phaseEndAtomExclusive);
                }

                snapshot.Vehicles.Add(new LineRunningVehicleSnapshot(
                    vehicle,
                    nextWaypointIndex,
                    boarding,
                    hasProjection,
                    hasProjection ? projection.DistanceMeters : 0f,
                    hasTrackCursor,
                    trackCursor,
                    currentControlEdgeIndex,
                    ownLineAtomCoordinate,
                    phaseEndAtomExclusive));
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
            BuildApproachStationCacheData(
                waypoints,
                buildingDistances,
                out int[] previousDistinctStationWaypointIndices,
                out float[] previousDistinctStationMeters,
                out float[] currentStationMeters);

            return new LineMileageModel
            {
                Signature = signature,
                TotalDistanceMeters = cumulative,
                WaypointDistances = anchors,
                BypassWaypointDistances = bypassWaypointDistances,
                BypassStopNodeDistances = bypassStopNodeDistances,
                PreviousDistinctStationWaypointIndices = previousDistinctStationWaypointIndices,
                PreviousDistinctStationMeters = previousDistinctStationMeters,
                CurrentStationMeters = currentStationMeters,
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

        private void BuildApproachStationCacheData(
            DynamicBuffer<RouteWaypoint> waypoints,
            Dictionary<Entity, float> buildingDistances,
            out int[] previousDistinctStationWaypointIndices,
            out float[] previousDistinctStationMeters,
            out float[] currentStationMeters)
        {
            int count = waypoints.Length;
            previousDistinctStationWaypointIndices = new int[count];
            previousDistinctStationMeters = new float[count];
            currentStationMeters = new float[count];

            for (int i = 0; i < count; i++)
            {
                previousDistinctStationWaypointIndices[i] = -1;
            }

            if (count == 0 || buildingDistances == null || buildingDistances.Count == 0)
                return;

            for (int waypointIndex = 0; waypointIndex < count; waypointIndex++)
            {
                Entity currentStationBuilding = GetStationBuildingForWaypoint(waypoints, waypointIndex);
                if (currentStationBuilding == Entity.Null
                    || !buildingDistances.TryGetValue(currentStationBuilding, out float currentMeters))
                {
                    continue;
                }

                currentStationMeters[waypointIndex] = currentMeters;
                for (int offset = 1; offset < count; offset++)
                {
                    int candidateIndex = waypointIndex - offset;
                    if (candidateIndex < 0)
                        candidateIndex += count;

                    Entity candidateBuilding = GetStationBuildingForWaypoint(waypoints, candidateIndex);
                    if (candidateBuilding == Entity.Null || candidateBuilding == currentStationBuilding)
                        continue;
                    if (!buildingDistances.TryGetValue(candidateBuilding, out float previousMeters))
                        break;

                    previousDistinctStationWaypointIndices[waypointIndex] = candidateIndex;
                    previousDistinctStationMeters[waypointIndex] = previousMeters;
                    break;
                }
            }
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

        private bool TryEstimateRemainingBoardingDwellFrames(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            uint nowFrame,
            out float remainingFrames)
        {
            remainingFrames = 0f;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentWaypointIndex <= 0
                || currentWaypointIndex >= waypoints.Length
                || currentBypassBuilding == Entity.Null
                || GetBypassBuildingForWaypoint(waypoints, currentWaypointIndex) != currentBypassBuilding
                || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                return false;
            }

            if ((EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            if (!m_StopDwellStartFrame.TryGetValue(vehicle, out uint dwellSinceFrame) || nowFrame <= dwellSinceFrame)
                return false;

            float elapsedFrames = nowFrame - dwellSinceFrame;
            int maxStationDwellMinutes = GetWorkbenchMaxStationDwellMinutes(line);
            float timeoutRemainingFrames = 0f;
            if (maxStationDwellMinutes > 0)
            {
                uint timeoutDeadlineFrame = ComputeAdjustedStopDwellDeadlineFrame(line, currentWaypointIndex, dwellSinceFrame, maxStationDwellMinutes);
                if (nowFrame < timeoutDeadlineFrame)
                    timeoutRemainingFrames = timeoutDeadlineFrame - nowFrame;
            }

            float estimatedRemainingFrames = 0f;
            if (TryGetObservedWaypointStopFrames(line, currentWaypointIndex, out float observedDwellFrames)
                && observedDwellFrames > 0f)
            {
                estimatedRemainingFrames = math.max(0f, observedDwellFrames - elapsedFrames);
            }

            if (!(estimatedRemainingFrames > 0f) && timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = timeoutRemainingFrames;

            if (timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = math.min(estimatedRemainingFrames, timeoutRemainingFrames);

            if (!(estimatedRemainingFrames > 0f))
                return false;

            remainingFrames = estimatedRemainingFrames;
            return true;
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
                FlushStopDwellObservation(line, waypointIndex, m_WaypointStopDwellObservations[key]);
                InvalidateTrackTimingForLine(line);
                return;
            }

            m_WaypointStopDwellObservations[key] = new StopDwellObservation
            {
                AverageFrames = sampleFrames,
                SampleCount = 1
            };
            FlushStopDwellObservation(line, waypointIndex, m_WaypointStopDwellObservations[key]);
            InvalidateTrackTimingForLine(line);
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

    }
}
