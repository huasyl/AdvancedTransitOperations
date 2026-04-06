using System.Collections.Generic;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DepartureControlSystem
    {
        private bool TryGetLineRunningVehicleFrameSnapshot(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineRunningVehicleFrameSnapshot snapshot)
        {
            snapshot = null;
            if (line == Entity.Null || waypoints.Length == 0)
                return false;

            if (m_LineRunningVehicleFrameSnapshots.TryGetValue(line, out snapshot)
                && snapshot != null
                && snapshot.Frame == nowFrame
                && snapshot.Line == line)
            {
                return true;
            }

            var routeVehicleBuffers = GetBufferLookup<RouteVehicle>(true);
            if (!routeVehicleBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> routeVehicles))
                return false;

            if (snapshot == null)
            {
                snapshot = new LineRunningVehicleFrameSnapshot();
                m_LineRunningVehicleFrameSnapshots[line] = snapshot;
            }

            snapshot.Frame = nowFrame;
            snapshot.Line = line;
            snapshot.Vehicles.Clear();

            bool hasTrackChain = TryGetLineTrackChain(line, waypoints, out LineTrackChain trackChain);

            for (int i = 0; i < routeVehicles.Length; i++)
            {
                Entity vehicle = routeVehicles[i].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    continue;
                if (!m_VehicleState.TryGetValue(vehicle, out VehicleState vehicleState) || vehicleState != VehicleState.Running)
                    continue;

                bool boarding = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                    && (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;

                int nextWaypointIndex;
                if (!TryGetRouteProgress(vehicle, out nextWaypointIndex, out _))
                {
                    nextWaypointIndex = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWp) ? cachedWp : -1;
                }

                bool hasProjection = TryProjectVehicleOntoLine(vehicle, line, waypoints, out LineDistanceProjection projection);
                bool hasTrackCursor = false;
                VehicleTrackCursor trackCursor = default;
                int currentControlEdgeIndex = -1;
                float ownLineAtomCoordinate = 0f;
                int phaseEndAtomExclusive = -1;
                if (hasTrackChain)
                {
                    hasTrackCursor = TryBuildLineRunningVehicleOwnLineRuntimeSnapshot(
                        vehicle,
                        line,
                        waypoints,
                        trackChain,
                        out trackCursor,
                        out currentControlEdgeIndex,
                        out ownLineAtomCoordinate,
                        out phaseEndAtomExclusive);
                }

                snapshot.Vehicles.Add(new LineRunningVehicleSnapshot(
                    vehicle,
                    nextWaypointIndex,
                    boarding,
                    hasProjection,
                    hasProjection ? projection.DistanceMeters : 0f,
                    hasTrackCursor,
                    trackCursor,
                    currentControlEdgeIndex,
                    ownLineAtomCoordinate,
                    phaseEndAtomExclusive));
            }

            return true;
        }

        private bool TryGetBypassWaypointContext(
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

        private Dictionary<Entity, int> BuildLocalBypassCorridorWaypointMap(
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

        private bool TryFindFutureSharedCorridorWaypoint(
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

        private Entity GetBypassBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            if (building == Entity.Null)
                building = ResolvePassingStationBuilding(stopEntity);
            if (building == Entity.Null || !IsBypassStation(building))
                return Entity.Null;

            return building;
        }

        private Entity GetStationBuildingForWaypoint(DynamicBuffer<RouteWaypoint> waypoints, int waypointIndex)
        {
            if (waypointIndex < 0 || waypointIndex >= waypoints.Length)
                return Entity.Null;

            Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[waypointIndex].m_Waypoint);
            if (stopEntity == Entity.Null)
                return Entity.Null;

            Entity building = FindTransportStationFromStop(stopEntity);
            return building != Entity.Null ? building : ResolvePassingStationBuilding(stopEntity);
        }

        private bool TryFindWaypointIndexForBypassBuilding(
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

        private static int CountForwardWaypointSteps(int waypointCount, int startIndexInclusive, int targetIndexInclusive)
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
