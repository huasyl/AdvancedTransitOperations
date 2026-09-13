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
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, WaypointIndexFrameSnapshot> m_FrameSnapshots = new Dictionary<Entity, WaypointIndexFrameSnapshot>();

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
            bool allowTrackWaypointAnchoring = m_Runtime.m_VehicleView.TryGetState(
                vehicle,
                out VehicleState trackState)
                && trackState != VehicleState.Retiring;
            bool captureStationOutcome = boarding
                && allowTrackWaypointAnchoring
                && RuntimeHotPathProbe.Enabled();
            WaypointStationOutcome outcome = captureStationOutcome
                ? CreateStationOutcome(vehicle)
                : default;
            bool hasTargetWaypoint = TryResolveTargetWaypoint(
                vehicle,
                ways,
                ref outcome,
                out int targetWaypointIndex,
                out Entity targetWaypoint);
            int boardingWaypointIndex = -1;
            Entity targetBuilding = Entity.Null;
            bool outsideConfirmed = false;
            bool hasBoardingTarget = allowTrackWaypointAnchoring
                && boarding
                && hasTargetWaypoint
                && TryResolveBoardingTarget(
                    vehicle,
                    targetWaypoint,
                    ref outcome,
                    out targetBuilding,
                    out outsideConfirmed);

            if (hasBoardingTarget && outsideConfirmed)
            {
                outcome.WaypointIndex = targetWaypointIndex;
                outcome.TargetWaypointIndex = targetWaypointIndex;
                outcome.Evidence = WaypointStationEvidence.OutsideConfirmed;
                if (captureStationOutcome)
                    RecordStationOutcome(outcome);
                return targetWaypointIndex;
            }

            if (hasBoardingTarget
                && TryMatchStationTrack(
                    vehicle,
                    line,
                    targetWaypoint,
                    targetBuilding,
                    ref outcome))
            {
                outcome.WaypointIndex = targetWaypointIndex;
                outcome.TargetWaypointIndex = targetWaypointIndex;
                outcome.Evidence = WaypointStationEvidence.Confirmed;
                if (captureStationOutcome)
                    RecordStationOutcome(outcome);
                return targetWaypointIndex;
            }

            if (hasBoardingTarget)
                boardingWaypointIndex = targetWaypointIndex;

            if (allowTrackWaypointAnchoring
                && TryCurrentLaneWaypoint(
                    vehicle,
                    line,
                    ways,
                    targetWaypointIndex,
                    boardingWaypointIndex,
                    out int currentWaypointIndex))
            {
                outcome.WaypointIndex = currentWaypointIndex;
                outcome.Evidence = WaypointStationEvidence.CurrentLaneWindow;
                if (captureStationOutcome)
                    RecordStationOutcome(outcome);
                return currentWaypointIndex;
            }

            if (captureStationOutcome)
                RecordStationOutcome(outcome);
            return -1;
        }

        private WaypointStationOutcome CreateStationOutcome(Entity vehicle)
        {
            return new WaypointStationOutcome
            {
                Frame = m_Runtime.m_SimulationSystem.frameIndex,
                Vehicle = vehicle,
                TargetWaypointIndex = -1,
                CachedWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint)
                    ? cachedWaypoint
                    : -1,
                WaypointIndex = -1,
                Evidence = WaypointStationEvidence.CurrentLaneUnavailable
            };
        }

        private void RecordStationOutcome(WaypointStationOutcome outcome)
        {
            m_Runtime.m_RuntimeHotPathProbe.RecordWaypointStationOutcome(outcome);
        }

        private bool TryResolveTargetWaypoint(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> ways,
            ref WaypointStationOutcome outcome,
            out int targetWaypointIndex,
            out Entity targetWaypoint)
        {
            targetWaypointIndex = -1;
            targetWaypoint = Entity.Null;
            Target target;
            if (!m_Runtime.m_RailEventSource.TryGetWrittenTarget(vehicle, out target))
            {
                if (!m_Runtime.EntityManager.HasComponent<Target>(vehicle))
                {
                    outcome.Evidence = WaypointStationEvidence.TargetUnavailable;
                    return false;
                }

                target = m_Runtime.EntityManager.GetComponentData<Target>(vehicle);
            }

            targetWaypoint = target.m_Target;
            outcome.Target = targetWaypoint;
            if (targetWaypoint == Entity.Null
                || !m_Runtime.EntityManager.Exists(targetWaypoint)
                || !m_Runtime.EntityManager.HasComponent<Waypoint>(targetWaypoint))
            {
                outcome.Evidence = WaypointStationEvidence.TargetUnavailable;
                return false;
            }

            targetWaypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(targetWaypoint).m_Index;
            outcome.TargetWaypointIndex = targetWaypointIndex;
            if (targetWaypointIndex < 0
                || targetWaypointIndex >= ways.Length
                || ways[targetWaypointIndex].m_Waypoint != targetWaypoint)
            {
                outcome.Evidence = WaypointStationEvidence.TargetNotOnLine;
                return false;
            }

            return true;
        }

        private bool TryResolveBoardingTarget(
            Entity vehicle,
            Entity targetWaypoint,
            ref WaypointStationOutcome outcome,
            out Entity targetBuilding,
            out bool outsideConfirmed)
        {
            targetBuilding = Entity.Null;
            outsideConfirmed = false;
            if (!m_Runtime.EntityManager.HasComponent<Connected>(targetWaypoint))
            {
                outcome.Evidence = WaypointStationEvidence.StopUnavailable;
                return false;
            }

            Entity targetStop = m_Runtime.EntityManager.GetComponentData<Connected>(targetWaypoint).m_Connected;
            outcome.BoardingStop = targetStop;
            if (targetStop == Entity.Null
                || !m_Runtime.EntityManager.Exists(targetStop)
                || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(targetStop))
            {
                outcome.Evidence = WaypointStationEvidence.StopUnavailable;
                return false;
            }

            Entity boardingVehicle = m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle;
            outcome.BoardingVehicle = boardingVehicle;
            if (boardingVehicle != vehicle)
            {
                outcome.Evidence = WaypointStationEvidence.BoardingVehicleMismatch;
                return false;
            }

            targetBuilding = m_Runtime.m_Resolve.PassingStation(targetStop);
            outcome.TargetBuilding = targetBuilding;
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
            ref WaypointStationOutcome outcome)
        {
            bool tramStop = targetBuilding == Entity.Null
                && TransportModeResolver.Resolve(m_Runtime.EntityManager, line) == TransitMode.Tram;
            if (targetBuilding == Entity.Null && !tramStop)
            {
                outcome.Evidence = WaypointStationEvidence.TrackStationUnavailable;
                return false;
            }

            Entity tramStartLane = Entity.Null;
            Entity tramEndLane = Entity.Null;
            if (tramStop)
            {
                if (!m_Runtime.EntityManager.HasComponent<RouteLane>(targetWaypoint))
                {
                    outcome.Evidence = WaypointStationEvidence.TrackLaneUnavailable;
                    return false;
                }

                RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(targetWaypoint);
                tramStartLane = routeLane.m_StartLane;
                tramEndLane = routeLane.m_EndLane;
            }

            Entity head = ResolveHead(vehicle);
            if (TryMatchCarriageTrack(head, targetBuilding, tramStop, tramStartLane, tramEndLane, ref outcome))
                return true;
            if (head != vehicle
                && TryMatchCarriageTrack(vehicle, targetBuilding, tramStop, tramStartLane, tramEndLane, ref outcome))
            {
                return true;
            }

            if (!m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                SetTrackFailure(targetBuilding, ref outcome);
                return false;
            }

            DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
            if (layout.Length == 0)
            {
                SetTrackFailure(targetBuilding, ref outcome);
                return false;
            }

            Entity middle = layout[layout.Length / 2].m_Vehicle;
            if (middle != Entity.Null
                && middle != head
                && middle != vehicle
                && TryMatchCarriageTrack(middle, targetBuilding, tramStop, tramStartLane, tramEndLane, ref outcome))
            {
                return true;
            }

            SetTrackFailure(targetBuilding, ref outcome);
            return false;
        }

        private static void SetTrackFailure(
            Entity targetBuilding,
            ref WaypointStationOutcome outcome)
        {
            if (outcome.TrackLane == Entity.Null)
                outcome.Evidence = WaypointStationEvidence.TrackLaneUnavailable;
            else if (targetBuilding != Entity.Null && outcome.TrackBuilding == Entity.Null)
                outcome.Evidence = WaypointStationEvidence.TrackStationUnavailable;
            else
                outcome.Evidence = targetBuilding == Entity.Null
                    ? WaypointStationEvidence.TrackLaneMismatch
                    : WaypointStationEvidence.TrackStationMismatch;
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
            ref WaypointStationOutcome outcome)
        {
            if (carriage == Entity.Null || !m_Runtime.EntityManager.Exists(carriage))
                return false;

            if (!m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(carriage))
            {
                return false;
            }
            TrainCurrentLane currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(carriage);
            outcome.Carriage = carriage;
            if (TryMatchLane(currentLane.m_Front.m_Lane, false, targetBuilding, tramStop, tramStartLane, tramEndLane, ref outcome))
                return true;
            return TryMatchLane(currentLane.m_Rear.m_Lane, true, targetBuilding, tramStop, tramStartLane, tramEndLane, ref outcome);
        }

        private bool TryMatchLane(
            Entity lane,
            bool rear,
            Entity targetBuilding,
            bool tramStop,
            Entity tramStartLane,
            Entity tramEndLane,
            ref WaypointStationOutcome outcome)
        {
            outcome.TrackLane = lane;
            outcome.TrackLaneRear = rear;
            outcome.TrackBuilding = Entity.Null;
            outcome.LaneOwner = Entity.Null;
            if (lane == Entity.Null || !m_Runtime.EntityManager.Exists(lane))
                return false;
            if (targetBuilding != Entity.Null)
            {
                Entity trackBuilding = m_Runtime.m_Resolve.PassingStation(lane);
                outcome.TrackBuilding = trackBuilding;
                return trackBuilding != Entity.Null && trackBuilding == targetBuilding;
            }
            return tramStop && (lane == tramStartLane || lane == tramEndLane);
        }

        private bool Boarding(Entity vehicle)
        {
            return m_Runtime.EntityManager.HasComponent<PublicTransport>(vehicle)
                && (m_Runtime.EntityManager.GetComponentData<PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) != 0;
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
