using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
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
    internal struct RailEtaVehicleIndexRow
    {
        public int ControllerOrdinal;
        public Entity Controller;
        public Entity Target;
        public Entity Blocker;
        public Entity Route;
        public int TargetSegmentIndex;
        public byte IsPassenger;
        public byte IsCargo;
        public float PathfindMaximumSpeed;
        public uint TrackTypes;
        public uint PathfindFlags;
        public float Speed;
        public int PathElementIndex;
        public uint PathState;
        public Entity PathDestination;
        public byte HasPathInformation;
        public uint DepartureFrame;
        public byte Boarding;
        public ulong PathSignature;
        public ulong ResourceSignature;
        public Entity FrontLane;
        public Entity RearLane;
        public Entity FrontCacheLane;
        public Entity RearCacheLane;
    }

    internal struct RailEtaScopedVehicleRow
    {
        public int ControllerOrdinal;
        public Entity Controller;
        public Entity Target;
        public Entity Blocker;
        public Entity Route;
        public int TargetSegmentIndex;
        public Entity FrontLane;
        public Entity RearLane;
        public Entity FrontCacheLane;
        public Entity RearCacheLane;
        public float FrontCurveStart;
        public float FrontCurveEnd;
        public float RearCurvePosition;
        public uint FrontLaneFlags;
        public uint RearLaneFlags;
        public float Speed;
        public uint DepartureFrame;
        public byte Boarding;
        public int PathElementIndex;
        public uint PathState;
        public Entity PathDestination;
        public byte HasPathInformation;
        public int UnitCount;
        public float MaximumSpeed;
        public float Acceleration;
        public float Braking;
        public float TurningLow;
        public float TurningHigh;
        public int VehiclePriority;
        public ulong PathSignature;
        public ulong ResourceSignature;
        public byte ExternalBlockerKind;
        public byte BlockerType;
        public byte BlockerMaximumSpeed;
        public float BlockerMaximumSpeedMetresPerSecond;
        public byte IsPassenger;
        public byte IsCargo;
        public float PathfindMaximumSpeed;
        public uint TrackTypes;
        public uint PathfindFlags;
        public Unity.Mathematics.float3 TransformPosition;
        public Unity.Mathematics.quaternion TransformRotation;
        public Unity.Mathematics.float3 MovingVelocity;
        public Unity.Mathematics.float3 MovingAngularVelocity;
        public float OdometerDistance;
        public byte HasOdometer;
        public Unity.Mathematics.float3 NavigationFrontPosition;
        public Unity.Mathematics.float3 NavigationFrontDirection;
        public Unity.Mathematics.float3 NavigationRearPosition;
        public Unity.Mathematics.float3 NavigationRearDirection;
        public float CurrentLaneDuration;
        public float CurrentLaneDistance;
        public Unity.Mathematics.float4 FrontCurvePosition;
        public Unity.Mathematics.float4 RearCurvePositions;
        public Unity.Mathematics.float2 FrontCacheCurvePosition;
        public Unity.Mathematics.float2 RearCacheCurvePosition;
        public uint FrontCacheLaneFlags;
        public uint RearCacheLaneFlags;
    }

    internal struct RailEtaLineRouteRow
    {
        public int LineOrdinal;
        public Entity Line;
        public int SegmentCount;
        public int WaypointCount;
        public ulong ChainSignature;
        public byte IsPassenger;
    }

    internal struct RailEtaRouteSegmentRow
    {
        public Entity Line;
        public Entity Segment;
        public Entity FromWaypoint;
        public Entity ToWaypoint;
        public int SegmentIndex;
        public uint PathState;
        public uint PathfindDelayFrames;
        public byte GeometryAvailable;
        public byte PathfindDelayKnown;
        public byte ToWaypointBoarding;
    }

    internal struct RailEtaRoutePathRow
    {
        public Entity Line;
        public int SegmentIndex;
        public int Sequence;
        public Entity Lane;
        public float Start;
        public float End;
        public uint PathFlags;
    }

    internal struct RailEtaScopedLaneRow
    {
        public int LaneOrdinal;
        public Entity Controller;
        public Entity Line;
        public int RouteSegmentIndex;
        public Entity Lane;
        public Entity OtherLane;
        public int Sequence;
        public float CurveStart;
        public float CurveEnd;
        public float Length;
        public float SpeedLimit;
        public float Curviness;
        public Unity.Mathematics.float3 CurveA;
        public Unity.Mathematics.float3 CurveB;
        public Unity.Mathematics.float3 CurveC;
        public Unity.Mathematics.float3 CurveD;
        public uint NavigationFlags;
        public uint PathFlags;
        public uint TrackFlags;
        public Entity TrackAccessRestriction;
        public Entity ConnectionAccessRestriction;
        public uint ConnectionFlags;
        public uint ConnectionTrackTypes;
        public uint ConnectionRoadTypes;
        public Unity.Mathematics.float2 EdgeDelta;
        public byte EdgeConnectedStartCount;
        public byte EdgeConnectedEndCount;
        public byte IsConnectionLane;
        public Entity PathPhysicalLane;
        public Entity SharedPhysicalLane;
        public byte ParticipatesInSharedCorridor;
        public Entity ReservationBlocker;
        public byte PreviousPriority;
        public byte PreviousOffset;
        public byte NextPriority;
        public byte NextOffset;
        public Entity SignalPetitioner;
        public Entity SignalBlocker;
        public byte SignalFlags;
        public sbyte SignalPriority;
        public byte ReservationExternalKind;
        public byte SignalPetitionerExternalKind;
        public byte SignalBlockerExternalKind;
        public sbyte OverlapPriorityDelta;
        public byte OverlapThisStart;
        public byte OverlapThisEnd;
        public byte OverlapOtherStart;
        public byte OverlapOtherEnd;
        public ushort OverlapFlags;
        public uint UpdateFrameIndex;
        public byte HasReservation;
        public byte HasUpdateFrame;
        public byte HasSignal;
        public byte HasCurve;
        public byte HasTrackLane;
        public byte HasConnectionLane;
        public byte SignalType;
        public ushort SignalGroupMask;
        public sbyte SignalDefault;
        public Entity SignalController;
        public uint SignalUpdateFrameIndex;
        public byte HasSignalUpdateFrame;
        public TrafficLights TrafficLights;
        public byte OverlapParallelism;
        public byte Source;
        public Curve Curve;
        public Game.Net.TrackLane TrackLane;
        public Game.Net.ConnectionLane ConnectionLane;
        public LaneReservation Reservation;
        public LaneSignal Signal;
    }

    internal struct RailEtaScopedUnitRow
    {
        public byte IsTheory;
        public int LayoutOrdinal;
        public Entity Controller;
        public Entity Unit;
        public Entity Prefab;
        public float Length;
        public float FrontBogieOffset;
        public float RearBogieOffset;
        public float FrontAttachOffset;
        public float RearAttachOffset;
        public uint TrainFlags;
        public uint PrefabTrainFlags;
        public uint EnergyTypes;
        public uint TrackTypes;
        public Unity.Mathematics.float3 TransformPosition;
        public Unity.Mathematics.quaternion TransformRotation;
        public Unity.Mathematics.float3 MovingVelocity;
        public Unity.Mathematics.float3 MovingAngularVelocity;
        public float OdometerDistance;
        public Game.Objects.Transform Transform;
        public Moving Moving;
        public Train Train;
        public TrainNavigation Navigation;
        public TrainCurrentLane CurrentLane;
        public TrainData PrefabTrainData;
        public ObjectGeometryData PrefabGeometryData;
        public byte HasTransform;
        public byte HasMoving;
        public byte HasTrain;
        public byte HasNavigation;
        public byte HasCurrentLane;
        public byte HasPrefabTrainData;
        public byte HasPrefabGeometryData;
    }

    internal struct RailEtaFrozenNavigationLaneRow { public int ControllerOrdinal; public int LaneOrdinal; public Entity Controller; public Entity Lane; public Unity.Mathematics.float2 CurvePosition; public uint Flags; public byte LaneExists; }
    internal struct RailEtaFrozenPathElementRow { public int ControllerOrdinal; public int ElementOrdinal; public Entity Controller; public Entity Target; public Unity.Mathematics.float2 TargetDelta; public uint Flags; public byte TargetExists; }

    internal struct RailEtaLaneOccupancyRow
    {
        public Entity Lane;
        public Entity Vehicle;
        public Entity Unit;
        public float Start;
        public float End;
    }

    internal struct RailEtaSignalPeerRow
    {
        public Entity Lane;
        public Entity Controller;
        public uint UpdateFrameIndex;
        public TrafficLights TrafficLights;
        public LaneSignal Signal;
    }

    internal sealed class RailEtaScopedStaging : System.IDisposable
    {
        public RailEtaScopedStaging(NativeList<Entity> controllers, NativeList<Entity> routeLines = default,
            NativeList<Entity> railLanes = default, NativeList<Entity> trafficLights = default)
        {
            Controllers = controllers;
            RouteLines = routeLines;
            RailLanes = railLanes;
            TrafficLightControllers = trafficLights;
            Vehicles = new NativeList<RailEtaScopedVehicleRow>(Unity.Mathematics.math.max(16, controllers.Capacity), Allocator.Persistent);
            Lanes = new NativeList<RailEtaScopedLaneRow>(Unity.Mathematics.math.min(RailEtaLimits.MaxFrozenLaneFacts,
                Unity.Mathematics.math.max(2048, railLanes.IsCreated ? railLanes.Capacity * 4 : 0)), Allocator.Persistent);
            Units = new NativeList<RailEtaScopedUnitRow>(Unity.Mathematics.math.max(16, controllers.Capacity * 4), Allocator.Persistent);
            NavigationLanes = new NativeList<RailEtaFrozenNavigationLaneRow>(Unity.Mathematics.math.max(32, controllers.Capacity * 8), Allocator.Persistent);
            PathElements = new NativeList<RailEtaFrozenPathElementRow>(Unity.Mathematics.math.max(64, controllers.Capacity * 16), Allocator.Persistent);
            Occupancies = new NativeList<RailEtaLaneOccupancyRow>(Unity.Mathematics.math.min(RailEtaLimits.MaxFrozenLaneOccupancies,
                Unity.Mathematics.math.max(2048, controllers.Capacity * 4)), Allocator.Persistent);
            Lines = new NativeList<RailEtaLineRouteRow>(64, Allocator.Persistent);
            Segments = new NativeList<RailEtaRouteSegmentRow>(256, Allocator.Persistent);
            Paths = new NativeList<RailEtaRoutePathRow>(4096, Allocator.Persistent);
            SignalControllerByLane = new NativeParallelHashMap<Entity, Entity>(RailEtaLimits.MaxFrozenLaneFacts, Allocator.Persistent);
            SignalPeers = new NativeList<RailEtaSignalPeerRow>(1024, Allocator.Persistent);
            Overflow = new NativeReference<int>(Allocator.Persistent);
        }

        public NativeList<Entity> Controllers;
        public NativeList<Entity> RouteLines;
        public NativeList<Entity> RailLanes;
        public NativeList<Entity> TrafficLightControllers;
        public NativeList<RailEtaScopedVehicleRow> Vehicles;
        public NativeList<RailEtaScopedLaneRow> Lanes;
        public NativeList<RailEtaScopedUnitRow> Units;
        public NativeList<RailEtaFrozenNavigationLaneRow> NavigationLanes;
        public NativeList<RailEtaFrozenPathElementRow> PathElements;
        public NativeList<RailEtaLaneOccupancyRow> Occupancies;
        public NativeList<RailEtaLineRouteRow> Lines;
        public NativeList<RailEtaRouteSegmentRow> Segments;
        public NativeList<RailEtaRoutePathRow> Paths;
        public NativeParallelHashMap<Entity, Entity> SignalControllerByLane;
        public NativeList<RailEtaSignalPeerRow> SignalPeers;
        public NativeReference<int> Overflow;
        public void Dispose() { if (Controllers.IsCreated) Controllers.Dispose(); if (RouteLines.IsCreated) RouteLines.Dispose(); if (RailLanes.IsCreated) RailLanes.Dispose(); if (TrafficLightControllers.IsCreated) TrafficLightControllers.Dispose(); if (Vehicles.IsCreated) Vehicles.Dispose(); if (Lanes.IsCreated) Lanes.Dispose(); if (Units.IsCreated) Units.Dispose(); if (NavigationLanes.IsCreated) NavigationLanes.Dispose(); if (PathElements.IsCreated) PathElements.Dispose(); if (Occupancies.IsCreated) Occupancies.Dispose(); if (Lines.IsCreated) Lines.Dispose(); if (Segments.IsCreated) Segments.Dispose(); if (Paths.IsCreated) Paths.Dispose(); if (SignalControllerByLane.IsCreated) SignalControllerByLane.Dispose(); if (SignalPeers.IsCreated) SignalPeers.Dispose(); if (Overflow.IsCreated) Overflow.Dispose(); }
        public JobHandle Dispose(JobHandle dependency) { if (Controllers.IsCreated) dependency = Controllers.Dispose(dependency); if (RouteLines.IsCreated) dependency = RouteLines.Dispose(dependency); if (RailLanes.IsCreated) dependency = RailLanes.Dispose(dependency); if (TrafficLightControllers.IsCreated) dependency = TrafficLightControllers.Dispose(dependency); if (Vehicles.IsCreated) dependency = Vehicles.Dispose(dependency); if (Lanes.IsCreated) dependency = Lanes.Dispose(dependency); if (Units.IsCreated) dependency = Units.Dispose(dependency); if (NavigationLanes.IsCreated) dependency = NavigationLanes.Dispose(dependency); if (PathElements.IsCreated) dependency = PathElements.Dispose(dependency); if (Occupancies.IsCreated) dependency = Occupancies.Dispose(dependency); if (Lines.IsCreated) dependency = Lines.Dispose(dependency); if (Segments.IsCreated) dependency = Segments.Dispose(dependency); if (Paths.IsCreated) dependency = Paths.Dispose(dependency); if (SignalControllerByLane.IsCreated) dependency = SignalControllerByLane.Dispose(dependency); if (SignalPeers.IsCreated) dependency = SignalPeers.Dispose(dependency); if (Overflow.IsCreated) dependency = Overflow.Dispose(dependency); return dependency; }
    }

    [BurstCompile]
    internal struct CollectRailSnapshotJob : IJob
    {
        public RailEtaMode Mode;
        [ReadOnly] public NativeArray<Entity> RailLanes;
        [ReadOnly] public NativeArray<Entity> Controllers;
        [ReadOnly] public NativeArray<Entity> RouteLines;
        [ReadOnly] public NativeArray<Entity> TrafficLightControllers;
        [ReadOnly] public EntityStorageInfoLookup EntityLookup;
        [ReadOnly] public SharedComponentTypeHandle<UpdateFrame> UpdateFrameType;
        [ReadOnly] public ComponentLookup<Target> TargetData;
        [ReadOnly] public ComponentLookup<PathOwner> PathOwnerData;
        [ReadOnly] public ComponentLookup<PathInformation> PathInformationData;
        [ReadOnly] public ComponentLookup<Blocker> BlockerData;
        [ReadOnly] public ComponentLookup<Controller> ControllerData;
        [ReadOnly] public ComponentLookup<CurrentRoute> CurrentRouteData;
        [ReadOnly] public ComponentLookup<Waypoint> WaypointData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.PublicTransport> PublicTransportData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.CargoTransport> CargoTransportData;
        [ReadOnly] public ComponentLookup<TrainNavigation> NavigationData;
        [ReadOnly] public ComponentLookup<TrainCurrentLane> CurrentLaneData;
        [ReadOnly] public ComponentLookup<PrefabRef> PrefabRefData;
        [ReadOnly] public ComponentLookup<TrainData> PrefabTrainData;
        [ReadOnly] public ComponentLookup<Train> TrainComponentData;
        [ReadOnly] public ComponentLookup<Transform> TransformData;
        [ReadOnly] public ComponentLookup<Moving> MovingData;
        [ReadOnly] public ComponentLookup<Odometer> OdometerData;
        [ReadOnly] public ComponentLookup<ObjectGeometryData> PrefabGeometryData;
        [ReadOnly] public ComponentLookup<Game.Net.TrackLane> TrackLaneData;
        [ReadOnly] public ComponentLookup<Game.Net.ConnectionLane> ConnectionLaneData;
        [ReadOnly] public ComponentLookup<Game.Net.EdgeLane> EdgeLaneData;
        [ReadOnly] public ComponentLookup<Curve> CurveData;
        [ReadOnly] public ComponentLookup<LaneReservation> ReservationData;
        [ReadOnly] public ComponentLookup<LaneSignal> SignalData;
        [ReadOnly] public ComponentLookup<TrafficLights> TrafficLightsData;
        [ReadOnly] public BufferLookup<Game.Net.SubLane> SubLaneData;
        [ReadOnly] public ComponentLookup<Game.Vehicles.Car> CarData;
        [ReadOnly] public ComponentLookup<Game.Creatures.Creature> CreatureData;
        [ReadOnly] public BufferLookup<LayoutElement> LayoutData;
        [ReadOnly] public BufferLookup<TrainNavigationLane> NavigationLaneData;
        [ReadOnly] public BufferLookup<PathElement> PathElementData;
        [ReadOnly] public BufferLookup<RouteSegment> RouteSegmentData;
        [ReadOnly] public BufferLookup<RouteWaypoint> RouteWaypointData;
        [ReadOnly] public ComponentLookup<RouteLane> RouteLaneData;
        [ReadOnly] public ComponentLookup<Connected> ConnectedData;
        [ReadOnly] public ComponentLookup<BoardingVehicle> BoardingVehicleData;
        [ReadOnly] public ComponentLookup<TransportLineData> TransportLinePrefabData;
        [ReadOnly] public BufferLookup<LaneOverlap> LaneOverlapData;
        [ReadOnly] public BufferLookup<LaneObject> LaneObjectData;
        public NativeList<RailEtaScopedVehicleRow> Vehicles;
        public NativeList<RailEtaScopedLaneRow> Lanes;
        public NativeList<RailEtaScopedUnitRow> Units;
        public NativeList<RailEtaFrozenNavigationLaneRow> NavigationLanes;
        public NativeList<RailEtaFrozenPathElementRow> PathElements;
        public NativeList<RailEtaLaneOccupancyRow> Occupancies;
        public NativeList<RailEtaLineRouteRow> Lines;
        public NativeList<RailEtaRouteSegmentRow> Segments;
        public NativeList<RailEtaRoutePathRow> Paths;
        public NativeParallelHashMap<Entity, Entity> SignalControllerByLane;
        public NativeList<RailEtaSignalPeerRow> SignalPeers;
        public NativeReference<int> Overflow;

        public void Execute()
        {
            if (RailLanes.Length > RailEtaLimits.MaxFrozenLaneFacts) { Overflow.Value = 1; return; }
            if (Mode == RailEtaMode.Full) BuildSignalControllerIndex();
            for (int i = 0; i < Controllers.Length; i++)
            {
                Entity controller = Controllers[i];
                if (!EntityLookup.Exists(controller) || !TargetData.HasComponent(controller) || !PathOwnerData.HasComponent(controller) || !BlockerData.HasComponent(controller)) { Overflow.Value = 2; continue; }
                PathOwner owner = PathOwnerData[controller];
                PathInformation pathInformation = PathInformationData.HasComponent(controller) ? PathInformationData[controller] : default;
                Blocker blocker = BlockerData[controller];
                Entity lead = controller;
                int unitCount = 0;
                float maxSpeed = float.MaxValue, acceleration = float.MaxValue, braking = float.MaxValue;
                float turningLow = 0f, turningHigh = 0f;
                int vehiclePriority = 0;
                bool isTram = false;
                float pathfindMaximumSpeed = 0f;
                uint trackTypes = 0;
                if (LayoutData.TryGetBuffer(controller, out DynamicBuffer<LayoutElement> layout))
                {
                    unitCount = layout.Length;
                    if (layout.Length > 0) lead = layout[0].m_Vehicle;
                    for (int u = 0; u < layout.Length; u++)
                    {
                        Entity unit = layout[u].m_Vehicle;
                        Entity prefab = PrefabRefData.HasComponent(unit) ? PrefabRefData[unit].m_Prefab : Entity.Null;
                        bool hasPrefabTrain = prefab != Entity.Null && PrefabTrainData.HasComponent(prefab);
                        TrainData data = hasPrefabTrain ? PrefabTrainData[prefab] : default;
                        if (u == 0)
                        {
                            turningLow = data.m_Turning.x;
                            turningHigh = data.m_Turning.y;
                            isTram = (data.m_TrackType & TrackTypes.Tram) != 0;
                            pathfindMaximumSpeed = data.m_MaxSpeed;
                            trackTypes = (uint)data.m_TrackType;
                        }
                        bool hasTrain = TrainComponentData.TryGetComponent(unit, out Train train);
                        bool hasTransform = TransformData.HasComponent(unit);
                        bool hasMoving = MovingData.HasComponent(unit);
                        bool hasNavigation = NavigationData.HasComponent(unit);
                        bool hasCurrentLane = CurrentLaneData.HasComponent(unit);
                        bool hasGeometry = PrefabGeometryData.HasComponent(prefab);
                        ObjectGeometryData geometry = PrefabGeometryData.HasComponent(prefab) ? PrefabGeometryData[prefab] : default;
                        Transform unitTransform = hasTransform ? TransformData[unit] : default;
                        Moving unitMoving = hasMoving ? MovingData[unit] : default;
                        Units.Add(new RailEtaScopedUnitRow { LayoutOrdinal = u, Controller = controller, Unit = unit, Prefab = prefab, Length = geometry.m_Size.z, FrontBogieOffset = data.m_BogieOffsets.x, RearBogieOffset = data.m_BogieOffsets.y, FrontAttachOffset = data.m_AttachOffsets.x, RearAttachOffset = data.m_AttachOffsets.y,
                            TrainFlags = TrainComponentData.HasComponent(unit) ? (uint)TrainComponentData[unit].m_Flags : 0u, PrefabTrainFlags = (uint)data.m_TrainFlags, EnergyTypes = (uint)data.m_EnergyType, TrackTypes = (uint)data.m_TrackType,
                            TransformPosition = unitTransform.m_Position, TransformRotation = unitTransform.m_Rotation, MovingVelocity = unitMoving.m_Velocity, MovingAngularVelocity = unitMoving.m_AngularVelocity,
                            OdometerDistance = OdometerData.HasComponent(unit) ? OdometerData[unit].m_Distance : 0f,
                            Transform = unitTransform, Moving = unitMoving,
                            Train = hasTrain ? train : default,
                            Navigation = hasNavigation ? NavigationData[unit] : default,
                            CurrentLane = hasCurrentLane ? CurrentLaneData[unit] : default,
                            PrefabTrainData = data, PrefabGeometryData = geometry,
                            HasTransform = (byte)(hasTransform ? 1 : 0), HasMoving = (byte)(hasMoving ? 1 : 0),
                            HasTrain = (byte)(hasTrain ? 1 : 0), HasNavigation = (byte)(hasNavigation ? 1 : 0),
                            HasCurrentLane = (byte)(hasCurrentLane ? 1 : 0), HasPrefabTrainData = (byte)(hasPrefabTrain ? 1 : 0),
                            HasPrefabGeometryData = (byte)(hasGeometry ? 1 : 0) });
                        if (u == 0) vehiclePriority = VehicleUtils.GetPriority(data);
                        maxSpeed = Unity.Mathematics.math.min(maxSpeed, data.m_MaxSpeed);
                        acceleration = Unity.Mathematics.math.min(acceleration, data.m_Acceleration);
                        braking = Unity.Mathematics.math.min(braking, data.m_Braking);
                    }
                }
                TrainCurrentLane current = CurrentLaneData.HasComponent(lead) ? CurrentLaneData[lead] : default;
                Entity normalizedBlocker = blocker.m_Blocker;
                if (ControllerData.HasComponent(normalizedBlocker)) normalizedBlocker = ControllerData[normalizedBlocker].m_Controller;
                byte externalKind = 0;
                if (normalizedBlocker != Entity.Null && !ContainsController(normalizedBlocker))
                    externalKind = blocker.m_Type == BlockerType.Crossing ? (byte)4 : CarData.HasComponent(normalizedBlocker) ? (byte)2 : CreatureData.HasComponent(normalizedBlocker) ? (byte)3 : (byte)5;
                if (externalKind != 0) normalizedBlocker = Entity.Null;
                byte boarding = 0;
                byte isPassenger = 0;
                byte isCargo = 0;
                uint pathfindFlags = 0;
                uint departureFrame = 0;
                if (PublicTransportData.TryGetComponent(controller, out Game.Vehicles.PublicTransport passenger))
                {
                    isPassenger = 1;
                    boarding = (byte)(((passenger.m_State & PublicTransportFlags.Boarding) != 0) ? 1 : 0);
                    departureFrame = passenger.m_DepartureFrame;
                    if ((passenger.m_State & (PublicTransportFlags.EnRoute | PublicTransportFlags.RouteSource)) == (PublicTransportFlags.EnRoute | PublicTransportFlags.RouteSource))
                        pathfindFlags = (uint)(PathfindFlags.Stable | PathfindFlags.IgnoreFlow);
                }
                else if (CargoTransportData.TryGetComponent(controller, out Game.Vehicles.CargoTransport cargo))
                {
                    isCargo = 1;
                    boarding = (byte)(((cargo.m_State & CargoTransportFlags.Boarding) != 0) ? 1 : 0);
                    departureFrame = cargo.m_DepartureFrame;
                    if ((cargo.m_State & (CargoTransportFlags.EnRoute | CargoTransportFlags.RouteSource)) == (CargoTransportFlags.EnRoute | CargoTransportFlags.RouteSource))
                        pathfindFlags = (uint)(PathfindFlags.Stable | PathfindFlags.IgnoreFlow);
                }
                if (Mode == RailEtaMode.Theory)
                {
                    // Theory keeps the real path and train physics, but deliberately removes
                    // dispatch waiting and any request-frame blocker from the same solver input.
                    normalizedBlocker = Entity.Null;
                    externalKind = 0;
                    boarding = 0;
                    departureFrame = 0;
                }
                ulong pathSignature = 1469598103934665603UL, resourceSignature = 1469598103934665603UL;
                int sequence = 0;
                if (LayoutData.TryGetBuffer(controller, out DynamicBuffer<LayoutElement> currentLayout))
                    for (int u = 0; u < currentLayout.Length; u++)
                    {
                        Entity unit = currentLayout[u].m_Vehicle;
                        if (!CurrentLaneData.TryGetComponent(unit, out TrainCurrentLane unitLane)) continue;
                        AddCurrentLane(controller, unitLane.m_Front.m_Lane, ref sequence, ref pathSignature, ref resourceSignature);
                        AddCurrentLane(controller, unitLane.m_Rear.m_Lane, ref sequence, ref pathSignature, ref resourceSignature);
                        AddCurrentLane(controller, unitLane.m_FrontCache.m_Lane, ref sequence, ref pathSignature, ref resourceSignature);
                        AddCurrentLane(controller, unitLane.m_RearCache.m_Lane, ref sequence, ref pathSignature, ref resourceSignature);
                    }
                if (NavigationLaneData.TryGetBuffer(controller, out DynamicBuffer<TrainNavigationLane> nav))
                    for (int n = 0; n < nav.Length; n++)
                    {
                        NavigationLanes.Add(new RailEtaFrozenNavigationLaneRow { ControllerOrdinal = i, LaneOrdinal = n, Controller = controller, Lane = nav[n].m_Lane, CurvePosition = nav[n].m_CurvePosition, Flags = (uint)nav[n].m_Flags, LaneExists = (byte)(EntityLookup.Exists(nav[n].m_Lane) ? 1 : 0) });
                        AddLane(controller, Entity.Null, -1, nav[n].m_Lane, nav[n].m_CurvePosition.x, nav[n].m_CurvePosition.y, sequence++, 1, (uint)nav[n].m_Flags, 0, ref pathSignature, ref resourceSignature);
                    }
                if (PathElementData.TryGetBuffer(controller, out DynamicBuffer<PathElement> path))
                    for (int p = 0; p < path.Length; p++)
                    {
                        PathElements.Add(new RailEtaFrozenPathElementRow { ControllerOrdinal = i, ElementOrdinal = p, Controller = controller, Target = path[p].m_Target, TargetDelta = path[p].m_TargetDelta, Flags = (uint)path[p].m_Flags, TargetExists = (byte)(EntityLookup.Exists(path[p].m_Target) ? 1 : 0) });
                        if (p >= Unity.Mathematics.math.max(0, owner.m_ElementIndex)) AddLane(controller, Entity.Null, -1, path[p].m_Target, path[p].m_TargetDelta.x, path[p].m_TargetDelta.y, sequence++, 2, 0, (uint)path[p].m_Flags, ref pathSignature, ref resourceSignature);
                    }
                TrainNavigation navigation = NavigationData.HasComponent(lead) ? NavigationData[lead] : default;
                Transform leadTransform = TransformData.HasComponent(lead) ? TransformData[lead] : default;
                Moving leadMoving = MovingData.HasComponent(lead) ? MovingData[lead] : default;
                Vehicles.Add(new RailEtaScopedVehicleRow
                {
                    ControllerOrdinal = i, Controller = controller, Target = TargetData[controller].m_Target, Blocker = normalizedBlocker,
                    Route = CurrentRouteData.HasComponent(controller) ? CurrentRouteData[controller].m_Route : Entity.Null,
                    TargetSegmentIndex = WaypointData.HasComponent(TargetData[controller].m_Target) ? WaypointData[TargetData[controller].m_Target].m_Index : -1,
                    FrontLane = current.m_Front.m_Lane, FrontCurveStart = current.m_Front.m_CurvePosition.y, FrontCurveEnd = current.m_Front.m_CurvePosition.w,
                    RearLane = current.m_Rear.m_Lane, RearCurvePosition = current.m_Rear.m_CurvePosition.y, FrontCacheLane = current.m_FrontCache.m_Lane, RearCacheLane = current.m_RearCache.m_Lane,
                    FrontLaneFlags = (uint)current.m_Front.m_LaneFlags, RearLaneFlags = (uint)current.m_Rear.m_LaneFlags,
                    Speed = navigation.m_Speed,
                    Boarding = boarding, DepartureFrame = departureFrame,
                    PathElementIndex = owner.m_ElementIndex, PathState = (uint)owner.m_State, UnitCount = unitCount,
                    PathDestination = pathInformation.m_Destination, HasPathInformation = (byte)(PathInformationData.HasComponent(controller) ? 1 : 0),
                    MaximumSpeed = maxSpeed == float.MaxValue ? 0f : maxSpeed, Acceleration = acceleration == float.MaxValue ? 0f : acceleration, Braking = braking == float.MaxValue ? 0f : braking,
                    TurningLow = turningLow, TurningHigh = turningHigh,
                    VehiclePriority = vehiclePriority,
                    PathSignature = pathSignature, ResourceSignature = resourceSignature, ExternalBlockerKind = 0,
                    BlockerType = Mode != RailEtaMode.Theory && externalKind == 0 ? (byte)blocker.m_Type : (byte)0,
                    BlockerMaximumSpeed = Mode != RailEtaMode.Theory && externalKind == 0 ? blocker.m_MaxSpeed : (byte)0,
                    BlockerMaximumSpeedMetresPerSecond = Mode != RailEtaMode.Theory && externalKind == 0 ? blocker.m_MaxSpeed / (isTram ? 2.2949998f : 1.8360001f) : 0f,
                    IsPassenger = isPassenger, IsCargo = isCargo, PathfindMaximumSpeed = pathfindMaximumSpeed,
                    TrackTypes = trackTypes, PathfindFlags = pathfindFlags,
                    TransformPosition = leadTransform.m_Position, TransformRotation = leadTransform.m_Rotation,
                    MovingVelocity = leadMoving.m_Velocity, MovingAngularVelocity = leadMoving.m_AngularVelocity,
                    OdometerDistance = OdometerData.HasComponent(controller) ? OdometerData[controller].m_Distance : 0f,
                    HasOdometer = (byte)(OdometerData.HasComponent(controller) ? 1 : 0),
                    NavigationFrontPosition = navigation.m_Front.m_Position, NavigationFrontDirection = navigation.m_Front.m_Direction,
                    NavigationRearPosition = navigation.m_Rear.m_Position, NavigationRearDirection = navigation.m_Rear.m_Direction,
                    CurrentLaneDuration = current.m_Duration, CurrentLaneDistance = current.m_Distance,
                    FrontCurvePosition = current.m_Front.m_CurvePosition, RearCurvePositions = current.m_Rear.m_CurvePosition,
                    FrontCacheCurvePosition = current.m_FrontCache.m_CurvePosition, RearCacheCurvePosition = current.m_RearCache.m_CurvePosition,
                    FrontCacheLaneFlags = (uint)current.m_FrontCache.m_LaneFlags, RearCacheLaneFlags = (uint)current.m_RearCache.m_LaneFlags
                });
            }

            for (int lineIndex = 0; lineIndex < RouteLines.Length; lineIndex++)
            {
                Entity line = RouteLines[lineIndex];
                if (!RouteSegmentData.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments)
                    || !RouteWaypointData.TryGetBuffer(line, out DynamicBuffer<RouteWaypoint> waypoints)) continue;
                Entity linePrefab = PrefabRefData.HasComponent(line) ? PrefabRefData[line].m_Prefab : Entity.Null;
                TransportLineData transport = TransportLinePrefabData.HasComponent(line) ? TransportLinePrefabData[line]
                    : TransportLinePrefabData.HasComponent(linePrefab) ? TransportLinePrefabData[linePrefab] : default;
                ulong signature = 1469598103934665603UL;
                signature = TrackBuild.MixLineTrackChainSignature(signature, line.Index);
                signature = TrackBuild.MixLineTrackChainSignature(signature, waypoints.Length);
                signature = TrackBuild.MixLineTrackChainSignature(signature, segments.Length);
                for (int waypointIndex = 0; waypointIndex < waypoints.Length; waypointIndex++)
                {
                    Entity waypoint = waypoints[waypointIndex].m_Waypoint;
                    signature = TrackBuild.MixLineTrackChainSignature(signature, waypoint.Index);
                    if (RouteLaneData.HasComponent(waypoint))
                    {
                        RouteLane routeLane = RouteLaneData[waypoint];
                        signature = TrackBuild.MixLineTrackChainSignature(signature, routeLane.m_StartLane.Index);
                        signature = TrackBuild.MixLineTrackChainSignature(signature, routeLane.m_EndLane.Index);
                        signature = TrackBuild.MixLineTrackChainSignature(signature, (int)Unity.Mathematics.math.round(routeLane.m_StartCurvePos * 1000f));
                        signature = TrackBuild.MixLineTrackChainSignature(signature, (int)Unity.Mathematics.math.round(routeLane.m_EndCurvePos * 1000f));
                    }
                }
                for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
                {
                    Entity segment = segments[segmentIndex].m_Segment;
                    Entity toWaypoint = waypoints.Length == 0 ? Entity.Null : waypoints[(segmentIndex + 1) % waypoints.Length].m_Waypoint;
                    bool boardingWaypoint = ConnectedData.TryGetComponent(toWaypoint, out Connected connected)
                        && BoardingVehicleData.HasComponent(connected.m_Connected);
                    signature = TrackBuild.MixLineTrackChainSignature(signature, segment.Index);
                    PathFlags state = PathInformationData.HasComponent(segment) ? PathInformationData[segment].m_State : default;
                    bool hasPath = PathElementData.TryGetBuffer(segment, out DynamicBuffer<PathElement> path) && path.Length > 0;
                    bool available = hasPath && (state & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) == 0;
                    if (Segments.Length >= RailEtaLimits.MaxEvents) { Overflow.Value = 1; return; }
                    Segments.Add(new RailEtaRouteSegmentRow
                    {
                        Line = line,
                        Segment = segment,
                        FromWaypoint = waypoints.Length == 0 ? Entity.Null : waypoints[segmentIndex % waypoints.Length].m_Waypoint,
                        ToWaypoint = toWaypoint,
                        SegmentIndex = segmentIndex,
                        PathState = (uint)state,
                        PathfindDelayFrames = 0,
                        GeometryAvailable = (byte)(available ? 1 : 0),
                        PathfindDelayKnown = 0,
                        // Station identity is still needed to choose the ETA endpoint. Theory returns
                        // as soon as the target stops there, before any dwell is started.
                        ToWaypointBoarding = (byte)(boardingWaypoint ? 1 : 0)
                    });
                    if (!hasPath) continue;
                    signature = TrackBuild.MixLineTrackChainSignature(signature, path.Length);
                    ulong routePathSignature = 1469598103934665603UL;
                    ulong routeResourceSignature = 1469598103934665603UL;
                    for (int pathIndex = 0; pathIndex < path.Length; pathIndex++)
                    {
                        PathElement element = path[pathIndex];
                        signature = TrackBuild.MixLineTrackChainSignature(signature, element.m_Target.Index);
                        signature = TrackBuild.MixLineTrackChainSignature(signature, (int)element.m_Flags);
                        if (ClassifyPathElement(element.m_Target, element.m_Flags) == TrackAtomClass.FilteredNoise) continue;
                        if (Paths.Length >= RailEtaLimits.MaxEvents) { Overflow.Value = 1; return; }
                        Paths.Add(new RailEtaRoutePathRow
                        {
                            Line = line,
                            SegmentIndex = segmentIndex,
                            Sequence = pathIndex,
                            Lane = element.m_Target,
                            Start = element.m_TargetDelta.x,
                            End = element.m_TargetDelta.y,
                            PathFlags = (uint)element.m_Flags
                        });
                        AddLane(Entity.Null, line, segmentIndex, element.m_Target, element.m_TargetDelta.x, element.m_TargetDelta.y,
                            pathIndex, 5, 0, (uint)element.m_Flags, ref routePathSignature, ref routeResourceSignature);
                    }
                }
                Lines.Add(new RailEtaLineRouteRow
                {
                    LineOrdinal = lineIndex, Line = line,
                    SegmentCount = segments.Length,
                    WaypointCount = waypoints.Length,
                    ChainSignature = signature,
                    IsPassenger = (byte)(transport.m_PassengerTransport ? 1 : 0)
                });
            }

            for (int laneIndex = 0; laneIndex < RailLanes.Length; laneIndex++)
            {
                ulong globalPathSignature = 1469598103934665603UL;
                ulong globalResourceSignature = 1469598103934665603UL;
                AddLane(Entity.Null, Entity.Null, -1, RailLanes[laneIndex], 0f, 1f, laneIndex, 6, 0, 0,
                    ref globalPathSignature, ref globalResourceSignature);
            }
        }

        private bool ContainsController(Entity entity) { for (int i = 0; i < Controllers.Length; i++) if (Controllers[i] == entity) return true; return false; }

        private void AddCurrentLane(Entity controller, Entity lane, ref int sequence,
            ref ulong pathSignature, ref ulong resourceSignature)
        {
            if (lane == Entity.Null) return;
            for (int i = Lanes.Length - 1; i >= 0; i--)
            {
                RailEtaScopedLaneRow existing = Lanes[i];
                if (existing.Controller != controller) break;
                if (existing.Source != 3 && existing.Lane == lane) return;
            }
            AddLane(controller, Entity.Null, -1, lane, 0f, 1f, sequence++, 0, 0, 0,
                ref pathSignature, ref resourceSignature);
        }

        private void AddLane(Entity controller, Entity line, int routeSegmentIndex, Entity lane, float start, float end, int sequence, byte source,
            uint navFlags, uint pathFlags, ref ulong pathSignature, ref ulong resourceSignature)
        {
            if (Overflow.Value != 0) return;
            TrackAtomClass atomClass = ClassifyPathElement(lane, (PathElementFlags)pathFlags);
            if (atomClass == TrackAtomClass.FilteredNoise) return;
            pathSignature = Mix(pathSignature, lane);
            if (Lanes.Length >= RailEtaLimits.MaxFrozenLaneFacts) { Overflow.Value = 1; return; }
            Curve curve = CurveData.HasComponent(lane) ? CurveData[lane] : default;
            Game.Net.TrackLane track = TrackLaneData.HasComponent(lane) ? TrackLaneData[lane] : default;
            Game.Net.ConnectionLane connection = ConnectionLaneData.HasComponent(lane) ? ConnectionLaneData[lane] : default;
            Game.Net.EdgeLane edge = EdgeLaneData.HasComponent(lane) ? EdgeLaneData[lane] : default;
            bool isTrackLane = TrackLaneData.HasComponent(lane);
            // ConnectionHelper covers real ConnectionLane, EdgeLane helpers, and flag-marked path connectors.
            // Match RailTravel PathQuery / Calculator: connection speed is not TrackLane.m_SpeedLimit.
            bool connectionHelper = atomClass == TrackAtomClass.ConnectionHelper;
            bool isConnectionLane = connectionHelper;
            // Same constant as RailTravel.Calculator.ConnectionSpeed.
            const float connectionSpeed = 277.77777f;
            float speedLimit = isConnectionLane ? connectionSpeed : track.m_SpeedLimit;
            uint trackFlags = isConnectionLane ? 0u : (uint)track.m_Flags;
            float curviness = isConnectionLane ? 0f : track.m_Curviness;
            // Every non-noise PathElement with frozen curve geometry is a valid path identity.
            // Only real TrackLane entities participate in the shared-corridor graph below.
            Entity pathPhysicalLane = CurveData.HasComponent(lane) ? lane : Entity.Null;
            // Helpers never participate in passenger shared-corridor graph.
            Entity sharedPhysicalLane = atomClass == TrackAtomClass.PrimaryLane && isTrackLane ? lane : Entity.Null;
            bool includeControls = Mode == RailEtaMode.Full;
            bool hasReservation = includeControls && ReservationData.HasComponent(lane);
            LaneReservation reservation = hasReservation ? ReservationData[lane] : default;
            bool hasSignal = includeControls && SignalData.HasComponent(lane);
            LaneSignal signal = hasSignal ? SignalData[lane] : default;
            Entity signalController = hasSignal && SignalControllerByLane.TryGetValue(lane, out Entity mappedController)
                ? mappedController : Entity.Null;
            TrafficLights trafficLights = signalController != Entity.Null && TrafficLightsData.HasComponent(signalController)
                ? TrafficLightsData[signalController] : default;
            if ((trafficLights.m_Flags & (TrafficLightFlags.LevelCrossing | TrafficLightFlags.MoveableBridge)) != 0)
            {
                hasSignal = false;
                signal = default;
                signalController = Entity.Null;
                trafficLights = default;
            }
            uint signalUpdateFrameIndex = 0;
            bool hasSignalUpdateFrame = false;
            if (signalController != Entity.Null && EntityLookup.Exists(signalController))
            {
                EntityStorageInfo signalInfo = EntityLookup[signalController];
                if (signalInfo.Chunk.Has(UpdateFrameType))
                {
                    signalUpdateFrameIndex = signalInfo.Chunk.GetSharedComponent(UpdateFrameType).m_Index;
                    hasSignalUpdateFrame = true;
                }
            }
            Entity reservationBlocker = NormalizeRailEntity(reservation.m_Blocker, out byte reservationExternalKind);
            Entity signalPetitioner = NormalizeRailEntity(signal.m_Petitioner, out byte signalPetitionerExternalKind);
            Entity signalBlocker = NormalizeRailEntity(signal.m_Blocker, out byte signalBlockerExternalKind);
            if (reservationExternalKind != 0) { reservation = default; reservationBlocker = Entity.Null; hasReservation = false; }
            if (signalPetitionerExternalKind != 0 || signalBlockerExternalKind != 0) { signal = default; signalPetitioner = Entity.Null; signalBlocker = Entity.Null; }
            reservationExternalKind = 0;
            signalPetitionerExternalKind = 0;
            signalBlockerExternalKind = 0;
            uint updateFrameIndex = 0;
            bool hasUpdateFrame = false;
            if (EntityLookup.Exists(lane))
            {
                EntityStorageInfo info = EntityLookup[lane];
                if (info.Chunk.Has(UpdateFrameType)) { updateFrameIndex = info.Chunk.GetSharedComponent(UpdateFrameType).m_Index; hasUpdateFrame = true; }
            }
            Lanes.Add(new RailEtaScopedLaneRow
            {
                LaneOrdinal = source == 6 ? sequence : -1, Controller = controller, Line = line, RouteSegmentIndex = routeSegmentIndex, Lane = lane, Sequence = sequence, CurveStart = start, CurveEnd = end, Source = source,
                Length = curve.m_Length, SpeedLimit = speedLimit, Curviness = curviness, TrackFlags = trackFlags, NavigationFlags = navFlags, IsConnectionLane = (byte)(isConnectionLane ? 1 : 0),
                TrackAccessRestriction = track.m_AccessRestriction, ConnectionAccessRestriction = connection.m_AccessRestriction, ConnectionFlags = (uint)connection.m_Flags,
                ConnectionTrackTypes = (uint)connection.m_TrackTypes, ConnectionRoadTypes = (uint)connection.m_RoadTypes, EdgeDelta = edge.m_EdgeDelta,
                EdgeConnectedStartCount = edge.m_ConnectedStartCount, EdgeConnectedEndCount = edge.m_ConnectedEndCount,
                PathPhysicalLane = pathPhysicalLane, SharedPhysicalLane = sharedPhysicalLane, ParticipatesInSharedCorridor = (byte)(sharedPhysicalLane != Entity.Null ? 1 : 0),
                PathFlags = pathFlags,
                CurveA = curve.m_Bezier.a, CurveB = curve.m_Bezier.b, CurveC = curve.m_Bezier.c, CurveD = curve.m_Bezier.d,
                Curve = curve, TrackLane = track, ConnectionLane = connection, Reservation = reservation, Signal = signal,
                ReservationBlocker = reservationBlocker, ReservationExternalKind = reservationExternalKind, PreviousPriority = reservation.m_Prev.m_Priority, PreviousOffset = reservation.m_Prev.m_Offset,
                NextPriority = reservation.m_Next.m_Priority, NextOffset = reservation.m_Next.m_Offset,
                SignalPetitioner = signalPetitioner, SignalBlocker = signalBlocker, SignalPetitionerExternalKind = signalPetitionerExternalKind, SignalBlockerExternalKind = signalBlockerExternalKind, SignalFlags = (byte)signal.m_Flags, SignalPriority = signal.m_Priority,
                SignalType = (byte)signal.m_Signal, SignalGroupMask = signal.m_GroupMask, SignalDefault = signal.m_Default,
                SignalController = signalController, SignalUpdateFrameIndex = signalUpdateFrameIndex, TrafficLights = trafficLights,
                HasSignalUpdateFrame = (byte)(hasSignalUpdateFrame ? 1 : 0),
                UpdateFrameIndex = updateFrameIndex, HasReservation = (byte)(hasReservation ? 1 : 0), HasUpdateFrame = (byte)(hasUpdateFrame ? 1 : 0), HasSignal = (byte)(hasSignal ? 1 : 0),
                HasCurve = (byte)(CurveData.HasComponent(lane) ? 1 : 0), HasTrackLane = (byte)(isTrackLane ? 1 : 0),
                HasConnectionLane = (byte)(ConnectionLaneData.HasComponent(lane) ? 1 : 0)
            });
            if (Mode != RailEtaMode.Theory && LaneObjectData.TryGetBuffer(lane, out DynamicBuffer<LaneObject> laneObjects))
            {
                for (int i = 0; i < laneObjects.Length; i++)
                {
                    Entity occupant = laneObjects[i].m_LaneObject;
                    Entity occupantController = ControllerData.HasComponent(occupant) ? ControllerData[occupant].m_Controller : occupant;
                    if (!ContainsController(occupantController)) continue;
                    if (Occupancies.Length >= RailEtaLimits.MaxFrozenLaneOccupancies) { Overflow.Value = 1; return; }
                    // Scope/public identity stays on the controller; simulation occupancy playback
                    // retains the LaneObject unit identity used by CurrentLaneCache.
                    Occupancies.Add(new RailEtaLaneOccupancyRow { Lane = lane, Vehicle = occupantController, Unit = occupant, Start = laneObjects[i].m_CurvePosition.x, End = laneObjects[i].m_CurvePosition.y });
                }
            }
            if (!includeControls || !LaneOverlapData.TryGetBuffer(lane, out DynamicBuffer<LaneOverlap> overlaps)) return;
            for (int i = 0; i < overlaps.Length; i++)
            {
                LaneOverlap overlap = overlaps[i];
                if (ClassifyPathElement(overlap.m_Other, 0) == TrackAtomClass.FilteredNoise) continue;
                resourceSignature = Mix(Mix(resourceSignature, lane), overlap.m_Other);
                if (Lanes.Length >= RailEtaLimits.MaxFrozenLaneFacts) { Overflow.Value = 1; return; }
                Lanes.Add(new RailEtaScopedLaneRow
                {
                    LaneOrdinal = source == 6 ? sequence : -1, Controller = controller, Line = line, RouteSegmentIndex = routeSegmentIndex, Lane = lane, OtherLane = overlap.m_Other, Sequence = sequence, Source = 3,
                    OverlapPriorityDelta = overlap.m_PriorityDelta, OverlapThisStart = overlap.m_ThisStart, OverlapThisEnd = overlap.m_ThisEnd,
                    OverlapOtherStart = overlap.m_OtherStart, OverlapOtherEnd = overlap.m_OtherEnd, OverlapFlags = (ushort)overlap.m_Flags, OverlapParallelism = overlap.m_Parallelism
                });
            }
        }

        private static ulong Mix(ulong hash, Entity value) { hash ^= (uint)value.Index; hash *= 1099511628211UL; hash ^= (uint)value.Version; return hash * 1099511628211UL; }

        private void BuildSignalControllerIndex()
        {
            for (int i = 0; i < TrafficLightControllers.Length; i++)
            {
                Entity controller = TrafficLightControllers[i];
                if (!TrafficLightsData.TryGetComponent(controller, out TrafficLights lights)
                    || (lights.m_Flags & TrafficLightFlags.IsSubNode) != 0) continue;
                bool excludedRoadControl = (lights.m_Flags & (TrafficLightFlags.LevelCrossing | TrafficLightFlags.MoveableBridge)) != 0;
                if (!EntityLookup.Exists(controller)) continue;
                EntityStorageInfo controllerInfo = EntityLookup[controller];
                if (!controllerInfo.Chunk.Has(UpdateFrameType)) continue;
                uint updateFrameIndex = controllerInfo.Chunk.GetSharedComponent(UpdateFrameType).m_Index;
                if (!SubLaneData.TryGetBuffer(controller, out DynamicBuffer<Game.Net.SubLane> subLanes)) continue;
                for (int j = 0; j < subLanes.Length; j++)
                {
                    Entity lane = subLanes[j].m_SubLane;
                    if (lane == Entity.Null) continue;
                    bool railPeer = TrackLaneData.HasComponent(lane);
                    if (!railPeer && ConnectionLaneData.TryGetComponent(lane, out Game.Net.ConnectionLane connection))
                        railPeer = connection.m_TrackTypes != TrackTypes.None;
                    if (!railPeer) continue;
                    if (!SignalData.TryGetComponent(lane, out LaneSignal signal)) continue;
                    SignalControllerByLane.TryAdd(lane, controller);
                    if (excludedRoadControl) continue;
                    if (SignalPeers.Length >= RailEtaLimits.MaxFrozenLaneFacts) { Overflow.Value = 1; return; }
                    SignalPeers.Add(new RailEtaSignalPeerRow { Lane = lane, Controller = controller,
                        UpdateFrameIndex = updateFrameIndex, TrafficLights = lights, Signal = signal });
                }
            }
        }

        private TrackAtomClass ClassifyPathElement(Entity lane, PathElementFlags flags)
        {
            if (lane == Entity.Null) return TrackAtomClass.FilteredNoise;
            bool hasConnection = ConnectionLaneData.HasComponent(lane);
            bool hasTrack = TrackLaneData.HasComponent(lane);
            bool hasEdge = EdgeLaneData.HasComponent(lane);
            // Compact snapshots do not have the global lane index that Full uses. A path marker
            // with no Curve and no vanilla lane component carries no traversable geometry.
            if (Mode != RailEtaMode.Full && !CurveData.HasComponent(lane) && !hasTrack && !hasConnection && !hasEdge)
                return TrackAtomClass.FilteredNoise;
            TrackTypes connectionTypes = hasConnection ? ConnectionLaneData[lane].m_TrackTypes : TrackTypes.None;
            return TrackBuild.ClassifyPathElementTarget(flags, hasTrack, hasConnection, connectionTypes, hasEdge);
        }
        private Entity NormalizeRailEntity(Entity entity, out byte externalKind)
        {
            externalKind = 0;
            if (ControllerData.HasComponent(entity)) entity = ControllerData[entity].m_Controller;
            if (entity == Entity.Null || ContainsController(entity)) return entity;
            externalKind = CarData.HasComponent(entity) ? (byte)2 : CreatureData.HasComponent(entity) ? (byte)3 : (byte)5;
            return Entity.Null;
        }
    }
}
