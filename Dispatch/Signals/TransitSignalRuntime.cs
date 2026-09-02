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

#if RT_DEBUG_TOOLS
        private static bool SignalPriorityLogEnabled() => false;
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
#if RT_DEBUG_TOOLS
            internal bool Submitted;
            internal uint SubmittedFrame;
            internal bool WasGreenAtSubmit;
            internal uint FirstGreenFrame;
            internal bool FirstGreenObserved;
#endif
            internal bool SelectedCurrentFrame;
            internal uint ProgressAnchorFrame;
            internal float ProgressAnchorDistance, ProgressSuppressedDistance;
            internal bool ProgressSuppressed;

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
#if RT_DEBUG_TOOLS
                Submitted = false;
                SubmittedFrame = 0;
                WasGreenAtSubmit = false;
                FirstGreenFrame = 0;
                FirstGreenObserved = false;
#endif
                SelectedCurrentFrame = false;
                ProgressAnchorFrame = uint.MaxValue;
                ProgressAnchorDistance = float.NaN;
                ProgressSuppressed = false;
                ProgressSuppressedDistance = float.NaN;
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
            internal ushort DueMask;

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
            internal uint UpdateFrame;
            internal ushort PriorityGroup;
            internal uint PriorityStartFrame;
            internal uint MaxPriorityFrames;
            internal ushort SelectedGroup;
            internal uint SelectedMaxPriorityFrames;
            internal Entity SelectedLane;
            internal readonly List<GroupCooldown> Cooldowns = new List<GroupCooldown>(4);
            internal ushort GreenGroups;
            internal int CandidateCount;
            internal ushort CandidateGroups;
        }

        private readonly struct DueCandidate
        {
            internal readonly VehicleSignalState State;
            internal readonly VehicleTarget Target;
            internal readonly SignalLaneFact Signal;
            internal readonly TransitMode Mode;

            internal DueCandidate(
                VehicleSignalState state,
                VehicleTarget target,
                SignalLaneFact signal)
            {
                State = state;
                Target = target;
                Signal = signal;
                Mode = state.Mode;
            }
        }

        private readonly struct PostObservation
        {
            internal readonly Entity Intersection;
            internal readonly Entity Lane;
            internal readonly ushort GroupMask;
            internal readonly uint ConsumeFrame;
#if RT_DEBUG_TOOLS
            internal readonly VehicleTarget Target;
#endif

            internal PostObservation(
                Entity intersection,
                Entity lane,
                ushort groupMask,
                uint consumeFrame
#if RT_DEBUG_TOOLS
                , VehicleTarget target
#endif
                )
            {
                Intersection = intersection;
                Lane = lane;
                GroupMask = groupMask;
                ConsumeFrame = consumeFrame;
#if RT_DEBUG_TOOLS
                Target = target;
#endif
            }
        }
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
        private readonly Dictionary<Entity, DueCandidate> m_Winners =
            new Dictionary<Entity, DueCandidate>(64);
        private readonly List<Entity> m_Entities = new List<Entity>(512);
        private readonly HashSet<Entity> m_RoadRetries = new HashSet<Entity>(64);
        private readonly HashSet<Entity>[] m_DueBuckets = new HashSet<Entity>[16];
        private readonly HashSet<Entity>[] m_IntersectionBuckets = new HashSet<Entity>[16];
        private readonly List<Entity> m_DueScratch = new List<Entity>(128);
        private readonly List<Entity> m_IntersectionScratch = new List<Entity>(64);
        private readonly List<DueCandidate> m_DueCandidates = new List<DueCandidate>(128);
        private readonly List<PostObservation> m_PostObservations =
            new List<PostObservation>(64);
        private readonly Dictionary<Entity, SignalLaneFact> m_ProcessSignals =
            new Dictionary<Entity, SignalLaneFact>(128);
        private readonly Dictionary<Entity, bool> m_LineGate =
            new Dictionary<Entity, bool>(64);
        private uint m_LineGateFrame = uint.MaxValue;
        private uint m_CurrentFrame;
        private bool m_ReadingProcessSignals;

        internal TransitSignalRuntime(TransitSignalPort port)
        {
            m_Port = port;
            m_Signals = port.Signals;
            for (int i = 0; i < 16; i++)
            {
                m_DueBuckets[i] = new HashSet<Entity>(32);
                m_IntersectionBuckets[i] = new HashSet<Entity>(16);
            }
        }

        internal void Tick(uint frame)
        {
            m_CurrentFrame = frame;
            if ((frame & 3u) == 2u)
                ObserveSignalResults(frame);
            ConsumeFacts(frame);
            RefreshSourceVehicles(frame);
            if ((frame & 3u) == 1u)
            {
                RetryRoadVehicles(frame);
                ProcessSignalBucket(frame);
            }
        }

        internal void ForgetVehicle(Entity vehicle, string reason)
        {
            if (vehicle == Entity.Null)
                return;
            if (m_States.TryGetValue(vehicle, out VehicleSignalState state))
            {
                EndAllTargets(state, reason, m_CurrentFrame);
                RemoveDueBuckets(state);
                RemovePostObservations(vehicle);
                m_States.Remove(vehicle);
            }
            CancelRoadRetry(vehicle);
        }

        internal void Clear(string reason)
        {
            m_Entities.Clear();
            foreach (Entity vehicle in m_States.Keys)
                m_Entities.Add(vehicle);
            for (int i = 0; i < m_Entities.Count; i++)
                ForgetVehicle(m_Entities[i], reason);
            m_States.Clear();
            m_Intersections.Clear();
            m_Winners.Clear();
            m_RoadRetries.Clear();
            for (int i = 0; i < 16; i++)
            {
                m_DueBuckets[i].Clear();
                m_IntersectionBuckets[i].Clear();
            }
            m_DueScratch.Clear();
            m_IntersectionScratch.Clear();
            m_DueCandidates.Clear();
            m_PostObservations.Clear();
            m_ProcessSignals.Clear();
            m_LineGate.Clear();
            m_LineGateFrame = uint.MaxValue;
            m_Entities.Clear();
        }

        private void ObserveSignalResults(uint frame)
        {
            for (int i = 0; i < m_PostObservations.Count; i++)
            {
                PostObservation observation = m_PostObservations[i];
                if (!m_Intersections.TryGetValue(
                        observation.Intersection,
                        out IntersectionState state)
                    || state.SelectedGroup != observation.GroupMask
                    || state.SelectedLane != observation.Lane)
                {
                    continue;
                }

                if (!m_Signals.TryRead(observation.Lane, out SignalLaneFact signal)
                    || signal.Intersection != observation.Intersection
                    || signal.GroupMask != observation.GroupMask
                    || signal.UpdateFrame != state.UpdateFrame)
                {
                    EndPriorityWindow(observation.Intersection, state, frame);
                    state.SelectedGroup = 0;
                    state.SelectedMaxPriorityFrames = 0;
                    state.SelectedLane = Entity.Null;
                    SyncIntersectionBucket(observation.Intersection, state);
                    continue;
                }

                ushort actualGroup = signal.Signal == LaneSignalType.Go
                    ? (ushort)(signal.CurrentGroupBit & state.SelectedGroup)
                    : (ushort)0;
                if (actualGroup != 0)
                {
                    if (state.PriorityGroup != 0
                        && (actualGroup & state.PriorityGroup) == 0)
                    {
                        EndPriorityWindow(observation.Intersection, state, frame);
                    }
                    if (state.PriorityGroup == 0)
                    {
                        state.PriorityGroup = actualGroup;
                        state.PriorityStartFrame = observation.ConsumeFrame;
                        state.MaxPriorityFrames = state.SelectedMaxPriorityFrames;
                    }
#if RT_DEBUG_TOOLS
                    if (SignalPriorityLogEnabled()
                        && observation.Target != null
                        && observation.Target.Submitted
                        && !observation.Target.FirstGreenObserved)
                    {
                        observation.Target.FirstGreenFrame = observation.ConsumeFrame;
                        observation.Target.FirstGreenObserved = true;
                    }
#endif
                }
                else if (state.PriorityGroup != 0)
                {
                    EndPriorityWindow(observation.Intersection, state, frame);
                }
                SyncIntersectionBucket(observation.Intersection, state);
            }
            m_PostObservations.Clear();
        }

        private void ProcessSignalBucket(uint frame)
        {
            m_ProcessSignals.Clear();
            m_ReadingProcessSignals = true;
            try
            {
            int bucket = (int)((frame >> 2) & 15u);
            m_Winners.Clear();
            m_DueCandidates.Clear();
            CopyBucket(m_IntersectionBuckets[bucket], m_IntersectionScratch);
            for (int i = 0; i < m_IntersectionScratch.Count; i++)
            {
                Entity intersection = m_IntersectionScratch[i];
                if (m_Intersections.TryGetValue(
                        intersection,
                        out IntersectionState state)
                    && state.UpdateFrame == (uint)bucket)
                {
                    state.GreenGroups = 0;
                    state.CandidateCount = 0;
                    state.CandidateGroups = 0;
                }
            }

            CopyBucket(m_DueBuckets[bucket], m_DueScratch);
            for (int i = 0; i < m_DueScratch.Count; i++)
            {
                Entity vehicle = m_DueScratch[i];
                if (!m_States.TryGetValue(vehicle, out VehicleSignalState state))
                    continue;
                if (!LineEnabledThisFrame(state.Line))
                {
                    ForgetVehicle(vehicle, "qualification-ended");
                    continue;
                }

                RefreshVehicle(state, frame, false);
                int targetIndex = 0;
                while (targetIndex < state.TargetCount)
                {
                    VehicleTarget target = state.Targets[targetIndex];
                    if (target.UpdateFrame != (uint)bucket
                        || target.PositionCurrentFrame != frame
                        || !math.isfinite(target.DistanceMeters))
                    {
                        targetIndex++;
                        continue;
                    }
                    if (!TryReadSignal(target.SignalLane, out SignalLaneFact signal))
                    {
                        EndTarget(state, target, "qualification-ended", frame);
                        continue;
                    }
                    if (signal.Intersection != target.Intersection
                        || signal.GroupMask != target.GroupMask
                        || signal.UpdateFrame != target.UpdateFrame)
                    {
                        EndTarget(state, target, "signal-identity-changed", frame);
                        continue;
                    }
                    if (m_Intersections.TryGetValue(
                            signal.Intersection,
                            out IntersectionState intersectionState)
                        && intersectionState.UpdateFrame != signal.UpdateFrame)
                    {
                        EndTarget(state, target, "signal-identity-changed", frame);
                        continue;
                    }

                    DueCandidate candidate = new DueCandidate(state, target, signal);
                    if (intersectionState != null)
                    {
                        bool targetGreen = signal.Signal == LaneSignalType.Go
                            && signal.CurrentGroupBit != 0
                            && (signal.CurrentGroupBit & target.GroupMask) != 0;
                        bool actualPriorityGreen = targetGreen
                            && intersectionState.PriorityGroup != 0
                            && (signal.CurrentGroupBit
                                & intersectionState.PriorityGroup) != 0;
                        if (ShouldSuppressBusTarget(
                                state,
                                target,
                                intersectionState,
                                actualPriorityGreen,
                                frame))
                        {
                            targetIndex++;
                            continue;
                        }
                        intersectionState.CandidateCount++;
                        intersectionState.CandidateGroups |= target.GroupMask;
                        if (targetGreen)
                            intersectionState.GreenGroups |= signal.CurrentGroupBit;
                    }
                    m_DueCandidates.Add(candidate);
                    targetIndex++;
                }
                SyncDueBuckets(state);
            }

            UpdateIntersections(frame, bucket);
            for (int i = 0; i < m_DueCandidates.Count; i++)
            {
                DueCandidate candidate = m_DueCandidates[i];
                if (!CandidateAllowed(candidate, frame))
                    continue;
                if (!m_Winners.TryGetValue(
                        candidate.Signal.Intersection,
                        out DueCandidate winner)
                    || Better(candidate, winner))
                {
                    m_Winners[candidate.Signal.Intersection] = candidate;
                }
            }
            foreach (DueCandidate winner in m_Winners.Values)
                SubmitWinner(winner, frame);

            CopyBucket(m_IntersectionBuckets[bucket], m_IntersectionScratch);
            for (int i = 0; i < m_IntersectionScratch.Count; i++)
            {
                Entity intersection = m_IntersectionScratch[i];
                if (!m_Intersections.TryGetValue(
                        intersection,
                        out IntersectionState state)
                    || state.UpdateFrame != (uint)bucket
                    || (state.PriorityGroup == 0 && state.SelectedGroup == 0))
                {
                    continue;
                }
                AddPostObservation(intersection, state, frame);
            }
            m_DueCandidates.Clear();
            m_DueScratch.Clear();
            m_IntersectionScratch.Clear();
            }
            finally
            {
                m_ReadingProcessSignals = false;
                m_ProcessSignals.Clear();
            }
        }

        private void UpdateIntersections(uint frame, int bucket)
        {
            for (int i = 0; i < m_IntersectionScratch.Count; i++)
            {
                Entity intersection = m_IntersectionScratch[i];
                if (!m_Intersections.TryGetValue(
                        intersection,
                        out IntersectionState state)
                    || state.UpdateFrame != (uint)bucket)
                {
                    continue;
                }
                if (state.PriorityGroup != 0
                    && (state.CandidateGroups & state.PriorityGroup) == 0)
                {
                    EndPriorityWindow(intersection, state, frame);
                }
                if (state.SelectedGroup != 0
                    && (state.CandidateGroups & state.SelectedGroup) == 0)
                {
                    state.SelectedGroup = 0;
                    state.SelectedMaxPriorityFrames = 0;
                    state.SelectedLane = Entity.Null;
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
                        state.MaxPriorityFrames = state.SelectedMaxPriorityFrames;
                    }
                }
                else if (state.PriorityGroup != 0
                    && state.MaxPriorityFrames > 0u
                    && !FrameBefore(
                        frame,
                        state.PriorityStartFrame + state.MaxPriorityFrames))
                {
                    EndPriorityWindow(intersection, state, frame);
                }
                else if (state.PriorityGroup != 0
                    && (state.GreenGroups & state.PriorityGroup) == 0)
                {
                    EndPriorityWindow(intersection, state, frame);
                }
                CleanupCooldowns(state, frame);
                SyncIntersectionBucket(intersection, state);
            }
        }

        private void AddPostObservation(
            Entity intersection,
            IntersectionState state,
            uint frame)
        {
#if RT_DEBUG_TOOLS
            VehicleTarget target = m_Winners.TryGetValue(
                intersection,
                out DueCandidate winner)
                ? winner.Target
                : null;
            m_PostObservations.Add(new PostObservation(
                intersection,
                state.SelectedLane,
                state.SelectedGroup,
                frame,
                target));
#else
            m_PostObservations.Add(new PostObservation(
                intersection,
                state.SelectedLane,
                state.SelectedGroup,
                frame));
#endif
        }

        private static void CopyBucket(
            HashSet<Entity> source,
            List<Entity> destination)
        {
            destination.Clear();
            foreach (Entity entity in source)
                destination.Add(entity);
        }

        private bool TryReadSignal(Entity lane, out SignalLaneFact signal)
        {
            if (m_ReadingProcessSignals
                && m_ProcessSignals.TryGetValue(lane, out signal))
            {
                return true;
            }
            if (!m_Signals.TryRead(lane, out signal))
                return false;
            if (m_ReadingProcessSignals)
                m_ProcessSignals[lane] = signal;
            return true;
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
            target.ProgressSuppressedDistance = target.DistanceMeters;
            return true;
        }

        private void EndPriorityWindow(
            Entity intersection,
            IntersectionState state,
            uint frame)
        {
            ushort endedGroup = state.PriorityGroup;
            uint logicalEndFrame = state.PriorityStartFrame
                + state.MaxPriorityFrames;
            if (state.MaxPriorityFrames > 0u
                && !FrameBefore(frame, logicalEndFrame))
            {
                AddCooldown(state, endedGroup, logicalEndFrame);
            }
            state.PriorityGroup = 0;
            state.PriorityStartFrame = 0;
            state.MaxPriorityFrames = 0;
            if ((state.SelectedGroup & endedGroup) != 0)
            {
                state.SelectedGroup = 0;
                state.SelectedMaxPriorityFrames = 0;
                state.SelectedLane = Entity.Null;
            }
        }

        private void ConsumeFacts(uint frame)
        {
            IReadOnlyList<DeparturePendingEvent> pending = m_Port.PendingFacts();
            IReadOnlyList<DispatchEvent> dispatch = m_Port.DispatchFacts();
            IReadOnlyList<StopEvent> stops = m_Port.StopEvents();
            IReadOnlyList<LifecycleEvent> lifecycle = m_Port.LifecycleFacts();
            if (pending.Count == 0
                && dispatch.Count == 0
                && stops.Count == 0
                && lifecycle.Count == 0)
            {
                return;
            }

            IReadOnlyList<FrameEventRef> events = m_Port.FactsBySequence();
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
            int round = (int)((frame >> 4) & 3u);
            if ((frame & 15u) == 3u)
            {
                int railSourceCount = m_Port.RailSourceCount();
                for (int i = 0; i < railSourceCount; i++)
                {
                    (bool success, ManagedSourceVehicle source) =
                        m_Port.RailSource(i, frame);
                    if (success
                        && (source.State != VehicleState.Running
                            || (source.Vehicle.Index & 3) == round))
                    {
                        DiscoverSourceVehicle(source, frame);
                    }
                }
            }

            if ((frame & 15u) == 1u)
            {
                int roadSourceCount = m_Port.RoadSourceCount();
                for (int i = 0; i < roadSourceCount; i++)
                {
                    (bool success, ManagedSourceVehicle source) =
                        m_Port.RoadSource(i, frame);
                    if (success
                        && (source.State != VehicleState.Running
                            || (source.Vehicle.Index & 3) == round))
                    {
                        DiscoverSourceVehicle(source, frame);
                    }
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
            m_RoadRetries.Add(vehicle);
        }
        private void ResolveRoadRetry(Entity vehicle)
        {
            m_RoadRetries.Remove(vehicle);
        }
        private void CancelRoadRetry(Entity vehicle)
        {
            m_RoadRetries.Remove(vehicle);
        }

        private void DiscoverSourceVehicle(
            ManagedSourceVehicle source,
            uint frame)
        {
            if (source.Vehicle == Entity.Null
                || source.Line == Entity.Null
                || source.State != VehicleState.Running)
            {
                ForgetVehicle(source.Vehicle, "source-state-not-running");
                return;
            }

            if (m_States.TryGetValue(
                    source.Vehicle,
                    out VehicleSignalState state))
            {
                if (state.Line != source.Line)
                {
                    ForgetVehicle(source.Vehicle, "source-line-changed");
                    state = null;
                }
                else if (!LineEnabledThisFrame(source.Line))
                {
                    ForgetVehicle(source.Vehicle, "qualification-ended");
                    return;
                }
                else if (state.TargetCount != 0)
                {
                    return;
                }
                else if (!m_Port.TryStop(
                        source.Vehicle,
                        out bool hasSession,
                        out bool pending,
                        out _)
                    || (hasSession && !pending))
                {
                    ForgetVehicle(source.Vehicle, "qualification-ended");
                    return;
                }
                else
                {
                    RefreshVehicle(state, frame, false);
                    return;
                }
            }

            if (!m_Port.TryVehicle(
                    source.Vehicle,
                    out Entity line,
                    out TransitMode mode,
                    out VehicleState stateNow)
                || line != source.Line
                || stateNow != VehicleState.Running
                || (mode != TransitMode.Tram && mode != TransitMode.Bus)
                || !LineEnabledThisFrame(line)
                || !m_Port.TryStop(
                    source.Vehicle,
                    out bool sourceHasSession,
                    out bool sourcePending,
                    out _)
                || (sourceHasSession && !sourcePending))
            {
                ForgetVehicle(source.Vehicle, "qualification-ended");
                return;
            }

            state = new VehicleSignalState(source.Vehicle, line, mode);
            m_States.Add(source.Vehicle, state);
            RefreshVehicle(state, frame, false);
        }

        private void DiscoverVehicle(
            Entity vehicle,
            uint frame,
            bool requireTargetAdvance)
        {
            if (!TryGetEligible(
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
        }

        private bool TryGetEligible(
            Entity vehicle,
            out Entity line,
            out TransitMode mode,
            out int pendingWaypoint)
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

            if (!LineEnabledThisFrame(line))
                return false;
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

        private bool LineEnabledThisFrame(Entity line)
        {
            if (line == Entity.Null)
                return false;
            if (m_LineGateFrame != m_CurrentFrame)
            {
                m_LineGate.Clear();
                m_LineGateFrame = m_CurrentFrame;
            }
            if (!m_LineGate.TryGetValue(line, out bool enabled))
            {
                enabled = m_Port.LineEnabled(line);
                m_LineGate.Add(line, enabled);
            }
            return enabled;
        }

        private void RefreshVehicle(
            VehicleSignalState state,
            uint frame,
            bool requireTargetAdvance)
        {
            if (state.RefreshFrame == frame)
            {
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

            try
            {
                if (state.Mode == TransitMode.Bus)
                    RefreshRoad(state, frame, requireTargetAdvance);
                else
                    RefreshTram(state, frame);
            }
            finally
            {
                SyncDueBuckets(state);
            }
        }

        private void RefreshRoad(
            VehicleSignalState state,
            uint frame,
            bool requireTargetAdvance)
        {
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
                    if (TryReadSignal(
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
                if (!TryReadSignal(
                        target.SignalLane,
                        out SignalLaneFact signal))
                {
                    EndTarget(state, target, "qualification-ended", frame);
                    continue;
                }
                if (signal.Intersection != target.Intersection
                    || signal.GroupMask != target.GroupMask
                    || signal.UpdateFrame != target.UpdateFrame)
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
                    bool directTargetSignal = TryReadSignal(
                            target.SignalLane,
                            out SignalLaneFact signal)
                        && signal.Intersection == target.Intersection
                        && signal.GroupMask == target.GroupMask
                        && m_Signals.HasBusTargetSignalBlocker(
                            state.Vehicle,
                            signal);
                    if (advanced < BusProgressGuardMeters
                        && !directTargetSignal)
                        continue;
                    target.ProgressSuppressed = false;
                    target.ProgressAnchorFrame = frame;
                    target.ProgressAnchorDistance = target.DistanceMeters;
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
                    || !TryReadSignal(
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
                if (!TryReadSignal(
                        target.SignalLane,
                        out SignalLaneFact signal))
                {
                    EndTarget(state, target, "qualification-ended", frame);
                    continue;
                }
                if (signal.Intersection != target.Intersection
                    || signal.GroupMask != target.GroupMask
                    || signal.UpdateFrame != target.UpdateFrame)
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
                if (!TryReadSignal(
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
                    && (target.GroupMask != candidate.GroupMask
                        || target.UpdateFrame != candidate.UpdateFrame))
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
                    index++;
                }
                else
                {
                    EndTarget(state, target, absentReason, frame);
                }
            }

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

        private bool CandidateAllowed(
            DueCandidate candidate,
            uint frame)
        {
            if (candidate.Mode == TransitMode.Bus
                && candidate.Target.ProgressSuppressed)
            {
                return false;
            }
            if (!m_Intersections.TryGetValue(
                    candidate.Signal.Intersection,
                    out IntersectionState state))
            {
                return true;
            }
            return state.UpdateFrame == candidate.Signal.UpdateFrame
                && !IsCooling(state, candidate.Target.GroupMask, frame);
        }

        private static bool Better(
            DueCandidate candidate,
            DueCandidate current)
        {
            bool candidateTram = candidate.Mode == TransitMode.Tram;
            bool currentTram = current.Mode == TransitMode.Tram;
            if (candidateTram != currentTram)
                return candidateTram;
            bool candidateGreen = candidate.Signal.Signal == LaneSignalType.Go
                && candidate.Signal.CurrentGroupBit != 0
                && (candidate.Signal.CurrentGroupBit
                    & candidate.Target.GroupMask) != 0;
            bool currentGreen = current.Signal.Signal == LaneSignalType.Go
                && current.Signal.CurrentGroupBit != 0
                && (current.Signal.CurrentGroupBit
                    & current.Target.GroupMask) != 0;
            if (candidateGreen != currentGreen)
                return candidateGreen;
            if (candidate.Target.DistanceMeters != current.Target.DistanceMeters)
            {
                return candidate.Target.DistanceMeters
                    < current.Target.DistanceMeters;
            }
            return candidate.Target.GroupMask != current.Target.GroupMask
                ? candidate.Target.GroupMask < current.Target.GroupMask
                : CompareEntity(
                    candidate.Target.Vehicle,
                    current.Target.Vehicle) < 0;
        }

        private void SubmitWinner(
            DueCandidate candidate,
            uint frame)
        {
            VehicleSignalState owner = candidate.State;
            VehicleTarget target = candidate.Target;
            SignalLaneFact before = candidate.Signal;
            if (m_Intersections.TryGetValue(
                    before.Intersection,
                    out IntersectionState state)
                && state.UpdateFrame != before.UpdateFrame)
            {
                EndTarget(owner, target, "signal-identity-changed", frame);
                SyncDueBuckets(owner);
                return;
            }
            if (state != null
                && IsCooling(state, target.GroupMask, frame))
            {
                return;
            }

            bool tram = candidate.Mode == TransitMode.Tram;
            sbyte priority = tram ? TramPriority : BusPriority;
            if (!m_Signals.TrySubmit(before, priority))
                return;

            if (state == null)
                state = GetIntersection(before.Intersection, before.UpdateFrame);
            state.SelectedGroup = target.GroupMask;
            state.SelectedMaxPriorityFrames = tram
                ? TramMaxPriorityFrames
                : BusMaxPriorityFrames;
            state.SelectedLane = before.Lane;
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
                    state.MaxPriorityFrames = state.SelectedMaxPriorityFrames;
                }
                else if (tram
                    && state.MaxPriorityFrames > TramMaxPriorityFrames)
                {
                    state.MaxPriorityFrames = TramMaxPriorityFrames;
                }
            }
            SyncIntersectionBucket(before.Intersection, state);

#if RT_DEBUG_TOOLS
            if (SignalPriorityLogEnabled())
            {
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
            }
#endif
        }

        private IntersectionState GetIntersection(
            Entity intersection,
            uint updateFrame)
        {
            if (!m_Intersections.TryGetValue(
                    intersection,
                    out IntersectionState state))
            {
                state = new IntersectionState { UpdateFrame = updateFrame };
                m_Intersections.Add(intersection, state);
            }
            return state;
        }

        private static void AddCooldown(
            IntersectionState state,
            ushort groupMask,
            uint endFrame)
        {
            if (groupMask == 0)
                return;
            uint untilFrame = endFrame + GroupCooldownFrames;
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
            uint frame)
        {
            CleanupCooldowns(state, frame);
            for (int i = 0; i < state.Cooldowns.Count; i++)
            {
                if ((state.Cooldowns[i].GroupMask & groupMask) != 0)
                {
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

        private void SyncDueBuckets(VehicleSignalState state)
        {
            ushort nextMask = 0;
            for (int i = 0; i < state.TargetCount; i++)
            {
                uint updateFrame = state.Targets[i].UpdateFrame;
                if (updateFrame < 16u)
                    nextMask |= (ushort)(1 << (int)updateFrame);
            }
            ushort changed = (ushort)(state.DueMask ^ nextMask);
            if (changed == 0)
                return;
            for (int i = 0; i < 16; i++)
            {
                ushort bit = (ushort)(1 << i);
                if ((changed & bit) == 0)
                    continue;
                if ((nextMask & bit) != 0)
                    m_DueBuckets[i].Add(state.Vehicle);
                else
                    m_DueBuckets[i].Remove(state.Vehicle);
            }
            state.DueMask = nextMask;
        }

        private void RemoveDueBuckets(VehicleSignalState state)
        {
            ushort mask = state.DueMask;
            for (int i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) != 0)
                    m_DueBuckets[i].Remove(state.Vehicle);
            }
            state.DueMask = 0;
        }

        private void SyncIntersectionBucket(
            Entity intersection,
            IntersectionState state)
        {
            if (state.UpdateFrame >= 16u)
                return;
            int bucket = (int)state.UpdateFrame;
            if (state.PriorityGroup != 0
                || state.SelectedGroup != 0
                || state.Cooldowns.Count != 0)
            {
                m_IntersectionBuckets[bucket].Add(intersection);
                return;
            }
            m_IntersectionBuckets[bucket].Remove(intersection);
            m_Intersections.Remove(intersection);
        }

        private void RemovePostObservations(Entity vehicle)
        {
#if RT_DEBUG_TOOLS
            for (int i = m_PostObservations.Count - 1; i >= 0; i--)
            {
                VehicleTarget target = m_PostObservations[i].Target;
                if (target != null && target.Vehicle == vehicle)
                    m_PostObservations.RemoveAt(i);
            }
#endif
        }

        private void RemovePostTarget(VehicleTarget target)
        {
#if RT_DEBUG_TOOLS
            for (int i = m_PostObservations.Count - 1; i >= 0; i--)
            {
                if (m_PostObservations[i].Target == target)
                    m_PostObservations.RemoveAt(i);
            }
#endif
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

#if RT_DEBUG_TOOLS
            if (SignalPriorityLogEnabled())
                LogTargetEnd(state, target, reason);
#endif
            RemovePostTarget(target);
            int last = state.TargetCount - 1;
            for (int i = index; i < last; i++)
                state.Targets[i] = state.Targets[i + 1];
            state.Targets[last] = target;
            state.TargetCount = last;
            target.Reset();

        }

#if RT_DEBUG_TOOLS
        private void LogTargetEnd(
            VehicleSignalState state,
            VehicleTarget target,
            string reason)
        {
            if (!target.Submitted)
                return;

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
