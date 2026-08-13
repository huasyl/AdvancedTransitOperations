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

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, WaypointIndexFrameSnapshot> m_FrameSnapshots = new Dictionary<Entity, WaypointIndexFrameSnapshot>();
        private readonly Dictionary<Entity, TrackAnchorSnapshot> m_TrackAnchorSnapshots = new Dictionary<Entity, TrackAnchorSnapshot>();

        private readonly struct WaypointIndexFrameSnapshot
        {
            public readonly uint Frame;
            public readonly Entity Route;
            public readonly bool Boarding;
            public readonly int WaypointIndex;

            public WaypointIndexFrameSnapshot(uint frame, Entity route, bool boarding, int waypointIndex)
            {
                Frame = frame;
                Route = route;
                Boarding = boarding;
                WaypointIndex = waypointIndex;
            }
        }

        private readonly struct TrackAnchorSnapshot
        {
            public readonly Entity Line;
            public readonly int TargetWaypointIndex;
            public readonly int BoardingWaypointIndex;
            public readonly int WaypointIndex;
            public readonly int SegmentIndex;
            public readonly int AtomIndex;
            public readonly int WindowStart;
            public readonly int WindowEndExclusive;

            public TrackAnchorSnapshot(
                Entity line,
                int targetWaypointIndex,
                int boardingWaypointIndex,
                int waypointIndex,
                int segmentIndex,
                int atomIndex,
                int windowStart,
                int windowEndExclusive)
            {
                Line = line;
                TargetWaypointIndex = targetWaypointIndex;
                BoardingWaypointIndex = boardingWaypointIndex;
                WaypointIndex = waypointIndex;
                SegmentIndex = segmentIndex;
                AtomIndex = atomIndex;
                WindowStart = windowStart;
                WindowEndExclusive = windowEndExclusive;
            }
        }

        public WaypointIndex(ModRuntimeHostSystem runtime)
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
            m_FrameSnapshots[vehicle] = new WaypointIndexFrameSnapshot(
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
                || !m_FrameSnapshots.TryGetValue(vehicle, out WaypointIndexFrameSnapshot snapshot))
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
                && boarding
                && TryGetTrackAnchorSnapshot(
                    vehicle,
                    line,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    out TrackAnchorSnapshot cachedAnchor))
            {
                MaybeLogTrackAnchor(
                    vehicle,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    cachedAnchor.WaypointIndex,
                    cachedAnchor.SegmentIndex,
                    cachedAnchor.AtomIndex,
                    cachedAnchor.WindowStart,
                    cachedAnchor.WindowEndExclusive);
                return cachedAnchor.WaypointIndex;
            }

            if (allowTrackWaypointAnchoring
                && TryTrack(
                    vehicle,
                    ways,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    -1,
                    out int anchoredWaypointIndex,
                    out int anchorSegmentIndex,
                    out int anchorAtomIndex,
                    out int anchorWindowStart,
                    out int anchorWindowEndExclusive))
            {
                if (boarding)
                {
                    m_TrackAnchorSnapshots[vehicle] = new TrackAnchorSnapshot(
                        line,
                        targetWaypointIndex,
                        boardingWaypointIndex,
                        anchoredWaypointIndex,
                        anchorSegmentIndex,
                        anchorAtomIndex,
                        anchorWindowStart,
                        anchorWindowEndExclusive);
                }

                MaybeLogTrackAnchor(
                    vehicle,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    anchoredWaypointIndex,
                    anchorSegmentIndex,
                    anchorAtomIndex,
                    anchorWindowStart,
                    anchorWindowEndExclusive);
                return anchoredWaypointIndex;
            }

            if (boarding && boardingWaypointIndex >= 0)
                return boardingWaypointIndex;

            return -1;
        }

        private bool TryGetTrackAnchorSnapshot(
            Entity vehicle,
            Entity line,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            out TrackAnchorSnapshot snapshot)
        {
            if (m_TrackAnchorSnapshots.TryGetValue(vehicle, out snapshot)
                && snapshot.Line == line
                && snapshot.TargetWaypointIndex == targetWaypointIndex
                && snapshot.BoardingWaypointIndex == boardingWaypointIndex)
            {
                return true;
            }

            snapshot = default;
            return false;
        }

        private void MaybeLogTrackAnchor(
            Entity vehicle,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int anchoredWaypointIndex,
            int anchorSegmentIndex,
            int anchorAtomIndex,
            int anchorWindowStart,
            int anchorWindowEndExclusive)
        {
            if (!RtLog.VerboseEnabled)
                return;

            string anchorStableKey = "track-anchor|wp=" + anchoredWaypointIndex;
            if (!m_Runtime.m_RuntimeLog.ShouldLogOnce(
                    m_Runtime.m_RuntimeLog.m_BvTrackAnchorRecoveryLogCache,
                    vehicle,
                    anchorStableKey))
            {
                return;
            }

            string anchorDetail = "atom=" + anchorAtomIndex
                + " seg=" + anchorSegmentIndex
                + " targetWp=" + targetWaypointIndex
                + " bvWp=" + boardingWaypointIndex
                + " closestWp=-1"
                + " window=" + anchorWindowStart + ".." + anchorWindowEndExclusive;
            m_Runtime.m_RuntimeLog.Once(
                m_Runtime.m_RuntimeLog.m_BvTrackAnchorRecoveryLogCache,
                vehicle,
                anchorStableKey,
                "[定位接管] 车辆" + vehicle.Index + " 按track锚定 wp[" + anchoredWaypointIndex + "] " + anchorDetail);
        }

        private bool TryTrack(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int closestWaypointIndex,
            out int waypointIndex,
            out int segmentIndex,
            out int atomIndex,
            out int selectedWindowStart,
            out int selectedWindowEndExclusive)
        {
            waypointIndex = -1;
            segmentIndex = -1;
            atomIndex = -1;
            selectedWindowStart = -1;
            selectedWindowEndExclusive = -1;
            Entity line = m_Runtime.m_Resolve.Line(vehicle);
            if (vehicle == Entity.Null
                || line == Entity.Null
                || !m_Runtime.TrackModel.TryGetChainForLine(line, ways, out LineTrackChain chain)
                || !m_Runtime.TrackProjection.TryGetVehicleTrackCursorCurrentFrame(vehicle, line, ways, chain, out VehicleTrackCursor cursor))
            {
                return false;
            }

            int candidateCount = 0;
            int candidate0 = -1;
            int candidate1 = -1;
            int candidate2 = -1;
            int candidate3 = -1;
            int candidate4 = -1;
            int candidate5 = -1;
            int candidate6 = -1;
            void AddCandidate(int index)
            {
                if (index >= 0 && index < ways.Length)
                {
                    for (int i = 0; i < candidateCount; i++)
                    {
                        int existing = i == 0 ? candidate0
                            : i == 1 ? candidate1
                            : i == 2 ? candidate2
                            : i == 3 ? candidate3
                            : i == 4 ? candidate4
                            : i == 5 ? candidate5
                            : candidate6;
                        if (existing == index)
                            return;
                    }

                    switch (candidateCount)
                    {
                        case 0: candidate0 = index; break;
                        case 1: candidate1 = index; break;
                        case 2: candidate2 = index; break;
                        case 3: candidate3 = index; break;
                        case 4: candidate4 = index; break;
                        case 5: candidate5 = index; break;
                        case 6: candidate6 = index; break;
                        default: return;
                    }
                    candidateCount++;
                }
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

            for (int candidateSlot = 0; candidateSlot < candidateCount; candidateSlot++)
            {
                int candidateIndex = candidateSlot == 0 ? candidate0
                    : candidateSlot == 1 ? candidate1
                    : candidateSlot == 2 ? candidate2
                    : candidateSlot == 3 ? candidate3
                    : candidateSlot == 4 ? candidate4
                    : candidateSlot == 5 ? candidate5
                    : candidate6;
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

            if (boardingWaypointIndex >= 0 && bestWaypointIndex != boardingWaypointIndex)
            {
                TraceTrackMismatch(
                    vehicle,
                    ways,
                    chain,
                    cursor,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    bestWaypointIndex,
                    bestWindowStart,
                    bestWindowEndExclusive);
            }

            waypointIndex = bestWaypointIndex;
            segmentIndex = cursor.SegmentIndex;
            atomIndex = cursor.AtomCursorIndex;
            selectedWindowStart = bestWindowStart;
            selectedWindowEndExclusive = bestWindowEndExclusive;
            return true;
        }

        private void TraceTrackMismatch(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            LineTrackChain chain,
            VehicleTrackCursor cursor,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            int selectedWaypointIndex,
            int selectedWindowStart,
            int selectedWindowEndExclusive)
        {
            if (!RtLog.VerboseEnabled)
                return;

            TryWindow(
                chain,
                boardingWaypointIndex,
                cursor.AtomCursorIndex,
                out int boardingWindowStart,
                out int boardingWindowEndExclusive);
            Entity frontLane = Entity.Null;
            Entity rearLane = Entity.Null;
            float frontCurve = -1f;
            float rearCurve = -1f;
            if (m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(vehicle))
            {
                TrainCurrentLane currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(vehicle);
                frontLane = currentLane.m_Front.m_Lane;
                rearLane = currentLane.m_Rear.m_Lane;
                frontCurve = currentLane.m_Front.m_CurvePosition.x;
                rearCurve = currentLane.m_Rear.m_CurvePosition.x;
            }

            Entity boardingWaypoint = boardingWaypointIndex >= 0 && boardingWaypointIndex < ways.Length
                ? ways[boardingWaypointIndex].m_Waypoint
                : Entity.Null;
            Entity boardingStop = boardingWaypoint != Entity.Null
                && m_Runtime.EntityManager.HasComponent<Connected>(boardingWaypoint)
                    ? m_Runtime.EntityManager.GetComponentData<Connected>(boardingWaypoint).m_Connected
                    : Entity.Null;
            Entity stopVehicle = boardingStop != Entity.Null
                && m_Runtime.EntityManager.HasComponent<BoardingVehicle>(boardingStop)
                    ? m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(boardingStop).m_Vehicle
                    : Entity.Null;
            RtLog.Diagnostics(
                "[TrackAnchorMismatch] frame=" + m_Runtime.m_SimulationSystem.frameIndex
                + " vehicle=" + vehicle.Index
                + " line=" + m_Runtime.m_Resolve.Line(vehicle).Index
                + " targetWp=" + targetWaypointIndex
                + " boardingWp=" + boardingWaypointIndex
                + " selectedWp=" + selectedWaypointIndex
                + " cursorSource=" + cursor.Source
                + " cursorConfidence=" + cursor.Confidence.ToString("0.00")
                + " cursorSeg=" + cursor.SegmentIndex
                + " cursorAtom=" + cursor.AtomCursorIndex
                + " cursorPos=" + cursor.AtomPosition01.ToString("0.000")
                + " boardingWindow=" + boardingWindowStart + ".." + boardingWindowEndExclusive
                + " selectedWindow=" + selectedWindowStart + ".." + selectedWindowEndExclusive
                + " frontLane=" + frontLane.Index
                + " frontCurve=" + frontCurve.ToString("0.000")
                + " rearLane=" + rearLane.Index
                + " rearCurve=" + rearCurve.ToString("0.000")
                + " boardingStop=" + boardingStop.Index
                + " stopVehicle=" + stopVehicle.Index
                + " frontLaneTrace=" + FormatLaneTrace(frontLane)
                + " boardingLaneTrace=" + FormatWindowTrace(chain, boardingWindowStart, boardingWindowEndExclusive));
        }

        private string FormatLaneTrace(Entity lane)
        {
            if (lane == Entity.Null)
                return "null";

            string trace = string.Empty;
            Entity current = lane;
            for (int depth = 0; depth < 8 && current != Entity.Null; depth++)
            {
                if (!m_Runtime.EntityManager.Exists(current))
                    return trace + (trace.Length == 0 ? string.Empty : ">") + current.Index + "[missing]";

                Entity station = m_Runtime.m_Resolve.PassingStation(current);
                trace += (trace.Length == 0 ? string.Empty : ">")
                    + current.Index
                    + "[track=" + (m_Runtime.EntityManager.HasComponent<Game.Net.TrackLane>(current) ? 1 : 0)
                    + ",edgeLane=" + (m_Runtime.EntityManager.HasComponent<Game.Net.EdgeLane>(current) ? 1 : 0)
                    + ",connection=" + (m_Runtime.EntityManager.HasComponent<Game.Net.ConnectionLane>(current) ? 1 : 0)
                    + ",edge=" + (m_Runtime.EntityManager.HasComponent<Game.Net.Edge>(current) ? 1 : 0)
                    + ",node=" + (m_Runtime.EntityManager.HasComponent<Game.Net.Node>(current) ? 1 : 0)
                    + ",subLanes=" + (m_Runtime.EntityManager.HasBuffer<Game.Net.SubLane>(current) ? 1 : 0)
                    + ",station=" + station.Index
                    + "]";

                if (!m_Runtime.EntityManager.HasComponent<Owner>(current))
                    break;

                Entity owner = m_Runtime.EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                    break;
                current = owner;
            }

            return trace;
        }

        private string FormatWindowTrace(LineTrackChain chain, int startAtomIndex, int endAtomIndexExclusive)
        {
            if (chain == null || startAtomIndex < 0 || endAtomIndexExclusive <= startAtomIndex)
                return "none";

            string trace = string.Empty;
            int end = math.min(endAtomIndexExclusive, chain.TrackAtoms.Count);
            for (int atomIndex = startAtomIndex; atomIndex < end; atomIndex++)
            {
                TrackAtom atom = chain.TrackAtoms[atomIndex];
                trace += (trace.Length == 0 ? string.Empty : ";")
                    + atomIndex + ":" + FormatLaneTrace(atom.Key.PhysicalLaneKey);
            }

            return trace;
        }

        private bool Boarding(Entity vehicle)
        {
            return m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                && (m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
            {
                m_FrameSnapshots.Remove(vehicle);
                m_TrackAnchorSnapshots.Remove(vehicle);
            }
        }

        public void Clear()
        {
            m_FrameSnapshots.Clear();
            m_TrackAnchorSnapshots.Clear();
        }
    }
}
