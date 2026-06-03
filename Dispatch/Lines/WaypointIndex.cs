using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal enum CursorAtomWindowRelation : byte
    {
        Unknown = 0,
        Before = 1,
        Inside = 2,
        After = 3,
    }
}

namespace RapidTransitMod.Dispatch.Lines
{
    internal sealed class WaypointIndex
    {
        private const bool TrackAnchor = true;

        private readonly DispatchRuntimeSystem m_Runtime;

        public WaypointIndex(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public int Compute(Entity vehicle, DynamicBuffer<RouteWaypoint> ways)
        {
            bool hit = TryCurrent(vehicle, out int cachedWaypointIndex);
            if (hit)
                return cachedWaypointIndex;

            int computedWaypointIndex = ComputeUncached(vehicle, ways);
            Entity route = m_Runtime.m_Resolve.Line(vehicle);
            bool boarding = Boarding(vehicle);
            m_Runtime.m_WaypointIndexFrameSnapshots[vehicle] = new DispatchRuntimeSystem.WaypointIndexFrameSnapshot(
                m_Runtime.m_SimulationSystem.frameIndex,
                route,
                boarding,
                computedWaypointIndex);

            return computedWaypointIndex;
        }

        public int ComputeForOriginArrivingRepair(Entity vehicle, DynamicBuffer<RouteWaypoint> ways)
        {
            return Compute(vehicle, ways);
        }

        public bool TryLookup(
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            out LineWaypointIndexLookup lookup)
        {
            return m_Runtime.TrackModel.TryGetWaypointIndexLookup(line, ways, out lookup);
        }

        public bool TryWindow(
            LineTrackChain chain,
            int waypointIndex,
            int referenceAtomIndex,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (chain == null
                || chain.TraversalProfile == null
                || chain.TraversalProfile.Events == null
                || waypointIndex < 0)
            {
                return false;
            }

            int bestDistance = int.MaxValue;
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                if (traversalEvent.WaypointIndex != waypointIndex
                    || (traversalEvent.Kind != TraversalEventKind.Stop && traversalEvent.Kind != TraversalEventKind.Pass))
                {
                    continue;
                }

                int candidateStart = traversalEvent.StartAtomIndex;
                int candidateEndExclusive = math.max(candidateStart + 1, traversalEvent.EndAtomIndexExclusive);
                int candidateDistance = referenceAtomIndex < candidateStart
                    ? candidateStart - referenceAtomIndex
                    : referenceAtomIndex >= candidateEndExclusive
                        ? referenceAtomIndex - (candidateEndExclusive - 1)
                        : 0;
                if (candidateDistance >= bestDistance)
                    continue;

                bestDistance = candidateDistance;
                startAtomIndex = candidateStart;
                endAtomIndexExclusive = candidateEndExclusive;
            }

            return startAtomIndex >= 0 && endAtomIndexExclusive > startAtomIndex;
        }

        public bool TryRelation(
            LineTrackChain chain,
            int waypointIndex,
            int cursorAtomIndex,
            out CursorAtomWindowRelation relation,
            out int startAtomIndex,
            out int endAtomIndexExclusive)
        {
            relation = CursorAtomWindowRelation.Unknown;
            startAtomIndex = -1;
            endAtomIndexExclusive = -1;
            if (!TryWindow(
                    chain,
                    waypointIndex,
                    cursorAtomIndex,
                    out startAtomIndex,
                    out endAtomIndexExclusive))
            {
                return false;
            }

            relation = Compare(cursorAtomIndex, startAtomIndex, endAtomIndexExclusive);
            return relation != CursorAtomWindowRelation.Unknown;
        }

        public static CursorAtomWindowRelation Compare(
            int cursorAtomIndex,
            int startAtomIndex,
            int endAtomIndexExclusive)
        {
            if (cursorAtomIndex < 0 || startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex)
                return CursorAtomWindowRelation.Unknown;

            if (cursorAtomIndex < startAtomIndex)
                return CursorAtomWindowRelation.Before;

            if (cursorAtomIndex >= endAtomIndexExclusive)
                return CursorAtomWindowRelation.After;

            return CursorAtomWindowRelation.Inside;
        }

        internal bool TryCurrent(Entity vehicle, out int waypointIndex)
        {
            waypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_Runtime.m_WaypointIndexFrameSnapshots.TryGetValue(vehicle, out DispatchRuntimeSystem.WaypointIndexFrameSnapshot snapshot))
            {
                return false;
            }

            if (snapshot.Frame != m_Runtime.m_SimulationSystem.frameIndex)
                return false;

            Entity route = m_Runtime.m_Resolve.Line(vehicle);
            if (snapshot.Route != route)
                return false;

            bool boarding = Boarding(vehicle);
            if (snapshot.Boarding != boarding)
                return false;

            waypointIndex = snapshot.WaypointIndex;
            return true;
        }

        internal int ComputeUncached(Entity vehicle, DynamicBuffer<RouteWaypoint> ways)
        {
            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            bool boarding = Boarding(vehicle);
            int targetWaypointIndex = -1;
            LineWaypointIndexLookup lookup = null;
            if (line != Entity.Null)
                TryLookup(line, ways, out lookup);
            if (m_Runtime.EntityManager.HasComponent<Target>(vehicle))
            {
                Entity targetWaypoint = m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target;
                if (lookup != null
                    && targetWaypoint != Entity.Null
                    && lookup.WaypointIndexByWaypoint.TryGetValue(targetWaypoint, out int indexedTargetWaypointIndex))
                {
                    targetWaypointIndex = indexedTargetWaypointIndex;
                }
                else if (m_Runtime.EntityManager.HasComponent<Waypoint>(targetWaypoint))
                {
                    targetWaypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
                }
            }

            int boardingWaypointIndex = -1;
            if (lookup != null && boarding)
            {
                foreach (KeyValuePair<Entity, int> entry in lookup.WaypointIndexByStop)
                {
                    Entity stop = entry.Key;
                    if (!m_Runtime.EntityManager.Exists(stop)
                        || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(stop)
                        || m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != vehicle)
                    {
                        continue;
                    }

                    boardingWaypointIndex = entry.Value;
                    break;
                }
            }
            else
            {
                for (int wi = 0; wi < ways.Length; wi++)
                {
                    Entity waypoint = ways[wi].m_Waypoint;
                    Entity stop = m_Runtime.EntityManager.HasComponent<Connected>(waypoint)
                        ? m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected
                        : Entity.Null;
                    if (stop == Entity.Null) continue;

                    if (!m_Runtime.EntityManager.HasComponent<BoardingVehicle>(stop)) continue;
                    if (m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != vehicle) continue;
                    boardingWaypointIndex = wi;
                    break;
                }
            }

            bool allowTrackWaypointAnchoring = TrackAnchor
                && m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState trackState)
                && trackState != VehicleState.Retiring;

            if (allowTrackWaypointAnchoring
                && TryTrack(vehicle, ways, targetWaypointIndex, boardingWaypointIndex, -1, out int anchoredWaypointIndex, out string anchorDetail, out string anchorStableKey))
            {
                m_Runtime.LogVehicleStateOnce(
                    m_Runtime.m_BvTrackAnchorRecoveryLogCache,
                    vehicle,
                    "track-anchor|" + anchorStableKey,
                    "[定位接管] 车辆" + vehicle.Index + " 按track锚定 wp[" + anchoredWaypointIndex + "] " + anchorDetail);
                return anchoredWaypointIndex;
            }

            if (boarding && boardingWaypointIndex >= 0)
                return boardingWaypointIndex;

            return -1;
        }

        private bool TryTrack(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int closestWaypointIndex,
            out int waypointIndex,
            out string detail,
            out string stableKey)
        {
            waypointIndex = -1;
            detail = string.Empty;
            stableKey = string.Empty;
            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_Runtime.TrackModel.TryGetChainForLine(line, ways, out LineTrackChain chain)
                || !m_Runtime.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, ways, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            HashSet<int> candidateIndices = new HashSet<int>();
            void AddCandidate(int index)
            {
                if (index >= 0 && index < ways.Length)
                    candidateIndices.Add(index);
            }

            AddCandidate(targetWaypointIndex);
            AddCandidate(boardingWaypointIndex);
            AddCandidate(closestWaypointIndex);
            if (cursor.SegmentIndex >= 0)
            {
                AddCandidate(cursor.SegmentIndex);
                AddCandidate(cursor.SegmentIndex + 1);
                AddCandidate(cursor.SegmentIndex - 1);
                AddCandidate(cursor.SegmentIndex + 2);
            }

            int bestWaypointIndex = -1;
            int bestWindowStart = -1;
            int bestWindowEndExclusive = -1;
            int bestDistance = int.MaxValue;
            const int anchorSlackAtoms = 3;

            foreach (int candidateIndex in candidateIndices)
            {
                if (!TryWindow(chain, candidateIndex, cursor.AtomCursorIndex, out int windowStart, out int windowEndExclusive))
                    continue;

                int expandedStart = math.max(0, windowStart - anchorSlackAtoms);
                int expandedEndExclusive = math.min(chain.TrackAtoms.Count, windowEndExclusive + anchorSlackAtoms);
                if (cursor.AtomCursorIndex < expandedStart || cursor.AtomCursorIndex >= expandedEndExclusive)
                    continue;

                int distance = cursor.AtomCursorIndex < windowStart
                    ? windowStart - cursor.AtomCursorIndex
                    : cursor.AtomCursorIndex >= windowEndExclusive
                        ? cursor.AtomCursorIndex - (windowEndExclusive - 1)
                        : 0;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestWaypointIndex = candidateIndex;
                bestWindowStart = windowStart;
                bestWindowEndExclusive = windowEndExclusive;
            }

            if (bestWaypointIndex < 0)
                return false;

            waypointIndex = bestWaypointIndex;
            stableKey = "wp=" + bestWaypointIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
            detail = "atom=" + cursor.AtomCursorIndex
                + " seg=" + cursor.SegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=" + closestWaypointIndex
                + " window=" + bestWindowStart + ".." + bestWindowEndExclusive;
            return true;
        }

        private bool Boarding(Entity vehicle)
        {
            return m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                && (m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
        }
    }
}
