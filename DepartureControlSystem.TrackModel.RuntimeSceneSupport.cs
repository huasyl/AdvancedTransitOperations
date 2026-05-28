using System.Collections.Generic;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
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
                int traversalPhaseIndex = -1;
                int traversalPhaseStartAtomIndex = -1;
                int traversalPhaseEndAtomExclusive = -1;
                int nextTurnbackBoundaryAtomIndex = -1;
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
                        out phaseEndAtomExclusive,
                        out traversalPhaseIndex,
                        out traversalPhaseStartAtomIndex,
                        out traversalPhaseEndAtomExclusive,
                        out nextTurnbackBoundaryAtomIndex);
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
                    phaseEndAtomExclusive,
                    traversalPhaseIndex,
                    traversalPhaseStartAtomIndex,
                    traversalPhaseEndAtomExclusive,
                    nextTurnbackBoundaryAtomIndex));

            }

            return true;
        }

        private void RequestLineOrderedRuntimeForceRefresh(Entity line, string reason)
        {
            if (line == Entity.Null)
                return;

            m_LineOrderedRuntimeForceRefreshReasons[line] = string.IsNullOrWhiteSpace(reason)
                ? "unspecified"
                : reason;
        }

        private bool TryGetLineOrderedRuntimeState(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out LineOrderedRuntimeState state)
        {
            state = null;
            if (line == Entity.Null
                || waypoints.Length == 0
                || !TryGetLineTrackChain(line, waypoints, out LineTrackChain chain))
            {
                return false;
            }

            if (!TryGetLineRunningVehicleFrameSnapshot(line, waypoints, nowFrame, out LineRunningVehicleFrameSnapshot snapshot))
                return false;

            if (!m_LineOrderedRuntimeStates.TryGetValue(line, out state) || state == null)
            {
                state = new LineOrderedRuntimeState();
                m_LineOrderedRuntimeStates[line] = state;
            }

            RefreshLineOrderedRuntimeState(line, chain, snapshot, nowFrame, state);
            return state.Entries.Count > 0;
        }

        private void RefreshLineOrderedRuntimeState(
            Entity line,
            LineTrackChain chain,
            LineRunningVehicleFrameSnapshot snapshot,
            uint nowFrame,
            LineOrderedRuntimeState state)
        {
            state.ScratchEntriesByVehicle.Clear();
            int unresolvedCount = 0;
            for (int i = 0; i < snapshot.Vehicles.Count; i++)
            {
                LineRunningVehicleSnapshot runningVehicle = snapshot.Vehicles[i];
                if (!TryBuildOrderedLineVehicleEntry(chain, runningVehicle, out OrderedLineVehicleEntry entry))
                {
                    unresolvedCount++;
                    continue;
                }

                state.ScratchEntriesByVehicle[entry.Vehicle] = entry;
            }

            bool requiresFullSort =
                state.Line != line
                || state.ChainSignature != chain.Signature
                || state.LastFullSortFrame == 0
                || nowFrame - state.LastFullSortFrame >= LINE_ORDERED_RUNTIME_FORCE_FULL_SORT_INTERVAL_FRAMES;
            string refreshReason = state.LastFullSortFrame == 0 ? "initial" : string.Empty;

            if (m_LineOrderedRuntimeForceRefreshReasons.TryGetValue(line, out string forcedReason))
            {
                requiresFullSort = true;
                refreshReason = forcedReason;
                m_LineOrderedRuntimeForceRefreshReasons.Remove(line);
            }

            if (!requiresFullSort)
            {
                for (int entryIndex = state.Entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    OrderedLineVehicleEntry previousEntry = state.Entries[entryIndex];
                    if (!state.ScratchEntriesByVehicle.TryGetValue(previousEntry.Vehicle, out OrderedLineVehicleEntry currentEntry))
                    {
                        state.Entries.RemoveAt(entryIndex);
                        continue;
                    }

                    state.Entries[entryIndex] = currentEntry;
                    state.ScratchEntriesByVehicle.Remove(previousEntry.Vehicle);
                    if (currentEntry.TraversalPhaseIndex != previousEntry.TraversalPhaseIndex)
                    {
                        requiresFullSort = true;
                        refreshReason = "phase-shift";
                        break;
                    }
                }

                if (!requiresFullSort && state.ScratchEntriesByVehicle.Count > 0)
                {
                    requiresFullSort = true;
                    refreshReason = "new-running-vehicle";
                }

                if (!requiresFullSort)
                {
                    for (int entryIndex = 1; entryIndex < state.Entries.Count; entryIndex++)
                    {
                        if (CompareOrderedLineVehicleEntry(state.Entries[entryIndex - 1], state.Entries[entryIndex]) > 0)
                        {
                            requiresFullSort = true;
                            refreshReason = "order-inversion";
                            break;
                        }
                    }
                }
            }

            if (requiresFullSort)
            {
                state.Entries.Clear();
                for (int i = 0; i < snapshot.Vehicles.Count; i++)
                {
                    if (TryBuildOrderedLineVehicleEntry(chain, snapshot.Vehicles[i], out OrderedLineVehicleEntry entry))
                        state.Entries.Add(entry);
                }

                state.Entries.Sort(CompareOrderedLineVehicleEntry);
                state.LastFullSortFrame = nowFrame;
                if (IsLineOrderedRuntimeLoggingEnabled())
                {
                    string refreshSummary = "reason=" + (string.IsNullOrWhiteSpace(refreshReason) ? "periodic" : refreshReason)
                        + "|entries=" + state.Entries.Count
                        + "|phases=" + math.max(1, chain.TurnbackBoundaries.Count + 1)
                        + "|unresolved=" + unresolvedCount;
                    if (!m_LineOrderedRuntimeLogCache.TryGetValue(line, out string previousRefreshSummary)
                        || previousRefreshSummary != refreshSummary)
                    {
                        m_LineOrderedRuntimeLogCache[line] = refreshSummary;
                        log.Info("[LineOrderedRefresh] line=" + line.Index
                            + " reason=" + (string.IsNullOrWhiteSpace(refreshReason) ? "periodic" : refreshReason)
                            + " entries=" + state.Entries.Count
                            + " phases=" + math.max(1, chain.TurnbackBoundaries.Count + 1)
                            + " unresolved=" + unresolvedCount);
                    }
                }
            }

            RebuildOrderedLinePhaseRanges(state, chain.TrackAtoms.Count);
            state.Line = line;
            state.ChainSignature = chain.Signature;
            state.LastRefreshFrame = nowFrame;
        }

        private bool TryBuildOrderedLineVehicleEntry(
            LineTrackChain chain,
            LineRunningVehicleSnapshot runningVehicle,
            out OrderedLineVehicleEntry entry)
        {
            entry = default;
            if (chain == null
                || !runningVehicle.HasTrackCursor
                || runningVehicle.Vehicle == Entity.Null
                || runningVehicle.TraversalPhaseIndex < 0
                || runningVehicle.TraversalPhaseEndAtomExclusive <= runningVehicle.TraversalPhaseStartAtomIndex)
            {
                return false;
            }

            entry = new OrderedLineVehicleEntry(
                runningVehicle.Vehicle,
                runningVehicle,
                runningVehicle.OwnLineAtomCoordinate,
                runningVehicle.TraversalPhaseIndex,
                runningVehicle.TraversalPhaseStartAtomIndex,
                runningVehicle.TraversalPhaseEndAtomExclusive);
            return true;
        }

        private bool TryResolveTraversalOrderingPhase(
            LineTrackChain chain,
            int atomCursorIndex,
            out int traversalPhaseIndex,
            out int traversalPhaseStartAtomIndex,
            out int traversalPhaseEndAtomExclusive,
            out int nextTurnbackBoundaryAtomIndex)
        {
            traversalPhaseIndex = -1;
            traversalPhaseStartAtomIndex = -1;
            traversalPhaseEndAtomExclusive = -1;
            nextTurnbackBoundaryAtomIndex = -1;
            if (chain == null || chain.TrackAtoms.Count == 0)
                return false;

            int atomIndex = math.clamp(atomCursorIndex, 0, chain.TrackAtoms.Count - 1);
            int phaseStartAtomIndex = 0;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                int boundaryAtomIndex = math.clamp(chain.TurnbackBoundaries[boundaryIndex].AtomIndex, 0, chain.TrackAtoms.Count);
                if (atomIndex < boundaryAtomIndex)
                {
                    traversalPhaseIndex = boundaryIndex;
                    traversalPhaseStartAtomIndex = phaseStartAtomIndex;
                    traversalPhaseEndAtomExclusive = boundaryAtomIndex;
                    nextTurnbackBoundaryAtomIndex = boundaryAtomIndex;
                    return true;
                }

                phaseStartAtomIndex = boundaryAtomIndex;
            }

            traversalPhaseIndex = chain.TurnbackBoundaries.Count;
            traversalPhaseStartAtomIndex = phaseStartAtomIndex;
            traversalPhaseEndAtomExclusive = chain.TrackAtoms.Count;
            return true;
        }

        private bool TryResolveNextTurnbackStationBoundary(
            LineTrackChain chain,
            int atomCursorIndex,
            out TrackTurnbackStationBoundary stationBoundary)
        {
            stationBoundary = default;
            if (chain == null
                || chain.TurnbackBoundaries == null
                || chain.TurnbackBoundaries.Count == 0)
            {
                return false;
            }

            int cursorAtomIndex = atomCursorIndex >= 0 ? atomCursorIndex : -1;
            for (int boundaryIndex = 0; boundaryIndex < chain.TurnbackBoundaries.Count; boundaryIndex++)
            {
                TurnbackBoundary boundary = chain.TurnbackBoundaries[boundaryIndex];
                if (cursorAtomIndex >= 0 && boundary.AtomIndex <= cursorAtomIndex)
                {
                    continue;
                }

                if (TryResolveTurnbackStationBoundary(chain, boundary, out stationBoundary))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryCollectTurnbackStationBoundaries(
            LineTrackChain chain,
            List<TrackTurnbackStationBoundary> stationBoundaries)
        {
            if (stationBoundaries == null)
            {
                return false;
            }

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

        private bool TryResolveTurnbackStationBoundary(
            LineTrackChain chain,
            TurnbackBoundary boundary,
            out TrackTurnbackStationBoundary stationBoundary)
        {
            stationBoundary = default;
            if (chain == null
                || chain.TraversalProfile == null)
            {
                return false;
            }

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

        private bool TryResolveNearbyTurnbackStationEvent(
            LineTrackChain chain,
            int atomIndex,
            int radiusAtoms,
            out TraversalEvent stationEvent)
        {
            stationEvent = default;
            if (chain == null || chain.TraversalProfile == null)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent candidate = chain.TraversalProfile.Events[eventIndex];
                if (candidate.Building == Entity.Null)
                {
                    continue;
                }

                int distance = ResolveTraversalEventAtomDistance(candidate, atomIndex);
                if (distance > radiusAtoms || distance >= bestDistance)
                {
                    continue;
                }

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
                {
                    return 0;
                }

                return atomIndex < startAtomIndex
                    ? startAtomIndex - atomIndex
                    : atomIndex - (endAtomIndexExclusive - 1);
            }

            return math.abs(atomIndex - startAtomIndex);
        }

        private void RebuildOrderedLinePhaseRanges(LineOrderedRuntimeState state, int atomCount)
        {
            state.PhaseRanges.Clear();
            if (state == null || state.Entries.Count == 0)
                return;

            int currentPhaseIndex = state.Entries[0].TraversalPhaseIndex;
            int currentPhaseStartAtomIndex = state.Entries[0].TraversalPhaseStartAtomIndex;
            int currentPhaseEndAtomExclusive = state.Entries[0].TraversalPhaseEndAtomExclusive;
            int phaseStartEntryIndex = 0;
            for (int entryIndex = 1; entryIndex < state.Entries.Count; entryIndex++)
            {
                OrderedLineVehicleEntry entry = state.Entries[entryIndex];
                if (entry.TraversalPhaseIndex == currentPhaseIndex)
                    continue;

                state.PhaseRanges.Add(new OrderedLinePhaseRange(
                    currentPhaseIndex,
                    currentPhaseStartAtomIndex,
                    currentPhaseEndAtomExclusive,
                    phaseStartEntryIndex,
                    entryIndex));
                currentPhaseIndex = entry.TraversalPhaseIndex;
                currentPhaseStartAtomIndex = entry.TraversalPhaseStartAtomIndex;
                currentPhaseEndAtomExclusive = entry.TraversalPhaseEndAtomExclusive;
                phaseStartEntryIndex = entryIndex;
            }

            state.PhaseRanges.Add(new OrderedLinePhaseRange(
                currentPhaseIndex,
                currentPhaseStartAtomIndex,
                currentPhaseEndAtomExclusive,
                phaseStartEntryIndex,
                state.Entries.Count));
        }

        private static int CompareOrderedLineVehicleEntry(
            OrderedLineVehicleEntry left,
            OrderedLineVehicleEntry right)
        {
            if (left.TraversalPhaseIndex != right.TraversalPhaseIndex)
                return left.TraversalPhaseIndex.CompareTo(right.TraversalPhaseIndex);
            if (left.OwnLineAtomCoordinate != right.OwnLineAtomCoordinate)
                return left.OwnLineAtomCoordinate.CompareTo(right.OwnLineAtomCoordinate);
            return left.Vehicle.Index.CompareTo(right.Vehicle.Index);
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
