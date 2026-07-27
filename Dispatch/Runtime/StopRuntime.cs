using System;
using System.Collections.Generic;
using RapidTransitMod.Bypass;
using RapidTransitMod.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal enum StopFactKind : byte
    {
        None,
        Opened,
        Restored,
        Recovered,
        BoardingEnded,
        BoardingCloseRequested,
        StopAssistActive,
        Departed,
        Cancelled,
        DwellTimedOut,
        Removed
    }

    internal readonly struct StopFact
    {
        public readonly StopFactKind Kind;
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly int WaypointIndex;
        public readonly int PreviousWaypointIndex;
        public readonly uint Frame;
        public readonly uint OfficialBoardingChanges;
        public readonly uint PendingFrames;
        public readonly uint DwellDeadlineFrame;
        public readonly bool ForcedDeparture;
        public readonly Entity Blocker;
        public readonly string Reason;

        public StopFact(
            StopFactKind kind,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame,
            uint officialBoardingChanges = 0,
            uint pendingFrames = 0,
            int previousWaypointIndex = -1,
            uint dwellDeadlineFrame = 0,
            bool forcedDeparture = false,
            Entity blocker = default,
            string reason = null)
        {
            Kind = kind;
            Vehicle = vehicle;
            Line = line;
            WaypointIndex = waypointIndex;
            PreviousWaypointIndex = previousWaypointIndex;
            Frame = frame;
            OfficialBoardingChanges = officialBoardingChanges;
            PendingFrames = pendingFrames;
            DwellDeadlineFrame = dwellDeadlineFrame;
            ForcedDeparture = forcedDeparture;
            Blocker = blocker;
            Reason = reason;
        }

        public bool Exists => Kind != StopFactKind.None;
    }

    internal readonly struct StopInput
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly uint SourceFrame;
        public readonly VehicleState State;
        public readonly bool InputValid;
        public readonly bool OfficialBoarding;
        public readonly bool CooldownActive;
        public readonly int PreviousWaypoint;
        public readonly int CurrentWaypoint;
        public readonly int RecoveryWaypoint;
        public readonly int WaypointCount;
        public readonly int LastStopWaypoint;
        public readonly bool MovingKnown;
        public readonly bool MovingForDeparture;
        public readonly bool SuppressBoardingGhost;

        public StopInput(
            Entity vehicle,
            Entity line,
            uint sourceFrame,
            VehicleState state,
            bool inputValid,
            bool officialBoarding,
            bool cooldownActive,
            int previousWaypoint,
            int currentWaypoint,
            int recoveryWaypoint,
            int waypointCount,
            int lastStopWaypoint,
            bool movingKnown,
            bool movingForDeparture,
            bool suppressBoardingGhost)
        {
            Vehicle = vehicle;
            Line = line;
            SourceFrame = sourceFrame;
            State = state;
            InputValid = inputValid;
            OfficialBoarding = officialBoarding;
            CooldownActive = cooldownActive;
            PreviousWaypoint = previousWaypoint;
            CurrentWaypoint = currentWaypoint;
            RecoveryWaypoint = recoveryWaypoint;
            WaypointCount = waypointCount;
            LastStopWaypoint = lastStopWaypoint;
            MovingKnown = movingKnown;
            MovingForDeparture = movingForDeparture;
            SuppressBoardingGhost = suppressBoardingGhost;
        }
    }

    internal enum StopInboundAction : byte
    {
        None,
        Mark,
        Clear
    }

    internal readonly struct StopControlResult
    {
        public readonly Entity Vehicle;
        public readonly int WaypointIndex;
        public readonly bool ClearBypassHoldSkipped;
        public readonly bool ClearForcedMidStop;
        public readonly bool ClearProgressSuspect;
        public readonly bool NoteProgressSuspect;
        public readonly bool WriteCachedWaypoint;
        public readonly int CachedWaypointIndex;
        public readonly StopInboundAction InboundAction;

        public StopControlResult(
            Entity vehicle,
            int waypointIndex,
            bool clearBypassHoldSkipped,
            bool clearForcedMidStop = false,
            bool clearProgressSuspect = false,
            bool noteProgressSuspect = false,
            bool writeCachedWaypoint = false,
            int cachedWaypointIndex = -1,
            StopInboundAction inboundAction = StopInboundAction.None)
        {
            Vehicle = vehicle;
            WaypointIndex = waypointIndex;
            ClearBypassHoldSkipped = clearBypassHoldSkipped;
            ClearForcedMidStop = clearForcedMidStop;
            ClearProgressSuspect = clearProgressSuspect;
            NoteProgressSuspect = noteProgressSuspect;
            WriteCachedWaypoint = writeCachedWaypoint;
            CachedWaypointIndex = cachedWaypointIndex;
            InboundAction = inboundAction;
        }

        public bool Exists => Vehicle != Entity.Null;
    }

    internal readonly struct StopDeparture
    {
        public readonly StopFact Fact;
        public readonly StopControlResult Control;

        public StopDeparture(StopFact fact, StopControlResult control)
        {
            Fact = fact;
            Control = control;
        }
    }

    internal readonly struct StopCancelResult
    {
        public readonly StopFact Fact;
        public readonly StopControlResult Control;

        public StopCancelResult(StopFact fact, StopControlResult control)
        {
            Fact = fact;
            Control = control;
        }

        public bool Exists => Fact.Exists;
    }

    internal readonly struct StopFrameState
    {
        public readonly bool Boarding;
        public readonly bool HadStopSession;
        public readonly bool BoardingChanged;
        public readonly bool HasForcedMidStopGrace;

        public StopFrameState(bool boarding, bool hadStopSession, bool boardingChanged, bool hasForcedMidStopGrace)
        {
            Boarding = boarding;
            HadStopSession = hadStopSession;
            BoardingChanged = boardingChanged;
            HasForcedMidStopGrace = hasForcedMidStopGrace;
        }
    }

    internal sealed class StopRuntime : IDisposable
    {
        private readonly StopRuntimeState m_State;
        private readonly RuntimeFramePlan m_FramePlan;
        private readonly Action<Entity, bool> m_SetDeparturePending;
        private readonly List<StopFact> m_Facts = new List<StopFact>();
        private readonly List<StopControlResult> m_Controls = new List<StopControlResult>();
        private readonly List<Entity> m_DepartureCandidates = new List<Entity>();
        private readonly HashSet<Entity> m_DepartureCandidateSet = new HashSet<Entity>();
        private readonly List<StopDeparture> m_ResolvedDepartures = new List<StopDeparture>();
        private readonly Dictionary<Entity, StopFrameState> m_FrameStates = new Dictionary<Entity, StopFrameState>();
        private readonly Dictionary<Entity, StopInput> m_InputByVehicle = new Dictionary<Entity, StopInput>();

        internal StopRuntime(
            StopRuntimeState state,
            RuntimeFramePlan framePlan,
            Action<Entity, bool> setDeparturePending)
        {
            m_State = state;
            m_FramePlan = framePlan;
            m_SetDeparturePending = setDeparturePending;
        }

        internal IReadOnlyList<StopFact> Facts => m_Facts;
        internal IReadOnlyList<StopControlResult> Controls => m_Controls;
        internal IReadOnlyList<StopDeparture> ResolvedDepartures => m_ResolvedDepartures;
        internal IReadOnlyDictionary<Entity, StopFrameState> FrameStates => m_FrameStates;

        internal void Process(IReadOnlyList<StopInput> inputs, uint nowFrame)
        {
            m_Facts.Clear();
            m_Controls.Clear();
            m_DepartureCandidates.Clear();
            m_DepartureCandidateSet.Clear();
            m_ResolvedDepartures.Clear();
            m_FrameStates.Clear();
            m_InputByVehicle.Clear();
            for (int i = 0; i < inputs.Count; i++)
            {
                StopInput input = inputs[i];
                Entity vehicle = input.Vehicle;
                if (vehicle == Entity.Null || !input.InputValid)
                    continue;

                m_InputByVehicle[vehicle] = input;
                ObserveOfficialBoarding(vehicle, input.OfficialBoarding);
                if (input.State == VehicleState.Retiring)
                {
                    m_FrameStates[vehicle] = new StopFrameState(
                        input.OfficialBoarding,
                        HasOpenStopSession(vehicle),
                        false,
                        IsForcedMidStopGraceActive(vehicle, nowFrame));
                    continue;
                }

                bool boarding = input.OfficialBoarding;
                if (input.State == VehicleState.Running && boarding && input.SuppressBoardingGhost)
                {
                    boarding = false;
                }

                bool lastEffectiveBoarding = ReadEffectiveBoarding(vehicle);
                bool boardingChanged = !input.CooldownActive && boarding != lastEffectiveBoarding;
                int previousWaypoint = input.PreviousWaypoint;
                int waypoint = previousWaypoint;
                bool lastBoarding = HasOpenStopSession(vehicle);
                SetEffectiveBoarding(vehicle, boarding);
                if (boardingChanged && input.State != VehicleState.Idle)
                {
                    if (boarding)
                    {
                        if (HasOpenStopSession(vehicle))
                        {
                            CancelDeparturePending(vehicle);
                            waypoint = TryGetSessionWaypoint(vehicle, out int sessionWaypoint)
                                ? sessionWaypoint
                                : previousWaypoint;
                            QueueCachedWaypoint(vehicle, waypoint);
                        }
                        else
                        {
                            waypoint = input.CurrentWaypoint;
                            QueueCachedWaypoint(vehicle, waypoint);
                            if (waypoint >= 0)
                            {
                                QueueControl(OpenStopSession(vehicle, input.Line, waypoint, nowFrame));
                                m_Facts.Add(new StopFact(
                                    StopFactKind.Opened,
                                    vehicle,
                                    input.Line,
                                    waypoint,
                                    nowFrame,
                                    previousWaypointIndex: previousWaypoint,
                                    reason: null));
                                ClearForcedMidStop(vehicle);
                            }
                        }
                    }
                    else if (HasOpenStopSession(vehicle))
                    {
                        waypoint = TryGetSessionWaypoint(vehicle, out int sessionWaypoint)
                            ? sessionWaypoint
                            : previousWaypoint;
                        QueueCachedWaypoint(vehicle, waypoint);
                        AddDepartureCandidate(vehicle);
                        StartDeparturePending(vehicle, nowFrame);
                        m_Facts.Add(new StopFact(
                            StopFactKind.BoardingEnded,
                            vehicle,
                            input.Line,
                            waypoint,
                            nowFrame,
                            previousWaypointIndex: previousWaypoint,
                            reason: null));
                    }
                }

                if (input.State == VehicleState.Running
                    && boarding
                    && waypoint < 0
                    && !lastBoarding
                    && HasInvalidatedRecovery(vehicle)
                    && TryRecoverInvalidatedMidStopSession(
                        vehicle,
                        input.Line,
                        input.RecoveryWaypoint,
                        nowFrame,
                        out StopFact recoveredFact,
                        out StopControlResult recoveredControl))
                {
                    QueueControl(recoveredControl);
                    m_Facts.Add(recoveredFact);
                }

                if (!boarding && IsDeparturePending(vehicle))
                    AddDepartureCandidate(vehicle);

                m_FrameStates[vehicle] = new StopFrameState(
                    boarding,
                    lastBoarding,
                    boardingChanged,
                    IsForcedMidStopGraceActive(vehicle, nowFrame));
            }
        }

        internal void ResolveDeparture(IReadOnlyDictionary<Entity, BypassControlResult> bypassControls, uint nowFrame)
        {
            m_ResolvedDepartures.Clear();
            for (int i = 0; i < m_DepartureCandidates.Count; i++)
            {
                Entity vehicle = m_DepartureCandidates[i];
                if (!m_DepartureCandidateSet.Contains(vehicle)
                    || !m_InputByVehicle.TryGetValue(vehicle, out StopInput input))
                    continue;
                if (bypassControls.TryGetValue(vehicle, out BypassControlResult bypass)
                    && bypass.ShouldHold
                    && !bypass.CanClearAfterExit)
                {
                    continue;
                }

                if (!IsDeparturePending(vehicle))
                    StartDeparturePending(vehicle, nowFrame);

                if (!m_State.StopSessionLine.TryGetValue(vehicle, out Entity line)
                    || line == Entity.Null
                    || line != input.Line
                    || !TryGetSessionWaypoint(vehicle, out int waypoint)
                    || !input.MovingKnown
                    || !input.MovingForDeparture)
                {
                    continue;
                }

                if (CompleteObservedDeparture(
                    vehicle,
                    line,
                    input.State,
                    waypoint,
                    input.WaypointCount,
                    input.LastStopWaypoint,
                    nowFrame,
                    out StopFact fact,
                    out StopControlResult control))
                {
                    m_ResolvedDepartures.Add(new StopDeparture(fact, control));
                }
            }
        }

        internal bool HasOpenStopSession(Entity vehicle) => m_State.StopSessionWaypointIndex.ContainsKey(vehicle);

        internal bool IsDepartureCandidate(Entity vehicle) => m_DepartureCandidateSet.Contains(vehicle);

        internal void RejectDepartureCandidate(Entity vehicle)
        {
            m_DepartureCandidateSet.Remove(vehicle);
        }

        internal void ResetCity() => m_State.ResetCity();

        internal bool TryGetSessionWaypoint(Entity vehicle, out int waypoint)
            => m_State.StopSessionWaypointIndex.TryGetValue(vehicle, out waypoint);

        internal bool IsDeparturePending(Entity vehicle) => m_State.DeparturePendingSinceFrame.ContainsKey(vehicle);

        internal bool HasInvalidatedRecovery(Entity vehicle) => m_State.InvalidatedMidStopRecoveryPending.Contains(vehicle);

        internal bool ReadEffectiveBoarding(Entity vehicle)
            => m_State.LastEffectiveBoarding.TryGetValue(vehicle, out byte boarding) && boarding != 0;

        internal void SetEffectiveBoarding(Entity vehicle, bool boarding)
            => m_State.LastEffectiveBoarding[vehicle] = BoardingByte(boarding);

        internal void SetForcedMidStopGrace(Entity vehicle, uint graceUntil)
        {
            if (vehicle == Entity.Null)
                return;

            m_State.ForcedMidStopBoardingGraceUntil[vehicle] = graceUntil;
            m_FramePlan.SetDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace, graceUntil);
        }

        internal bool IsForcedMidStopGraceActive(Entity vehicle, uint nowFrame)
        {
            return m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil)
                && nowFrame < graceUntil;
        }

        internal void ClearExpiredForcedMidStopGrace(uint nowFrame)
        {
            NativeArray<Entity> vehicles = m_State.ForcedMidStopBoardingGraceUntil.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out uint graceUntil)
                        && nowFrame >= graceUntil)
                    {
                        ClearForcedMidStop(vehicle);
                    }
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        internal bool TryGetForcedMidStopGrace(Entity vehicle, out uint graceUntil)
            => m_State.ForcedMidStopBoardingGraceUntil.TryGetValue(vehicle, out graceUntil);

        internal void ClearForcedMidStop(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_State.ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_FramePlan.ClearDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace);
        }

        internal StopControlResult OpenStopSession(Entity vehicle, Entity line, int waypoint, uint nowFrame)
        {
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_State.DwellTimedOutLatched.Remove(vehicle);
            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = waypoint;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            CancelDeparturePending(vehicle);
            return new StopControlResult(vehicle, waypoint, clearBypassHoldSkipped: true);
        }

        internal StopControlResult ClearStopSession(Entity vehicle)
        {
            int waypoint = TryGetSessionWaypoint(vehicle, out int sessionWaypoint) ? sessionWaypoint : -1;
            ClearSession(vehicle);
            return new StopControlResult(vehicle, waypoint, clearBypassHoldSkipped: true);
        }

        internal void StartDeparturePending(Entity vehicle, uint nowFrame)
        {
            if (!m_State.DeparturePendingSinceFrame.ContainsKey(vehicle))
                m_State.DeparturePendingSinceFrame[vehicle] = nowFrame;
            m_SetDeparturePending(vehicle, true);
        }

        internal void CancelDeparturePending(Entity vehicle)
        {
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_SetDeparturePending(vehicle, false);
        }

        internal bool TryRecoverInvalidatedMidStopSession(
            Entity vehicle,
            Entity line,
            int recoveryWaypoint,
            uint nowFrame,
            out StopFact fact,
            out StopControlResult control)
        {
            fact = default;
            control = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || recoveryWaypoint <= 0
                || HasOpenStopSession(vehicle)
                || !m_State.InvalidatedMidStopRecoveryPending.Contains(vehicle))
            {
                return false;
            }

            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_State.DwellTimedOutLatched.Remove(vehicle);
            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = recoveryWaypoint;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            CancelDeparturePending(vehicle);
            fact = new StopFact(
                StopFactKind.Recovered,
                vehicle,
                line,
                recoveryWaypoint,
                nowFrame);
            control = new StopControlResult(
                vehicle,
                recoveryWaypoint,
                clearBypassHoldSkipped: false,
                clearForcedMidStop: true,
                noteProgressSuspect: true,
                writeCachedWaypoint: true,
                cachedWaypointIndex: recoveryWaypoint);
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

        internal bool CompleteObservedDeparture(
            Entity vehicle,
            Entity line,
            VehicleState state,
            int waypoint,
            int waypointCount,
            int lastStopWaypoint,
            uint nowFrame,
            out StopFact fact,
            out StopControlResult control)
        {
            fact = default;
            control = default;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypoint < 0
                || waypoint >= waypointCount)
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
            if (state == VehicleState.Running && lastStopWaypoint >= 0)
            {
                if (waypoint == lastStopWaypoint)
                    inbound = StopInboundAction.Mark;
                else if (waypoint > 0)
                    inbound = StopInboundAction.Clear;
            }

            fact = new StopFact(
                StopFactKind.Departed,
                vehicle,
                line,
                waypoint,
                nowFrame,
                officialBoardingChanges,
                pendingFrames);
            control = new StopControlResult(
                vehicle,
                waypoint,
                clearBypassHoldSkipped: true,
                clearForcedMidStop: true,
                clearProgressSuspect: true,
                writeCachedWaypoint: true,
                cachedWaypointIndex: -1,
                inboundAction: inbound);
            return true;
        }

        internal void FinalizeDeparture(Entity vehicle) => ClearSession(vehicle);

        internal StopFact RestoreRegistration(
            Entity vehicle,
            Entity line,
            bool boarding,
            int waypoint,
            uint nowFrame)
        {
            byte boardingByte = BoardingByte(boarding);
            m_State.LastEffectiveBoarding[vehicle] = boardingByte;
            m_State.LastOfficialBoarding[vehicle] = boardingByte;
            m_State.DwellTimedOutLatched.Remove(vehicle);
            if (!boarding || waypoint < 0)
                return default;

            m_State.StopSessionLine[vehicle] = line;
            m_State.StopSessionWaypointIndex[vehicle] = waypoint;
            m_State.StopSessionArrivalFrame[vehicle] = nowFrame;
            m_State.StopSessionBoardingChangeCount[vehicle] = 0;
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_State.InvalidatedMidStopRecoveryPending.Remove(vehicle);
            return new StopFact(StopFactKind.Restored, vehicle, line, waypoint, nowFrame);
        }

        internal StopCancelResult CancelStopSession(Entity vehicle, uint nowFrame)
        {
            if (!HasOpenStopSession(vehicle))
                return default;

            Entity line = m_State.StopSessionLine.TryGetValue(vehicle, out Entity sessionLine)
                ? sessionLine
                : Entity.Null;
            int waypoint = TryGetSessionWaypoint(vehicle, out int sessionWaypoint)
                ? sessionWaypoint
                : -1;
            ClearSession(vehicle);
            return new StopCancelResult(
                new StopFact(StopFactKind.Cancelled, vehicle, line, waypoint, nowFrame),
                new StopControlResult(vehicle, waypoint, clearBypassHoldSkipped: false));
        }

        internal void ClearBoardingObservation(Entity vehicle)
        {
            m_State.LastEffectiveBoarding.Remove(vehicle);
            m_State.LastOfficialBoarding.Remove(vehicle);
            ClearSession(vehicle);
        }

        internal StopCancelResult CancelRebind(Entity vehicle, uint nowFrame)
        {
            StopCancelResult result = CancelStopSession(vehicle, nowFrame);
            RemoveVehicle(vehicle);
            ClearForcedMidStop(vehicle);
            return result;
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
            if (m_State.StopSessionWaypointIndex.TryGetValue(vehicle, out int stopSessionWaypoint)
                && stopSessionWaypoint > 0
                && !m_State.DeparturePendingSinceFrame.ContainsKey(vehicle))
            {
                m_State.InvalidatedMidStopRecoveryPending.Add(vehicle);
            }

            m_State.StopSessionLine.Remove(vehicle);
            m_State.StopSessionWaypointIndex.Remove(vehicle);
            m_State.StopSessionArrivalFrame.Remove(vehicle);
            m_State.StopSessionBoardingChangeCount.Remove(vehicle);
            m_State.DeparturePendingSinceFrame.Remove(vehicle);
            m_State.DwellTimedOutLatched.Remove(vehicle);
            m_State.ForcedMidStopBoardingGraceUntil.Remove(vehicle);
            m_FramePlan.ClearDeadline(vehicle, DeadlineKind.ForcedMidStopBoardingGrace);
            m_SetDeparturePending(vehicle, false);
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
            m_State.DwellTimedOutLatched.Remove(vehicle);
            m_SetDeparturePending(vehicle, false);
        }

        private void AddDepartureCandidate(Entity vehicle)
        {
            if (vehicle != Entity.Null && m_DepartureCandidateSet.Add(vehicle))
                m_DepartureCandidates.Add(vehicle);
        }

        internal bool TryLatchDwellTimedOut(Entity vehicle)
        {
            return vehicle != Entity.Null && m_State.DwellTimedOutLatched.Add(vehicle);
        }

        private void QueueControl(StopControlResult control)
        {
            if (control.Exists)
                m_Controls.Add(control);
        }

        private void QueueCachedWaypoint(Entity vehicle, int waypoint)
        {
            QueueControl(new StopControlResult(
                vehicle,
                waypoint,
                clearBypassHoldSkipped: false,
                writeCachedWaypoint: true,
                cachedWaypointIndex: waypoint));
        }

        private static byte BoardingByte(bool boarding) => boarding ? (byte)1 : (byte)0;
    }
}
