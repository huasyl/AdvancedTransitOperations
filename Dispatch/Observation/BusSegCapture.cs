using System;
using System.Collections.Generic;
using System.Text;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class BusSegCapture
    {
        internal const float BusSegMaxSampleHours = 24f;

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly BusSegStore m_Store;
        private readonly Action<Entity> m_OnChanged;

        internal BusSegCapture(
            ModRuntimeHostSystem runtime,
            BusSegStore store,
            Action<Entity> onChanged)
        {
            m_Runtime = runtime;
            m_Store = store;
            m_OnChanged = onChanged;
        }

        internal void Begin(Entity vehicle, Entity line, int fromWaypointIndex, uint nowFrame)
        {
            if (vehicle == Entity.Null
                || !IsBus(line)
                || !TryRoute(line, out DynamicBuffer<RouteWaypoint> waypoints)
                || fromWaypointIndex < 0
                || fromWaypointIndex >= waypoints.Length)
            {
                return;
            }

            Entity fromWaypoint = waypoints[fromWaypointIndex].m_Waypoint;
            Entity fromStop = ResolveStop(fromWaypoint);
            if (fromWaypoint == Entity.Null || fromStop == Entity.Null)
                return;

            if (!TryNextStop(waypoints, fromWaypointIndex, out int toWaypointIndex, out Entity toStop))
                return;

            Entity toWaypoint = waypoints[toWaypointIndex].m_Waypoint;
            if (toWaypoint == Entity.Null || toStop == Entity.Null)
                return;

            m_Store.Begin(vehicle, new BusSegSession(
                line,
                fromWaypoint,
                fromStop,
                toWaypoint,
                toStop,
                nowFrame));
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[BusSeg] phase=begin vehicle=" + vehicle.Index
                    + " line=" + line.Index
                    + " from=" + fromWaypoint.Index + "/" + fromStop.Index
                    + " expectedTo=" + toWaypoint.Index + "/" + toStop.Index
                    + " trigger=begin"
                    + " reason=started"
                    + " frame=" + nowFrame);
            }
        }

        internal bool TryEnd(
            Entity vehicle,
            Entity line,
            int arrivedWaypointIndex,
            uint nowFrame,
            out BusSegSample sample)
        {
            sample = default;
            if (!m_Store.TrySession(vehicle, out BusSegSession session))
            {
                if (RtLog.VerboseEnabled)
                {
                    m_Runtime.log.Info("[BusSeg] phase=reject vehicle=" + vehicle.Index
                        + " line=" + line.Index
                        + " waypoint=" + arrivedWaypointIndex
                        + " trigger=opened"
                        + " reason=session_missing"
                        + " frame=" + nowFrame);
                }
                return false;
            }

            if (!IsBus(line))
            {
                m_Store.Cancel(vehicle);
                LogReject(vehicle, line, session, Entity.Null, Entity.Null, 0, nowFrame, "line_not_bus");
                return false;
            }
            if (session.Line != line)
            {
                m_Store.Cancel(vehicle);
                LogReject(vehicle, line, session, Entity.Null, Entity.Null, 0, nowFrame, "line_changed");
                return false;
            }
            if (nowFrame <= session.StartFrame)
            {
                m_Store.Cancel(vehicle);
                LogReject(vehicle, line, session, Entity.Null, Entity.Null, 0, nowFrame, "elapsed_invalid");
                return false;
            }
            if (!TryRoute(line, out DynamicBuffer<RouteWaypoint> waypoints))
            {
                m_Store.Cancel(vehicle);
                LogReject(vehicle, line, session, Entity.Null, Entity.Null, 0, nowFrame, "route_missing");
                return false;
            }
            if (arrivedWaypointIndex < 0 || arrivedWaypointIndex >= waypoints.Length)
            {
                m_Store.Cancel(vehicle);
                LogReject(vehicle, line, session, Entity.Null, Entity.Null, 0, nowFrame, "to_index_invalid");
                return false;
            }

            Entity actualToWaypoint = waypoints[arrivedWaypointIndex].m_Waypoint;
            Entity actualToStop = ResolveStop(actualToWaypoint);
            if (actualToWaypoint == Entity.Null || actualToStop == Entity.Null)
            {
                LogReject(
                    vehicle,
                    line,
                    session,
                    actualToWaypoint,
                    actualToStop,
                    0,
                    nowFrame,
                    "actual_stop_missing");
                return false;
            }

            if (!TryFindWaypoint(
                    waypoints,
                    session.FromWaypoint,
                    session.FromStop,
                    out int fromWaypointIndex))
            {
                m_Store.Cancel(vehicle);
                LogReject(
                    vehicle,
                    line,
                    session,
                    actualToWaypoint,
                    actualToStop,
                    0,
                    nowFrame,
                    "from_waypoint_missing");
                return false;
            }

            int skippedStops = CountSkippedStops(waypoints, fromWaypointIndex, arrivedWaypointIndex);
            BusSegKey key = new BusSegKey(
                line,
                session.FromWaypoint,
                session.FromStop,
                actualToWaypoint,
                actualToStop);

            uint elapsed = nowFrame - session.StartFrame;
            uint maxFrames = m_Runtime.m_SimClock.Snapshot.ToFramesCeil(BusSegMaxSampleHours * 60f);
            if (elapsed == 0u || elapsed > maxFrames)
            {
                m_Store.Cancel(vehicle);
                LogReject(
                    vehicle,
                    line,
                    session,
                    actualToWaypoint,
                    actualToStop,
                    skippedStops,
                    nowFrame,
                    "sample_over_24h");
                return false;
            }

            float sampleFrames = elapsed;
            BusSegObservation observation;
            float previousFrames = 0f;
            if (!m_Store.TryObservation(key, out BusSegObservation previous))
            {
                observation = new BusSegObservation(sampleFrames, 1);
            }
            else
            {
                previousFrames = previous.EstimatedFrames;
                float weight = sampleFrames > previous.EstimatedFrames ? 0.5f : 0.15f;
                observation = new BusSegObservation(
                    math.max(0f, previous.EstimatedFrames + (sampleFrames - previous.EstimatedFrames) * weight),
                    math.min(32, previous.SampleCount + 1));
            }

            m_Store.Put(key, observation);
            m_Store.Cancel(vehicle);
            sample = new BusSegSample(key);
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[BusSeg] phase=commit vehicle=" + vehicle.Index
                    + " line=" + line.Index
                    + " from=" + key.FromWaypoint.Index + "/" + key.FromStop.Index
                    + " expectedTo=" + session.ExpectedToWaypoint.Index + "/" + session.ExpectedToStop.Index
                    + " actualTo=" + key.ToWaypoint.Index + "/" + key.ToStop.Index
                    + " skippedStops=" + skippedStops
                    + " trigger=opened"
                    + " reason=accepted"
                    + " elapsed=" + elapsed
                    + " previous=" + previousFrames.ToString("F0")
                    + " estimated=" + observation.EstimatedFrames.ToString("F0")
                    + " samples=" + observation.SampleCount);
            }
            m_OnChanged?.Invoke(line);
            return true;
        }

        internal void Cancel(Entity vehicle)
        {
            m_Store.Cancel(vehicle);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            m_Store.RemoveVehicle(vehicle);
        }

        internal void RemoveLine(Entity line)
        {
            m_Store.RemoveLine(line);
            m_OnChanged?.Invoke(line);
        }

        internal void Clear()
        {
            m_Store.Clear();
        }

        internal void InvalidateRoute(
            Entity line,
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            if (line == Entity.Null || oldRoute == null || newRoute == null)
                return;

            var staleSessions = new List<Entity>();
            foreach (KeyValuePair<Entity, BusSegSession> pair in m_Store.Sessions)
            {
                if (pair.Value.Line == line)
                    staleSessions.Add(pair.Key);
            }
            for (int i = 0; i < staleSessions.Count; i++)
                m_Store.Cancel(staleSessions[i]);

            var staleObservations = new List<BusSegKey>();
            var keptObservations = new List<BusSegKey>();
            foreach (KeyValuePair<BusSegKey, BusSegObservation> pair in m_Store.Observations)
            {
                if (pair.Key.Line != line)
                    continue;

                if (MatchesSegment(pair.Key, oldRoute, newRoute))
                    keptObservations.Add(pair.Key);
                else
                    staleObservations.Add(pair.Key);
            }
            for (int i = 0; i < staleObservations.Count; i++)
                m_Store.Remove(staleObservations[i]);

            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                m_Runtime.log.Info("[BusSegRouteInvalidated] line=" + line.Index
                    + " oldWaypoints=" + oldRoute.Waypoints.Length
                    + " newWaypoints=" + newRoute.Waypoints.Length
                    + " cancelledSessions=" + staleSessions.Count
                    + " cancelledVehicles=" + FormatVehicles(staleSessions)
                    + " removedObservations=" + staleObservations.Count
                    + " removedSegments=" + FormatSegments(staleObservations)
                    + " keptObservations=" + keptObservations.Count
                    + " keptSegments=" + FormatSegments(keptObservations));
            }

            m_OnChanged?.Invoke(line);
        }

        private static string FormatVehicles(List<Entity> vehicles)
        {
            if (vehicles == null || vehicles.Count == 0)
                return "[]";

            var text = new StringBuilder();
            text.Append('[');
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (i > 0)
                    text.Append(',');
                text.Append(vehicles[i].Index);
            }
            text.Append(']');
            return text.ToString();
        }

        private static string FormatSegments(List<BusSegKey> segments)
        {
            if (segments == null || segments.Count == 0)
                return "[]";

            var text = new StringBuilder();
            text.Append('[');
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                    text.Append(',');

                BusSegKey key = segments[i];
                text.Append(key.FromWaypoint.Index);
                text.Append('/');
                text.Append(key.FromStop.Index);
                text.Append("->");
                text.Append(key.ToWaypoint.Index);
                text.Append('/');
                text.Append(key.ToStop.Index);
            }
            text.Append(']');
            return text.ToString();
        }

        internal static bool MatchesSegment(
            BusSegKey key,
            LineProfile.RoadRouteSnapshot oldRoute,
            LineProfile.RoadRouteSnapshot newRoute)
        {
            if (!IsRouteValid(oldRoute) || !IsRouteValid(newRoute))
                return false;

            for (int oldFrom = 0; oldFrom < oldRoute.Waypoints.Length; oldFrom++)
            {
                if (!MatchesFrom(oldRoute, oldFrom, key))
                {
                    continue;
                }

                int oldSpan = SpanLength(oldRoute, oldFrom, key);
                if (oldSpan <= 0)
                    continue;

                for (int newFrom = 0; newFrom < newRoute.Waypoints.Length; newFrom++)
                {
                    if (!MatchesFrom(newRoute, newFrom, key))
                    {
                        continue;
                    }

                    int newSpan = SpanLength(newRoute, newFrom, key);
                    if (newSpan <= 0)
                        continue;

                    if (SamePath(oldRoute, oldFrom, oldSpan, newRoute, newFrom, newSpan))
                        return true;
                }
            }

            return false;
        }

        private void LogReject(
            Entity vehicle,
            Entity line,
            BusSegSession session,
            Entity actualToWaypoint,
            Entity actualToStop,
            int skippedStops,
            uint nowFrame,
            string reason)
        {
            if (!RtLog.VerboseEnabled)
                return;

            m_Runtime.log.Info("[BusSeg] phase=reject vehicle=" + vehicle.Index
                + " line=" + line.Index
                + " from=" + session.FromWaypoint.Index + "/" + session.FromStop.Index
                + " expectedTo=" + session.ExpectedToWaypoint.Index + "/" + session.ExpectedToStop.Index
                + " actualTo=" + actualToWaypoint.Index + "/" + actualToStop.Index
                + " skippedStops=" + skippedStops
                + " trigger=opened"
                + " reason=" + reason
                + " frame=" + nowFrame);
        }

        private bool IsBus(Entity line)
        {
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && TransportModeResolver.Resolve(m_Runtime.EntityManager, line) == TransitMode.Bus;
        }

        private bool TryRoute(Entity line, out DynamicBuffer<RouteWaypoint> waypoints)
        {
            waypoints = default;
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line)
                && (waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true)).Length > 1;
        }

        private Entity ResolveStop(Entity waypoint)
        {
            return waypoint != Entity.Null ? m_Runtime.m_Resolve.Stop(waypoint) : Entity.Null;
        }

        private bool TryFindWaypoint(
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity waypoint,
            Entity stop,
            out int index)
        {
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i].m_Waypoint == waypoint && ResolveStop(waypoint) == stop)
                {
                    index = i;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        private int CountSkippedStops(
            DynamicBuffer<RouteWaypoint> waypoints,
            int fromWaypointIndex,
            int toWaypointIndex)
        {
            int span = (toWaypointIndex - fromWaypointIndex + waypoints.Length) % waypoints.Length;
            if (span == 0)
                span = waypoints.Length;

            int skipped = 0;
            for (int step = 1; step < span; step++)
            {
                int index = (fromWaypointIndex + step) % waypoints.Length;
                if (ResolveStop(waypoints[index].m_Waypoint) != Entity.Null)
                    skipped++;
            }

            return skipped;
        }

        private bool TryNextStop(
            DynamicBuffer<RouteWaypoint> waypoints,
            int fromWaypointIndex,
            out int toWaypointIndex,
            out Entity toStop)
        {
            toWaypointIndex = -1;
            toStop = Entity.Null;
            for (int step = 1; step <= waypoints.Length; step++)
            {
                int index = (fromWaypointIndex + step) % waypoints.Length;
                Entity stop = ResolveStop(waypoints[index].m_Waypoint);
                if (stop == Entity.Null)
                    continue;

                toWaypointIndex = index;
                toStop = stop;
                return true;
            }

            return false;
        }

        private static bool IsRouteValid(LineProfile.RoadRouteSnapshot route)
        {
            return route != null
                && route.Waypoints != null
                && route.Stops != null
                && route.Waypoints.Length > 1
                && route.Waypoints.Length == route.Stops.Length;
        }

        private static bool MatchesFrom(LineProfile.RoadRouteSnapshot route, int index, BusSegKey key)
        {
            return route.Waypoints[index] == key.FromWaypoint && route.Stops[index] == key.FromStop;
        }

        private static bool MatchesTo(LineProfile.RoadRouteSnapshot route, int index, BusSegKey key)
        {
            return route.Waypoints[index] == key.ToWaypoint && route.Stops[index] == key.ToStop;
        }

        private static int SpanLength(
            LineProfile.RoadRouteSnapshot route,
            int from,
            BusSegKey key)
        {
            for (int step = 1; step <= route.Waypoints.Length; step++)
            {
                int index = (from + step) % route.Waypoints.Length;
                if (MatchesTo(route, index, key))
                    return step;
            }

            return 0;
        }

        private static bool SamePath(
            LineProfile.RoadRouteSnapshot oldRoute,
            int oldFrom,
            int oldSpan,
            LineProfile.RoadRouteSnapshot newRoute,
            int newFrom,
            int newSpan)
        {
            if (oldSpan != newSpan)
                return false;

            for (int step = 0; step <= oldSpan; step++)
            {
                int oldIndex = (oldFrom + step) % oldRoute.Waypoints.Length;
                int newIndex = (newFrom + step) % newRoute.Waypoints.Length;
                if (oldRoute.Waypoints[oldIndex] != newRoute.Waypoints[newIndex]
                    || oldRoute.Stops[oldIndex] != newRoute.Stops[newIndex])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
