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
    internal readonly struct RailSnapshot : IEquatable<RailSnapshot>
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
            HasTrainNavigationLanes = hasTrainNavigationLanes; NavigationLaneCount = navigationLaneCount; NavigationLaneSignature = navigationLaneSignature; HasMoving = hasMoving;
            VelocityX = velocityX; VelocityY = velocityY; VelocityZ = velocityZ;
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
        public RailSnapshot WithPath(PathOwner path, int pathElementCount, ulong pathElementSignature) => new RailSnapshot(Exists, HasPublicTransport, Transport, HasTarget, TargetData, HasCurrentRoute, Route,
            HasPathOwner, path, true, pathElementCount, pathElementSignature, HasTrainCurrentLane, FrontLane, RearLane,
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

    internal sealed class RailEventSource : IDisposable
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly FrameEvents m_Events;

        // 跨帧差分：仅 ResetTracking 清空；BeginFrame 不影响来源基线。
        private readonly Dictionary<Entity, RailSnapshot> m_LastSnapshots = new Dictionary<Entity, RailSnapshot>();
        // 仅本来源帧缓存实际涉及线路的完整成员实体，不扫描全线路。
        private readonly Dictionary<Entity, HashSet<Entity>> m_RouteMembers = new Dictionary<Entity, HashSet<Entity>>();
        // 本帧写后影子：同车同字段以后写值覆盖前写值，供本帧来源读取。
        private readonly Dictionary<Entity, RailSnapshot> m_ModWrites = new Dictionary<Entity, RailSnapshot>();
        private uint m_LastCollectedFrame = uint.MaxValue;

        public RailEventSource(ModRuntimeHostSystem runtime, FrameEvents events) { m_Runtime = runtime; m_Events = events; }

        // 仅清本帧写后影子；不清来源差分和成员基线。
        public void BeginFrame() => m_ModWrites.Clear();

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
                    PublishChanged(vehicle, snapshot, frame);
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

        // 所有模组 PublicTransport 写入入口：DispatchActions、RouteWriter、RetireHost。
        public void AppendModWrite(Entity vehicle, PublicTransport publicTransport, uint frame)
        {
            if (vehicle == Entity.Null) return;
            RailSnapshot snapshot = ReadSnapshot(vehicle);
            PublishChanged(vehicle, snapshot.WithPublicTransport(publicTransport), frame);
        }

        public void AppendTargetWrite(Entity vehicle, Target target, uint frame) => PublishChanged(vehicle, ReadSnapshot(vehicle).WithTarget(target), frame);
        public void AppendPathWrite(Entity vehicle, PathOwner pathOwner, int pathElementCount, ulong pathElementSignature, uint frame) => PublishChanged(vehicle, ReadSnapshot(vehicle).WithPath(pathOwner, pathElementCount, pathElementCount == 0 ? EmptyPathSignature : pathElementSignature), frame);

        public bool TryReadPublicTransport(Entity vehicle, out PublicTransport publicTransport)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailSnapshot snapshot))
            {
                publicTransport = snapshot.Transport;
                return true;
            }
            publicTransport = default;
            return false;
        }

        public bool TryReadTarget(Entity vehicle, out Target target)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailSnapshot snapshot))
            {
                target = snapshot.TargetData;
                return true;
            }
            target = default;
            return false;
        }

        public bool TryReadPath(Entity vehicle, out PathOwner pathOwner)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailSnapshot snapshot))
            {
                pathOwner = snapshot.Path;
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
            if (m_ModWrites.TryGetValue(vehicle, out RailSnapshot snapshot))
                return snapshot.PathElementCount;
            return m_Runtime.EntityManager.Exists(vehicle)
                && m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle)
                    ? m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true).Length
                    : 0;
        }

        public const ulong EmptyPathSignature = 1469598103934665603UL;

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
            // 读档/整体清理：清本帧影子及跨帧来源基线。
            m_ModWrites.Clear();
            m_LastSnapshots.Clear();
            m_RouteMembers.Clear();
            m_LastCollectedFrame = uint.MaxValue;
        }

        // 销毁时不保留任何 ECS 快照或来源基线。
        public void Dispose() => ResetTracking();

        private RailSnapshot ReadSnapshot(Entity vehicle)
        {
            if (m_ModWrites.TryGetValue(vehicle, out RailSnapshot written)) return written;
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
            return new RailSnapshot(true, hasPublicTransport, publicTransport, hasTarget, target, hasCurrentRoute, route, hasPathOwner, pathOwner,
                hasPathElements, pathCount, pathSignature, hasTrainCurrentLane, lane.m_Front.m_Lane, lane.m_Rear.m_Lane,
                lane.m_Front.m_LaneFlags, lane.m_Rear.m_LaneFlags, lane.m_Front.m_CurvePosition.x, lane.m_Rear.m_CurvePosition.x,
                hasTrainNavigationLanes, navigationCount, navigationSignature, hasMoving, movingData.m_Velocity.x,
                movingData.m_Velocity.y, movingData.m_Velocity.z, hasTransform, transform.m_Position.x, transform.m_Position.y,
                transform.m_Position.z, hasOdometer, odometer);
        }

        private void PublishChanged(Entity vehicle, RailSnapshot current, uint sourceFrame)
        {
            if (!m_LastSnapshots.TryGetValue(vehicle, out RailSnapshot previous))
            {
                AppendFacts(vehicle, sourceFrame, default, current);
            }
            else
            {
                if (previous.TransportChanged(current) || previous.Exists != current.Exists)
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.PublicTransport, current.State, current.Route, previous, current);
                if ((previous.State & PublicTransportFlags.Boarding) != (current.State & PublicTransportFlags.Boarding))
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Boarding, current.State, current.Route, previous, current);
                if (previous.HasCurrentRoute != current.HasCurrentRoute || previous.Route != current.Route)
                {
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Route, current.State, current.Route, previous, current);
                    m_Runtime.m_VehicleRegistrar.ObserveRailRoute(vehicle);
                }
                if (previous.HasTarget != current.HasTarget || previous.Target != current.Target)
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Target, current.State, current.Route, previous, current);
                if (previous.HasPathOwner != current.HasPathOwner || previous.PathState != current.PathState || previous.PathElementIndex != current.PathElementIndex
                    || previous.HasPathElements != current.HasPathElements || previous.PathElementCount != current.PathElementCount || previous.PathElementSignature != current.PathElementSignature)
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Path, current.State, current.Route, previous, current);
                if (previous.HasTrainCurrentLane != current.HasTrainCurrentLane || previous.FrontLane != current.FrontLane || previous.RearLane != current.RearLane
                    || previous.FrontLaneFlags != current.FrontLaneFlags || previous.RearLaneFlags != current.RearLaneFlags
                    || previous.FrontCurvePosition != current.FrontCurvePosition || previous.RearCurvePosition != current.RearCurvePosition
                    || previous.HasTrainNavigationLanes != current.HasTrainNavigationLanes || previous.NavigationLaneCount != current.NavigationLaneCount || previous.NavigationLaneSignature != current.NavigationLaneSignature)
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Lane, current.State, current.Route, previous, current);
                if (previous.HasMoving != current.HasMoving || previous.VelocityX != current.VelocityX || previous.VelocityY != current.VelocityY || previous.VelocityZ != current.VelocityZ
                    || previous.HasTransform != current.HasTransform || previous.PositionX != current.PositionX || previous.PositionY != current.PositionY || previous.PositionZ != current.PositionZ
                    || previous.HasOdometer != current.HasOdometer || previous.Odometer != current.Odometer)
                    m_Events.AppendVehicle(vehicle, sourceFrame, VehicleFactKind.Motion, current.State, current.Route, previous, current);
            }
            m_LastSnapshots[vehicle] = current;
            m_ModWrites[vehicle] = current;
        }

        private void AppendFacts(Entity vehicle, uint frame, RailSnapshot previous, RailSnapshot current)
        {
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.PublicTransport, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Boarding, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Route, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Target, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Path, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Lane, current.State, current.Route, previous, current);
            m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Motion, current.State, current.Route, previous, current);
        }

        private void ObserveRouteMembership(Entity vehicle, Entity currentRoute)
        {
            if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity registeredLine))
                return;

            bool oldLineMember = HasRouteMembership(registeredLine, vehicle);
            bool routeMember = currentRoute == registeredLine
                ? oldLineMember
                : HasRouteMembership(currentRoute, vehicle);
            if (currentRoute != registeredLine || !oldLineMember || !routeMember)
                m_Runtime.m_VehicleRegistrar.ObserveRailRoute(vehicle);
        }

        private bool HasRouteMembership(Entity line, Entity vehicle)
        {
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteVehicle>(line))
            {
                return false;
            }

            if (!m_RouteMembers.TryGetValue(line, out HashSet<Entity> members))
            {
                members = new HashSet<Entity>();
                DynamicBuffer<RouteVehicle> routeVehicles = m_Runtime.EntityManager.GetBuffer<RouteVehicle>(line, true);
                for (int i = 0; i < routeVehicles.Length; i++)
                {
                    Entity resolved = m_Runtime.m_Resolve.RuntimeVehicle(routeVehicles[i].m_Vehicle);
                    if (resolved != Entity.Null)
                        members.Add(resolved);
                }
                m_RouteMembers.Add(line, members);
            }
            return members.Contains(vehicle);
        }

        private static ulong NavigationSignature(DynamicBuffer<TrainNavigationLane> lanes)
        {
            ulong signature = 1469598103934665603UL;
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

        private void PruneSnapshots()
        {
            List<Entity> stale = null;
            foreach (Entity vehicle in m_LastSnapshots.Keys)
            {
                if (m_Runtime.EntityManager.Exists(vehicle) && m_Runtime.m_VehicleView.Contains(vehicle))
                    continue;
                stale ??= new List<Entity>();
                stale.Add(vehicle);
            }
            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
            {
                m_LastSnapshots.Remove(stale[i]);
                m_ModWrites.Remove(stale[i]);
            }
        }

        private static ulong Mix(ulong signature, uint value) => (signature ^ value) * 1099511628211UL;
    }
}
