using System;
using Game.Net;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal enum RouteWaypointEndpointKind
    {
        None,
        OutsideTrainConnection,
        Unknown
    }

    internal enum RouteWaypointEndpointDirection
    {
        Unknown,
        Entry,
        Exit,
        Boundary
    }

    internal readonly struct RouteWaypointEndpoint
    {
        public readonly Entity Waypoint;
        public readonly RouteWaypointEndpointKind Kind;
        public readonly RouteWaypointEndpointDirection Direction;
        public readonly Entity StartLane;
        public readonly Entity EndLane;
        public readonly Entity OutsideConnection;
        public readonly float StartCurvePos;
        public readonly float EndCurvePos;

        public RouteWaypointEndpoint(
            Entity waypoint,
            RouteWaypointEndpointKind kind,
            RouteWaypointEndpointDirection direction,
            Entity startLane,
            Entity endLane,
            Entity outsideConnection,
            float startCurvePos,
            float endCurvePos)
        {
            Waypoint = waypoint;
            Kind = kind;
            Direction = direction;
            StartLane = startLane;
            EndLane = endLane;
            OutsideConnection = outsideConnection;
            StartCurvePos = startCurvePos;
            EndCurvePos = endCurvePos;
        }
    }

    internal readonly struct LineDispatchSupport
    {
        public const string ReasonOriginOutsideEndpoint = "origin-outside-endpoint";
        public const string ReasonOriginNotPassengerStop = "origin-not-passenger-stop";

        public readonly bool Supported;
        public readonly string Reason;

        public LineDispatchSupport(bool supported, string reason = null)
        {
            Supported = supported;
            Reason = reason;
        }

        public static LineDispatchSupport CreateSupported() => new LineDispatchSupport(true);
        public static LineDispatchSupport CreateUnsupported(string reason) => new LineDispatchSupport(false, reason);
    }

    internal static class RouteWaypointEndpointResolver
    {
        internal static bool TryResolveRouteWaypointEndpoint(
            EntityManager entityManager,
            Entity waypoint,
            out RouteWaypointEndpoint endpoint)
        {
            endpoint = default;

            if (waypoint == Entity.Null || !entityManager.Exists(waypoint))
                return false;

            if (!entityManager.HasComponent<RouteLane>(waypoint))
                return false;

            RouteLane routeLane = entityManager.GetComponentData<RouteLane>(waypoint);
            if (routeLane.m_StartLane == Entity.Null && routeLane.m_EndLane == Entity.Null)
                return false;

            bool connectedOutside = IsConnectedOutsideConnection(entityManager, waypoint);
            bool startOutside = IsOutsideTrainConnection(entityManager, routeLane.m_StartLane)
                || (connectedOutside && IsTrainRouteLane(entityManager, routeLane.m_StartLane));
            bool endOutside = IsOutsideTrainConnection(entityManager, routeLane.m_EndLane)
                || (connectedOutside && IsTrainRouteLane(entityManager, routeLane.m_EndLane));

            if (!startOutside && !endOutside)
                return false;

            RouteWaypointEndpointKind kind = RouteWaypointEndpointKind.OutsideTrainConnection;
            RouteWaypointEndpointDirection direction = ComputeDirection(startOutside, endOutside);

            endpoint = new RouteWaypointEndpoint(
                waypoint,
                kind,
                direction,
                routeLane.m_StartLane,
                routeLane.m_EndLane,
                ResolveOutsideConnection(entityManager, waypoint, routeLane.m_StartLane, routeLane.m_EndLane),
                routeLane.m_StartCurvePos,
                routeLane.m_EndCurvePos);

            return true;
        }

        internal static LineDispatchSupport ComputeLineDispatchSupport(
            EntityManager entityManager,
            Entity route,
            Func<Entity, Entity> resolveStop)
        {
            if (route == Entity.Null || !entityManager.Exists(route))
                return LineDispatchSupport.CreateUnsupported(LineDispatchSupport.ReasonOriginNotPassengerStop);

            if (!entityManager.HasBuffer<RouteWaypoint>(route))
                return LineDispatchSupport.CreateUnsupported(LineDispatchSupport.ReasonOriginNotPassengerStop);

            var waypoints = entityManager.GetBuffer<RouteWaypoint>(route);
            if (waypoints.Length < 2)
                return LineDispatchSupport.CreateUnsupported(LineDispatchSupport.ReasonOriginNotPassengerStop);

            Entity firstWaypoint = waypoints[0].m_Waypoint;
            if (TryResolveRouteWaypointEndpoint(entityManager, firstWaypoint, out _))
                return LineDispatchSupport.CreateUnsupported(LineDispatchSupport.ReasonOriginOutsideEndpoint);

            Entity firstStop = resolveStop?.Invoke(firstWaypoint) ?? Entity.Null;

            if (firstStop != Entity.Null)
                return LineDispatchSupport.CreateSupported();

            return LineDispatchSupport.CreateUnsupported(LineDispatchSupport.ReasonOriginNotPassengerStop);
        }

        internal static uint ComputeRouteEndpointSignature(EntityManager entityManager, Entity route, Func<Entity, Entity> resolveStop)
        {
            if (route == Entity.Null || !entityManager.HasBuffer<RouteWaypoint>(route))
                return 0;

            var waypoints = entityManager.GetBuffer<RouteWaypoint>(route);
            uint hash = (uint)route.Index;
            hash = hash * 31 + (uint)waypoints.Length;

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                hash = hash * 31 + (uint)waypoint.Index;

                if (entityManager.HasComponent<Game.Routes.Connected>(waypoint))
                {
                    Entity connected = entityManager.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
                    hash = hash * 31 + (uint)connected.Index;
                }

                if (i == 0 && resolveStop != null)
                {
                    Entity stop = resolveStop(waypoint);
                    if (stop != Entity.Null)
                    {
                        hash = hash * 31 + (uint)stop.Index;
                    }
                }

                if (entityManager.HasComponent<RouteLane>(waypoint))
                {
                    RouteLane routeLane = entityManager.GetComponentData<RouteLane>(waypoint);
                    hash = hash * 31 + (uint)routeLane.m_StartLane.Index;
                    hash = hash * 31 + (uint)routeLane.m_EndLane.Index;
                    hash = hash * 31 + (uint)math.round(routeLane.m_StartCurvePos * 1000f);
                    hash = hash * 31 + (uint)math.round(routeLane.m_EndCurvePos * 1000f);

                    if (TryResolveRouteWaypointEndpoint(entityManager, waypoint, out var endpoint))
                    {
                        hash = hash * 31 + (uint)endpoint.Kind;
                        hash = hash * 31 + (uint)endpoint.Direction;
                    }
                }

                if (i == 0 || i == waypoints.Length - 1)
                {
                    hash = MixOutsideOwnerChain(entityManager, waypoint, hash);
                }
            }

            return hash;
        }

        private static uint MixOutsideOwnerChain(EntityManager entityManager, Entity waypoint, uint hash)
        {
            if (entityManager.HasComponent<Game.Routes.Connected>(waypoint))
            {
                Entity current = entityManager.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
                for (int depth = 0; depth < 4 && current != Entity.Null && entityManager.Exists(current); depth++)
                {
                    if (entityManager.HasComponent<Game.Objects.OutsideConnection>(current)
                        || entityManager.HasComponent<Game.Net.OutsideConnection>(current))
                    {
                        hash = hash * 31 + (uint)current.Index;
                        break;
                    }
                    if (entityManager.HasComponent<Game.Common.Owner>(current))
                    {
                        current = entityManager.GetComponentData<Game.Common.Owner>(current).m_Owner;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            return hash;
        }

        private static Entity ResolveOutsideConnection(EntityManager entityManager, Entity waypoint, Entity startLane, Entity endLane)
        {
            if (TryFindOutsideConnectionFromConnected(entityManager, waypoint, out Entity connectedOutside))
                return connectedOutside;
            if (TryFindOutsideConnectionInOwnerChain(entityManager, startLane, out Entity startOutside))
                return startOutside;
            if (TryFindOutsideConnectionInOwnerChain(entityManager, endLane, out Entity endOutside))
                return endOutside;
            return Entity.Null;
        }

        private static bool TryFindOutsideConnectionFromConnected(EntityManager entityManager, Entity waypoint, out Entity outsideConnection)
        {
            outsideConnection = Entity.Null;
            if (waypoint == Entity.Null
                || !entityManager.Exists(waypoint)
                || !entityManager.HasComponent<Game.Routes.Connected>(waypoint))
            {
                return false;
            }

            Entity connected = entityManager.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
            return TryFindOutsideConnectionInOwnerChain(entityManager, connected, out outsideConnection);
        }

        private static bool TryFindOutsideConnectionInOwnerChain(EntityManager entityManager, Entity entity, out Entity outsideConnection)
        {
            outsideConnection = Entity.Null;
            Entity current = entity;
            for (int depth = 0; depth < 4 && current != Entity.Null && entityManager.Exists(current); depth++)
            {
                if (entityManager.HasComponent<Game.Objects.OutsideConnection>(current)
                    || entityManager.HasComponent<Game.Net.OutsideConnection>(current))
                {
                    outsideConnection = current;
                    return true;
                }

                if (!entityManager.HasComponent<Game.Common.Owner>(current))
                    break;

                Entity owner = entityManager.GetComponentData<Game.Common.Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return false;
        }

        private static bool IsOutsideTrainConnection(EntityManager entityManager, Entity lane)
        {
            if (lane == Entity.Null || !entityManager.Exists(lane))
                return false;

            if (!entityManager.HasComponent<Game.Net.ConnectionLane>(lane))
                return false;

            Game.Net.ConnectionLane connectionLane = entityManager.GetComponentData<Game.Net.ConnectionLane>(lane);

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Outside) == 0)
                return false;

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Track) == 0)
                return false;

            return (connectionLane.m_TrackTypes & TrackTypes.Train) != 0;
        }

        private static bool IsConnectedOutsideConnection(EntityManager entityManager, Entity waypoint)
        {
            if (waypoint == Entity.Null
                || !entityManager.Exists(waypoint)
                || !entityManager.HasComponent<Game.Routes.Connected>(waypoint))
            {
                return false;
            }

            Entity current = entityManager.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
            for (int depth = 0; depth < 4 && current != Entity.Null && entityManager.Exists(current); depth++)
            {
                if (entityManager.HasComponent<Game.Objects.OutsideConnection>(current)
                    || entityManager.HasComponent<Game.Net.OutsideConnection>(current))
                {
                    return true;
                }

                if (!entityManager.HasComponent<Game.Common.Owner>(current))
                    break;

                Entity owner = entityManager.GetComponentData<Game.Common.Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;

                current = owner;
            }

            return false;
        }

        private static bool IsTrainRouteLane(EntityManager entityManager, Entity lane)
        {
            if (lane == Entity.Null || !entityManager.Exists(lane))
                return false;

            if (entityManager.HasComponent<Game.Net.ConnectionLane>(lane))
            {
                Game.Net.ConnectionLane connectionLane = entityManager.GetComponentData<Game.Net.ConnectionLane>(lane);
                return (connectionLane.m_Flags & ConnectionLaneFlags.Track) != 0
                    && (connectionLane.m_TrackTypes & TrackTypes.Train) != 0;
            }

            if (!entityManager.HasComponent<Game.Net.TrackLane>(lane))
                return false;

            if (!entityManager.HasComponent<PrefabRef>(lane))
                return false;

            Entity prefab = entityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
            if (prefab == Entity.Null || !entityManager.HasComponent<TrackLaneData>(prefab))
                return false;

            TrackLaneData data = entityManager.GetComponentData<TrackLaneData>(prefab);
            return (data.m_TrackTypes & TrackTypes.Train) != 0;
        }

        private static RouteWaypointEndpointDirection ComputeDirection(bool startOutside, bool endOutside)
        {
            if (startOutside && endOutside)
                return RouteWaypointEndpointDirection.Boundary;
            if (startOutside && !endOutside)
                return RouteWaypointEndpointDirection.Entry;
            if (!startOutside && endOutside)
                return RouteWaypointEndpointDirection.Exit;
            return RouteWaypointEndpointDirection.Unknown;
        }
    }
}
