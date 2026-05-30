using System.Collections.Generic;
using Game.Routes;
using RapidTransitMod.TrackModel;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        internal bool TryCollectTurnbackStationBoundaries(
            LineTrackChain chain,
            List<TrackTurnbackStationBoundary> stationBoundaries)
        {
            if (stationBoundaries == null)
                return false;

            stationBoundaries.Clear();
            if (chain == null
                || chain.TurnbackBoundaries == null
                || chain.TurnbackBoundaries.Count == 0)
            {
                return false;
            }

            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                if (TryResolveTurnbackStationBoundary(
                        chain,
                        chain.TurnbackBoundaries[boundaryIndex],
                        out TrackTurnbackStationBoundary stationBoundary))
                {
                    stationBoundaries.Add(stationBoundary);
                }
            }

            return stationBoundaries.Count > 0;
        }

        internal bool TryResolveTurnbackStationBoundary(
            LineTrackChain chain,
            TurnbackBoundary boundary,
            out TrackTurnbackStationBoundary stationBoundary)
        {
            stationBoundary = default;
            if (chain == null || chain.TraversalProfile == null)
                return false;

            if (boundary.BoundaryEventIndex >= 0
                && boundary.BoundaryEventIndex < chain.TraversalProfile.Events.Count)
            {
                TraversalEvent boundaryEvent = chain.TraversalProfile.Events[boundary.BoundaryEventIndex];
                if (boundaryEvent.Building != Entity.Null)
                {
                    stationBoundary = new TrackTurnbackStationBoundary(
                        boundaryEvent.Building,
                        boundaryEvent.WaypointIndex,
                        boundary.AtomIndex,
                        boundary.BoundaryEventIndex);
                    return true;
                }
            }

            if (TryResolveNearbyTurnbackStationEvent(
                    chain,
                    boundary.AtomIndex,
                    4,
                    out TraversalEvent nearbyEvent))
            {
                stationBoundary = new TrackTurnbackStationBoundary(
                    nearbyEvent.Building,
                    nearbyEvent.WaypointIndex,
                    boundary.AtomIndex,
                    nearbyEvent.EventIndex);
                return true;
            }

            stationBoundary = new TrackTurnbackStationBoundary(
                Entity.Null,
                -1,
                boundary.AtomIndex,
                boundary.BoundaryEventIndex);
            return true;
        }

        private static bool TryResolveNearbyTurnbackStationEvent(
            LineTrackChain chain,
            int atomIndex,
            int radiusAtoms,
            out TraversalEvent stationEvent)
        {
            stationEvent = default;
            if (chain == null || chain.TraversalProfile == null)
                return false;

            int bestDistance = int.MaxValue;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[eventIndex];
                if (candidate.Building == Entity.Null)
                    continue;

                int distance = ResolveTraversalEventAtomDistance(candidate, atomIndex);
                if (distance > radiusAtoms || distance >= bestDistance)
                    continue;

                bestDistance = distance;
                stationEvent = candidate;
            }

            return bestDistance != int.MaxValue;
        }

        private static int ResolveTraversalEventAtomDistance(TraversalEvent traversalEvent, int atomIndex)
        {
            int startAtomIndex = traversalEvent.StartAtomIndex;
            int endAtomIndexExclusive = traversalEvent.EndAtomIndexExclusive;
            if (endAtomIndexExclusive > startAtomIndex)
            {
                if (atomIndex >= startAtomIndex && atomIndex < endAtomIndexExclusive)
                    return 0;

                return atomIndex < startAtomIndex
                    ? startAtomIndex - atomIndex
                    : atomIndex - (endAtomIndexExclusive - 1);
            }

            return math.abs(atomIndex - startAtomIndex);
        }

        internal bool TryGetBypassWaypointContext(
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            out Entity currentBypassBuilding,
            out int nextBypassWaypointIndex,
            out Entity nextBypassBuilding)
        {
            currentBypassBuilding = Entity.Null;
            nextBypassWaypointIndex = -1;
            nextBypassBuilding = Entity.Null;

            if (currentWaypointIndex < 0 || currentWaypointIndex >= waypoints.Length)
                return false;

            currentBypassBuilding = GetBypassBuildingForWaypoint(waypoints, currentWaypointIndex);
            if (currentBypassBuilding == Entity.Null)
                return false;

            for (int candidateIndex = currentWaypointIndex + 1; candidateIndex < waypoints.Length; candidateIndex++)
            {
                Entity candidateBuilding = GetBypassBuildingForWaypoint(waypoints, candidateIndex);
                if (candidateBuilding == Entity.Null || candidateBuilding == currentBypassBuilding)
                    continue;

                nextBypassWaypointIndex = candidateIndex;
                nextBypassBuilding = candidateBuilding;
                return true;
            }

            return false;
        }

        internal Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            int nextBypassWaypointIndex,
            Entity currentBypassBuilding)
        {
            Dictionary<Entity, int> result = new Dictionary<Entity, int>();
            if (waypoints.Length == 0
                || currentWaypointIndex < 0
                || currentWaypointIndex >= waypoints.Length
                || nextBypassWaypointIndex < 0
                || nextBypassWaypointIndex >= waypoints.Length)
            {
                return result;
            }

            int cursor = (currentWaypointIndex + 1) % waypoints.Length;
            int guard = 0;
            while (guard++ < waypoints.Length)
            {
                Entity building = GetStationBuildingForWaypoint(waypoints, cursor);
                if (building != Entity.Null
                    && building != currentBypassBuilding
                    && !result.ContainsKey(building))
                {
                    result[building] = cursor;
                }

                if (cursor == nextBypassWaypointIndex)
                    break;

                cursor = (cursor + 1) % waypoints.Length;
            }

            return result;
        }

        internal bool TryFindFutureSharedCorridorWaypoint(
            DynamicBuffer<RouteWaypoint> expressWaypoints,
            Dictionary<Entity, int> localCorridorWaypoints,
            int startIndexInclusive,
            int endIndexInclusive,
            out int expressWaypointIndex,
            out int localWaypointIndex)
        {
            expressWaypointIndex = -1;
            localWaypointIndex = -1;

            if (expressWaypoints.Length == 0 || localCorridorWaypoints.Count == 0)
                return false;

            int start = math.clamp(startIndexInclusive, 0, expressWaypoints.Length - 1);
            int maxScanCount = expressWaypoints.Length;
            if (endIndexInclusive >= 0 && endIndexInclusive < expressWaypoints.Length)
            {
                int stepsToEnd = CountForwardWaypointSteps(expressWaypoints.Length, start, endIndexInclusive);
                if (stepsToEnd < 0)
                    return false;
                maxScanCount = stepsToEnd + 1;
            }

            for (int offset = 0; offset < maxScanCount; offset++)
            {
                int candidateIndex = (start + offset) % expressWaypoints.Length;
                Entity building = GetStationBuildingForWaypoint(expressWaypoints, candidateIndex);
                if (building == Entity.Null || !localCorridorWaypoints.TryGetValue(building, out int localIndex))
                    continue;

                expressWaypointIndex = candidateIndex;
                localWaypointIndex = localIndex;
                return true;
            }

            return false;
        }

        internal Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            if (building == Entity.Null)
                building = ResolvePassingStationBuilding(stopEntity);
            if (building == Entity.Null || !m_SelectionPanel.IsBypassStation(building))
                return Entity.Null;

            return building;
        }

        internal Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            return building != Entity.Null ? building : ResolvePassingStationBuilding(stopEntity);
        }

        internal bool TryFindWaypointIndexForBypassBuilding(
            DynamicBuffer<RouteWaypoint> waypoints,
            Entity building,
            int startIndexInclusive,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (building == Entity.Null || waypoints.Length == 0)
                return false;

            int start = math.clamp(startIndexInclusive, 0, waypoints.Length - 1);
            for (int offset = 0; offset < waypoints.Length; offset++)
            {
                int candidateIndex = (start + offset) % waypoints.Length;
                if (GetBypassBuildingForWaypoint(waypoints, candidateIndex) != building)
                    continue;

                waypointIndex = candidateIndex;
                return true;
            }

            return false;
        }

        internal static int CountForwardWaypointSteps(int waypointCount, int startIndexInclusive, int targetIndexInclusive)
        {
            if (waypointCount <= 0
                || startIndexInclusive < 0
                || startIndexInclusive >= waypointCount
                || targetIndexInclusive < 0
                || targetIndexInclusive >= waypointCount)
            {
                return -1;
            }

            return (targetIndexInclusive - startIndexInclusive + waypointCount) % waypointCount;
        }
    }
}
