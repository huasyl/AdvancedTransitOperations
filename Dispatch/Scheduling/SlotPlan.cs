using System;
using System.Collections.Generic;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Scheduling
{
    internal sealed class SlotPlan
    {
        internal enum Status
        {
            None,
            Claimed,
            Skip
        }

        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly SchedulePolicy m_Policy;
        private readonly Func<Entity, bool> m_Managed;
        private readonly Func<Entity, int[]> m_Times;
        private readonly Func<Entity, int> m_Hold;
        private readonly Func<Entity, Entity> m_ResolveRuntimeControllerVehicle;

        public SlotPlan(
            DispatchRuntimeSystem runtime,
            SchedulePolicy policy,
            Func<Entity, bool> managed,
            Func<Entity, int[]> times,
            Func<Entity, int> hold,
            Func<Entity, Entity> resolveRuntimeControllerVehicle)
        {
            m_Runtime = runtime;
            m_Policy = policy;
            m_Managed = managed;
            m_Times = times;
            m_Hold = hold;
            m_ResolveRuntimeControllerVehicle = resolveRuntimeControllerVehicle;
        }

        public Status Build(
            LineTick tick,
            VehiclePick.Result pick,
            int slotMinute,
            Entity holder,
            string lineTag,
            int cycleMinutes,
            List<DispatchScheduler.SlotClaim> claims)
        {
            if (holder != Entity.Null && pick.Vehicle == holder)
                return Status.Skip;

            if (pick.Vehicle == Entity.Null)
                return Status.None;

            VehicleState bestState = m_Runtime.m_VehicleView.GetState(pick.Vehicle);
            m_Runtime.m_RuntimeLog.CrossLineCandidate(
                tick.Line,
                pick.Vehicle,
                bestState,
                slotMinute,
                pick.EtaFrames,
                pick.PreviousTargetMinute);
            if (bestState == VehicleState.Idle || bestState == VehicleState.Holding)
            {
                if (pick.PreviousTargetMinute < 0 && HoldingFull(tick, cycleMinutes))
                    return Status.Skip;

                claims.Add(new DispatchScheduler.SlotClaim(pick.Vehicle, slotMinute, holder, commitHold: true, clearIdle: true));
                return Status.Claimed;
            }

            claims.Add(new DispatchScheduler.SlotClaim(pick.Vehicle, slotMinute, holder, commitHold: false, clearIdle: false));
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[调度候选] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slotMinute)
                    + " 选择车辆" + pick.Vehicle.Index
                    + " state=" + bestState
                    + " eta=" + m_Runtime.m_SimClock.ToMinutes(pick.EtaFrames).ToString("F1") + "分钟"
                    + " prevTarget=" + (pick.PreviousTargetMinute >= 0 ? DispatchRuntimeSystem.SlotStr(pick.PreviousTargetMinute) : "-"));
            }
            return Status.Claimed;
        }

        public bool TryAssignCurrentOrLateSlot(
            Entity line,
            Entity vehicle,
            int nowMinute,
            string lineTag,
            string stateTag,
            out Entity releasedVehicle,
            out int lateSlotMinute)
        {
            releasedVehicle = Entity.Null;
            lateSlotMinute = -1;
            int previousSlotMinute = ScheduleClock.PreviousSlot(nowMinute);
            if (!ScheduleClock.CurrentOrRecent(nowMinute, previousSlotMinute))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, previousSlotMinute))
                return false;

            if (!TryResolveReleasedVehicle(line, vehicle, previousSlotMinute, lineTag, out releasedVehicle))
                return false;

            lateSlotMinute = previousSlotMinute;
            if (ScheduleClock.CanLate(nowMinute, previousSlotMinute))
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousSlotMinute + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousSlotMinute)
                        + " 已过期" + ScheduleClock.Overdue(nowMinute, previousSlotMinute) + "分钟");
            }

            return true;
        }

        public bool TryAssignUpcomingTarget(
            Entity line,
            Entity vehicle,
            int nowMinute,
            string lineTag,
            string stateTag,
            out int assignedTargetMinute)
        {
            assignedTargetMinute = -1;
            if (line == Entity.Null || vehicle == Entity.Null || !m_Managed(line))
                return false;

            int[] appliedTargets = m_Times(line);
            int nextTargetMinute = ScheduleTargets.Next(nowMinute, appliedTargets);
            if (nextTargetMinute < 0 || ScheduleClock.CurrentOrRecent(nowMinute, nextTargetMinute))
                return false;

            int waitMinutes = ScheduleClock.MinutesUntil(nowMinute, nextTargetMinute);
            if (waitMinutes > m_Hold(line))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, nextTargetMinute))
                return false;

            assignedTargetMinute = nextTargetMinute;
            if (RtLog.VerboseEnabled)
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "UpcomingTarget|" + nextTargetMinute + "|" + stateTag,
                    "[预分配] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 预分配未来班次" + DispatchRuntimeSystem.SlotStr(nextTargetMinute)
                        + " 距今" + waitMinutes + "分钟");
            }
            return true;
        }

        public bool TryAssignCurrentOrLateScheduledTarget(
            Entity line,
            Entity vehicle,
            int nowMinute,
            string lineTag,
            string stateTag,
            IReadOnlyList<int> targets,
            out Entity releasedVehicle,
            out int lateTarget)
        {
            releasedVehicle = Entity.Null;
            lateTarget = -1;
            int previousTargetMinute = ScheduleTargets.Previous(nowMinute, targets);
            if (previousTargetMinute < 0 || !ScheduleClock.CurrentOrRecent(nowMinute, previousTargetMinute))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, previousTargetMinute))
                return false;

            if (!TryResolveReleasedVehicle(line, vehicle, previousTargetMinute, lineTag, out releasedVehicle))
                return false;

            lateTarget = previousTargetMinute;
            if (ScheduleClock.CanLate(nowMinute, previousTargetMinute))
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousTargetMinute + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousTargetMinute)
                        + " 已过期" + ScheduleClock.Overdue(nowMinute, previousTargetMinute) + "分钟");
            }

            return true;
        }

        private bool HoldingFull(LineTick tick, int cycleMinutes)
        {
            int holdingCount = 0;
            for (int i = 0; i < tick.Vehicles.Count; i++)
            {
                Entity vehicle = tick.Vehicles[i];
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state))
                    continue;
                if (state == VehicleState.Holding)
                {
                    holdingCount++;
                    continue;
                }
                if (state == VehicleState.Idle
                    && m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMinute)
                    && targetMinute >= 0)
                {
                    holdingCount++;
                }
            }

            int holdingCap = Math.Max(1, tick.HoldMinutes / cycleMinutes);
            return holdingCount >= holdingCap;
        }

        private bool TryResolveReleasedVehicle(
            Entity line,
            Entity vehicle,
            int target,
            string lineTag,
            out Entity releasedVehicle)
        {
            releasedVehicle = Entity.Null;
            BufferLookup<RouteVehicle> rvBuffers = m_Runtime.GetBufferLookup<RouteVehicle>(true);
            if (!rvBuffers.TryGetBuffer(line, out DynamicBuffer<RouteVehicle> rvs))
                return false;

            for (int i = 0; i < rvs.Length; i++)
            {
                Entity other = m_ResolveRuntimeControllerVehicle(rvs[i].m_Vehicle);
                if (other == vehicle || !m_Runtime.EntityManager.Exists(other))
                    continue;
                if (!m_Runtime.m_VehicleView.TryGetTarget(other, out int otherTarget) || otherTarget != target)
                    continue;

                if (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState otherState)
                    && (otherState == VehicleState.Preparing || otherState == VehicleState.Idle || otherState == VehicleState.Holding))
                {
                    return false;
                }

                releasedVehicle = other;
                if (RtLog.VerboseEnabled)
                {
                    Once(
                        m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                        vehicle,
                        "LateDispatchTakeover|" + target + "|" + other.Index,
                        "[补发接管] " + lineTag + " 车辆" + vehicle.Index
                            + " 接管班次" + DispatchRuntimeSystem.SlotStr(target)
                            + " 释放车辆" + other.Index
                            + " state=" + (m_Runtime.m_VehicleView.TryGetState(other, out VehicleState releasedState) ? releasedState.ToString() : "?"));
                }
            }

            return true;
        }

        private void Once(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (cache.TryGetValue(vehicle, out string previousKey) && previousKey == key)
                return;

            cache[vehicle] = key;
            m_Runtime.log.Info(message);
        }
    }
}
