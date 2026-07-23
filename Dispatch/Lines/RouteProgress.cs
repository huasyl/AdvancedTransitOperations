using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class RouteProgress
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, RouteProgressFrameSnapshot> m_FrameSnapshots = new Dictionary<Entity, RouteProgressFrameSnapshot>();

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

        public RouteProgress(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public bool Try(Entity vehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            bool hit = TryCurrent(vehicle, out nextWaypointIndex, out segmentPosition);
            if (hit)
                return true;

            if (m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                CurrentRoute currentRoute = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle);
                if (TryPath(vehicle, currentRoute, out nextWaypointIndex, out segmentPosition))
                    return true;

                if (TryTarget(vehicle, currentRoute, out nextWaypointIndex, out segmentPosition))
                    return true;
            }

            nextWaypointIndex = 0;
            segmentPosition = 0f;
            return false;
        }

        public bool TryOriginArrivalRepair(
            Entity vehicle,
            out int nextWaypointIndex,
            out float segmentPosition)
        {
            if (!Try(vehicle, out nextWaypointIndex, out segmentPosition))
                return false;

            return nextWaypointIndex == 0
                && segmentPosition >= ModRuntimeHostSystem.ORIGIN_FORCE_IDLE_SEGMENT_PROGRESS;
        }

        private bool TryPath(Entity vehicle, CurrentRoute currentRoute, out int nextWaypointIndex, out float segmentPosition)
        {
            nextWaypointIndex = 0;
            segmentPosition = 0f;
            if (!m_Runtime.EntityManager.HasComponent<PathInformation>(vehicle))
                return false;

            PathInformation pathInfo = m_Runtime.EntityManager.GetComponentData<PathInformation>(vehicle);
            if (!m_Runtime.EntityManager.HasComponent<Waypoint>(pathInfo.m_Destination)
                || !m_Runtime.EntityManager.HasBuffer<RouteSegment>(currentRoute.m_Route))
            {
                return false;
            }

            Waypoint destinationWp = m_Runtime.EntityManager.GetComponentData<Waypoint>(pathInfo.m_Destination);
            DynamicBuffer<RouteSegment> routeSegments = m_Runtime.EntityManager.GetBuffer<RouteSegment>(currentRoute.m_Route, true);
            if (routeSegments.Length <= 0)
                return false;

            nextWaypointIndex = destinationWp.m_Index;
            int index = math.select(nextWaypointIndex - 1, routeSegments.Length - 1, nextWaypointIndex == 0);
            RouteSegment routeSegment = routeSegments[index];
            if (!m_Runtime.EntityManager.HasBuffer<PathElement>(routeSegment.m_Segment))
                return false;

            DynamicBuffer<PathElement> segmentPath = m_Runtime.EntityManager.GetBuffer<PathElement>(routeSegment.m_Segment, true);
            if (segmentPath.Length == 0)
                return false;

            int remaining = 0;
            if (m_Runtime.EntityManager.HasComponent<PathOwner>(vehicle)
                && m_Runtime.EntityManager.HasBuffer<PathElement>(vehicle))
            {
                PathOwner pathOwner = m_Runtime.EntityManager.GetComponentData<PathOwner>(vehicle);
                DynamicBuffer<PathElement> vehiclePath = m_Runtime.EntityManager.GetBuffer<PathElement>(vehicle, true);
                remaining += math.max(0, vehiclePath.Length - pathOwner.m_ElementIndex);
            }
            if (m_Runtime.EntityManager.HasBuffer<CarNavigationLane>(vehicle))
                remaining += m_Runtime.EntityManager.GetBuffer<CarNavigationLane>(vehicle, true).Length;
            else if (m_Runtime.EntityManager.HasBuffer<TrainNavigationLane>(vehicle))
                remaining += m_Runtime.EntityManager.GetBuffer<TrainNavigationLane>(vehicle, true).Length;
            else if (m_Runtime.EntityManager.HasBuffer<WatercraftNavigationLane>(vehicle))
                remaining += m_Runtime.EntityManager.GetBuffer<WatercraftNavigationLane>(vehicle, true).Length;
            else if (m_Runtime.EntityManager.HasBuffer<AircraftNavigationLane>(vehicle))
                remaining += m_Runtime.EntityManager.GetBuffer<AircraftNavigationLane>(vehicle, true).Length;

            segmentPosition = math.saturate((float)(segmentPath.Length - remaining) / (float)segmentPath.Length);
            Store(vehicle, currentRoute.m_Route, pathInfo.m_Destination, nextWaypointIndex, segmentPosition);
            return true;
        }

        private bool TryTarget(Entity vehicle, CurrentRoute currentRoute, out int nextWaypointIndex, out float segmentPosition)
        {
            nextWaypointIndex = 0;
            segmentPosition = 0f;
            if (!m_Runtime.EntityManager.HasComponent<Target>(vehicle))
                return false;

            Target target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            if (!m_Runtime.EntityManager.HasComponent<Waypoint>(target.m_Target)
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(currentRoute.m_Route))
            {
                return false;
            }

            Waypoint targetWp = m_Runtime.EntityManager.GetComponentData<Waypoint>(target.m_Target);
            DynamicBuffer<RouteWaypoint> routeWaypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(currentRoute.m_Route, true);
            if (routeWaypoints.Length <= 0)
                return false;

            nextWaypointIndex = targetWp.m_Index;
            int index = math.select(nextWaypointIndex - 1, routeWaypoints.Length - 1, nextWaypointIndex == 0);
            RouteWaypoint previousWaypoint = routeWaypoints[index];
            if (!m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(vehicle)
                || !m_Runtime.EntityManager.HasComponent<Position>(previousWaypoint.m_Waypoint)
                || !m_Runtime.EntityManager.HasComponent<Position>(target.m_Target))
            {
                return false;
            }

            Game.Objects.Transform transform = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle);
            Position prevPos = m_Runtime.EntityManager.GetComponentData<Position>(previousWaypoint.m_Waypoint);
            Position targetPos = m_Runtime.EntityManager.GetComponentData<Position>(target.m_Target);
            float toTarget = math.distance(transform.m_Position, targetPos.m_Position);
            float segmentLength = math.max(1f, math.distance(prevPos.m_Position, targetPos.m_Position));
            segmentPosition = math.saturate((segmentLength - toTarget) / segmentLength);
            Store(vehicle, currentRoute.m_Route, target.m_Target, nextWaypointIndex, segmentPosition);
            return true;
        }

        internal bool TryCurrent(Entity vehicle, out int nextWaypointIndex, out float segmentPosition)
        {
            nextWaypointIndex = 0;
            segmentPosition = 0f;
            if (vehicle == Entity.Null
                || !m_FrameSnapshots.TryGetValue(vehicle, out RouteProgressFrameSnapshot snapshot))
            {
                return false;
            }

            if (snapshot.Frame != m_Runtime.m_SimulationSystem.frameIndex)
                return false;
            if (!m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle) || !m_Runtime.EntityManager.HasComponent<Target>(vehicle))
                return false;

            CurrentRoute currentRoute = m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle);
            Target target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            if (snapshot.Route != currentRoute.m_Route || snapshot.Target != target.m_Target)
                return false;

            nextWaypointIndex = snapshot.NextWaypointIndex;
            segmentPosition = snapshot.SegmentPosition;
            return true;
        }

        internal void Store(
            Entity vehicle,
            Entity route,
            Entity target,
            int nextWaypointIndex,
            float segmentPosition)
        {
            if (vehicle == Entity.Null || route == Entity.Null || nextWaypointIndex < 0)
                return;

            m_FrameSnapshots[vehicle] = new RouteProgressFrameSnapshot(
                m_Runtime.m_SimulationSystem.frameIndex,
                route,
                target,
                nextWaypointIndex,
                segmentPosition);
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_FrameSnapshots.Remove(vehicle);
        }

        public void Clear()
        {
            m_FrameSnapshots.Clear();
        }
    }
}
