using System.Collections.Generic;
using Game.Common;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private readonly Dictionary<Entity, string> m_YieldSkipLogCache = new Dictionary<Entity, string>();

        private void ClearDispatchLogCaches()
        {
            m_BypassDecisionLogCache.Clear();
            m_BypassDepartureGateLogCache.Clear();
            m_BypassHoldFrameLogCache.Clear();
            m_BypassReleaseDiagLogCache.Clear();
            m_BypassExitClearLogCache.Clear();
            m_PreparingSlotLogCache.Clear();
            m_PreparingTargetDriftLogCache.Clear();
            m_CrossLineCandidateLogCache.Clear();
            m_RouteVehicleOwnerMismatchLogCache.Clear();
            m_HoldingSkipLogCache.Clear();
            m_LateDispatchLogCache.Clear();
            m_YieldSkipLogCache.Clear();
            m_OriginDispatchTraceLogCache.Clear();
            m_OriginDispatchTraceLastLogFrameCache.Clear();
            m_DispatchSlotHeldLogCache.Clear();
            m_DispatchSlotHeldLastLogFrameCache.Clear();
        }

        private void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (vehicle == Entity.Null)
            {
                log.Info(message);
                return;
            }

            if (cache.TryGetValue(vehicle, out string previous) && previous == key)
                return;

            cache[vehicle] = key;
            log.Info(message);
        }

        private bool ShouldEmitVehicleLogWithCooldown(
            Dictionary<Entity, string> keyCache,
            Dictionary<Entity, uint> lastLogFrameCache,
            Entity vehicle,
            string key,
            uint nowFrame,
            uint cooldownFrames)
        {
            if (vehicle == Entity.Null)
                return true;

            bool keyChanged = !keyCache.TryGetValue(vehicle, out string previousKey) || previousKey != key;
            if (keyChanged)
            {
                keyCache[vehicle] = key;
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            if (!lastLogFrameCache.TryGetValue(vehicle, out uint lastLogFrame)
                || nowFrame >= lastLogFrame + cooldownFrames)
            {
                lastLogFrameCache[vehicle] = nowFrame;
                return true;
            }

            return false;
        }

        private static string FormatDispatchTraceSlot(int targetMin)
            => targetMin >= 0 ? SlotStr(targetMin) : "-";

        private void LogOriginDispatchTrace(
            string reason,
            Entity vehicle,
            Entity line,
            Entity route,
            DynamicBuffer<RouteWaypoint> wps,
            VehicleState state,
            int targetMin,
            int nowMin,
            int curWpIdx,
            bool atA,
            bool boarding,
            bool lastBoarding,
            uint nowFrame,
            string extra = "")
        {
            if (vehicle == Entity.Null)
                return;

            int cachedWpIdx = m_CachedWpIdx.TryGetValue(vehicle, out int cachedWp) ? cachedWp : -1;
            bool hasForcedReady = m_VehicleView.TryGetReady(vehicle, out uint forcedReadyFrame) && forcedReadyFrame > nowFrame;
            bool hasBvMisfire = m_BVMisfire.Contains(vehicle);
            int currentSlot = m_VehicleView.TryGetSlot(vehicle, out int currentAssignedSlot) ? currentAssignedSlot : -1;
            string key = reason
                + "|state=" + state
                + "|target=" + targetMin
                + "|current=" + currentSlot
                + "|curWp=" + curWpIdx
                + "|cached=" + cachedWpIdx
                + "|atA=" + (atA ? "1" : "0")
                + "|boarding=" + (boarding ? "1" : "0")
                + "|last=" + (lastBoarding ? "1" : "0")
                + "|forced=" + (hasForcedReady ? "1" : "0")
                + "|misfire=" + (hasBvMisfire ? "1" : "0");

            if (!ShouldEmitVehicleLogWithCooldown(
                    m_OriginDispatchTraceLogCache,
                    m_OriginDispatchTraceLastLogFrameCache,
                    vehicle,
                    key,
                    nowFrame,
                    ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES))
            {
                return;
            }

            float distanceToOriginMeters = wps.Length > 0 ? GetDistanceToOriginMeters(vehicle, wps) : -1f;
            bool hasAssistPending = TryGetAssistLaunchPending(vehicle, route, targetMin, out AssistLaunchPendingRecord assistPending);
            int assistTargetMin = hasAssistPending ? assistPending.TargetMin : -1;
            uint forcedReadyRemainingFrames = hasForcedReady ? forcedReadyFrame - nowFrame : 0;

            log.Info("[OriginDispatchTrace] reason=" + reason
                + " line=" + line.Index
                + " route=" + route.Index
                + " vehicle=" + vehicle.Index
                + " state=" + state
                + " now=" + SlotStr(nowMin)
                + " target=" + FormatDispatchTraceSlot(targetMin)
                + " current=" + FormatDispatchTraceSlot(currentSlot)
                + " atA=" + (atA ? "1" : "0")
                + " boarding=" + (boarding ? "1" : "0")
                + " lastBoarding=" + (lastBoarding ? "1" : "0")
                + " curWpIdx=" + curWpIdx
                + " cachedWpIdx=" + cachedWpIdx
                + " distOrigin=" + (distanceToOriginMeters >= 0f ? distanceToOriginMeters.ToString("F1") : "?")
                + " forcedReadyFrames=" + forcedReadyRemainingFrames
                + " assistPending=" + (hasAssistPending ? ("1(" + FormatDispatchTraceSlot(assistTargetMin) + ")") : "0")
                + " bvMisfire=" + (hasBvMisfire ? "1" : "0")
                + (string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra));
        }

        private void LogDispatchSlotHeld(
            Entity line,
            int slot,
            Entity holder,
            Entity route,
            DynamicBuffer<RouteWaypoint> wps,
            int nowMin,
            uint nowFrame,
            string reason)
        {
            if (line == Entity.Null || holder == Entity.Null || !EntityManager.Exists(holder))
                return;

            VehicleState holderState = m_VehicleView.TryGetState(holder, out var st) ? st : VehicleState.Running;
            if (holderState != VehicleState.Holding)
                return;

            int holderTarget = m_VehicleView.TryGetTarget(holder, out int target) ? target : -1;
            int holderCurrent = m_VehicleView.TryGetSlot(holder, out int current) ? current : -1;
            int holderCachedWp = m_CachedWpIdx.TryGetValue(holder, out int cachedWp) ? cachedWp : -1;
            string key = reason
                + "|slot=" + slot
                + "|holder=" + holder.Index
                + "|state=" + holderState
                + "|target=" + holderTarget
                + "|current=" + holderCurrent
                + "|cached=" + holderCachedWp;

            if (!ShouldEmitVehicleLogWithCooldown(
                    m_DispatchSlotHeldLogCache,
                    m_DispatchSlotHeldLastLogFrameCache,
                    line,
                    key,
                    nowFrame,
                    ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES))
            {
                return;
            }

            bool holderBoarding = EntityManager.HasComponent<PublicTransport>(holder)
                && (EntityManager.GetComponentData<PublicTransport>(holder).m_State & PublicTransportFlags.Boarding) != 0;
            float distanceToOriginMeters = wps.Length > 0 ? GetDistanceToOriginMeters(holder, wps) : -1f;
            log.Info("[DispatchSlotHeld] reason=" + reason
                + " line=" + line.Index
                + " route=" + route.Index
                + " now=" + SlotStr(nowMin)
                + " slot=" + SlotStr(slot)
                + " holder=" + holder.Index
                + " state=" + holderState
                + " target=" + FormatDispatchTraceSlot(holderTarget)
                + " current=" + FormatDispatchTraceSlot(holderCurrent)
                + " cachedWpIdx=" + holderCachedWp
                + " boarding=" + (holderBoarding ? "1" : "0")
                + " distOrigin=" + (distanceToOriginMeters >= 0f ? distanceToOriginMeters.ToString("F1") : "?"));
        }

        private void ObserveBvMisfireCandidate(
            Entity vehicle,
            string lineTag,
            string phase,
            string detail,
            uint nowFrame)
        {
            LogVehicleStateOnce(
                m_BvMisfireObserveLogCache,
                vehicle,
                phase + "|" + detail,
                "[BVObserve] " + lineTag + " 车辆" + vehicle.Index
                    + " phase=" + phase
                    + " detail=" + detail
                    + " enforcement=" + (IsBvMisfireEnforcementEnabled() ? "on" : "off")
                    + " frame=" + nowFrame);

            if (IsBvMisfireEnforcementEnabled())
            {
                m_BVMisfire.Add(vehicle);
                m_BVMisfireStartFrame[vehicle] = nowFrame;
            }
            else
            {
                m_BVMisfire.Remove(vehicle);
                m_BVMisfireStartFrame.Remove(vehicle);
            }
        }
    }
}
