#if RT_DEBUG_TOOLS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game;
using Game.Common;
using Game.Creatures;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.RailEta.Contracts;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using RapidTransitMod.RailEtaHost;

#if RT_RAIL_ETA_HOT_BUILD
namespace RapidTransitMod.RailEta.Hot
#else
namespace RapidTransitMod.RailEta.BuiltIn
#endif
{
    internal struct RailEtaComparisonVehicleRow
    {
        public Entity Controller;
        public byte EntityExists;
        public byte HasTarget;
        public byte HasPathOwner;
        public Entity Target;
        public float Speed;
        public Entity FrontLane;
        public float FrontPosition;
        public uint FrontFlags;
        public int PathElementIndex;
        public uint PathState;
        public ulong PathSignature;
        public byte Boarding;
        public uint DepartureFrame;
        public Entity Blocker;
        public byte BlockerExternalKind;
        public byte BlockerType;
        public byte BlockerMaximumSpeed;
        public float BlockerMaximumSpeedMetresPerSecond;
    }

    internal struct RailEtaComparisonPathRow
    {
        public Entity Lane;
        public byte Source;
        public sbyte Direction;
        public float Start;
        public float End;
        public uint NavigationFlags;
    }

    internal struct RailEtaComparisonLaneRow
    {
        public Entity Lane;
        public uint TrackFlags;
        public byte HasReservation;
        public Entity ReservationBlocker;
        public byte ReservationExternalKind;
        public byte PreviousPriority;
        public byte PreviousOffset;
        public byte NextPriority;
        public byte NextOffset;
        public byte HasUpdateFrame;
        public uint UpdateFrameIndex;
        public Entity SignalPetitioner;
        public Entity SignalBlocker;
        public byte SignalPetitionerExternalKind;
        public byte SignalBlockerExternalKind;
        public byte SignalType;
        public sbyte SignalPriority;
        public byte SignalFlags;
    }

    internal struct RailEtaComparisonOccupancyRow
    {
        public Entity Lane;
        public Entity Occupant;
        public byte ExternalKind;
        public float Start;
        public float End;
    }

    internal sealed class RailEtaComparisonStaging : IDisposable
    {
        public RailEtaComparisonStaging(Entity[] vehicles, Entity[] lanes)
        {
            Vehicles = new NativeArray<Entity>(vehicles, Allocator.Persistent);
            Lanes = new NativeArray<Entity>(lanes, Allocator.Persistent);
            VehicleRows = new NativeList<RailEtaComparisonVehicleRow>(Math.Max(1, vehicles.Length), Allocator.Persistent);
            PathRows = new NativeList<RailEtaComparisonPathRow>(256, Allocator.Persistent);
            LaneRows = new NativeList<RailEtaComparisonLaneRow>(Math.Max(1, lanes.Length), Allocator.Persistent);
            OccupancyRows = new NativeList<RailEtaComparisonOccupancyRow>(64, Allocator.Persistent);
        }

        public NativeArray<Entity> Vehicles;
        public NativeArray<Entity> Lanes;
        public NativeList<RailEtaComparisonVehicleRow> VehicleRows;
        public NativeList<RailEtaComparisonPathRow> PathRows;
        public NativeList<RailEtaComparisonLaneRow> LaneRows;
        public NativeList<RailEtaComparisonOccupancyRow> OccupancyRows;

        public void Dispose()
        {
            if (Vehicles.IsCreated) Vehicles.Dispose();
            if (Lanes.IsCreated) Lanes.Dispose();
            if (VehicleRows.IsCreated) VehicleRows.Dispose();
            if (PathRows.IsCreated) PathRows.Dispose();
            if (LaneRows.IsCreated) LaneRows.Dispose();
            if (OccupancyRows.IsCreated) OccupancyRows.Dispose();
        }

        public JobHandle Dispose(JobHandle dependency)
        {
            if (Vehicles.IsCreated) dependency = Vehicles.Dispose(dependency);
            if (Lanes.IsCreated) dependency = Lanes.Dispose(dependency);
            if (VehicleRows.IsCreated) dependency = VehicleRows.Dispose(dependency);
            if (PathRows.IsCreated) dependency = PathRows.Dispose(dependency);
            if (LaneRows.IsCreated) dependency = LaneRows.Dispose(dependency);
            if (OccupancyRows.IsCreated) dependency = OccupancyRows.Dispose(dependency);
            return dependency;
        }
    }

    [BurstCompile]
    internal struct CollectRailEtaComparisonJob : IJob
    {
        [ReadOnly] public NativeArray<Entity> Vehicles;
        [ReadOnly] public NativeArray<Entity> Lanes;
        [ReadOnly] public EntityStorageInfoLookup EntityLookup;
        [ReadOnly] public SharedComponentTypeHandle<UpdateFrame> UpdateFrameType;
        [ReadOnly] public ComponentLookup<Target> TargetData;
        [ReadOnly] public ComponentLookup<PathOwner> PathOwnerData;
        [ReadOnly] public ComponentLookup<Blocker> BlockerData;
        [ReadOnly] public ComponentLookup<Controller> ControllerData;
        [ReadOnly] public ComponentLookup<TrainNavigation> NavigationData;
        [ReadOnly] public ComponentLookup<TrainCurrentLane> CurrentLaneData;
        [ReadOnly] public ComponentLookup<PrefabRef> PrefabRefData;
        [ReadOnly] public ComponentLookup<TrainData> TrainPrefabData;
        [ReadOnly] public ComponentLookup<Train> TrainComponentData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.PublicTransport> PublicTransportData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.CargoTransport> CargoTransportData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.Car> CarData;
        [ReadOnly] public ComponentLookup<Creature> CreatureData;
        [ReadOnly] public ComponentLookup<Game.Net.TrackLane> TrackLaneData;
        [ReadOnly] public ComponentLookup<Game.Net.ConnectionLane> ConnectionLaneData;
        [ReadOnly] public ComponentLookup<LaneReservation> ReservationData;
        [ReadOnly] public ComponentLookup<LaneSignal> SignalData;
        [ReadOnly] public BufferLookup<LayoutElement> LayoutData;
        [ReadOnly] public BufferLookup<TrainNavigationLane> NavigationLaneData;
        [ReadOnly] public BufferLookup<PathElement> PathElementData;
        [ReadOnly] public BufferLookup<LaneObject> LaneObjectData;
        public NativeList<RailEtaComparisonVehicleRow> VehicleRows;
        public NativeList<RailEtaComparisonPathRow> PathRows;
        public NativeList<RailEtaComparisonLaneRow> LaneRows;
        public NativeList<RailEtaComparisonOccupancyRow> OccupancyRows;

        public void Execute()
        {
            for (int i = 0; i < Vehicles.Length; i++) AddVehicle(Vehicles[i], i == 0);
            for (int i = 0; i < Lanes.Length; i++) AddLane(Lanes[i]);
        }

        private void AddVehicle(Entity controller, bool selected)
        {
            var row = new RailEtaComparisonVehicleRow { Controller = controller };
            if (!EntityLookup.Exists(controller)) { VehicleRows.Add(row); return; }
            row.EntityExists = 1;
            row.HasTarget = (byte)(TargetData.HasComponent(controller) ? 1 : 0);
            row.HasPathOwner = (byte)(PathOwnerData.HasComponent(controller) ? 1 : 0);
            if (row.HasTarget != 0) row.Target = TargetData[controller].m_Target;
            PathOwner owner = row.HasPathOwner != 0 ? PathOwnerData[controller] : default;
            row.PathElementIndex = owner.m_ElementIndex;
            row.PathState = (uint)owner.m_State;
            Entity lead = controller;
            if (LayoutData.TryGetBuffer(controller, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0) lead = layout[0].m_Vehicle;
            if (NavigationData.HasComponent(lead)) row.Speed = NavigationData[lead].m_Speed;
            TrainCurrentLane current = CurrentLaneData.HasComponent(lead) ? CurrentLaneData[lead] : default;
            row.FrontLane = current.m_Front.m_Lane;
            row.FrontPosition = current.m_Front.m_CurvePosition.y;
            row.FrontFlags = (uint)current.m_Front.m_LaneFlags;

            Blocker blocker = BlockerData.HasComponent(controller) ? BlockerData[controller] : default;
            row.Blocker = Normalize(blocker.m_Blocker, out row.BlockerExternalKind);
            row.BlockerType = (byte)blocker.m_Type;
            row.BlockerMaximumSpeed = blocker.m_MaxSpeed;
            bool tram = false;
            if (PrefabRefData.TryGetComponent(lead, out PrefabRef prefabRef) && TrainPrefabData.TryGetComponent(prefabRef.m_Prefab, out TrainData trainData)) tram = (trainData.m_TrackType & TrackTypes.Tram) != 0;
            row.BlockerMaximumSpeedMetresPerSecond = blocker.m_MaxSpeed / (tram ? 2.2949998f : 1.8360001f);
            if (PublicTransportData.TryGetComponent(controller, out Game.Vehicles.PublicTransport passenger))
            {
                row.Boarding = (byte)(((passenger.m_State & PublicTransportFlags.Boarding) != 0) ? 1 : 0);
                row.DepartureFrame = passenger.m_DepartureFrame;
            }
            else if (CargoTransportData.TryGetComponent(controller, out Game.Vehicles.CargoTransport cargo))
            {
                row.Boarding = (byte)(((cargo.m_State & CargoTransportFlags.Boarding) != 0) ? 1 : 0);
                row.DepartureFrame = cargo.m_DepartureFrame;
            }

            if (selected)
            {
                ulong signature = 1469598103934665603UL;
                if (IsRailLane(current.m_Front.m_Lane)) AddToken(true, current.m_Front.m_Lane, 0, current.m_Front.m_CurvePosition.y, current.m_Front.m_CurvePosition.w, (uint)current.m_Front.m_LaneFlags, ref signature);
                if (NavigationLaneData.TryGetBuffer(controller, out DynamicBuffer<TrainNavigationLane> nav))
                    for (int i = 0; i < nav.Length; i++) if (IsRailLane(nav[i].m_Lane)) AddToken(true, nav[i].m_Lane, 1, nav[i].m_CurvePosition.x, nav[i].m_CurvePosition.y, (uint)nav[i].m_Flags, ref signature);
                if (row.HasPathOwner != 0 && PathElementData.TryGetBuffer(controller, out DynamicBuffer<PathElement> path))
                    for (int i = Math.Max(0, owner.m_ElementIndex); i < path.Length; i++) if (IsRailLane(path[i].m_Target)) AddToken(true, path[i].m_Target, 2, path[i].m_TargetDelta.x, path[i].m_TargetDelta.y, 0, ref signature);
                row.PathSignature = signature;
            }
            VehicleRows.Add(row);
        }

        private void AddToken(bool selected, Entity lane, byte source, float start, float end, uint flags, ref ulong signature)
        {
            signature = Mix(signature, lane);
            signature = Mix(signature, start);
            signature = Mix(signature, end);
            if (selected) PathRows.Add(new RailEtaComparisonPathRow { Lane = lane, Source = source, Direction = (sbyte)(end >= start ? 1 : -1), Start = start, End = end, NavigationFlags = flags });
        }

        private void AddLane(Entity lane)
        {
            if (lane == Entity.Null || !EntityLookup.Exists(lane)) return;
            bool hasReservation = ReservationData.HasComponent(lane);
            LaneReservation reservation = hasReservation ? ReservationData[lane] : default;
            LaneSignal signal = SignalData.HasComponent(lane) ? SignalData[lane] : default;
            Entity reservationBlocker = Normalize(reservation.m_Blocker, out byte reservationExternal);
            Entity signalPetitioner = Normalize(signal.m_Petitioner, out byte petitionerExternal);
            Entity signalBlocker = Normalize(signal.m_Blocker, out byte signalExternal);
            uint updateFrame = 0;
            byte hasUpdateFrame = 0;
            EntityStorageInfo info = EntityLookup[lane];
            if (info.Chunk.Has(UpdateFrameType)) { updateFrame = info.Chunk.GetSharedComponent(UpdateFrameType).m_Index; hasUpdateFrame = 1; }
            LaneRows.Add(new RailEtaComparisonLaneRow
            {
                Lane = lane,
                TrackFlags = TrackLaneData.HasComponent(lane) ? (uint)TrackLaneData[lane].m_Flags : 0,
                HasReservation = (byte)(hasReservation ? 1 : 0),
                ReservationBlocker = reservationBlocker,
                ReservationExternalKind = reservationExternal,
                PreviousPriority = reservation.m_Prev.m_Priority,
                PreviousOffset = reservation.m_Prev.m_Offset,
                NextPriority = reservation.m_Next.m_Priority,
                NextOffset = reservation.m_Next.m_Offset,
                HasUpdateFrame = hasUpdateFrame,
                UpdateFrameIndex = updateFrame,
                SignalPetitioner = signalPetitioner,
                SignalBlocker = signalBlocker,
                SignalPetitionerExternalKind = petitionerExternal,
                SignalBlockerExternalKind = signalExternal,
                SignalType = (byte)signal.m_Signal,
                SignalPriority = signal.m_Priority,
                SignalFlags = (byte)signal.m_Flags
            });
            if (!LaneObjectData.TryGetBuffer(lane, out DynamicBuffer<LaneObject> objects)) return;
            for (int i = 0; i < objects.Length; i++)
            {
                Entity occupant = Normalize(objects[i].m_LaneObject, out byte external);
                OccupancyRows.Add(new RailEtaComparisonOccupancyRow { Lane = lane, Occupant = occupant, ExternalKind = external, Start = objects[i].m_CurvePosition.x, End = objects[i].m_CurvePosition.y });
            }
        }

        private Entity Normalize(Entity entity, out byte externalKind)
        {
            externalKind = 0;
            if (entity == Entity.Null) return Entity.Null;
            if (ControllerData.HasComponent(entity)) entity = ControllerData[entity].m_Controller;
            if (CarData.HasComponent(entity) || IsRoadController(entity)) externalKind = (byte)RailExternalBlockerKind.RoadVehicle;
            else if (CreatureData.HasComponent(entity)) externalKind = (byte)RailExternalBlockerKind.Creature;
            else if (IsRailController(entity)) return entity;
            else externalKind = (byte)RailExternalBlockerKind.Unknown;
            return entity;
        }

        private bool IsRailController(Entity entity)
        {
            if (entity == Entity.Null || !EntityLookup.Exists(entity)) return false;
            if (TrainComponentData.HasComponent(entity) && PrefabRefData.TryGetComponent(entity, out PrefabRef directPrefab) && TrainPrefabData.HasComponent(directPrefab.m_Prefab)) return true;
            if (!LayoutData.TryGetBuffer(entity, out DynamicBuffer<LayoutElement> layout)) return false;
            for (int i = 0; i < layout.Length; i++)
            {
                Entity unit = layout[i].m_Vehicle;
                if (TrainComponentData.HasComponent(unit) && PrefabRefData.TryGetComponent(unit, out PrefabRef prefab) && TrainPrefabData.HasComponent(prefab.m_Prefab)) return true;
            }
            return false;
        }
        private bool IsRoadController(Entity entity)
        {
            if (entity == Entity.Null || !EntityLookup.Exists(entity) || !LayoutData.TryGetBuffer(entity, out DynamicBuffer<LayoutElement> layout)) return false;
            for (int i = 0; i < layout.Length; i++) if (CarData.HasComponent(layout[i].m_Vehicle)) return true;
            return false;
        }
        private bool IsRailLane(Entity lane) => lane != Entity.Null && (TrackLaneData.HasComponent(lane) || (ConnectionLaneData.HasComponent(lane) && ConnectionLaneData[lane].m_TrackTypes != TrackTypes.None));
        private static ulong Mix(ulong hash, Entity value) { hash ^= (uint)value.Index; hash *= 1099511628211UL; hash ^= (uint)value.Version; return hash * 1099511628211UL; }
        private static ulong Mix(ulong hash, float value) { hash ^= (uint)Unity.Mathematics.math.asint(value); return hash * 1099511628211UL; }
    }

    internal sealed partial class RailEtaComparisonSystem : GameSystemBase
    {
        private sealed class StartRequest
        {
            public RailEtaService Service;
            public RailEtaTicket Ticket;
            public int Generation;
        }

        private sealed class ExportOperation
        {
            public long Ticket;
            public Task<string> Task;
        }

        private static RailEtaComparisonSystem s_Current;
        private static long s_NextSessionIdentity;
        private static long s_NextExportIdentity;
        private readonly List<Entity> m_ObservedVehicles = new List<Entity>();
        private readonly List<Entity> m_StaticObservedVehicles = new List<Entity>();
        private readonly List<Entity> m_NextObservedVehicles = new List<Entity>();
        private readonly List<Entity> m_NextObservedLanes = new List<Entity>();
        private readonly List<ExportOperation> m_Exports = new List<ExportOperation>();
        private readonly HashSet<long> m_FinalExportStarted = new HashSet<long>();
        private SimulationSystem m_Simulation;
        private StartRequest m_PendingStart;
        private RailEtaComparisonSession m_Session;
        private RailEtaComparisonStaging m_Staging;
        private JobHandle m_Handle;
        private bool m_JobActive;
        private uint m_ScheduledFrame;
        private uint m_LastConsumedFrame = UInt32.MaxValue;
        private int m_MissedIntervals;

        internal bool NeedsTick => m_PendingStart != null
            || m_JobActive
            || (m_Session != null && !m_Session.IsTerminal)
            || m_Exports.Count != 0;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            s_Current = this;
        }

        protected override void OnUpdate()
        {
            PollExports();
            if (m_JobActive)
            {
                m_Handle.Complete();
                ConsumeSample();
                m_Staging.Dispose();
                m_Staging = null;
                m_JobActive = false;
            }
            if (m_PendingStart != null) ApplyStart();
            if (m_Session != null && !m_Session.IsTerminal && !m_JobActive) ScheduleSample();
        }

        internal JobHandle TickExternal(uint simulationFrame, JobHandle inputDependency)
        {
            Dependency = JobHandle.CombineDependencies(Dependency, inputDependency);
            if ((simulationFrame & 15u) == 3u) Update();
            return Dependency;
        }

        internal bool PrepareForHotReload(out RailEtaComparisonStatus status)
        {
            if (m_JobActive)
            {
                m_Handle.Complete();
                ConsumeSample();
                m_Staging.Dispose();
                m_Staging = null;
                m_JobActive = false;
            }
            m_PendingStart = null;
            StopAndExportCurrent("HotReload");
            if (m_Session != null)
            {
                status = m_Session.BuildStatus(m_Simulation.frameIndex);
                return true;
            }
            status = null;
            return false;
        }

        internal static void RequestStart(RailEtaService service, RailEtaTicket ticket)
        {
            if (s_Current != null && service != null) s_Current.m_PendingStart = new StartRequest { Service = service, Ticket = ticket, Generation = service.Generation };
        }

        internal static void StopForReset()
        {
            RailEtaComparisonSystem current = s_Current;
            if (current == null) return;
            current.m_PendingStart = null;
            current.StopAndExportCurrent("CityReset");
        }

        internal static bool TryGetStatus(out RailEtaComparisonStatus status)
        {
            RailEtaComparisonSystem current = s_Current;
            if (current != null && current.m_Session != null)
            {
                status = current.m_Session.BuildStatus(current.m_Simulation.frameIndex);
                return true;
            }
            status = null;
            return false;
        }

        private void ApplyStart()
        {
            StartRequest start = m_PendingStart;
            m_PendingStart = null;
            RailEtaService service = start?.Service;
            if (service == null || !ReferenceEquals(service, RailEtaService.Current) || service.IsDisposed || start.Generation != service.Generation) return;
            if (!service.TryGetState(start.Ticket, out RailEtaTicketStatus status) || status.State != RailEtaRequestState.Completed || status.ServiceGeneration != service.Generation) return;
            if (!service.TryGetSnapshot(start.Ticket, out RailEtaWorldSnapshot snapshot) || snapshot == null || snapshot.ServiceGeneration != service.Generation || snapshot.BatchId != status.BatchId) return;
            if (!service.TryGetRequest(start.Ticket, out RailEtaRequest request) || request == null || request.RequestId != start.Ticket.Value.ToString()) return;
            if (!service.TryGetPrediction(start.Ticket, out RailEtaPrediction prediction) || prediction == null || prediction.Failure != RailEtaFailure.None || prediction.RequestId != request.RequestId) return;
            if (!service.TryGetReplayWorld(start.Ticket, out RailEtaFrozenWorld frozenWorld) || frozenWorld == null) return;
            RailVehicleSnapshot target = FindVehicle(snapshot.Vehicles, request.VehicleId.Value);
            if (target == null || target.VehicleId.Value != request.VehicleId.Value) return;

            if (m_Session != null)
            {
                if (!m_Session.IsTerminal) m_Session.Stop(m_Simulation.frameIndex, "ReplacedByNewEtaRequest");
                StartFinalExportOnce(m_Session, m_Simulation.frameIndex);
            }
            long identity = Interlocked.Increment(ref s_NextSessionIdentity);
            m_Session = new RailEtaComparisonSession(identity, start.Ticket, status, snapshot, request, prediction, target, frozenWorld);
            m_StaticObservedVehicles.Clear();
            BuildVehicles(prediction, request.VehicleId.Value, m_StaticObservedVehicles);
            m_ObservedVehicles.Clear();
            AddUnique(m_ObservedVehicles, m_StaticObservedVehicles);
            m_NextObservedLanes.Clear();
            m_MissedIntervals = 0;
            m_LastConsumedFrame = UInt32.MaxValue;
            Mod.log.Info("[RailEtaComparison] start ticket=" + start.Ticket.Value + " vehicle=" + request.VehicleId.Value + " predictedArrival=" + prediction.PredictedArrivalFrame);
        }

        private void ScheduleSample()
        {
            Entity[] vehicles = m_ObservedVehicles.ToArray();
            Entity[] lanes = m_NextObservedLanes.ToArray();
            m_Staging = new RailEtaComparisonStaging(vehicles, lanes);
            var job = new CollectRailEtaComparisonJob
            {
                Vehicles = m_Staging.Vehicles,
                Lanes = m_Staging.Lanes,
                EntityLookup = GetEntityStorageInfoLookup(),
                UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>(),
                TargetData = GetComponentLookup<Target>(true),
                PathOwnerData = GetComponentLookup<PathOwner>(true),
                BlockerData = GetComponentLookup<Blocker>(true),
                ControllerData = GetComponentLookup<Controller>(true),
                NavigationData = GetComponentLookup<TrainNavigation>(true),
                CurrentLaneData = GetComponentLookup<TrainCurrentLane>(true),
                PrefabRefData = GetComponentLookup<PrefabRef>(true),
                TrainPrefabData = GetComponentLookup<TrainData>(true),
                TrainComponentData = GetComponentLookup<Train>(true),
                PublicTransportData = GetComponentLookup<Game.Vehicles.PublicTransport>(true),
                CargoTransportData = GetComponentLookup<Game.Vehicles.CargoTransport>(true),
                CarData = GetComponentLookup<Game.Vehicles.Car>(true),
                CreatureData = GetComponentLookup<Creature>(true),
                TrackLaneData = GetComponentLookup<Game.Net.TrackLane>(true),
                ConnectionLaneData = GetComponentLookup<Game.Net.ConnectionLane>(true),
                ReservationData = GetComponentLookup<LaneReservation>(true),
                SignalData = GetComponentLookup<LaneSignal>(true),
                LayoutData = GetBufferLookup<LayoutElement>(true),
                NavigationLaneData = GetBufferLookup<TrainNavigationLane>(true),
                PathElementData = GetBufferLookup<PathElement>(true),
                LaneObjectData = GetBufferLookup<LaneObject>(true),
                VehicleRows = m_Staging.VehicleRows,
                PathRows = m_Staging.PathRows,
                LaneRows = m_Staging.LaneRows,
                OccupancyRows = m_Staging.OccupancyRows
            };
            m_Handle = job.Schedule(Dependency);
            Dependency = m_Handle;
            m_JobActive = true;
            m_ScheduledFrame = m_Simulation.frameIndex;
        }

        private void ConsumeSample()
        {
            if (m_ScheduledFrame == m_LastConsumedFrame) return;
            m_LastConsumedFrame = m_ScheduledFrame;
            if (m_Session == null || m_Session.IsTerminal) return;
            var sample = new RailEtaComparisonSample
            {
                Frame = m_ScheduledFrame,
                MissedIntervalsBeforeSample = m_MissedIntervals,
                Vehicles = new RailEtaComparisonVehicleSample[m_Staging.VehicleRows.Length],
                Lanes = new RailEtaComparisonLaneSample[m_Staging.LaneRows.Length],
                Occupancies = new RailEtaComparisonOccupancySample[m_Staging.OccupancyRows.Length]
            };
            m_MissedIntervals = 0;
            for (int i = 0; i < sample.Vehicles.Length; i++)
            {
                RailEtaComparisonVehicleRow row = m_Staging.VehicleRows[i];
                sample.Vehicles[i] = new RailEtaComparisonVehicleSample
                {
                    VehicleId = Pack(row.Controller), EntityExists = row.EntityExists != 0, HasTarget = row.HasTarget != 0, HasPathOwner = row.HasPathOwner != 0,
                    TargetEntityId = Pack(row.Target), SpeedMetresPerSecond = row.Speed, FrontLaneId = Pack(row.FrontLane), FrontPosition = row.FrontPosition, FrontFlags = row.FrontFlags,
                    PathElementIndex = row.PathElementIndex, PathState = row.PathState, PathSignature = row.PathSignature, Boarding = row.Boarding != 0, DepartureFrame = row.DepartureFrame,
                    BlockerEntityId = Pack(row.Blocker), BlockerExternalKind = ExternalKind(row.BlockerExternalKind), BlockerType = ((BlockerType)row.BlockerType).ToString(),
                    BlockerMaximumSpeedCode = row.BlockerMaximumSpeed, BlockerMaximumSpeedMetresPerSecond = row.BlockerMaximumSpeedMetresPerSecond
                };
            }
            for (int i = 0; i < sample.Lanes.Length; i++)
            {
                RailEtaComparisonLaneRow row = m_Staging.LaneRows[i];
                sample.Lanes[i] = new RailEtaComparisonLaneSample
                {
                    LaneId = Pack(row.Lane), TrackFlags = row.TrackFlags, HasReservation = row.HasReservation != 0,
                    ReservationBlockerEntityId = Pack(row.ReservationBlocker), ReservationExternalKind = ExternalKind(row.ReservationExternalKind),
                    PreviousPriority = row.PreviousPriority, PreviousOffset = row.PreviousOffset, NextPriority = row.NextPriority, NextOffset = row.NextOffset,
                    HasUpdateFrame = row.HasUpdateFrame != 0, UpdateFrameIndex = row.UpdateFrameIndex,
                    SignalPetitionerEntityId = Pack(row.SignalPetitioner), SignalBlockerEntityId = Pack(row.SignalBlocker),
                    SignalPetitionerExternalKind = ExternalKind(row.SignalPetitionerExternalKind), SignalBlockerExternalKind = ExternalKind(row.SignalBlockerExternalKind),
                    SignalType = ((LaneSignalType)row.SignalType).ToString(), SignalPriority = row.SignalPriority, SignalFlags = row.SignalFlags
                };
            }
            for (int i = 0; i < sample.Occupancies.Length; i++)
            {
                RailEtaComparisonOccupancyRow row = m_Staging.OccupancyRows[i];
                sample.Occupancies[i] = new RailEtaComparisonOccupancySample { LaneId = Pack(row.Lane), OccupantEntityId = Pack(row.Occupant), OccupantExternalKind = ExternalKind(row.ExternalKind), StartFraction = row.Start, EndFraction = row.End };
            }

            string previousState = m_Session.State;
            m_Session.AddSample(sample, m_Staging.PathRows.AsArray());
            UpdateObservationSet();
            if (m_Session.IsTerminal)
            {
                Mod.log.Info("[RailEtaComparison] finish ticket=" + m_Session.Ticket.Value + " state=" + m_Session.State + " valid=" + m_Session.ComparisonValid + " reason=" + m_Session.InvalidReason);
                StartFinalExportOnce(m_Session, m_ScheduledFrame);
            }
            else if (previousState != m_Session.State)
            {
                Mod.log.Info("[RailEtaComparison] ticket=" + m_Session.Ticket.Value + " state=" + m_Session.State + " reason=" + m_Session.InvalidReason);
            }
        }

        private void UpdateObservationSet()
        {
            m_NextObservedLanes.Clear();
            NativeArray<RailEtaComparisonPathRow> path = m_Staging.PathRows.AsArray();
            for (int i = 0; i < path.Length; i++) if (path[i].Source <= 1) AddUnique(m_NextObservedLanes, path[i].Lane);
            m_NextObservedVehicles.Clear();
            AddUnique(m_NextObservedVehicles, m_StaticObservedVehicles);
            NativeArray<RailEtaComparisonVehicleRow> vehicles = m_Staging.VehicleRows.AsArray();
            for (int i = 0; i < vehicles.Length; i++) if (vehicles[i].Blocker != Entity.Null && vehicles[i].BlockerExternalKind == 0) AddUnique(m_NextObservedVehicles, vehicles[i].Blocker);
            NativeArray<RailEtaComparisonLaneRow> lanes = m_Staging.LaneRows.AsArray();
            for (int i = 0; i < lanes.Length; i++)
            {
                if (lanes[i].ReservationBlocker != Entity.Null && lanes[i].ReservationExternalKind == 0) AddUnique(m_NextObservedVehicles, lanes[i].ReservationBlocker);
                if (lanes[i].SignalPetitioner != Entity.Null && lanes[i].SignalPetitionerExternalKind == 0) AddUnique(m_NextObservedVehicles, lanes[i].SignalPetitioner);
                if (lanes[i].SignalBlocker != Entity.Null && lanes[i].SignalBlockerExternalKind == 0) AddUnique(m_NextObservedVehicles, lanes[i].SignalBlocker);
            }
            NativeArray<RailEtaComparisonOccupancyRow> occupancies = m_Staging.OccupancyRows.AsArray();
            for (int i = 0; i < occupancies.Length; i++) if (occupancies[i].Occupant != Entity.Null && occupancies[i].ExternalKind == 0) AddUnique(m_NextObservedVehicles, occupancies[i].Occupant);
            m_ObservedVehicles.Clear();
            AddUnique(m_ObservedVehicles, m_NextObservedVehicles);
        }

        private void StopAndExportCurrent(string reason)
        {
            if (m_Session == null) return;
            if (!m_Session.IsTerminal) m_Session.Stop(m_Simulation.frameIndex, reason);
            StartFinalExportOnce(m_Session, m_Simulation.frameIndex);
        }

        private void StartFinalExportOnce(RailEtaComparisonSession session, uint frame)
        {
            if (session == null || !m_FinalExportStarted.Add(session.Identity)) return;
            StartExport(session, frame);
        }

        private void StartExport(RailEtaComparisonSession session, uint frame)
        {
            long exportIdentity = Interlocked.Increment(ref s_NextExportIdentity);
            RailEtaComparisonExport value = session.FreezeExport(exportIdentity, frame);
            RailEtaReplayPackage replay = session.FreezeReplay();
            var operation = new ExportOperation { Ticket = session.Ticket.Value, Task = RailEtaComparisonSession.ExportAsync(value, replay) };
            m_Exports.Add(operation);
        }

        private void PollExports()
        {
            for (int i = m_Exports.Count - 1; i >= 0; i--)
            {
                ExportOperation operation = m_Exports[i];
                if (!operation.Task.IsCompleted) continue;
                if (operation.Task.IsFaulted)
                {
                    string error = operation.Task.Exception?.GetBaseException().Message ?? "comparison export failed";
                    Mod.log.Info("[RailEtaComparison] export failed ticket=" + operation.Ticket + " error=" + error);
                }
                else
                {
                    string path = operation.Task.Result;
                    Mod.log.Info("[RailEtaComparison] exported ticket=" + operation.Ticket + " path=" + path);
                }
                m_Exports.RemoveAt(i);
            }
        }

        private static RailVehicleSnapshot FindVehicle(RailVehicleSnapshot[] vehicles, long id)
        {
            vehicles = vehicles ?? Array.Empty<RailVehicleSnapshot>();
            for (int i = 0; i < vehicles.Length; i++) if (vehicles[i] != null && vehicles[i].VehicleId.Value == id) return vehicles[i];
            return null;
        }

        private static void BuildVehicles(RailEtaPrediction prediction, long selected, List<Entity> result)
        {
            AddUnique(result, Unpack(selected));
            RailEtaTraceEvent[] trace = prediction.Trace ?? Array.Empty<RailEtaTraceEvent>();
            for (int i = 0; i < trace.Length; i++) if (trace[i] != null && trace[i].OtherVehicleId.Value != 0) AddUnique(result, Unpack(trace[i].OtherVehicleId.Value));
        }

        private static void AddUnique(List<Entity> values, Entity value) { if (value != Entity.Null && !values.Contains(value)) values.Add(value); }
        private static void AddUnique(List<Entity> values, List<Entity> source) { for (int i = 0; i < source.Count; i++) AddUnique(values, source[i]); }
        private static long Pack(Entity entity) => entity == Entity.Null ? 0 : ((long)(uint)entity.Index << 32) | (uint)entity.Version;
        private static Entity Unpack(long value) => value == 0 ? Entity.Null : new Entity { Index = unchecked((int)(uint)((ulong)value >> 32)), Version = unchecked((int)(uint)value) };
        private static string ExternalKind(byte value) => value == 0 ? string.Empty : ((RailExternalBlockerKind)value).ToString();

        protected override void OnDestroy()
        {
            if (m_Session != null)
            {
                if (!m_Session.IsTerminal) m_Session.Stop(m_Simulation?.frameIndex ?? 0, "SystemDestroyed");
                StartFinalExportOnce(m_Session, m_Simulation?.frameIndex ?? 0);
            }
            m_PendingStart = null;
            if (ReferenceEquals(s_Current, this)) s_Current = null;
            if (m_Staging != null)
            {
                JobHandle cleanup = m_Staging.Dispose(m_Handle);
                Dependency = JobHandle.CombineDependencies(Dependency, cleanup);
                m_Staging = null;
            }
            base.OnDestroy();
        }
    }

}
#endif
