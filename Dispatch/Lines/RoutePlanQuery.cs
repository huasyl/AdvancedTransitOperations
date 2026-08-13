using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class RoutePlanQuery
    {
        private readonly EntityManager m_EntityManager;
        private readonly TrackModelService m_TrackModel;
        private readonly LineProfile m_LineProfile;
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, Entity> m_Anchor;
        private readonly Func<Entity, string> m_StopKey;

        internal RoutePlanQuery(
            EntityManager entityManager,
            TrackModelService trackModel,
            LineProfile lineProfile,
            Func<Entity, Entity> stop,
            Func<Entity, Entity> anchor,
            Func<Entity, string> stopKey)
        {
            m_EntityManager = entityManager;
            m_TrackModel = trackModel ?? throw new ArgumentNullException(nameof(trackModel));
            m_LineProfile = lineProfile ?? throw new ArgumentNullException(nameof(lineProfile));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            m_StopKey = stopKey ?? throw new ArgumentNullException(nameof(stopKey));
        }

        internal bool TryGet(Entity line, LifecycleKind lifecycle, out RoutePlan plan)
        {
            plan = null;
            if (line == Entity.Null
                || !m_EntityManager.Exists(line)
                || !m_EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            return TryGet(
                line,
                m_EntityManager.GetBuffer<RouteWaypoint>(line, true),
                lifecycle,
                out plan);
        }

        internal bool TryGet(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LifecycleKind lifecycle,
            out RoutePlan plan)
        {
            plan = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            LineProfile.RoadRouteSnapshot roadSnapshot = null;
            if (lifecycle == LifecycleKind.Rail)
            {
                if (!m_TrackModel.TryGetWaypointIndexLookup(line, waypoints, out LineWaypointIndexLookup lookup)
                    || lookup == null
                    || lookup.Signature != WaypointSignature(waypoints))
                {
                    return false;
                }
            }
            else if (lifecycle == LifecycleKind.Road)
            {
                if (!m_LineProfile.TryReadRoadRoute(line, out roadSnapshot)
                    || !MatchesRoadRoute(roadSnapshot, waypoints))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            List<RouteWaypointRef> ordered = new List<RouteWaypointRef>(waypoints.Length);
            List<RouteStopRef> stops = new List<RouteStopRef>();
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null || !m_EntityManager.Exists(waypoint))
                    return false;

                Entity stop = lifecycle == LifecycleKind.Road
                    ? roadSnapshot.Stops[i]
                    : m_Stop(waypoint);
                string stopKey = string.Empty;
                if (stop != Entity.Null)
                {
                    Entity anchor = m_Anchor(stop);
                    stopKey = m_StopKey(anchor);
                    if (string.IsNullOrEmpty(stopKey))
                        return false;

                    stops.Add(new RouteStopRef(i, waypoint, stop, stopKey));
                }

                ordered.Add(new RouteWaypointRef(i, waypoint, stop, stopKey));
            }

            if (stops.Count == 0)
                return false;

            plan = new RoutePlan
            {
                Lifecycle = lifecycle,
                Waypoints = ordered.ToArray(),
                Stops = stops.ToArray(),
                StopSig = BuildSignature(stops)
            };
            return true;
        }

        private bool MatchesRoadRoute(
            LineProfile.RoadRouteSnapshot snapshot,
            DynamicBuffer<RouteWaypoint> waypoints)
        {
            if (snapshot == null
                || snapshot.Waypoints == null
                || snapshot.Stops == null
                || snapshot.Waypoints.Length != waypoints.Length
                || snapshot.Stops.Length != waypoints.Length)
            {
                return false;
            }

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                Entity stop = snapshot.Stops[i];
                if (snapshot.Waypoints[i] != waypoint
                    || stop != m_Stop(waypoint)
                    || (stop != Entity.Null && !m_EntityManager.Exists(stop)))
                {
                    return false;
                }
            }

            return true;
        }

        private static ulong WaypointSignature(DynamicBuffer<RouteWaypoint> waypoints)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
                hash = Mix(hash, waypoints[i].m_Waypoint.Index);
            return hash;
        }

        private static string BuildSignature(IReadOnlyList<RouteStopRef> stops)
        {
            ulong hash = 1469598103934665603UL;
            hash = Mix(hash, stops.Count);
            for (int i = 0; i < stops.Count; i++)
            {
                string key = stops[i].StopKey ?? string.Empty;
                hash = Mix(hash, key.Length);
                for (int j = 0; j < key.Length; j++)
                    hash = Mix(hash, key[j]);
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static ulong Mix(ulong hash, int value)
        {
            unchecked
            {
                return (hash ^ (uint)value) * 1099511628211UL;
            }
        }
    }

    internal sealed class RoutePlan
    {
        public LifecycleKind Lifecycle;
        public RouteWaypointRef[] Waypoints = Array.Empty<RouteWaypointRef>();
        public RouteStopRef[] Stops = Array.Empty<RouteStopRef>();
        public string StopSig = string.Empty;
    }

    internal readonly struct RouteWaypointRef
    {
        public readonly int WaypointIndex;
        public readonly Entity Waypoint;
        public readonly Entity Stop;
        public readonly string StopKey;

        public RouteWaypointRef(int waypointIndex, Entity waypoint, Entity stop, string stopKey)
        {
            WaypointIndex = waypointIndex;
            Waypoint = waypoint;
            Stop = stop;
            StopKey = stopKey ?? string.Empty;
        }
    }

    internal readonly struct RouteStopRef
    {
        public readonly int WaypointIndex;
        public readonly Entity Waypoint;
        public readonly Entity Stop;
        public readonly string StopKey;

        public RouteStopRef(int waypointIndex, Entity waypoint, Entity stop, string stopKey)
        {
            WaypointIndex = waypointIndex;
            Waypoint = waypoint;
            Stop = stop;
            StopKey = stopKey ?? string.Empty;
        }
    }
}
