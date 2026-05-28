using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private bool TryResolveVehicleCurrentProtectedInterval(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null || line == Entity.Null || chain == null || waypoints.Length == 0)
                return false;

            if (!TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
                return false;

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            return TryResolveProtectedIntervalByCursor(
                chain,
                currentControlEdgeIndex,
                cursor.AtomCursorIndex,
                out protectedIntervalIndex,
                out protectedInterval);
        }

        private static bool TryResolveProtectedIntervalByCursor(
            LineTrackChain chain,
            int currentControlEdgeIndex,
            int currentAtomIndex,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (chain == null)
                return false;

            int bestRelativeScore = int.MinValue;
            int bestEntryDistanceAtoms = int.MaxValue;
            int bestIntervalLengthAtoms = int.MaxValue;
            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                TrackModelRelativeToProtectedInterval relative = ResolveRelativeToProtectedInterval(currentControlEdgeIndex, currentAtomIndex, candidate);
                if (relative == TrackModelRelativeToProtectedInterval.Unknown
                    || relative == TrackModelRelativeToProtectedInterval.After)
                {
                    continue;
                }

                int relativeScore = relative == TrackModelRelativeToProtectedInterval.Inside ? 2 : 1;
                int entryDistanceAtoms = math.max(0, candidate.StartAtomIndex - currentAtomIndex);
                int intervalLengthAtoms = math.max(1, candidate.EndAtomIndexExclusive - candidate.StartAtomIndex);
                bool better = protectedIntervalIndex < 0;
                if (!better && relativeScore != bestRelativeScore)
                    better = relativeScore > bestRelativeScore;
                if (!better && entryDistanceAtoms != bestEntryDistanceAtoms)
                    better = entryDistanceAtoms < bestEntryDistanceAtoms;
                if (!better && intervalLengthAtoms != bestIntervalLengthAtoms)
                    better = intervalLengthAtoms < bestIntervalLengthAtoms;
                if (!better)
                    continue;

                bestRelativeScore = relativeScore;
                bestEntryDistanceAtoms = entryDistanceAtoms;
                bestIntervalLengthAtoms = intervalLengthAtoms;
                protectedIntervalIndex = i;
                protectedInterval = candidate;
            }

            return protectedIntervalIndex >= 0;
        }

        private static int FindProtectedIntervalIndex(LineTrackChain chain, BypassProtectedInterval protectedInterval)
        {
            if (chain == null)
                return -1;

            for (int i = 0; i < chain.BypassProtectedIntervals.Count; i++)
            {
                BypassProtectedInterval candidate = chain.BypassProtectedIntervals[i];
                if (candidate.StartControlPointIndex == protectedInterval.StartControlPointIndex
                    && candidate.EndControlPointIndex == protectedInterval.EndControlPointIndex
                    && candidate.StartAtomIndex == protectedInterval.StartAtomIndex
                    && candidate.EndAtomIndexExclusive == protectedInterval.EndAtomIndexExclusive)
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryResolveVehicleCurrentProtectedIntervalForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out string resolutionSource)
        {
            m_BypassPerfProbeResolveCalls++;
            resolutionSource = "direct";
            if (TryResolveVehicleCurrentProtectedInterval(vehicle, line, waypoints, chain, out protectedIntervalIndex, out protectedInterval))
            {
                return true;
            }

            protectedIntervalIndex = -1;
            protectedInterval = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || chain == null
                || localChain == null
                || waypoints.Length == 0
                || !TryGetVehicleTrackCursorCurrentFrame(vehicle, line, waypoints, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int currentControlEdgeIndex = ResolveControlEdgeIndexForAtom(chain, cursor.AtomCursorIndex);
            if (!TryResolveProtectedIntervalByCursor(
                    chain,
                    currentControlEdgeIndex,
                    cursor.AtomCursorIndex,
                    out protectedIntervalIndex,
                    out protectedInterval))
            {
                return false;
            }

            resolutionSource = "fallback";
            return true;
        }

        private bool TryResolveExpressConflictWindowForLocalConflict(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            LineTrackChain localChain,
            int localProtectedIntervalIndex,
            BypassProtectedInterval localProtectedInterval,
            PhysicalSharedWindowMatch sharedWindowMatch,
            out int protectedIntervalIndex,
            out BypassProtectedInterval protectedInterval,
            out int overlapCount,
            out int orderedRun,
            out string resolutionSource)
        {
            protectedIntervalIndex = -1;
            protectedInterval = default;
            overlapCount = 0;
            orderedRun = 0;
            resolutionSource = "shared-window";

            // Consume the already-selected current shared-window first.
            // This keeps vehicle-level evaluation aligned with the current
            // local scene instead of collapsing back to a coarser
            // local-interval x express-interval matrix.
            if (sharedWindowMatch.Found && !sharedWindowMatch.Ambiguous)
            {
                protectedInterval = sharedWindowMatch.ExpressSharedWindow;
                overlapCount = sharedWindowMatch.OverlapCount;
                orderedRun = sharedWindowMatch.OrderedRun;
                return true;
            }

            if (chain == null
                || chain.BypassProtectedIntervals.Count > 0
                || !sharedWindowMatch.Found
                || sharedWindowMatch.Ambiguous)
            {
                return false;
            }

            protectedInterval = sharedWindowMatch.ExpressSharedWindow;
            overlapCount = sharedWindowMatch.OverlapCount;
            orderedRun = sharedWindowMatch.OrderedRun;
            resolutionSource = "shared-window";
            return true;
        }


    }
}
