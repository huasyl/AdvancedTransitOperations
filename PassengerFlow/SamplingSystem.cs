using Game;
using Game.Creatures;
using Game.Citizens;
using Game.Prefabs;
using Game.Routes;
using Game.Serialization;
using Game.SceneFlow;
using Game.Simulation;
using Game.Vehicles;
using PassengerFlowJobs = RapidTransitMod.PassengerFlow.Jobs;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.TrackModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed partial class SamplingSystem : GameSystemBase, IPreSerialize
    {
        internal const int BucketsPerDay = 96;
        internal const int RetainedBucketCapacity = BucketsPerDay * 2;
        internal const uint DepartureSampleDelayFrames = 30;
        internal const uint RepresentativeIntervalFrames = 60;
        internal const uint SlowRepresentativeIntervalFrames = 180;
        internal const uint StopScanIntervalFrames = 16;
        internal const uint PendingTransferCleanupIntervalFrames = 60;
        internal const int SameModeTransferWindowMinutes = 90;
        internal const int MaxDueSamplesPerTick = 32;
        internal const int MaxStopSamplesPerTick = 32;
        internal const int MaxPendingTransfers = 20000;
        private static SamplingSystem s_Current;
        internal static State CurrentState { get; private set; }
        private readonly Dictionary<Entity, LineSampleMetadata> m_LineMetadata = new Dictionary<Entity, LineSampleMetadata>();
        private readonly HashSet<Entity> m_AppliedLineEntities = new HashSet<Entity>();
        private bool m_AppliedLinesDirty;
        private readonly List<PendingSample> m_ReadySamples = new List<PendingSample>(MaxDueSamplesPerTick);
        private readonly List<OpenStop> m_StopBatch = new List<OpenStop>(MaxStopSamplesPerTick);
        private readonly List<(Entity Passenger, PassengerFlowTransferSample Sample, StopPlanRaw Plan, bool SameStop)> m_NewRepresentatives =
            new List<(Entity, PassengerFlowTransferSample, StopPlanRaw, bool)>();
        private readonly List<(Entity Passenger, OpenStop Stop)> m_StopPlanContexts =
            new List<(Entity, OpenStop)>();
        private readonly List<StopPlanRequest> m_StopPlanRequests = new List<StopPlanRequest>();
        private readonly List<Entity> m_NextBaseline = new List<Entity>();
        private readonly Dictionary<Entity, PurposeInfo>[] m_PurposeByRequest =
            new Dictionary<Entity, PurposeInfo>[MaxDueSamplesPerTick];
        private readonly bool[] m_OkRequests = new bool[MaxDueSamplesPerTick];
        private EntityQuery m_RepresentativeQuery;
        private EntityQuery m_FastRepresentativeQuery;
        private ComponentLookup<CurrentVehicle> m_CurrentVehicles;
        private ComponentLookup<Controller> m_Controllers;
        private ComponentLookup<GroupMember> m_GroupMembers;
        private ComponentLookup<Game.Pathfind.PathOwner> m_PathOwners;
        private BufferLookup<Game.Pathfind.PathElement> m_Paths;
        private ComponentLookup<HumanCurrentLane> m_Lanes;
        private ComponentLookup<Connected> m_Connections;
        private WaitingPassengersSystem m_WaitingPassengersSystem;
        private UpdateSystem m_UpdateSystem;
        private SaveGameSystem m_SaveGameSystem;
        private SimulationSystem m_SimulationSystem;
        private bool m_SaveRequested;
        private bool m_SaveDeferred;
        private Task<Persistence.PreparedSave> m_SavePreparation;
        private Persistence.PreparedSave m_PreparedSave;
        private State m_SaveState;
        private Entity m_SaveCity;
        private uint m_SaveFrame;
        private ulong m_ConsumedTraversalVersion;
        private readonly List<PublishedTraversalSnapshot> m_TraversalChanges = new List<PublishedTraversalSnapshot>(128);
        private uint m_LastLoadLogFrame;
        private uint m_MaxStopPlanDelay;
        private uint m_MaxSampleDelay;
        private int m_MaxSampleGroups;
        private int m_MaxRepresentatives;
        private int m_MaxOpenStops;
        private int m_MaxPendingSamples;

        private readonly struct LineSampleMetadata
        {
            public readonly TransitMode Mode;
            public readonly string LineId;
            public readonly bool Supported;

            public LineSampleMetadata(TransitMode mode, string lineId, bool supported)
            {
                Mode = mode;
                LineId = lineId ?? string.Empty;
                Supported = supported;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            s_Current = this;
            CurrentState = new State();
            m_WaitingPassengersSystem = World.GetOrCreateSystemManaged<WaitingPassengersSystem>();
            m_UpdateSystem = World.GetOrCreateSystemManaged<UpdateSystem>();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<SaveGameSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            GameManager.instance.onGameSaveLoad += OnSaveGame;
            m_RepresentativeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PassengerFlowTransferSample>() },
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            });
            m_FastRepresentativeQuery = GetEntityQuery(ComponentType.ReadOnly<PassengerFlowTransferSample>());
            m_CurrentVehicles = GetComponentLookup<CurrentVehicle>(true);
            m_Controllers = GetComponentLookup<Controller>(true);
            m_GroupMembers = GetComponentLookup<GroupMember>(true);
            m_PathOwners = GetComponentLookup<Game.Pathfind.PathOwner>(true);
            m_Paths = GetBufferLookup<Game.Pathfind.PathElement>(true);
            m_Lanes = GetComponentLookup<HumanCurrentLane>(true);
            m_Connections = GetComponentLookup<Connected>(true);
            for (int i = 0; i < m_PurposeByRequest.Length; i++)
                m_PurposeByRequest[i] = new Dictionary<Entity, PurposeInfo>();
        }

        protected override void OnUpdate()
        {
            if (m_UpdateSystem.currentPhase == SystemUpdatePhase.MainLoop)
            {
                UpdateSavePreparation();
                return;
            }
            Port port = Runtime.Current;
            State state = CurrentState;
            if (port == null || state == null)
                return;

            uint frame = port.Frame();
            ClockSnapshot clock = port.Clock();
            UpdateBucketIfNeeded(state, frame, clock);
            RunWaitingSample(port, state, frame, clock);
            if (state.RuntimeRestorePending)
            {
                if (!port.IsReady)
                    return;
                Persistence.ResumeRuntime(EntityManager, state, frame);
            }
            if (port.IsReady && !state.LoadsInitialized)
            {
                RefreshAppliedLines(port, state, frame, clock);
                using NativeArray<Entity> vehicles = port.VehicleViews(Allocator.Temp);
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (port.TryLine(vehicle, out Entity line) && port.TryState(vehicle, out VehicleState vehicleState))
                        port.RegisterVehicle(vehicle, line, vehicleState, frame);
                }
                state.UpdateAppliedLines(m_AppliedLineEntities, frame, clock);
                state.StartNetworkLoads(frame);
                state.LoadsInitialized = true;
            }
            else if (port.IsReady && m_AppliedLinesDirty)
                RefreshAppliedLines(port, state, frame, clock);
            if (state.LastTimeFlushFrame == 0 || frame - state.LastTimeFlushFrame >= StopScanIntervalFrames)
            {
                state.CloseAllLines(frame, clock);
                state.LastTimeFlushFrame = frame;
            }
            ExpirePendingSamples(port, state, frame, clock);
            RunStopSample(port, state, frame, clock);
            RunInitialLoads(port, state, frame, clock);
            RunRepresentatives(port, state, frame);
            RunPendingCleanup(state, frame);

            UpdateLoad(port, state, frame);
        }

        protected override void OnDestroy()
        {
            GameManager.instance.onGameSaveLoad -= OnSaveGame;
            ClearSavePreparation();
            CurrentState?.Clear();
            ClearLineMetadata();
            CurrentState = null;
            if (ReferenceEquals(s_Current, this))
                s_Current = null;
            base.OnDestroy();
        }

        internal static void ClearState()
        {
            s_Current?.ClearSavePreparation();
            s_Current?.CancelRepresentativesInternal(Entity.Null, null);
            CurrentState?.Clear();
            s_Current?.ClearLineMetadata();
            s_Current?.ClearLoadDiagnostics();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            ClearSavePreparation();
            base.OnGamePreload(purpose, mode);
        }

        private void OnSaveGame(string saveName, string previewUri, bool start, bool success)
        {
            ClearSavePreparation();
            m_SaveRequested = start;
        }

        private void ReleaseSave()
        {
            if (!m_SaveDeferred)
                return;
            m_SaveDeferred = false;
            m_SaveGameSystem.Enabled = true;
        }

        private void ClearSavePreparation()
        {
            m_SaveRequested = false;
            if (m_SavePreparation != null)
            {
                // 放弃结果时只观察异常，不等待，也不向游戏状态写回。
                m_SavePreparation.ContinueWith(task => { _ = task.Exception; },
                    System.Threading.CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                m_SavePreparation = null;
            }
            m_PreparedSave = null;
            m_SaveState = null;
            m_SaveCity = Entity.Null;
            ReleaseSave();
        }

        private bool SaveSnapshotIsValid()
        {
            ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
            Port port = Runtime.Current;
            return GameManager.instance.gameMode == GameMode.Game
                && !GameManager.instance.isGameLoading
                && m_SimulationSystem.selectedSpeed == 0f
                && m_SimulationSystem.frameIndex == m_SaveFrame
                && ReferenceEquals(CurrentState, m_SaveState)
                && port != null && port.IsReady
                && runtime?.m_CitySystem != null
                && runtime.m_CitySystem.City == m_SaveCity
                && EntityManager.Exists(m_SaveCity);
        }

        private void UpdateSavePreparation()
        {
            try
            {
                if (m_SavePreparation != null)
                {
                    if (!SaveSnapshotIsValid())
                    {
                        ClearSavePreparation();
                        Mod.log.Info("[PassengerFlowPersistence] Background preparation fallback -> snapshot invalid");
                        return;
                    }
                    if (!m_SavePreparation.IsCompleted)
                        return;
                    // 已完成才取结果；失败会进入同步回退，不阻塞主循环。
                    m_PreparedSave = m_SavePreparation.GetAwaiter().GetResult();
                    m_SavePreparation = null;
                    ReleaseSave();
                    return;
                }
                if (!m_SaveRequested || !m_SaveGameSystem.Enabled)
                    return;

                // 启用的首次保存只消费一次请求，写入阶段不再暂缓。
                m_SaveRequested = false;
                ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
                Port port = Runtime.Current;
                State state = CurrentState;
                if (GameManager.instance.gameMode != GameMode.Game || GameManager.instance.isGameLoading
                    || m_SimulationSystem.selectedSpeed != 0f || port == null || !port.IsReady
                    || state == null || runtime?.m_CitySystem == null)
                    return;
                Entity city = runtime.m_CitySystem.City;
                if (city == Entity.Null || !EntityManager.Exists(city))
                    return;

                m_SaveState = state;
                m_SaveCity = city;
                m_SaveFrame = m_SimulationSystem.frameIndex;
                m_SaveGameSystem.Enabled = false;
                m_SaveDeferred = true;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                SealForSave(port, state);
                long capturedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                PassengerFlowPersistentState snapshot = Persistence.Capture();
                long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                double tickMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
                double captureMilliseconds = (finished - capturedAt) * tickMs;
                m_SavePreparation = Task.Run(() => Persistence.Prepare(snapshot, captureMilliseconds));
                Diagnostics.Log("PassengerFlowSaveTiming", "scope=mainLoopPreparation"
                    + " sealMainMs=" + ((capturedAt - started) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " captureMainMs=" + captureMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ClearSavePreparation();
                Mod.log.Info("[PassengerFlowPersistence] Background preparation fallback -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SealForSave(Port port, State state)
        {
            if (port == null || state == null)
                return;
            ClockSnapshot clock = port.Clock();
            uint frame = port.Frame();
            UpdateBucketIfNeeded(state, frame, clock);
            state.CloseAllLines(frame, clock);
            FlushWaitingCoverage(state, clock, frame, WaitingInterval());
        }

        internal static void AppliedLinesChanged()
        {
            if (s_Current != null)
                s_Current.m_AppliedLinesDirty = true;
        }

        internal static void CancelRepresentatives(Entity vehicle, uint? openFrame)
            => s_Current?.CancelRepresentativesInternal(vehicle, openFrame);

        private void CancelRepresentativesInternal(Entity vehicle, uint? openFrame)
        {
            if (m_RepresentativeQuery.IsEmptyIgnoreFilter)
                return;
            using NativeArray<Entity> passengers = m_RepresentativeQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < passengers.Length; i++)
            {
                Entity passenger = passengers[i];
                PassengerFlowTransferSample sample = EntityManager.GetComponentData<PassengerFlowTransferSample>(passenger);
                if (vehicle != Entity.Null && sample.m_OldVehicle != vehicle)
                    continue;
                if (openFrame.HasValue && sample.m_OpenFrame != openFrame.Value)
                    continue;
                EntityManager.RemoveComponent<PassengerFlowTransferSample>(passenger);
            }
        }

        public void PreSerialize(Colossal.Serialization.Entities.Context context)
        {
            ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
            if (runtime == null || runtime.m_CitySystem == null)
            {
                ClearSavePreparation();
                return;
            }

            bool timing = Diagnostics.Enabled;
            long started = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            long sealedAt = 0;
            int gc0 = timing ? GC.CollectionCount(0) : 0;
            int gc1 = timing ? GC.CollectionCount(1) : 0;
            int gc2 = timing ? GC.CollectionCount(2) : 0;
            string result = "completed";
            try
            {
                Persistence.PreparedSave prepared = m_PreparedSave;
                if (prepared != null && !SaveSnapshotIsValid())
                {
                    prepared = null;
                    Mod.log.Info("[PassengerFlowPersistence] Background preparation fallback -> snapshot invalid at serialize");
                }
                ClearSavePreparation();
                if (prepared == null)
                    SealForSave(Runtime.Current, CurrentState);
                sealedAt = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                Persistence.SaveToCity(EntityManager, runtime.m_CitySystem.City, prepared);
            }
            catch (Exception ex)
            {
                result = "failed";
                Mod.log.Info("[PassengerFlowPersistence] Save failed -> " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (timing)
                {
                    long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                    double tickMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
                    long sealEnd = sealedAt != 0 ? sealedAt : finished;
                    Diagnostics.Log("PassengerFlowSaveTiming",
                        "result=" + result + " scope=preSerialize"
                        + " totalMs=" + ((finished - started) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                        + " sealMs=" + ((sealEnd - started) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                        + " persistMs=" + ((finished - sealEnd) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                        + " gc0=" + (GC.CollectionCount(0) - gc0)
                        + " gc1=" + (GC.CollectionCount(1) - gc1)
                        + " gc2=" + (GC.CollectionCount(2) - gc2));
                }
            }
        }

        internal static bool SupportsMode(TransitMode mode)
            => mode == TransitMode.Train || mode == TransitMode.Subway || mode == TransitMode.Tram || mode == TransitMode.Bus;

        private void ClearLineMetadata()
        {
            m_LineMetadata.Clear();
        }

        private void ClearLoadDiagnostics()
        {
            m_LastLoadLogFrame = 0;
            m_MaxStopPlanDelay = 0;
            m_MaxSampleDelay = 0;
            m_MaxSampleGroups = 0;
            m_MaxRepresentatives = 0;
            m_MaxOpenStops = 0;
            m_MaxPendingSamples = 0;
        }

        private void RecordStopPlanDelay(OpenStop stop, uint frame)
        {
            if (RtLog.VerboseEnabled && frame - stop.OpenFrame > m_MaxStopPlanDelay)
                m_MaxStopPlanDelay = frame - stop.OpenFrame;
        }

        private void RecordSampleDelay(uint delay)
        {
            if (RtLog.VerboseEnabled && delay > m_MaxSampleDelay)
                m_MaxSampleDelay = delay;
        }

        private void UpdateLoad(Port port, State state, uint frame)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (state.OpenStops.Count > m_MaxOpenStops)
                m_MaxOpenStops = state.OpenStops.Count;
            if (state.PendingSamples.Count > m_MaxPendingSamples)
                m_MaxPendingSamples = state.PendingSamples.Count;
            if (state.Aggregates.SampleGroupCount > m_MaxSampleGroups)
                m_MaxSampleGroups = state.Aggregates.SampleGroupCount;

            if (m_LastLoadLogFrame == 0)
            {
                m_LastLoadLogFrame = frame;
                return;
            }
            if (frame - m_LastLoadLogFrame < 1800u)
                return;

            port.Log("[PassengerFlowLoad] stopPlanDelay=" + m_MaxStopPlanDelay
                + " sampleDelay=" + m_MaxSampleDelay
                + " openStopsPeak=" + m_MaxOpenStops
                + " pendingSamplesPeak=" + m_MaxPendingSamples
                + " sampleGroupsPeak=" + m_MaxSampleGroups
                + " representativesPeak=" + m_MaxRepresentatives);
            m_LastLoadLogFrame = frame;
            m_MaxStopPlanDelay = 0;
            m_MaxSampleDelay = 0;
            m_MaxOpenStops = 0;
            m_MaxPendingSamples = 0;
            m_MaxSampleGroups = 0;
            m_MaxRepresentatives = 0;
        }

        private LineSampleMetadata GetLineMetadata(Port port, Entity line)
        {
            if (port == null || !port.LineExists(line))
            {
                m_LineMetadata.Remove(line);
                return new LineSampleMetadata(TransitMode.Unknown, string.Empty, false);
            }

            if (m_LineMetadata.TryGetValue(line, out LineSampleMetadata metadata))
                return metadata;

            if (!port.TryLineMetadata(line, out TransitMode mode, out string lineId))
            {
                m_LineMetadata.Remove(line);
                return new LineSampleMetadata(TransitMode.Unknown, string.Empty, false);
            }

            metadata = new LineSampleMetadata(
                mode,
                lineId,
                SupportsMode(mode));
            m_LineMetadata[line] = metadata;
            return metadata;
        }

        internal static void ClockChanged(Port port, ClockSnapshot oldClock, ClockSnapshot newClock)
        {
            State state = CurrentState;
            if (port == null || state == null)
                return;

            state.CloseAllLines(port.Frame(), oldClock);
            FlushWaitingCoverage(state, oldClock, port.Frame(), s_Current.WaitingInterval());
            UpdateBucketIfNeeded(state, port.Frame(), newClock);
        }

        private void RunWaitingSample(Port port, State state, uint frame, ClockSnapshot clock)
        {
            if (state.LastWaitingSampleFrame == 0)
            {
                state.LastWaitingSampleFrame = frame;
                state.NextWaitingSampleFrame = frame + (uint)WaitingInterval();
                return;
            }

            if (frame < state.NextWaitingSampleFrame)
                return;

            int waitingInterval = WaitingInterval();
            EntityManager.CompleteDependencyBeforeRO<WaitingPassengers>();
            ConsumeTraversalChanges(port, state);
            RefreshAppliedLines(port, state, frame, clock);
            FlushWaitingCoverage(state, clock, frame, waitingInterval);
            foreach (KeyValuePair<string, AppliedLine> entry in port.AppliedLines)
            {
                AppliedLine applied = entry.Value;
                Entity line = applied.LineEntity;
                if (!port.TryLineMetadata(line, out TransitMode mode, out string lineId)
                    || !SupportsMode(mode))
                {
                    state.RemoveWaitingLine(line);
                    continue;
                }

                if (!state.Anchors.TryWaitingDirectory(port, line, out WaitingStop[] stops))
                {
                    state.RemoveWaitingLine(line);
                    continue;
                }
                for (int i = 0; i < stops.Length; i++)
                {
                    RouteStopRef stop = stops[i].Stop;
                    int stationIndex = stops[i].StationIndex;
                    Entity waypoint = stop.Waypoint;
                    StationWaitingSource source = new StationWaitingSource(line, waypoint);
                    if (stationIndex < 0
                        || !EntityManager.HasComponent<WaitingPassengers>(waypoint))
                    {
                        state.WaitingBaselines.Remove(source);
                        continue;
                    }

                    WaitingPassengers waiting = EntityManager.GetComponentData<WaitingPassengers>(waypoint);
                    // 原版 m_AverageWaitingTime 的单位是仿真秒，不是游戏秒或游戏分钟。
                    // 原版仿真秒先乘 60 换成模拟帧；内部和存档保留帧数。
                    // 这里的 60 是原版仿真帧/秒；不能直接除 60，也不能用现实帧率替代。
                    // 依据：CreatureUtils.QUEUE_TICKS_TO_SECONDS、RouteUtils.CalculateDepartureFrame。
                    double estimatedWaitFrames = waiting.m_AverageWaitingTime * 60d;
                    TimeBucketKey bucket = BucketFor(clock.DayIndex, clock.NowMinute);
                    state.Aggregates.RecordWaiting(
                        mode, lineId, stop.StationOccurrence, stationIndex, bucket,
                        waiting.m_Count, estimatedWaitFrames);

                    if (!state.WaitingBaselines.TryGetValue(source, out WaitingBaseline baseline))
                    {
                        state.WaitingBaselines[source] = new WaitingBaseline(
                            mode, lineId, stop.StationOccurrence, stationIndex,
                            waiting.m_Count, estimatedWaitFrames, frame);
                    }
                    else
                    {
                        baseline.Mode = mode;
                        baseline.LineId = lineId;
                        baseline.StationSakIndex = stationIndex;
                        baseline.StationOccurrence = stop.StationOccurrence;
                        baseline.WaitingCount = waiting.m_Count;
                        baseline.EstimatedWaitFrames = estimatedWaitFrames;
                        baseline.Observe(frame);
                    }
                }
            }

            state.LastWaitingSampleFrame = frame;
            state.NextWaitingSampleFrame = frame + (uint)waitingInterval;
        }

        private void ConsumeTraversalChanges(Port port, State state)
        {
            ulong version = port.PublishedTraversalVersion;
            if (version == m_ConsumedTraversalVersion)
                return;
            if (version < m_ConsumedTraversalVersion)
            {
                state.Anchors.ClearRouteCaches();
                m_ConsumedTraversalVersion = 0;
            }
            while (m_ConsumedTraversalVersion < version)
            {
                int count = port.CopyTraversalChanges(m_ConsumedTraversalVersion, 128,
                    m_TraversalChanges, out bool historyGap);
                if (historyGap)
                {
                    state.Anchors.ClearRouteCaches();
                    m_ConsumedTraversalVersion = version;
                    break;
                }
                if (count == 0)
                    break;
                for (int i = 0; i < count; i++)
                {
                    PublishedTraversalSnapshot change = m_TraversalChanges[i];
                    state.Anchors.InvalidateLine(change.Line);
                    m_ConsumedTraversalVersion = change.PublishVersion;
                }
            }
            m_TraversalChanges.Clear();
        }

        private void RefreshAppliedLines(Port port, State state, uint frame, ClockSnapshot clock)
        {
            if (!port.IsReady)
                return;
            m_AppliedLinesDirty = false;
            m_AppliedLineEntities.Clear();
            foreach (AppliedLine applied in port.AppliedLines.Values)
            {
                Entity line = applied.LineEntity;
                m_AppliedLineEntities.Add(line);
                if (port.TryLineMetadata(line, out TransitMode mode, out string lineId)
                    && SupportsMode(mode))
                    state.EnsureAppliedLine(line, mode, lineId, frame, clock);
            }
            state.UpdateAppliedLines(m_AppliedLineEntities, frame, clock);
        }

        private int WaitingInterval()
            => m_WaitingPassengersSystem
                .GetUpdateInterval(SystemUpdatePhase.GameSimulation);

        private static void FlushWaitingCoverage(
            State state, ClockSnapshot clock, uint frame, int waitingInterval)
        {
            foreach (WaitingBaseline baseline in state.WaitingBaselines.Values)
                AccumulateWaiting(state, baseline, frame, clock, waitingInterval);
        }

        internal static void CloseWaitingLine(State state, Entity line, uint frame, ClockSnapshot clock)
        {
            FlushWaitingLine(state, line, frame, clock, s_Current.WaitingInterval());
            state.RemoveWaitingLine(line);
        }

        private static void FlushWaitingLine(State state, Entity line, uint frame,
            ClockSnapshot clock, int waitingInterval)
        {
            foreach (KeyValuePair<StationWaitingSource, WaitingBaseline> pair in state.WaitingBaselines)
                if (pair.Key.Line == line)
                    AccumulateWaiting(state, pair.Value, frame, clock, waitingInterval);
        }

        private static void AccumulateWaiting(
            State state, WaitingBaseline baseline, uint frame, ClockSnapshot clock, int waitingInterval)
        {
            uint endFrame = Math.Min(frame, baseline.ObservedFrame + (uint)waitingInterval);
            if (endFrame <= baseline.AccumulatedThroughFrame)
                return;

            uint remaining = endFrame - baseline.AccumulatedThroughFrame;
            double cursorMinute = clock.DayIndex * 1440d
                + clock.NowMinuteExact - (frame - endFrame) / clock.FramesPerMinute;
            while (remaining > 0)
            {
                int absoluteBucket = (int)Math.Floor((cursorMinute - 1e-6) / Snapshot.BucketMinutes);
                double bucketStart = absoluteBucket * (double)Snapshot.BucketMinutes;
                uint framesInBucket = (uint)Math.Max(1d,
                    Math.Ceiling((cursorMinute - bucketStart) * clock.FramesPerMinute));
                uint covered = Math.Min(remaining, framesInBucket);
                state.Aggregates.AccumulateWaiting(
                    baseline.Mode, baseline.LineId, baseline.StationOccurrence, baseline.StationSakIndex,
                    BucketFromAbsoluteIndex(absoluteBucket), baseline.WaitingCount,
                    baseline.EstimatedWaitFrames, covered);
                remaining -= covered;
                cursorMinute -= covered / clock.FramesPerMinute;
            }
            baseline.AccumulatedThroughFrame = endFrame;
        }
        private static void UpdateBucketIfNeeded(State state, uint frame, ClockSnapshot clock)
        {
            int dayIndex = clock.DayIndex;
            int nowMinute = clock.NowMinute;
            if (state.DayIndex == dayIndex
                && state.LastBucketUpdateMinute == nowMinute
                && state.LastMinute == nowMinute)
                return;

            if (state.LastMinute >= 0 && dayIndex > state.DayIndex
                && state.LoadsInitialized && !state.RuntimeRestorePending)
                state.CloseAllLines(frame, clock);
            state.DayIndex = dayIndex;
            state.LastMinute = nowMinute;
            state.LastBucketUpdateMinute = nowMinute;
            state.CurrentBucket = BucketFor(dayIndex, nowMinute);
            state.CurrentAbsoluteBucketIndex = AbsoluteBucketIndex(state.CurrentBucket);
            TrimRetainedDays(state);
        }

        internal static int RetainedFromAbsolute(int currentDayIndex)
            => Math.Max(0, currentDayIndex - 1) * BucketsPerDay;

        internal static void TrimRetainedDays(State state)
        {
            TimeBucketKey minimum = BucketFromAbsoluteIndex(RetainedFromAbsolute(state.DayIndex));
            state.Aggregates.TrimBefore(minimum.DayIndex, minimum.BucketStartMinute);
        }

        internal static int AbsoluteBucketIndex(TimeBucketKey bucket)
            => bucket.DayIndex * BucketsPerDay + bucket.BucketStartMinute / Snapshot.BucketMinutes;

        internal static TimeBucketKey BucketFromAbsoluteIndex(int absoluteBucketIndex)
            => new TimeBucketKey(absoluteBucketIndex / BucketsPerDay,
                (absoluteBucketIndex % BucketsPerDay) * Snapshot.BucketMinutes);

        internal static TimeBucketKey BucketFor(int dayIndex, int nowMinute)
            => new TimeBucketKey(dayIndex,
                (nowMinute / Snapshot.BucketMinutes) * Snapshot.BucketMinutes);

        private void ExpirePendingSamples(Port port, State state, uint frame, ClockSnapshot clock)
        {
            m_ReadySamples.Clear();
            while (state.PendingSamples.Count > 0
                && state.PendingSamples.Peek().SampleFrame <= frame
                && m_ReadySamples.Count < MaxDueSamplesPerTick)
            {
                PendingSample sample = state.PendingSamples.Dequeue();
                RecordSampleDelay(frame - sample.SampleFrame);
                if (!IsPendingSampleStillValid(port, sample))
                {
                    state.Trips.ReleaseAlightMarks(sample.Vehicle, sample.OpenFrame);
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningStalePendingSample,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                m_ReadySamples.Add(sample);
            }

            if (m_ReadySamples.Count == 0)
                return;

            RunPassengerSampleJobs(port, state, frame, clock, m_ReadySamples);
        }

        private static bool IsPendingSampleStillValid(Port port, PendingSample request)
        {
            if (port == null
                || request.Vehicle == Entity.Null
                || request.Line == Entity.Null
                || !port.TryState(request.Vehicle, out _)
                || !port.TryLine(request.Vehicle, out Entity currentLine)
                || currentLine != request.Line
                || !port.TryLineMetadata(request.Line, out TransitMode mode, out _)
                || mode != request.Mode)
            {
                return false;
            }

            return request.OpenWaypointIndex >= 0;
        }

        private static void RunPendingCleanup(State state, uint frame)
        {
            if (frame < state.LastPendingCleanupFrame + PendingTransferCleanupIntervalFrames)
                return;

            state.LastPendingCleanupFrame = frame;
            bool hasPendingDeparture = false;
            uint earliestPendingDepartureFrame = uint.MaxValue;
            foreach (PendingSample sample in state.PendingSamples)
            {
                hasPendingDeparture = true;
                if (sample.DepartureFrame < earliestPendingDepartureFrame)
                    earliestPendingDepartureFrame = sample.DepartureFrame;
            }
            state.Trips.CleanupExpired(frame, hasPendingDeparture, earliestPendingDepartureFrame, state.Aggregates);
            state.Trips.EnforceLimit(MaxPendingTransfers, state.Aggregates);
        }

        private void RunRepresentatives(Port port, State state, uint frame)
        {
            if (state.NextFastRepresentativeFrame == 0 || frame < state.NextFastRepresentativeFrame)
                return;
            bool slowDue = frame >= state.NextSlowRepresentativeFrame;
            state.NextFastRepresentativeFrame +=
                ((frame - state.NextFastRepresentativeFrame) / RepresentativeIntervalFrames + 1)
                * RepresentativeIntervalFrames;
            if (slowDue)
                state.NextSlowRepresentativeFrame +=
                    ((frame - state.NextSlowRepresentativeFrame) / SlowRepresentativeIntervalFrames + 1)
                    * SlowRepresentativeIntervalFrames;
            int representativeCount = m_RepresentativeQuery.CalculateEntityCountWithoutFiltering();
            if (representativeCount > m_MaxRepresentatives)
                m_MaxRepresentatives = representativeCount;
            EntityQuery query = slowDue ? m_RepresentativeQuery : m_FastRepresentativeQuery;
            if (query.IsEmpty)
                return;
            using NativeArray<Entity> members = query.ToEntityArray(Allocator.TempJob);
            using NativeArray<PassengerFlowTransferSample> samples =
                query.ToComponentDataArray<PassengerFlowTransferSample>(Allocator.TempJob);
            using NativeArray<RepresentativeRead> results = new NativeArray<RepresentativeRead>(members.Length, Allocator.TempJob);
            JobHandle readHandle = default;
            try
            {
                UpdateRepresentativeLookups();
                PassengerFlowJobs.RepresentativeJob job = new PassengerFlowJobs.RepresentativeJob
                {
                    Passengers = members,
                    Samples = samples,
                    CurrentVehicles = m_CurrentVehicles,
                    Controllers = m_Controllers,
                    GroupMembers = m_GroupMembers,
                    PathOwners = m_PathOwners,
                    Paths = m_Paths,
                    Lanes = m_Lanes,
                    Connections = m_Connections,
                    Results = results,
                    Frame = frame
                };
                readHandle = job.Schedule(members.Length, 32, Dependency);
                Dependency = readHandle;
                readHandle.Complete();
                for (int i = 0; i < members.Length; i++)
                {
                    Entity passenger = members[i];
                    PassengerFlowTransferSample sample = samples[i];
                    RepresentativeRead read = results[i];
                    if (read.Kind == RepresentativeKind.Reached)
                    {
                        state.Trips.AcceptRepresentative(passenger, sample.m_OldVehicle, sample.m_OpenFrame,
                            sample.m_TargetStop, (TransitMode)sample.m_TargetMode,
                            sample.m_TargetLineId.ToString(), sample.m_TargetStationSakIndex,
                            sample.m_TargetStationOccurrence,
                            sample.m_HasWalkPath != 0 ? sample.m_WalkPathMeters : (double?)null,
                            frame - read.AlightFrame);
                        EntityManager.RemoveComponent<PassengerFlowTransferSample>(passenger);
                    }
                    else if (read.Kind == RepresentativeKind.Invalid)
                        EntityManager.RemoveComponent<PassengerFlowTransferSample>(passenger);
                    else
                    {
                        bool startSlowChecks = sample.m_AlightFrame == 0 && read.AlightFrame != 0
                            && sample.m_WalkCheckIntervalFrames == SlowRepresentativeIntervalFrames;
                        sample.m_AlightFrame = read.AlightFrame;
                        sample.m_NextReadFrame = sample.m_AlightFrame != 0
                            && sample.m_WalkCheckIntervalFrames == SlowRepresentativeIntervalFrames
                            ? state.NextSlowRepresentativeFrame : state.NextFastRepresentativeFrame;
                        EntityManager.SetComponentData(passenger, sample);
                        if (startSlowChecks)
                            EntityManager.SetComponentEnabled<PassengerFlowTransferSample>(passenger, false);
                    }
                }
            }
            finally
            {
                readHandle.Complete();
            }
        }

        private void UpdateRepresentativeLookups()
        {
            m_CurrentVehicles.Update(this);
            m_Controllers.Update(this);
            m_GroupMembers.Update(this);
            m_PathOwners.Update(this);
            m_Paths.Update(this);
            m_Lanes.Update(this);
            m_Connections.Update(this);
        }

        private void RunInitialLoads(Port port, State state, uint frame, ClockSnapshot clock)
        {
            if (state.InitialLoadSamples.Count == 0)
                return;
            using NativeList<Entity> pending = new NativeList<Entity>(MaxDueSamplesPerTick, Allocator.Temp);
            using NativeList<VehicleSampleRequest> batch = new NativeList<VehicleSampleRequest>(MaxDueSamplesPerTick, Allocator.TempJob);
            using (HashSet<Entity>.Enumerator iterator = state.InitialLoadSamples.GetEnumerator())
            {
                while (pending.Length < MaxDueSamplesPerTick && iterator.MoveNext())
                    pending.Add(iterator.Current);
            }
            for (int i = 0; i < pending.Length; i++)
            {
                Entity vehicle = pending[i];
                state.InitialLoadSamples.Remove(vehicle);
                // 注册保证任务属于合资格车辆；自然到离站读取可能已先取得人数。
                VehicleLoadState load = state.VehicleLoads[vehicle];
                if (load.HasPassengers)
                    continue;
                LineLoadState line = state.LineLoads[load.Line];
                batch.Add(new VehicleSampleRequest(frame, line.Mode, load.Line,
                    vehicle, port.RuntimeVehicle(vehicle), -1, -1));
            }
            if (batch.Length == 0)
                return;
            using NativeArray<PassengerFlowJobs.VehicleSampleResult> results =
                new NativeArray<PassengerFlowJobs.VehicleSampleResult>(batch.Length, Allocator.TempJob);
            using NativeParallelMultiHashMap<int, Entity> unusedPassengers =
                new NativeParallelMultiHashMap<int, Entity>(1, Allocator.TempJob);
            PassengerFlowJobs.VehicleScanJob job = new PassengerFlowJobs.VehicleScanJob
            {
                Requests = batch.AsArray(), PassengerBuffers = GetBufferLookup<Passenger>(true),
                LayoutBuffers = GetBufferLookup<LayoutElement>(true), Prefabs = GetComponentLookup<PrefabRef>(true),
                PublicTransportVehicles = GetComponentLookup<PublicTransportVehicleData>(true),
                Pets = GetComponentLookup<Game.Creatures.Pet>(true), CountOnly = true, Results = results,
                CurrentPassengers = unusedPassengers.AsParallelWriter()
            };
            Dependency = job.Schedule(batch.Length, 1, Dependency);
            Dependency.Complete();
            for (int i = 0; i < batch.Length; i++)
            {
                VehicleSampleRequest request = batch[i];
                PassengerFlowJobs.VehicleSampleResult result = results[i];
                bool valid = result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.Ok;
                state.SetVehicleLoad(request.Vehicle, result.PassengerCount, result.PassengerCapacity,
                    result.HasCapacity != 0, valid, frame, clock);
                if (!valid)
                {
                    string lineId = state.LineLoads[request.Line].LineId;
                    LogScanFailure(port, frame, "initial", lineId, request, result);
                    state.Aggregates.RecordWarning(request.Mode,
                        result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.LayoutMissing
                            ? Aggregates.WarningLayoutMissing : Aggregates.WarningPassengerBufferMissing,
                        lineId, -1, state.CurrentBucket, frame);
                }
            }
        }

        private static void LogScanFailure(Port port, uint frame, string entry, string lineId,
            VehicleSampleRequest request, PassengerFlowJobs.VehicleSampleResult result)
        {
            if (!RtLog.VerboseEnabled)
                return;
            Entity selected = request.RuntimeVehicle != Entity.Null ? request.RuntimeVehicle : request.Vehicle;
            port.Log("[PassengerFlowScanFailure] frame=" + frame + " entry=" + entry
                + " vehicle=" + ScanEntity(request.Vehicle) + " runtimeVehicle=" + ScanEntity(request.RuntimeVehicle)
                + " selected=" + ScanEntity(selected) + " line=" + lineId
                + " station=" + (request.OpenStationSakIndex < 0 ? "none" : request.OpenStationSakIndex.ToString())
                + " status=" + result.StatusCode + " branch=" + result.FailureBranch
                + " layoutLength=" + result.LayoutLength + " readableMembers=" + result.ReadableMembers
                + " missingMember=" + ScanEntity(result.MissingMember) + " missingPosition=" + result.MissingPosition
                + " passengerKind=" + result.MissingPassengerKind);
        }

        private static string ScanEntity(Entity entity) => entity.Index + ":" + entity.Version;

        private void RunStopSample(Port port, State state, uint frame, ClockSnapshot clock)
        {
            if (state.StopSamples.Count == 0
                || (state.LastStopScanFrame != 0 && frame - state.LastStopScanFrame < StopScanIntervalFrames))
                return;

            state.LastStopScanFrame = frame;
            m_StopBatch.Clear();
            while (state.StopSamples.Count > 0 && m_StopBatch.Count < MaxStopSamplesPerTick)
            {
                OpenStop stop = state.StopSamples.Dequeue();
                if (state.OpenStops.TryGetValue(stop.Vehicle, out OpenStop current)
                    && current.OpenFrame == stop.OpenFrame && current.Line == stop.Line
                    && current.OpenWaypointIndex == stop.OpenWaypointIndex)
                {
                    RecordStopPlanDelay(stop, frame);
                    m_StopBatch.Add(stop);
                }
            }
            if (m_StopBatch.Count == 0)
                return;
            m_NewRepresentatives.Clear();
            m_StopPlanContexts.Clear();
            m_StopPlanRequests.Clear();

            NativeArray<VehicleSampleRequest> requests = new NativeArray<VehicleSampleRequest>(m_StopBatch.Count, Allocator.TempJob);
            for (int i = 0; i < m_StopBatch.Count; i++)
            {
                OpenStop stop = m_StopBatch[i];
                requests[i] = new VehicleSampleRequest(frame, stop.Mode, stop.Line, stop.Vehicle,
                    BaselineKey(port, stop), stop.OpenWaypointIndex, stop.OpenStationSakIndex);
            }
            BufferLookup<Passenger> passengerBuffers = GetBufferLookup<Passenger>(true);
            BufferLookup<LayoutElement> layoutBuffers = GetBufferLookup<LayoutElement>(true);
            ComponentLookup<PrefabRef> prefabs = GetComponentLookup<PrefabRef>(true);
            ComponentLookup<PublicTransportVehicleData> vehicles = GetComponentLookup<PublicTransportVehicleData>(true);
            ComponentLookup<Game.Creatures.Pet> pets = GetComponentLookup<Game.Creatures.Pet>(true);
            Dependency.Complete();
            NativeParallelMultiHashMap<int, Entity> passengers = new NativeParallelMultiHashMap<int, Entity>(
                Math.Max(1, EstimateCurrentPassengerCapacity(requests, passengerBuffers, layoutBuffers)), Allocator.TempJob);
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results =
                new NativeArray<PassengerFlowJobs.VehicleSampleResult>(m_StopBatch.Count, Allocator.TempJob);
            try
            {
                PassengerFlowJobs.VehicleScanJob job = new PassengerFlowJobs.VehicleScanJob
                {
                    Requests = requests, PassengerBuffers = passengerBuffers, LayoutBuffers = layoutBuffers,
                    Prefabs = prefabs, PublicTransportVehicles = vehicles, Pets = pets,
                    CurrentPassengers = passengers.AsParallelWriter(), Results = results
                };
                Dependency = job.Schedule(m_StopBatch.Count, 1, Dependency);
                Dependency.Complete();
                bool purposeReadsReady = false;
                for (int i = 0; i < m_StopBatch.Count; i++)
                {
                    if (results[i].StatusCode != (int)PassengerFlowJobs.VehicleScanStatus.Ok)
                    {
                        state.SetVehicleLoad(m_StopBatch[i].Vehicle, 0, 0, false, false, frame, clock);
                        OpenStop failed = m_StopBatch[i];
                        LogScanFailure(port, frame, "arrival", failed.LineId, requests[i], results[i]);
                        state.Aggregates.RecordWarning(failed.Mode,
                            results[i].StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.LayoutMissing
                                ? Aggregates.WarningLayoutMissing : Aggregates.WarningPassengerBufferMissing,
                            failed.LineId, failed.OpenStationSakIndex, state.CurrentBucket, frame);
                        continue;
                    }
                    OpenStop stop = m_StopBatch[i];
                    if (!state.OpenStops.TryGetValue(stop.Vehicle, out OpenStop current)
                        || current.OpenFrame != stop.OpenFrame)
                        continue;
                    state.SetVehicleLoad(stop.Vehicle, results[i].PassengerCount,
                        results[i].PassengerCapacity, results[i].HasCapacity != 0, true,
                        frame, clock);
                    PassengerBaseline initialBaseline = null;
                    if (!state.Baselines.ContainsKey(requests[i].BaselineKey))
                    {
                        if (!purposeReadsReady)
                        {
                            CompletePurposeReads();
                            purposeReadsReady = true;
                        }
                        initialBaseline = new PassengerBaseline();
                        state.Baselines.Add(requests[i].BaselineKey, initialBaseline);
                    }
                    if (!passengers.TryGetFirstValue(i, out Entity passenger,
                        out NativeParallelMultiHashMapIterator<int> iterator))
                        continue;
                    do
                    {
                        if (initialBaseline != null)
                        {
                            initialBaseline.Passengers.Add(passenger);
                            initialBaseline.Purposes.Add(passenger, ReadPurpose(passenger));
                        }
                        m_StopPlanContexts.Add((passenger, stop));
                        m_StopPlanRequests.Add(new StopPlanRequest(passenger, stop.Position.Waypoint,
                            stop.Vehicle, requests[i].RuntimeVehicle));
                    }
                    while (passengers.TryGetNextValue(out passenger, ref iterator));
                }
                RunStopPlanJob(port, state, frame, clock);
            }
            finally
            {
                results.Dispose();
                passengers.Dispose();
                requests.Dispose();
            }
            MeasureRepresentativeWalks();
            if (m_NewRepresentatives.Count > 0 && state.NextFastRepresentativeFrame == 0)
            {
                state.NextFastRepresentativeFrame = frame + RepresentativeIntervalFrames;
                state.NextSlowRepresentativeFrame = frame + SlowRepresentativeIntervalFrames;
            }
            for (int i = 0; i < m_NewRepresentatives.Count; i++)
            {
                (Entity passenger, PassengerFlowTransferSample sample, StopPlanRaw _, bool _) = m_NewRepresentatives[i];
                sample.m_NextReadFrame = state.NextFastRepresentativeFrame;
                EntityManager.AddComponentData(passenger, sample);
            }
        }

        private void RunStopPlanJob(Port port, State state, uint frame, ClockSnapshot clock)
        {
            if (m_StopPlanRequests.Count == 0)
                return;
            NativeArray<StopPlanRequest> requests = new NativeArray<StopPlanRequest>(m_StopPlanRequests.Count, Allocator.TempJob);
            NativeArray<StopPlanRaw> results = new NativeArray<StopPlanRaw>(m_StopPlanRequests.Count, Allocator.TempJob);
            for (int i = 0; i < requests.Length; i++)
                requests[i] = m_StopPlanRequests[i];
            try
            {
                PassengerFlowJobs.StopPlanJob job = new PassengerFlowJobs.StopPlanJob
                {
                    Requests = requests,
                    Members = GetComponentLookup<GroupMember>(true),
                    CurrentVehicles = GetComponentLookup<CurrentVehicle>(true),
                    Controllers = GetComponentLookup<Controller>(true),
                    Residents = GetComponentLookup<Game.Creatures.Resident>(true),
                    Owners = GetComponentLookup<Game.Pathfind.PathOwner>(true),
                    Paths = GetBufferLookup<Game.Pathfind.PathElement>(true),
                    Connections = GetComponentLookup<Connected>(true),
                    Boarding = GetComponentLookup<BoardingVehicle>(true),
                    LineOwners = GetComponentLookup<Game.Common.Owner>(true),
                    Results = results
                };
                Dependency = job.Schedule(requests.Length, 32, Dependency);
                Dependency.Complete();
                Dictionary<Entity, Dictionary<Entity, StopPosition>> positionsByLine =
                    new Dictionary<Entity, Dictionary<Entity, StopPosition>>();
                uint transferWindowFrames = clock.ToFramesCeil(SameModeTransferWindowMinutes);
                for (int i = 0; i < requests.Length; i++)
                {
                    (Entity passenger, OpenStop stop) = m_StopPlanContexts[i];
                    StopPlanRead plan = TripPath.FinishStopPlan(results[i], port, state.Anchors,
                        out TransitMode toMode, out string toLineId);
                    state.Trips.AcceptStopPlan(passenger, stop, plan);
                    if (plan.NextLeg.Kind != NextLegKind.NextLeg
                        || EntityManager.HasComponent<PassengerFlowTransferSample>(passenger)
                        || !stop.Position.IsValid)
                        continue;
                    if (!positionsByLine.TryGetValue(plan.BoardLine,
                        out Dictionary<Entity, StopPosition> positions))
                    {
                        positions = port.TryRoutePlan(plan.BoardLine, out RoutePlan routePlan)
                            ? Anchors.BuildPositions(port, routePlan) : null;
                        positionsByLine.Add(plan.BoardLine, positions);
                    }
                    if (positions == null
                        || !positions.TryGetValue(plan.BoardWaypoint, out StopPosition toPosition)
                        || !toPosition.IsValid || toPosition.Stop != plan.BoardStop)
                        continue;
                    SampleGroupKey group = new SampleGroupKey(stop.Mode, stop.LineId,
                        stop.OpenStationSakIndex, stop.Position.StationOccurrence,
                        toMode, toLineId, plan.NextLeg.BoardStationSakIndex,
                        toPosition.StationOccurrence,
                        state.CurrentBucket);
                    if (state.Trips.TrySelectRepresentative(passenger, stop, plan, group,
                        state.Aggregates, frame + transferWindowFrames,
                        out PassengerFlowTransferSample sample))
                    {
                        m_NewRepresentatives.Add((passenger, sample, results[i], stop.Position.Stop == plan.BoardStop));
                    }
                }
            }
            finally
            {
                results.Dispose();
                requests.Dispose();
            }
        }

        private void MeasureRepresentativeWalks()
        {
            int count = m_NewRepresentatives.Count;
            if (count == 0)
                return;
            NativeArray<Entity> passengers = new NativeArray<Entity>(count, Allocator.TempJob);
            NativeArray<StopPlanRaw> plans = new NativeArray<StopPlanRaw>(count, Allocator.TempJob);
            NativeArray<WalkMeasure> results = new NativeArray<WalkMeasure>(count, Allocator.TempJob);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    passengers[i] = m_NewRepresentatives[i].Passenger;
                    plans[i] = m_NewRepresentatives[i].Plan;
                }
                Game.Pathfind.PathfindQueueSystem queue = World.GetOrCreateSystemManaged<Game.Pathfind.PathfindQueueSystem>();
                Game.Pathfind.NativePathfindData graph = queue.GetDataContainer(out JobHandle graphWrites);
                WalkMeasureJob job = new WalkMeasureJob
                {
                    Passengers = passengers, Plans = plans, Graph = graph,
                    Members = GetComponentLookup<GroupMember>(true),
                    Owners = GetComponentLookup<Game.Pathfind.PathOwner>(true),
                    Paths = GetBufferLookup<Game.Pathfind.PathElement>(true), Results = results
                };
                Dependency = job.Schedule(count, 1, JobHandle.CombineDependencies(Dependency, graphWrites));
                queue.AddDataReader(Dependency);
                Dependency.Complete();
                for (int i = 0; i < count; i++)
                {
                    (Entity passenger, PassengerFlowTransferSample sample, StopPlanRaw plan, bool sameStop) = m_NewRepresentatives[i];
                    WalkMeasure result = results[i];
                    sample.m_HasWalkPath = result.Failure == WalkFailure.None ? (byte)1 : (byte)0;
                    sample.m_WalkPathMeters = result.Meters;
                    sample.m_WalkCheckIntervalFrames = !sameStop && sample.m_HasWalkPath != 0
                        && sample.m_WalkPathMeters > 70d
                        ? SlowRepresentativeIntervalFrames : RepresentativeIntervalFrames;
                    if (result.Failure != WalkFailure.None)
                        TripPath.LogWalkFailure(passenger, plan, result);
                    m_NewRepresentatives[i] = (passenger, sample, plan, sameStop);
                }
            }
            finally
            {
                results.Dispose(); plans.Dispose(); passengers.Dispose();
            }
        }

        private void CompletePathReads()
        {
            EntityManager.CompleteDependencyBeforeRO<CurrentVehicle>();
            EntityManager.CompleteDependencyBeforeRO<Controller>();
            EntityManager.CompleteDependencyBeforeRO<GroupMember>();
            EntityManager.CompleteDependencyBeforeRO<Game.Creatures.Resident>();
            EntityManager.CompleteDependencyBeforeRO<Game.Pathfind.PathOwner>();
            EntityManager.CompleteDependencyBeforeRO<Game.Pathfind.PathElement>();
            EntityManager.CompleteDependencyBeforeRO<HumanCurrentLane>();
            EntityManager.CompleteDependencyBeforeRO<Connected>();
            EntityManager.CompleteDependencyBeforeRO<BoardingVehicle>();
            EntityManager.CompleteDependencyBeforeRO<Game.Common.Owner>();
        }

        private void CompletePurposeReads()
        {
            EntityManager.CompleteDependencyBeforeRO<Game.Creatures.Resident>();
            EntityManager.CompleteDependencyBeforeRO<CurrentTransport>();
            EntityManager.CompleteDependencyBeforeRO<Divert>();
            EntityManager.CompleteDependencyBeforeRO<TravelPurpose>();
            EntityManager.CompleteDependencyBeforeRO<Citizen>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdMember>();
            EntityManager.CompleteDependencyBeforeRO<Household>();
        }

        private PurposeInfo ReadPurpose(Entity passenger)
        {
            if (!EntityManager.HasComponent<Game.Creatures.Resident>(passenger))
                return PurposeInfo.Unknown;
            Entity citizenEntity = EntityManager.GetComponentData<Game.Creatures.Resident>(passenger).m_Citizen;
            if (citizenEntity == Entity.Null)
                return PurposeInfo.Unknown;
            Purpose purpose = Purpose.None;
            bool hasPurpose = false;
            if (EntityManager.HasComponent<CurrentTransport>(citizenEntity))
            {
                Entity transport = EntityManager.GetComponentData<CurrentTransport>(citizenEntity).m_CurrentTransport;
                if (transport != Entity.Null && EntityManager.HasComponent<Divert>(transport))
                {
                    Purpose diverted = EntityManager.GetComponentData<Divert>(transport).m_Purpose;
                    if (diverted == Purpose.Safety || diverted == Purpose.Shopping || diverted == Purpose.SendMail)
                    {
                        purpose = diverted;
                        hasPurpose = true;
                    }
                }
            }
            if (!hasPurpose)
            {
                if (!EntityManager.HasComponent<TravelPurpose>(citizenEntity))
                    return PurposeInfo.Unknown;
                purpose = EntityManager.GetComponentData<TravelPurpose>(citizenEntity).m_Purpose;
            }
            int raw = (int)purpose;
            if (purpose == Purpose.None)
                return new PurposeInfo(raw, PurposeCategory.Unknown);
            if (purpose == Purpose.GoingToWork)
                return new PurposeInfo(raw, PurposeCategory.Work);
            if (purpose == Purpose.GoingToSchool)
                return new PurposeInfo(raw, PurposeCategory.School);
            if (purpose == Purpose.Shopping)
                return new PurposeInfo(raw, PurposeCategory.Shopping);
            if (purpose != Purpose.Leisure && purpose != Purpose.VisitAttractions
                && purpose != Purpose.GoingHome && purpose != Purpose.MovingAway)
                return new PurposeInfo(raw, PurposeCategory.Other);
            if (!EntityManager.HasComponent<Citizen>(citizenEntity))
                return new PurposeInfo(raw, PurposeCategory.Unknown);
            CitizenFlags flags = EntityManager.GetComponentData<Citizen>(citizenEntity).m_State;
            bool tourist = (flags & CitizenFlags.Tourist) != 0;
            if (purpose == Purpose.Leisure || purpose == Purpose.VisitAttractions)
                return new PurposeInfo(raw, tourist ? PurposeCategory.Sightseeing : PurposeCategory.Leisure);
            if (purpose == Purpose.MovingAway)
                return new PurposeInfo(raw, tourist ? PurposeCategory.LeavingCity : PurposeCategory.MovingAway);
            if (tourist)
                return new PurposeInfo(raw, PurposeCategory.Hotel);
            if ((flags & CitizenFlags.Commuter) != 0)
                return new PurposeInfo(raw, PurposeCategory.Home);
            if (!EntityManager.HasComponent<HouseholdMember>(citizenEntity))
                return new PurposeInfo(raw, PurposeCategory.Unknown);
            Entity householdEntity = EntityManager.GetComponentData<HouseholdMember>(citizenEntity).m_Household;
            if (householdEntity == Entity.Null || !EntityManager.HasComponent<Household>(householdEntity))
                return new PurposeInfo(raw, PurposeCategory.Unknown);
            Household household = EntityManager.GetComponentData<Household>(householdEntity);
            return new PurposeInfo(raw, (household.m_Flags & HouseholdFlags.MovedIn) != 0
                ? PurposeCategory.Home : PurposeCategory.MovingIn);
        }

        private void RunPassengerSampleJobs(Port port, State state, uint frame, ClockSnapshot clock, List<PendingSample> samples)
        {
            int requestCount = samples.Count;
            NativeArray<VehicleSampleRequest> requests = new NativeArray<VehicleSampleRequest>(requestCount, Allocator.TempJob);
            NativeArray<byte> hasPreviousBaseline = new NativeArray<byte>(requestCount, Allocator.TempJob);
            int previousCapacity = 0;
            for (int i = 0; i < requestCount; i++)
            {
                VehicleSampleRequest jobRequest = samples[i].ToJobRequest();
                requests[i] = jobRequest;
                if (state.Baselines.TryGetValue(jobRequest.BaselineKey, out PassengerBaseline baseline))
                {
                    hasPreviousBaseline[i] = 1;
                    previousCapacity += baseline.Passengers.Count;
                }
            }

            NativeParallelMultiHashMap<int, Entity> previousPassengers =
                new NativeParallelMultiHashMap<int, Entity>(Math.Max(1, previousCapacity), Allocator.TempJob);
            NativeParallelHashSet<PassengerFlowJobs.PassengerMemberKey> previousMembers =
                new NativeParallelHashSet<PassengerFlowJobs.PassengerMemberKey>(Math.Max(1, previousCapacity), Allocator.TempJob);
            for (int i = 0; i < requestCount; i++)
            {
                if (hasPreviousBaseline[i] == 0)
                    continue;
                VehicleSampleRequest request = requests[i];
                PassengerBaseline baseline = state.Baselines[request.BaselineKey];
                for (int p = 0; p < baseline.Passengers.Count; p++)
                {
                    previousPassengers.Add(i, baseline.Passengers[p]);
                    previousMembers.Add(new PassengerFlowJobs.PassengerMemberKey(i, baseline.Passengers[p]));
                }
            }

            BufferLookup<Passenger> passengerBuffers = GetBufferLookup<Passenger>(true);
            BufferLookup<LayoutElement> layoutBuffers = GetBufferLookup<LayoutElement>(true);
            ComponentLookup<PrefabRef> prefabs = GetComponentLookup<PrefabRef>(true);
            ComponentLookup<PublicTransportVehicleData> publicTransportVehicles = GetComponentLookup<PublicTransportVehicleData>(true);
            ComponentLookup<Game.Creatures.Pet> pets = GetComponentLookup<Game.Creatures.Pet>(true);
            int currentCapacity = Math.Max(1, EstimateCurrentPassengerCapacity(requests, passengerBuffers, layoutBuffers));
            NativeParallelMultiHashMap<int, Entity> currentPassengers =
                new NativeParallelMultiHashMap<int, Entity>(currentCapacity, Allocator.TempJob);
            NativeParallelHashSet<PassengerFlowJobs.PassengerMemberKey> currentMembers =
                new NativeParallelHashSet<PassengerFlowJobs.PassengerMemberKey>(currentCapacity, Allocator.TempJob);
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results =
                new NativeArray<PassengerFlowJobs.VehicleSampleResult>(requestCount, Allocator.TempJob);
            NativeList<PassengerFlowJobs.BoardEvent> boardEvents = new NativeList<PassengerFlowJobs.BoardEvent>(Allocator.TempJob);
            NativeList<PassengerFlowJobs.AlightEvent> alightEvents = new NativeList<PassengerFlowJobs.AlightEvent>(Allocator.TempJob);
            NativeList<PassengerFlowJobs.DepartureLoadEvent> departureLoadEvents =
                new NativeList<PassengerFlowJobs.DepartureLoadEvent>(Allocator.TempJob);
            NativeParallelMultiHashMap<int, Entity> nextBaseline =
                new NativeParallelMultiHashMap<int, Entity>(currentCapacity, Allocator.TempJob);

            JobHandle batchHandle = default;
            try
            {
                PassengerFlowJobs.VehicleScanJob scanJob = new PassengerFlowJobs.VehicleScanJob
                {
                    Requests = requests,
                    PassengerBuffers = passengerBuffers,
                    LayoutBuffers = layoutBuffers,
                    Prefabs = prefabs,
                    PublicTransportVehicles = publicTransportVehicles,
                    Pets = pets,
                    CurrentPassengers = currentPassengers.AsParallelWriter(),
                    Results = results
                };
                JobHandle scanHandle = scanJob.Schedule(requestCount, 1, Dependency);
                batchHandle = scanHandle;
                PassengerFlowJobs.DiffJob diffJob = new PassengerFlowJobs.DiffJob
                {
                    Requests = requests,
                    PreviousPassengers = previousPassengers,
                    CurrentPassengers = currentPassengers,
                    PreviousMembers = previousMembers,
                    CurrentMembers = currentMembers,
                    BoardEvents = boardEvents,
                    AlightEvents = alightEvents,
                    DepartureLoadEvents = departureLoadEvents,
                    NextBaseline = nextBaseline
                };
                batchHandle = diffJob.Schedule(scanHandle);
                Dependency = batchHandle;
                batchHandle.Complete();
                CommitPassengerSampleResults(port, state, frame, clock, samples, requests, hasPreviousBaseline,
                    results, boardEvents, alightEvents, departureLoadEvents, nextBaseline);
            }
            finally
            {
                batchHandle.Complete();
                nextBaseline.Dispose();
                departureLoadEvents.Dispose();
                alightEvents.Dispose();
                boardEvents.Dispose();
                results.Dispose();
                currentPassengers.Dispose();
                currentMembers.Dispose();
                previousPassengers.Dispose();
                previousMembers.Dispose();
                hasPreviousBaseline.Dispose();
                requests.Dispose();
            }
        }

        private static Entity BaselineKey(Port port, OpenStop openStop)
        {
            Entity runtimeVehicle = port.RuntimeVehicle(openStop.Vehicle);
            return runtimeVehicle != Entity.Null ? runtimeVehicle : openStop.Vehicle;
        }

        private static int EstimateCurrentPassengerCapacity(
            NativeArray<VehicleSampleRequest> requests,
            BufferLookup<Passenger> passengerBuffers,
            BufferLookup<LayoutElement> layoutBuffers)
        {
            int passengerCountEstimate = 0;
            for (int i = 0; i < requests.Length; i++)
            {
                Entity vehicle = requests[i].RuntimeVehicle != Entity.Null
                    ? requests[i].RuntimeVehicle
                    : requests[i].Vehicle;
                if (vehicle == Entity.Null)
                    continue;

                if (layoutBuffers.HasBuffer(vehicle))
                {
                    DynamicBuffer<LayoutElement> layout = layoutBuffers[vehicle];
                    for (int j = 0; j < layout.Length; j++)
                    {
                        Entity layoutVehicle = layout[j].m_Vehicle;
                        if (layoutVehicle != Entity.Null && passengerBuffers.HasBuffer(layoutVehicle))
                            passengerCountEstimate += passengerBuffers[layoutVehicle].Length;
                    }

                    continue;
                }

                if (passengerBuffers.HasBuffer(vehicle))
                    passengerCountEstimate += passengerBuffers[vehicle].Length;
            }

            return passengerCountEstimate;
        }

        private void CommitPassengerSampleResults(
            Port port,
            State state,
            uint frame,
            ClockSnapshot sampleClock,
            List<PendingSample> samples,
            NativeArray<VehicleSampleRequest> requests,
            NativeArray<byte> hasPreviousBaseline,
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results,
            NativeList<PassengerFlowJobs.BoardEvent> boardEvents,
            NativeList<PassengerFlowJobs.AlightEvent> alightEvents,
            NativeList<PassengerFlowJobs.DepartureLoadEvent> departureLoadEvents,
            NativeParallelMultiHashMap<int, Entity> nextBaseline)
        {
            Array.Clear(m_OkRequests, 0, samples.Count);
            uint transferWindowFrames = sampleClock.ToFramesCeil(SameModeTransferWindowMinutes);
            for (int i = 0; i < results.Length; i++)
            {
                PendingSample sample = samples[i];
                PassengerFlowJobs.VehicleSampleResult result = results[i];
                if (result.StatusCode != (int)PassengerFlowJobs.VehicleScanStatus.Ok)
                    LogScanFailure(port, frame, "departure", sample.LineId, requests[i], result);
                if (result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.PassengerBufferMissing)
                {
                    state.SetVehicleLoad(sample.Vehicle, 0, 0, false, false, frame, sampleClock);
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningPassengerBufferMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                if (result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.LayoutMissing)
                {
                    state.SetVehicleLoad(sample.Vehicle, 0, 0, false, false, frame, sampleClock);
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningLayoutMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                m_OkRequests[i] = true;
                state.SetVehicleLoad(sample.Vehicle, result.PassengerCount,
                    result.PassengerCapacity, result.HasCapacity != 0, true, frame, sampleClock);
                if (hasPreviousBaseline[i] == 0)
                {
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningOriginBaselineMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                }
            }

            CompletePurposeReads();
            for (int i = 0; i < results.Length; i++)
            {
                Dictionary<Entity, PurposeInfo> purposes = m_PurposeByRequest[i];
                purposes.Clear();
                if (!m_OkRequests[i])
                    continue;
                PassengerBaseline previous = hasPreviousBaseline[i] != 0
                    ? state.Baselines[requests[i].BaselineKey] : null;
                if (!nextBaseline.TryGetFirstValue(i, out Entity passenger,
                    out NativeParallelMultiHashMapIterator<int> iterator))
                    continue;
                do
                {
                    purposes[passenger] = previous != null
                        && previous.Purposes.TryGetValue(passenger, out PurposeInfo known)
                        ? known : ReadPurpose(passenger);
                }
                while (nextBaseline.TryGetNextValue(out passenger, ref iterator));
            }

            // 同批必须先建立旧车下车上下文，随后才消费新车的正式上车差分。
            if (alightEvents.Length > 0)
                CompletePathReads();
            for (int i = 0; i < alightEvents.Length; i++)
            {
                PassengerFlowJobs.AlightEvent alightEvent = alightEvents[i];
                if (!m_OkRequests[alightEvent.RequestIndex] || hasPreviousBaseline[alightEvent.RequestIndex] == 0)
                    continue;
                PendingSample sample = samples[alightEvent.RequestIndex];
                NextLegRead nextLeg = state.Trips.TryStopPlan(alightEvent.Passenger,
                    sample.Vehicle, sample.OpenFrame, out StopPlanRead plan)
                    && plan.NextLeg.Kind != NextLegKind.Unresolved
                    ? plan.NextLeg
                    : TripPath.ReadNextLeg(EntityManager, port, state.Anchors, alightEvent.Passenger);
                state.Trips.OnAlight(
                    alightEvent.Passenger, sample.Vehicle, sample.OpenFrame, sample.Mode, sample.LineId, sample.OpenStationSakIndex,
                    sample.Position,
                    state.CurrentBucket, frame,
                    frame + transferWindowFrames, nextLeg,
                    state.Aggregates);
            }

            for (int i = 0; i < boardEvents.Length; i++)
            {
                PassengerFlowJobs.BoardEvent boardEvent = boardEvents[i];
                if (!m_OkRequests[boardEvent.RequestIndex] || hasPreviousBaseline[boardEvent.RequestIndex] == 0)
                    continue;

                PendingSample sample = samples[boardEvent.RequestIndex];
                OpenStop? previousStop = null;
                TimeBucketKey previousBucket = state.CurrentBucket;
                uint previousAlightFrame = frame;
                if (state.Trips.TryActiveVehicle(boardEvent.Passenger, out Entity oldVehicle)
                    && oldVehicle != sample.Vehicle)
                {
                    TryFindPreviousStop(state, samples, oldVehicle,
                        out previousStop, out previousBucket, out previousAlightFrame, frame);
                }
                state.Aggregates.RecordBoarding(
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    m_PurposeByRequest[boardEvent.RequestIndex][boardEvent.Passenger]);
                bool hasRepresentative = EntityManager.HasComponent<PassengerFlowTransferSample>(boardEvent.Passenger);
                PassengerFlowTransferSample representative = hasRepresentative
                    ? EntityManager.GetComponentData<PassengerFlowTransferSample>(boardEvent.Passenger)
                    : default;
                state.Trips.OnBoard(
                    boardEvent.Passenger,
                    sample.Vehicle,
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    sample.Position,
                    sample.DepartureFrame,
                    state.CurrentBucket,
                    state.Aggregates,
                    m_PurposeByRequest[boardEvent.RequestIndex][boardEvent.Passenger],
                    previousStop,
                    previousBucket,
                    previousAlightFrame,
                    transferWindowFrames,
                    frame,
                    hasRepresentative,
                    in representative);
                if (hasRepresentative && representative.m_OldVehicle != sample.Vehicle)
                    EntityManager.RemoveComponent<PassengerFlowTransferSample>(boardEvent.Passenger);
            }

            for (int i = 0; i < alightEvents.Length; i++)
            {
                PassengerFlowJobs.AlightEvent alightEvent = alightEvents[i];
                if (!m_OkRequests[alightEvent.RequestIndex] || hasPreviousBaseline[alightEvent.RequestIndex] == 0)
                    continue;

                PendingSample sample = samples[alightEvent.RequestIndex];
                state.Aggregates.RecordAlighting(
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    state.Baselines[requests[alightEvent.RequestIndex].BaselineKey].Purposes
                        .TryGetValue(alightEvent.Passenger, out PurposeInfo alightPurpose)
                        ? alightPurpose : PurposeInfo.Unknown);
            }

            for (int i = 0; i < departureLoadEvents.Length; i++)
            {
                PassengerFlowJobs.DepartureLoadEvent loadEvent = departureLoadEvents[i];
                if (!m_OkRequests[loadEvent.RequestIndex] || !Sections.Supports(samples[loadEvent.RequestIndex].Mode))
                    continue;

                PendingSample sample = samples[loadEvent.RequestIndex];
                PassengerFlowJobs.VehicleSampleResult result = results[loadEvent.RequestIndex];
                if (sample.StopSections.Length == 0 && sample.TrackSections.Length == 0)
                    continue;
                Dictionary<PurposeInfo, PurposeCount> purposes = new Dictionary<PurposeInfo, PurposeCount>();
                foreach (PurposeInfo purpose in m_PurposeByRequest[loadEvent.RequestIndex].Values)
                {
                    purposes.TryGetValue(purpose, out PurposeCount count);
                    count.LoadPassengersSum++;
                    purposes[purpose] = count;
                }
                RecordPreparedSections(state, sample, sample.StopSections, SectionKind.Stops,
                    loadEvent.PassengerCount, result, purposes);
                RecordPreparedSections(state, sample, sample.TrackSections, SectionKind.Track,
                    loadEvent.PassengerCount, result, purposes);
            }

            for (int i = 0; i < requests.Length; i++)
            {
                if (!m_OkRequests[i])
                {
                    state.Trips.ReleaseAlightMarks(samples[i].Vehicle, samples[i].OpenFrame);
                    continue;
                }

                m_NextBaseline.Clear();
                NativeParallelMultiHashMapIterator<int> iterator;
                Entity passenger;
                if (nextBaseline.TryGetFirstValue(i, out passenger, out iterator))
                {
                    do
                    {
                        m_NextBaseline.Add(passenger);
                    }
                    while (nextBaseline.TryGetNextValue(out passenger, ref iterator));
                }

                Entity baselineKey = requests[i].BaselineKey;
                if (!state.Baselines.TryGetValue(baselineKey, out PassengerBaseline baseline))
                {
                    baseline = new PassengerBaseline();
                    state.Baselines[baselineKey] = baseline;
                }

                baseline.Replace(m_NextBaseline, m_PurposeByRequest[i]);
                state.Trips.ReleaseAlightMarks(samples[i].Vehicle, samples[i].OpenFrame);
            }
        }

        private static void RecordPreparedSections(State state, PendingSample sample,
            SectionSegment[] sections, SectionKind kind, int passengers,
            PassengerFlowJobs.VehicleSampleResult result,
            Dictionary<PurposeInfo, PurposeCount> purposes)
        {
            for (int i = 0; i < sections.Length; i++)
                state.Aggregates.RecordSectionLoad(sample.Mode, sample.LineId,
                    sections[i].FromStationSakIndex, sections[i].ToStationSakIndex,
                    passengers, result.PassengerCapacity, result.HasCapacity != 0,
                    sample.DepartureBucket, purposes, kind);
        }

        private static void TryFindPreviousStop(State state, List<PendingSample> batch,
            Entity vehicle, out OpenStop? stop, out TimeBucketKey bucket,
            out uint alightFrame, uint observationFrame)
        {
            if (state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
            {
                stop = openStop;
                bucket = state.CurrentBucket;
                alightFrame = observationFrame;
                return;
            }
            foreach (PendingSample sample in batch)
            {
                if (sample.Vehicle == vehicle)
                {
                    stop = new OpenStop(sample.Vehicle, sample.Mode, sample.LineId, sample.Line,
                        sample.OpenWaypointIndex, sample.OpenStationSakIndex, sample.OpenFrame, 0,
                        sample.Position);
                    bucket = state.CurrentBucket;
                    alightFrame = observationFrame;
                    return;
                }
            }
            foreach (PendingSample sample in state.PendingSamples)
            {
                if (sample.Vehicle == vehicle)
                {
                    stop = new OpenStop(sample.Vehicle, sample.Mode, sample.LineId, sample.Line,
                        sample.OpenWaypointIndex, sample.OpenStationSakIndex, sample.OpenFrame, 0,
                        sample.Position);
                    bucket = state.CurrentBucket;
                    alightFrame = observationFrame;
                    return;
                }
            }
            stop = null;
            bucket = state.CurrentBucket;
            alightFrame = observationFrame;
        }
    }
}
