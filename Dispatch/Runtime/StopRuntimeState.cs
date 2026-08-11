using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class TimedStopPlan
    {
        internal Entity Line;
        internal string RowId = string.Empty;
        internal string StopSig = string.Empty;
        internal DateTime ServiceDate;
        internal int SlotMinute;
        internal TimedStop[] Stops = Array.Empty<TimedStop>();
        internal int[] WaypointIndices = Array.Empty<int>();
        internal int NextStopOrder;
        internal int ActiveStopOrder = -1;
        internal uint EarliestReleaseFrame;
        internal long ClockEpoch;
        internal bool HoldApplied;
        internal bool CanBypass;
        internal bool RestorePending;
        internal double RestoredWaitMinutes = -1d;
        internal int SavedTicksPerDay;
    }

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
        internal readonly Dictionary<Entity, TimedStopPlan> TimedPlans =
            new Dictionary<Entity, TimedStopPlan>();
        internal readonly HashSet<Entity> TimedStopPending = new HashSet<Entity>();

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
            ClearTimedPlans();
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

        internal void ClearTimedPlans()
        {
            TimedPlans.Clear();
            TimedStopPending.Clear();
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
