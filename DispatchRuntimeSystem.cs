// fix: IsSlotExpired 无法覆盖大幅过期班次，Holding 车卡死；调度器未过滤过期槽；Running->Idle 未清 targetMin；新线路不产车；UI 标签残留；关闭线路无效；始发站压队
// - IsSlotExpired 上限由 SLOT_INTERVAL(30) 改为 SPAWN_LEAD_MIN + SLOT_GRACE_MIN(64)，覆盖所有真过期场景
// - Holding 过期处理细分：overdue > SLOT_INTERVAL 直接回库，否则释放槽等重新分配
// - 调度器槽扫描入口加 IsSlotExpired 检查，过期槽直接跳过不参与分配
// - Running->Idle 时清理旧的 targetMin，防止旧槽值残留导致下帧直接 Idle->Holding 绕过调度保护
// - PuppetMaster：D=0 时改用 iDefault 兜底；每帧清理 m_SpawningLines 中已不存在的线路 Entity 记录
// - 注册新车时清理 m_UICache，防止旧线路缓存导致 SetUILabel 去重跳过、UI 标签不刷新
// - m_LineQuery 加 Disabled 过滤，关闭线路后调度器停止处理，现有车跑完当前圈自然 Idle 超时回库
// - 新增末端入站标签：车进入最后一个 waypoint 时打标签，Idle->Holding 前检查本线路有无标签车距始发站 <= 350 米，有则回库疏解

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Creatures;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI;
using RapidTransitMod.Dispatch.Scheduling;
using Game.UI.InGame;
using Game.Vehicles;
using RapidTransitMod.Bypass;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Dispatch.Workbench;
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

    public partial class DispatchRuntimeSystem : GameSystemBase
    {
        internal CameraUpdateSystem m_CameraUpdateSystem;
        internal Broadcasting.WorkbenchBackend.Workbench m_AnnouncementWorkbench;
        internal Broadcasting.Runtime m_Announcements;

        internal readonly struct AssistLaunchPendingRecord
        {
            public readonly Entity Line;
            public readonly int TargetMin;

            public AssistLaunchPendingRecord(Entity line, int targetMin)
            {
                Line = line;
                TargetMin = targetMin;
            }
        }

        internal sealed class TrackBuffers : TrackModelContext.IBuffers
        {
            private readonly DispatchRuntimeSystem m_Owner;

            public TrackBuffers(DispatchRuntimeSystem owner)
            {
                m_Owner = owner;
            }

            public BufferLookup<T> Get<T>(bool readOnly) where T : unmanaged, IBufferElementData
            {
                return m_Owner.GetBufferLookup<T>(readOnly);
            }
        }

        public string LoadBroadcastWorkbenchSnapshotJson(string preferredLineId)
            => m_AnnouncementWorkbench?.LoadBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;

        public string RefreshBroadcastWorkbenchSnapshotJson(string preferredLineId)
            => m_AnnouncementWorkbench?.RefreshBroadcastWorkbenchSnapshotJson(preferredLineId) ?? string.Empty;

        public string LoadBroadcastBindingSlotHintsJson(string lineId)
            => m_AnnouncementWorkbench?.LoadBroadcastBindingSlotHintsJson(lineId) ?? string.Empty;

        public string LoadBroadcastAssetBrowserJson(string requestedPath)
            => m_AnnouncementWorkbench?.LoadBroadcastAssetBrowserJson(requestedPath) ?? string.Empty;

        public string SaveBroadcastRulesJson(string requestJson)
            => m_AnnouncementWorkbench?.SaveBroadcastRulesJson(requestJson) ?? string.Empty;

        public string SaveBroadcastPlatformAnnouncementJson(string requestJson)
            => m_AnnouncementWorkbench?.SaveBroadcastPlatformAnnouncementJson(requestJson) ?? string.Empty;

        public string CopyBroadcastPlatformAnnouncementToAllStationsJson(string requestJson)
            => m_AnnouncementWorkbench?.CopyBroadcastPlatformAnnouncementToAllStationsJson(requestJson) ?? string.Empty;

        public string ImportBroadcastExternalAssetsJson(string requestJson)
            => m_AnnouncementWorkbench?.ImportBroadcastExternalAssetsJson(requestJson) ?? string.Empty;

        public string SaveBroadcastStationBindingJson(string requestJson)
            => m_AnnouncementWorkbench?.SaveBroadcastStationBindingJson(requestJson) ?? string.Empty;

        public string SaveBroadcastStationBindingsJson(string requestJson)
            => m_AnnouncementWorkbench?.SaveBroadcastStationBindingsJson(requestJson) ?? string.Empty;

        public string DeleteBroadcastAssetJson(string requestJson)
            => m_AnnouncementWorkbench?.DeleteBroadcastAssetJson(requestJson) ?? string.Empty;

        public string DeleteAllBroadcastAssetsJson()
            => m_AnnouncementWorkbench?.DeleteAllBroadcastAssetsJson() ?? string.Empty;

        public string AutoBindBroadcastStationMappingsJson(string requestJson)
            => m_AnnouncementWorkbench?.AutoBindBroadcastStationMappingsJson(requestJson) ?? string.Empty;

        public string ApplyBroadcastConfigJson(string requestJson)
            => m_AnnouncementWorkbench?.ApplyBroadcastConfigJson(requestJson) ?? string.Empty;

        public string OpenBroadcastAssetDirectoryPickerJson()
            => m_AnnouncementWorkbench?.OpenBroadcastAssetDirectoryPickerJson() ?? string.Empty;

        public string PlayBroadcastAssetPreviewJson(string assetName)
            => m_AnnouncementWorkbench?.PlayBroadcastAssetPreviewJson(assetName) ?? string.Empty;

        public string PlayBroadcastRulePreviewJson(string requestJson)
            => m_AnnouncementWorkbench?.PlayBroadcastRulePreviewJson(requestJson) ?? string.Empty;

        public string StopBroadcastAssetPreviewJson(string assetName)
            => m_AnnouncementWorkbench?.StopBroadcastAssetPreviewJson(assetName) ?? string.Empty;

        public string StopBroadcastRulePreviewJson(string ruleId)
            => m_AnnouncementWorkbench?.StopBroadcastRulePreviewJson(ruleId) ?? string.Empty;

        public string SetBroadcastPreviewVolumeJson(string volumeJson)
            => m_AnnouncementWorkbench?.SetBroadcastPreviewVolumeJson(volumeJson) ?? string.Empty;

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

        internal string LineId(Entity line)
            => m_WorkbenchBridge.Ids().Get(line);

        internal string EntityName(Entity entity)
            => m_WorkbenchBridge.Name(entity);

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

        internal readonly struct TrainHeadSnapshot
        {
            public readonly uint Frame;
            public readonly Entity HeadVehicle;
            public readonly Entity FrontLane;
            public readonly Entity RearLane;
            public readonly bool Reversed;
            public readonly int WaypointIndex;

            public TrainHeadSnapshot(
                uint frame,
                Entity headVehicle,
                Entity frontLane,
                Entity rearLane,
                bool reversed,
                int waypointIndex)
            {
                Frame = frame;
                HeadVehicle = headVehicle;
                FrontLane = frontLane;
                RearLane = rearLane;
                Reversed = reversed;
                WaypointIndex = waypointIndex;
            }
        }

        internal readonly struct WaypointIndexFrameSnapshot
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

        internal readonly struct RouteProgressFrameSnapshot
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

        internal sealed class LineMileageModel
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

        internal struct CorridorNode
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

        internal struct LineDistanceProjection
        {
            public float TotalDistanceMeters;
            public float DistanceMeters;
            public float Progress01;
            public int NextWaypointIndex;
            public float SegmentPosition;
        }

        public static DispatchRuntimeSystem Instance = null!;
        internal TimedLogger log = Mod.log;
        internal SimulationSystem m_SimulationSystem = null!;
        internal TimeSystem m_TimeSystem = null!;
        internal NameSystem m_NameSystem = null!;
        internal EndFrameBarrier m_EndFrameBarrier = null!;

        // ── 车辆状态 ──
        internal VehicleStateStore m_VehicleStateStore = null!;
        internal VehicleRegistry m_VehicleRegistry = null!;
        internal VehicleView m_VehicleView = null!;
        internal LineView m_LineView = null!;
        internal FeatureGate m_Features = null!;
        internal DispatchRuntimeController m_RuntimeController = null!;
        internal VehicleRegistrar m_VehicleRegistrar = null!;
        internal RuntimeVehicleLabels m_VehicleLabels = null!;
        internal RuntimeResolve m_Resolve = null!;
        internal SelectPort m_SelectPort = null!;
        internal SelectPanel m_SelectPanel = null!;
        internal StationAnchorDiagnostics m_StationAnchorDiagnostics = null!;
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
        internal DwellStore m_Dwell = null!;
        internal SliceStore m_Slices = null!;
        internal TraceStore m_Obs = null!;
        internal Recorder m_ObsRecorder = null!;
        internal Capture m_ObsCapture = null!;
        internal RapidTransitMod.Dispatch.Observation.RuntimeObs m_RuntimeObs = null!;
        internal ObservationPort m_Observation = null!;
        internal Buffers m_ObsBuffers = null!;
        internal LineRange m_LineRange = null!;
        internal LineProfile m_LineProfile = null!;
        internal LineTimes m_LineTimes = null!;
        internal LineVehicles m_LineVehicles = null!;
        internal RouteProgress m_RouteProgress = null!;
        internal WaypointIndex m_WaypointIndex = null!;
        internal RapidTransitMod.Dispatch.Observation.Query m_ObsQuery = null!;
        internal RapidTransitMod.Dispatch.Observation.Persist m_ObsPersist = null!;
        internal NativeHashMap<Entity, FixedString64Bytes> m_UICache;
        internal NativeHashMap<Entity, bool> m_LastBoarding;
        internal NativeHashMap<Entity, int> m_CachedWpIdx;
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
        internal NativeHashMap<Entity, uint> m_LastRetireFixLogFrame;
        internal NativeHashMap<Entity, uint> m_RetireFixCooldownUntil;
        internal NativeHashMap<Entity, uint> m_PreparingFixCooldownUntil;
        internal NativeHashMap<Entity, byte> m_RetireFixCount;
        internal readonly Dictionary<Entity, AssistLaunchPendingRecord> m_AssistLaunchPendingByVehicle = new Dictionary<Entity, AssistLaunchPendingRecord>();
        private readonly Dictionary<Entity, LineMileageModel> m_LineMileageModels = new Dictionary<Entity, LineMileageModel>();
        private SharedLocalCorridorGraph m_SharedLocalCorridorGraph;
        internal readonly Dictionary<Entity, string> m_PreparingSlotLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_PreparingTargetDriftLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_CrossLineCandidateLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_RouteVehicleOwnerMismatchLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_HoldingSkipLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_LateDispatchLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_BvMisfireObserveLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_DepartureObserveLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_OriginDispatchTraceLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, uint> m_OriginDispatchTraceLastLogFrameCache = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, string> m_DispatchSlotHeldLogCache = new Dictionary<Entity, string>();
        private readonly Dictionary<Entity, uint> m_DispatchSlotHeldLastLogFrameCache = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, string> m_BvWaypointMismatchLogCache = new Dictionary<Entity, string>();
        private const bool ENABLE_TRACK_WAYPOINT_ANCHORING = true;
        internal readonly Dictionary<Entity, string> m_BvTrackAnchorRecoveryLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, TrainHeadSnapshot> m_LastLaunchHeadSnapshots = new Dictionary<Entity, TrainHeadSnapshot>();
        internal readonly Dictionary<Entity, TrainHeadSnapshot> m_LastBoardingHeadSnapshots = new Dictionary<Entity, TrainHeadSnapshot>();
        internal readonly Dictionary<Entity, string> m_MidStopTimeoutLogCache = new Dictionary<Entity, string>();
        internal static bool IsTraversalSliceObservationPersistenceEnabled() => true;
        internal static bool IsDwellObservationPersistenceEnabled() => false;
        internal static bool IsStationDwellObservationPersistenceEnabled() => true;
        internal readonly Dictionary<Entity, uint> m_BvWaypointMismatchLastLogFrame = new Dictionary<Entity, uint>();
        
        internal readonly Dictionary<Entity, WaypointIndexFrameSnapshot> m_WaypointIndexFrameSnapshots = new Dictionary<Entity, WaypointIndexFrameSnapshot>();
        internal readonly Dictionary<Entity, RouteProgressFrameSnapshot> m_RouteProgressFrameSnapshots = new Dictionary<Entity, RouteProgressFrameSnapshot>();
        private bool m_CorridorModelFaulted = false;
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
        internal NativeHashMap<Entity, ulong> m_LineWaypointSignature;
        internal NativeHashMap<Entity, uint> m_LineStableSinceFrame;
        internal NativeHashSet<Entity> m_LineInitialAdopted;
        internal NativeHashMap<Entity, LineTimeProfileHeader> m_LineTimeProfiles;
        internal NativeList<float> m_LineTimeProfileSegmentFrames;
        internal NativeList<float> m_LineTimeProfileStopFrames;

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

        // ── 诊断 ──

        internal uint GetCurrentSimulationFrameIndex()
        {
            return m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0;
        }

        internal bool IsRtManagedLine(Entity line)
        {
            return line != Entity.Null
                && EntityManager.Exists(line)
                && !EntityManager.HasComponent<Disabled>(line)
                && m_LineView.Managed(line, m_Features.Dispatch());
        }

        internal bool TryGetRtSpawnTarget(Entity line, out int targetCount)
        {
            targetCount = 0;
            if (line == Entity.Null || !m_SpawningLines.IsCreated)
                return false;

            return m_SpawningLines.TryGetValue(line, out targetCount);
        }

        internal int CountRtActiveVehicles(Entity line)
        {
            if (line == Entity.Null || !EntityManager.Exists(line))
                return 0;

            BufferLookup<RouteVehicle> routeVehicles = GetBufferLookup<RouteVehicle>(true);
            return m_LineVehicles.Count(line, routeVehicles);
        }

        internal bool IsWaitingForcedOriginDwell(Entity vehicle, uint nowFrame)
        {
            return m_VehicleView.TryGetReady(vehicle, out uint readyFrame) && nowFrame < readyFrame;
        }

        internal void ClearForcedMidStopClosingConsist(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        internal bool IsRuntimeReadyForOriginArrivingRepair()
        {
            return m_SystemReady;
        }

        internal bool TryGetRuntimeVehicleState(Entity vehicle, out VehicleState state)
        {
            state = default;
            return m_VehicleStateStore.State.IsCreated && m_VehicleView.TryGetState(vehicle, out state);
        }

        internal bool IsFreshDispatchedPreparingVehicle(Entity vehicle, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !m_VehicleView.TryGetDispatch(vehicle, out uint dispatchStartFrame))
            {
                return false;
            }

            return nowFrame >= dispatchStartFrame
                && (nowFrame - dispatchStartFrame) <= PREPARING_ROUTE_FIX_GRACE_FRAMES;
        }

        internal bool IsSuppressedForcedMidStopBoardingGhost(
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
            if (waypoint == Entity.Null
                || !EntityManager.Exists(waypoint)
                || !EntityManager.HasComponent<Connected>(waypoint))
            {
                return Entity.Null;
            }

            Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return connected != Entity.Null && EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
        }

        internal bool IsRtParkedVehicleRequest(Entity request, Entity line)
        {
            if (request == Entity.Null
                || !EntityManager.Exists(request)
                || !EntityManager.HasComponent<RtVehicleRequestSentinel>(request)
                || !EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
            return line == Entity.Null || vehicleRequest.m_Route == line;
        }

        internal bool IsRtSpawnPermitRequest(Entity request, Entity line)
        {
            if (request == Entity.Null
                || !EntityManager.Exists(request)
                || !EntityManager.HasComponent<RtSpawnPermitRequest>(request)
                || !EntityManager.HasComponent<TransportVehicleRequest>(request))
            {
                return false;
            }

            TransportVehicleRequest vehicleRequest = EntityManager.GetComponentData<TransportVehicleRequest>(request);
            return line == Entity.Null || vehicleRequest.m_Route == line;
        }

        internal bool ShouldDestroyOfficialTransportVehicleRequest(Entity request, Entity line)
        {
            if (line == Entity.Null
                || !EntityManager.Exists(line)
                || !m_LineView.Managed(line, m_Features.Dispatch()))
                return false;

            if (IsRtParkedVehicleRequest(request, line) || IsRtSpawnPermitRequest(request, line))
                return false;

            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return true;

            DynamicBuffer<RouteVehicle> routeVehicles = EntityManager.GetBuffer<RouteVehicle>(line, true);
            uint nowFrame = GetCurrentSimulationFrameIndex();
            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = m_Resolve.RuntimeVehicle(routeVehicles[i].m_Vehicle);
                if (IsFreshDispatchedPreparingVehicle(vehicle, nowFrame))
                    return false;
            }

            return true;
        }

        internal bool ShouldDestroyOfficialTransportVehicleRequest(Entity line)
        {
            return ShouldDestroyOfficialTransportVehicleRequest(Entity.Null, line);
        }
        internal NativeHashSet<Entity> m_DiagnosedLines;

        // ── 启动稳定检测（仅启动阶段执行一次，通过后永久关闭）──
        private bool m_SystemReady = false;
        private bool m_StartupRuntimeStateCleared = false;
        private int m_StableFrameCount = 0;
        private int m_LastVehicleCount = -1;
        internal int m_LastPuppetMasterMinute = -1;
        private int m_LastRegisterSweepMinute = -1;
        internal int m_LastSchedulerTickMinute = -1;
        private const int STABLE_FRAMES_REQUIRED = 5;

        // ── 定期写缓存 ──
        private uint m_LastVehicleCacheFlushFrame = 0;
        private const uint VEHICLE_CACHE_FLUSH_INTERVAL = 300;

        internal EntityQuery m_VehicleQuery;
        private EntityQuery m_AllPublicTransportQuery;
        internal EntityQuery m_LineQuery;
        internal const int SLOT_INTERVAL = 30;
        internal const int SPAWN_LEAD_MIN = 60;
        internal const float MAINTENANCE_THRESHOLD = 0.9f;
        internal const int IDLE_TIMEOUT_MIN = 2;
        internal const double SIM_FRAMES_PER_MINUTE = 182.044;
        internal const float EARLY_STOP_DWELL_CLOSE_MAX_MINUTES = 3f;
        private const float AT_STOP_MAX_DIST = 300f;
        /// <summary>班次宽限分钟数：发车窗口和过期判断共用同一阈值。</summary>
        internal const int SLOT_GRACE_MIN = 4;
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
        private const uint RETIREFIX_LOG_COOLDOWN_FRAMES = 1800;
        private const uint RETIREFIX_REPATH_COOLDOWN_FRAMES = 120;
        internal const uint RETIRE_HANDOFF_RETRY_INTERVAL_FRAMES = 30;
        internal const uint RETIRE_HANDOFF_TRACE_COOLDOWN_FRAMES = 180;
        private const uint ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES = 1800;
        internal const byte RETIRE_HANDOFF_MAX_ATTEMPTS = 12;
        internal const uint PREPARINGFIX_REPATH_COOLDOWN_FRAMES = 120;
        private const uint BV_WAYPOINT_MISMATCH_LOG_COOLDOWN_FRAMES = 120;
        private const uint BYPASS_HELD_REEVALUATE_INTERVAL_FRAMES = 8;
        private const uint BYPASS_EPISODE_RELEASE_RECHECK_INTERVAL_FRAMES = 60;
        internal const float BOARDING_CLOSE_BYPASS_MIN_WAITING_DISTANCE_SENTINEL = -1f;
        private const uint BYPASS_UNLATCHED_REEVALUATE_INTERVAL_FRAMES = 6;
        private const uint BYPASS_TRACKMODEL_DETAIL_LOG_COOLDOWN_FRAMES = 60;
        private const uint BYPASS_PERF_PROBE_LOG_INTERVAL_FRAMES = 3600;
        private const byte RETIREFIX_DELETE_THRESHOLD = 3;
        internal const float DISPATCH_ESTIMATE_MIN_MINUTES = 2f;
        internal const float DISPATCH_ESTIMATE_MAX_MINUTES = 20f;
        private const float DISPATCH_FALLBACK_SPEED_M_PER_MIN = 450f;
        private const float PROFILE_STOP_START_BUFFER_MINUTES = 3f;
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
        private static bool IsBypassRuntimeLoggingEnabled() => false;
        internal static bool IsLineOrderedRuntimeLoggingEnabled() => true;
        private static bool IsTrackModelTurnbackBuildLoggingEnabled() => true;
        private const uint PERF_PROBE_SCENE_EXPRESS_LINE_RECENT_WINDOW_FRAMES = 30;
        internal const uint RETIRE_SHADOW_SAMPLE_INTERVAL_FRAMES = 30;
        internal const int RETIRE_SHADOW_HISTORY_LIMIT = 4;
        internal static readonly uint RETIRE_HANDOFF_MAX_AGE_FRAMES = (uint)math.round(
            3f * (float)SIM_FRAMES_PER_MINUTE);
        private const float ORIGIN_ARRIVAL_HOLD_MINUTES = 2f;
        internal static readonly uint FORCED_ORIGIN_MIN_DWELL_FRAMES = (uint)math.round(3f * (float)SIM_FRAMES_PER_MINUTE);
        internal static readonly uint PREPARING_ORIGIN_SETTLE_FRAMES = (uint)math.max(1f, math.round(2f * (float)SIM_FRAMES_PER_MINUTE));
        internal const float SPAWN_TRIGGER_BUFFER_SHORT_MINUTES = 10f;
        internal const float SPAWN_TRIGGER_BUFFER_LONG_MINUTES = 15f;
        internal const float SPAWN_TRIGGER_BUFFER_THRESHOLD_MINUTES = 20f;
        private const uint TRAVERSAL_SLICE_SAMPLE_INTERVAL_MEDIUM_FRAMES = 20;
        private const uint TRAVERSAL_SLICE_SAMPLE_INTERVAL_LOW_FRAMES = 60;
        private const float TRAVERSAL_SLICE_SAMPLE_HIGH_THRESHOLD = 0.03f;
        private const float TRAVERSAL_SLICE_SAMPLE_MEDIUM_THRESHOLD = 0.05f;
        internal const int YIELD_PROTECT_MINUTES = 5;
        internal const int LATE_DISPATCH_WINDOW_MINUTES = 8;
        private const float ETA_SCALE_MIN = 0.5f;
        private const float ETA_SCALE_MAX = 2.0f;
        private const uint NEW_LINE_STABLE_FRAMES = 300;
        private const int DISPATCH_SAMPLE_HISTORY_LIMIT = 8;
        private const float DISPATCH_SAMPLE_OUTLIER_FACTOR = 1.5f;
        private const float DISPATCH_FAST_SAMPLE_MARGIN = 0.98f;
        private const float DISPATCH_SLOW_SAMPLE_BLEND = 0.5f;
        private const float DISPATCH_SLOW_SAMPLE_MAX_STEP_MINUTES = 4f;
        private const uint BYPASS_YIELD_DECISION_COOLDOWN_FRAMES = 30;
        private const uint PREPARING_ROUTE_FIX_GRACE_FRAMES = 300;
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
                    ComponentType.ReadOnly<Disabled>()
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

        private bool TryGetTraversalProfileLapTiming(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out float runFrames,
            out float stopFrames,
            out int stopCount,
            out int passCount)
            => m_RuntimeObs.LapTiming(line, waypoints, out runFrames, out stopFrames, out stopCount, out passCount);

        private bool ShouldSampleVehicleTraversalSliceObservation(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
            => m_RuntimeObs.ShouldSample(vehicle, line, waypoints, nowFrame);

        private bool TryBuildTraversalSliceSamplingPlan(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out TraversalSliceSamplingPlan plan)
            => m_RuntimeObs.BuildPlan(vehicle, line, waypoints, out plan);

        private bool TryBuildTraversalSliceSamplingPlanUncached(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out TraversalSliceSamplingPlan plan)
            => m_RuntimeObs.BuildPlanRaw(vehicle, waypoints, chain, out plan);

        private void MaybeRecordTraversalPositionSample(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int sliceIndex,
            VehicleTrackCursor cursor,
            uint nowFrame)
            => m_RuntimeObs.RecordSample(vehicle, line, chain, sliceIndex, cursor, nowFrame);

        private static float ComputeFastTraversalBaselineFrames(float existingFastTraversalBaselineFrames, float observedFrames)
            => Capture.ComputeFastTraversalBaselineFrames(existingFastTraversalBaselineFrames, observedFrames);

        private bool TryGetCurrentTraversalRunSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
            => m_RuntimeObs.CurrentSlice(vehicle, line, waypoints, out chain, out sliceIndex, out cursor);

        internal bool TryGetEffectiveTraversalRunSliceFrames(
            Entity line,
            TraversalRunSlice slice,
            out float effectiveRunFrames)
            => m_RuntimeObs.EffectiveFrames(line, slice, out effectiveRunFrames);

        private void RecordTraversalSliceLapDebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
            => m_RuntimeObs.DebugStart(vehicle, slice, atomIndex, atomPosition01);

        internal void RecordTraversalSliceLapDebugDropped(Entity vehicle, int sliceIndex)
            => m_RuntimeObs.DebugDrop(vehicle, sliceIndex);

        private void RecordTraversalSliceLapDebugFinalize(Entity vehicle, int sliceIndex, float observedFrames)
            => m_RuntimeObs.DebugFinish(vehicle, sliceIndex, observedFrames);

        internal void ClearVehicleTraversalSliceLapDebug(Entity vehicle)
            => m_RuntimeObs.ClearDebug(vehicle);

        private static bool IsStationDwellObservationKey(string value)
            => Capture.IsStationDwellKey(value);

        internal bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
            => m_Observation.TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames);

        private bool TryEstimateRemainingBoardingDwellFrames(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            uint nowFrame,
            out float remainingFrames)
            => m_Observation.TryEstimateRemainingBoardingDwellFrames(vehicle, line, waypoints, currentWaypointIndex, currentBypassBuilding, nowFrame, out remainingFrames);

        internal void BeginObservedDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
            => m_Observation.BeginObservedDwellSession(vehicle, line, waypointIndex, nowFrame);

        internal void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
            => m_Observation.TryRecordObservedStopDwellOnBoardingEnd(vehicle, line, fallbackWaypointIndex, nowFrame);

        private void ClearStationAnchorObservationDiagnosticsState()
            => m_Observation.ClearStationAnchorObservationDiagnosticsState();

        private uint ComputeAdjustedStopDwellDeadlineFrame(
            Entity line,
            int waypointIndex,
            uint dwellSinceFrame,
            int maxDwellMinutes)
            => m_Observation.ComputeAdjustedStopDwellDeadlineFrame(line, waypointIndex, dwellSinceFrame, maxDwellMinutes);

        protected override void OnDestroy()
        {
            if (ReferenceEquals(Instance, this)) Instance = null!;
            RuntimeRoot.Clear(this);
            if (m_UICache.IsCreated) m_UICache.Dispose();
            if (m_LastBoarding.IsCreated) m_LastBoarding.Dispose();
            if (m_CachedWpIdx.IsCreated) m_CachedWpIdx.Dispose();
            if (m_BVMisfire.IsCreated) m_BVMisfire.Dispose();
            if (m_BVMisfireStartFrame.IsCreated) m_BVMisfireStartFrame.Dispose();
            if (m_ForcedMidStopBoardingGraceUntil.IsCreated) m_ForcedMidStopBoardingGraceUntil.Dispose();
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
            if (m_LineSpawnRequestFrame.IsCreated) m_LineSpawnRequestFrame.Dispose();
            m_LineMileageModels.Clear();
            m_SharedLocalCorridorGraph = null;
            base.OnDestroy();
        }

        public string ObservationJson()
        {
            return m_ObsRecorder?.SnapshotJson() ?? string.Empty;
        }

        public void DumpObservation()
        {
            try
            {
                string json = ObservationJson();
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-runtime-observation-latest.json");
                File.WriteAllText(filePath, json);
                Mod.log.Info("[ObservationDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[ObservationDump] export failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal void SeedObservation(string selectedLineId)
        {
            m_ObsRecorder?.Seed(selectedLineId);
        }

        internal IReadOnlyDictionary<string, LinePlan> BuildObservationLines()
        {
            Dictionary<string, LinePlan> lines = new Dictionary<string, LinePlan>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, AppliedLine> entry in AppliedLines)
            {
                AppliedLine applied = entry.Value;
                if (applied == null)
                    continue;

                LinePlan line = new LinePlan
                {
                    Line = applied.LineEntity
                };

                if (applied.StagedRows != null)
                {
                    foreach (DispatchWorkbenchStagedRowDto row in applied.StagedRows)
                    {
                        if (row == null)
                            continue;

                        line.Rows.Add(new RowPlan
                        {
                            Id = row.id ?? string.Empty,
                            LineId = row.lineId ?? string.Empty,
                            Time = row.time ?? string.Empty,
                            Kind = row.kind ?? string.Empty,
                            Source = row.source ?? string.Empty
                        });
                    }
                }

                lines[entry.Key] = line;
            }

            return lines;
        }

        internal ContractDto[] BuildObservationContracts()
        {
            List<ContractDto> contracts = new List<ContractDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchPlannerImportContractDto> entry in Applied().Refs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract = entry.Value;
                if (contract?.plan == null)
                    continue;

                ChangeDto[] changedRows = (contract.plan.changedWindows ?? Array.Empty<DispatchPlannerChangedWindowDto>())
                    .SelectMany(window => window?.rowDiffs ?? Array.Empty<DispatchPlannerChangedRowDto>())
                    .Select(CopyChange)
                    .ToArray();
                contracts.Add(new ContractDto
                {
                    draftKey = entry.Key,
                    importedFrom = contract.importedFrom ?? string.Empty,
                    importedPlanId = contract.importedPlanId ?? contract.plan.planId ?? string.Empty,
                    importedObjectiveId = contract.importedObjectiveId ?? contract.plan.objectiveId ?? string.Empty,
                    importedLineIds = contract.importedLineIds ?? Array.Empty<string>(),
                    requestEcho = CopyEcho(contract.requestEcho),
                    lineRoleSummary = CopyRoleSummary(contract.plan.lineRoleSummary),
                    selectedBypassStationIds = contract.plan.selectedBypassStationIds ?? Array.Empty<string>(),
                    changedRows = changedRows,
                    structuredActions = (contract.plan.structuredScheduleActions ?? Array.Empty<DispatchPlannerScheduleActionDto>())
                        .Select(CopyAction)
                        .ToArray(),
                    riskItems = (contract.plan.riskItems ?? Array.Empty<DispatchPlannerRiskItemDto>())
                        .Select(CopyRisk)
                        .ToArray()
                });
            }

            return contracts.ToArray();
        }

        private static EchoDto CopyEcho(DispatchPlannerRequestEchoDto source)
        {
            if (source == null)
                return null;

            return new EchoDto
            {
                draftKey = source.draftKey,
                analysisWindowId = source.analysisWindowId,
                windowStart = source.windowStart,
                windowEnd = source.windowEnd,
                localLineIds = source.localLineIds,
                adjustableLineIds = source.adjustableLineIds,
                expressSourceMode = source.expressSourceMode,
                expressLineId = source.expressLineId,
                virtualExpressBaseLineId = source.virtualExpressBaseLineId,
                expressStopStationIds = source.expressStopStationIds,
                departureMode = source.departureMode,
                expressTripsPerHour = source.expressTripsPerHour,
                intervalMinutes = source.intervalMinutes,
                phaseTime = source.phaseTime,
                expressOffsetMinutes = source.expressOffsetMinutes,
                maxOffsetMinutes = source.maxOffsetMinutes,
                offsetStepMinutes = source.offsetStepMinutes,
                maxLocalRetimeMinutes = source.maxLocalRetimeMinutes,
                maxLocalWaitMinutes = source.maxLocalWaitMinutes,
                maxAdditionalBypassStations = source.maxAdditionalBypassStations,
                forcedBypassStationIds = source.forcedBypassStationIds
            };
        }

        private static RoleSummaryDto CopyRoleSummary(DispatchPlannerLineRoleSummaryDto source)
        {
            if (source == null)
                return null;

            return new RoleSummaryDto
            {
                effectiveLineIds = source.effectiveLineIds,
                adjustableLineIds = source.adjustableLineIds,
                fixedLineIds = source.fixedLineIds,
                targetLineIds = source.targetLineIds,
                autoFixedConstraintLineIds = source.autoFixedConstraintLineIds,
                suppressedFixedVsFixedClusterCount = source.suppressedFixedVsFixedClusterCount,
                roles = (source.roles ?? Array.Empty<DispatchPlannerLineRoleDto>())
                    .Select(CopyRole)
                    .ToArray()
            };
        }

        private static RoleDto CopyRole(DispatchPlannerLineRoleDto source)
        {
            if (source == null)
                return null;

            return new RoleDto
            {
                lineId = source.lineId,
                participates = source.participates,
                adjustable = source.adjustable,
                fixedLine = source.fixedLine,
                target = source.target
            };
        }

        private static ChangeDto CopyChange(DispatchPlannerChangedRowDto source)
        {
            if (source == null)
                return null;

            return new ChangeDto
            {
                tripId = source.tripId,
                lineId = source.lineId,
                kind = source.kind,
                beforeTime = source.beforeTime,
                afterTime = source.afterTime,
                scheduleShiftMinutes = source.scheduleShiftMinutes,
                predictedDelayMinutes = source.predictedDelayMinutes,
                totalDeltaMinutes = source.totalDeltaMinutes,
                changeType = source.changeType,
                statusCode = source.statusCode,
                statusMinutes = source.statusMinutes
            };
        }

        private static ActionDto CopyAction(DispatchPlannerScheduleActionDto source)
        {
            if (source == null)
                return null;

            return new ActionDto
            {
                actionType = source.actionType,
                type = source.type,
                shape = source.shape,
                reason = source.reason,
                targetRegionIds = source.targetRegionIds,
                reasonRegionIds = source.reasonRegionIds,
                clusterIds = source.clusterIds,
                reasonClusterIds = source.reasonClusterIds,
                stationIds = source.stationIds,
                affectedLineIds = source.affectedLineIds,
                affectedLineId = source.affectedLineId,
                affectedTripIds = source.affectedTripIds,
                priorityTripIds = source.priorityTripIds,
                tripIds = source.tripIds,
                deltaPattern = source.deltaPattern,
                deltaMinutes = source.deltaMinutes,
                deltaOffsetMinutes = source.deltaOffsetMinutes,
                riskScore = source.riskScore
            };
        }

        private static RiskDto CopyRisk(DispatchPlannerRiskItemDto source)
        {
            if (source == null)
                return null;

            return new RiskDto
            {
                riskId = source.riskId,
                problemType = source.problemType,
                resolutionState = source.resolutionState,
                pairRole = source.pairRole,
                treatmentType = source.treatmentType,
                blockReasonCode = source.blockReasonCode,
                suggestedOptionCodes = source.suggestedOptionCodes,
                yieldingLineId = source.yieldingLineId,
                priorityLineId = source.priorityLineId,
                yieldingTripId = source.yieldingTripId,
                priorityTripId = source.priorityTripId,
                yieldingDepartTime = source.yieldingDepartTime,
                priorityDepartTime = source.priorityDepartTime,
                fromStationId = source.fromStationId,
                toStationId = source.toStationId,
                catchupFromStationId = source.catchupFromStationId,
                catchupToStationId = source.catchupToStationId,
                catchupTime = source.catchupTime,
                selectedBypassStationId = source.selectedBypassStationId,
                requiredHoldMinutes = source.requiredHoldMinutes,
                plannedAdjustmentMinutes = source.plannedAdjustmentMinutes,
                holdBudgetMinutes = source.holdBudgetMinutes,
                unresolvedRiskMinutes = source.unresolvedRiskMinutes,
                robustnessRiskMinutes = source.robustnessRiskMinutes,
                requiredMarginMinutes = source.requiredMarginMinutes,
                currentWorstCaseGapMinutes = source.currentWorstCaseGapMinutes
            };
        }

        internal void BindObservationTarget(Entity line, Entity vehicle, int targetMinute, uint nowFrame, string reasonCode)
        {
            m_ObsRecorder?.TargetBound(line, vehicle, targetMinute, nowFrame, reasonCode);
        }

        internal void LaunchObservation(Entity line, Entity vehicle, int targetMinute, int actualMinute, uint launchFrame, bool lateDispatch)
        {
            m_ObsRecorder?.Launch(line, vehicle, targetMinute, actualMinute, launchFrame, lateDispatch);
        }

        internal void StopObservation(
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
            m_ObsRecorder?.Stop(vehicle, line, station, kind, waypointIndex, isOrigin, arrival, clockTime, frame);
        }

        private void HoldObservation(
            Entity vehicle,
            Entity blocker,
            Entity holdStation,
            int waypointIndex,
            uint nowFrame,
            string reasonCode)
        {
            m_ObsRecorder?.Hold(vehicle, blocker, holdStation, waypointIndex, nowFrame, reasonCode);
        }

        private void ReleaseObservation(Entity vehicle, Entity blocker, uint nowFrame, string releaseReason)
        {
            m_ObsRecorder?.Release(vehicle, blocker, nowFrame, releaseReason);
        }

        internal int ObservationTargetMin(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return -1;
            if (m_VehicleStateStore.CurrentSlot.IsCreated && m_VehicleView.TryGetSlot(vehicle, out int currentSlot))
                return currentSlot;
            if (m_VehicleStateStore.TargetMin.IsCreated && m_VehicleView.TryGetTarget(vehicle, out int targetMinute))
                return targetMinute;
            return -1;
        }

        public bool DisplayDebugFor(Entity entity, Entity prefab)
        {
            if (entity == Entity.Null) return false;
            if (m_VehicleView.Contains(entity)) return true;
            if (EntityManager.HasComponent<TransportLine>(entity) && EntityManager.HasComponent<RouteWaypoint>(entity)) return true;
            return false;
        }

        internal bool HasPreparingVehicleReachedOrigin(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            bool boarding,
            int currentWaypointIndex)
        {
            if (vehicle == Entity.Null || waypoints.Length == 0 || currentWaypointIndex != 0)
                return false;

            return boarding;
        }

        internal string BuildOriginHoldRetireReason(Entity line, int nowMin, int targetMin)
        {
            int waitMinutes = ScheduleClock.MinutesUntil(nowMin, targetMin);
            int holdLimitMinutes = m_LineView.Hold(line);
            return "下一班仍需等待" + waitMinutes + "分钟，超出候车窗口" + holdLimitMinutes + "分钟";
        }

        internal static string SlotStr(int min)
        {
            min = ((min % 1440) + 1440) % 1440;
            int h = min / 60 % 24;
            int m = min % 60;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
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

            float lineDurationFrames = m_LapCache.Read(line);
            if (lineDurationFrames <= 0f)
                return float.MaxValue;

            int hopCount = (targetWaypointIndex - fromWaypointIndex + waypoints.Length) % waypoints.Length;
            if (hopCount <= 0)
                hopCount = waypoints.Length;

            return lineDurationFrames * (hopCount / (float)waypoints.Length);
        }

        internal float ComputeDepartureToWaypointFramesFromProfile(
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
                if (m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
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

        internal VehicleState InferInitialVehicleState(
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
                float originDistAtBoarding = m_LineProfile.DistanceToOrigin(v, wps);
                if (originDistAtBoarding <= ORIGIN_FORCE_IDLE_RADIUS_METERS)
                {
                    if (!m_RouteProgress.Try(v, out int nearOriginWp, out float nearOriginSeg)
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

            if (m_RouteProgress.Try(v, out int nextWaypointIndex, out float segmentPosition))
            {
                float nearOriginDist = m_LineProfile.DistanceToOrigin(v, wps);
                bool nearOriginProgress = nextWaypointIndex == 0 || (nextWaypointIndex == 1 && segmentPosition <= 0.05f);
                if (nearOriginProgress && nearOriginDist <= ORIGIN_FORCE_IDLE_RADIUS_METERS
                    && (boarding0 || arriving0))
                {
                    reason = "route-progress-origin-fallback wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                    return VehicleState.Holding;
                }
                reason = "route-progress wp=" + nextWaypointIndex + " seg=" + segmentPosition.ToString("F2");
                return (boarding0 && nextWaypointIndex == 0) ? VehicleState.Holding : VehicleState.Running;
            }

            float originDist = m_LineProfile.DistanceToOrigin(v, wps);
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

        internal static ulong MixLineSignature(ulong hash, int value)
        {
            return (hash ^ (uint)value) * 1099511628211UL;
        }

        internal ulong ComputeLineWaypointSignature(DynamicBuffer<RouteWaypoint> wps)
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
            foreach (KeyValuePair<string, AppliedLine> entry in AppliedLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !EntityManager.HasBuffer<RouteSegment>(line)
                    || !m_LineView.Local(line))
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

            foreach (KeyValuePair<string, AppliedLine> entry in AppliedLines)
            {
                Entity line = entry.Value.LineEntity;
                if (line == Entity.Null
                    || !EntityManager.Exists(line)
                    || !EntityManager.HasBuffer<RouteWaypoint>(line)
                    || !EntityManager.HasBuffer<RouteSegment>(line)
                    || !m_LineView.Local(line))
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

        internal bool TryBuildLineDistanceModel(Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineMileageModel model) => TryGetLineMileageModel(line, waypoints, out model);

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
                if (!node.IsStopNode || node.Building == Entity.Null || !IsBypassStationSetting(node.Building))
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

        internal float ReadRouteSegmentDistanceMeters(Entity segmentEntity, DynamicBuffer<RouteWaypoint> waypoints, int segmentIndex)
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

        internal bool TryProjectVehicleOntoLine(Entity vehicle, Entity line, DynamicBuffer<RouteWaypoint> waypoints, out LineDistanceProjection projection)
        {
            projection = default;
            if (!TryGetLineMileageModel(line, waypoints, out LineMileageModel model)
                || model.TotalDistanceMeters <= 0f
                || model.WaypointDistances.Length != waypoints.Length)
            {
                return false;
            }

            if (!m_RouteProgress.Try(vehicle, out int nextWaypointIndex, out float segmentPosition))
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

        internal static float ForwardDistanceOnLoop(float totalDistanceMeters, float fromMeters, float toMeters)
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

        internal bool IsLineStable(Entity line, DynamicBuffer<RouteWaypoint> wps)
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

        internal void ClearLineTimeProfiles()
        {
            if (m_LineTimeProfiles.IsCreated) m_LineTimeProfiles.Clear();
            if (m_LineTimeProfileSegmentFrames.IsCreated) m_LineTimeProfileSegmentFrames.Clear();
            if (m_LineTimeProfileStopFrames.IsCreated) m_LineTimeProfileStopFrames.Clear();
        }

        internal bool TryGetLineTimeProfile(Entity line, DynamicBuffer<RouteWaypoint> wps, out LineTimeProfileHeader profile)
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
            if (waypoint == Entity.Null
                || !EntityManager.Exists(waypoint)
                || !EntityManager.HasComponent<VehicleTiming>(waypoint))
                return 0f;

            float stopDuration = prefabLineData.m_StopDuration;
            if (EntityManager.HasComponent<Connected>(waypoint))
            {
                Entity connectedStop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connectedStop != Entity.Null
                    && EntityManager.Exists(connectedStop)
                    && EntityManager.HasComponent<Game.Routes.TransportStop>(connectedStop))
                    stopDuration = RouteUtils.GetStopDuration(prefabLineData, EntityManager.GetComponentData<Game.Routes.TransportStop>(connectedStop));
            }
            return math.max(0f, stopDuration * 60f);
        }

        internal float GetProfileWaypointStopFrames(
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
            if (!m_Observation.TryGetObservedWaypointStopFrames(line, waypointIndex, out dwellFrames))
            {
                int maxStationDwellMinutes = m_LineView.Dwell(line);
                if (maxStationDwellMinutes > 0)
                    dwellFrames = maxStationDwellMinutes * (float)SIM_FRAMES_PER_MINUTE;
                else
                    dwellFrames = configuredStopFrames;
            }

            return math.max(0f, dwellFrames + PROFILE_STOP_START_BUFFER_MINUTES * (float)SIM_FRAMES_PER_MINUTE);
        }

        private float ResolveProfileScale(Entity v, float baseLoopFrames)
        {
            if (baseLoopFrames <= 0f) return 1f;
            if (!m_ObsQuery.TryLapFrames(v, out uint observedLoopFrames) || observedLoopFrames == 0)
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

        internal float EstimatePreparingArrivalFrames(
            Entity v,
            Entity line,
            DynamicBuffer<RouteWaypoint> wps,
            uint nowFrame,
            float lineDurationFrames)
        {
            if (!m_VehicleStateStore.PreparingStartFrame.ContainsKey(v))
                return float.MaxValue;
            float cachedFrames = m_DispatchCache.Read(line);
            if (cachedFrames <= 0f)
                cachedFrames = EstimateDispatchFallbackFrames(v, line, lineDurationFrames);
            if (cachedFrames <= 0f)
                return float.MaxValue;

            if (wps.Length > 0
                && EntityManager.HasComponent<Target>(v)
                && EntityManager.GetComponentData<Target>(v).m_Target == wps[0].m_Waypoint
                && m_RouteProgress.Try(v, out int nextWaypointIndex, out float segmentPosition)
                && nextWaypointIndex == 0)
            {
                return math.max(0f, cachedFrames * (1f - math.saturate(segmentPosition)));
            }

            return cachedFrames;
        }

        internal float EstimateRunningArrivalFrames(
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

                if (m_RouteProgress.Try(v, out int nextWaypointIndex, out float segmentPosition))
                    return ComputeRemainingFramesFromProfile(profile, nextWaypointIndex, segmentPosition) * scale;

                int cachedWpIdx = m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                float cachedWaypointEstimate = ComputeRemainingFramesFromCachedWaypoint(profile, cachedWpIdx);
                if (cachedWaypointEstimate != float.MaxValue)
                    return cachedWaypointEstimate * scale;
            }

            float lapFrames = m_ObsQuery.TryLapFrames(v, out uint vehicleLapFrames) && vehicleLapFrames > 0
                ? vehicleLapFrames
                : 0f;
            if (lapFrames <= 0f && lineHasHistory && lineDurationFrames > 0f)
                lapFrames = lineDurationFrames;

            if (lapFrames <= 0f)
                return float.MaxValue;

            if (m_ObsQuery.TryLapStartFrame(v, out uint lapStartFrame))
                return math.max(0f, lapFrames - (float)(nowFrame - lapStartFrame));

            return float.MaxValue;
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
            if (stationA == Entity.Null
                || !EntityManager.Exists(stationA)
                || !EntityManager.HasComponent<Connected>(stationA))
                return Entity.Null;
            Entity connected = EntityManager.GetComponentData<Connected>(stationA).m_Connected;
            return connected != Entity.Null && EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
        }

    }
}
