using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.TrackModel
{
    internal static class Turnbacks
    {
        internal static bool TryCollectTurnbackStationBoundaries(
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

        internal static bool TryResolveTurnbackStationBoundary(
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
    }
}
