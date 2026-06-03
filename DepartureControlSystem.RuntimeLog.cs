using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        internal readonly Dictionary<Entity, string> m_YieldSkipLogCache = new Dictionary<Entity, string>();

        private void ClearDispatchLogCaches()
        {
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

        internal void LogVehicleStateOnce(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
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

        internal bool ShouldEmitVehicleLogWithCooldown(
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

        internal static string FormatDispatchTraceSlot(int targetMin)
            => targetMin >= 0 ? SlotStr(targetMin) : "-";

        private static string FormatTrainHeadSnapshotEntity(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
        }

        internal string BuildTrainHeadLaunchDiagnostic(
            Entity vehicle,
            bool hasCurrentLaunchSnapshot,
            TrainHeadSnapshot currentLaunchSnapshot)
        {
            if (!m_LastLaunchHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot previousLaunchSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-prev-launch launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-prev-launch launchHead=capture-failed";
            }

            if (!m_LastBoardingHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot boardingSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-boarding prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                        + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-boarding prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                        + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchHead=capture-failed";
            }

            if (boardingSnapshot.Frame <= previousLaunchSnapshot.Frame)
            {
                return " headCheck=stale"
                    + " prevLaunchFrame=" + previousLaunchSnapshot.Frame
                    + " boardFrame=" + boardingSnapshot.Frame
                    + (hasCurrentLaunchSnapshot
                        ? " launchFrame=" + currentLaunchSnapshot.Frame
                        : string.Empty);
            }

            bool turned =
                previousLaunchSnapshot.HeadVehicle != boardingSnapshot.HeadVehicle
                || previousLaunchSnapshot.Reversed != boardingSnapshot.Reversed
                || previousLaunchSnapshot.FrontLane != boardingSnapshot.FrontLane
                || previousLaunchSnapshot.RearLane != boardingSnapshot.RearLane;

            string diagnostic = " headCheck=" + (turned ? "turned" : "same")
                + " prevHead=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                + " boardHead=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.HeadVehicle)
                + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                + " boardRev=" + (boardingSnapshot.Reversed ? "1" : "0")
                + " prevFront=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.FrontLane)
                + " prevRear=" + FormatTrainHeadSnapshotEntity(previousLaunchSnapshot.RearLane)
                + " boardFront=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.FrontLane)
                + " boardRear=" + FormatTrainHeadSnapshotEntity(boardingSnapshot.RearLane)
                + " boardWp=" + boardingSnapshot.WaypointIndex;

            if (hasCurrentLaunchSnapshot)
            {
                diagnostic += " launchHead=" + FormatTrainHeadSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                    + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                    + " launchWp=" + currentLaunchSnapshot.WaypointIndex;
            }
            else
            {
                diagnostic += " launchHead=capture-failed";
            }

            return diagnostic;
        }

        internal void LogRouteVehicleOwnerMismatch(Entity observedLine, Entity vehicle, string phase)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            VehicleState state = m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState)
                ? runtimeState
                : default;
            int targetMin = m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = phase
                + "|observed=" + observedLine.Index
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            LogVehicleStateOnce(
                m_RouteVehicleOwnerMismatchLogCache,
                vehicle,
                key,
                "[RouteVehicleOwnerMismatch] line=" + observedLine.Index
                    + " vehicle=" + vehicle.Index
                    + " phase=" + phase
                    + " " + BuildVehicleOwnershipDiagnostic(observedLine, vehicle, state, targetMin, phase));
        }

        internal void LogCrossLineCandidate(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int slot,
            float etaFrames,
            int previousTarget)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            int targetMin = m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = "candidate"
                + "|observed=" + observedLine.Index
                + "|slot=" + slot
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            LogVehicleStateOnce(
                m_CrossLineCandidateLogCache,
                vehicle,
                key,
                "[CrossLineCandidate] line=" + observedLine.Index
                    + " slot=" + SlotStr(slot)
                    + " vehicle=" + vehicle.Index
                    + " state=" + state
                    + " eta=" + (etaFrames == float.MaxValue ? "?" : (etaFrames / (float)SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟")
                    + " prevTarget=" + (previousTarget >= 0 ? SlotStr(previousTarget) : "-")
                    + " " + BuildVehicleOwnershipDiagnostic(observedLine, vehicle, state, targetMin, "candidate"));
        }

        internal void LogOriginDispatchTrace(
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

            float distanceToOriginMeters = wps.Length > 0 ? m_LineProfile.DistanceToOrigin(vehicle, wps) : -1f;
            bool hasAssistPending = m_RuntimeController.TryGetAssistLaunchPending(vehicle, route, targetMin, out AssistLaunchPendingRecord assistPending);
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

        internal void LogDispatchSlotHeld(
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
            float distanceToOriginMeters = wps.Length > 0 ? m_LineProfile.DistanceToOrigin(holder, wps) : -1f;
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

        internal void ObserveBvMisfireCandidate(
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
        internal void LogPreparingTargetDrift(
            Entity line,
            Entity vehicle,
            Entity route,
            Entity originWaypoint,
            Entity target,
            int targetMin,
            int curWpIdx,
            bool boarding,
            bool atOrigin)
        {
            if (line == Entity.Null || vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                return;
            if (target != Entity.Null && target == originWaypoint)
                return;

            Entity targetDepot = CanonDepot(target);
            string key = "target=" + target.Index
                + "|route=" + route.Index
                + "|targetDepot=" + targetDepot.Index
                + "|wp=" + curWpIdx
                + "|boarding=" + (boarding ? "1" : "0")
                + "|atA=" + (atOrigin ? "1" : "0");

            LogVehicleStateOnce(
                m_PreparingTargetDriftLogCache,
                vehicle,
                key,
                "[PreparingTargetDrift] line=" + line.Index
                    + " vehicle=" + vehicle.Index
                    + " originWp=" + DispatchCommandApplier.DescribeRetireShadowEntity(originWaypoint)
                    + " curWp=" + curWpIdx
                    + " boarding=" + (boarding ? "1" : "0")
                    + " atA=" + (atOrigin ? "1" : "0")
                    + " " + BuildVehicleOwnershipDiagnostic(line, vehicle, VehicleState.Preparing, targetMin, "preparing"));
        }

        internal string BuildVehicleOwnershipDiagnostic(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int targetMin,
            string phase)
        {
            Entity mappedLine = m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            Entity owner = EntityManager.HasComponent<Owner>(vehicle)
                ? EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            Entity ownerDepot = CanonDepot(owner);
            Entity target = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            Entity targetDepot = CanonDepot(target);
            Entity pathDestination = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                : Entity.Null;
            Entity pathDestinationDepot = CanonDepot(pathDestination);
            string publicState = EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string pathState = EntityManager.HasComponent<PathInformation>(vehicle)
                ? EntityManager.GetComponentData<PathInformation>(vehicle).m_State.ToString()
                : "-";
            int cachedWp = m_CachedWpIdx.TryGetValue(vehicle, out int cached)
                ? cached
                : -1;
            uint preparingAge = m_VehicleView.TryGetPreparing(vehicle, out uint prepStart)
                ? m_SimulationSystem.frameIndex - prepStart
                : 0;

            return "phase=" + phase
                + " observedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(observedLine)
                + " mappedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                + " currentRoute=" + DispatchCommandApplier.DescribeRetireShadowEntity(currentRoute)
                + " state=" + state
                + " targetMin=" + (targetMin >= 0 ? SlotStr(targetMin) : "-")
                + " cachedWp=" + cachedWp
                + " preparingAgeFrames=" + preparingAge
                + " owner=" + DispatchCommandApplier.DescribeRetireShadowEntity(owner)
                + " ownerDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(ownerDepot)
                + " target=" + DispatchCommandApplier.DescribeRetireShadowEntity(target)
                + " targetKind=" + m_CommandApplier.DescribeRetireShadowTargetKind(target)
                + " targetExists=" + ((target != Entity.Null && EntityManager.Exists(target)) ? "1" : "0")
                + " targetDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(targetDepot)
                + " pathDest=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestination)
                + " pathDestDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestinationDepot)
                + " pathState=" + pathState
                + " ptState=" + publicState
                + " deleted=" + (EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0");
        }

    }
}
