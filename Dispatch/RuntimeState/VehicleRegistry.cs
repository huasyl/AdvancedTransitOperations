using System;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Runtime;
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
        private RuntimeFramePlan m_FramePlan;
        private Action<Entity, RuntimeDemandMask, bool> m_SetDemand;
        private Action<Entity> m_MarkSchedulerDirty;
        private bool m_Restoring;

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

        public void BindFramePlan(
            RuntimeFramePlan framePlan,
            Action<Entity, RuntimeDemandMask, bool> setDemand = null,
            Action<Entity> markSchedulerDirty = null)
        {
            m_FramePlan = framePlan;
            m_SetDemand = setDemand;
            m_MarkSchedulerDirty = markSchedulerDirty;
        }

        public void Track(Entity vehicle, Entity line)
        {
            if (vehicle == Entity.Null)
                return;

            if (m_Store.Line.TryGetValue(vehicle, out Entity oldLine))
            {
                if (oldLine != line)
                    ClearTheoryWait(vehicle);
                m_Worksets.RemoveMode(vehicle);
                m_Worksets.RemoveLine(vehicle, oldLine);
            }
            m_Store.Line[vehicle] = line;
            m_Worksets.AddMode(vehicle, m_ModeOfLine(line));
            m_Worksets.AddLine(vehicle, line);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Stop | RuntimeStageMask.Dispatch);
            if (!m_Restoring)
            {
                if (oldLine != Entity.Null)
                    MarkSchedulerDirty(oldLine);
                MarkSchedulerDirty(line);
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
            if (previous == VehicleState.Preparing && state != VehicleState.Preparing)
                ClearTheoryWait(vehicle);
            m_Worksets.AddState(vehicle, state);
            if (state == VehicleState.Retiring)
                m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Retire);
            else
                m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
            if (!m_Restoring)
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, previous, state, ReadLine(vehicle));
        }

        public void SetIdle(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame[vehicle] = frame;
            uint idleDeadline = unchecked(frame + m_ToFramesCeil(ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES));
            m_FramePlan?.SetDeadline(vehicle, DeadlineKind.Idle, idleDeadline);
        }

        public void ClearIdle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame.Remove(vehicle);
            m_FramePlan?.ClearDeadline(vehicle, DeadlineKind.Idle);
        }

        public void SetPreparingStartFrame(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame[vehicle] = frame;
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void SetEstimatedOriginArrivalFrame(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            ClearTheoryWait(vehicle);
            m_Store.EstimatedOriginArrivalFrame[vehicle] = frame;
        }

        public void SetTheoryWait(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.EstimatedOriginArrivalFrame.Remove(vehicle);
            m_Store.WaitingForTheory[vehicle] = frame;
        }

        public void ClearTheoryWait(Entity vehicle) => m_Store.WaitingForTheory.Remove(vehicle);

        public void ClearPreparingData(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame.Remove(vehicle);
            m_Store.EstimatedOriginArrivalFrame.Remove(vehicle);
            ClearTheoryWait(vehicle);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void SetLaunch(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame[vehicle] = frame;
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void ClearLaunch(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame.Remove(vehicle);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void SetSpawnOrderRequestFrame(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.SpawnOrderRequestFrame[vehicle] = frame;
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void ClearSpawnOrderRequestFrame(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.SpawnOrderRequestFrame.Remove(vehicle);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void MarkInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Add(vehicle);
            m_SetDemand?.Invoke(vehicle, RuntimeDemandMask.InboundWatch, true);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void ClearInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Remove(vehicle);
            m_SetDemand?.Invoke(vehicle, RuntimeDemandMask.InboundWatch, false);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void ReprojectIdle(uint nowFrame, ClockSnapshot oldClock, ClockSnapshot newClock)
        {
            NativeArray<Entity> vehicles = m_Store.IdleStartFrame.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Store.IdleStartFrame.TryGetValue(vehicle, out uint startFrame)) continue;
                    double elapsed = Math.Min(ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES,
                        oldClock.ToMinutes(unchecked(nowFrame - startFrame)));
                    uint rebasedStart = unchecked(nowFrame - newClock.ToFramesRound(elapsed));
                    m_Store.IdleStartFrame[vehicle] = rebasedStart;
                    uint deadline = unchecked(rebasedStart + newClock.ToFramesCeil(ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES));
                    m_FramePlan?.SetDeadline(vehicle, DeadlineKind.Idle, deadline);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        public void Remove(Entity vehicle)
        {
            Entity line = ReadLine(vehicle);
            VehicleState state = m_Store.State.TryGetValue(vehicle, out VehicleState existingState)
                ? existingState
                : default;
            if (vehicle != Entity.Null)
            {
                uint frame = m_Frame();
                m_Events.AppendLifecycle(vehicle, frame, LifecycleFactKind.Removed, line: line, state: state);
                m_PublishStopFact(new StopFact(StopFactKind.Removed, vehicle, line, -1, frame));
            }
            m_Worksets.RemoveMode(vehicle);
            m_Worksets.RemoveLine(vehicle, line);
            if (m_Store.State.TryGetValue(vehicle, out state))
                m_Worksets.RemoveState(vehicle, state);
            m_FramePlan?.ClearVehicle(vehicle);
            m_SetDemand?.Invoke(vehicle, RuntimeDemandMask.DeparturePending | RuntimeDemandMask.InboundWatch, false);
            m_Store.Remove(vehicle);
            MarkSchedulerDirty(line);
        }

        public void Clear()
        {
            m_Worksets.ResetCity();
            m_FramePlan?.ResetCity();
            m_Events.ResetCity();
            m_Store.Clear();
        }

        public void BeginRestore()
        {
            m_Restoring = true;
        }

        public void EndRestore(Entity vehicle, Entity line)
        {
            if (!m_Restoring)
                return;

            m_Restoring = false;
            m_Events.AppendLifecycle(
                vehicle,
                m_Frame(),
                LifecycleFactKind.Registered,
                line: line,
                state: m_Store.State.TryGetValue(vehicle, out VehicleState state) ? state : default);
            if (m_Store.State.TryGetValue(vehicle, out VehicleState restoredState))
                m_Events.AppendDispatch(vehicle, m_Frame(), DispatchFactKind.State, default, restoredState, line);
            m_FramePlan?.AddStage(vehicle, RuntimeStageMask.Stop | RuntimeStageMask.Dispatch);
            MarkSchedulerDirty(line);
        }

        public void CancelRestore()
        {
            m_Restoring = false;
        }

        private void MarkSchedulerDirty(Entity line) => m_MarkSchedulerDirty?.Invoke(line);

        private Entity ReadLine(Entity vehicle) => m_Store.Line.TryGetValue(vehicle, out Entity line) ? line : Entity.Null;
    }
}
