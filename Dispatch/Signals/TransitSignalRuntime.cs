using System;
using System.Collections.Generic;
using Game.Net;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.TrackProjection;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Signals
{
    internal sealed class TransitSignalRuntime
    {
        private const float LookAheadMeters = 320f;
        private const sbyte BusPriority = 105;
        private const sbyte TramPriority = 106;
        private const uint TramMaxPriorityFrames = 1800u;
        private const uint BusMaxPriorityFrames = 3600u;
        private const uint GroupCooldownFrames = 900u;
        private const uint BusProgressGuardFrames = 320u;
        private const float BusProgressGuardMeters = 20f;
        internal const int MaxTargetsPerVehicle = 3;
        private const byte NavigationUnknown = 0;
        private const byte NavigationAbsent = 1;
        private const byte NavigationForward = 2;

        private static bool IsTrafficLightFrame(
            uint frame,
            uint updateFrame)
        {
            return (frame & 3u) == 1u
                && ((frame >> 2) & 15u) == updateFrame;
        }

#if RT_BUS_SIGNAL_WAIT_MEASURE
        private const uint BusQueueTraceFrames = 512u;
        private const int BusCycleIntersectionIndex = 136712;
        private const int BusCycleHistorySize = 8;
        private enum BusMeasureDecision : byte { None, Position, Ineligible, Signal, Identity, Cooldown, Lost, Rejected, Submitted }
        private struct BusCycleSample
        {
            internal uint CycleFrame, ObserveFrame, CarriedWriteFrame;
            internal float Distance; internal BusMeasureDecision Decision;
            internal Entity Related, BeforePetitioner; internal sbyte BeforePriority;
            internal bool Open, WaitingPost, Eligible, GuardSuppressed, Wrote, PreOwn, PostOwn, PostAvailable;
            internal SignalTraceSnapshot Pre, Post; internal BusSignalBlocker Blocker;
            internal SignalLaneFact Terminal;
        }
        private sealed class BusCycleState
        {
            internal Entity Vehicle, Line, Lane, Intersection, LastTerminal; internal ushort Group;
            internal uint UpdateFrame, LastWriteFrame, Cycles, Writes, StillPresent, PhaseCovered, Go, GreenQueued, GuardQueued, PhaseMissed, NonRealWrites, TerminalChanges;
            internal byte MaxChainDepth; internal bool Locked, Armed, HasTerminal;
            internal BusCycleSample Sample; internal readonly BusCycleSample[] History = new BusCycleSample[BusCycleHistorySize];
            internal int HistoryHead, HistoryCount;
        }
#endif

#if RT_SIGNAL_WAIT_MEASURE
        private const int TraceSlots = 17;
        private const uint TraceWaitFrames = 640u;
        private const byte TracePosition = 1;
        private const byte TraceLong = 3;

        private enum TraceResult : byte
        {
            None,
            Position,
            Ineligible,
            Signal,
            Identity,
            Cooldown,
            Lost,
            Rejected,
            Submitted,
            Green,
            Ended,
        }

        private struct TraceSample
        {
            internal uint Frame;
            internal uint Value;
            internal Entity Related;
            internal TraceResult Result;
            internal byte Consumption;
            internal SignalTraceSnapshot Pre;
            internal SignalTraceSnapshot Post;
            internal SignalTraceBlocker Blocker;
        }

        private sealed class TraceState
        {
            internal readonly TraceSample[] Samples = new TraceSample[TraceSlots];
            internal Entity Vehicle;
            internal Entity Line;
            internal Entity Lane;
            internal Entity Intersection;
            internal ushort Group;
            internal uint UpdateFrame;
            internal uint NonGoStart;
            internal uint LastWrite;
            internal int Head;
            internal int Count;
            internal int Cycle;
            internal int PostSample;
            internal byte Trigger;
            internal byte PostCycles;
            internal bool Active;
            internal bool Closed;
            internal bool CycleOpen;
            internal bool Waiting;
        }
#endif

        private sealed class VehicleTarget
        {
            internal Entity Vehicle;
            internal Entity SignalLane;
            internal Entity Intersection;
            internal ushort GroupMask;
            internal float DistanceMeters;
            internal uint UpdateFrame;
            internal uint PositionCurrentFrame;
            internal bool NavigationLatched;
            internal int MarkerAtomIndex;
            internal byte NavigationResult;
            internal bool Submitted;
            internal uint SubmittedFrame;
            internal bool WasGreenAtSubmit;
            internal uint FirstGreenFrame;
            internal bool FirstGreenObserved;
            internal bool SelectedCurrentFrame;
            internal uint ProgressAnchorFrame, ProgressSuppressedFrame;
            internal float ProgressAnchorDistance, ProgressSuppressedDistance;
            internal bool ProgressSuppressed;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            internal bool BusMeasureActive, BusMeasureApply;
            internal uint BusMeasureStartFrame, BusMeasureLastFrame;
            internal uint BusMeasureObservedFrames, BusMeasureDirectFrames,
                BusMeasureQueuedFrames;
            internal BusMeasureDecision BusMeasureLastDecision;
            internal Entity BusMeasureDecisionRelated;
            internal uint BusMeasureDecisionFrame, BusMeasureDecisionValue,
                BusMeasureDecisionMask;
            internal bool BusMeasureTraceLogged;
            internal BusSignalBlocker BusMeasureLastQueuedBlocker;
            internal uint BusMeasureTerminalChangeCount;
            internal bool GuardAwaitResubmit, GuardAwaitGreen;
#endif

            internal void Reset()
            {
                Vehicle = Entity.Null;
                SignalLane = Entity.Null;
                Intersection = Entity.Null;
                GroupMask = 0;
                DistanceMeters = float.NaN;
                UpdateFrame = uint.MaxValue;
                PositionCurrentFrame = uint.MaxValue;
                NavigationLatched = false;
                MarkerAtomIndex = -1;
                NavigationResult = NavigationUnknown;
                Submitted = false;
                SubmittedFrame = 0;
                WasGreenAtSubmit = false;
                FirstGreenFrame = 0;
                FirstGreenObserved = false;
                SelectedCurrentFrame = false;
                ProgressAnchorFrame = uint.MaxValue;
                ProgressAnchorDistance = float.NaN;
                ProgressSuppressed = false;
                ProgressSuppressedFrame = 0;
                ProgressSuppressedDistance = float.NaN;
#if RT_BUS_SIGNAL_WAIT_MEASURE
                TransitSignalRuntime.ResetBusMeasure(this);
                GuardAwaitResubmit = false;
                GuardAwaitGreen = false;
#endif
            }
        }

        private readonly struct SignalCandidate
        {
            internal readonly Entity SignalLane;
            internal readonly Entity Intersection;
            internal readonly ushort GroupMask;
            internal readonly float DistanceMeters;
            internal readonly uint UpdateFrame;
            internal readonly int MarkerAtomIndex;

            internal SignalCandidate(
                Entity signalLane,
                Entity intersection,
                ushort groupMask,
                float distanceMeters,
                uint updateFrame,
                int markerAtomIndex = -1)
            {
                SignalLane = signalLane;
                Intersection = intersection;
                GroupMask = groupMask;
                DistanceMeters = distanceMeters;
                UpdateFrame = updateFrame;
                MarkerAtomIndex = markerAtomIndex;
            }
        }

        private sealed class VehicleSignalState
        {
            internal readonly VehicleTarget[] Targets =
                new VehicleTarget[MaxTargetsPerVehicle];
            internal readonly SignalCandidate[] Candidates =
                new SignalCandidate[MaxTargetsPerVehicle];
            internal readonly List<RoadEventSource.RoadNavigationSlice> RoadSlices =
                new List<RoadEventSource.RoadNavigationSlice>(32);
            internal Entity Vehicle;
            internal Entity Line;
            internal TransitMode Mode;
            internal int TargetCount;
            internal uint RefreshFrame = uint.MaxValue;

            internal VehicleSignalState(Entity vehicle, Entity line, TransitMode mode)
            {
                Vehicle = vehicle;
                Line = line;
                Mode = mode;
                for (int i = 0; i < Targets.Length; i++)
                {
                    Targets[i] = new VehicleTarget();
                    Targets[i].Reset();
                }
            }
        }

        private sealed class IntersectionState
        {
            internal ushort PriorityGroup;
            internal uint PriorityStartFrame;
            internal uint MaxPriorityFrames;
            internal ushort SelectedGroup;
            internal uint SelectedMaxPriorityFrames;
            internal readonly List<GroupCooldown> Cooldowns = new List<GroupCooldown>(4);
            internal ushort GreenGroups;
            internal int CandidateCount;
            internal ushort CandidateGroups;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            internal ushort GuardWatchGroup;
#endif
        }

#if RT_BUS_SIGNAL_WAIT_MEASURE
        private sealed class GuardAudit
        {
            internal uint RoadRetryQueued, RoadRetryResolved, RoadRetryCancelled;
            internal uint CandidateTruncatedFrames, CandidateTruncatedTargets;
            internal uint RefreshDedupHits, DropFallbackHits;
            internal bool DropFallbackLogged;
        }
#endif
        private readonly struct GroupCooldown
        {
            internal readonly ushort GroupMask;
            internal readonly uint UntilFrame;

            internal GroupCooldown(ushort groupMask, uint untilFrame)
            {
                GroupMask = groupMask;
                UntilFrame = untilFrame;
            }
        }

        private readonly TransitSignalPort m_Port;
        private readonly SignalLanePort m_Signals;
        private readonly Dictionary<Entity, VehicleSignalState> m_States =
            new Dictionary<Entity, VehicleSignalState>(256);
        private readonly Dictionary<Entity, IntersectionState> m_Intersections =
            new Dictionary<Entity, IntersectionState>(64);
        private readonly Dictionary<Entity, VehicleTarget> m_Winners =
            new Dictionary<Entity, VehicleTarget>(64);
        private readonly List<Entity> m_Entities = new List<Entity>(512);
        private readonly List<Entity> m_DueVehicles = new List<Entity>(128);
        private readonly HashSet<Entity> m_RoadRetries = new HashSet<Entity>(64);
#if RT_BUS_SIGNAL_WAIT_MEASURE
        private GuardAudit m_GuardAudit = new GuardAudit();
        private readonly BusCycleState m_BusCycle = new BusCycleState();
#endif
#if RT_SIGNAL_WAIT_MEASURE
        private readonly TraceState m_Trace = new TraceState();
#endif
        private uint m_CurrentFrame;

        internal TransitSignalRuntime(TransitSignalPort port)
        {
            m_Port = port;
            m_Signals = port.Signals;
        }

        internal void Tick(uint frame)
        {
            m_CurrentFrame = frame;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            PostBusCycle(frame);
            OpenBusCycle(frame);
#endif
#if RT_SIGNAL_WAIT_MEASURE
            TracePost(frame);
            TraceOpen(frame);
#endif
            m_Winners.Clear();
            ConsumeFacts(frame);
            RefreshSourceVehicles(frame);
            RetryRoadVehicles(frame);
            ObserveGreen(frame);
            ArbitrateDue(frame);
#if RT_BUS_SIGNAL_WAIT_MEASURE
            FinishBusCycle(frame);
#endif
#if RT_SIGNAL_WAIT_MEASURE
            TraceFinish();
#endif
        }

        internal void ForgetVehicle(Entity vehicle, string reason)
        {
            if (vehicle == Entity.Null)
                return;
#if RT_SIGNAL_WAIT_MEASURE
            if (m_Trace.Active
                && m_Trace.Vehicle == vehicle
                && m_Trace.CycleOpen)
            {
                ref TraceSample sample = ref m_Trace.Samples[m_Trace.Cycle];
                if (sample.Result == TraceResult.None)
                {
                    sample.Result = TraceResult.Ineligible;
                }
            }
#endif
            if (m_States.TryGetValue(vehicle, out VehicleSignalState state))
            {
                EndAllTargets(state, reason, m_CurrentFrame);
                m_States.Remove(vehicle);
            }
            CancelRoadRetry(vehicle);
        }

        internal void Clear(string reason)
        {
#if RT_SIGNAL_WAIT_MEASURE
            if (m_Trace.Active && m_Trace.Trigger != 0)
            {
                FlushTrace(4);
            }
            else if (m_Trace.Active)
            {
                ResetTrace();
            }
#endif
            m_Entities.Clear();
            foreach (Entity vehicle in m_States.Keys)
                m_Entities.Add(vehicle);
            for (int i = 0; i < m_Entities.Count; i++)
                ForgetVehicle(m_Entities[i], reason);
            m_States.Clear();
            m_Intersections.Clear();
            m_Winners.Clear();
            m_DueVehicles.Clear();
            m_RoadRetries.Clear();
#if RT_BUS_SIGNAL_WAIT_MEASURE
            m_GuardAudit = new GuardAudit();
            ResetBusCycle();
#endif
            m_Entities.Clear();
        }

        private void ObserveGreen(uint frame)
        {
            m_DueVehicles.Clear();
            foreach (IntersectionState state in m_Intersections.Values)
            {
                state.GreenGroups = 0;
                state.CandidateCount = 0;
                state.CandidateGroups = 0;
            }

            m_Entities.Clear();
            foreach (Entity vehicle in m_States.Keys)
                m_Entities.Add(vehicle);
            for (int vehicleIndex = 0;
                vehicleIndex < m_Entities.Count;
                vehicleIndex++)
            {
                Entity vehicle = m_Entities[vehicleIndex];
                if (!m_States.TryGetValue(
                        vehicle,
                        out VehicleSignalState vehicleState))
                {
                    continue;
                }

                for (int i = 0; i < vehicleState.TargetCount; i++)
                {
                    VehicleTarget target = vehicleState.Targets[i];
                    if (!target.Submitted
                        || !m_Signals.TryRead(
                            target.SignalLane,
                            out SignalLaneFact signal)
                        || signal.Intersection != target.Intersection
                        || signal.GroupMask != target.GroupMask
                        || signal.Signal != LaneSignalType.Go)
                    {
                        continue;
                    }
                    if (!target.FirstGreenObserved)
                    {
                        target.FirstGreenFrame = frame;
                        target.FirstGreenObserved = true;
                    }
#if RT_BUS_SIGNAL_WAIT_MEASURE
                    if (target.GuardAwaitGreen)
                    {
                        target.GuardAwaitGreen = false;
                        LogProgressGuard(vehicleState, target,
                            "green-after-resubmit",
                            " group=" + target.GroupMask
                            + " frame=" + frame);
                    }
#endif
                }

                if (HasDueTarget(vehicleState, frame))
                {
                    DiscoverVehicle(vehicle, frame, false);
                    if (!m_States.TryGetValue(
                            vehicle,
                            out vehicleState))
                    {
                        continue;
                    }
                }

                string reason = IneligibleReason(vehicleState);
                if (reason != null)
                {
                    ForgetVehicle(vehicle, reason);
                    continue;
                }

                bool canApply = CanApplyPriority(vehicleState);
                for (int i = 0; i < vehicleState.TargetCount; i++)
                {
                    VehicleTarget target = vehicleState.Targets[i];
                    if (!canApply
                        || target.PositionCurrentFrame == uint.MaxValue
                        || !math.isfinite(target.DistanceMeters)
                        || !m_Intersections.TryGetValue(
                            target.Intersection,
                            out IntersectionState intersectionState))
                    {
                        continue;
                    }
                    bool shouldReadSignal = intersectionState.PriorityGroup != 0
                        || intersectionState.SelectedGroup != 0;
                    SignalLaneFact signal = default;
                    bool targetGreen = shouldReadSignal
                        && m_Signals.TryRead(
                            target.SignalLane,
                            out signal)
                        && signal.Intersection == target.Intersection
                        && signal.GroupMask == target.GroupMask
                        && signal.Signal == LaneSignalType.Go
                        && signal.CurrentGroupBit != 0
                        && (signal.CurrentGroupBit & target.GroupMask) != 0;
                    bool actualPriorityGreen = targetGreen
                        && intersectionState.PriorityGroup != 0
                        && (signal.CurrentGroupBit
                            & intersectionState.PriorityGroup) != 0;
                    if (ShouldSuppressBusTarget(
                            vehicleState,
                            target,
                            intersectionState,
                            actualPriorityGreen,
                            frame))
                    {
                        continue;
                    }
                    intersectionState.CandidateCount++;
                    intersectionState.CandidateGroups |= target.GroupMask;
                    if (!shouldReadSignal || !targetGreen)
                    {
                        continue;
                    }
                    intersectionState.GreenGroups |= signal.CurrentGroupBit;
                }

                if (HasDueTarget(vehicleState, frame))
                    m_DueVehicles.Add(vehicle);
            }

            m_Entities.Clear();
            foreach (KeyValuePair<Entity, IntersectionState> pair in m_Intersections)
            {
                Entity intersection = pair.Key;
                IntersectionState state = pair.Value;
                if (state.PriorityGroup != 0
                    && (state.CandidateGroups & state.PriorityGroup) == 0)
                {
                    EndPriorityWindow(
                        intersection,
                        state,
                        "candidate-group-lost",
                        frame);
                }
                if (state.SelectedGroup != 0
                    && (state.CandidateGroups & state.SelectedGroup) == 0)
                {
                    state.SelectedGroup = 0;
                    state.SelectedMaxPriorityFrames = 0;
                }
                if (state.PriorityGroup == 0
                    && state.SelectedGroup != 0)
                {
                    ushort actualGroup = (ushort)(
                        state.GreenGroups & state.SelectedGroup);
                    if (actualGroup != 0)
                    {
                        state.PriorityGroup = actualGroup;
                        state.PriorityStartFrame = frame;
                        state.MaxPriorityFrames =
                            state.SelectedMaxPriorityFrames;
                    }
                }
                else if (state.PriorityGroup != 0
                    && state.MaxPriorityFrames > 0u
                    && frame - state.PriorityStartFrame >= state.MaxPriorityFrames)
                {
                    EndPriorityWindow(
                        intersection,
                        state,
                        "max-window",
                        frame);
                }
                else if (state.PriorityGroup != 0
                    && (state.GreenGroups & state.PriorityGroup) == 0)
                {
                    EndPriorityWindow(
                        intersection,
                        state,
                        "green-lost",
                        frame);
                }

                CleanupCooldowns(state, frame);
                if (state.CandidateCount == 0
                    && state.PriorityGroup == 0
                    && state.SelectedGroup == 0
                    && state.Cooldowns.Count == 0)
                {
                    m_Entities.Add(intersection);
                }
            }
            for (int i = 0; i < m_Entities.Count; i++)
                m_Intersections.Remove(m_Entities[i]);
        }

        private bool ShouldSuppressBusTarget(
            VehicleSignalState state,
            VehicleTarget target,
            IntersectionState intersectionState,
            bool actualPriorityGreen,
            uint frame)
        {
            if (state.Mode != TransitMode.Bus)
                return false;
            if (target.ProgressSuppressed)
                return true;
            if (!actualPriorityGreen
                || target.ProgressAnchorFrame == uint.MaxValue
                || !math.isfinite(target.ProgressAnchorDistance))
            {
                return false;
            }
            ushort actualGroup = intersectionState.PriorityGroup;
            if (target.ProgressAnchorFrame
                <= intersectionState.PriorityStartFrame)
            {
                target.ProgressAnchorFrame = frame;
                target.ProgressAnchorDistance = target.DistanceMeters;
                return false;
            }
            uint elapsedFrames = frame - target.ProgressAnchorFrame;
            float progress = target.ProgressAnchorDistance
                - target.DistanceMeters;
            if (elapsedFrames < BusProgressGuardFrames
                || progress >= BusProgressGuardMeters)
            {
                return false;
            }
            target.ProgressSuppressed = true;
            target.ProgressSuppressedFrame = frame;
            target.ProgressSuppressedDistance = target.DistanceMeters;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            intersectionState.GuardWatchGroup = actualGroup;
            LogProgressGuard(state, target, "guard-suppressed",
                " frame=" + frame
                + " actualGroup=" + actualGroup
                + " priorityStartFrame=" + intersectionState.PriorityStartFrame
                + " anchorFrame=" + target.ProgressAnchorFrame
                + " anchorDistance=" + target.ProgressAnchorDistance
                + " currentDistance=" + target.DistanceMeters
                + " elapsedFrames=" + elapsedFrames
                + " progressMeters=" + progress);
#endif
            return true;
        }

#if RT_BUS_SIGNAL_WAIT_MEASURE
        private void LogProgressGuard(
            VehicleSignalState state,
            VehicleTarget target,
            string stage,
            string details)
        {
            m_Port.Log("[BusSignalProgressGuard] stage=" + stage
                + " vehicle=" + target.Vehicle
                + " line=" + state.Line
                + " lane=" + target.SignalLane
                + " intersection=" + target.Intersection
                + details);
        }
#endif
        private void EndPriorityWindow(
            Entity intersection,
            IntersectionState state,
            string reason,
            uint frame)
        {
            ushort endedGroup = state.PriorityGroup;
            if (state.MaxPriorityFrames > 0u
                && frame - state.PriorityStartFrame
                    >= state.MaxPriorityFrames)
            {
                AddCooldown(state, endedGroup, frame);
            }
#if RT_BUS_SIGNAL_WAIT_MEASURE
            if (state.GuardWatchGroup == endedGroup)
            {
                m_Port.Log("[BusSignalProgressGuard] stage=priority-ended"
                    + " intersection=" + intersection
                    + " group=" + endedGroup
                    + " frame=" + frame
                    + " reason=" + (reason ?? string.Empty));
                state.GuardWatchGroup = 0;
            }
#endif
            state.PriorityGroup = 0;
            state.PriorityStartFrame = 0;
            state.MaxPriorityFrames = 0;
            if ((state.SelectedGroup & endedGroup) != 0)
            {
                state.SelectedGroup = 0;
                state.SelectedMaxPriorityFrames = 0;
            }
        }

        private void ConsumeFacts(uint frame)
        {
            IReadOnlyList<FrameEventRef> events = m_Port.FactsBySequence();
            IReadOnlyList<DeparturePendingEvent> pending = m_Port.PendingFacts();
            IReadOnlyList<DispatchEvent> dispatch = m_Port.DispatchFacts();
            IReadOnlyList<StopEvent> stops = m_Port.StopEvents();
            IReadOnlyList<LifecycleEvent> lifecycle = m_Port.LifecycleFacts();
            for (int i = 0; i < events.Count; i++)
            {
                FrameEventRef frameEvent = events[i];
                if (frameEvent.Kind == FrameEventKind.DeparturePending)
                {
                    DeparturePendingEvent fact = pending[frameEvent.Index];
                    if (fact.Active)
                        DiscoverVehicle(fact.Vehicle, frame, true);
                    else if (HasDepartedStop(stops, fact.Vehicle))
                        DiscoverVehicle(fact.Vehicle, frame, false);
                    else
                        ForgetVehicle(fact.Vehicle, "departure-pending-cancelled");
                    continue;
                }

                if (frameEvent.Kind == FrameEventKind.Stop)
                {
                    StopEvent fact = stops[frameEvent.Index];
                    switch (fact.Fact.Kind)
                    {
                        case StopFactKind.Opened:
                            ForgetVehicle(fact.Vehicle, "stop-opened");
                            break;
                        case StopFactKind.Restored:
                            ForgetVehicle(fact.Vehicle, "stop-restored");
                            break;
                        case StopFactKind.Recovered:
                            ForgetVehicle(fact.Vehicle, "stop-recovered");
                            break;
                    }
                    continue;
                }

                if (frameEvent.Kind == FrameEventKind.Dispatch)
                {
                    DispatchEvent fact = dispatch[frameEvent.Index];
                    if (fact.Kind == DispatchFactKind.State
                        && fact.CurrentState != VehicleState.Running)
                    {
                        ForgetVehicle(fact.Vehicle, "dispatch-state-not-running");
                    }
                    else if (fact.Kind == DispatchFactKind.LaunchConfirmed)
                        DiscoverVehicle(fact.Vehicle, frame, false);
                    continue;
                }

                if (frameEvent.Kind == FrameEventKind.Lifecycle)
                {
                    LifecycleEvent fact = lifecycle[frameEvent.Index];
                    if (fact.Kind == LifecycleFactKind.Removed
                        || fact.Kind == LifecycleFactKind.Rebound)
                    {
                        ForgetVehicle(
                            fact.Vehicle,
                            fact.Kind == LifecycleFactKind.Removed
                                ? "vehicle-removed"
                                : "vehicle-rebound");
                    }
                }
            }
        }

        private static bool HasDepartedStop(
            IReadOnlyList<StopEvent> stops,
            Entity vehicle)
        {
            for (int i = 0; i < stops.Count; i++)
            {
                StopEvent stop = stops[i];
                if (stop.Vehicle == vehicle
                    && stop.Fact.Kind == StopFactKind.Departed)
                    return true;
            }
            return false;
        }

        private void RefreshSourceVehicles(uint frame)
        {
            if ((frame & 15u) == 3u)
            {
                for (int i = 0; i < m_Port.RailSourceCount(); i++)
                {
                    (bool success, ManagedSourceVehicle source) =
                        m_Port.RailSource(i, frame);
                    if (success && source.State == VehicleState.Running)
                        DiscoverVehicle(source.Vehicle, frame, false);
                }
            }

            if ((frame & 15u) == 1u)
            {
                for (int i = 0; i < m_Port.RoadSourceCount(); i++)
                {
                    (bool success, ManagedSourceVehicle source) =
                        m_Port.RoadSource(i, frame);
                    if (success && source.State == VehicleState.Running)
                        DiscoverVehicle(source.Vehicle, frame, false);
                }
            }
        }

        private void RetryRoadVehicles(uint frame)
        {
            m_Entities.Clear();
            foreach (Entity vehicle in m_RoadRetries)
                m_Entities.Add(vehicle);

            for (int i = 0; i < m_Entities.Count; i++)
            {
                Entity vehicle = m_Entities[i];
                if (!m_States.TryGetValue(
                        vehicle,
                        out VehicleSignalState state)
                    || state.Mode != TransitMode.Bus)
                {
                    CancelRoadRetry(vehicle);
                    continue;
                }
                if (state.RefreshFrame != frame)
                    DiscoverVehicle(vehicle, frame, false);
            }
            m_Entities.Clear();
        }

        private void QueueRoadRetry(Entity vehicle)
        {
            if (!m_RoadRetries.Add(vehicle))
                return;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            m_GuardAudit.RoadRetryQueued++;
#endif
        }
        private void ResolveRoadRetry(Entity vehicle)
        {
            if (!m_RoadRetries.Remove(vehicle))
                return;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            m_GuardAudit.RoadRetryResolved++;
#endif
        }
        private void CancelRoadRetry(Entity vehicle)
        {
            if (!m_RoadRetries.Remove(vehicle))
                return;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            m_GuardAudit.RoadRetryCancelled++;
#endif
        }
        private string IneligibleReason(VehicleSignalState state)
        {
            if (m_Port.LinePending(state.Line))
                return "line-structure-pending";
            if (!m_Port.LineEnabled(state.Line))
            {
#if RT_BUS_SIGNAL_WAIT_MEASURE
                if (state.Mode == TransitMode.Bus)
                    return null;
#endif
                return "qualification-ended";
            }
            return null;
        }

        private void DiscoverVehicle(
            Entity vehicle,
            uint frame,
            bool requireTargetAdvance)
        {
            if (!Eligible(
                    vehicle,
                    out Entity line,
                    out TransitMode mode,
                    out _))
            {
                ForgetVehicle(vehicle, "qualification-ended");
                return;
            }

            if (!m_States.TryGetValue(
                    vehicle,
                    out VehicleSignalState state))
            {
                state = new VehicleSignalState(vehicle, line, mode);
                m_States.Add(vehicle, state);
            }
            else if (state.Line != line || state.Mode != mode)
            {
                ForgetVehicle(vehicle, "qualification-ended");
                state = new VehicleSignalState(vehicle, line, mode);
                m_States.Add(vehicle, state);
            }

            RefreshVehicle(state, frame, requireTargetAdvance);
            if (state.TargetCount == 0
                && !m_RoadRetries.Contains(state.Vehicle))
            {
                m_States.Remove(state.Vehicle);
            }
        }

        private bool Eligible(
            Entity vehicle,
            out Entity line,
            out TransitMode mode,
            out int pendingWaypoint,
            bool checkLinePending = true)
        {
            line = Entity.Null;
            mode = TransitMode.Unknown;
            pendingWaypoint = -1;
            if (vehicle == Entity.Null
                || !m_Port.TryVehicle(
                    vehicle,
                    out line,
                    out mode,
                    out VehicleState state)
                || state != VehicleState.Running
                || (mode != TransitMode.Tram && mode != TransitMode.Bus))
            {
                return false;
            }

            if (checkLinePending && m_Port.LinePending(line))
            {
                return false;
            }
            if (!m_Port.LineEnabled(line))
            {
#if RT_BUS_SIGNAL_WAIT_MEASURE
                if (mode != TransitMode.Bus)
#endif
                    return false;
            }
            if (!m_Port.TryStop(
                    vehicle,
                    out bool hasSession,
                    out bool pending,
                    out pendingWaypoint))
            {
                return false;
            }
            return !hasSession || pending;
        }

        private bool CanApplyPriority(VehicleSignalState state)
        {
#if RT_BUS_SIGNAL_WAIT_MEASURE
            if (state != null
                && state.Mode == TransitMode.Bus
                && !m_Port.LineEnabled(state.Line))
            {
                return false;
            }
#endif
            return true;
        }

        private void RefreshVehicle(
            VehicleSignalState state,
            uint frame,
            bool requireTargetAdvance)
        {
            if (state.RefreshFrame == frame)
            {
#if RT_BUS_SIGNAL_WAIT_MEASURE
                m_GuardAudit.RefreshDedupHits++;
#endif
                return;
            }

            state.RefreshFrame = frame;
            if (state.Mode != TransitMode.Bus)
                CancelRoadRetry(state.Vehicle);
            for (int i = 0; i < state.TargetCount; i++)
            {
                state.Targets[i].PositionCurrentFrame = uint.MaxValue;
                state.Targets[i].SelectedCurrentFrame = false;
            }

            if (state.Mode == TransitMode.Bus)
                RefreshRoad(state, frame, requireTargetAdvance);
            else
                RefreshTram(state, frame);
        }

        private void RefreshRoad(
            VehicleSignalState state,
            uint frame,
            bool requireTargetAdvance)
        {
#if RT_BUS_SIGNAL_WAIT_MEASURE
            if ((frame & 15u) == 1u)
                SettleBusMeasureMode(state, frame);
#endif
            RoadEventSource.RoadNavigationResult result =
                m_Port.RoadNavigation(state.Vehicle, state.RoadSlices);
            if (result.Status == RoadEventSource.RoadNavigationStatus.Invalid)
            {
                CancelRoadRetry(state.Vehicle);
                EndAllTargets(state, "navigation-invalid", frame);
                return;
            }
            if (result.Status == RoadEventSource.RoadNavigationStatus.NotGenerated)
            {
                QueueRoadRetry(state.Vehicle);
#if RT_BUS_SIGNAL_WAIT_MEASURE
                if ((frame & 15u) == 1u)
                    PauseBusMeasures(state);
#endif
                return;
            }

            ResolveRoadRetry(state.Vehicle);

            RoadEventSource.RoadNavigationRead read = result.Read;
            if (requireTargetAdvance)
            {
                if (!m_Port.TryStop(
                        state.Vehicle,
                        out _,
                        out bool pending,
                        out int pendingWaypoint)
                    || !pending)
                {
#if RT_BUS_SIGNAL_WAIT_MEASURE
                    if ((frame & 15u) == 1u)
                        PauseBusMeasures(state);
#endif
                    return;
                }
                Entity sessionWaypoint = m_Port.Waypoint(
                    state.Line,
                    pendingWaypoint);
                if (sessionWaypoint == Entity.Null)
                {
                    EndAllTargets(state, "navigation-invalid", frame);
                    return;
                }
                if (sessionWaypoint == read.TargetWaypoint)
                {
                    QueueRoadRetry(state.Vehicle);
                    for (int i = 0; i < state.TargetCount; i++)
                    {
                        state.Targets[i].PositionCurrentFrame =
                            uint.MaxValue;
                        state.Targets[i].DistanceMeters = float.NaN;
                    }
#if RT_BUS_SIGNAL_WAIT_MEASURE
                    if ((frame & 15u) == 1u)
                        PauseBusMeasures(state);
#endif
                    return;
                }
            }

            bool candidateTruncated = read.CandidateTruncated;
            int targetIndex = 0;
            while (targetIndex < state.TargetCount)
            {
                VehicleTarget target = state.Targets[targetIndex];
                if (target.SignalLane == read.CurrentLane)
                {
                    if (m_Signals.TryRead(
                            target.SignalLane,
                            out SignalLaneFact currentSignal)
                        && (currentSignal.Intersection != target.Intersection
                            || currentSignal.GroupMask != target.GroupMask))
                    {
                        EndTarget(state, target, "signal-identity-changed", frame);
                    }
                    else
                    {
                        EndTarget(state, target, "target-lane-entered", frame);
                    }
                    continue;
                }

                int inspectedIndex = FindInspectedLane(
                    state.RoadSlices,
                    target.SignalLane);
                if (inspectedIndex < 0)
                {
                    if (!candidateTruncated)
                        EndTarget(
                            state,
                            target,
                            "navigation-target-ended",
                            frame);
                    else
                    {
                        target.PositionCurrentFrame = uint.MaxValue;
                        target.DistanceMeters = float.NaN;
                        targetIndex++;
                    }
                    continue;
                }

                RoadEventSource.RoadNavigationSlice inspected =
                    state.RoadSlices[inspectedIndex];
                if (!m_Signals.TryRead(
                        target.SignalLane,
                        out SignalLaneFact signal))
                {
                    EndTarget(state, target, "qualification-ended", frame);
                    continue;
                }
                if (signal.Intersection != target.Intersection
                    || signal.GroupMask != target.GroupMask)
                {
                    EndTarget(state, target, "signal-identity-changed", frame);
                    continue;
                }
                MarkPosition(
                    target,
                    signal,
                    inspected.EntryDistanceMeters,
                    frame);
                targetIndex++;
            }

            Entity currentIntersection = m_Signals.TryIntersection(
                    read.CurrentLane,
                    out Entity roadIntersection)
                ? roadIntersection
                : Entity.Null;
            int candidateCount = CollectRoadCandidates(
                state,
                state.RoadSlices,
                read.CurrentLane,
                currentIntersection);
            ReconcileCandidates(
                state,
                candidateCount,
                frame,
                candidateTruncated,
                "navigation-target-ended");
            ObserveRoadProgress(state, frame);
#if RT_BUS_SIGNAL_WAIT_MEASURE
            if ((frame & 15u) == 1u)
                ObserveBusMeasure(state, frame);
#endif
        }

        private void ObserveRoadProgress(
            VehicleSignalState state,
            uint frame)
        {
            for (int i = 0; i < state.TargetCount; i++)
            {
                VehicleTarget target = state.Targets[i];
                if (target.PositionCurrentFrame != frame
                    || !math.isfinite(target.DistanceMeters))
                {
                    continue;
                }
                if (target.ProgressAnchorFrame == uint.MaxValue
                    || !math.isfinite(target.ProgressAnchorDistance))
                {
                    target.ProgressAnchorFrame = frame;
                    target.ProgressAnchorDistance = target.DistanceMeters;
                    continue;
                }
                if (target.ProgressSuppressed)
                {
                    float advanced = target.ProgressSuppressedDistance
                        - target.DistanceMeters;
                    bool directTargetSignal = m_Signals.HasBusTargetSignalBlocker(
                            state.Vehicle,
                            target.SignalLane,
                            target.Intersection,
                            target.GroupMask);
                    if (advanced < BusProgressGuardMeters
                        && !directTargetSignal)
                        continue;
                    target.ProgressSuppressed = false;
                    target.ProgressAnchorFrame = frame;
                    target.ProgressAnchorDistance = target.DistanceMeters;
#if RT_BUS_SIGNAL_WAIT_MEASURE
                    target.GuardAwaitResubmit = true;
                    LogProgressGuard(state, target, "guard-rearmed",
                        " suppressedFrame=" + target.ProgressSuppressedFrame
                        + " suppressedDistance=" + target.ProgressSuppressedDistance
                        + " frame=" + frame
                        + " currentDistance=" + target.DistanceMeters
                        + " advancedMeters=" + advanced
                        + " reason=" + (directTargetSignal
                            ? "direct-target-signal"
                            : "progress-20m"));
#endif
                    continue;
                }
                float progress = target.ProgressAnchorDistance
                    - target.DistanceMeters;
                if (progress >= BusProgressGuardMeters
                    || target.DistanceMeters > target.ProgressAnchorDistance)
                {
                    target.ProgressAnchorFrame = frame;
                    target.ProgressAnchorDistance = target.DistanceMeters;
                }
            }
        }

        private int CollectRoadCandidates(
            VehicleSignalState state,
            List<RoadEventSource.RoadNavigationSlice> slices,
            Entity currentLane,
            Entity currentIntersection)
        {
            int count = 0;
            for (int i = 0;
                i < slices.Count && count < MaxTargetsPerVehicle;
                i++)
            {
                RoadEventSource.RoadNavigationSlice slice = slices[i];
                if (slice.EntryDistanceMeters > LookAheadMeters)
                    break;
                if (slice.Lane == currentLane
                    || !m_Signals.TryRead(
                        slice.Lane,
                        out SignalLaneFact signal)
                    || signal.Intersection == currentIntersection
                    || signal.Intersection == Entity.Null
                    || HasCandidateIntersection(
                        state.Candidates,
                        count,
                        signal.Intersection))
                {
                    continue;
                }
                state.Candidates[count++] = new SignalCandidate(
                    signal.Lane,
                    signal.Intersection,
                    signal.GroupMask,
                    slice.EntryDistanceMeters,
                    signal.UpdateFrame);
            }
            return count;
        }

#if RT_BUS_SIGNAL_WAIT_MEASURE
        private void SettleBusMeasureMode(
            VehicleSignalState state,
            uint frame)
        {
            bool apply = m_Port.LineEnabled(state.Line);
            for (int i = 0; i < state.TargetCount; i++)
            {
                VehicleTarget target = state.Targets[i];
                if (target.BusMeasureActive
                    && target.BusMeasureApply != apply)
                {
                    EndBusMeasure(
                        state,
                        target,
                        "signal-measure-mode-changed",
                        frame);
                }
            }
        }

        private static void PauseBusMeasures(
            VehicleSignalState state)
        {
            for (int i = 0; i < state.TargetCount; i++)
            {
                VehicleTarget target = state.Targets[i];
                if (target.BusMeasureActive)
                    target.BusMeasureLastFrame = uint.MaxValue;
            }
        }

        private void ObserveBusMeasure(
            VehicleSignalState state,
            uint frame)
        {
            for (int i = 1; i < state.TargetCount; i++)
            {
                EndBusMeasure(
                    state,
                    state.Targets[i],
                    "target-not-nearest",
                    frame);
            }
            if (state.Mode != TransitMode.Bus || state.TargetCount == 0)
                return;

            VehicleTarget target = state.Targets[0];
            if (!m_Port.TryVehicle(
                    state.Vehicle,
                    out _,
                    out TransitMode mode,
                    out VehicleState vehicleState)
                || mode != TransitMode.Bus
                || vehicleState != VehicleState.Running)
            {
                EndBusMeasure(
                    state,
                    target,
                    "qualification-ended",
                    frame);
                return;
            }
            if (!m_Port.TryStop(
                    state.Vehicle,
                    out bool hasSession,
                    out _,
                    out _)
                || hasSession)
            {
                EndBusMeasure(
                    state,
                    target,
                    hasSession
                        ? "stop-opened"
                        : "qualification-ended",
                    frame);
                return;
            }
            if (target.PositionCurrentFrame != frame
                || !math.isfinite(target.DistanceMeters)
                || target.DistanceMeters <= 0f
                || target.DistanceMeters > LookAheadMeters)
            {
                if (target.BusMeasureActive)
                    target.BusMeasureLastFrame = uint.MaxValue;
                return;
            }

            bool apply = m_Port.LineEnabled(state.Line);
            if (target.BusMeasureActive
                && target.BusMeasureApply != apply)
            {
                EndBusMeasure(
                    state,
                    target,
                    "signal-measure-mode-changed",
                    frame);
            }
            if (!target.BusMeasureActive)
            {
                BeginBusMeasure(target, apply, frame);
                LockBusCycle(state, target, apply);
                return;
            }

            LockBusCycle(state, target, apply);
            if (target.BusMeasureLastFrame == uint.MaxValue)
            {
                target.BusMeasureLastFrame = frame;
                return;
            }

            uint observedFrames = frame - target.BusMeasureLastFrame;
            target.BusMeasureLastFrame = frame;
            target.BusMeasureObservedFrames += observedFrames;
            BusSignalBlocker blocker = m_Signals.ReadBusBlocker(
                state.Vehicle);
            RememberBusBlocker(target, blocker);
            if (blocker.Kind == BusSignalBlockerKind.DirectSignal)
            {
                target.BusMeasureDirectFrames += observedFrames;
            }
            else if (blocker.Kind == BusSignalBlockerKind.QueuedSignal)
            {
                target.BusMeasureQueuedFrames += observedFrames;
                if (!target.BusMeasureTraceLogged
                    && target.BusMeasureQueuedFrames >= BusQueueTraceFrames)
                {
                    target.BusMeasureTraceLogged = true;
                    LogBusQueueTrace(
                        state,
                        target,
                        "threshold",
                        null,
                        frame,
                        blocker);
                    ArmBusCycle(target);
                }
            }
        }

        private static void BeginBusMeasure(
            VehicleTarget target,
            bool apply,
            uint frame)
        {
            ResetBusMeasure(target);
            target.BusMeasureActive = true;
            target.BusMeasureApply = apply;
            target.BusMeasureStartFrame = frame;
            target.BusMeasureLastFrame = frame;
        }

        private void RecordBusMeasureDecision(
            VehicleTarget target,
            BusMeasureDecision decision,
            uint value = 0,
            Entity related = default)
        {
            if (!target.BusMeasureActive)
                return;
            target.BusMeasureLastDecision = decision;
            target.BusMeasureDecisionFrame = m_CurrentFrame;
            target.BusMeasureDecisionRelated = related;
            target.BusMeasureDecisionValue = value;
            target.BusMeasureDecisionMask |= 1u << (int)decision;
            RecordBusCycleDecision(target, decision, related);
        }

        private static bool IsBusCycleTarget(BusCycleState cycle, VehicleTarget target) =>
            cycle.Locked && target != null
            && target.Vehicle == cycle.Vehicle && target.SignalLane == cycle.Lane
            && target.Intersection == cycle.Intersection && target.GroupMask == cycle.Group;
        private static bool IsOwnBusRequest(SignalTraceSnapshot trace, Entity lane) =>
            trace.TargetPriority == BusPriority && trace.TargetPetitioner == lane;
        private static bool TraceCoversGroup(SignalTraceSnapshot trace, ushort group)
        {
            int current = trace.CurrentSignalGroup, next = trace.NextSignalGroup;
            ushort phase = (ushort)((current > 0 && current <= 16 ? 1 << current - 1 : 0) | (next > 0 && next <= 16 ? 1 << next - 1 : 0));
            return (phase & group) != 0;
        }
        private void LockBusCycle(VehicleSignalState state, VehicleTarget target, bool apply)
        {
            if (m_BusCycle.Locked || !apply || state.Mode != TransitMode.Bus
                || target.Intersection.Index != BusCycleIntersectionIndex || target.Intersection.Version != 1)
                return;
            m_BusCycle.Locked = true; m_BusCycle.Vehicle = target.Vehicle;
            m_BusCycle.Line = state.Line; m_BusCycle.Lane = target.SignalLane;
            m_BusCycle.Intersection = target.Intersection; m_BusCycle.Group = target.GroupMask;
            m_BusCycle.UpdateFrame = target.UpdateFrame;
        }
        private void OpenBusCycle(uint frame)
        {
            if (!m_BusCycle.Locked || m_BusCycle.Sample.Open
                || m_BusCycle.Sample.WaitingPost || !IsTrafficLightFrame(frame, m_BusCycle.UpdateFrame))
                return;
            m_BusCycle.Sample = new BusCycleSample { Open = true, CycleFrame = frame, Distance = float.NaN, CarriedWriteFrame = m_BusCycle.LastWriteFrame };
            ref BusCycleSample sample = ref m_BusCycle.Sample;
            if (!m_Signals.TryReadTrace(m_BusCycle.Lane, out sample.Pre)
                || sample.Pre.Intersection != m_BusCycle.Intersection || sample.Pre.TargetGroupMask != m_BusCycle.Group)
                return;
            sample.PreOwn = IsOwnBusRequest(sample.Pre, m_BusCycle.Lane);
            sample.BeforePriority = sample.Pre.TargetPriority; sample.BeforePetitioner = sample.Pre.TargetPetitioner;
        }
        private void RecordBusCycleDecision(VehicleTarget target, BusMeasureDecision decision, Entity related)
        {
            if (!IsBusCycleTarget(m_BusCycle, target) || !m_BusCycle.Sample.Open)
                return;
            m_BusCycle.Sample.Decision = decision; m_BusCycle.Sample.Related = related;
            m_BusCycle.Sample.Distance = target.DistanceMeters;
        }
        private void RecordBusCycleWrite(VehicleTarget target, SignalLaneFact before, uint frame)
        {
            if (!IsBusCycleTarget(m_BusCycle, target)) return;
            m_BusCycle.LastWriteFrame = frame;
            if (!IsTrafficLightFrame(frame, m_BusCycle.UpdateFrame)) m_BusCycle.NonRealWrites++;
            if (!m_BusCycle.Sample.Open) return;
            m_BusCycle.Sample.Wrote = true; m_BusCycle.Sample.BeforePriority = before.Priority;
            m_BusCycle.Sample.BeforePetitioner = before.Petitioner;
        }
        private void FinishBusCycle(uint frame)
        {
            if (!m_BusCycle.Sample.Open || m_BusCycle.Sample.CycleFrame != frame) return;
            ref BusCycleSample sample = ref m_BusCycle.Sample;
            if (m_States.TryGetValue(m_BusCycle.Vehicle, out VehicleSignalState state))
                for (int i = 0; i < state.TargetCount; i++) {
                    VehicleTarget target = state.Targets[i]; if (!IsBusCycleTarget(m_BusCycle, target)) continue;
                    sample.Eligible = m_Port.LineEnabled(m_BusCycle.Line) && target.PositionCurrentFrame == frame && math.isfinite(target.DistanceMeters) && target.DistanceMeters > 0f && target.DistanceMeters <= LookAheadMeters;
                    sample.GuardSuppressed = target.ProgressSuppressed; sample.Distance = target.DistanceMeters; break;
                }
            sample.Open = false; sample.WaitingPost = true;
        }
        private void PostBusCycle(uint frame)
        {
            if (!m_BusCycle.Sample.WaitingPost || frame != m_BusCycle.Sample.CycleFrame + 1u) return;
            ref BusCycleSample sample = ref m_BusCycle.Sample; sample.ObserveFrame = frame;
            sample.PostAvailable = m_Signals.TryReadTrace(m_BusCycle.Lane, out sample.Post) && sample.Post.Intersection == m_BusCycle.Intersection && sample.Post.TargetGroupMask == m_BusCycle.Group;
            if (!sample.PostAvailable) sample.Post = default;
            sample.PostOwn = IsOwnBusRequest(sample.Post, m_BusCycle.Lane);
            sample.Blocker = m_Signals.ReadBusBlocker(m_BusCycle.Vehicle);
            if (sample.Blocker.Available) m_Signals.TryReadBusTerminalSignal(sample.Blocker.TerminalVehicle, out sample.Terminal);
            CompleteBusCycle();
        }
        private void CompleteBusCycle()
        {
            ref BusCycleSample sample = ref m_BusCycle.Sample;
            sample.Open = false; sample.WaitingPost = false;
            if (m_BusCycle.Armed) { EmitBusCycle(sample); return; }
            if (!sample.PostAvailable) return;
            m_BusCycle.History[m_BusCycle.HistoryHead] = sample;
            m_BusCycle.HistoryHead = (m_BusCycle.HistoryHead + 1) % BusCycleHistorySize;
            if (m_BusCycle.HistoryCount < BusCycleHistorySize) m_BusCycle.HistoryCount++;
        }
        private void ArmBusCycle(VehicleTarget target)
        {
            if (!IsBusCycleTarget(m_BusCycle, target) || m_BusCycle.Armed) return;
            m_BusCycle.Armed = true;
            int first = (m_BusCycle.HistoryHead - m_BusCycle.HistoryCount + BusCycleHistorySize) % BusCycleHistorySize;
            for (int i = 0; i < m_BusCycle.HistoryCount; i++)
                EmitBusCycle(m_BusCycle.History[(first + i) % BusCycleHistorySize]);
            m_BusCycle.HistoryCount = 0;
        }
        private void CloseBusCycle(VehicleTarget target, string reason, uint frame)
        {
            if (!IsBusCycleTarget(m_BusCycle, target)) return;
            if (m_BusCycle.Sample.Open || m_BusCycle.Sample.WaitingPost)
            {
                if (m_BusCycle.Armed)
                {
                    m_BusCycle.Sample.ObserveFrame = frame;
                    m_BusCycle.Sample.Post = default;
                    m_BusCycle.Sample.PostAvailable = false;
                    EmitBusCycle(m_BusCycle.Sample);
                }
                m_BusCycle.Sample.Open = false;
                m_BusCycle.Sample.WaitingPost = false;
            }
            if (m_BusCycle.Armed)
            {
                m_Port.Log("[BusSignalCycleSummary] cycles=" + m_BusCycle.Cycles + " writes=" + m_BusCycle.Writes
                    + " stillPresent=" + m_BusCycle.StillPresent + " phaseCovered=" + m_BusCycle.PhaseCovered + " go=" + m_BusCycle.Go
                    + " greenQueued=" + m_BusCycle.GreenQueued + " guardQueued=" + m_BusCycle.GuardQueued + " phaseMissed=" + m_BusCycle.PhaseMissed
                    + " maxChainDepth=" + m_BusCycle.MaxChainDepth + " terminalChanges=" + m_BusCycle.TerminalChanges + " end=" + reason
                    + " totalDirectFrames=" + target.BusMeasureDirectFrames + " totalQueuedFrames=" + target.BusMeasureQueuedFrames + " nonRealWrites=" + m_BusCycle.NonRealWrites);
            }
            ResetBusCycle();
        }
        private void EmitBusCycle(BusCycleSample sample)
        {
            bool phase = sample.PostAvailable && TraceCoversGroup(sample.Post, m_BusCycle.Group), go = sample.PostAvailable && sample.Post.TargetSignal == (byte)LaneSignalType.Go;
            bool queued = sample.Blocker.Kind == BusSignalBlockerKind.QueuedSignal;
            if (sample.Wrote) m_BusCycle.Writes++; if (sample.PostOwn) m_BusCycle.StillPresent++;
            if (phase) m_BusCycle.PhaseCovered++; else if (sample.PostAvailable) m_BusCycle.PhaseMissed++;
            if (go) m_BusCycle.Go++; if (go && queued) m_BusCycle.GreenQueued++;
            if (sample.GuardSuppressed && queued) m_BusCycle.GuardQueued++;
            if (sample.Blocker.ChainDepth > m_BusCycle.MaxChainDepth) m_BusCycle.MaxChainDepth = sample.Blocker.ChainDepth;
            if (sample.Blocker.TerminalVehicle != Entity.Null)
            {
                if (m_BusCycle.HasTerminal && m_BusCycle.LastTerminal != sample.Blocker.TerminalVehicle) m_BusCycle.TerminalChanges++;
                m_BusCycle.LastTerminal = sample.Blocker.TerminalVehicle; m_BusCycle.HasTerminal = true;
            }
            m_BusCycle.Cycles++;
            string request = !sample.PostAvailable ? "post-unavailable" : sample.PostOwn ? "own-request-still-present" : "own-request-cleared-or-changed";
            m_Port.Log("[BusSignalCycle] cycleFrame=" + sample.CycleFrame + " observeFrame=" + sample.ObserveFrame
                + " vehicle=" + m_BusCycle.Vehicle + " line=" + m_BusCycle.Line + " targetLane=" + m_BusCycle.Lane + " intersection=" + m_BusCycle.Intersection
                + " targetGroup=" + m_BusCycle.Group + " distance=" + sample.Distance + " eligible=" + sample.Eligible + " guardSuppressed=" + sample.GuardSuppressed
                + " decision=" + sample.Decision + " related=" + sample.Related + " wrote=" + sample.Wrote + " beforePriority=" + sample.BeforePriority + " beforePetitioner=" + sample.BeforePetitioner
                + " own-request-carried-in=" + sample.PreOwn + " carriedWriteFrame=" + sample.CarriedWriteFrame + " pre=" + BusCycleTrace(sample.Pre)
                + " post=" + BusCycleTrace(sample.Post) + " request=" + request + " phaseCovered=" + phase + " green-but-queued=" + (go && queued)
                + " priority-off-queue-persists=" + (sample.GuardSuppressed && queued) + " blocker=" + sample.Blocker.Kind + "/" + sample.Blocker.ChainDepth + "/" + sample.Blocker.TerminalVehicle
                + " terminal=" + BusCycleTerminal(sample.Terminal));
        }
        private static string BusCycleTerminal(SignalLaneFact fact) => fact.Lane == Entity.Null ? "-" : fact.Lane + "/" + fact.Intersection + "/" + fact.GroupMask + "/" + fact.Signal + "/" + fact.Priority + "/" + fact.Petitioner + "/" + fact.Blocker;
        private static string BusCycleTrace(SignalTraceSnapshot s) => s.Intersection == Entity.Null ? "-" : s.State + "/" + s.Timer + "/" + s.CurrentSignalGroup + "/" + s.NextSignalGroup + "/" + s.TargetSignal + "/" + s.TargetPriority + "/" + s.TargetDefault + "/" + s.TargetPetitioner + "/" + s.TargetBlocker + "/" + s.HighestPriority + "/" + s.HighestGroupMask + "/" + s.HighestPetitioner;

        private void ResetBusCycle()
        {
            m_BusCycle.Vehicle = Entity.Null;
            m_BusCycle.Line = Entity.Null;
            m_BusCycle.Lane = Entity.Null;
            m_BusCycle.Intersection = Entity.Null;
            m_BusCycle.LastTerminal = Entity.Null;
            m_BusCycle.Group = 0;
            m_BusCycle.UpdateFrame = uint.MaxValue;
            m_BusCycle.LastWriteFrame = 0;
            m_BusCycle.Cycles = 0;
            m_BusCycle.Writes = 0;
            m_BusCycle.StillPresent = 0;
            m_BusCycle.PhaseCovered = 0;
            m_BusCycle.Go = 0;
            m_BusCycle.GreenQueued = 0;
            m_BusCycle.GuardQueued = 0;
            m_BusCycle.PhaseMissed = 0;
            m_BusCycle.NonRealWrites = 0;
            m_BusCycle.TerminalChanges = 0;
            m_BusCycle.MaxChainDepth = 0;
            m_BusCycle.Locked = false;
            m_BusCycle.Armed = false;
            m_BusCycle.HasTerminal = false;
            m_BusCycle.Sample = default;
            Array.Clear(m_BusCycle.History, 0, BusCycleHistorySize);
            m_BusCycle.HistoryHead = 0;
            m_BusCycle.HistoryCount = 0;
        }

        private static void ResetBusMeasure(VehicleTarget target)
        {
            target.BusMeasureActive = false; target.BusMeasureApply = false;
            target.BusMeasureStartFrame = 0; target.BusMeasureLastFrame = 0;
            target.BusMeasureObservedFrames = 0; target.BusMeasureDirectFrames = 0;
            target.BusMeasureQueuedFrames = 0;
            target.BusMeasureLastDecision = BusMeasureDecision.None;
            target.BusMeasureDecisionRelated = Entity.Null;
            target.BusMeasureDecisionFrame = 0; target.BusMeasureDecisionValue = 0;
            target.BusMeasureDecisionMask = 0;
            target.BusMeasureTraceLogged = false;
            target.BusMeasureLastQueuedBlocker = default;
            target.BusMeasureTerminalChangeCount = 0;
        }

        private static void RememberBusBlocker(
            VehicleTarget target,
            BusSignalBlocker blocker)
        {
            if (!blocker.Available)
                return;
            if (blocker.Kind != BusSignalBlockerKind.QueuedSignal)
                return;
            if (target.BusMeasureLastQueuedBlocker.Kind
                    == BusSignalBlockerKind.QueuedSignal
                && (target.BusMeasureLastQueuedBlocker.TerminalVehicle
                        != blocker.TerminalVehicle
                    || target.BusMeasureLastQueuedBlocker.SignalPetitioner
                        != blocker.SignalPetitioner))
            {
                target.BusMeasureTerminalChangeCount++;
            }
            target.BusMeasureLastQueuedBlocker = blocker;
        }

        private void LogBusQueueTrace(
            VehicleSignalState owner,
            VehicleTarget target,
            string stage,
            string reason,
            uint frame,
            BusSignalBlocker blocker)
        {
            bool targetTraceAvailable = m_Signals.TryReadTrace(target.SignalLane, out SignalTraceSnapshot targetTrace);
            bool intersectionStateAvailable = m_Intersections.TryGetValue(target.Intersection, out IntersectionState intersectionState);
            uint cooldownUntil = 0;
            bool cooldownActive = intersectionStateAvailable && TryReadCooldown(intersectionState, target.GroupMask, frame, out cooldownUntil);
            SignalLaneFact terminalSignal = default;
            bool terminalSignalAvailable = blocker.Available && m_Signals.TryReadBusTerminalSignal(blocker.TerminalVehicle, out terminalSignal);
            SignalTraceSnapshot terminalTrace = default;
            bool terminalTraceAvailable = terminalSignalAvailable && m_Signals.TryReadTrace(terminalSignal.Lane, out terminalTrace);
            string sameTerminalLane = !terminalSignalAvailable ? "unknown" : terminalSignal.Lane == target.SignalLane ? "true" : "false";
            string sameTerminalIntersection = !terminalSignalAvailable ? "unknown" : terminalSignal.Intersection == target.Intersection ? "true" : "false";
            string petitionerMatchesTargetLane = !targetTraceAvailable ? "unknown" : targetTrace.TargetPetitioner == target.SignalLane ? "true" : "false";
            m_Port.Log("[BusSignalQueueTrace] vehicle=" + target.Vehicle + " line=" + owner.Line + " frame=" + frame + " stage=" + stage
                + " reason=" + (reason ?? string.Empty) + " targetLane=" + target.SignalLane + " targetIntersection=" + target.Intersection
                + " targetGroup=" + target.GroupMask + " distance=" + target.DistanceMeters + " apply=" + (target.BusMeasureApply ? "true" : "false")
                + " submittedFrame=" + target.SubmittedFrame + " firstGreenObserved=" + target.FirstGreenObserved + " firstGreenFrame=" + target.FirstGreenFrame
                + " lastDecision=" + target.BusMeasureLastDecision + " decisionFrame=" + target.BusMeasureDecisionFrame + " decisionRelated=" + target.BusMeasureDecisionRelated
                + " decisionValue=" + target.BusMeasureDecisionValue + " decisionMask=" + target.BusMeasureDecisionMask + " targetTraceAvailable=" + targetTraceAvailable
                + " traceSignal=" + targetTrace.TargetSignal + " tracePriority=" + targetTrace.TargetPriority + " traceDefault=" + targetTrace.TargetDefault
                + " tracePetitioner=" + targetTrace.TargetPetitioner + " traceBlocker=" + targetTrace.TargetBlocker + " petitionerMatchesTargetLane=" + petitionerMatchesTargetLane
                + " traceCurrentGroup=" + targetTrace.CurrentSignalGroup + " traceNextGroup=" + targetTrace.NextSignalGroup + " traceHighestPriority=" + targetTrace.HighestPriority
                + " traceHighestGroup=" + targetTrace.HighestGroupMask + " traceHighestPetitioner=" + targetTrace.HighestPetitioner + " intersectionStateAvailable=" + intersectionStateAvailable
                + " priorityGroup=" + (intersectionStateAvailable ? intersectionState.PriorityGroup : 0) + " priorityStartFrame=" + (intersectionStateAvailable ? intersectionState.PriorityStartFrame : 0)
                + " maxPriorityFrames=" + (intersectionStateAvailable ? intersectionState.MaxPriorityFrames : 0) + " selectedGroup=" + (intersectionStateAvailable ? intersectionState.SelectedGroup : 0)
                + " cooldownActive=" + cooldownActive + " cooldownUntil=" + cooldownUntil
                + " blockerAvailable=" + blocker.Available + " blockerKind=" + blocker.Kind + " chainDepth=" + blocker.ChainDepth
                + " terminalVehicle=" + blocker.TerminalVehicle + " signalPetitioner=" + blocker.SignalPetitioner + " terminalChangeCount=" + target.BusMeasureTerminalChangeCount
                + " latestQueuedKind=" + target.BusMeasureLastQueuedBlocker.Kind + " latestQueuedTerminal=" + target.BusMeasureLastQueuedBlocker.TerminalVehicle
                + " latestQueuedPetitioner=" + target.BusMeasureLastQueuedBlocker.SignalPetitioner + " latestQueuedDepth=" + target.BusMeasureLastQueuedBlocker.ChainDepth
                + " terminalSignalAvailable=" + terminalSignalAvailable + " terminalLane=" + terminalSignal.Lane + " terminalIntersection=" + terminalSignal.Intersection
                + " terminalGroup=" + terminalSignal.GroupMask + " terminalSignal=" + terminalSignal.Signal + " terminalPriority=" + terminalSignal.Priority
                + " terminalPetitioner=" + terminalSignal.Petitioner + " terminalTraceAvailable=" + terminalTraceAvailable + " terminalBlocker=" + terminalTrace.TargetBlocker
                + " sameTerminalLane=" + sameTerminalLane + " sameTerminalIntersection=" + sameTerminalIntersection + " observedFrames=" + target.BusMeasureObservedFrames
                + " directFrames=" + target.BusMeasureDirectFrames + " queuedFrames=" + target.BusMeasureQueuedFrames);
        }

        private void EndBusMeasure(
            VehicleSignalState state,
            VehicleTarget target,
            string reason,
            uint frame)
        {
            if (!target.BusMeasureActive)
                return;

            CloseBusCycle(target, reason, frame);

            if (target.BusMeasureTraceLogged)
            {
                LogBusQueueTrace(
                    state,
                    target,
                    "end",
                    reason,
                    frame,
                    target.BusMeasureLastQueuedBlocker);
            }

            if (target.BusMeasureObservedFrames > 0u)
            {
                uint blockedFrames = target.BusMeasureDirectFrames
                    + target.BusMeasureQueuedFrames;
                m_Port.Log("[BusSignalMeasure] vehicle="
                    + target.Vehicle
                    + " line=" + state.Line
                    + " lane=" + target.SignalLane
                    + " intersection=" + target.Intersection
                    + " apply=" + (target.BusMeasureApply ? "true" : "false")
                    + " startFrame=" + target.BusMeasureStartFrame
                    + " endFrame=" + frame
                    + " observedFrames=" + target.BusMeasureObservedFrames
                    + " directSignalFrames=" + target.BusMeasureDirectFrames
                    + " queuedSignalFrames=" + target.BusMeasureQueuedFrames
                    + " blockedFrames=" + blockedFrames
                    + " reason=" + (reason ?? string.Empty));
            }

            ResetBusMeasure(target);
        }
#endif

        private void RefreshTram(
            VehicleSignalState state,
            uint frame)
        {
            if (!m_Port.TryTramPosition(
                    state.Vehicle,
                    state.Line,
                    out SignalLineModel model,
                    out TramSignalPosition position,
                    out _))
            {
                return;
            }
            if (model == null
                || position.AtomIndex < 0
                || position.AtomIndex >= model.Atoms.Length
                || position.CurrentLane == Entity.Null)
            {
                return;
            }

            int sectionIndex = ResolveTramSection(
                model,
                position,
                state.Vehicle);
            if (sectionIndex < 0
                || sectionIndex >= model.Sections.Length)
            {
                return;
            }

            Entity lane0 = state.TargetCount > 0
                ? state.Targets[0].SignalLane
                : Entity.Null;
            Entity lane1 = state.TargetCount > 1
                ? state.Targets[1].SignalLane
                : Entity.Null;
            Entity lane2 = state.TargetCount > 2
                ? state.Targets[2].SignalLane
                : Entity.Null;
            VehicleTarget target0 = state.TargetCount > 0
                ? state.Targets[0]
                : null;
            VehicleTarget target1 = state.TargetCount > 1
                ? state.Targets[1]
                : null;
            VehicleTarget target2 = state.TargetCount > 2
                ? state.Targets[2]
                : null;
            byte forwardMask = 0;
            bool navigationKnown = state.TargetCount == 0
                || m_Port.TryTramNavigation(
                    state.Vehicle,
                    lane0,
                    lane1,
                    lane2,
                    out forwardMask);

            ApplyNavigation(target0, navigationKnown, (forwardMask & 1) != 0);
            ApplyNavigation(target1, navigationKnown, (forwardMask & 2) != 0);
            ApplyNavigation(target2, navigationKnown, (forwardMask & 4) != 0);

            int targetIndex = 0;
            while (targetIndex < state.TargetCount)
            {
                VehicleTarget target = state.Targets[targetIndex];
                if (target.MarkerAtomIndex < 0
                    || target.MarkerAtomIndex >= model.Atoms.Length)
                {
                    EndTarget(
                        state,
                        target,
                        "track-marker-index-invalid",
                        frame);
                    continue;
                }
                if (target.MarkerAtomIndex == position.AtomIndex)
                {
                    EndTarget(state, target, "target-atom-entered", frame);
                    continue;
                }
                if (target.NavigationLatched
                    && target.NavigationResult == NavigationAbsent)
                {
                    EndTarget(
                        state,
                        target,
                        "navigation-target-ended",
                        frame);
                    continue;
                }
                if (!m_Signals.TryRead(
                        target.SignalLane,
                        out SignalLaneFact signal))
                {
                    EndTarget(state, target, "qualification-ended", frame);
                    continue;
                }
                if (signal.Intersection != target.Intersection
                    || signal.GroupMask != target.GroupMask)
                {
                    EndTarget(
                        state,
                        target,
                        "signal-identity-changed",
                        frame);
                    continue;
                }

                float distance = LineMileage.Forward(
                    model.TotalDistanceMeters,
                    position.VehicleMeters,
                    model.Atoms[target.MarkerAtomIndex].StartMeters);
                if (!math.isfinite(distance))
                {
                    EndTarget(
                        state,
                        target,
                        "track-distance-nonfinite",
                        frame);
                    continue;
                }
                if (distance <= 0f)
                {
                    EndTarget(
                        state,
                        target,
                        "track-distance-not-forward",
                        frame);
                    continue;
                }
                if (!target.NavigationLatched
                    && distance > LookAheadMeters)
                {
                    EndTarget(
                        state,
                        target,
                        "track-distance-unlatched-over-limit",
                        frame);
                    continue;
                }
                MarkPosition(target, signal, distance, frame);
                targetIndex++;
            }

            Entity currentIntersection = m_Signals.TryIntersection(
                    position.CurrentLane,
                    out Entity tramIntersection)
                ? tramIntersection
                : Entity.Null;
            SignalTrackSection section = model.Sections[sectionIndex];
            int candidateCount = 0;
            int firstMarker = FirstMarkerAfter(
                model,
                section,
                position.AtomIndex);
            for (int i = firstMarker;
                i < section.JunctionCount
                    && candidateCount < MaxTargetsPerVehicle;
                i++)
            {
                SignalJunction marker =
                    model.Junctions[section.FirstJunctionIndex + i];
                if (marker.AtomIndex == position.AtomIndex
                    || marker.SignalLane == position.CurrentLane)
                {
                    continue;
                }

                float distance = LineMileage.Forward(
                    model.TotalDistanceMeters,
                    position.VehicleMeters,
                    model.Atoms[marker.AtomIndex].StartMeters);
                if (!math.isfinite(distance) || distance <= 0f)
                    continue;
                if (distance > LookAheadMeters)
                    break;
                if (!m_Signals.TryRead(
                        marker.SignalLane,
                        out SignalLaneFact signal)
                    || signal.Intersection == currentIntersection
                    || signal.Intersection == Entity.Null
                    || HasCandidateIntersection(
                        state.Candidates,
                        candidateCount,
                        signal.Intersection))
                {
                    continue;
                }
                state.Candidates[candidateCount++] = new SignalCandidate(
                    signal.Lane,
                    signal.Intersection,
                    signal.GroupMask,
                    distance,
                    signal.UpdateFrame,
                    marker.AtomIndex);
            }

            ReconcileCandidates(
                state,
                candidateCount,
                frame,
                false,
                "track-candidate-missing");
#if RT_SIGNAL_WAIT_MEASURE
            LockTrace(state, frame);
#endif
        }

        private static void ApplyNavigation(
            VehicleTarget target,
            bool known,
            bool forward)
        {
            if (target == null)
                return;
            target.NavigationResult = known
                ? forward
                    ? NavigationForward
                    : NavigationAbsent
                : NavigationUnknown;
            if (forward)
                target.NavigationLatched = true;
        }

        private static int FirstMarkerAfter(
            SignalLineModel model,
            SignalTrackSection section,
            int atomIndex)
        {
            int low = 0;
            int high = section.JunctionCount;
            if (section.WrapsAtoms)
            {
                if (atomIndex >= section.StartAtomIndex)
                    high = section.WrapJunctionOffset;
                else
                    low = section.WrapJunctionOffset;
            }
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                int markerAtom = model.Junctions[
                    section.FirstJunctionIndex + middle].AtomIndex;
                if (markerAtom <= atomIndex)
                    low = middle + 1;
                else
                    high = middle;
            }
            if (section.WrapsAtoms
                && atomIndex >= section.StartAtomIndex
                && low == section.WrapJunctionOffset)
            {
                return section.WrapJunctionOffset;
            }
            return low;
        }

        private int ResolveTramSection(
            SignalLineModel model,
            TramSignalPosition position,
            Entity vehicle)
        {
            if (m_Port.TryStop(
                    vehicle,
                    out _,
                    out bool pending,
                    out int waypoint)
                && pending
                && waypoint >= 0
                && waypoint < model.DepartureSectionByWaypoint.Length)
            {
                int departureSection =
                    model.DepartureSectionByWaypoint[waypoint];
                if (departureSection >= 0
                    && departureSection < model.Sections.Length)
                {
                    return departureSection;
                }
            }
            return position.SectionIndex >= 0
                && position.SectionIndex < model.Sections.Length
                ? position.SectionIndex
                : -1;
        }

        private void ReconcileCandidates(
            VehicleSignalState state,
            int candidateCount,
            uint frame,
            bool candidateTruncated,
            string absentReason)
        {
            for (int i = 0; i < state.TargetCount; i++)
                state.Targets[i].SelectedCurrentFrame = false;
#if RT_BUS_SIGNAL_WAIT_MEASURE
            bool keptTruncatedTarget = false;
#endif

            for (int candidateIndex = 0;
                candidateIndex < candidateCount;
                candidateIndex++)
            {
                SignalCandidate candidate =
                    state.Candidates[candidateIndex];
                VehicleTarget target = FindExactTarget(
                    state,
                    candidate.SignalLane,
                    candidate.Intersection);
                if (target != null
                    && target.GroupMask != candidate.GroupMask)
                {
                    EndTarget(
                        state,
                        target,
                        "signal-identity-changed",
                        frame);
                    target = null;
                }
                if (target == null)
                {
                    VehicleTarget sameIntersection =
                        FindIntersectionTarget(
                            state,
                            candidate.Intersection);
                    if (sameIntersection != null)
                    {
                        EndTarget(
                            state,
                            sameIntersection,
                            "target-replaced-earlier-lane",
                            frame);
                    }

                    if (state.TargetCount >= MaxTargetsPerVehicle)
                    {
                        int dropIndex = FindDropTarget(
                            state,
                            candidateIndex,
                            candidateCount);
                        if (dropIndex < 0)
                        {
#if RT_BUS_SIGNAL_WAIT_MEASURE
                            m_GuardAudit.DropFallbackHits++;
                            if (!m_GuardAudit.DropFallbackLogged)
                            {
                                m_GuardAudit.DropFallbackLogged = true;
                                m_Port.Log("[SignalGuardAudit] kind=drop-fallback"
                                    + " vehicle=" + state.Vehicle
                                    + " lane=" + candidate.SignalLane
                                    + " intersection=" + candidate.Intersection
                                    + " frame=" + frame);
                            }
#endif
                            dropIndex = state.TargetCount - 1;
                        }
                        EndTarget(
                            state,
                            state.Targets[dropIndex],
                            "target-displaced-by-earlier-target",
                            frame);
                    }

                    target = AddTarget(state, candidate);
                }
                if (target == null)
                    continue;
                target.GroupMask = candidate.GroupMask;
                target.UpdateFrame = candidate.UpdateFrame;
                target.DistanceMeters = candidate.DistanceMeters;
                if (candidate.MarkerAtomIndex >= 0)
                    target.MarkerAtomIndex = candidate.MarkerAtomIndex;
                target.PositionCurrentFrame = frame;
                target.SelectedCurrentFrame = true;
                MoveTargetToIndex(
                    state,
                    target,
                    candidateIndex);
            }

            int index = candidateCount;
            while (index < state.TargetCount)
            {
                VehicleTarget target = state.Targets[index];
                if (target.PositionCurrentFrame == frame
                    && candidateCount < MaxTargetsPerVehicle)
                {
                    index++;
                }
                else if (target.PositionCurrentFrame == frame)
                {
                    EndTarget(
                        state,
                        target,
                        "target-displaced-by-earlier-target",
                        frame);
                }
                else if (candidateTruncated)
                {
                    target.DistanceMeters = float.NaN;
                    target.PositionCurrentFrame = uint.MaxValue;
#if RT_BUS_SIGNAL_WAIT_MEASURE
                    keptTruncatedTarget = true;
                    m_GuardAudit.CandidateTruncatedTargets++;
#endif
                    index++;
                }
                else
                {
                    EndTarget(state, target, absentReason, frame);
                }
            }

#if RT_BUS_SIGNAL_WAIT_MEASURE
            if (keptTruncatedTarget)
                m_GuardAudit.CandidateTruncatedFrames++;
#endif
            for (int i = 0; i < state.TargetCount; i++)
                state.Targets[i].SelectedCurrentFrame = false;
        }

        private VehicleTarget AddTarget(
            VehicleSignalState state,
            SignalCandidate candidate)
        {
            if (state.TargetCount >= MaxTargetsPerVehicle)
                return null;
            VehicleTarget target = state.Targets[state.TargetCount];
            target.Reset();
            target.Vehicle = state.Vehicle;
            target.SignalLane = candidate.SignalLane;
            target.Intersection = candidate.Intersection;
            target.GroupMask = candidate.GroupMask;
            target.UpdateFrame = candidate.UpdateFrame;
            target.DistanceMeters = candidate.DistanceMeters;
            target.PositionCurrentFrame = state.RefreshFrame;
            state.TargetCount++;
            return target;
        }

        private int FindDropTarget(
            VehicleSignalState state,
            int currentCandidate,
            int candidateCount)
        {
            for (int i = state.TargetCount - 1; i >= 0; i--)
            {
                VehicleTarget target = state.Targets[i];
                if (target.SelectedCurrentFrame)
                    continue;
                bool neededLater = false;
                for (int j = currentCandidate + 1;
                    j < candidateCount;
                    j++)
                {
                    SignalCandidate later = state.Candidates[j];
                    if (later.Intersection == target.Intersection)
                    {
                        neededLater = true;
                        break;
                    }
                }
                if (!neededLater)
                    return i;
            }
            for (int i = state.TargetCount - 1; i >= 0; i--)
            {
                if (!state.Targets[i].SelectedCurrentFrame)
                    return i;
            }
            return -1;
        }

        private static bool HasCandidateIntersection(
            SignalCandidate[] candidates,
            int count,
            Entity intersection)
        {
            for (int i = 0; i < count; i++)
            {
                if (candidates[i].Intersection == intersection)
                    return true;
            }
            return false;
        }

        private static VehicleTarget FindExactTarget(
            VehicleSignalState state,
            Entity signalLane,
            Entity intersection)
        {
            for (int i = 0; i < state.TargetCount; i++)
            {
                VehicleTarget target = state.Targets[i];
                if (target.SignalLane == signalLane
                    && target.Intersection == intersection)
                {
                    return target;
                }
            }
            return null;
        }

        private static VehicleTarget FindIntersectionTarget(
            VehicleSignalState state,
            Entity intersection)
        {
            for (int i = 0; i < state.TargetCount; i++)
            {
                if (state.Targets[i].Intersection == intersection)
                    return state.Targets[i];
            }
            return null;
        }

        private static void MoveTargetToIndex(
            VehicleSignalState state,
            VehicleTarget target,
            int destination)
        {
            int source = -1;
            for (int i = 0; i < state.TargetCount; i++)
            {
                if (state.Targets[i] == target)
                {
                    source = i;
                    break;
                }
            }
            if (source < 0 || source == destination)
                return;
            if (source > destination)
            {
                for (int i = source; i > destination; i--)
                    state.Targets[i] = state.Targets[i - 1];
            }
            else
            {
                for (int i = source; i < destination; i++)
                    state.Targets[i] = state.Targets[i + 1];
            }
            state.Targets[destination] = target;
        }

        private static int FindInspectedLane(
            List<RoadEventSource.RoadNavigationSlice> inspected,
            Entity lane)
        {
            for (int i = 0; i < inspected.Count; i++)
            {
                if (inspected[i].Lane == lane)
                    return i;
            }
            return -1;
        }

        private static void MarkPosition(
            VehicleTarget target,
            SignalLaneFact signal,
            float distance,
            uint frame)
        {
            target.GroupMask = signal.GroupMask;
            target.UpdateFrame = signal.UpdateFrame;
            target.DistanceMeters = distance;
            target.PositionCurrentFrame = frame;
        }

        private bool HasDueTarget(
            VehicleSignalState state,
            uint frame)
        {
            if (!CanApplyPriority(state))
                return false;
            for (int i = 0; i < state.TargetCount; i++)
            {
                VehicleTarget target = state.Targets[i];
                if (IsTrafficLightFrame(frame, target.UpdateFrame))
                {
                    return true;
                }
            }
            return false;
        }

        private void ArbitrateDue(uint frame)
        {
            for (int i = 0; i < m_DueVehicles.Count; i++)
            {
                Entity vehicle = m_DueVehicles[i];
                if (!m_States.TryGetValue(
                        vehicle,
                        out VehicleSignalState state))
                {
                    continue;
                }

                if (state.TargetCount == 0)
                {
                    continue;
                }

                int targetIndex = 0;
                while (targetIndex < state.TargetCount)
                {
                    VehicleTarget target = state.Targets[targetIndex];
                    if (target.PositionCurrentFrame != frame)
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Position);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Position);
#endif
                        targetIndex++;
                        continue;
                    }
                    if (!CanApplyPriority(state))
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Ineligible);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Ineligible);
#endif
                        targetIndex++;
                        continue;
                    }
                    if (!m_Signals.TryRead(
                            target.SignalLane,
                            out SignalLaneFact signal))
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Signal);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Signal);
#endif
                        EndTarget(state, target, "qualification-ended", frame);
                        continue;
                    }
                    if (signal.Intersection != target.Intersection
                        || signal.GroupMask != target.GroupMask)
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Identity);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Identity);
#endif
                        EndTarget(state, target, "signal-identity-changed", frame);
                        continue;
                    }
                    if (!IsTrafficLightFrame(frame, signal.UpdateFrame))
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Identity);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Identity);
#endif
                        targetIndex++;
                        continue;
                    }
                    if (!CandidateAllowed(target, frame))
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        if (TraceMatch(target)
                            && m_Intersections.TryGetValue(target.Intersection, out IntersectionState traceState)
                            && IsCooling(traceState, target.GroupMask, frame, out uint until))
                        {
                            RecordTrace(target, TraceResult.Cooldown, until);
                        }
                        else
                        {
                            RecordTrace(target, TraceResult.Ineligible);
                        }
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        if (m_Intersections.TryGetValue(
                                target.Intersection,
                                out IntersectionState busState)
                            && IsCooling(
                                busState,
                                target.GroupMask,
                                frame,
                                out uint busUntil))
                        {
                            RecordBusMeasureDecision(target, BusMeasureDecision.Cooldown, busUntil);
                        }
                        else
                        {
                            RecordBusMeasureDecision(target, BusMeasureDecision.Ineligible);
                        }
#endif
                        targetIndex++;
                        continue;
                    }
                    if (!m_Winners.TryGetValue(
                            target.Intersection,
                            out VehicleTarget winner))
                    {
                        m_Winners[target.Intersection] = target;
                    }
                    else if (Better(target, winner))
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(winner, TraceResult.Lost, 0, target.Vehicle);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(winner, BusMeasureDecision.Lost, 0, target.Vehicle);
#endif
                        m_Winners[target.Intersection] = target;
                    }
#if RT_SIGNAL_WAIT_MEASURE || RT_BUS_SIGNAL_WAIT_MEASURE
                    else
                    {
#if RT_SIGNAL_WAIT_MEASURE
                        RecordTrace(target, TraceResult.Lost, 0, winner.Vehicle);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                        RecordBusMeasureDecision(target, BusMeasureDecision.Lost, 0, winner.Vehicle);
#endif
                    }
#endif
                    targetIndex++;
                }
            }
            foreach (VehicleTarget winner in m_Winners.Values)
            {
                if (m_States.TryGetValue(
                        winner.Vehicle,
                        out VehicleSignalState state))
                {
                    SubmitWinner(state, winner, frame);
                }
            }
            m_DueVehicles.Clear();
        }

        private bool CandidateAllowed(
            VehicleTarget target,
            uint frame)
        {
            if (!m_States.TryGetValue(
                    target.Vehicle,
                    out VehicleSignalState owner)
                || !CanApplyPriority(owner))
            {
                return false;
            }
            if (owner.Mode == TransitMode.Bus && target.ProgressSuppressed)
                return false;
            if (!m_Intersections.TryGetValue(
                    target.Intersection,
                    out IntersectionState state))
            {
                return true;
            }
            if (!IsCooling(
                    state,
                    target.GroupMask,
                    frame,
                    out uint cooldownUntil))
            {
                return true;
            }
            return false;
        }

        private bool IsTramTarget(VehicleTarget target)
        {
            return target != null
                && m_States.TryGetValue(
                    target.Vehicle,
                    out VehicleSignalState owner)
                && owner.Mode == TransitMode.Tram;
        }

        private bool Better(
            VehicleTarget candidate,
            VehicleTarget current)
        {
            bool candidateTram = IsTramTarget(candidate);
            bool currentTram = IsTramTarget(current);
            if (candidateTram != currentTram)
                return candidateTram;
            bool candidateGreen = m_Signals.TryRead(
                    candidate.SignalLane,
                    out SignalLaneFact left)
                && left.Signal == LaneSignalType.Go
                && left.CurrentGroupBit != 0
                && (left.CurrentGroupBit & candidate.GroupMask) != 0;
            bool currentGreen = m_Signals.TryRead(
                    current.SignalLane,
                    out SignalLaneFact right)
                && right.Signal == LaneSignalType.Go
                && right.CurrentGroupBit != 0
                && (right.CurrentGroupBit & current.GroupMask) != 0;
            if (candidateGreen != currentGreen)
                return candidateGreen;
            if (candidate.DistanceMeters != current.DistanceMeters)
                return candidate.DistanceMeters < current.DistanceMeters;
            return candidate.GroupMask != current.GroupMask
                ? candidate.GroupMask < current.GroupMask
                : CompareEntity(candidate.Vehicle, current.Vehicle) < 0;
        }

        private void SubmitWinner(
            VehicleSignalState owner,
            VehicleTarget target,
            uint frame)
        {
            if (!CanApplyPriority(owner)
                || !m_Signals.TryRead(
                    target.SignalLane,
                    out SignalLaneFact signal))
            {
#if RT_SIGNAL_WAIT_MEASURE
                RecordTrace(
                    target,
                    CanApplyPriority(owner)
                        ? TraceResult.Signal
                        : TraceResult.Ineligible);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                RecordBusMeasureDecision(target, CanApplyPriority(owner)
                    ? BusMeasureDecision.Signal : BusMeasureDecision.Ineligible);
#endif
                return;
            }
            IntersectionState state = GetIntersection(
                target.Intersection);
            if (IsCooling(state, target.GroupMask, frame, out uint cooldownUntil))
            {
#if RT_SIGNAL_WAIT_MEASURE
                RecordTrace(
                    target,
                    TraceResult.Cooldown, cooldownUntil);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                RecordBusMeasureDecision(target, BusMeasureDecision.Cooldown, cooldownUntil);
#endif
                return;
            }

            bool tram = owner.Mode == TransitMode.Tram;
            sbyte priority = tram
                ? TramPriority
                : BusPriority;
            if (!m_Signals.TrySubmit(
                    target.SignalLane,
                    target.Vehicle,
                    priority,
                    out SignalLaneFact before))
            {
#if RT_SIGNAL_WAIT_MEASURE
                RecordTrace(
                    target,
                    TraceResult.Rejected, 0,
                    before.Petitioner != Entity.Null
                        ? before.Petitioner
                        : signal.Petitioner);
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
                RecordBusMeasureDecision(target, BusMeasureDecision.Rejected, 0,
                    before.Petitioner != Entity.Null ? before.Petitioner : signal.Petitioner);
#endif
                return;
            }

#if RT_BUS_SIGNAL_WAIT_MEASURE
            RecordBusCycleWrite(target, before, frame);
#endif
            state.SelectedGroup = target.GroupMask;
            state.SelectedMaxPriorityFrames = tram
                ? TramMaxPriorityFrames
                : BusMaxPriorityFrames;
            ushort actualGroup = before.Signal == LaneSignalType.Go
                && before.CurrentGroupBit != 0
                && (before.CurrentGroupBit & target.GroupMask) != 0
                ? before.CurrentGroupBit
                : (ushort)0;
            if (actualGroup != 0)
            {
                if (state.PriorityGroup == 0
                    || state.PriorityGroup != actualGroup)
                {
                    state.PriorityGroup = actualGroup;
                    state.PriorityStartFrame = frame;
                    state.MaxPriorityFrames =
                        state.SelectedMaxPriorityFrames;
                }
                else if (tram
                    && state.MaxPriorityFrames > TramMaxPriorityFrames)
                {
                    state.MaxPriorityFrames = TramMaxPriorityFrames;
                }
            }

#if RT_SIGNAL_WAIT_MEASURE
            RecordTrace(target, TraceResult.Submitted);
            if (TraceMatch(target) && m_Trace.CycleOpen)
            {
                m_Trace.Waiting = true;
                m_Trace.LastWrite = frame;
                m_Trace.PostSample = m_Trace.Cycle;
            }
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
            RecordBusMeasureDecision(target, BusMeasureDecision.Submitted, (uint)priority);
            if (target.GuardAwaitResubmit)
            {
                target.GuardAwaitResubmit = false;
                target.GuardAwaitGreen = true;
                LogProgressGuard(owner, target, "resubmitted",
                    " group=" + target.GroupMask
                    + " frame=" + frame);
            }
#endif
            if (target.Submitted)
                return;
            target.Submitted = true;
            target.SubmittedFrame = frame;
            target.WasGreenAtSubmit = before.Signal == LaneSignalType.Go;
            if (target.WasGreenAtSubmit)
            {
                target.FirstGreenFrame = frame;
                target.FirstGreenObserved = true;
            }
#if !RT_SIGNAL_WAIT_MEASURE
            m_Port.Log("[SignalPrioritySubmitted] vehicle="
                + target.Vehicle
                + " line=" + owner.Line
                + " mode=" + owner.Mode
                + " lane=" + target.SignalLane
                + " intersection=" + target.Intersection
                + " group=" + target.GroupMask
                + " priority=" + priority
                + " submittedFrame=" + frame
                + " wasGreenAtSubmit=" + target.WasGreenAtSubmit);
#endif
        }

        private IntersectionState GetIntersection(
            Entity intersection)
        {
            if (!m_Intersections.TryGetValue(
                    intersection,
                    out IntersectionState state))
            {
                state = new IntersectionState();
                m_Intersections.Add(intersection, state);
            }
            return state;
        }

        private static void AddCooldown(
            IntersectionState state,
            ushort groupMask,
            uint frame)
        {
            if (groupMask == 0)
                return;
            uint untilFrame = frame + GroupCooldownFrames;
            for (int i = 0; i < state.Cooldowns.Count; i++)
            {
                if (state.Cooldowns[i].GroupMask != groupMask)
                    continue;
                state.Cooldowns[i] = new GroupCooldown(
                    groupMask,
                    untilFrame);
                return;
            }
            state.Cooldowns.Add(new GroupCooldown(
                groupMask,
                untilFrame));
        }

        private static bool IsCooling(
            IntersectionState state,
            ushort groupMask,
            uint frame,
            out uint untilFrame)
        {
            untilFrame = 0;
            CleanupCooldowns(state, frame);
            for (int i = 0; i < state.Cooldowns.Count; i++)
            {
                if ((state.Cooldowns[i].GroupMask & groupMask) != 0)
                {
                    untilFrame = state.Cooldowns[i].UntilFrame;
                    return true;
                }
            }
            return false;
        }

        private static bool TryReadCooldown(
            IntersectionState state,
            ushort groupMask,
            uint frame,
            out uint untilFrame)
        {
            untilFrame = 0;
            for (int i = 0; i < state.Cooldowns.Count; i++)
            {
                GroupCooldown cooldown = state.Cooldowns[i];
                if ((cooldown.GroupMask & groupMask) != 0
                    && FrameBefore(frame, cooldown.UntilFrame))
                {
                    untilFrame = cooldown.UntilFrame;
                    return true;
                }
            }
            return false;
        }

        private static void CleanupCooldowns(
            IntersectionState state,
            uint frame)
        {
            for (int i = state.Cooldowns.Count - 1; i >= 0; i--)
            {
                if (!FrameBefore(
                        frame,
                        state.Cooldowns[i].UntilFrame))
                {
                    state.Cooldowns.RemoveAt(i);
                }
            }
        }

        private static bool FrameBefore(uint frame, uint untilFrame)
        {
            return unchecked((int)(frame - untilFrame)) < 0;
        }

        private void EndAllTargets(
            VehicleSignalState state,
            string reason,
            uint frame)
        {
            while (state.TargetCount > 0)
            {
                EndTarget(
                    state,
                    state.Targets[state.TargetCount - 1],
                    reason,
                    frame);
            }
        }

        private void EndTarget(
            VehicleSignalState state,
            VehicleTarget target,
            string reason,
            uint frame)
        {
            int index = -1;
            for (int i = 0; i < state.TargetCount; i++)
            {
                if (state.Targets[i] == target)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                return;

#if RT_BUS_SIGNAL_WAIT_MEASURE
            if (state.Mode == TransitMode.Bus
                && (target.ProgressSuppressed
                    || target.GuardAwaitResubmit
                    || target.GuardAwaitGreen))
            {
                string phase = target.ProgressSuppressed
                    ? "suppressed"
                    : target.GuardAwaitResubmit
                        ? "await-resubmit"
                        : "await-green";
                LogProgressGuard(state, target, "guard-ended",
                    " phase=" + phase
                    + " frame=" + frame
                    + " reason=" + (reason ?? string.Empty)
                    + " suppressedFrame=" + target.ProgressSuppressedFrame
                    + " suppressedDistance=" + target.ProgressSuppressedDistance);
            }
#endif
#if RT_BUS_SIGNAL_WAIT_MEASURE
            EndBusMeasure(state, target, reason, frame);
#endif
            LogTargetEnd(state, target, reason);
#if RT_SIGNAL_WAIT_MEASURE
            TraceEnd(target, reason);
#endif
            int last = state.TargetCount - 1;
            for (int i = index; i < last; i++)
                state.Targets[i] = state.Targets[i + 1];
            state.Targets[last] = target;
            state.TargetCount = last;
            target.Reset();

        }

        private void LogTargetEnd(
            VehicleSignalState state,
            VehicleTarget target,
            string reason)
        {
            if (!target.Submitted)
                return;

#if !RT_SIGNAL_WAIT_MEASURE
            string green = target.WasGreenAtSubmit
                ? "green-at-submit"
                : target.FirstGreenObserved
                    ? "observed-green-delay-frames="
                        + (target.FirstGreenFrame
                            - target.SubmittedFrame)
                    : "green-not-observed";
            m_Port.Log("[SignalPriorityEnded] vehicle="
                + target.Vehicle
                + " line=" + state.Line
                + " mode=" + state.Mode
                + " lane=" + target.SignalLane
                + " intersection=" + target.Intersection
                + " group=" + target.GroupMask
                + " submittedFrame=" + target.SubmittedFrame
                + " result=" + green
                + " reason=" + (reason ?? string.Empty));
#endif
        }

#if RT_SIGNAL_WAIT_MEASURE
        private void LockTrace(
            VehicleSignalState state,
            uint frame)
        {
            if (m_Trace.Active
                || m_Trace.Closed
                || state.Mode != TransitMode.Tram
                || !m_Port.LineEnabled(state.Line)
                || state.TargetCount == 0)
            {
                return;
            }

            VehicleTarget target = state.Targets[0];
            if (target.PositionCurrentFrame != frame
                || !math.isfinite(target.DistanceMeters)
                || target.DistanceMeters <= 0f
                || target.DistanceMeters > LookAheadMeters
                || !m_Signals.TryRead(target.SignalLane, out SignalLaneFact signal)
                || signal.Intersection != target.Intersection
                || signal.GroupMask != target.GroupMask
                || signal.Signal == LaneSignalType.Go)
            {
                return;
            }

            m_Trace.Active = true;
            m_Trace.Vehicle = state.Vehicle;
            m_Trace.Line = state.Line;
            m_Trace.Lane = target.SignalLane;
            m_Trace.Intersection = target.Intersection;
            m_Trace.Group = target.GroupMask;
            m_Trace.UpdateFrame = target.UpdateFrame;
            m_Trace.NonGoStart = frame;
            m_Trace.Head = 0;
            m_Trace.Count = 0;
            m_Trace.CycleOpen = false;
            m_Trace.Waiting = false;
            m_Trace.LastWrite = 0;
            m_Trace.Trigger = 0;
            m_Trace.PostCycles = 0;
            if (IsTrafficLightFrame(frame, target.UpdateFrame))
            {
                TraceOpen(frame);
            }
        }

        private void TracePost(uint frame)
        {
            if (!m_Trace.Active
                || !m_Trace.Waiting
                || frame <= m_Trace.LastWrite)
            {
                return;
            }

            m_Trace.Waiting = false;
            ref TraceSample sample = ref m_Trace.Samples[m_Trace.PostSample];
            if (!m_Signals.TryReadTrace(m_Trace.Lane, out SignalTraceSnapshot post)
                || post.Intersection != m_Trace.Intersection
                || post.TargetGroupMask != m_Trace.Group)
            {
                sample.Consumption = 4;
                return;
            }

            sample.Post = post;
            if (post.TargetPriority == post.TargetDefault
                && post.TargetPetitioner == Entity.Null)
            {
                sample.Consumption = 1;
            }
            else if (post.TargetPriority == TramPriority
                && post.TargetPetitioner == m_Trace.Lane)
            {
                sample.Consumption = 3;
            }
            else
            {
                sample.Consumption = 2;
            }

            if (post.TargetSignal == (byte)LaneSignalType.Go)
            {
                if (m_Trace.Trigger != 0)
                {
                    FlushTrace(1);
                }
                else
                {
                    ResetTrace();
                }
            }
        }

        private void TraceOpen(uint frame)
        {
            if (!m_Trace.Active
                || m_Trace.CycleOpen
                || !IsTrafficLightFrame(frame, m_Trace.UpdateFrame))
            {
                return;
            }

            int index = m_Trace.Head;
            m_Trace.Samples[index] = new TraceSample
            {
                Frame = frame,
            };
            m_Trace.Head = (index + 1) % TraceSlots;
            if (m_Trace.Count < TraceSlots)
            {
                m_Trace.Count++;
            }

            m_Trace.Cycle = index;
            m_Trace.CycleOpen = true;
            ref TraceSample sample = ref m_Trace.Samples[index];
            m_Signals.TryReadTraceBlocker(m_Trace.Vehicle, out sample.Blocker);
            if (!m_States.TryGetValue(m_Trace.Vehicle, out VehicleSignalState state)
                || state.TargetCount == 0
                || !TraceMatch(state.Targets[0]))
            {
                sample.Result = TraceResult.Ended;
                return;
            }
            if (!m_Signals.TryReadTrace(m_Trace.Lane, out SignalTraceSnapshot pre))
            {
                sample.Result = TraceResult.Signal;
                return;
            }
            sample.Pre = pre;
            if (pre.Intersection != m_Trace.Intersection || pre.TargetGroupMask != m_Trace.Group)
            {
                sample.Result = TraceResult.Identity;
                return;
            }

            if (pre.TargetSignal == (byte)LaneSignalType.Go)
            {
                sample.Result = TraceResult.Green;
            }
        }

        private bool TraceMatch(VehicleTarget target)
        {
            return m_Trace.Active
                && target != null
                && target.Vehicle == m_Trace.Vehicle
                && target.SignalLane == m_Trace.Lane
                && target.Intersection == m_Trace.Intersection
                && target.GroupMask == m_Trace.Group;
        }

        private void RecordTrace(
            VehicleTarget target,
            TraceResult result,
            uint value = 0,
            Entity related = default)
        {
            if (!TraceMatch(target) || !m_Trace.CycleOpen)
            {
                return;
            }

            ref TraceSample sample = ref m_Trace.Samples[m_Trace.Cycle];
            if (sample.Result != TraceResult.None)
            {
                return;
            }

            sample.Result = result;
            sample.Value = value;
            sample.Related = related;
            if (result >= TraceResult.Cooldown
                && result <= TraceResult.Submitted
                && sample.Pre.TargetSignal != (byte)LaneSignalType.Go
                && target.PositionCurrentFrame == m_CurrentFrame
                && math.isfinite(target.DistanceMeters)
                && target.DistanceMeters > 0f
                && target.DistanceMeters <= LookAheadMeters
                && m_CurrentFrame - m_Trace.NonGoStart >= TraceWaitFrames
                && m_Trace.Trigger == 0)
            {
                m_Trace.Trigger = TraceLong;
            }
        }

        private void TraceEnd(
            VehicleTarget target,
            string reason)
        {
            if (!TraceMatch(target))
            {
                return;
            }

            if (!m_Trace.CycleOpen)
            {
                if (m_Trace.Trigger != 0)
                {
                    FlushTrace(2);
                }
                else
                {
                    ResetTrace();
                }

                return;
            }

            TraceResult result = TraceResult.Ended;
            if (reason == "signal-identity-changed")
            {
                result = TraceResult.Identity;
            }
            else if (reason == "qualification-ended")
            {
                result = TraceResult.Signal;
            }

            RecordTrace(target, result);
            if (m_Trace.Trigger != 0)
            {
                FlushTrace(2);
            }
            else if (m_Trace.Samples[m_Trace.Cycle].Result == TraceResult.Submitted)
            {
                ResetTrace();
            }
        }

        private void TraceFinish()
        {
            if (!m_Trace.Active || !m_Trace.CycleOpen)
            {
                return;
            }

            ref TraceSample sample = ref m_Trace.Samples[m_Trace.Cycle];
            byte trigger = m_Trace.Trigger;
            if (sample.Result == TraceResult.None)
            {
                sample.Result = TraceResult.Position;
            }

            if (sample.Result == TraceResult.Position && m_Trace.Trigger == 0)
            {
                m_Trace.Trigger = TracePosition;
            }

            m_Trace.CycleOpen = false;
            bool terminal = sample.Result == TraceResult.Ineligible
                || sample.Result == TraceResult.Signal
                || sample.Result == TraceResult.Identity
                || sample.Result == TraceResult.Green
                || sample.Result == TraceResult.Ended;
            if (m_Trace.Trigger == 0)
            {
                if (terminal)
                {
                    ResetTrace();
                }

                return;
            }

            if (terminal)
            {
                FlushTrace(sample.Result == TraceResult.Green ? (byte)1 : (byte)2);
                return;
            }

            if (trigger != 0 && ++m_Trace.PostCycles >= 8)
            {
                FlushTrace(3);
            }
        }

        private void ResetTrace()
        {
            m_Trace.Active = false;
            m_Trace.CycleOpen = false;
            m_Trace.Waiting = false;
        }

        private void FlushTrace(byte close)
        {
            if (!m_Trace.Active || m_Trace.Trigger == 0)
            {
                return;
            }

            m_Port.Log("[TramSignalTrace] trigger=" + m_Trace.Trigger
                + " close=" + close
                + " vehicle=" + m_Trace.Vehicle
                + " line=" + m_Trace.Line
                + " lane=" + m_Trace.Lane
                + " intersection=" + m_Trace.Intersection
                + " group=" + m_Trace.Group
                + " nonGoStart=" + m_Trace.NonGoStart);
            int first = m_Trace.Count == TraceSlots ? m_Trace.Head : 0;
            for (int i = 0; i < m_Trace.Count; i++)
            {
                TraceSample sample = m_Trace.Samples[(first + i) % TraceSlots];
                m_Port.Log("[TramSignalTrace] frame=" + sample.Frame
                    + " result=" + (byte)sample.Result
                    + " value=" + sample.Value
                    + " related=" + sample.Related
                    + " blocker=" + TraceBlocker(sample)
                    + " pre=" + TraceSnapshot(sample.Pre)
                    + " post=" + TraceSnapshot(sample.Post)
                    + " consumed=" + sample.Consumption);
            }

            m_Port.Log("[TramSignalTraceSummary] trigger=" + m_Trace.Trigger
                + " close=" + close
                + " samples=" + m_Trace.Count
                + " postCycles=" + m_Trace.PostCycles);
            ResetTrace();
            m_Trace.Closed = true;
        }

        private static string TraceBlocker(TraceSample sample)
        {
            return ((byte)sample.Blocker.Type).ToString()
                + "/" + sample.Blocker.MaxSpeed
                + "/" + sample.Blocker.Entity
                + "/" + (byte)sample.Blocker.Kind;
        }

        private static string TraceSnapshot(SignalTraceSnapshot s)
        {
            if (s.Intersection == Entity.Null)
            {
                return "-";
            }

            return s.Intersection
                + "/" + s.State
                + "/" + s.Timer
                + "/" + s.CurrentSignalGroup
                + "/" + s.NextSignalGroup
                + "/" + s.SignalGroupCount
                + "/t=" + s.TargetGroupMask
                + "/" + s.TargetSignal
                + "/" + s.TargetPriority
                + "/" + s.TargetDefault
                + "/" + s.TargetPetitioner
                + "/" + (byte)s.TargetPetitionerKind
                + "/" + s.TargetBlocker
                + "/" + (byte)s.TargetBlockerKind
                + "/h=" + s.HighestPriority
                + "/" + s.HighestGroupMask
                + "/" + s.HighestPetitioner
                + "/" + (byte)s.HighestPetitionerKind;
        }
#endif

        private static int CompareEntity(
            Entity left,
            Entity right)
        {
            return left.Index != right.Index
                ? left.Index.CompareTo(right.Index)
                : left.Version.CompareTo(right.Version);
        }
    }
}
