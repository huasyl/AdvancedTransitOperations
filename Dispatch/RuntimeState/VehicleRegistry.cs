using System;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistry
    {
        private readonly VehicleStateStore m_Store;
        private readonly VehicleWorksets m_Worksets;
        private readonly FrameEvents m_Events;
        private readonly Func<uint> m_Frame;
        private readonly Func<double, uint> m_ToFramesCeil;
        private readonly Func<Entity, TransitMode> m_ModeOfLine;
        private readonly Action<StopFact> m_PublishStopFact;
        private RuntimeWorksets m_RuntimeWorksets;
        private bool m_Restoring;
        private Entity m_RestoreVehicle;
        private VehicleFactKind m_RestoreFactKind;
        private Entity m_RestorePreviousLine;
        private ulong m_RestoreSourceGeneration;

        public VehicleRegistry(VehicleStateStore store, VehicleWorksets worksets, FrameEvents events, Func<uint> frame,
            Func<double, uint> toFramesCeil, Func<Entity, TransitMode> modeOfLine, Action<StopFact> publishStopFact)
        {
            m_Store = store;
            m_Worksets = worksets;
            m_Events = events;
            m_Frame = frame;
            m_ToFramesCeil = toFramesCeil;
            m_ModeOfLine = modeOfLine;
            m_PublishStopFact = publishStopFact;
        }

        // RuntimeWorksets 按组合根顺序创建后一次性绑定。
        public void BindWorksets(RuntimeWorksets worksets) => m_RuntimeWorksets = worksets;

        public void Track(Entity vehicle, Entity line)
        {
            if (vehicle == Entity.Null)
                return;

            if (m_Store.Line.TryGetValue(vehicle, out Entity oldLine))
                m_Worksets.RemoveMode(vehicle);
            m_Store.Line[vehicle] = line;
            m_Worksets.AddMode(vehicle, m_ModeOfLine(line));
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
            {
                if (oldLine != Entity.Null)
                    m_RuntimeWorksets?.MarkDirty(oldLine);
                m_RuntimeWorksets?.MarkDirty(line);
            }
        }

        public void SetState(Entity vehicle, VehicleState state, ulong sourceGeneration = 0UL)
        {
            if (vehicle == Entity.Null)
                return;

            VehicleState previous = m_Store.State.TryGetValue(vehicle, out VehicleState existing)
                ? existing
                : default;
            if (m_Store.State.TryGetValue(vehicle, out existing) && existing == state)
                return;
            if (m_Store.State.TryGetValue(vehicle, out VehicleState oldState))
                m_Worksets.RemoveState(vehicle, oldState);
            m_Store.State[vehicle] = state;
            m_Worksets.AddState(vehicle, state);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, previous, state, ReadLine(vehicle), sourceGeneration: sourceGeneration);
        }

        public void SetTarget(Entity vehicle, int targetMinute, ulong sourceGeneration = 0UL)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.TargetMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == targetMinute) return;
            m_Store.TargetMinute[vehicle] = targetMinute;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddHoldingLineCandidates(ReadLine(vehicle));
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Target, default, default, ReadLine(vehicle), previous, targetMinute, sourceGeneration: sourceGeneration);
        }

        public void ClearTarget(Entity vehicle, ulong sourceGeneration = 0UL)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.TargetMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == -1) return;
            m_Store.TargetMinute[vehicle] = -1;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddHoldingLineCandidates(ReadLine(vehicle));
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Target, default, default, ReadLine(vehicle), previous, -1, sourceGeneration: sourceGeneration);
        }

        public void SetSlot(Entity vehicle, int slotMinute, ulong sourceGeneration = 0UL)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.CurrentSlotMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == slotMinute) return;
            m_Store.CurrentSlotMinute[vehicle] = slotMinute;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddHoldingLineCandidates(ReadLine(vehicle));
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Slot, default, default, ReadLine(vehicle), previous, slotMinute, sourceGeneration: sourceGeneration);
        }

        public void ClearSlot(Entity vehicle, ulong sourceGeneration = 0UL)
        {
            if (vehicle == Entity.Null)
                return;

            if (!m_Store.CurrentSlotMinute.TryGetValue(vehicle, out int previous)) return;
            m_Store.CurrentSlotMinute.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddHoldingLineCandidates(ReadLine(vehicle));
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Slot, default, default, ReadLine(vehicle), previous, -1, sourceGeneration: sourceGeneration);
        }

        public void SetIdle(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame[vehicle] = frame;
            uint idleDeadline = unchecked(frame + m_ToFramesCeil(ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES));
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.Idle, idleDeadline);
        }

        public void ClearIdle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.Idle);
        }

        public void SetPreparing(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame[vehicle] = frame;
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void ClearPreparing(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void SetLaunch(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame[vehicle] = frame;
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void ClearLaunch(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void SetCooldown(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LaunchCooldownUntil[vehicle] = frame;
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.LaunchCooldown, frame);
        }

        public void ClearCooldown(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LaunchCooldownUntil.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.LaunchCooldown);
        }

        public void SetDispatch(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.DispatchRequestStartFrame[vehicle] = frame;
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void ClearDispatch(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.DispatchRequestStartFrame.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void MarkInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Add(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddIdleLineCandidates(ReadLine(vehicle));
        }

        public void ClearInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.AddIdleLineCandidates(ReadLine(vehicle));
        }

        public void SetOriginCandidate(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame[vehicle] = frame;
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.OriginSettle, unchecked(frame + 180u));
            m_RuntimeWorksets?.SetOriginCandidate(vehicle, true);
        }

        public void ClearOriginCandidate(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.OriginSettle);
            m_RuntimeWorksets?.SetOriginCandidate(vehicle, false);
        }

        public void SetReady(
            Entity vehicle,
            uint startFrame,
            double waitMinutes,
            ClockSnapshot clockSnapshot)
        {
            if (vehicle == Entity.Null)
                return;

            uint readyFrame = unchecked(startFrame + clockSnapshot.ToFramesCeil(waitMinutes));
            m_Store.ForcedOriginReadyFrame[vehicle] = new ReadyClockState(startFrame, waitMinutes, readyFrame);
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.Ready, readyFrame);
        }

        public void ReprojectReady(
            uint nowFrame,
            ClockSnapshot oldClockSnapshot,
            ClockSnapshot newClockSnapshot)
        {
            NativeArray<Entity> vehicles = m_Store.ForcedOriginReadyFrame.GetKeyArray(Allocator.Temp);
            try
            {
                for (int vehicleIndex = 0; vehicleIndex < vehicles.Length; vehicleIndex++)
                {
                    Entity vehicle = vehicles[vehicleIndex];
                    if (!m_Store.ForcedOriginReadyFrame.TryGetValue(vehicle, out ReadyClockState readyState))
                        continue;

                    uint elapsedFrames = unchecked(nowFrame - readyState.StartFrame);
                    double elapsedMinutes = oldClockSnapshot.ToMinutes(elapsedFrames);
                    double remainingMinutes = Math.Max(0d, readyState.WaitMinutes - elapsedMinutes);
                    uint readyFrame = unchecked(nowFrame + newClockSnapshot.ToFramesCeil(remainingMinutes));
                    m_Store.ForcedOriginReadyFrame[vehicle] =
                        new ReadyClockState(nowFrame, remainingMinutes, readyFrame);
                    m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.Ready, readyFrame);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        public void ReprojectIdle(ClockSnapshot clockSnapshot)
        {
            NativeArray<Entity> vehicles = m_Store.IdleStartFrame.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Store.IdleStartFrame.TryGetValue(vehicle, out uint startFrame)) continue;
                    uint deadline = unchecked(startFrame + clockSnapshot.ToFramesCeil(ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES));
                    m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.Idle, deadline);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        public void ClearReady(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.ForcedOriginReadyFrame.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.Ready);
        }

        public void SetBoardingGrace(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.ForcedOriginBoardingGraceUntil[vehicle] = frame;
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.OriginBoardingGrace, frame);
        }

        public void ClearBoardingGrace(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.ForcedOriginBoardingGraceUntil.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.OriginBoardingGrace);
        }

        public void Remove(Entity vehicle)
        {
            Entity line = ReadLine(vehicle);
            VehicleState state = m_Store.State.TryGetValue(vehicle, out VehicleState existingState)
                ? existingState
                : default;
            if (vehicle != Entity.Null && !m_Restoring)
            {
                uint frame = m_Frame();
                m_Events.AppendVehicle(vehicle, frame, VehicleFactKind.Removed, line: line, state: state);
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Removed, default, default, line);
                m_PublishStopFact(new StopFact(StopFactKind.Removed, vehicle, line, -1, frame));
            }
            m_Worksets.RemoveMode(vehicle);
            if (m_Store.State.TryGetValue(vehicle, out state))
                m_Worksets.RemoveState(vehicle, state);
            m_RuntimeWorksets?.ClearVehicle(vehicle);
            m_Store.Remove(vehicle);
            if (!m_Restoring)
            {
                m_RuntimeWorksets?.AddCandidate(vehicle);
                m_RuntimeWorksets?.MarkDirty(line);
            }
        }

        public void Clear()
        {
            m_Worksets.ResetCity();
            m_RuntimeWorksets?.ResetCity();
            m_Events.ResetCity();
            m_Store.Clear();
        }

        public void BeginRestore(Entity vehicle, ulong sourceGeneration)
        {
            m_Restoring = true;
            m_RestoreVehicle = vehicle;
            m_RestoreFactKind = VehicleFactKind.Registered;
            m_RestorePreviousLine = Entity.Null;
            m_RestoreSourceGeneration = sourceGeneration;
        }

        public void BeginRebind(Entity vehicle, Entity previousLine, ulong sourceGeneration)
        {
            m_Restoring = true;
            m_RestoreVehicle = vehicle;
            m_RestoreFactKind = VehicleFactKind.Rebound;
            m_RestorePreviousLine = previousLine;
            m_RestoreSourceGeneration = sourceGeneration;
        }

        public void EndRestore(Entity line)
        {
            if (!m_Restoring)
                return;

            Entity vehicle = m_RestoreVehicle;
            VehicleFactKind factKind = m_RestoreFactKind;
            Entity previousLine = m_RestorePreviousLine;
            ulong sourceGeneration = m_RestoreSourceGeneration;
            m_Restoring = false;
            m_RestoreVehicle = Entity.Null;
            m_Events.AppendVehicle(
                vehicle,
                m_Frame(),
                factKind,
                previousLine: factKind == VehicleFactKind.Rebound ? previousLine : Entity.Null,
                line: line,
                state: m_Store.State.TryGetValue(vehicle, out VehicleState state) ? state : default,
                sourceGeneration: sourceGeneration);
            if (m_Store.State.TryGetValue(vehicle, out VehicleState restoredState))
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, default, restoredState, line, sourceGeneration: sourceGeneration);
            m_RestoreFactKind = default;
            m_RestorePreviousLine = Entity.Null;
            m_RestoreSourceGeneration = 0UL;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.MarkDirty(line);
        }

        public void CancelRestore()
        {
            m_Restoring = false;
            m_RestoreVehicle = Entity.Null;
            m_RestoreFactKind = default;
            m_RestorePreviousLine = Entity.Null;
            m_RestoreSourceGeneration = 0UL;
        }

        private Entity ReadLine(Entity vehicle) => m_Store.Line.TryGetValue(vehicle, out Entity line) ? line : Entity.Null;
    }
}
