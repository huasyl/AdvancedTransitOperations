using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class RailEventSource : IDisposable
    {
        private readonly struct RailSnapshot : IEquatable<RailSnapshot>
        {
            public readonly bool Exists;
            public readonly bool HasPublicTransport;
            public readonly PublicTransport Transport;
            public readonly bool HasTarget;
            public readonly Target TargetData;
            public readonly bool HasCurrentRoute;
            public readonly Entity Route;
            public readonly bool HasPathOwner;
            public readonly PathOwner Path;
            public readonly bool HasPathElements;
            public readonly int PathElementCount;
            public readonly ulong PathElementSignature;
            public readonly bool HasTrainCurrentLane;
            public readonly Entity FrontLane;
            public readonly Entity RearLane;
            public readonly TrainLaneFlags FrontLaneFlags;
            public readonly TrainLaneFlags RearLaneFlags;
            public readonly float FrontCurvePosition;
            public readonly float RearCurvePosition;
            public readonly bool HasTrainNavigationLanes;
            public readonly int NavigationLaneCount;
            public readonly ulong NavigationLaneSignature;
            public readonly bool HasMoving;
            public readonly float VelocityX;
            public readonly float VelocityY;
            public readonly float VelocityZ;
            public readonly bool HasTransform;
            public readonly float PositionX;
            public readonly float PositionY;
            public readonly float PositionZ;
            public readonly bool HasOdometer;
            public readonly float Odometer;

            public PublicTransportFlags State => Transport.m_State;
            public Entity Target => TargetData.m_Target;
            public PathFlags PathState => Path.m_State;
            public int PathElementIndex => Path.m_ElementIndex;

            public RailSnapshot(bool exists, bool hasPublicTransport, PublicTransport transport, bool hasTarget, Target target, bool hasCurrentRoute, Entity route,
                bool hasPathOwner, PathOwner path, bool hasPathElements, int pathElementCount, ulong pathElementSignature,
                bool hasTrainCurrentLane, Entity frontLane, Entity rearLane, TrainLaneFlags frontLaneFlags, TrainLaneFlags rearLaneFlags,
                float frontCurvePosition, float rearCurvePosition, bool hasTrainNavigationLanes, int navigationLaneCount,
                ulong navigationLaneSignature, bool hasMoving, float velocityX, float velocityY, float velocityZ,
                bool hasTransform, float positionX, float positionY, float positionZ, bool hasOdometer, float odometer)
            {
                Exists = exists; HasPublicTransport = hasPublicTransport; Transport = transport; HasTarget = hasTarget; TargetData = target; HasCurrentRoute = hasCurrentRoute; Route = route;
                HasPathOwner = hasPathOwner; Path = path; HasPathElements = hasPathElements; PathElementCount = pathElementCount; PathElementSignature = pathElementSignature;
                HasTrainCurrentLane = hasTrainCurrentLane; FrontLane = frontLane; RearLane = rearLane;
                FrontLaneFlags = frontLaneFlags; RearLaneFlags = rearLaneFlags; FrontCurvePosition = frontCurvePosition; RearCurvePosition = rearCurvePosition;
                HasTrainNavigationLanes = hasTrainNavigationLanes; NavigationLaneCount = navigationLaneCount; NavigationLaneSignature = navigationLaneSignature;
                HasMoving = hasMoving; VelocityX = velocityX; VelocityY = velocityY; VelocityZ = velocityZ;
                HasTransform = hasTransform; PositionX = positionX; PositionY = positionY; PositionZ = positionZ; HasOdometer = hasOdometer; Odometer = odometer;
            }

            public RailSnapshot WithPublicTransport(PublicTransport transport) => new RailSnapshot(Exists, true, transport, HasTarget, TargetData, HasCurrentRoute, Route,
                HasPathOwner, Path, HasPathElements, PathElementCount, PathElementSignature, HasTrainCurrentLane, FrontLane, RearLane,
                FrontLaneFlags, RearLaneFlags, FrontCurvePosition, RearCurvePosition, HasTrainNavigationLanes, NavigationLaneCount,
                NavigationLaneSignature, HasMoving, VelocityX, VelocityY, VelocityZ, HasTransform, PositionX, PositionY, PositionZ, HasOdometer, Odometer);
            public RailSnapshot WithTarget(Target target) => new RailSnapshot(Exists, HasPublicTransport, Transport, true, target, HasCurrentRoute, Route,
                HasPathOwner, Path, HasPathElements, PathElementCount, PathElementSignature, HasTrainCurrentLane, FrontLane, RearLane,
                FrontLaneFlags, RearLaneFlags, FrontCurvePosition, RearCurvePosition, HasTrainNavigationLanes, NavigationLaneCount,
                NavigationLaneSignature, HasMoving, VelocityX, VelocityY, VelocityZ, HasTransform, PositionX, PositionY, PositionZ, HasOdometer, Odometer);
            public RailSnapshot WithPath(PathOwner path, bool hasPathElements, int pathElementCount, ulong pathElementSignature) => new RailSnapshot(Exists, HasPublicTransport, Transport, HasTarget, TargetData, HasCurrentRoute, Route,
                true, path, hasPathElements, pathElementCount, pathElementSignature, HasTrainCurrentLane, FrontLane, RearLane,
                FrontLaneFlags, RearLaneFlags, FrontCurvePosition, RearCurvePosition, HasTrainNavigationLanes, NavigationLaneCount,
                NavigationLaneSignature, HasMoving, VelocityX, VelocityY, VelocityZ, HasTransform, PositionX, PositionY, PositionZ, HasOdometer, Odometer);

            public bool Equals(RailSnapshot other) => Exists == other.Exists
                && !TransportChanged(other) && HasTarget == other.HasTarget && Target == other.Target
                && HasCurrentRoute == other.HasCurrentRoute && Route == other.Route
                && HasPathOwner == other.HasPathOwner && PathState == other.PathState && PathElementIndex == other.PathElementIndex
                && HasPathElements == other.HasPathElements && PathElementCount == other.PathElementCount && PathElementSignature == other.PathElementSignature
                && HasTrainCurrentLane == other.HasTrainCurrentLane && FrontLane == other.FrontLane && RearLane == other.RearLane && FrontLaneFlags == other.FrontLaneFlags && RearLaneFlags == other.RearLaneFlags
                && FrontCurvePosition == other.FrontCurvePosition && RearCurvePosition == other.RearCurvePosition
                && HasTrainNavigationLanes == other.HasTrainNavigationLanes && NavigationLaneCount == other.NavigationLaneCount && NavigationLaneSignature == other.NavigationLaneSignature
                && HasMoving == other.HasMoving && VelocityX == other.VelocityX && VelocityY == other.VelocityY && VelocityZ == other.VelocityZ
                && HasTransform == other.HasTransform && PositionX == other.PositionX && PositionY == other.PositionY && PositionZ == other.PositionZ
                && HasOdometer == other.HasOdometer && Odometer == other.Odometer;
            public bool TransportChanged(RailSnapshot other) => HasPublicTransport != other.HasPublicTransport
                || State != other.State
                || Transport.m_DepartureFrame != other.Transport.m_DepartureFrame
                || Transport.m_MinWaitingDistance != other.Transport.m_MinWaitingDistance
                || Transport.m_MaxBoardingDistance != other.Transport.m_MaxBoardingDistance
                || Transport.m_RequestCount != other.Transport.m_RequestCount;
        }

        private readonly struct RailFrameFact
        {
            public readonly bool HadPreviousRoute;
            public readonly bool HasCurrentRoute;
            public readonly Entity Route;
            public readonly bool BoardingKnown;
            public readonly bool Boarding;
            public readonly bool PreviousMoving;
            public readonly bool CurrentMoving;
            public readonly int PreviousWaypointIndex;
            public readonly int CurrentWaypointIndex;
            public readonly int WaypointCount;
            public readonly bool PathReady;

            public RailFrameFact(
                bool hadPreviousRoute,
                bool hasCurrentRoute,
                Entity route,
                bool boardingKnown,
                bool boarding,
                bool previousMoving,
                bool currentMoving,
                int previousWaypointIndex,
                int currentWaypointIndex,
                int waypointCount,
                bool pathReady)
            {
                HadPreviousRoute = hadPreviousRoute;
                HasCurrentRoute = hasCurrentRoute;
                Route = route;
                BoardingKnown = boardingKnown;
                Boarding = boarding;
                PreviousMoving = previousMoving;
                CurrentMoving = currentMoving;
                PreviousWaypointIndex = previousWaypointIndex;
                CurrentWaypointIndex = currentWaypointIndex;
                WaypointCount = waypointCount;
                PathReady = pathReady;
            }
        }

        private sealed class RailWriteShadow
        {
            public bool PublicTransportStateKnown;
            public PublicTransportFlags PublicTransportState;
            public bool DepartureFrameKnown;
            public uint DepartureFrame;
            public bool MinWaitingDistanceKnown;
            public float MinWaitingDistance;
            public bool MaxBoardingDistanceKnown;
            public float MaxBoardingDistance;
            public bool RequestCountKnown;
            public int RequestCount;
            public bool TargetKnown;
            public Target Target;
            public bool PathStateKnown;
            public PathFlags PathState;
            public bool PathElementIndexKnown;
            public int PathElementIndex;
            public bool PathBufferKnown;
            public bool HasPathElements;
            public bool PathElementCountKnown;
            public int PathElementCount;
            public bool PathSignatureKnown;
            public ulong PathSignature;

            public void SetPublicTransport(PublicTransport value)
            {
                PublicTransportStateKnown = true;
                PublicTransportState = value.m_State;
                DepartureFrameKnown = true;
                DepartureFrame = value.m_DepartureFrame;
                MinWaitingDistanceKnown = true;
                MinWaitingDistance = value.m_MinWaitingDistance;
                MaxBoardingDistanceKnown = true;
                MaxBoardingDistance = value.m_MaxBoardingDistance;
                RequestCountKnown = true;
                RequestCount = value.m_RequestCount;
            }

            public bool TryGetPublicTransport(out PublicTransport value)
            {
                if (!PublicTransportStateKnown || !DepartureFrameKnown || !MinWaitingDistanceKnown || !MaxBoardingDistanceKnown || !RequestCountKnown)
                {
                    value = default;
                    return false;
                }

                value = new PublicTransport
                {
                    m_State = PublicTransportState,
                    m_DepartureFrame = DepartureFrame,
                    m_MinWaitingDistance = MinWaitingDistance,
                    m_MaxBoardingDistance = MaxBoardingDistance,
                    m_RequestCount = RequestCount
                };
                return true;
            }

            public void SetPath(PathOwner value, bool hasPathElements, int pathElementCount, ulong pathSignature)
            {
                PathStateKnown = true;
                PathState = value.m_State;
                PathElementIndexKnown = true;
                PathElementIndex = value.m_ElementIndex;
                PathBufferKnown = true;
                HasPathElements = hasPathElements;
                PathElementCountKnown = true;
                PathElementCount = pathElementCount;
                PathSignatureKnown = true;
                PathSignature = RailEventSource.NormalizePathSignature(hasPathElements, pathElementCount, pathSignature);
            }

            public bool TryGetPath(out PathOwner value, out bool hasPathElements, out int pathElementCount)
            {
                if (!PathStateKnown || !PathElementIndexKnown || !PathBufferKnown || !PathElementCountKnown)
                {
                    value = default;
                    hasPathElements = false;
                    pathElementCount = 0;
                    return false;
                }

                value = new PathOwner { m_State = PathState, m_ElementIndex = PathElementIndex };
                hasPathElements = PathBufferKnown && HasPathElements;
                pathElementCount = PathElementCount;
                return true;
            }
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly FrameEvents m_Events;
        // 来源帧差分基线跨帧保留；车辆删除和 ResetTracking 时清除。
        private readonly Dictionary<Entity, RailSnapshot> m_LastSnapshots = new Dictionary<Entity, RailSnapshot>();
        private readonly Dictionary<Entity, HashSet<Entity>> m_RouteMembers = new Dictionary<Entity, HashSet<Entity>>();
        // 本帧主动写入影子在 BeginFrame 清除，避免回读尚未回放的 ECS 旧值。
        private readonly Dictionary<Entity, RailWriteShadow> m_ModWrites = new Dictionary<Entity, RailWriteShadow>();
        // 本帧按 Sequence 提供的 Rail 专用事实在 BeginFrame 清除。
        private readonly Dictionary<ulong, RailFrameFact> m_RailFrameFacts = new Dictionary<ulong, RailFrameFact>();
        // 来源代次跨帧保留；车辆删除和 ResetTracking 时清除。
        private readonly Dictionary<Entity, ulong> m_SourceGenerations = new Dictionary<Entity, ulong>();
        private uint m_LastCollectedFrame = uint.MaxValue;

        public const ulong EmptyPathSignature = 1469598103934665603UL;

        public RailEventSource(ModRuntimeHostSystem runtime, FrameEvents events)
        {
            m_Runtime = runtime;
            m_Events = events;
        }

        public void BeginFrame()
        {
            // 仅清当前 Host 帧数据，不清来源基线或来源代次。
            m_ModWrites.Clear();
            m_RailFrameFacts.Clear();
        }

        public void CollectIfDue(uint frame)
        {
            if ((frame & 15u) != 3u) return;
            m_LastCollectedFrame = frame;
            m_RouteMembers.Clear();
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    RailSnapshot snapshot = ReadSnapshot(vehicle);
                    ulong sourceGeneration = AdvanceSourceGeneration(vehicle);
                    PublishChanged(vehicle, snapshot, frame, sourceGeneration);
                    ObserveRouteMembership(vehicle, snapshot.Route);
                }
            }
            finally
            {
                vehicles.Dispose();
                PruneSnapshots();
            }
        }

        public bool CollectedThisFrame(uint frame) => m_LastCollectedFrame == frame;

        public ulong RegisterSource(Entity vehicle)
        {
            if (vehicle == Entity.Null) return 0UL;
            return AdvanceSourceGeneration(vehicle);
        }

        public ulong RebindSource(Entity vehicle)
        {
            if (vehicle == Entity.Null) return 0UL;
            m_LastSnapshots.Remove(vehicle);
            m_ModWrites.Remove(vehicle);
            return AdvanceSourceGeneration(vehicle);
        }

        public ulong CurrentSourceGeneration(Entity vehicle)
            => m_SourceGenerations.TryGetValue(vehicle, out ulong value) ? value : 0UL;


        public void RemoveVehicle(Entity vehicle)
        {
            // 车辆删除同时清理该实体的本帧影子、跨帧基线和来源代次。
            m_LastSnapshots.Remove(vehicle);
            m_ModWrites.Remove(vehicle);
            m_SourceGenerations.Remove(vehicle);
        }

        public void AppendPublicTransportWrite(Entity vehicle, PublicTransport publicTransport, uint frame)
        {
            if (vehicle == Entity.Null) return;
            RailWriteShadow shadow = WriteShadow(vehicle);
            bool hasPrevious = shadow.TryGetPublicTransport(out PublicTransport previous)
                || TryReadLastPublicTransport(vehicle, out previous);
            ulong sourceGeneration = AdvanceSourceGeneration(vehicle);
            shadow.SetPublicTransport(publicTransport);
            UpdateLastPublicTransport(vehicle, publicTransport);
            if (hasPrevious
                && ((previous.m_State & PublicTransportFlags.Boarding) != (publicTransport.m_State & PublicTransportFlags.Boarding)))
            {
                AppendWriteFact(vehicle, frame, VehicleFactKind.Boarding, previous, publicTransport, sourceGeneration);
            }
        }

        public void AppendTargetWrite(Entity vehicle, Target target, uint frame)
        {
            if (vehicle == Entity.Null) return;
            RailWriteShadow shadow = WriteShadow(vehicle);
            bool hasPrevious = shadow.TargetKnown;
            Target previous = shadow.Target;
            if (!hasPrevious && m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot baseline) && baseline.HasTarget)
            {
                hasPrevious = true;
                previous = baseline.TargetData;
            }
            ulong sourceGeneration = AdvanceSourceGeneration(vehicle);
            shadow.TargetKnown = true;
            shadow.Target = target;
            UpdateLastTarget(vehicle, target);
            if (hasPrevious && previous.m_Target != target.m_Target)
                AppendTargetWriteFact(vehicle, frame, sourceGeneration);
        }

        public void AppendPathWrite(Entity vehicle, PathOwner pathOwner, bool hasPathElements, int pathElementCount, ulong pathElementSignature, uint frame)
        {
            if (vehicle == Entity.Null) return;
            RailWriteShadow shadow = WriteShadow(vehicle);
            bool hasPrevious = shadow.TryGetPath(out PathOwner previousPath, out bool previousHasPathElements, out int previousCount)
                || TryReadLastPath(vehicle, out previousPath, out previousHasPathElements, out previousCount);
            ulong previousSignature = shadow.PathSignatureKnown
                ? shadow.PathSignature
                : TryReadLastPathSignature(vehicle, out ulong baselineSignature) ? baselineSignature : 0UL;
            ulong sourceGeneration = AdvanceSourceGeneration(vehicle);
            ulong currentSignature = NormalizePathSignature(hasPathElements, pathElementCount, pathElementSignature);
            shadow.SetPath(pathOwner, hasPathElements, pathElementCount, pathElementSignature);
            UpdateLastPath(vehicle, pathOwner, hasPathElements, pathElementCount, pathElementSignature);
            bool previousReady = hasPrevious && PathReady(previousHasPathElements, previousCount);
            bool currentReady = PathReady(hasPathElements, pathElementCount);
            if (hasPrevious
                && (previousPath.m_State != pathOwner.m_State
                    || previousPath.m_ElementIndex != pathOwner.m_ElementIndex
                    || previousHasPathElements != hasPathElements
                    || previousCount != pathElementCount
                    || previousSignature != currentSignature))
            {
                AppendPathWriteFact(vehicle, frame, sourceGeneration, previousReady, currentReady);
            }
        }

        public bool TryReadPublicTransport(Entity vehicle, out PublicTransport publicTransport)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow)
                && shadow.TryGetPublicTransport(out publicTransport))
            {
                return true;
            }
            publicTransport = default;
            return false;
        }

        public bool TryReadTarget(Entity vehicle, out Target target)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow) && shadow.TargetKnown)
            {
                target = shadow.Target;
                return true;
            }
            target = default;
            return false;
        }

        public bool TryReadPath(Entity vehicle, out PathOwner pathOwner)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow)
                && shadow.TryGetPath(out pathOwner, out _, out _))
            {
                return true;
            }
            pathOwner = default;
            return false;
        }

        public PublicTransport ReadPublicTransport(Entity vehicle) => TryReadPublicTransport(vehicle, out PublicTransport value)
            ? value : m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle);
        public Target ReadTarget(Entity vehicle) => TryReadTarget(vehicle, out Target value)
            ? value : m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
        public PathOwner ReadPath(Entity vehicle) => TryReadPath(vehicle, out PathOwner value)
            ? value : m_Runtime.EntityManager.GetComponentData<PathOwner>(vehicle);
        public int ReadPathElementCount(Entity vehicle)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow)
                && shadow.TryGetPath(out _, out _, out int pathElementCount))
            {
                return pathElementCount;
            }
            return m_Runtime.EntityManager.Exists(vehicle) && m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle)
                ? m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true).Length
                : 0;
        }

        public bool TryGetRailRoute(ulong sequence, out bool hadPreviousRoute, out bool hasCurrentRoute, out Entity route)
        {
            if (m_RailFrameFacts.TryGetValue(sequence, out RailFrameFact railFact))
            {
                hadPreviousRoute = railFact.HadPreviousRoute;
                hasCurrentRoute = railFact.HasCurrentRoute;
                route = railFact.Route;
                return true;
            }
            hadPreviousRoute = false;
            hasCurrentRoute = false;
            route = Entity.Null;
            return false;
        }

        public bool TryGetRailBoarding(ulong sequence, out bool boardingKnown, out bool boarding)
        {
            if (m_RailFrameFacts.TryGetValue(sequence, out RailFrameFact railFact))
            {
                boardingKnown = railFact.BoardingKnown;
                boarding = railFact.Boarding;
                return true;
            }
            boardingKnown = false;
            boarding = false;
            return false;
        }

        public bool TryGetRailMotion(ulong sequence, out bool previousMoving, out bool currentMoving)
        {
            if (m_RailFrameFacts.TryGetValue(sequence, out RailFrameFact fact))
            {
                previousMoving = fact.PreviousMoving;
                currentMoving = fact.CurrentMoving;
                return true;
            }
            previousMoving = false;
            currentMoving = false;
            return false;
        }

        public bool TryGetRailProgress(ulong sequence, out int previousWaypointIndex, out int currentWaypointIndex, out int waypointCount)
        {
            if (m_RailFrameFacts.TryGetValue(sequence, out RailFrameFact fact))
            {
                previousWaypointIndex = fact.PreviousWaypointIndex;
                currentWaypointIndex = fact.CurrentWaypointIndex;
                waypointCount = fact.WaypointCount;
                return true;
            }
            previousWaypointIndex = -1;
            currentWaypointIndex = -1;
            waypointCount = 0;
            return false;
        }

        public bool TryGetRailPath(ulong sequence, out bool pathReady)
        {
            if (m_RailFrameFacts.TryGetValue(sequence, out RailFrameFact fact))
            {
                pathReady = fact.PathReady;
                return true;
            }
            pathReady = false;
            return false;
        }

        public static ulong PathSignature(DynamicBuffer<PathElement> path)
        {
            ulong signature = EmptyPathSignature;
            for (int i = 0; i < path.Length; i++)
            {
                signature = Mix(signature, (uint)path[i].m_Target.Index);
                signature = Mix(signature, (uint)path[i].m_Target.Version);
                signature = Mix(signature, math.asuint(path[i].m_TargetDelta.x));
                signature = Mix(signature, math.asuint(path[i].m_TargetDelta.y));
                signature = Mix(signature, (uint)path[i].m_Flags);
            }
            return signature;
        }

        public void ResetTracking()
        {
            // 城市重置清理全部本帧数据、来源基线和来源代次。
            m_ModWrites.Clear();
            m_LastSnapshots.Clear();
            m_RouteMembers.Clear();
            m_RailFrameFacts.Clear();
            m_SourceGenerations.Clear();
            m_LastCollectedFrame = uint.MaxValue;
        }

        public void Dispose() => ResetTracking();

        private RailSnapshot ReadSnapshot(Entity vehicle)
        {
            if (!m_Runtime.EntityManager.Exists(vehicle)) return default;
            bool hasPublicTransport = m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle);
            PublicTransport publicTransport = hasPublicTransport ? m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle) : default;
            bool hasTarget = m_Runtime.EntityManager.HasComponent<Target>(vehicle);
            Target target = hasTarget ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle) : default;
            bool hasCurrentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle);
            Entity route = hasCurrentRoute ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route : Entity.Null;
            bool hasPathOwner = m_Runtime.EntityManager.HasComponent<PathOwner>(vehicle);
            PathOwner pathOwner = hasPathOwner ? m_Runtime.EntityManager.GetComponentData<PathOwner>(vehicle) : default;
            bool hasPathElements = m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle);
            int pathCount = 0;
            ulong pathSignature = 0UL;
            if (hasPathElements)
            {
                DynamicBuffer<PathElement> path = m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true);
                pathCount = path.Length;
                pathSignature = PathSignature(path);
            }
            bool hasTrainCurrentLane = m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(vehicle);
            TrainCurrentLane lane = hasTrainCurrentLane ? m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(vehicle) : default;
            bool hasTrainNavigationLanes = m_Runtime.EntityManager.HasBuffer<TrainNavigationLane>(vehicle);
            int navigationCount = 0;
            ulong navigationSignature = 0UL;
            if (hasTrainNavigationLanes)
            {
                DynamicBuffer<TrainNavigationLane> navigation = m_Runtime.EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true);
                navigationCount = navigation.Length;
                navigationSignature = NavigationSignature(navigation);
            }
            bool hasMoving = m_Runtime.EntityManager.HasComponent<Moving>(vehicle);
            Moving movingData = hasMoving ? m_Runtime.EntityManager.GetComponentData<Moving>(vehicle) : default;
            bool hasTransform = m_Runtime.EntityManager.HasComponent<Transform>(vehicle);
            Transform transform = hasTransform ? m_Runtime.EntityManager.GetComponentData<Transform>(vehicle) : default;
            bool hasOdometer = m_Runtime.EntityManager.HasComponent<Odometer>(vehicle);
            float odometer = hasOdometer ? m_Runtime.EntityManager.GetComponentData<Odometer>(vehicle).m_Distance : 0f;
            RailSnapshot snapshot = new RailSnapshot(true, hasPublicTransport, publicTransport, hasTarget, target, hasCurrentRoute, route, hasPathOwner, pathOwner,
                hasPathElements, pathCount, pathSignature, hasTrainCurrentLane, lane.m_Front.m_Lane, lane.m_Rear.m_Lane,
                lane.m_Front.m_LaneFlags, lane.m_Rear.m_LaneFlags, lane.m_Front.m_CurvePosition.x, lane.m_Rear.m_CurvePosition.x,
                hasTrainNavigationLanes, navigationCount, navigationSignature, hasMoving, movingData.m_Velocity.x,
                movingData.m_Velocity.y, movingData.m_Velocity.z, hasTransform, transform.m_Position.x, transform.m_Position.y,
                transform.m_Position.z, hasOdometer, odometer);
            return ApplyWriteShadow(snapshot, vehicle);
        }

        private RailSnapshot ApplyWriteShadow(RailSnapshot snapshot, Entity vehicle)
        {
            if (!m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow))
                return snapshot;
            if (shadow.TryGetPublicTransport(out PublicTransport publicTransport))
                snapshot = snapshot.WithPublicTransport(publicTransport);
            if (shadow.TargetKnown)
                snapshot = snapshot.WithTarget(shadow.Target);
            if (shadow.TryGetPath(out PathOwner pathOwner, out bool hasPathElements, out int pathElementCount))
            {
                ulong signature = shadow.PathSignatureKnown ? shadow.PathSignature : EmptyPathSignature;
                snapshot = snapshot.WithPath(pathOwner, shadow.PathBufferKnown && hasPathElements, pathElementCount, signature);
            }
            return snapshot;
        }

        private void PublishChanged(Entity vehicle, RailSnapshot current, uint sourceFrame, ulong sourceGeneration)
        {
            if (!m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot previous))
            {
                AppendInitialFacts(vehicle, current, sourceFrame, sourceGeneration);
                m_LastSnapshots[vehicle] = current;
                return;
            }

            if ((previous.State & PublicTransportFlags.Boarding) != (current.State & PublicTransportFlags.Boarding))
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Boarding, previous, current, sourceGeneration);
            if (previous.HasCurrentRoute != current.HasCurrentRoute || previous.Route != current.Route)
            {
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Route, previous, current, sourceGeneration);
                m_Runtime.m_VehicleRegistrar.ObserveRailRoute(vehicle);
            }
            if (previous.HasTarget != current.HasTarget || previous.Target != current.Target)
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Target, previous, current, sourceGeneration);
            if (previous.HasPathOwner != current.HasPathOwner || previous.PathState != current.PathState || previous.PathElementIndex != current.PathElementIndex
                || previous.HasPathElements != current.HasPathElements || previous.PathElementCount != current.PathElementCount || previous.PathElementSignature != current.PathElementSignature)
            {
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.PathReady, previous, current, sourceGeneration);
            }
            if (previous.HasTrainCurrentLane != current.HasTrainCurrentLane || previous.FrontLane != current.FrontLane || previous.RearLane != current.RearLane
                || previous.FrontLaneFlags != current.FrontLaneFlags || previous.RearLaneFlags != current.RearLaneFlags
                || previous.FrontCurvePosition != current.FrontCurvePosition || previous.RearCurvePosition != current.RearCurvePosition
                || previous.HasTrainNavigationLanes != current.HasTrainNavigationLanes || previous.NavigationLaneCount != current.NavigationLaneCount || previous.NavigationLaneSignature != current.NavigationLaneSignature)
            {
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Waypoint, previous, current, sourceGeneration);
            }
            if (previous.HasMoving != current.HasMoving || previous.VelocityX != current.VelocityX || previous.VelocityY != current.VelocityY || previous.VelocityZ != current.VelocityZ
                || previous.HasTransform != current.HasTransform || previous.PositionX != current.PositionX || previous.PositionY != current.PositionY || previous.PositionZ != current.PositionZ
                || previous.HasOdometer != current.HasOdometer || previous.Odometer != current.Odometer)
            {
                AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Moving, previous, current, sourceGeneration);
            }
            m_LastSnapshots[vehicle] = current;
        }

        private void AppendInitialFacts(Entity vehicle, RailSnapshot current, uint sourceFrame, ulong sourceGeneration)
        {
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Boarding, default, current, sourceGeneration);
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Route, default, current, sourceGeneration);
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Target, default, current, sourceGeneration);
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.PathReady, default, current, sourceGeneration);
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Waypoint, default, current, sourceGeneration);
            AppendVehicleFact(vehicle, sourceFrame, VehicleFactKind.Moving, default, current, sourceGeneration);
        }

        private void AppendPathWriteFact(
            Entity vehicle,
            uint sourceFrame,
            ulong sourceGeneration,
            bool previousPathReady,
            bool currentPathReady)
        {
            m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot);
            RailFrameFact railFact = CreateFrameFact(vehicle, snapshot, snapshot, currentPathReady, previousPathReady);
            Entity line = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) ? mappedLine : Entity.Null;
            ulong sequence = m_Events.AppendVehicle(
                vehicle,
                sourceFrame,
                VehicleFactKind.PathReady,
                line: line,
                state: m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) ? state : default,
                previousBoarding: false,
                currentBoarding: false,
                pathReady: currentPathReady,
                sourceGeneration: sourceGeneration);
            m_RailFrameFacts[sequence] = railFact;
        }

        private void AppendTargetWriteFact(Entity vehicle, uint sourceFrame, ulong sourceGeneration)
        {
            m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot);
            RailFrameFact railFact = CreateFrameFact(vehicle, snapshot, snapshot, PathReady(snapshot), PathReady(snapshot));
            Entity line = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) ? mappedLine : Entity.Null;
            ulong sequence = m_Events.AppendVehicle(
                vehicle,
                sourceFrame,
                VehicleFactKind.Target,
                line: line,
                state: m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) ? state : default,
                pathReady: PathReady(snapshot),
                sourceGeneration: sourceGeneration);
            m_RailFrameFacts[sequence] = railFact;
        }

        private void AppendWriteFact(Entity vehicle, uint sourceFrame, VehicleFactKind kind, PublicTransport previous, PublicTransport current, ulong sourceGeneration)
        {
            m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot);
            RailFrameFact railFact = CreateFrameFact(vehicle, snapshot, snapshot, PathReady(snapshot), PathReady(snapshot));
            Entity line = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) ? mappedLine : Entity.Null;
            ulong sequence = m_Events.AppendVehicle(
                vehicle,
                sourceFrame,
                kind,
                line: line,
                state: m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) ? state : default,
                previousBoarding: (previous.m_State & PublicTransportFlags.Boarding) != 0,
                currentBoarding: (current.m_State & PublicTransportFlags.Boarding) != 0,
                pathReady: PathReady(snapshot),
                sourceGeneration: sourceGeneration);
            m_RailFrameFacts[sequence] = railFact;
        }

        private void AppendVehicleFact(Entity vehicle, uint sourceFrame, VehicleFactKind kind, RailSnapshot previous, RailSnapshot current, ulong sourceGeneration)
        {
            RailFrameFact railFact = CreateFrameFact(vehicle, previous, current, PathReady(current), PathReady(previous));
            Entity line = current.HasCurrentRoute
                ? current.Route
                : m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mappedLine) ? mappedLine : Entity.Null;
            ulong sequence = m_Events.AppendVehicle(
                vehicle,
                sourceFrame,
                kind,
                previousLine: previous.HasCurrentRoute ? previous.Route : Entity.Null,
                line: line,
                state: m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state) ? state : default,
                previousBoarding: (previous.State & PublicTransportFlags.Boarding) != 0,
                currentBoarding: (current.State & PublicTransportFlags.Boarding) != 0,
                previousMoving: IsMoving(previous),
                currentMoving: IsMoving(current),
                previousWaypointIndex: railFact.PreviousWaypointIndex,
                currentWaypointIndex: railFact.CurrentWaypointIndex,
                waypointCount: railFact.WaypointCount,
                pathReady: railFact.PathReady,
                sourceGeneration: sourceGeneration);
            m_RailFrameFacts[sequence] = railFact;
        }

        private RailFrameFact CreateFrameFact(Entity vehicle, RailSnapshot previous, RailSnapshot current, bool pathReady, bool previousPathReady)
        {
            int previousWaypoint = TryGetWaypointIndex(vehicle, previous.Route, out int previousCount, out int previousWaypointCount)
                ? previousCount
                : -1;
            int currentWaypoint = TryGetWaypointIndex(vehicle, current.Route, out int currentCount, out int currentWaypointCount)
                ? currentCount
                : -1;
            int waypointCount = currentWaypointCount > 0 ? currentWaypointCount : previousWaypointCount;
            return new RailFrameFact(
                previous.HasCurrentRoute,
                current.HasCurrentRoute,
                current.Route,
                current.HasPublicTransport,
                (current.State & PublicTransportFlags.Boarding) != 0,
                IsMoving(previous),
                IsMoving(current),
                previousWaypoint,
                currentWaypoint,
                waypointCount,
                pathReady);
        }

        private bool TryGetWaypointIndex(Entity vehicle, Entity route, out int waypointIndex, out int waypointCount)
        {
            waypointIndex = -1;
            waypointCount = 0;
            if (route != Entity.Null
                && m_Runtime.EntityManager.Exists(route)
                && m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                waypointCount = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(route, true).Length;
            }
            return m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out waypointIndex)
                && waypointIndex >= 0;
        }

        private RailWriteShadow WriteShadow(Entity vehicle)
        {
            if (!m_ModWrites.TryGetValue(vehicle, out RailWriteShadow shadow))
            {
                shadow = new RailWriteShadow();
                m_ModWrites.Add(vehicle, shadow);
            }
            return shadow;
        }

        private bool TryReadLastPublicTransport(Entity vehicle, out PublicTransport value)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot) && snapshot.HasPublicTransport)
            {
                value = snapshot.Transport;
                return true;
            }
            value = default;
            return false;
        }

        private bool TryReadLastPath(Entity vehicle, out PathOwner value, out bool hasPathElements, out int count)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot) && snapshot.HasPathOwner)
            {
                value = snapshot.Path;
                hasPathElements = snapshot.HasPathElements;
                count = snapshot.PathElementCount;
                return true;
            }
            value = default;
            hasPathElements = false;
            count = 0;
            return false;
        }

        private bool TryReadLastPathSignature(Entity vehicle, out ulong signature)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot))
            {
                signature = snapshot.PathElementSignature;
                return true;
            }
            signature = 0UL;
            return false;
        }

        private void UpdateLastPublicTransport(Entity vehicle, PublicTransport value)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot))
                m_LastSnapshots[vehicle] = snapshot.WithPublicTransport(value);
        }

        private void UpdateLastTarget(Entity vehicle, Target value)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot))
                m_LastSnapshots[vehicle] = snapshot.WithTarget(value);
        }

        private void UpdateLastPath(Entity vehicle, PathOwner value, bool hasPathElements, int pathElementCount, ulong pathSignature)
        {
            if (m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot snapshot))
                m_LastSnapshots[vehicle] = snapshot.WithPath(
                    value,
                    hasPathElements,
                    pathElementCount,
                    NormalizePathSignature(hasPathElements, pathElementCount, pathSignature));
        }

        private ulong AdvanceSourceGeneration(Entity vehicle)
        {
            ulong next = CurrentSourceGeneration(vehicle) + 1UL;
            m_SourceGenerations[vehicle] = next;
            return next;
        }

        private void ObserveRouteMembership(Entity vehicle, Entity currentRoute)
        {
            if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine)) return;
            bool oldLineMember = HasRouteMembership(registeredLine, vehicle);
            bool routeMember = currentRoute == registeredLine ? oldLineMember : HasRouteMembership(currentRoute, vehicle);
            if (currentRoute != registeredLine || !oldLineMember || !routeMember)
                m_Runtime.m_VehicleRegistrar.ObserveRailRoute(vehicle);
        }

        private bool HasRouteMembership(Entity line, Entity vehicle)
        {
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line) || !m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line)) return false;
            if (!m_RouteMembers.TryGetValue(line, out HashSet<Entity> members))
            {
                members = new HashSet<Entity>();
                DynamicBuffer<RouteVehicle> routeVehicles = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity resolved = m_Runtime.m_Resolve.RuntimeVehicle(routeVehicles[i].m_Vehicle);
                    if (resolved != Entity.Null) members.Add(resolved);
                }
                m_RouteMembers.Add(line, members);
            }
            return members.Contains(vehicle);
        }

        private void PruneSnapshots()
        {
            List<Entity> stale = null;
            foreach (Entity vehicle in m_LastSnapshots.Keys)
            {
                if (m_Runtime.EntityManager.Exists(vehicle) && m_Runtime.m_VehicleView.Contains(vehicle)) continue;
                stale ??= new List<Entity>();
                stale.Add(vehicle);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) RemoveVehicle(stale[i]);
        }

        private static bool PathReady(RailSnapshot snapshot) => snapshot.HasPathOwner && snapshot.HasPathElements && snapshot.PathElementCount > 0;
        private static bool PathReady(bool hasPathElements, int pathElementCount) => hasPathElements && pathElementCount > 0;
        private static ulong NormalizePathSignature(bool hasPathElements, int pathElementCount, ulong pathSignature)
            => !hasPathElements ? 0UL : pathElementCount == 0 ? EmptyPathSignature : pathSignature;
        private static bool IsMoving(RailSnapshot snapshot) => snapshot.HasMoving
            && (snapshot.VelocityX != 0f || snapshot.VelocityY != 0f || snapshot.VelocityZ != 0f);

        private static ulong NavigationSignature(DynamicBuffer<TrainNavigationLane> lanes)
        {
            ulong signature = EmptyPathSignature;
            for (int i = 0; i < lanes.Length; i++)
            {
                signature = Mix(signature, (uint)lanes[i].m_Lane.Index);
                signature = Mix(signature, (uint)lanes[i].m_Lane.Version);
                signature = Mix(signature, math.asuint(lanes[i].m_CurvePosition.x));
                signature = Mix(signature, math.asuint(lanes[i].m_CurvePosition.y));
                signature = Mix(signature, (uint)lanes[i].m_Flags);
            }
            return signature;
        }

        private static ulong Mix(ulong signature, uint value) => (signature ^ value) * 1099511628211UL;
    }
}
