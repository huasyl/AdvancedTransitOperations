using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Diagnostics;
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
        [System.Flags]
        private enum WaypointIndexStage : byte
        {
            None = 0,
            IndependentFailed = 1,
            IndependentConfirmed = 2,
            Computed = 4,
        }

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, WaypointIndexFrameSnapshot> m_FrameSnapshots = new Dictionary<Entity, WaypointIndexFrameSnapshot>();

        private struct WaypointIndexFrameSnapshot
        {
            public uint Frame;
            public Entity Route;
            public bool Boarding;
            public WaypointIndexStage Stage;
            public int WaypointIndex;
            public int TargetWaypointIndex;

            public WaypointIndexFrameSnapshot(uint frame, Entity route, bool boarding)
            {
                Frame = frame;
                Route = route;
                Boarding = boarding;
                Stage = WaypointIndexStage.None;
                WaypointIndex = -1;
                TargetWaypointIndex = -1;
            }
        }

        public WaypointIndex(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public int Compute(Entity vehicle, DynamicBuffer<RouteWaypoint> ways)
        {
            Entity route = m_Runtime.m_Resolve.Line(vehicle);
            bool boarding = Boarding(vehicle);
            if (TryCurrentSnapshot(vehicle, route, boarding, out WaypointIndexFrameSnapshot snapshot)
                && (snapshot.Stage & WaypointIndexStage.Computed) != 0)
            {
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state == VehicleState.Retiring)
                {
                    return -1;
                }

                return snapshot.WaypointIndex;
            }

            int computedWaypointIndex = ComputeUncached(vehicle, route, boarding, ways, out int targetWaypointIndex);
            if (!TryCurrentSnapshot(vehicle, route, boarding, out snapshot))
                snapshot = new WaypointIndexFrameSnapshot(m_Runtime.m_SimulationSystem.frameIndex, route, boarding);
            snapshot.Stage |= WaypointIndexStage.Computed;
            snapshot.WaypointIndex = computedWaypointIndex;
            snapshot.TargetWaypointIndex = targetWaypointIndex;
            m_FrameSnapshots[vehicle] = snapshot;

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

        internal bool TryResolveProjectionTargetWaypoint(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            out int waypointIndex)
        {
            waypointIndex = -1;
            return TryResolveTargetWaypoint(vehicle, ways, out waypointIndex, out _);
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
            Entity route = m_Runtime.m_Resolve.Line(vehicle);
            bool boarding = Boarding(vehicle);
            if (!TryCurrentSnapshot(vehicle, route, boarding, out WaypointIndexFrameSnapshot snapshot)
                || (snapshot.Stage & WaypointIndexStage.Computed) == 0)
                return false;

            waypointIndex = snapshot.WaypointIndex;
            return true;
        }

        internal bool TryConfirmCurrentBoardingWaypoint(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            bool hasHeadCurrentLane,
            TrainCurrentLane headCurrentLane,
            out int waypointIndex)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || m_Runtime.m_Resolve.Line(vehicle) != line
                || !Boarding(vehicle)
                || !m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                || state == VehicleState.Retiring)
            {
                waypointIndex = -1;
                return false;
            }

            return TryConfirmBoardingWaypoint(
                vehicle,
                line,
                ways,
                hasHeadCurrentLane,
                headCurrentLane,
                out _,
                out waypointIndex);
        }

        internal int ComputeUncached(
            Entity vehicle,
            Entity line,
            bool boarding,
            DynamicBuffer<RouteWaypoint> ways,
            out int targetWaypointIndex)
        {
            bool allowTrackWaypointAnchoring = m_Runtime.m_VehicleView.TryGetState(
                vehicle,
                out VehicleState trackState)
                && trackState != VehicleState.Retiring;
            bool captureStationOutcome = boarding
                && allowTrackWaypointAnchoring
                && RuntimeHotPathProbe.Enabled();
            targetWaypointIndex = -1;
            int confirmedWaypointIndex = -1;
            bool hasBoardingConfirmation;
            if (boarding && allowTrackWaypointAnchoring)
            {
                hasBoardingConfirmation = TryConfirmBoardingWaypoint(
                    vehicle,
                    line,
                    ways,
                    false,
                    default,
                    out targetWaypointIndex,
                    out confirmedWaypointIndex);
            }
            else
            {
                if (!boarding)
                {
                    TryResolveTargetWaypoint(
                        vehicle,
                        ways,
                        out targetWaypointIndex,
                        out _);
                }
                hasBoardingConfirmation = false;
            }
            if (hasBoardingConfirmation)
            {
                if (captureStationOutcome)
                    m_Runtime.m_RuntimeHotPathProbe.RecordWaypointStationOutcome(true);
                return confirmedWaypointIndex;
            }

            if (allowTrackWaypointAnchoring
                && TryCurrentLaneWaypoint(
                    vehicle,
                    line,
                    ways,
                    targetWaypointIndex,
                    targetWaypointIndex,
                    out int currentWaypointIndex))
            {
                if (captureStationOutcome)
                    m_Runtime.m_RuntimeHotPathProbe.RecordWaypointStationOutcome(true);
                return currentWaypointIndex;
            }

            if (captureStationOutcome)
                m_Runtime.m_RuntimeHotPathProbe.RecordWaypointStationOutcome(false);
            return -1;
        }

        internal bool TryConfirmBoardingWaypoint(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            bool hasHeadCurrentLane,
            TrainCurrentLane headCurrentLane,
            out int targetWaypointIndex,
            out int waypointIndex)
        {
            targetWaypointIndex = -1;
            waypointIndex = -1;
            if (TryCurrentSnapshot(vehicle, line, true, out WaypointIndexFrameSnapshot snapshot)
                && (snapshot.Stage & (WaypointIndexStage.IndependentFailed | WaypointIndexStage.IndependentConfirmed)) != 0)
            {
                targetWaypointIndex = snapshot.TargetWaypointIndex;
                if ((snapshot.Stage & WaypointIndexStage.IndependentConfirmed) != 0)
                {
                    waypointIndex = snapshot.WaypointIndex;
                    return waypointIndex >= 0;
                }

                return false;
            }

            if (!TryResolveTargetWaypoint(
                    vehicle, ways, out targetWaypointIndex, out Entity targetWaypoint))
            {
                StoreIndependentResult(vehicle, line, false, targetWaypointIndex, -1);
                return false;
            }

            if (!TryResolveBoardingTarget(
                    vehicle,
                    targetWaypoint,
                    out Entity targetBuilding,
                    out bool outsideConfirmed))
            {
                StoreIndependentResult(vehicle, line, false, targetWaypointIndex, -1);
                return false;
            }

            if (outsideConfirmed)
            {
                waypointIndex = targetWaypointIndex;
                StoreIndependentResult(vehicle, line, true, targetWaypointIndex, waypointIndex);
                return true;
            }

            if (!TryMatchStationTrack(
                    vehicle,
                    line,
                    targetWaypoint,
                    targetBuilding,
                    hasHeadCurrentLane,
                    headCurrentLane))
            {
                StoreIndependentResult(vehicle, line, false, targetWaypointIndex, -1);
                return false;
            }

            waypointIndex = targetWaypointIndex;
            StoreIndependentResult(vehicle, line, true, targetWaypointIndex, waypointIndex);
            return true;
        }

        private void StoreIndependentResult(
            Entity vehicle,
            Entity line,
            bool confirmed,
            int targetWaypointIndex,
            int waypointIndex)
        {
            WaypointIndexFrameSnapshot snapshot = new WaypointIndexFrameSnapshot(
                m_Runtime.m_SimulationSystem.frameIndex,
                line,
                true);
            snapshot.Stage = confirmed
                ? WaypointIndexStage.IndependentConfirmed
                : WaypointIndexStage.IndependentFailed;
            snapshot.WaypointIndex = waypointIndex;
            snapshot.TargetWaypointIndex = targetWaypointIndex;
            m_FrameSnapshots[vehicle] = snapshot;
        }

        private bool TryResolveTargetWaypoint(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            out int targetWaypointIndex,
            out Entity targetWaypoint)
        {
            targetWaypointIndex = -1;
            targetWaypoint = Entity.Null;
            if (!m_Runtime.m_RailEventSource.TryReadTarget(vehicle, out Target target))
            {
                return false;
            }

            targetWaypoint = target.m_Target;
            if (targetWaypoint == Entity.Null
                || !m_Runtime.EntityManager.Exists(targetWaypoint)
                || !m_Runtime.EntityManager.HasComponent<Waypoint>(targetWaypoint))
            {
                return false;
            }

            targetWaypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
            if (targetWaypointIndex < 0
                || targetWaypointIndex >= ways.Length
                || ways[targetWaypointIndex].m_Waypoint != targetWaypoint)
            {
                return false;
            }

            return true;
        }

        private bool TryResolveBoardingTarget(
            Entity vehicle,
            Entity targetWaypoint,
            out Entity targetBuilding,
            out bool outsideConfirmed)
        {
            targetBuilding = Entity.Null;
            outsideConfirmed = false;
            if (!m_Runtime.EntityManager.HasComponent<Connected>(targetWaypoint))
            {
                return false;
            }

            Entity targetStop = m_Runtime.EntityManager.GetComponentData<Connected>(targetWaypoint).m_Connected;
            if (targetStop == Entity.Null
                || !m_Runtime.EntityManager.Exists(targetStop)
                || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(targetStop))
            {
                return false;
            }

            Entity boardingVehicle = m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle;
            if (boardingVehicle != vehicle)
            {
                return false;
            }

            targetBuilding = m_Runtime.m_Resolve.PassingStation(targetStop);
            if (m_Runtime.EntityManager.HasComponent<Game.Objects.OutsideConnection>(targetStop))
            {
                outsideConfirmed = true;
            }
            return true;
        }

        private bool TryCurrentLaneWaypoint(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> ways,
            int targetWaypointIndex,
            int boardingWaypointIndex,
            out int waypointIndex)
        {
            waypointIndex = -1;
            if (!m_Runtime.TrackModel.TryGetChainForLine(line, ways, out LineTrackChain chain)
                || !m_Runtime.TrackProjection.TryGetExactCurrentFrameCursor(
                    vehicle,
                    line,
                    ways,
                    chain,
                    out VehicleTrackCursor cursor,
                    ProjectionRequestSource.Waypoint)
                || cursor.Source != VehicleTrackCursorSource.CurrentLane)
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
            void AddCandidate(int candidate)
            {
                if (candidate < 0 || candidate >= ways.Length)
                    return;

                for (int i = 0; i < candidateCount; i++)
                {
                    int existing = i == 0 ? candidate0
                        : i == 1 ? candidate1
                        : i == 2 ? candidate2
                        : i == 3 ? candidate3
                        : i == 4 ? candidate4
                        : candidate5;
                    if (existing == candidate)
                        return;
                }

                switch (candidateCount)
                {
                    case 0: candidate0 = candidate; break;
                    case 1: candidate1 = candidate; break;
                    case 2: candidate2 = candidate; break;
                    case 3: candidate3 = candidate; break;
                    case 4: candidate4 = candidate; break;
                    case 5: candidate5 = candidate; break;
                    default: return;
                }
                candidateCount++;
            }

            AddCandidate(targetWaypointIndex);
            AddCandidate(boardingWaypointIndex);
            if (cursor.SegmentIndex >= 0)
            {
                AddCandidate(cursor.SegmentIndex);
                AddCandidate(cursor.SegmentIndex + 1);
                AddCandidate(cursor.SegmentIndex - 1);
                AddCandidate(cursor.SegmentIndex + 2);
            }

            int bestWaypointIndex = -1;
            int bestDistance = int.MaxValue;
            for (int candidateSlot = 0; candidateSlot < candidateCount; candidateSlot++)
            {
                int candidate = candidateSlot == 0 ? candidate0
                    : candidateSlot == 1 ? candidate1
                    : candidateSlot == 2 ? candidate2
                    : candidateSlot == 3 ? candidate3
                    : candidateSlot == 4 ? candidate4
                    : candidate5;
                if (!TryWindow(
                        chain,
                        candidate,
                        cursor.AtomCursorIndex,
                        out int windowStart,
                        out int windowEndExclusive))
                {
                    continue;
                }

                if (cursor.AtomCursorIndex < windowStart || cursor.AtomCursorIndex >= windowEndExclusive)
                    continue;

                int distance = cursor.AtomCursorIndex < windowStart
                    ? windowStart - cursor.AtomCursorIndex
                    : cursor.AtomCursorIndex >= windowEndExclusive
                        ? cursor.AtomCursorIndex - (windowEndExclusive - 1)
                        : 0;
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestWaypointIndex = candidate;
            }

            waypointIndex = bestWaypointIndex;
            return waypointIndex >= 0;
        }

        private bool TryMatchStationTrack(
            Entity vehicle,
            Entity line,
            Entity targetWaypoint,
            Entity targetBuilding,
            bool hasHeadCurrentLane,
            TrainCurrentLane headCurrentLane)
        {
            bool tramStop = targetBuilding == Entity.Null
                && TransportModeResolver.Resolve(m_Runtime.EntityManager, line) == TransitMode.Tram;
            if (targetBuilding == Entity.Null && !tramStop)
            {
                return false;
            }

            Entity tramStartLane = Entity.Null;
            Entity tramEndLane = Entity.Null;
            if (tramStop)
            {
                if (!m_Runtime.EntityManager.HasComponent<RouteLane>(targetWaypoint))
                {
                    return false;
                }

                RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(targetWaypoint);
                tramStartLane = routeLane.m_StartLane;
                tramEndLane = routeLane.m_EndLane;
            }

            Entity head = ResolveHead(vehicle);
            if (!hasHeadCurrentLane)
                hasHeadCurrentLane = m_Runtime.m_RailEventSource.TryReadProjectionCurrentLane(
                    vehicle,
                    out headCurrentLane);
            if (hasHeadCurrentLane
                && TryMatchCarriageTrack(
                    head,
                    targetBuilding,
                    tramStop,
                    tramStartLane,
                    tramEndLane,
                    hasHeadCurrentLane,
                    headCurrentLane))
                return true;
            if (head != vehicle
                && TryMatchCarriageTrack(
                    vehicle,
                    targetBuilding,
                    tramStop,
                    tramStartLane,
                    tramEndLane,
                    false,
                    default))
            {
                return true;
            }

            if (!m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                return false;
            }

            DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            if (layout.Length == 0)
            {
                return false;
            }

            Entity middle = layout[layout.Length / 2].m_Vehicle;
            if (middle != Entity.Null
                && middle != head
                && middle != vehicle
                && TryMatchCarriageTrack(
                    middle,
                    targetBuilding,
                    tramStop,
                    tramStartLane,
                    tramEndLane,
                    false,
                    default))
            {
                return true;
            }

            return false;
        }

        private Entity ResolveHead(Entity vehicle)
        {
            if (!m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
                return vehicle;

            DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            return layout.Length == 0 ? vehicle : layout[0].m_Vehicle;
        }

        private bool TryMatchCarriageTrack(
            Entity carriage,
            Entity targetBuilding,
            bool tramStop,
            Entity tramStartLane,
            Entity tramEndLane,
            bool hasKnownCurrentLane,
            TrainCurrentLane knownCurrentLane)
        {
            if (carriage == Entity.Null || !m_Runtime.EntityManager.Exists(carriage))
                return false;

            TrainCurrentLane currentLane;
            if (hasKnownCurrentLane)
            {
                currentLane = knownCurrentLane;
            }
            else
            {
                if (!m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(carriage))
                    return false;
                currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(carriage);
            }
            if (TryMatchLane(currentLane.m_Front.m_Lane, targetBuilding, tramStop, tramStartLane, tramEndLane))
                return true;
            return TryMatchLane(currentLane.m_Rear.m_Lane, targetBuilding, tramStop, tramStartLane, tramEndLane);
        }

        private bool TryMatchLane(
            Entity lane,
            Entity targetBuilding,
            bool tramStop,
            Entity tramStartLane,
            Entity tramEndLane)
        {
            if (lane == Entity.Null || !m_Runtime.EntityManager.Exists(lane))
                return false;
            if (targetBuilding != Entity.Null)
            {
                Entity trackBuilding = m_Runtime.m_Resolve.PassingStation(lane);
                return trackBuilding != Entity.Null && trackBuilding == targetBuilding;
            }
            return tramStop && (lane == tramStartLane || lane == tramEndLane);
        }

        private bool Boarding(Entity vehicle)
        {
            return m_Runtime.m_RailEventSource.TryReadPublicTransport(
                vehicle,
                out PublicTransport publicTransport)
                && (publicTransport.m_State & PublicTransportFlags.Boarding) != 0;
        }

        private bool TryCurrentSnapshot(
            Entity vehicle,
            Entity route,
            bool boarding,
            out WaypointIndexFrameSnapshot snapshot)
        {
            if (vehicle != Entity.Null
                && m_FrameSnapshots.TryGetValue(vehicle, out snapshot)
                && snapshot.Frame == m_Runtime.m_SimulationSystem.frameIndex
                && snapshot.Route == route
                && snapshot.Boarding == boarding)
            {
                return true;
            }

            snapshot = default;
            return false;
        }

        public void Remove(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_FrameSnapshots.Remove(vehicle);
        }

        public void Clear()
        {
            m_FrameSnapshots.Clear();
        }
    }
}
