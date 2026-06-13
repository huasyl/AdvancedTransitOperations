using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Observation;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Diagnostics
{
    internal sealed class RuntimeLog
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        internal readonly Dictionary<Entity, string> m_YieldSkipLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_PreparingSlotLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_PreparingTargetDriftLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_CrossLineCandidateLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_RouteVehicleOwnerMismatchLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_HoldingSkipLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_LateDispatchLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_BvMisfireObserveLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_DepartureObserveLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_OriginDispatchTraceLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, uint> m_OriginDispatchTraceLastLogFrameCache = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, string> m_DispatchSlotHeldLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, uint> m_DispatchSlotHeldLastLogFrameCache = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, string> m_BvWaypointMismatchLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_BvTrackAnchorRecoveryLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, string> m_MidStopTimeoutLogCache = new Dictionary<Entity, string>();
        internal readonly Dictionary<Entity, uint> m_BvWaypointMismatchLastLogFrame = new Dictionary<Entity, uint>();
        internal readonly Dictionary<Entity, TrainHeadSnapshot> m_LastLaunchHeadSnapshots = new Dictionary<Entity, TrainHeadSnapshot>();
        internal readonly Dictionary<Entity, TrainHeadSnapshot> m_LastBoardingHeadSnapshots = new Dictionary<Entity, TrainHeadSnapshot>();

        public RuntimeLog(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Clear()
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
            m_BvWaypointMismatchLogCache.Clear();
            m_BvTrackAnchorRecoveryLogCache.Clear();
            m_BvMisfireObserveLogCache.Clear();
            m_DepartureObserveLogCache.Clear();
            m_BvWaypointMismatchLastLogFrame.Clear();
            m_LastLaunchHeadSnapshots.Clear();
            m_LastBoardingHeadSnapshots.Clear();
            m_MidStopTimeoutLogCache.Clear();
        }

        public void ClearVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_PreparingSlotLogCache.Remove(vehicle);
            m_PreparingTargetDriftLogCache.Remove(vehicle);
            m_CrossLineCandidateLogCache.Remove(vehicle);
            m_RouteVehicleOwnerMismatchLogCache.Remove(vehicle);
            m_HoldingSkipLogCache.Remove(vehicle);
            m_LateDispatchLogCache.Remove(vehicle);
            m_YieldSkipLogCache.Remove(vehicle);
            m_BvMisfireObserveLogCache.Remove(vehicle);
            m_DepartureObserveLogCache.Remove(vehicle);
            m_OriginDispatchTraceLogCache.Remove(vehicle);
            m_OriginDispatchTraceLastLogFrameCache.Remove(vehicle);
            m_BvWaypointMismatchLogCache.Remove(vehicle);
            m_BvTrackAnchorRecoveryLogCache.Remove(vehicle);
            m_BvWaypointMismatchLastLogFrame.Remove(vehicle);
            m_LastLaunchHeadSnapshots.Remove(vehicle);
            m_LastBoardingHeadSnapshots.Remove(vehicle);
            m_MidStopTimeoutLogCache.Remove(vehicle);
        }

        public void RememberLaunchHead(Entity vehicle, TrainHeadSnapshot snapshot)
        {
            if (vehicle != Entity.Null)
                m_LastLaunchHeadSnapshots[vehicle] = snapshot;
        }

        public void ForgetLaunchHead(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_LastLaunchHeadSnapshots.Remove(vehicle);
        }

        public void RememberBoardingHead(Entity vehicle, TrainHeadSnapshot snapshot)
        {
            if (vehicle != Entity.Null)
                m_LastBoardingHeadSnapshots[vehicle] = snapshot;
        }

        public void ForgetBoardingHead(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_LastBoardingHeadSnapshots.Remove(vehicle);
        }

        public void Once(Dictionary<Entity, string> cache, Entity vehicle, string key, string message)
        {
            if (vehicle == Entity.Null)
            {
                m_Runtime.log.Info(message);
                return;
            }

            if (cache.TryGetValue(vehicle, out string previous) && previous == key)
                return;

            cache[vehicle] = key;
            m_Runtime.log.Info(message);
        }

        public bool ShouldLogOnce(Dictionary<Entity, string> cache, Entity vehicle, string key)
        {
            return vehicle == Entity.Null
                || !cache.TryGetValue(vehicle, out string previous)
                || previous != key;
        }

        public bool Cooldown(
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

        public static string Slot(int targetMin)
        {
            return targetMin >= 0 ? DispatchRuntimeSystem.SlotStr(targetMin) : "-";
        }

        public string TrainHeadLaunch(
            Entity vehicle,
            bool hasCurrentLaunchSnapshot,
            TrainHeadSnapshot currentLaunchSnapshot)
        {
            if (!m_LastLaunchHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot previousLaunchSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-prev-launch launchHead=" + FormatSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-prev-launch launchHead=capture-failed";
            }

            if (!m_LastBoardingHeadSnapshots.TryGetValue(vehicle, out TrainHeadSnapshot boardingSnapshot))
            {
                return hasCurrentLaunchSnapshot
                    ? " headCheck=no-boarding prevHead=" + FormatSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                        + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchHead=" + FormatSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                        + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                        + " launchWp=" + currentLaunchSnapshot.WaypointIndex
                    : " headCheck=no-boarding prevHead=" + FormatSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
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
                + " prevHead=" + FormatSnapshotEntity(previousLaunchSnapshot.HeadVehicle)
                + " boardHead=" + FormatSnapshotEntity(boardingSnapshot.HeadVehicle)
                + " prevRev=" + (previousLaunchSnapshot.Reversed ? "1" : "0")
                + " boardRev=" + (boardingSnapshot.Reversed ? "1" : "0")
                + " prevFront=" + FormatSnapshotEntity(previousLaunchSnapshot.FrontLane)
                + " prevRear=" + FormatSnapshotEntity(previousLaunchSnapshot.RearLane)
                + " boardFront=" + FormatSnapshotEntity(boardingSnapshot.FrontLane)
                + " boardRear=" + FormatSnapshotEntity(boardingSnapshot.RearLane)
                + " boardWp=" + boardingSnapshot.WaypointIndex;

            if (hasCurrentLaunchSnapshot)
            {
                diagnostic += " launchHead=" + FormatSnapshotEntity(currentLaunchSnapshot.HeadVehicle)
                    + " launchRev=" + (currentLaunchSnapshot.Reversed ? "1" : "0")
                    + " launchWp=" + currentLaunchSnapshot.WaypointIndex;
            }
            else
            {
                diagnostic += " launchHead=capture-failed";
            }

            return diagnostic;
        }

        public void RouteOwnerMismatch(Entity observedLine, Entity vehicle, string phase)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            VehicleState state = m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState runtimeState)
                ? runtimeState
                : default;
            int targetMin = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = phase
                + "|observed=" + observedLine.Index
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            Once(
                m_RouteVehicleOwnerMismatchLogCache,
                vehicle,
                key,
                "[RouteVehicleOwnerMismatch] line=" + observedLine.Index
                    + " vehicle=" + vehicle.Index
                    + " phase=" + phase
                    + " " + VehicleOwnership(observedLine, vehicle, state, targetMin, phase));
        }

        public void CrossLineCandidate(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int slot,
            float etaFrames,
            int previousTarget)
        {
            if (observedLine == Entity.Null || vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return;

            Entity mappedLine = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            bool mappedMismatch = mappedLine != Entity.Null && mappedLine != observedLine;
            bool routeMismatch = currentRoute != Entity.Null && currentRoute != observedLine;
            if (!mappedMismatch && !routeMismatch)
                return;

            int targetMin = m_Runtime.m_VehicleView.TryGetTarget(vehicle, out int assignedTarget)
                ? assignedTarget
                : -1;
            Entity targetEntity = m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            string key = "candidate"
                + "|observed=" + observedLine.Index
                + "|slot=" + slot
                + "|mapped=" + mappedLine.Index
                + "|route=" + currentRoute.Index
                + "|target=" + targetEntity.Index
                + "|state=" + state;

            Once(
                m_CrossLineCandidateLogCache,
                vehicle,
                key,
                "[CrossLineCandidate] line=" + observedLine.Index
                    + " slot=" + DispatchRuntimeSystem.SlotStr(slot)
                    + " vehicle=" + vehicle.Index
                    + " state=" + state
                    + " eta=" + (etaFrames == float.MaxValue ? "?" : (etaFrames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE).ToString("F1") + "分钟")
                    + " prevTarget=" + (previousTarget >= 0 ? DispatchRuntimeSystem.SlotStr(previousTarget) : "-")
                    + " " + VehicleOwnership(observedLine, vehicle, state, targetMin, "candidate"));
        }

        public void OriginDispatchTrace(
            string reason,
            Entity vehicle,
            Entity line,
            Entity route,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleState state,
            int targetMin,
            int nowMin,
            int currentWaypointIndex,
            bool atOrigin,
            bool boarding,
            bool lastBoarding,
            uint nowFrame,
            string extra = "")
        {
            if (!RtLog.VerboseEnabled || vehicle == Entity.Null)
                return;

            int cachedWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint)
                ? cachedWaypoint
                : -1;
            bool hasForcedReady = m_Runtime.m_VehicleView.TryGetReady(vehicle, out uint forcedReadyFrame) && forcedReadyFrame > nowFrame;
            bool hasBvMisfire = m_Runtime.m_BVMisfire.Contains(vehicle);
            int currentSlot = m_Runtime.m_VehicleView.TryGetSlot(vehicle, out int currentAssignedSlot) ? currentAssignedSlot : -1;
            string key = reason
                + "|state=" + state
                + "|target=" + targetMin
                + "|current=" + currentSlot
                + "|curWp=" + currentWaypointIndex
                + "|cached=" + cachedWaypointIndex
                + "|atA=" + (atOrigin ? "1" : "0")
                + "|boarding=" + (boarding ? "1" : "0")
                + "|last=" + (lastBoarding ? "1" : "0")
                + "|forced=" + (hasForcedReady ? "1" : "0")
                + "|misfire=" + (hasBvMisfire ? "1" : "0");

            if (!Cooldown(
                    m_OriginDispatchTraceLogCache,
                    m_OriginDispatchTraceLastLogFrameCache,
                    vehicle,
                    key,
                    nowFrame,
                    DispatchRuntimeSystem.ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES))
            {
                return;
            }

            float distanceToOriginMeters = waypoints.Length > 0 ? m_Runtime.m_LineProfile.DistanceToOrigin(vehicle, waypoints) : -1f;
            bool hasAssistPending = m_Runtime.m_RuntimeController.TryGetAssistPendingTarget(vehicle, route, targetMin, out int assistTargetMin);
            uint forcedReadyRemainingFrames = hasForcedReady ? forcedReadyFrame - nowFrame : 0;

            m_Runtime.log.Info("[OriginDispatchTrace] reason=" + reason
                + " line=" + line.Index
                + " route=" + route.Index
                + " vehicle=" + vehicle.Index
                + " state=" + state
                + " now=" + DispatchRuntimeSystem.SlotStr(nowMin)
                + " target=" + Slot(targetMin)
                + " current=" + Slot(currentSlot)
                + " atA=" + (atOrigin ? "1" : "0")
                + " boarding=" + (boarding ? "1" : "0")
                + " lastBoarding=" + (lastBoarding ? "1" : "0")
                + " curWpIdx=" + currentWaypointIndex
                + " cachedWpIdx=" + cachedWaypointIndex
                + " distOrigin=" + (distanceToOriginMeters >= 0f ? distanceToOriginMeters.ToString("F1") : "?")
                + " forcedReadyFrames=" + forcedReadyRemainingFrames
                + " assistPending=" + (hasAssistPending ? ("1(" + Slot(assistTargetMin) + ")") : "0")
                + " bvMisfire=" + (hasBvMisfire ? "1" : "0")
                + (string.IsNullOrWhiteSpace(extra) ? string.Empty : " " + extra));
        }

        public void DispatchSlotHeld(
            Entity line,
            int slot,
            Entity holder,
            Entity route,
            DynamicBuffer<RouteWaypoint> waypoints,
            int nowMin,
            uint nowFrame,
            string reason)
        {
            if (!RtLog.VerboseEnabled || line == Entity.Null || holder == Entity.Null || !m_Runtime.EntityManager.Exists(holder))
                return;

            VehicleState holderState = m_Runtime.m_VehicleView.TryGetState(holder, out VehicleState state)
                ? state
                : VehicleState.Running;
            if (holderState != VehicleState.Holding)
                return;

            int holderTarget = m_Runtime.m_VehicleView.TryGetTarget(holder, out int target) ? target : -1;
            int holderCurrent = m_Runtime.m_VehicleView.TryGetSlot(holder, out int current) ? current : -1;
            int holderCachedWaypoint = m_Runtime.m_CachedWpIdx.TryGetValue(holder, out int cachedWaypoint) ? cachedWaypoint : -1;
            string key = reason
                + "|slot=" + slot
                + "|holder=" + holder.Index
                + "|state=" + holderState
                + "|target=" + holderTarget
                + "|current=" + holderCurrent
                + "|cached=" + holderCachedWaypoint;

            if (!Cooldown(
                    m_DispatchSlotHeldLogCache,
                    m_DispatchSlotHeldLastLogFrameCache,
                    line,
                    key,
                    nowFrame,
                    DispatchRuntimeSystem.ORIGIN_DISPATCH_TRACE_COOLDOWN_FRAMES))
            {
                return;
            }

            bool holderBoarding = m_Runtime.EntityManager.HasComponent<PublicTransport>(holder)
                && (m_Runtime.EntityManager.GetComponentData<PublicTransport>(holder).m_State & PublicTransportFlags.Boarding) != 0;
            float distanceToOriginMeters = waypoints.Length > 0 ? m_Runtime.m_LineProfile.DistanceToOrigin(holder, waypoints) : -1f;
            m_Runtime.log.Info("[DispatchSlotHeld] reason=" + reason
                + " line=" + line.Index
                + " route=" + route.Index
                + " now=" + DispatchRuntimeSystem.SlotStr(nowMin)
                + " slot=" + DispatchRuntimeSystem.SlotStr(slot)
                + " holder=" + holder.Index
                + " state=" + holderState
                + " target=" + Slot(holderTarget)
                + " current=" + Slot(holderCurrent)
                + " cachedWpIdx=" + holderCachedWaypoint
                + " boarding=" + (holderBoarding ? "1" : "0")
                + " distOrigin=" + (distanceToOriginMeters >= 0f ? distanceToOriginMeters.ToString("F1") : "?"));
        }

        public void BvMisfireCandidate(
            Entity vehicle,
            string lineTag,
            string phase,
            string detail,
            uint nowFrame)
        {
            Once(
                m_BvMisfireObserveLogCache,
                vehicle,
                phase + "|" + detail,
                "[BVObserve] " + lineTag + " 车辆" + vehicle.Index
                    + " phase=" + phase
                    + " detail=" + detail
                    + " enforcement=" + (DispatchRuntimeSystem.IsBvMisfireEnforcementEnabled() ? "on" : "off")
                    + " frame=" + nowFrame);

            if (DispatchRuntimeSystem.IsBvMisfireEnforcementEnabled())
            {
                m_Runtime.m_BVMisfire.Add(vehicle);
                m_Runtime.m_BVMisfireStartFrame[vehicle] = nowFrame;
            }
            else
            {
                m_Runtime.m_BVMisfire.Remove(vehicle);
                m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            }
        }

        public void PreparingTargetDrift(
            Entity line,
            Entity vehicle,
            Entity route,
            Entity originWaypoint,
            Entity target,
            int targetMin,
            int currentWaypointIndex,
            bool boarding,
            bool atOrigin)
        {
            if (line == Entity.Null || vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return;
            if (target != Entity.Null && target == originWaypoint)
                return;

            Entity targetDepot = m_Runtime.CanonDepot(target);
            string key = "target=" + target.Index
                + "|route=" + route.Index
                + "|targetDepot=" + targetDepot.Index
                + "|wp=" + currentWaypointIndex
                + "|boarding=" + (boarding ? "1" : "0")
                + "|atA=" + (atOrigin ? "1" : "0");

            Once(
                m_PreparingTargetDriftLogCache,
                vehicle,
                key,
                "[PreparingTargetDrift] line=" + line.Index
                    + " vehicle=" + vehicle.Index
                    + " originWp=" + DispatchCommandApplier.DescribeRetireShadowEntity(originWaypoint)
                    + " curWp=" + currentWaypointIndex
                    + " boarding=" + (boarding ? "1" : "0")
                    + " atA=" + (atOrigin ? "1" : "0")
                    + " " + VehicleOwnership(line, vehicle, VehicleState.Preparing, targetMin, "preparing"));
        }

        public string VehicleOwnership(
            Entity observedLine,
            Entity vehicle,
            VehicleState state,
            int targetMin,
            string phase)
        {
            Entity mappedLine = m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity mapped)
                ? mapped
                : Entity.Null;
            Entity currentRoute = m_Runtime.EntityManager.HasComponent<CurrentRoute>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route
                : Entity.Null;
            Entity owner = m_Runtime.EntityManager.HasComponent<Owner>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<Owner>(vehicle).m_Owner
                : Entity.Null;
            Entity ownerDepot = m_Runtime.CanonDepot(owner);
            Entity target = m_Runtime.EntityManager.HasComponent<Target>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;
            Entity targetDepot = m_Runtime.CanonDepot(target);
            Entity pathDestination = m_Runtime.EntityManager.HasComponent<PathInformation>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<PathInformation>(vehicle).m_Destination
                : Entity.Null;
            Entity pathDestinationDepot = m_Runtime.CanonDepot(pathDestination);
            string publicState = m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State.ToString()
                : "-";
            string pathState = m_Runtime.EntityManager.HasComponent<PathInformation>(vehicle)
                ? m_Runtime.EntityManager.GetComponentData<PathInformation>(vehicle).m_State.ToString()
                : "-";
            int cachedWaypointIndex = m_Runtime.m_CachedWpIdx.TryGetValue(vehicle, out int cachedWaypoint)
                ? cachedWaypoint
                : -1;
            uint preparingAge = m_Runtime.m_VehicleView.TryGetPreparing(vehicle, out uint preparingStart)
                ? m_Runtime.m_SimulationSystem.frameIndex - preparingStart
                : 0;

            return "phase=" + phase
                + " observedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(observedLine)
                + " mappedLine=" + DispatchCommandApplier.DescribeRetireShadowEntity(mappedLine)
                + " currentRoute=" + DispatchCommandApplier.DescribeRetireShadowEntity(currentRoute)
                + " state=" + state
                + " targetMin=" + (targetMin >= 0 ? DispatchRuntimeSystem.SlotStr(targetMin) : "-")
                + " cachedWp=" + cachedWaypointIndex
                + " preparingAgeFrames=" + preparingAge
                + " owner=" + DispatchCommandApplier.DescribeRetireShadowEntity(owner)
                + " ownerDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(ownerDepot)
                + " target=" + DispatchCommandApplier.DescribeRetireShadowEntity(target)
                + " targetKind=" + m_Runtime.m_CommandApplier.DescribeRetireShadowTargetKind(target)
                + " targetExists=" + ((target != Entity.Null && m_Runtime.EntityManager.Exists(target)) ? "1" : "0")
                + " targetDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(targetDepot)
                + " pathDest=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestination)
                + " pathDestDepot=" + DispatchCommandApplier.DescribeRetireShadowEntity(pathDestinationDepot)
                + " pathState=" + pathState
                + " ptState=" + publicState
                + " deleted=" + (m_Runtime.EntityManager.HasComponent<Deleted>(vehicle) ? "1" : "0")
                + " parked=" + (m_Runtime.EntityManager.HasComponent<ParkedTrain>(vehicle) ? "1" : "0");
        }

        private static string FormatSnapshotEntity(Entity entity)
        {
            return entity == Entity.Null ? "null" : entity.Index.ToString();
        }
    }
}
