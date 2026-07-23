using System;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum StopFactKind : byte
    {
        None,
        Restored,
        Recovered,
        Departed
    }

    internal readonly struct StopFact
    {
        public readonly StopFactKind Kind;
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly uint Frame;
        public readonly uint OfficialBoardingChanges;
        public readonly uint PendingFrames;

        public StopFact(
            StopFactKind kind,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame,
            uint officialBoardingChanges = 0,
            uint pendingFrames = 0)
        {
            Kind = kind;
            Vehicle = vehicle;
            Line = line;
            WaypointIndex = waypointIndex;
            Frame = frame;
            OfficialBoardingChanges = officialBoardingChanges;
            PendingFrames = pendingFrames;
        }

        public bool Exists => Kind != StopFactKind.None;
    }

    internal enum StopInboundAction : byte
    {
        None,
        Mark,
        Clear
    }

    internal readonly struct StopControlResult
    {
        public readonly bool ClearBypassHoldSkipped;
        public readonly bool ClearForcedMidStop;
        public readonly bool ClearProgressSuspect;
        public readonly bool NoteProgressSuspect;
        public readonly bool WriteCachedWaypoint;
        public readonly int CachedWaypointIndex;
        public readonly StopInboundAction InboundAction;

        public StopControlResult(
            bool clearBypassHoldSkipped,
            bool clearForcedMidStop = false,
            bool clearProgressSuspect = false,
            bool noteProgressSuspect = false,
            bool writeCachedWaypoint = false,
            int cachedWaypointIndex = -1,
            StopInboundAction inboundAction = StopInboundAction.None)
        {
            ClearBypassHoldSkipped = clearBypassHoldSkipped;
            ClearForcedMidStop = clearForcedMidStop;
            ClearProgressSuspect = clearProgressSuspect;
            NoteProgressSuspect = noteProgressSuspect;
            WriteCachedWaypoint = writeCachedWaypoint;
            CachedWaypointIndex = cachedWaypointIndex;
            InboundAction = inboundAction;
        }
    }

    internal sealed class StopRuntime : IDisposable
    {
        private const float DepartureMovingSpeedSq = 0.01f;

        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly StopRuntimeState m_State;

        internal StopRuntime(ModRuntimeHostSystem runtime, StopRuntimeState state)
        {
            m_Runtime = runtime;
            m_State = state;
        }

        internal bool HasOpenStopSession(Entity vehicle)
        {
            return m_State.StopSessionWaypointIndex.ContainsKey(vehicle);
        }

        internal void ResetCity()
        {
            m_State.ResetCity();
        }

        internal bool TryGetSessionWaypoint(Entity vehicle, out int waypointIndex)
        {
            return m_State.StopSessionWaypointIndex.TryGetValue(vehicle, out waypointIndex);
        }

        internal bool IsDeparturePending(Entity vehicle)
        {
            return m_State.DeparturePendingSinceFrame.ContainsKey(vehicle);
        }

        internal bool HasInvalidatedRecovery(Entity vehicle)
        {
            return m_State.InvalidatedMidStopRecoveryPending.Contains(vehicle);
        }

        internal bool ReadEffectiveBoarding(Entity vehicle)
        {
            return m_State.LastEffectiveBoarding.TryGetValue(vehicle, out byte boarding) && boarding != 0;
        }

        internal void SetEffectiveBoarding(Entity vehicle, bool boarding)
        {
            m_State.LastEffectiveBoarding[vehicle] = BoardingByte(boarding);
        }

        internal void SetForcedMidStopGrace(Entity vehicle, uint graceUntil)
        {
            if (vehicle == Entity.Null)
                return;

            m_State.ForcedMidStopBoardingGraceUntil[vehicle] = graceUntil;
            m_Runtime.m_RuntimeWorksets.SetDeadline(
                vehicle,
                DeadlineKind.ForcedMidStopBoardingGrace,
                graceUntil);
        }

        internal bool HasForcedMidStopGrace(Entity vehicle, uint nowFrame)
        {
            return m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil)
                && nowFrame < graceUntil;
        }

        internal bool TryGetForcedMidStopGrace(Entity vehicle, out uint graceUntil)
        {
            return m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out graceUntil);
        }

        internal void ClearForcedMidStop(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_State.ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace);
            m_Runtime.m_RuntimeLog.m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        internal bool IsSuppressedMidStopGhost(
            Entity vehicle,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            out int targetWaypointIndex)
        {
            targetWaypointIndex = -1;
            if (vehicle == Entity.Null
                || !m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil))
            {
                return false;
            }

            if (nowFrame >= graceUntil)
            {
                m_State.ForcedMidStopBoardingGraceUntil.Remove(vehicle);
                m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace);
                return false;
            }

            if (!m_Runtime.EntityManager.HasComponent<Waypoint>(target.m_Target))
                return false;

            targetWaypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(target.m_Target).m_Index;
            if (targetWaypointIndex < 0 || targetWaypointIndex >= waypoints.Length)
                return false;

            Entity targetStop = ConnectedStop(waypoints[targetWaypointIndex].m_Waypoint);
            if (targetStop == Entity.Null
                || !m_Runtime.EntityManager.HasComponent<BoardingVehicle>(targetStop)
                || m_Runtime.EntityManager.GetComponentData<BoardingVehicle>(targetStop).m_Vehicle != vehicle
                || !m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(targetStop)
                || !m_Runtime.EntityManager.HasComponent<Game.Objects.Transform>(vehicle))
            {
                return false;
            }

            float3 vehiclePosition = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(vehicle).m_Position;
            float3 stopPosition = m_Runtime.EntityManager.GetComponentData<Game.Objects.Transform>(targetStop).m_Position;
            return math.distance(vehiclePosition, stopPosition) > ModRuntimeHostSystem.AT_STOP_MAX_DIST;
        }

        internal StopControlResult OpenStopSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = waypointIndex;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            return new StopControlResult(clearBypassHoldSkipped: true);
        }

        internal StopControlResult ClearStopSession(Entity vehicle)
        {
            ClearSession(vehicle);
            return new StopControlResult(clearBypassHoldSkipped: true);
        }

        internal void StartDeparturePending(Entity vehicle, uint nowFrame)
        {
            if (!m_State.DeparturePendingSinceFrame.ContainsKey(vehicle))
                m_State.DeparturePendingSinceFrame[vehicle] = nowFrame;
        }

        internal void CancelDeparturePending(Entity vehicle)
        {
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
        }

        internal bool TryRecoverInvalidatedMidStopSession(
            Entity vehicle,
            Entity line,
            int recoveryWaypointIndex,
            uint nowFrame,
            out StopFact fact,
            out StopControlResult control)
        {
            fact = default;
            control = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || recoveryWaypointIndex <= 0
                || HasOpenStopSession(vehicle)
                || !m_State.InvalidatedMidStopRecoveryPending.Contains(vehicle))
            {
                return false;
            }

            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = recoveryWaypointIndex;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            CancelDeparturePending(vehicle);
            fact = new StopFact(StopFactKind.Recovered, vehicle, line, recoveryWaypointIndex, nowFrame);
            control = new StopControlResult(
                clearBypassHoldSkipped: false,
                clearForcedMidStop: true,
                noteProgressSuspect: true,
                writeCachedWaypoint: true,
                cachedWaypointIndex: recoveryWaypointIndex);
            return true;
        }

        internal void ObserveOfficialBoarding(Entity vehicle, bool officialBoarding)
        {
            byte current = BoardingByte(officialBoarding);
            if (m_State.LastOfficialBoarding.TryGetValue(vehicle, out byte previous)
                && previous != current
                && HasOpenStopSession(vehicle))
            {
                uint changes = m_State.StopSessionBoardingChangeCount.TryGetValue(vehicle, out uint existing)
                    ? existing
                    : 0;
                m_State.StopSessionBoardingChangeCount[vehicle] = changes + 1;
            }

            m_State.LastOfficialBoarding[vehicle] = current;
        }

        internal bool IsVehicleMovingForDeparture(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !m_Runtime.EntityManager.Exists(vehicle)
                || !m_Runtime.EntityManager.HasComponent<Game.Objects.Moving>(vehicle))
            {
                return false;
            }

            float3 velocity = m_Runtime.EntityManager.GetComponentData<Game.Objects.Moving>(vehicle).m_Velocity;
            return math.lengthsq(velocity) > DepartureMovingSpeedSq;
        }

        internal bool CompleteObservedDeparture(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleState state,
            int waypointIndex,
            int waypointCount,
            uint nowFrame,
            out StopFact fact,
            out StopControlResult control)
        {
            fact = default;
            control = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex < 0
                || waypointIndex >= waypointCount
                || waypointIndex >= waypoints.Length)
            {
                return false;
            }

            uint officialBoardingChanges = m_State.StopSessionBoardingChangeCount.TryGetValue(vehicle, out uint changes)
                ? changes
                : 0;
            uint pendingFrames = m_State.DeparturePendingSinceFrame.TryGetValue(vehicle, out uint pendingSince)
                && nowFrame >= pendingSince
                    ? nowFrame - pendingSince
                    : 0;

            StopInboundAction inbound = StopInboundAction.None;
            if (state == VehicleState.Running)
            {
                if (waypointIndex == waypointCount - 1)
                    inbound = StopInboundAction.Mark;
                else if (waypointIndex > 0 && waypointIndex < waypointCount - 1)
                    inbound = StopInboundAction.Clear;
            }

            fact = new StopFact(
                StopFactKind.Departed,
                vehicle,
                line,
                waypointIndex,
                nowFrame,
                officialBoardingChanges,
                pendingFrames);
            control = new StopControlResult(
                clearBypassHoldSkipped: true,
                clearForcedMidStop: false,
                clearProgressSuspect: true,
                writeCachedWaypoint: true,
                cachedWaypointIndex: -1,
                inboundAction: inbound);
            return true;
        }

        internal void FinalizeDeparture(Entity vehicle)
        {
            ClearSession(vehicle);
            ClearForcedMidStop(vehicle);
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.BvMisfire);
            m_Runtime.m_Observation.ClearDwellDeadlineCache(vehicle);
            m_Runtime.m_ObsPersist.ClearDwell(vehicle);
        }

        internal StopFact RestoreRegistration(
            Entity vehicle,
            Entity line,
            bool boarding,
            int waypointIndex,
            uint nowFrame)
        {
            byte boardingByte = BoardingByte(boarding);
            m_State.LastEffectiveBoarding[vehicle] = boardingByte;
            m_State.LastOfficialBoarding[vehicle] = boardingByte;
            if (!boarding || waypointIndex < 0)
                return default;

            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = waypointIndex;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            return new StopFact(StopFactKind.Restored, vehicle, line, waypointIndex, nowFrame);
        }

        internal void ClearBoardingObservation(Entity vehicle)
        {
            m_State.LastEffectiveBoarding.Remove(vehicle);
            m_State.LastOfficialBoarding.Remove(vehicle);
            ClearSession(vehicle);
        }

        internal bool CancelRebind(Entity vehicle)
        {
            bool hadStop = HasOpenStopSession(vehicle);
            RemoveVehicle(vehicle);
            ClearForcedMidStop(vehicle);
            return hadStop;
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            m_State.LastEffectiveBoarding.Remove(vehicle);
            m_State.LastOfficialBoarding.Remove(vehicle);
            ClearSession(vehicle);
        }

        internal void InvalidateVehiclePosition(Entity vehicle)
        {
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            if (m_State.StopSessionWaypointIndex.TryGetValue(vehicle, out int stopSessionWaypointIndex)
                && stopSessionWaypointIndex > 0
                && !m_State.DeparturePendingSinceFrame.ContainsKey(vehicle))
            {
                m_State.InvalidatedMidStopRecoveryPending.Add(vehicle);
            }

            m_State.StopSessionLine.Remove(vehicle);
            m_State.StopSessionWaypointIndex.Remove(vehicle);
            m_State.StopSessionArrivalFrame.Remove(vehicle);
            m_State.StopSessionBoardingChangeCount.Remove(vehicle);
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_State.ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_Runtime.m_RuntimeWorksets.ClearDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace);
        }

        public void Dispose()
        {
        }

        private void ClearSession(Entity vehicle)
        {
            m_State.StopSessionLine.Remove(vehicle);
            m_State.StopSessionWaypointIndex.Remove(vehicle);
            m_State.StopSessionArrivalFrame.Remove(vehicle);
            m_State.StopSessionBoardingChangeCount.Remove(vehicle);
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
        }

        private static byte BoardingByte(bool boarding)
        {
            return boarding ? (byte)1 : (byte)0;
        }

        private Entity ConnectedStop(Entity waypoint)
        {
            if (waypoint == Entity.Null
                || !m_Runtime.EntityManager.Exists(waypoint)
                || !m_Runtime.EntityManager.HasComponent<Connected>(waypoint))
            {
                return Entity.Null;
            }

            Entity connected = m_Runtime.EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
            return connected != Entity.Null && m_Runtime.EntityManager.Exists(connected)
                ? connected
                : Entity.Null;
        }
    }
}
