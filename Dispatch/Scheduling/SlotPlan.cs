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
            int slot,
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
            m_Runtime.m_RuntimeLog.CrossLineCandidate(tick.Line, pick.Vehicle, bestState, slot, pick.Eta, pick.PrevTarget);
            if (bestState == VehicleState.Idle || bestState == VehicleState.Holding)
            {
                if (pick.PrevTarget < 0 && HoldingFull(tick, cycleMinutes))
                    return Status.Skip;

                claims.Add(new DispatchScheduler.SlotClaim(pick.Vehicle, slot, holder, commitHold: true, clearIdle: true));
                return Status.Claimed;
            }

            claims.Add(new DispatchScheduler.SlotClaim(pick.Vehicle, slot, holder, commitHold: false, clearIdle: false));
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[调度候选] " + lineTag + " 班次" + DispatchRuntimeSystem.SlotStr(slot)
                    + " 选择车辆" + pick.Vehicle.Index
                    + " state=" + bestState
                    + " eta=" + (pick.Eta / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟"
                    + " prevTarget=" + (pick.PrevTarget >= 0 ? DispatchRuntimeSystem.SlotStr(pick.PrevTarget) : "-"));
            }
            return Status.Claimed;
        }

        public bool TryAssignCurrentOrLateSlot(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            out Entity releasedVehicle,
            out int lateSlot)
        {
            releasedVehicle = Entity.Null;
            lateSlot = -1;
            int previousSlot = ScheduleClock.PreviousSlot(nowMin);
            if (!ScheduleClock.CurrentOrRecent(nowMin, previousSlot))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, previousSlot))
                return false;

            if (!TryResolveReleasedVehicle(line, vehicle, previousSlot, lineTag, out releasedVehicle))
                return false;

            lateSlot = previousSlot;
            if (ScheduleClock.CanLate(nowMin, previousSlot))
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousSlot + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousSlot)
                        + " 已过期" + ScheduleClock.Overdue(nowMin, previousSlot) + "分钟");
            }

            return true;
        }

        public bool TryAssignUpcomingTarget(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            out int assignedTarget)
        {
            assignedTarget = -1;
            if (line == Entity.Null || vehicle == Entity.Null || !m_Managed(line))
                return false;

            int[] appliedTargets = m_Times(line);
            int nextTarget = ScheduleTargets.Next(nowMin, appliedTargets);
            if (nextTarget < 0 || ScheduleClock.CurrentOrRecent(nowMin, nextTarget))
                return false;

            int waitMinutes = ScheduleClock.MinutesUntil(nowMin, nextTarget);
            if (waitMinutes > m_Hold(line))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, nextTarget))
                return false;

            assignedTarget = nextTarget;
            if (RtLog.VerboseEnabled)
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "UpcomingTarget|" + nextTarget + "|" + stateTag,
                    "[预分配] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 预分配未来班次" + DispatchRuntimeSystem.SlotStr(nextTarget)
                        + " 距今" + waitMinutes + "分钟");
            }
            return true;
        }

        public bool TryAssignCurrentOrLateScheduledTarget(
            Entity line,
            Entity vehicle,
            int nowMin,
            string lineTag,
            string stateTag,
            IReadOnlyList<int> targets,
            out Entity releasedVehicle,
            out int lateTarget)
        {
            releasedVehicle = Entity.Null;
            lateTarget = -1;
            int previousTarget = ScheduleTargets.Previous(nowMin, targets);
            if (previousTarget < 0 || !ScheduleClock.CurrentOrRecent(nowMin, previousTarget))
                return false;
            if (m_Policy.IsOccupied(line, vehicle, previousTarget))
                return false;

            if (!TryResolveReleasedVehicle(line, vehicle, previousTarget, lineTag, out releasedVehicle))
                return false;

            lateTarget = previousTarget;
            if (ScheduleClock.CanLate(nowMin, previousTarget))
            {
                Once(
                    m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                    vehicle,
                    "LateDispatchCandidate|" + previousTarget + "|" + stateTag,
                    "[补发候选] " + lineTag + " 车辆" + vehicle.Index
                        + " state=" + stateTag
                        + " 候选补发班次" + DispatchRuntimeSystem.SlotStr(previousTarget)
                        + " 已过期" + ScheduleClock.Overdue(nowMin, previousTarget) + "分钟");
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
                    && m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int targetMin)
                    && targetMin >= 0)
                {
                    holdingCount++;
                }
            }

            int holdingCap = Math.Max(1, tick.Hold / cycleMinutes);
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
