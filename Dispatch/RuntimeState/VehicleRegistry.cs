using System;
using System.Collections.Generic;
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
        private RuntimeWorksets m_RuntimeWorksets;
        private bool m_Restoring;
        private Entity m_RestoreVehicle;

        public VehicleRegistry(VehicleStateStore store, VehicleWorksets worksets, FrameEvents events, Func<uint> frame,
            Func<double, uint> toFramesCeil, Func<Entity, TransitMode> modeOfLine)
        {
            m_Store = store;
            m_Worksets = worksets;
            m_Events = events;
            m_Frame = frame;
            m_ToFramesCeil = toFramesCeil;
            m_ModeOfLine = modeOfLine;
        }

        // RuntimeWorksets 按组合根顺序在 RailEventSource 后创建，随后一次性绑定。
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

        public void SetState(Entity vehicle, VehicleState state)
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
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, previous, state, ReadLine(vehicle));
        }

        public void SetTarget(Entity vehicle, int targetMinute)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.TargetMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == targetMinute) return;
            m_Store.TargetMinute[vehicle] = targetMinute;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Target, default, default, ReadLine(vehicle), previous, targetMinute);
        }

        public void ClearTarget(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.TargetMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == -1) return;
            m_Store.TargetMinute[vehicle] = -1;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Target, default, default, ReadLine(vehicle), previous, -1);
        }

        public void SetSlot(Entity vehicle, int slotMinute)
        {
            if (vehicle == Entity.Null)
                return;

            int previous = m_Store.CurrentSlotMinute.TryGetValue(vehicle, out int old) ? old : -1;
            if (previous == slotMinute) return;
            m_Store.CurrentSlotMinute[vehicle] = slotMinute;
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Slot, default, default, ReadLine(vehicle), previous, slotMinute);
        }

        public void ClearSlot(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            if (!m_Store.CurrentSlotMinute.TryGetValue(vehicle, out int previous)) return;
            m_Store.CurrentSlotMinute.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(ReadLine(vehicle));
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Slot, default, default, ReadLine(vehicle), previous, -1);
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
        }

        public void ClearInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Remove(vehicle);
            m_RuntimeWorksets?.AddCandidate(vehicle);
        }

        public void SetOriginCandidate(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame[vehicle] = frame;
            m_RuntimeWorksets?.SetDeadline(vehicle, DeadlineKind.OriginSettle, unchecked(frame + 180u));
        }

        public void ClearOriginCandidate(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame.Remove(vehicle);
            m_RuntimeWorksets?.ClearDeadline(vehicle, DeadlineKind.OriginSettle);
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
            if (vehicle != Entity.Null && !m_Restoring)
            {
                m_Events.AppendVehicle(vehicle, m_Frame(), VehicleFactKind.Removed);
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.Removed, default, default, line);
            }
            m_Worksets.RemoveMode(vehicle);
            if (m_Store.State.TryGetValue(vehicle, out VehicleState state))
                m_Worksets.RemoveState(vehicle, state);
            m_RuntimeWorksets?.ClearVehicle(vehicle);
            m_Store.Remove(vehicle);
            if (!m_Restoring)
                m_RuntimeWorksets?.MarkDirty(line);
        }

        public void Clear()
        {
            m_Worksets.ResetCity();
            m_RuntimeWorksets?.ResetCity();
            m_Events.ResetCity();
            m_Store.Clear();
        }

        public void BeginRestore(Entity vehicle)
        {
            m_Restoring = true;
            m_RestoreVehicle = vehicle;
        }

        public void EndRestore(Entity line)
        {
            if (!m_Restoring)
                return;

            Entity vehicle = m_RestoreVehicle;
            m_Restoring = false;
            m_RestoreVehicle = Entity.Null;
            m_Events.AppendVehicle(vehicle, m_Frame(), VehicleFactKind.Registered);
            if (m_Store.State.TryGetValue(vehicle, out VehicleState state))
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, default, state, line);
            m_RuntimeWorksets?.AddCandidate(vehicle);
            m_RuntimeWorksets?.MarkDirty(line);
        }

        public void CancelRestore()
        {
            m_Restoring = false;
            m_RestoreVehicle = Entity.Null;
        }

        // 只读审查入口：不创建 ECS 写入，也不修正任何索引。
        public bool IsWorksetConsistent(Entity vehicle)
        {
            if (!m_Store.State.TryGetValue(vehicle, out VehicleState state)
                || !m_Store.Line.TryGetValue(vehicle, out Entity line)
                )
            {
                return false;
            }

            return m_Worksets.HasOnlyState(vehicle, state)
                && m_Worksets.HasOnlyMode(vehicle, m_ModeOfLine(line));
        }

        // 只读审查入口：逐完整 Entity 比较权威 Store 与两类派生桶，不修复任何异常。
        public bool AreWorksetsConsistent()
        {
            NativeArray<Entity> stateKeys = m_Store.State.GetKeyArray(Allocator.Temp);
            NativeArray<Entity> lineKeys = m_Store.Line.GetKeyArray(Allocator.Temp);
            try
            {
                var states = new HashSet<Entity>();
                var lines = new HashSet<Entity>();
                for (int i = 0; i < stateKeys.Length; i++)
                {
                    Entity vehicle = stateKeys[i];
                    if (!states.Add(vehicle) || !IsWorksetConsistent(vehicle)) return false;
                }
                for (int i = 0; i < lineKeys.Length; i++)
                {
                    Entity vehicle = lineKeys[i];
                    if (!lines.Add(vehicle) || !m_Store.Line.TryGetValue(vehicle, out Entity line)
                        || !m_Worksets.HasOnlyMode(vehicle, m_ModeOfLine(line)))
                    {
                        return false;
                    }
                }
                return states.SetEquals(lines)
                    && m_Worksets.MatchesStateKeys(states)
                    && m_Worksets.MatchesModeKeys(lines);
            }
            finally
            {
                stateKeys.Dispose();
                lineKeys.Dispose();
            }
        }

        private Entity ReadLine(Entity vehicle) => m_Store.Line.TryGetValue(vehicle, out Entity line) ? line : Entity.Null;
    }
}
