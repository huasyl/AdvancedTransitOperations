using System;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class VehicleRegistry
    {
        private readonly VehicleStateStore m_Store;

        public VehicleRegistry(VehicleStateStore store)
        {
            m_Store = store;
        }

        public void Track(Entity vehicle, Entity line)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.Line[vehicle] = line;
        }

        public void SetState(Entity vehicle, VehicleState state)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.State[vehicle] = state;
        }

        public void SetTarget(Entity vehicle, int targetMinute)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.TargetMinute[vehicle] = targetMinute;
        }

        public void ClearTarget(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.TargetMinute[vehicle] = -1;
        }

        public void SetSlot(Entity vehicle, int slotMinute)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.CurrentSlotMinute[vehicle] = slotMinute;
        }

        public void ClearSlot(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.CurrentSlotMinute.Remove(vehicle);
        }

        public void SetIdle(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame[vehicle] = frame;
        }

        public void ClearIdle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.IdleStartFrame.Remove(vehicle);
        }

        public void SetPreparing(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame[vehicle] = frame;
        }

        public void ClearPreparing(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.PreparingStartFrame.Remove(vehicle);
        }

        public void SetLaunch(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame[vehicle] = frame;
        }

        public void ClearLaunch(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LastLaunchFrame.Remove(vehicle);
        }

        public void SetCooldown(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LaunchCooldownUntil[vehicle] = frame;
        }

        public void ClearCooldown(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.LaunchCooldownUntil.Remove(vehicle);
        }

        public void SetDispatch(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.DispatchRequestStartFrame[vehicle] = frame;
        }

        public void ClearDispatch(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.DispatchRequestStartFrame.Remove(vehicle);
        }

        public void MarkInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Add(vehicle);
        }

        public void ClearInbound(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.NearingTerminus.Remove(vehicle);
        }

        public void SetOriginCandidate(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame[vehicle] = frame;
        }

        public void ClearOriginCandidate(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.OriginArrivalCandidateSinceFrame.Remove(vehicle);
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
        }

        public void SetBoardingGrace(Entity vehicle, uint frame)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.ForcedOriginBoardingGraceUntil[vehicle] = frame;
        }

        public void ClearBoardingGrace(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_Store.ForcedOriginBoardingGraceUntil.Remove(vehicle);
        }

        public void Remove(Entity vehicle)
        {
            m_Store.Remove(vehicle);
        }

        public void Clear()
        {
            m_Store.Clear();
        }
    }
}
