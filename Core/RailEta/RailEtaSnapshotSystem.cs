using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using RapidTransitMod.RailEta.Contracts;
using RapidTransitMod.RailEtaHost;
using RapidTransitMod.Bypass;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    /// <summary>
    /// Request-driven read-only capture system. RailEtaHotModule ticks it from DispatchRuntimeSystem after
    /// the runtime read port has been updated. TrainNavigation updates at interval 16/offset 3, while lane
    /// reservations reset their frameIndex % 16 bucket every frame.
    /// No query or job is scheduled while the service has no request and this system has no active batch.
    /// </summary>
    internal sealed partial class RailEtaSnapshotSystem : GameSystemBase
    {
        private enum Phase { Idle, IndexJob, TheoryPaths, ScopeWorker, PathRequests, MaterializeWorker, PredictorWorker }
        private sealed class PendingPathRequest
        {
            public string Id;
            public RailEtaMissingRouteSegment Segment;
            public uint StartFrame;
        }
        private sealed class ResolvedPathResult
        {
            public RailEtaMissingRouteSegment Segment;
            public RailTravel.Path Path;
            public uint DelayFrames;
        }
        private sealed class CaptureSeed
        {
            public readonly List<Entity> Controllers = new List<Entity>();
            public readonly List<Entity> RouteLines = new List<Entity>();
        }
        private static readonly ProfilerMarker s_ScheduleIndex = new ProfilerMarker("RailEta.ScheduleIndexJob");
        private static readonly ProfilerMarker s_WorkerScope = new ProfilerMarker("RailEta.WorkerScopeBuild");
        private static readonly ProfilerMarker s_WorkerMaterialize = new ProfilerMarker("RailEta.WorkerMaterialize");
        private static readonly ProfilerMarker s_Publish = new ProfilerMarker("RailEta.PublishCompleted");
        private readonly ConcurrentQueue<RailEtaScopeResult> m_ScopeResults = new ConcurrentQueue<RailEtaScopeResult>();
        private readonly ConcurrentQueue<RailEtaMaterializeResult> m_MaterializeResults = new ConcurrentQueue<RailEtaMaterializeResult>();
        private readonly ConcurrentQueue<RailEtaPredictionResult> m_PredictionResults = new ConcurrentQueue<RailEtaPredictionResult>();
        private readonly object m_CallbackGate = new object();
        private SimulationSystem m_Simulation;
        private EntityQuery m_TrainQuery;
        private EntityQuery m_RouteQuery;
        private EntityQuery m_RailLaneQuery;
        private EntityQuery m_TrafficLightQuery;
        private RailTravel.QuerySystem m_RailTravelQuery;
        private RailEtaTheoryPaths m_TheoryPaths;
        private Phase m_Phase;
        private JobHandle m_Handle;
        private RailEtaScopedStaging m_Staging;
        private RailEtaScopeResult m_Scope;
        private readonly List<PendingPathRequest> m_PathRequests = new List<PendingPathRequest>();
        private readonly List<ResolvedPathResult> m_ResolvedPaths = new List<ResolvedPathResult>();
        private RailEtaRequestFrameFacts m_FrozenRuntimeFacts;
        private List<RailEtaBatchRequest> m_Requests;
        private long m_BatchId;
        private int m_Generation;
        private RailEtaMode m_Mode;
        private long m_PredictorGeneration;
        private uint m_IndexOriginFrame;
        private uint m_RequestStartFrame;
        private long m_IndexStartTicks;
        private long m_IndexWallTicks;
        private RailEtaService m_BatchService;
        private long m_BatchServiceId;
        private volatile bool m_ShuttingDown;
        private RailEtaRuntimeReadPort m_RuntimeReadPort;
        private Func<long> m_PredictorGenerationAccessor;

        internal void Configure(RailEtaRuntimeReadPort runtimeReadPort, Func<long> predictorGenerationAccessor)
        {
            m_RuntimeReadPort = runtimeReadPort;
            m_PredictorGenerationAccessor = predictorGenerationAccessor;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_RailTravelQuery = World.GetOrCreateSystemManaged<RailTravel.QuerySystem>();
            m_TheoryPaths = new RailEtaTheoryPaths(World, m_RailTravelQuery, CreateTheoryLog());
            m_TrainQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Train>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<Moving>(),
                    ComponentType.ReadOnly<Target>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<PathElement>(),
                    ComponentType.ReadOnly<PathOwner>(), ComponentType.ReadOnly<TrainCurrentLane>(), ComponentType.ReadOnly<TrainNavigation>(),
                    ComponentType.ReadOnly<TrainNavigationLane>(), ComponentType.ReadOnly<LayoutElement>()
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<TripSource>() }
            });
            m_RouteQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RouteSegment>(), ComponentType.ReadOnly<RouteWaypoint>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
            // Global staging covers TrackLane + ConnectionLane + EdgeLane only.
            // Flags-only PathElement helpers without those components are not world-scanned;
            // vehicle/route PathElement freeze and Query append must fail explicitly if such a target lacks a frozen fact.
            m_RailLaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[] { ComponentType.ReadOnly<Game.Net.TrackLane>(), ComponentType.ReadOnly<Game.Net.ConnectionLane>(), ComponentType.ReadOnly<Game.Net.EdgeLane>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
            m_TrafficLightQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<TrafficLights>(), ComponentType.ReadOnly<Game.Net.SubLane>(), ComponentType.ReadOnly<UpdateFrame>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() }
            });
        }

        protected override void OnUpdate()
        {
            RailEtaService service = m_Phase == Phase.Idle ? RailEtaService.Current : m_BatchService;
            if (service == null) return;
            service.ObserveFrame(m_Simulation.frameIndex);
            long watchdogTicks = Stopwatch.Frequency * RailEtaLimits.WorkerWatchdogMilliseconds / 1000;
            if (service.Worker.TryMarkLostIfStalled(Stopwatch.GetTimestamp(), watchdogTicks))
                service.MarkWorkerLost("Rail ETA worker watchdog budget exceeded.");
            if (service.Worker.WorkerLost || service.WorkerLost)
            {
                if (m_Phase != Phase.Idle)
                {
                    Exception failure = service.Worker.LastFailure;
                    string detail = failure == null ? "Rail ETA worker is lost." : failure.GetType().Name + ": " + failure.Message;
                    service.MarkWorkerLost(detail);
                    FinishBatch();
                }
                return;
            }
            switch (m_Phase)
            {
                case Phase.Idle: TryStartBatch(service); break;
                case Phase.IndexJob: PollIndex(service); break;
                case Phase.TheoryPaths: PollTheoryPaths(service); break;
                case Phase.ScopeWorker: PollScope(service); break;
                case Phase.PathRequests: PollPathRequests(service); break;
                case Phase.MaterializeWorker: PollMaterialize(service); break;
                case Phase.PredictorWorker: PollPrediction(service); break;
            }
        }

        private void TryStartBatch(RailEtaService service)
        {
            if (service.PendingCount == 0 || !service.TryPeek(out RailEtaRequestEnvelope pending)) return;
            uint requestFrame = m_Simulation.frameIndex;
            int generation = service.Generation;
            if (pending.EnqueueGeneration != generation)
            {
                if (service.TryDrain(out RailEtaRequestEnvelope stale))
                    service.Transition(stale.Ticket, RailEtaRequestState.Cancelled, 0, 0, stale.EnqueueGeneration, RailEtaFailure.Cancelled);
                return;
            }
            RailEtaMode mode = pending.Descriptor.Mode;
            CaptureSeed seed = null;
            if (mode != RailEtaMode.Full && !TryBuildTargetedCaptureSeed(pending.Descriptor, mode, out seed, out string seedFailure, out bool retrySeed))
            {
                // Newly spawned consists receive their trailing-unit lane caches a few frames after
                // the controller path. Keep the same queued request until vanilla finishes that work.
                if (retrySeed) return;
                if (service.TryDrain(out RailEtaRequestEnvelope failed))
                    service.Transition(failed.Ticket, RailEtaRequestState.Failed, requestFrame, 0, generation, RailEtaFailure.PathIncomplete, seedFailure);
                return;
            }
            RailEtaRequestFrameFacts runtimeFacts = CaptureRuntimeFactsAtRequestFrame(requestFrame, mode, seed?.Controllers);
            if (runtimeFacts == null) return;
            if (!service.TryDrain(out RailEtaRequestEnvelope envelope)) return;
            // HotModule admits one active ticket. Keeping one request per frozen world prevents mode mixing.
            var requests = new List<RailEtaBatchRequest>(1)
            {
                new RailEtaBatchRequest { Ticket = envelope.Ticket, Descriptor = envelope.Descriptor }
            };
            m_BatchId = service.NextBatchId();
            m_RequestStartFrame = requestFrame;
            m_IndexOriginFrame = requestFrame;
            m_FrozenRuntimeFacts = runtimeFacts;
            foreach (RailEtaBatchRequest request in requests) service.BindRequestFrame(request.Ticket, requestFrame, m_BatchId, generation);
            m_BatchService = service;
            m_BatchServiceId = service.InstanceId;
            m_Generation = generation;
            m_Mode = mode;
            m_PredictorGeneration = m_PredictorGenerationAccessor?.Invoke() ?? 0;
            m_Requests = requests;
            ScheduleIndex(service, seed);
        }

        private static Action<string> CreateTheoryLog()
        {
#if RT_DEBUG_TOOLS
            return RailEtaDebugSettings.DetailedLogsEnabled ? new Action<string>(value => Mod.log.Info(value)) : null;
#else
            return null;
#endif
        }

        private void ScheduleIndex(RailEtaService service, CaptureSeed seed)
        {
            m_IndexStartTicks = Stopwatch.GetTimestamp();
            NativeList<Entity> controllers;
            NativeList<Entity> routeLines;
            NativeList<Entity> railLanes;
            NativeList<Entity> trafficLights;
            JobHandle controllerHandle = default;
            JobHandle routeLineHandle = default;
            JobHandle railLaneHandle = default;
            JobHandle trafficLightHandle = default;
            if (m_Mode == RailEtaMode.Full)
            {
                controllers = m_TrainQuery.ToEntityListAsync(Allocator.Persistent, out controllerHandle);
                controllerHandle.Complete();
                AppendRequestedTargetControllers(controllers);
                routeLines = m_RouteQuery.ToEntityListAsync(Allocator.Persistent, out routeLineHandle);
                railLanes = m_RailLaneQuery.ToEntityListAsync(Allocator.Persistent, out railLaneHandle);
                trafficLights = m_TrafficLightQuery.ToEntityListAsync(Allocator.Persistent, out trafficLightHandle);
            }
            else if (m_Mode == RailEtaMode.Theory && seed.Controllers.Count == 0)
            {
                controllers = new NativeList<Entity>(Allocator.Persistent);
                routeLines = ToNativeList(seed.RouteLines);
                railLanes = m_RailLaneQuery.ToEntityListAsync(Allocator.Persistent, out railLaneHandle);
                trafficLights = new NativeList<Entity>(Allocator.Persistent);
            }
            else
            {
                controllers = ToNativeList(seed.Controllers);
                routeLines = ToNativeList(seed.RouteLines);
                railLanes = new NativeList<Entity>(Allocator.Persistent);
                trafficLights = new NativeList<Entity>(Allocator.Persistent);
            }
            m_Staging = new RailEtaScopedStaging(controllers, routeLines, railLanes, trafficLights);
            CollectRailSnapshotJob snapshotJob = CreateSnapshotJob(m_Staging);
            using (s_ScheduleIndex.Auto())
            {
                JobHandle entityListsHandle = JobHandle.CombineDependencies(controllerHandle,
                    JobHandle.CombineDependencies(routeLineHandle, railLaneHandle, trafficLightHandle));
                JobHandle snapshotDependency = JobHandle.CombineDependencies(Dependency, entityListsHandle);
                m_Handle = IJobExtensions.Schedule(snapshotJob, snapshotDependency);
            }
            Dependency = m_Handle;
            foreach (RailEtaBatchRequest request in m_Requests) service.Transition(request.Ticket, RailEtaRequestState.IndexJobScheduled, m_IndexOriginFrame, m_BatchId, m_Generation);
            m_Phase = Phase.IndexJob;
        }

        private static NativeList<Entity> ToNativeList(List<Entity> values)
        {
            var result = new NativeList<Entity>(values?.Count ?? 0, Allocator.Persistent);
            if (values != null) for (int i = 0; i < values.Count; i++) result.Add(values[i]);
            return result;
        }

        private void AppendRequestedTargetControllers(NativeList<Entity> controllers)
        {
            EntityManager entities = EntityManager;
            for (int requestIndex = 0; requestIndex < m_Requests.Count; requestIndex++)
            {
                Entity controller = RailEtaEntityId.ToEntity(m_Requests[requestIndex].Descriptor);
                bool present = false;
                for (int i = 0; i < controllers.Length; i++)
                    if (controllers[i] == controller) { present = true; break; }
                if (present || !HasCompleteTargetFreezeFacts(entities, controller)) continue;
                controllers.Add(controller);
            }
        }

        private static bool HasCompleteTargetFreezeFacts(EntityManager entities, Entity controller)
        {
            if (!entities.Exists(controller)
                || !entities.HasComponent<Target>(controller)
                || !entities.HasComponent<PathOwner>(controller)
                || !entities.HasComponent<Blocker>(controller)
                || !entities.HasComponent<PathElement>(controller)
                || !entities.HasComponent<LayoutElement>(controller)) return false;
            if (entities.GetBuffer<PathElement>(controller, true).Length == 0) return false;
            DynamicBuffer<LayoutElement> layout = entities.GetBuffer<LayoutElement>(controller, true);
            if (layout.Length == 0) return false;
            for (int i = 0; i < layout.Length; i++)
            {
                Entity unit = layout[i].m_Vehicle;
                if (!entities.Exists(unit)
                    || !entities.HasComponent<Train>(unit)
                    || !entities.HasComponent<Game.Objects.Transform>(unit)
                    || !entities.HasComponent<Moving>(unit)
                    || !entities.HasComponent<TrainNavigation>(unit)
                    || !entities.HasComponent<TrainCurrentLane>(unit)
                    || !entities.HasComponent<PrefabRef>(unit)) return false;
            }
            return true;
        }

        private bool TryBuildTargetedCaptureSeed(RailEtaRequestDescriptor descriptor, RailEtaMode mode,
            out CaptureSeed seed, out string failure, out bool retry)
        {
            seed = new CaptureSeed();
            failure = string.Empty;
            retry = false;
            EntityManager entities = EntityManager;
            Entity target = RailEtaEntityId.ToEntity(descriptor);
            if (mode == RailEtaMode.Theory && descriptor.ModelIndex != 0)
            {
                Entity model = new Entity { Index = descriptor.ModelIndex, Version = descriptor.ModelVersion };
                Entity waypoint = RailEtaEntityId.ToEntity(descriptor.TargetCheckpointId);
                if (!entities.Exists(target) || !entities.Exists(model) || !entities.Exists(waypoint)
                    || !entities.HasComponent<TrainData>(model) || !entities.HasComponent<ObjectGeometryData>(model))
                {
                    failure = "Theory line, depot, waypoint, or vehicle model facts are unavailable.";
                    return false;
                }
                seed.RouteLines.Add(target);
                return true;
            }
            if (!HasCompleteTargetFreezeFacts(entities, target))
            {
                failure = "Target train does not have a complete navigation path snapshot.";
                return false;
            }
            if (!HasInitializedConsistLanes(entities, target))
            {
                retry = true;
                failure = "Waiting for vanilla to initialize all consist lane caches.";
                return false;
            }
            seed.Controllers.Add(target);
            var targetPathLanes = new HashSet<Entity>();
            DynamicBuffer<LayoutElement> layout = entities.GetBuffer<LayoutElement>(target, true);
            Entity lead = layout[0].m_Vehicle;
            if (entities.HasComponent<TrainCurrentLane>(lead))
            {
                TrainCurrentLane current = entities.GetComponentData<TrainCurrentLane>(lead);
                if (current.m_Front.m_Lane != Entity.Null) targetPathLanes.Add(current.m_Front.m_Lane);
            }
            if (entities.HasBuffer<TrainNavigationLane>(target))
            {
                DynamicBuffer<TrainNavigationLane> navigation = entities.GetBuffer<TrainNavigationLane>(target, true);
                for (int i = 0; i < navigation.Length; i++) if (navigation[i].m_Lane != Entity.Null) targetPathLanes.Add(navigation[i].m_Lane);
            }
            PathOwner owner = entities.GetComponentData<PathOwner>(target);
            DynamicBuffer<PathElement> path = entities.GetBuffer<PathElement>(target, true);
            for (int i = Unity.Mathematics.math.max(0, owner.m_ElementIndex); i < path.Length; i++)
                if (path[i].m_Target != Entity.Null) targetPathLanes.Add(path[i].m_Target);
            if (targetPathLanes.Count == 0)
            {
                failure = "Target train navigation path has no lane.";
                return false;
            }

            if (mode == RailEtaMode.PathOccupants)
            {
                // Compact mode expands exactly once from target-path physical lane occupancy.
                // It does not recurse through blocker chains, shared lines, overlaps or occupant paths.
                foreach (Entity lane in targetPathLanes)
                {
                    if (!entities.HasBuffer<LaneObject>(lane)) continue;
                    DynamicBuffer<LaneObject> laneObjects = entities.GetBuffer<LaneObject>(lane, true);
                    for (int i = 0; i < laneObjects.Length; i++)
                    {
                        Entity occupant = laneObjects[i].m_LaneObject;
                        Entity controller = entities.HasComponent<Controller>(occupant)
                            ? entities.GetComponentData<Controller>(occupant).m_Controller : occupant;
                        if (controller == Entity.Null || Contains(seed.Controllers, controller)
                            || !HasCompleteTargetFreezeFacts(entities, controller)
                            || !HasInitializedConsistLanes(entities, controller)) continue;
                        seed.Controllers.Add(controller);
                    }
                }
            }
            for (int i = 0; i < seed.Controllers.Count; i++)
            {
                Entity controller = seed.Controllers[i];
                if (!entities.HasComponent<CurrentRoute>(controller)) continue;
                Entity line = entities.GetComponentData<CurrentRoute>(controller).m_Route;
                if (line != Entity.Null && !Contains(seed.RouteLines, line)) seed.RouteLines.Add(line);
            }
            return true;
        }

        private static bool HasInitializedConsistLanes(EntityManager entities, Entity controller)
        {
            if (!entities.HasBuffer<LayoutElement>(controller)) return false;
            DynamicBuffer<LayoutElement> layout = entities.GetBuffer<LayoutElement>(controller, true);
            if (layout.Length == 0) return false;
            for (int i = 0; i < layout.Length; i++)
            {
                Entity unit = layout[i].m_Vehicle;
                if (!entities.HasComponent<TrainCurrentLane>(unit)) return false;
                TrainCurrentLane current = entities.GetComponentData<TrainCurrentLane>(unit);
                if (current.m_Front.m_Lane == Entity.Null
                    || current.m_Rear.m_Lane == Entity.Null
                    || current.m_FrontCache.m_Lane == Entity.Null
                    || current.m_RearCache.m_Lane == Entity.Null
                    || (current.m_Front.m_LaneFlags & TrainLaneFlags.Obsolete) != 0) return false;
            }
            return true;
        }

        private static bool Contains(List<Entity> values, Entity value)
        {
            for (int i = 0; i < values.Count; i++) if (values[i] == value) return true;
            return false;
        }

        private void PollIndex(RailEtaService service)
        {
            if (!m_Handle.IsCompleted) return;
            try { m_Handle.Complete(); }
            catch (Exception ex) { m_Staging?.Dispose(); FailBatch(service, RailEtaFailure.InvalidResult, ex.GetType().Name + ": " + ex.Message); return; }
            m_IndexWallTicks = Stopwatch.GetTimestamp() - m_IndexStartTicks;
            if (!IsCurrentBatchService(service) || service.Generation != m_Generation) { m_Staging.Dispose(); m_Staging = null; CancelBatch(service); return; }
            foreach (RailEtaBatchRequest request in m_Requests) service.Transition(request.Ticket, RailEtaRequestState.IndexReady, m_IndexOriginFrame, m_BatchId, m_Generation);
            if (m_Mode == RailEtaMode.Theory)
            {
                if (!m_TheoryPaths.Start(m_Requests[0].Descriptor, out string theoryFailure))
                {
                    m_Staging.Dispose();
                    m_Staging = null;
                    FailBatch(service, RailEtaFailure.FuturePathfindFailed, theoryFailure);
                    return;
                }
                m_Phase = Phase.TheoryPaths;
                return;
            }
            EnqueueScope(service);
        }

        private void PollTheoryPaths(RailEtaService service)
        {
            if (unchecked(m_Simulation.frameIndex - m_RequestStartFrame) >= 512u)
            {
                FailBatch(service, RailEtaFailure.FuturePathfindFailed,
                    "Theory depot path selection exceeded the 512-frame request budget.");
                return;
            }
            if (!m_TheoryPaths.Poll(out RailEtaTheoryPathResult result, out string failure)) return;
            if (result == null)
            {
                m_Staging.Dispose();
                m_Staging = null;
                FailBatch(service, RailEtaFailure.FuturePathfindFailed, failure);
                return;
            }
            RailEtaRequestDescriptor descriptor = m_Requests[0].Descriptor;
            Entity line = RailEtaEntityId.ToEntity(descriptor);
            Entity target = RailEtaEntityId.ToEntity(descriptor.TargetCheckpointId);
            var missing = new RailEtaMissingRouteSegment
            {
                Controller = line,
                Target = target,
                IsVehicleTarget = 1,
                NeedsGeometry = 1
            };
            IReadOnlyDictionary<Entity, List<RailEtaScopedLaneRow>> facts = BuildFrozenLaneFactIndex(m_Staging);
            bool pathAppended = AppendResolvedPathForWorker(
                m_Staging, facts, missing, result.Path, 0u, out string appendFailure);
            string vehicleFailure = string.Empty;
            bool vehicleAppended = pathAppended
                && RailEtaTheoryVehicle.Append(EntityManager, m_Staging, descriptor, result.Path, out vehicleFailure);
            if (!pathAppended || !vehicleAppended)
            {
                m_Staging.Dispose();
                m_Staging = null;
                FailBatch(service, RailEtaFailure.RouteGeometryMissing,
                    !String.IsNullOrEmpty(appendFailure) ? appendFailure : vehicleFailure);
                return;
            }
            EnqueueScope(service);
        }

        private void EnqueueScope(RailEtaService service)
        {
            RailEtaScopeWork work = new RailEtaScopeWork { Mode = m_Mode, BatchId = m_BatchId, IndexOriginFrame = m_IndexOriginFrame, Generation = m_Generation, Requests = m_Requests, Staging = m_Staging, RequestFrameFacts = m_FrozenRuntimeFacts,
                ExcludedVehicles = new HashSet<Entity>(), VehiclePathFailures = new List<RailEtaVehiclePathFailure>(), TicketFailures = new List<RailEtaTicketFailure>(), FailedSegments = new HashSet<RailEtaFailedSegmentKey>() };
            m_Staging = null;
            m_FrozenRuntimeFacts = null;
            if (!service.Worker.TryEnqueue(() =>
            {
                RailEtaScopeResult result;
                try { using (s_WorkerScope.Auto()) result = new RailEtaScopeBuilder().Build(work); }
                catch (Exception ex) { result = new RailEtaScopeResult { Mode = work.Mode, BatchId = work.BatchId, IndexOriginFrame = work.IndexOriginFrame, Generation = work.Generation, Requests = work.Requests, Staging = work.Staging, Failure = RailEtaFailure.InvalidResult, Detail = ex.GetType().Name + ": " + ex.Message }; }
                lock (m_CallbackGate) { if (m_ShuttingDown) result.Dispose(); else m_ScopeResults.Enqueue(result); }
            }))
            {
                work.Staging.Dispose();
                FailBatch(service, RailEtaFailure.Busy, "Rail ETA worker queue is full.");
                return;
            }
            m_Phase = Phase.ScopeWorker;
        }

        private void PollScope(RailEtaService service)
        {
            if (!m_ScopeResults.TryDequeue(out RailEtaScopeResult scope)) return;
            if (!IsCurrentBatchService(service) || scope.Generation != service.Generation || scope.Generation != m_Generation) { scope.Dispose(); CancelBatch(service); return; }
            if (scope.TicketFailures != null) foreach (RailEtaTicketFailure failure in scope.TicketFailures) service.Transition(failure.Ticket, RailEtaRequestState.Failed, scope.IndexOriginFrame, scope.BatchId, scope.Generation, failure.Failure, failure.Detail);
            if (scope.Failure != RailEtaFailure.None) { FailRequests(service, scope.Requests ?? m_Requests, scope.Failure, scope.Detail); scope.Dispose(); FinishBatch(); return; }
            if (scope.Requests == null || scope.Requests.Count == 0) { scope.Dispose(); FinishBatch(); return; }
            if (scope.VehiclePathFailures.Count != 0) for (int i = 0; i < scope.Requests.Count; i++) service.MarkIncomplete(scope.Requests[i].Ticket);
            foreach (RailEtaBatchRequest request in scope.Requests) service.Transition(request.Ticket, RailEtaRequestState.ScopeReady, scope.IndexOriginFrame, scope.BatchId, scope.Generation);
            m_Scope = scope;
            if (scope.MissingSegments != null && scope.MissingSegments.Count > 0)
            {
                if (m_Mode == RailEtaMode.Theory)
                {
                    FailRequests(service, scope.Requests, RailEtaFailure.FuturePathfindFailed,
                        "Theory selected path was not retained by the frozen scope.");
                    scope.Dispose();
                    FinishBatch();
                    return;
                }
                StartPathRequests(service, scope);
                return;
            }
            EnqueueMaterialize(service, scope);
        }

        private void EnqueueMaterialize(RailEtaService service, RailEtaScopeResult scope)
        {
            RailEtaMaterializeWork work = new RailEtaMaterializeWork { Scope = scope, OriginFrame = scope.IndexOriginFrame, IndexWallTicks = m_IndexWallTicks };
            if (!service.Worker.TryEnqueue(() =>
            {
                RailEtaMaterializeResult result;
                try
                {
                    long materializeStart = Stopwatch.GetTimestamp();
                    using (s_WorkerMaterialize.Auto()) result = new RailEtaSnapshotMaterializer().Materialize(work);
                    result.MaterializeWallTicks = Stopwatch.GetTimestamp() - materializeStart;
                }
                catch (Exception ex) { result = new RailEtaMaterializeResult { Scope = work.Scope, Failure = RailEtaFailure.InvalidResult, Detail = ex.GetType().Name + ": " + ex.Message }; }
                lock (m_CallbackGate) { if (m_ShuttingDown) result.Scope.Dispose(); else m_MaterializeResults.Enqueue(result); }
            }))
            {
                scope.Dispose();
                FailBatch(service, RailEtaFailure.Busy, "Rail ETA worker queue is full.");
                return;
            }
            m_Scope = null;
            m_Phase = Phase.MaterializeWorker;
        }

        internal JobHandle TickExternal(JobHandle inputDependency)
        {
            if (m_Phase == Phase.Idle)
            {
                RailEtaService service = RailEtaService.Current;
                if (service == null || service.PendingCount == 0) return inputDependency;
            }
            Dependency = JobHandle.CombineDependencies(Dependency, inputDependency);
            Update();
            return Dependency;
        }

        private RailEtaRequestFrameFacts CaptureRuntimeFactsAtRequestFrame(uint frame, RailEtaMode mode, List<Entity> selectedControllers)
        {
            RailEtaRuntimeReadPort port = m_RuntimeReadPort;
            if (port?.ClockSnapshot == null) return null;
            ClockSnapshot clockSnapshot = port.ClockSnapshot();
            var result = new RailEtaRequestFrameFacts
            {
                FramesPerMinute = clockSnapshot.FramesPerMinute,
                ClockEpoch = clockSnapshot.ClockEpoch
            };
            if (mode == RailEtaMode.Theory) return result;
            if (port.LineDwellMinutes == null || port.TryReadOriginScheduledHold == null
                || port.TryReadHold == null || port.TryReadTrackChain == null) return null;
            List<Entity> controllers = selectedControllers;
            if (controllers == null)
            {
                controllers = new List<Entity>();
                using (NativeArray<Entity> values = m_TrainQuery.ToEntityArray(Allocator.Temp))
                    for (int i = 0; i < values.Length; i++) controllers.Add(values[i]);
            }
            HashSet<Entity> selected = mode == RailEtaMode.PathOccupants ? new HashSet<Entity>(controllers) : null;
            EntityManager entityManager = EntityManager;
            for (int i = 0; i < controllers.Count; i++)
            {
                    Entity vehicle = controllers[i];
                    if (!entityManager.HasComponent<CurrentRoute>(vehicle)) continue;
                    Entity line = entityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;
                    if (line == Entity.Null || !entityManager.HasBuffer<RouteWaypoint>(line)) continue;
                    if (!result.LineMaxDwellMinutes.ContainsKey(line))
                    {
                        int dwell = port.LineDwellMinutes(line);
                        result.LineMaxDwellMinutes[line] = dwell > 0 ? dwell : 10;
                    }
                    if (!result.TrackChains.TryGetValue(line, out RailEtaFrozenTrackChain frozenChain))
                    {
                        if (!port.TryReadTrackChain(line, out RailEtaRuntimeTrackChainFact chain) || chain?.Atoms == null) return null;
                        var atoms = new RailEtaFrozenTrackAtom[chain.Atoms.Length];
                        for (int atomIndex = 0; atomIndex < atoms.Length; atomIndex++)
                        {
                            RailEtaRuntimeTrackAtomFact atom = chain.Atoms[atomIndex];
                            atoms[atomIndex] = new RailEtaFrozenTrackAtom { Ordinal = atomIndex, PhysicalLane = atom.PhysicalLane,
                                PreviousTarget = atom.PreviousTarget, NextTarget = atom.NextTarget, Start = atom.Start, End = atom.End,
                                SourceFlags = atom.SourceFlags, AtomClass = atom.AtomClass, Direction = atom.Direction };
                        }
                        frozenChain = new RailEtaFrozenTrackChain { Line = line, Signature = chain.Signature, Atoms = atoms };
                        result.TrackChains.Add(line, frozenChain);
                    }
                    if (port.TryReadOriginScheduledHold(vehicle, frame, out uint earliestReleaseFrame))
                    {
                        result.ControlledHolds[vehicle] = new RailControlledHoldSnapshot
                        {
                            Kind = RailControlledHoldKind.OriginScheduled,
                            EarliestReleaseFrame = earliestReleaseFrame,
                            ReasonCode = "request-frame-origin-scheduled"
                        };
                        continue;
                    }
                    if (!port.TryReadHold(vehicle, frame, out RailEtaRuntimeHoldFact hold)) continue;
                    if (selected != null && !selected.Contains(hold.ReleaseVehicle)) continue;
                    if (!result.TrackChains.TryGetValue(hold.ReleaseLine, out RailEtaFrozenTrackChain expressChain))
                    {
                        if (!port.TryReadTrackChain(hold.ReleaseLine, out RailEtaRuntimeTrackChainFact liveExpress) || liveExpress?.Atoms == null || liveExpress.Signature != hold.ExpectedChainSignature) return null;
                        var atoms = new RailEtaFrozenTrackAtom[liveExpress.Atoms.Length];
                        for (int atomIndex = 0; atomIndex < atoms.Length; atomIndex++)
                        {
                            RailEtaRuntimeTrackAtomFact atom = liveExpress.Atoms[atomIndex];
                            atoms[atomIndex] = new RailEtaFrozenTrackAtom { Ordinal = atomIndex, PhysicalLane = atom.PhysicalLane, PreviousTarget = atom.PreviousTarget,
                                NextTarget = atom.NextTarget, Start = atom.Start, End = atom.End, SourceFlags = atom.SourceFlags,
                                AtomClass = atom.AtomClass, Direction = atom.Direction };
                        }
                        expressChain = new RailEtaFrozenTrackChain { Line = hold.ReleaseLine, Signature = liveExpress.Signature, Atoms = atoms };
                        result.TrackChains.Add(hold.ReleaseLine, expressChain);
                    }
                    if (expressChain.Signature != hold.ExpectedChainSignature || expressChain.Atoms.Length == 0
                        || hold.IntervalStartAtomIndex < 0 || hold.IntervalEndAtomIndexExclusive > expressChain.Atoms.Length
                        || hold.IntervalStartAtomIndex >= hold.IntervalEndAtomIndexExclusive) return null;
                    int intervalLength = hold.IntervalEndAtomIndexExclusive - hold.IntervalStartAtomIndex;
                    float coordinate = Unity.Mathematics.math.clamp(hold.ReleaseCoordinate, 0f, intervalLength - 0.0001f);
                    int relativeAtomIndex = Unity.Mathematics.math.min((int)Unity.Mathematics.math.floor(coordinate), intervalLength - 1);
                    int releaseAtomIndex = hold.IntervalStartAtomIndex + relativeAtomIndex;
                    RailEtaFrozenTrackAtom releaseAtom = expressChain.Atoms[releaseAtomIndex];
                    if (releaseAtom.PhysicalLane == Entity.Null) return null;
                    float atomFraction = coordinate - relativeAtomIndex;
                    result.ControlledHolds[vehicle] = new RailControlledHoldSnapshot { Kind = RailControlledHoldKind.BypassYield,
                        ReleaseVehicleId = new RailVehicleId(RailEtaEntityId.Pack(hold.ReleaseVehicle)), ReleaseLaneId = new RailLaneId(RailEtaEntityId.Pack(releaseAtom.PhysicalLane)),
                        ReleaseLaneFraction = Unity.Mathematics.math.lerp(releaseAtom.Start, releaseAtom.End, atomFraction), ReleaseDirection = releaseAtom.Direction,
                        TrackModelSignature = expressChain.Signature, ReasonCode = "request-frame-bypass-episode" };
            }
            return result;
        }

        private void StartPathRequests(RailEtaService service, RailEtaScopeResult scope)
        {
            CancelPathRequests();
            uint elapsed = unchecked(m_Simulation.frameIndex - m_RequestStartFrame);
            if (elapsed >= 512u)
            {
                FailRequests(service, scope.Requests, RailEtaFailure.FuturePathfindFailed, "Route path expansion exceeded the 512-frame request budget.");
                scope.Dispose();
                FinishBatch();
                return;
            }
            var requestsByKey = new Dictionary<string, PendingPathRequest>(StringComparer.Ordinal);
            for (int i = 0; i < scope.MissingSegments.Count; i++)
            {
                RailEtaMissingRouteSegment segment = scope.MissingSegments[i];
                RailEtaVehicleIndexRow representative = default;
                bool found = segment.IsVehicleTarget != 0 && scope.Index.Vehicles.TryGetValue(segment.Controller, out representative);
                if (!found) foreach (KeyValuePair<Entity, RailEtaVehicleIndexRow> pair in scope.Index.Vehicles)
                {
                    if (pair.Value.Route != segment.Line) continue;
                    representative = pair.Value;
                    if (pair.Value.IsPassenger != 0) { found = true; break; }
                    found = true;
                }
                Entity from = segment.IsVehicleTarget != 0 ? segment.Controller : segment.FromWaypoint;
                Entity to = segment.IsVehicleTarget != 0 ? segment.Target : segment.ToWaypoint;
                if (!found || from == Entity.Null || to == Entity.Null || representative.TrackTypes == 0)
                {
                    HandlePathFailure(service, scope, segment, "request", "Missing route segment has no authoritative waypoint or train pathfind parameters.");
                    if (scope.Requests == null || scope.Requests.Count == 0) { scope.Dispose(); FinishBatch(); return; }
                    continue;
                }
                string key = segment.IsVehicleTarget != 0
                    ? "v:" + segment.Controller.Index + ":" + segment.Target.Index + ":" + representative.TrackTypes
                    : "l:" + segment.Line.Index + ":" + segment.SegmentIndex + ":" + representative.TrackTypes + ":" + segment.ChainSignature;
                if (requestsByKey.TryGetValue(key, out PendingPathRequest existing))
                {
                    MergeConsumers(existing.Segment, segment);
                    continue;
                }
                PathfindParameters parameters = new PathfindParameters
                {
                    m_MaxSpeed = representative.PathfindMaximumSpeed,
                    m_WalkSpeed = 5.555556f,
                    m_Weights = new PathfindWeights(1f, 1f, 1f, 1f),
                    m_Methods = PathMethod.Track,
                    m_IgnoredRules = RuleFlags.ForbidCombustionEngines | RuleFlags.ForbidHeavyTraffic | RuleFlags.ForbidPrivateTraffic | RuleFlags.ForbidSlowTraffic | RuleFlags.AvoidBicycles,
                    m_PathfindFlags = (PathfindFlags)representative.PathfindFlags
                };
                SetupQueueTarget origin = new SetupQueueTarget { m_Type = SetupTargetType.CurrentLocation, m_Methods = PathMethod.Track, m_TrackTypes = (TrackTypes)representative.TrackTypes, m_Entity = from };
                SetupQueueTarget destination = new SetupQueueTarget { m_Type = SetupTargetType.CurrentLocation, m_Methods = PathMethod.Track, m_TrackTypes = (TrackTypes)representative.TrackTypes, m_Entity = to };
                string id = m_RailTravelQuery.Start(parameters, origin, destination, 512u - elapsed, 64, 0);
                var pending = new PendingPathRequest { Id = id, Segment = segment, StartFrame = m_Simulation.frameIndex };
                requestsByKey.Add(key, pending);
                m_PathRequests.Add(pending);
            }
            if (m_PathRequests.Count == 0) { EnqueueMaterialize(service, scope); return; }
            m_Phase = Phase.PathRequests;
        }

        private static void MergeConsumers(RailEtaMissingRouteSegment target, RailEtaMissingRouteSegment source)
        {
            for (int i = 0; i < source.Consumers.Count; i++)
                if (!target.Consumers.Contains(source.Consumers[i])) target.Consumers.Add(source.Consumers[i]);
        }

        private void PollPathRequests(RailEtaService service)
        {
            RailEtaScopeResult scope = m_Scope;
            if (scope == null) { FinishBatch(); return; }
            if (!IsCurrentBatchService(service) || service.Generation != m_Generation) { CancelPathRequests(); scope.Dispose(); CancelBatch(service); return; }
            if (unchecked(m_Simulation.frameIndex - m_RequestStartFrame) >= 512u)
            {
                CancelPathRequests();
                FailRequests(service, scope.Requests, RailEtaFailure.FuturePathfindFailed, "Route path expansion exceeded the 512-frame request budget.");
                scope.Dispose(); FinishBatch(); return;
            }
            for (int i = m_PathRequests.Count - 1; i >= 0; i--)
            {
                PendingPathRequest pending = m_PathRequests[i];
                if (!m_RailTravelQuery.TryGetResult(pending.Id, out RailTravel.QueryResult result)) continue;
                if (String.Equals(result.State, "pending", StringComparison.Ordinal)) continue;
                if (!result.Success || result.Path == null)
                {
                    string reason = String.IsNullOrEmpty(result.Error) ? "Vanilla rail pathfinder failed to expand a route segment." : result.Error;
                    m_PathRequests.RemoveAt(i);
                    HandlePathFailure(service, scope, pending.Segment, "pathfind", reason);
                    if (scope.Requests == null || scope.Requests.Count == 0)
                    {
                        CancelPathRequests();
                        scope.Dispose(); FinishBatch(); return;
                    }
                    continue;
                }
                Entity expectedDestination = pending.Segment.IsVehicleTarget != 0 ? pending.Segment.Target : pending.Segment.ToWaypoint;
                if (expectedDestination == Entity.Null || result.Information.m_Destination != expectedDestination)
                {
                    m_PathRequests.RemoveAt(i);
                    HandlePathFailure(service, scope, pending.Segment, "destination", "Vanilla rail pathfinder result did not reach the frozen target.");
                    if (scope.Requests == null || scope.Requests.Count == 0)
                    {
                        CancelPathRequests();
                        scope.Dispose(); FinishBatch(); return;
                    }
                    continue;
                }
                m_ResolvedPaths.Add(new ResolvedPathResult
                {
                    Segment = pending.Segment,
                    Path = result.Path,
                    DelayFrames = unchecked(m_Simulation.frameIndex - pending.StartFrame)
                });
                m_PathRequests.RemoveAt(i);
            }
            if (m_PathRequests.Count != 0) return;
            RebuildScopeAfterPaths(service, scope, m_ResolvedPaths.ToArray());
        }

        private void HandlePathFailure(RailEtaService service, RailEtaScopeResult scope, RailEtaMissingRouteSegment segment, string stage, string reason)
        {
            scope.FailedSegments.Add(new RailEtaFailedSegmentKey(segment));
            for (int consumerIndex = 0; consumerIndex < segment.Consumers.Count; consumerIndex++)
            {
                Entity consumer = segment.Consumers[consumerIndex];
                bool targetRequired = false;
                bool requestTarget = false;
                for (int requestIndex = scope.Requests.Count - 1; requestIndex >= 0; requestIndex--)
                {
                    RailEtaBatchRequest request = scope.Requests[requestIndex];
                    if (RailEtaEntityId.ToEntity(request.Descriptor) != consumer) continue;
                    requestTarget = true;
                    if (segment.IsVehicleTarget == 0) continue;
                    targetRequired = true;
                    RailEtaFailure failure = stage == "pathfind" || stage == "destination" ? RailEtaFailure.FuturePathfindFailed : RailEtaFailure.RouteGeometryMissing;
                    service.Transition(request.Ticket, RailEtaRequestState.Failed, m_Simulation.frameIndex, scope.BatchId,
                        scope.Generation, failure, reason);
                    scope.Requests.RemoveAt(requestIndex);
                }
                if (targetRequired) continue;
                if (requestTarget) continue;
                scope.VehiclePathFailures.Add(new RailEtaVehiclePathFailure
                {
                    Vehicle = consumer,
                    Line = segment.Line,
                    SegmentIndex = segment.SegmentIndex,
                    Frame = m_Simulation.frameIndex,
                    Stage = stage,
                    Reason = reason
                });
                ExcludeFailedVehicle(scope, consumer);
                for (int requestIndex = 0; requestIndex < scope.Requests.Count; requestIndex++)
                    service.MarkIncomplete(scope.Requests[requestIndex].Ticket);
            }
        }

        private static void ExcludeFailedVehicle(RailEtaScopeResult scope, Entity vehicle)
        {
            if (vehicle == Entity.Null || !scope.ExcludedVehicles.Add(vehicle) || scope.Staging == null) return;
            NativeArray<RailEtaScopedVehicleRow> vehicles = scope.Staging.Vehicles.AsArray();
            for (int i = 0; i < vehicles.Length; i++)
            {
                RailEtaScopedVehicleRow row = vehicles[i];
                if (row.Blocker != vehicle) continue;
                row.Blocker = Entity.Null;
                row.BlockerType = 0;
                row.ExternalBlockerKind = 0;
                vehicles[i] = row;
            }
            NativeArray<RailEtaScopedLaneRow> lanes = scope.Staging.Lanes.AsArray();
            for (int i = 0; i < lanes.Length; i++)
            {
                RailEtaScopedLaneRow lane = lanes[i];
                if (lane.ReservationBlocker == vehicle) { lane.ReservationBlocker = Entity.Null; lane.ReservationExternalKind = 0; }
                if (lane.SignalPetitioner == vehicle) { lane.SignalPetitioner = Entity.Null; lane.SignalPetitionerExternalKind = 0; }
                if (lane.SignalBlocker == vehicle) { lane.SignalBlocker = Entity.Null; lane.SignalBlockerExternalKind = 0; }
                lanes[i] = lane;
            }
        }

        private static bool AppendResolvedPathForWorker(RailEtaScopedStaging staging,
            IReadOnlyDictionary<Entity, List<RailEtaScopedLaneRow>> frozenFactsByLane,
            RailEtaMissingRouteSegment missing, RailTravel.Path path, uint pathfindDelayFrames, out string detail)
        {
            detail = null;
            if (missing.IsVehicleTarget == 0 && missing.NeedsGeometry == 0)
            {
                NativeArray<RailEtaRouteSegmentRow> existingSegments = staging.Segments.AsArray();
                for (int i = 0; i < existingSegments.Length; i++)
                {
                    RailEtaRouteSegmentRow value = existingSegments[i];
                    if (value.Line != missing.Line || value.SegmentIndex != missing.SegmentIndex) continue;
                    value.PathfindDelayFrames = pathfindDelayFrames;
                    value.PathfindDelayKnown = 1;
                    existingSegments[i] = value;
                    return true;
                }
                detail = "resolved route segment metadata is missing";
                return false;
            }
            for (int i = 0; i < path.Segments.Length; i++)
            {
                RailTravel.Segment segment = path.Segments[i];
                bool foundBase = false;
                var copies = new List<RailEtaScopedLaneRow>();
                if (!frozenFactsByLane.TryGetValue(segment.LaneEntity, out List<RailEtaScopedLaneRow> facts))
                {
                    detail = "Query result lane has no request-frame geometry/reservation/signal fact.";
                    return false;
                }
                for (int f = 0; f < facts.Count; f++)
                {
                    RailEtaScopedLaneRow fact = facts[f];
                    fact.Controller = missing.IsVehicleTarget != 0 ? missing.Controller : Entity.Null;
                    fact.Line = missing.IsVehicleTarget != 0 ? Entity.Null : missing.Line;
                    fact.RouteSegmentIndex = missing.SegmentIndex;
                    fact.Sequence = i;
                    if (fact.Source == 6)
                    {
                        if (fact.PathPhysicalLane == Entity.Null)
                        {
                            detail = "Query result lane has no request-frame canonical path-lane fact.";
                            return false;
                        }
                        foundBase = true;
                        fact.Source = missing.IsVehicleTarget != 0 ? (byte)7 : (byte)5;
                        fact.CurveStart = segment.TargetDelta.x;
                        fact.CurveEnd = segment.TargetDelta.y;
                        fact.PathFlags = (uint)segment.PathFlags;
                        // Query Segment is authoritative for connection kind/speed/flags; do not keep a bare TrackLane default of 0.
                        if (segment.IsConnectionLane)
                        {
                            fact.IsConnectionLane = 1;
                            fact.SpeedLimit = segment.SpeedLimit;
                            fact.TrackFlags = 0;
                            fact.Curviness = 0f;
                            fact.SharedPhysicalLane = Entity.Null;
                            fact.ParticipatesInSharedCorridor = 0;
                        }
                        else if (fact.IsConnectionLane != 0)
                        {
                            detail = "Query result primary segment conflicts with frozen connection-lane fact.";
                            return false;
                        }
                        else if (segment.SpeedLimit > 0f && fact.SpeedLimit <= 0f)
                        {
                            fact.SpeedLimit = segment.SpeedLimit;
                        }
                    }
                    copies.Add(fact);
                }
                // Flags-only helpers without TrackLane/ConnectionLane/EdgeLane never enter global lane query (source 6).
                // PathQuery already refuses non-noise elements it cannot project; if a Query segment has no frozen fact, fail explicitly.
                if (!foundBase) { detail = "Query result lane has no request-frame geometry/reservation/signal fact."; return false; }
                if (staging.Lanes.Length + copies.Count > RailEtaLimits.MaxFrozenLaneFacts)
                { detail = "frozen-fact-limit-exceeded"; return false; }
                for (int f = 0; f < copies.Count; f++) staging.Lanes.Add(copies[f]);
                if (missing.IsVehicleTarget == 0)
                {
                    if (staging.Paths.Length >= RailEtaLimits.MaxEvents) { detail = "route-path-limit-exceeded"; return false; }
                    staging.Paths.Add(new RailEtaRoutePathRow { Line = missing.Line, SegmentIndex = missing.SegmentIndex, Sequence = i,
                        Lane = segment.LaneEntity, Start = segment.TargetDelta.x, End = segment.TargetDelta.y, PathFlags = (uint)segment.PathFlags });
                }
            }
            if (missing.IsVehicleTarget != 0)
            {
                NativeArray<RailEtaFrozenNavigationLaneRow> frozenNavigation = staging.NavigationLanes.AsArray();
                for (int i = 0; i < frozenNavigation.Length; i++)
                {
                    RailEtaFrozenNavigationLaneRow lane = frozenNavigation[i];
                    if (lane.Controller != missing.Controller) continue;
                    lane.Controller = Entity.Null;
                    frozenNavigation[i] = lane;
                }
                NativeArray<RailEtaFrozenPathElementRow> frozenPath = staging.PathElements.AsArray();
                for (int i = 0; i < frozenPath.Length; i++)
                {
                    RailEtaFrozenPathElementRow element = frozenPath[i];
                    if (element.Controller != missing.Controller) continue;
                    element.Controller = Entity.Null;
                    frozenPath[i] = element;
                }
                for (int i = 0; i < path.Segments.Length; i++)
                {
                    RailTravel.Segment segment = path.Segments[i];
                    staging.PathElements.Add(new RailEtaFrozenPathElementRow
                    {
                        ControllerOrdinal = -1,
                        ElementOrdinal = i,
                        Controller = missing.Controller,
                        Target = segment.LaneEntity,
                        TargetDelta = segment.TargetDelta,
                        Flags = (uint)segment.PathFlags,
                        TargetExists = 1
                    });
                }
                NativeArray<RailEtaScopedVehicleRow> scopedVehicles = staging.Vehicles.AsArray();
                for (int i = 0; i < scopedVehicles.Length; i++)
                {
                    RailEtaScopedVehicleRow vehicle = scopedVehicles[i];
                    if (vehicle.Controller != missing.Controller) continue;
                    vehicle.PathState = 0;
                    vehicle.PathElementIndex = 0;
                    vehicle.PathDestination = missing.Target;
                    vehicle.HasPathInformation = 1;
                    if (path.Segments.Length > 0)
                    {
                        RailTravel.Segment first = path.Segments[0];
                        vehicle.FrontLane = first.LaneEntity;
                        vehicle.RearLane = first.LaneEntity;
                        vehicle.FrontCacheLane = first.LaneEntity;
                        vehicle.RearCacheLane = first.LaneEntity;
                        vehicle.FrontCurveStart = first.TargetDelta.x;
                        vehicle.FrontCurveEnd = first.TargetDelta.x;
                        vehicle.RearCurvePosition = first.TargetDelta.x;
                        vehicle.FrontCurvePosition = new Unity.Mathematics.float4(first.TargetDelta.x);
                        vehicle.RearCurvePositions = new Unity.Mathematics.float4(first.TargetDelta.x);
                        vehicle.FrontCacheCurvePosition = new Unity.Mathematics.float2(first.TargetDelta.x);
                        vehicle.RearCacheCurvePosition = new Unity.Mathematics.float2(first.TargetDelta.x);
                    }
                    scopedVehicles[i] = vehicle;
                    break;
                }
                return true;
            }
            NativeArray<RailEtaRouteSegmentRow> segments = staging.Segments.AsArray();
            for (int i = 0; i < segments.Length; i++)
            {
                RailEtaRouteSegmentRow value = segments[i];
                if (value.Line != missing.Line || value.SegmentIndex != missing.SegmentIndex) continue;
                value.GeometryAvailable = 1;
                value.PathState = 0;
                value.PathfindDelayFrames = pathfindDelayFrames;
                value.PathfindDelayKnown = 1;
                segments[i] = value;
                break;
            }
            return true;
        }

        private static Dictionary<Entity, List<RailEtaScopedLaneRow>> BuildFrozenLaneFactIndex(RailEtaScopedStaging staging)
        {
            var result = new Dictionary<Entity, List<RailEtaScopedLaneRow>>();
            NativeArray<RailEtaScopedLaneRow> facts = staging.Lanes.AsArray();
            for (int i = 0; i < facts.Length; i++)
            {
                RailEtaScopedLaneRow fact = facts[i];
                if (fact.Lane == Entity.Null || (fact.Source != 6 && fact.Source != 3)) continue;
                if (fact.Source == 3 && (fact.Line != Entity.Null || fact.Controller != Entity.Null)) continue;
                if (!result.TryGetValue(fact.Lane, out List<RailEtaScopedLaneRow> laneFacts))
                    result[fact.Lane] = laneFacts = new List<RailEtaScopedLaneRow>();
                laneFacts.Add(fact);
            }
            return result;
        }

        private void RebuildScopeAfterPaths(RailEtaService service, RailEtaScopeResult oldScope, ResolvedPathResult[] resolvedPaths)
        {
            RailEtaScopeWork work = new RailEtaScopeWork
            {
                Mode = oldScope.Mode,
                BatchId = oldScope.BatchId, IndexOriginFrame = oldScope.IndexOriginFrame, Generation = oldScope.Generation,
                Requests = oldScope.Requests, Staging = oldScope.Staging, RequestFrameFacts = oldScope.RequestFrameFacts,
                ExcludedVehicles = new HashSet<Entity>(oldScope.ExcludedVehicles),
                VehiclePathFailures = new List<RailEtaVehiclePathFailure>(oldScope.VehiclePathFailures),
                TicketFailures = oldScope.TicketFailures != null ? new List<RailEtaTicketFailure>(oldScope.TicketFailures) : new List<RailEtaTicketFailure>(),
                FailedSegments = new HashSet<RailEtaFailedSegmentKey>(oldScope.FailedSegments)
            };
            oldScope.Staging = null;
            oldScope.Dispose();
            m_Scope = null;
            if (!service.Worker.TryEnqueue(() =>
            {
                RailEtaScopeResult result;
                try
                {
                    using (s_WorkerScope.Auto())
                    {
                        IReadOnlyDictionary<Entity, List<RailEtaScopedLaneRow>> frozenFactsByLane = BuildFrozenLaneFactIndex(work.Staging);
                        for (int i = 0; i < resolvedPaths.Length; i++)
                        {
                            if (AppendResolvedPathForWorker(work.Staging, frozenFactsByLane, resolvedPaths[i].Segment,
                                resolvedPaths[i].Path, resolvedPaths[i].DelayFrames, out string appendFailure)) continue;
                            HandleResolvedPathAppendFailure(work, resolvedPaths[i].Segment, appendFailure);
                        }
                        result = new RailEtaScopeBuilder().Build(work);
                    }
                }
                catch (Exception ex) { result = new RailEtaScopeResult { Mode = work.Mode, BatchId = work.BatchId, IndexOriginFrame = work.IndexOriginFrame, Generation = work.Generation, Requests = work.Requests, Staging = work.Staging, Failure = RailEtaFailure.InvalidResult, Detail = ex.GetType().Name + ": " + ex.Message }; }
                lock (m_CallbackGate) { if (m_ShuttingDown) result.Dispose(); else m_ScopeResults.Enqueue(result); }
            }))
            {
                work.Staging.Dispose();
                FailBatch(service, RailEtaFailure.Busy, "Rail ETA worker queue is full.");
                return;
            }
            m_Phase = Phase.ScopeWorker;
        }

        private static void HandleResolvedPathAppendFailure(RailEtaScopeWork work, RailEtaMissingRouteSegment segment, string reason)
        {
            work.FailedSegments.Add(new RailEtaFailedSegmentKey(segment));
            for (int consumerIndex = 0; consumerIndex < segment.Consumers.Count; consumerIndex++)
            {
                Entity consumer = segment.Consumers[consumerIndex];
                bool requestTarget = false;
                for (int requestIndex = work.Requests.Count - 1; requestIndex >= 0; requestIndex--)
                {
                    RailEtaBatchRequest request = work.Requests[requestIndex];
                    if (RailEtaEntityId.ToEntity(request.Descriptor) != consumer) continue;
                    requestTarget = true;
                    if (segment.IsVehicleTarget == 0) continue;
                    work.TicketFailures.Add(new RailEtaTicketFailure { Ticket = request.Ticket, Failure = RailEtaFailure.RouteGeometryMissing, Detail = reason });
                    work.Requests.RemoveAt(requestIndex);
                }
                if (requestTarget) continue;
                if (work.ExcludedVehicles == null) work.ExcludedVehicles = new HashSet<Entity>();
                if (!work.ExcludedVehicles.Add(consumer)) continue;
                work.VehiclePathFailures.Add(new RailEtaVehiclePathFailure { Vehicle = consumer, Line = segment.Line, SegmentIndex = segment.SegmentIndex,
                    Frame = work.IndexOriginFrame, Stage = "materialize", Reason = reason });
                RemoveFailedVehicleReferences(work.Staging, consumer);
            }
        }

        private static void RemoveFailedVehicleReferences(RailEtaScopedStaging staging, Entity vehicle)
        {
            NativeArray<RailEtaScopedVehicleRow> vehicles = staging.Vehicles.AsArray();
            for (int i = 0; i < vehicles.Length; i++)
            {
                RailEtaScopedVehicleRow row = vehicles[i];
                if (row.Blocker != vehicle) continue;
                row.Blocker = Entity.Null; row.BlockerType = 0; row.ExternalBlockerKind = 0; vehicles[i] = row;
            }
            NativeArray<RailEtaScopedLaneRow> lanes = staging.Lanes.AsArray();
            for (int i = 0; i < lanes.Length; i++)
            {
                RailEtaScopedLaneRow lane = lanes[i];
                if (lane.ReservationBlocker == vehicle) { lane.ReservationBlocker = Entity.Null; lane.ReservationExternalKind = 0; }
                if (lane.SignalPetitioner == vehicle) { lane.SignalPetitioner = Entity.Null; lane.SignalPetitionerExternalKind = 0; }
                if (lane.SignalBlocker == vehicle) { lane.SignalBlocker = Entity.Null; lane.SignalBlockerExternalKind = 0; }
                lanes[i] = lane;
            }
        }

        private void CancelPathRequests()
        {
            for (int i = 0; i < m_PathRequests.Count; i++) m_RailTravelQuery?.Cancel(m_PathRequests[i].Id);
            m_PathRequests.Clear();
            m_ResolvedPaths.Clear();
        }

        private List<RailEtaTicketPrediction> PredictBatch(RailEtaService service, RailEtaPredictionWork work, long predictorGeneration)
        {
            var predictions = new List<RailEtaTicketPrediction>(work.Scope.Requests.Count);
            RailPredictionSolver predictor = service.Predictor;
            foreach (RailEtaBatchRequest batchRequest in work.Scope.Requests)
            {
                    service.Transition(batchRequest.Ticket, RailEtaRequestState.Predicting, service.LastObservedFrame, work.Scope.BatchId, work.Scope.Generation);
                    RailEtaRequest request = new RailEtaRequest
                    {
                        RequestId = batchRequest.Ticket.Value.ToString(),
                        Mode = work.Scope.Mode,
                        VehicleId = new RailVehicleId(RailEtaEntityId.Pack(RailEtaEntityId.ToEntity(batchRequest.Descriptor))),
                        TargetCheckpointId = new RailCheckpointId(batchRequest.Descriptor.TargetCheckpointId != 0
                            ? batchRequest.Descriptor.TargetCheckpointId
                            : RailEtaEntityId.Pack(batchRequest.ExpectedTarget)),
                        ExpectedTarget = new RailEntityIdentity { Index = batchRequest.ExpectedTarget.Index, Version = batchRequest.ExpectedTarget.Version }
                    };
                    service.StoreRequest(batchRequest.Ticket, request, work.Scope.Generation);
                    var workspace = new RailEtaWorkspace
                    {
                        MaxEvents = RailEtaLimits.MaxEvents, MaxTraceEvents = RailEtaLimits.MaxTraceEvents,
                        MaxDiagnostics = RailEtaLimits.MaxDiagnostics, MaxCheckpoints = RailEtaLimits.MaxCheckpoints,
                        MaxVehicles = RailEtaLimits.MaxScopeVehicles, MaxResources = RailEtaLimits.MaxScopeResources,
                        MaxBlockerDepth = RailEtaLimits.MaxBlockerDepth
                    };
                    long predictStart = Stopwatch.GetTimestamp();
                    RailEtaPrediction prediction = null;
                    string predictorFailureDetail = string.Empty;
                    try
                    {
                        prediction = predictor.Predict(work.FrozenWorld, work.Snapshot, request, workspace, new RailEtaCancellation(() =>
                        {
                            if (service.IsDisposed || service.WorkerLost || service.Generation != work.Scope.Generation) return true;
                            return service.TryGetState(batchRequest.Ticket, out RailEtaTicketStatus status) && status.State == RailEtaRequestState.Cancelled;
                        }));
                    }
                    catch (Exception ex)
                    {
                        predictorFailureDetail = "hot predictor threw " + ex.GetType().Name;
                    }
                    AppendPathFailureDiagnostics(prediction, work.Scope.VehiclePathFailures);
                    if (prediction?.Diagnostics != null)
                        for (int diagnosticIndex = 0; diagnosticIndex < prediction.Diagnostics.Length; diagnosticIndex++)
                            if (String.Equals(prediction.Diagnostics[diagnosticIndex]?.Code,
                                "simulation-non-target-failure", StringComparison.Ordinal))
                            {
                                service.MarkIncomplete(batchRequest.Ticket);
                                break;
                            }
                    long predictTicks = Stopwatch.GetTimestamp() - predictStart;
                    service.MarkPredictionFinished(batchRequest.Ticket, work.Scope.Generation);
                    service.Transition(batchRequest.Ticket, RailEtaRequestState.Validating, service.LastObservedFrame, work.Scope.BatchId, work.Scope.Generation);
                    long validationTicks = 0;
                    var timings = new List<RailEtaStageTiming>
                    {
                        RailEtaResultValidator.Timing("index", work.IndexWallTicks, work.Snapshot.Vehicles.Length),
                        RailEtaResultValidator.Timing("scoped", work.ScopedWallTicks, work.Snapshot.Vehicles.Length),
                        RailEtaResultValidator.Timing("materialize", work.MaterializeWallTicks, work.Snapshot.Vehicles.Length),
                        RailEtaResultValidator.Timing("predict", predictTicks, 1)
                    };
                    long validationStart = Stopwatch.GetTimestamp();
                    RailEtaResultValidator.ApplyHostMetadata(prediction, work.Snapshot, request, predictor.Version, predictorGeneration, timings);
                    bool valid = RailEtaResultValidator.TryValidate(prediction, work.Snapshot, request, workspace, out string validationDetail);
                    if (!valid && !String.IsNullOrEmpty(predictorFailureDetail)) validationDetail = predictorFailureDetail;
                    if (valid && prediction != null && prediction.Failure == RailEtaFailure.InvalidResult)
                    {
                        valid = false;
                        validationDetail = String.IsNullOrEmpty(prediction.Reason) ? "predictor-invalid-result" : prediction.Reason;
                    }
                    validationTicks += Stopwatch.GetTimestamp() - validationStart;
                    timings.Add(RailEtaResultValidator.Timing("validate", validationTicks, 1));
                    if (!valid)
                    {
                        prediction = new RailEtaPrediction { RequestId = request.RequestId, Confidence = RailEtaConfidence.Unknown, Failure = RailEtaFailure.InvalidResult, Reason = validationDetail };
                    }
                    RailEtaResultValidator.ApplyHostMetadata(prediction, work.Snapshot, request, predictor.Version, predictorGeneration, timings);
                    if (!RailEtaResultValidator.TryValidate(prediction, work.Snapshot, request, workspace, out string finalValidationDetail))
                    {
                        prediction = new RailEtaPrediction
                        {
                            RequestId = request.RequestId,
                            Confidence = RailEtaConfidence.Unknown,
                            Failure = RailEtaFailure.InvalidResult,
                            Reason = "final-contract-rejected:" + finalValidationDetail
                        };
                        RailEtaResultValidator.ApplyHostMetadata(prediction, work.Snapshot, request, predictor.Version, predictorGeneration, timings);
                        if (!RailEtaResultValidator.TryValidate(prediction, work.Snapshot, request, workspace, out string failureValidationDetail))
                            throw new InvalidOperationException("Controlled invalid ETA result rejected: " + failureValidationDetail);
                    }
                    predictions.Add(new RailEtaTicketPrediction { Ticket = batchRequest.Ticket, VehicleId = request.VehicleId.Value, Prediction = prediction });
            }
            return predictions;
        }

        private static void AppendPathFailureDiagnostics(RailEtaPrediction prediction, List<RailEtaVehiclePathFailure> failures)
        {
            if (prediction == null || failures == null || failures.Count == 0) return;
            var diagnostics = new List<RailEtaDiagnosticRecord>(prediction.Diagnostics ?? Array.Empty<RailEtaDiagnosticRecord>());
            for (int i = 0; i < failures.Count && diagnostics.Count < RailEtaLimits.MaxDiagnostics; i++)
            {
                RailEtaVehiclePathFailure failure = failures[i];
                string message = "stage=" + (failure.Stage ?? string.Empty) + " reason=" + (failure.Reason ?? string.Empty);
                if (message.Length > 512) message = message.Substring(0, 512);
                diagnostics.Add(new RailEtaDiagnosticRecord
                {
                    Code = "path-non-target-failure",
                    Severity = RailEtaDiagnosticSeverity.Warning,
                    Message = message,
                    VehicleId = new RailVehicleId(RailEtaEntityId.Pack(failure.Vehicle)),
                    Frame = failure.Frame
                });
            }
            prediction.Diagnostics = diagnostics.ToArray();
        }

        private void PollMaterialize(RailEtaService service)
        {
            if (!m_MaterializeResults.TryDequeue(out RailEtaMaterializeResult result)) return;
            RailEtaScopeResult scope = result.Scope;
            if (!IsCurrentBatchService(service) || scope.Generation != service.Generation || scope.Generation != m_Generation) { scope.Dispose(); CancelBatch(service); return; }
            if (result.Snapshot == null)
            {
                FailRequests(service, scope.Requests, result.Failure, result.Detail);
                scope.Dispose(); FinishBatch(); return;
            }
#if RT_DEBUG_TOOLS
            if (RailEtaDebugSettings.DetailedLogsEnabled && !string.IsNullOrEmpty(result.DiagnosticSummary))
                Mod.log.Info("[RailEtaSnapshot] " + result.DiagnosticSummary);
#endif
            using (s_Publish.Auto())
            {
                foreach (RailEtaBatchRequest request in scope.Requests) service.Publish(request.Ticket, result.Snapshot, scope.Generation);
            }
            RailEtaPredictionWork predictionWork = new RailEtaPredictionWork
            {
                Scope = scope,
                Snapshot = result.Snapshot,
                FrozenWorld = result.FrozenWorld,
                IndexWallTicks = m_IndexWallTicks,
                ScopedWallTicks = 0,
                MaterializeWallTicks = result.MaterializeWallTicks
            };
            foreach (RailEtaBatchRequest request in scope.Requests)
                service.Transition(request.Ticket, RailEtaRequestState.PredictorQueued, service.LastObservedFrame, scope.BatchId, scope.Generation);
            long predictorGeneration = m_PredictorGeneration;
            if (!service.Worker.TryEnqueue(() =>
            {
                RailEtaPredictionResult predictionResult;
                try
                {
                    predictionResult = new RailEtaPredictionResult
                    {
                        Scope = predictionWork.Scope,
                        FrozenWorld = predictionWork.FrozenWorld,
                        Predictions = PredictBatch(service, predictionWork, predictorGeneration)
                    };
                }
                catch (Exception ex)
                {
                    predictionResult = new RailEtaPredictionResult { Scope = predictionWork.Scope, Failure = RailEtaFailure.InvalidResult, Detail = ex.GetType().Name + ": " + ex.Message };
                }
                lock (m_CallbackGate) { if (m_ShuttingDown) predictionResult.Scope.Dispose(); else m_PredictionResults.Enqueue(predictionResult); }
            }))
            {
                scope.Dispose();
                FailBatch(service, service.WorkerLost ? RailEtaFailure.WorkerLost : RailEtaFailure.Busy, service.WorkerLost ? "Rail ETA worker is lost." : "Rail ETA worker queue is full.");
                return;
            }
            m_Phase = Phase.PredictorWorker;
        }

        private void PollPrediction(RailEtaService service)
        {
            if (!m_PredictionResults.TryDequeue(out RailEtaPredictionResult result)) return;
            RailEtaScopeResult scope = result.Scope;
            if (scope == null || !IsCurrentBatchService(service) || scope.Generation != service.Generation || scope.Generation != m_Generation)
            {
                scope?.Dispose();
                CancelBatch(service);
                return;
            }
            if (result.Failure != RailEtaFailure.None)
            {
                FailRequests(service, scope.Requests, result.Failure, result.Detail);
                scope.Dispose();
                FinishBatch();
                return;
            }
            if (result.Predictions == null || result.Predictions.Count != scope.Requests.Count)
            {
                FailRequests(service, scope.Requests, RailEtaFailure.InvalidResult, "Prediction result count does not match the request batch.");
                scope.Dispose();
                FinishBatch();
                return;
            }
            using (s_Publish.Auto())
            {
                foreach (RailEtaTicketPrediction prediction in result.Predictions)
                {
#if RT_DEBUG_TOOLS
                    if (RailEtaDebugSettings.HeavyExportsEnabled)
                        service.StoreReplayWorld(prediction.Ticket, result.FrozenWorld);
#endif
                    service.PublishPrediction(prediction.Ticket, prediction.Prediction, scope.Generation);
#if RT_DEBUG_TOOLS
                    if (RailEtaDebugSettings.DetailedLogsEnabled)
                        LogPrediction(service, prediction);
                    if (RailEtaDebugSettings.HeavyExportsEnabled && prediction.Prediction != null
                        && prediction.Prediction.Failure == RailEtaFailure.None)
                        RailEtaComparisonSystem.RequestStart(service, prediction.Ticket);
#endif
                }
            }
            scope.Dispose();
            FinishBatch();
        }

#if RT_DEBUG_TOOLS
        private static void LogPrediction(RailEtaService service, RailEtaTicketPrediction ticketPrediction)
        {
            service.TryGetSnapshot(ticketPrediction.Ticket, out RailEtaWorldSnapshot snapshot);
            service.TryGetState(ticketPrediction.Ticket, out RailEtaTicketStatus status);
            RailEtaPrediction prediction = ticketPrediction.Prediction;
            RailEtaInputScale scale = prediction?.InputScale ?? new RailEtaInputScale();
            var stages = new StringBuilder(96);
            RailEtaStageTiming[] timings = prediction?.StageTimings ?? Array.Empty<RailEtaStageTiming>();
            for (int i = 0; i < timings.Length; i++)
            {
                if (i > 0) stages.Append('|');
                stages.Append(timings[i]?.Code ?? "?").Append('=').Append(timings[i]?.WallMilliseconds.ToString("F2") ?? "0");
            }
            Mod.log.Info("[RailEta] ticket=" + ticketPrediction.Ticket.Value
                + " source=" + (prediction?.PredictorSource ?? "")
                + " build=" + (prediction?.PredictorBuildId ?? "")
                + " generation=" + (prediction?.PredictorGeneration ?? 0)
                + " vehicle=" + ticketPrediction.VehicleId
                + " requestFrame=" + (status?.RequestFrame ?? 0)
                + " originFrame=" + (snapshot?.OriginFrame ?? 0)
                + " snapshotReadyFrame=" + (status?.SnapshotReadyFrame ?? 0)
                + " predictorQueuedFrame=" + (status?.PredictorQueuedFrame ?? 0)
                + " finishFrame=" + (status?.PredictionFinishedFrame ?? 0)
                + " publishFrame=" + (status?.PublishedFrame ?? 0)
                + " state=" + (prediction != null && prediction.Failure == RailEtaFailure.None ? "Completed" : "Failed")
                + " confidence=" + (prediction?.Confidence.ToString() ?? "Unknown")
                + " arrival=" + (prediction?.PredictedArrivalFrame ?? 0)
                + " elapsed=" + (prediction == null || snapshot == null ? 0 : unchecked(prediction.PredictedArrivalFrame - snapshot.OriginFrame))
                + " vehicles=" + scale.VehicleCount
                + " pathSegments=" + scale.PathSegmentCount
                + " blockers=" + scale.BlockerCount
                + " reservations=" + scale.ReservationCount
                + " signals=" + scale.SignalCount
                + " occupancies=" + scale.OccupancyCount
                + " checkpoints=" + scale.CheckpointCount
                + " resources=" + scale.ResourceCount
                + " events=" + (prediction?.EventCount ?? 0)
                + " workerMs=" + (prediction?.WorkerMilliseconds ?? 0).ToString("F2")
                + " stages=" + stages
                + " failure=" + (prediction?.Failure.ToString() ?? RailEtaFailure.InvalidResult.ToString()));
        }
#endif

        private CollectRailSnapshotJob CreateSnapshotJob(RailEtaScopedStaging staging) => new CollectRailSnapshotJob
        {
            Mode = m_Mode,
            Controllers = staging.Controllers.AsDeferredJobArray(), RouteLines = staging.RouteLines.AsDeferredJobArray(), RailLanes = staging.RailLanes.AsDeferredJobArray(), TrafficLightControllers = staging.TrafficLightControllers.AsDeferredJobArray(), EntityLookup = GetEntityStorageInfoLookup(), UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>(), TargetData = GetComponentLookup<Target>(true), PathOwnerData = GetComponentLookup<PathOwner>(true), PathInformationData = GetComponentLookup<PathInformation>(true), BlockerData = GetComponentLookup<Blocker>(true),
            ControllerData = GetComponentLookup<Controller>(true), CurrentRouteData = GetComponentLookup<CurrentRoute>(true), WaypointData = GetComponentLookup<Waypoint>(true), PublicTransportData = GetComponentLookup<Game.Vehicles.PublicTransport>(true), CargoTransportData = GetComponentLookup<Game.Vehicles.CargoTransport>(true), NavigationData = GetComponentLookup<TrainNavigation>(true), CurrentLaneData = GetComponentLookup<TrainCurrentLane>(true), PrefabRefData = GetComponentLookup<PrefabRef>(true), PrefabTrainData = GetComponentLookup<TrainData>(true), TrainComponentData = GetComponentLookup<Train>(true),
            TransformData = GetComponentLookup<Game.Objects.Transform>(true), MovingData = GetComponentLookup<Game.Objects.Moving>(true), OdometerData = GetComponentLookup<Odometer>(true),
            PrefabGeometryData = GetComponentLookup<ObjectGeometryData>(true),
            TrackLaneData = GetComponentLookup<Game.Net.TrackLane>(true), ConnectionLaneData = GetComponentLookup<Game.Net.ConnectionLane>(true), CurveData = GetComponentLookup<Curve>(true), ReservationData = GetComponentLookup<LaneReservation>(true), SignalData = GetComponentLookup<LaneSignal>(true), TrafficLightsData = GetComponentLookup<TrafficLights>(true), SubLaneData = GetBufferLookup<Game.Net.SubLane>(true),
            EdgeLaneData = GetComponentLookup<Game.Net.EdgeLane>(true),
            CarData = GetComponentLookup<Game.Vehicles.Car>(true), CreatureData = GetComponentLookup<Game.Creatures.Creature>(true), LayoutData = GetBufferLookup<LayoutElement>(true), NavigationLaneData = GetBufferLookup<TrainNavigationLane>(true), PathElementData = GetBufferLookup<PathElement>(true), RouteSegmentData = GetBufferLookup<RouteSegment>(true), RouteWaypointData = GetBufferLookup<RouteWaypoint>(true), RouteLaneData = GetComponentLookup<RouteLane>(true), ConnectedData = GetComponentLookup<Connected>(true), BoardingVehicleData = GetComponentLookup<BoardingVehicle>(true), TransportLinePrefabData = GetComponentLookup<TransportLineData>(true), LaneOverlapData = GetBufferLookup<LaneOverlap>(true), LaneObjectData = GetBufferLookup<LaneObject>(true),
            Vehicles = staging.Vehicles, Lanes = staging.Lanes, Units = staging.Units, NavigationLanes = staging.NavigationLanes, PathElements = staging.PathElements, Occupancies = staging.Occupancies,
            Lines = staging.Lines, Segments = staging.Segments, Paths = staging.Paths,
            SignalControllerByLane = staging.SignalControllerByLane, SignalPeers = staging.SignalPeers,
            Overflow = staging.Overflow
        };

        private void FailBatch(RailEtaService service, RailEtaFailure failure, string detail)
        {
            if (failure == RailEtaFailure.WorkerLost || service.WorkerLost)
            {
                service.MarkWorkerLost(detail);
                FinishBatch();
                return;
            }
            FailRequests(service, m_Requests, failure, detail);
            FinishBatch();
        }
        private void FailRequests(RailEtaService service, List<RailEtaBatchRequest> requests, RailEtaFailure failure, string detail) { if (requests == null) return; foreach (RailEtaBatchRequest request in requests) service.Transition(request.Ticket, RailEtaRequestState.Failed, m_Simulation.frameIndex, m_BatchId, m_Generation, failure, detail); }
        private void CancelBatch(RailEtaService service) { if (m_Requests != null) foreach (RailEtaBatchRequest request in m_Requests) service.Cancel(request.Ticket); FinishBatch(); }
        private bool IsCurrentBatchService(RailEtaService service) => ReferenceEquals(service, m_BatchService) && ReferenceEquals(RailEtaService.Current, service) && service.InstanceId == m_BatchServiceId && !service.IsDisposed;
        private void FinishBatch() { CancelPathRequests(); m_TheoryPaths?.Cancel(); m_Phase = Phase.Idle; m_Mode = RailEtaMode.Full; m_Requests = null; m_Scope = null; m_Staging = null; m_FrozenRuntimeFacts = null; m_RequestStartFrame = 0; m_Handle = default; m_BatchService = null; m_BatchServiceId = 0; }

        protected override void OnDestroy()
        {
            lock (m_CallbackGate)
            {
                m_ShuttingDown = true;
                while (m_ScopeResults.TryDequeue(out RailEtaScopeResult scopeResult)) scopeResult.Dispose();
                while (m_MaterializeResults.TryDequeue(out RailEtaMaterializeResult materializeResult)) materializeResult.Scope?.Dispose();
                while (m_PredictionResults.TryDequeue(out RailEtaPredictionResult predictionResult)) predictionResult.Scope?.Dispose();
            }
            JobHandle cleanup = m_Handle;
            if (m_Staging != null) { cleanup = m_Staging.Dispose(cleanup); m_Staging = null; }
            m_Scope?.Dispose();
            m_Scope = null;
            Dependency = JobHandle.CombineDependencies(Dependency, cleanup);
            base.OnDestroy();
        }
    }
}
