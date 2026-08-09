using System;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class StopRuntimeState : IDisposable
    {
        internal NativeHashMap<Entity, byte> LastOfficialBoarding;
        internal NativeHashMap<Entity, byte> LastEffectiveBoarding;
        internal NativeHashMap<Entity, Entity> StopSessionLine;
        internal NativeHashMap<Entity, int> StopSessionWaypointIndex;
        internal NativeHashMap<Entity, uint> StopSessionArrivalFrame;
        internal NativeHashMap<Entity, uint> StopSessionBoardingChangeCount;
        internal NativeHashMap<Entity, uint> DeparturePendingSinceFrame;
        internal NativeHashSet<Entity> InvalidatedMidStopRecoveryPending;
        internal NativeHashMap<Entity, uint> DwellDeadlineFrame;
        internal NativeHashSet<Entity> DwellTimeoutPending;
        internal NativeHashSet<Entity> DwellTimedOutLatched;
        internal NativeHashMap<Entity, uint> ForcedMidStopBoardingGraceUntil;

        internal StopRuntimeState()
        {
            LastEffectiveBoarding = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            LastOfficialBoarding = new NativeHashMap<Entity, byte>(1024, Allocator.Persistent);
            StopSessionLine = new NativeHashMap<Entity, Entity>(1024, Allocator.Persistent);
            StopSessionWaypointIndex = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            StopSessionArrivalFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            StopSessionBoardingChangeCount = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            DeparturePendingSinceFrame = new NativeHashMap<Entity, uint>(1024, Allocator.Persistent);
            InvalidatedMidStopRecoveryPending = new NativeHashSet<Entity>(256, Allocator.Persistent);
            DwellDeadlineFrame = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
            DwellTimeoutPending = new NativeHashSet<Entity>(256, Allocator.Persistent);
            DwellTimedOutLatched = new NativeHashSet<Entity>(256, Allocator.Persistent);
        }

        internal void InitForcedGrace()
        {
            ForcedMidStopBoardingGraceUntil = new NativeHashMap<Entity, uint>(256, Allocator.Persistent);
        }

        internal void ResetCity()
        {
            ClearBoardingStates();
            ClearStopSessions();
            ClearInvalidatedRecovery();
            ClearDwellTimeoutLatches();
            ClearForcedMidStopGrace();
        }

        internal void ClearBoardingStates()
        {
            LastEffectiveBoarding.Clear();
            LastOfficialBoarding.Clear();
        }

        internal void ClearStopSessions()
        {
            StopSessionLine.Clear();
            StopSessionWaypointIndex.Clear();
            StopSessionArrivalFrame.Clear();
            StopSessionBoardingChangeCount.Clear();
            DeparturePendingSinceFrame.Clear();
        }

        internal void ClearInvalidatedRecovery()
        {
            InvalidatedMidStopRecoveryPending.Clear();
        }

        internal void ClearDwellTimeoutLatches()
        {
            DwellDeadlineFrame.Clear();
            DwellTimeoutPending.Clear();
            DwellTimedOutLatched.Clear();
        }

        internal void ClearForcedMidStopGrace()
        {
            ForcedMidStopBoardingGraceUntil.Clear();
        }

        public void Dispose()
        {
            DisposeBoardingStates();
            DisposeStopSessions();
            DisposeInvalidatedRecovery();
            DisposeDwellTimeoutLatches();
            DisposeForcedMidStopGrace();
        }

        internal void DisposeBoardingStates()
        {
            if (LastEffectiveBoarding.IsCreated) LastEffectiveBoarding.Dispose();
            if (LastOfficialBoarding.IsCreated) LastOfficialBoarding.Dispose();
        }

        internal void DisposeStopSessions()
        {
            if (StopSessionLine.IsCreated) StopSessionLine.Dispose();
            if (StopSessionWaypointIndex.IsCreated) StopSessionWaypointIndex.Dispose();
            if (StopSessionArrivalFrame.IsCreated) StopSessionArrivalFrame.Dispose();
            if (StopSessionBoardingChangeCount.IsCreated) StopSessionBoardingChangeCount.Dispose();
            if (DeparturePendingSinceFrame.IsCreated) DeparturePendingSinceFrame.Dispose();
        }

        internal void DisposeInvalidatedRecovery()
        {
            if (InvalidatedMidStopRecoveryPending.IsCreated) InvalidatedMidStopRecoveryPending.Dispose();
        }

        internal void DisposeDwellTimeoutLatches()
        {
            if (DwellDeadlineFrame.IsCreated) DwellDeadlineFrame.Dispose();
            if (DwellTimeoutPending.IsCreated) DwellTimeoutPending.Dispose();
            if (DwellTimedOutLatched.IsCreated) DwellTimedOutLatched.Dispose();
        }

        internal void DisposeForcedMidStopGrace()
        {
            if (ForcedMidStopBoardingGraceUntil.IsCreated) ForcedMidStopBoardingGraceUntil.Dispose();
        }
    }
}
