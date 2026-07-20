using System;
using System.Collections.Generic;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod
{
    internal sealed class DispatchRuntimeController
    {
        private readonly VehicleRegistry m_Vehicles;
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly LineSpawnControl m_LineSpawnControl;
        private readonly RuntimeVehicleCleanup m_RuntimeVehicleCleanup;
        private readonly SchedulerApply m_SchedulerApply;
        private readonly Dictionary<Entity, AssistLaunchPendingRecord> m_AssistLaunchPendingByVehicle = new Dictionary<Entity, AssistLaunchPendingRecord>();
        private readonly Dictionary<Entity, uint> m_PreparingWaypointLiveFrames = new Dictionary<Entity, uint>();

        private readonly struct AssistLaunchPendingRecord
        {
            public readonly Entity Line;
            public readonly int TargetMinute;

            public AssistLaunchPendingRecord(Entity line, int targetMinute)
            {
                Line = line;
                TargetMinute = targetMinute;
            }
        }

        public DispatchRuntimeController(VehicleRegistry vehicles, DispatchRuntimeSystem runtime)
        {
            m_Vehicles = vehicles;
            m_Runtime = runtime;
            m_LineSpawnControl = new LineSpawnControl(runtime);
            m_RuntimeVehicleCleanup = new RuntimeVehicleCleanup(runtime, m_LineSpawnControl, ClearAssistLaunchPending);
            m_SchedulerApply = new SchedulerApply(runtime);
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        private const uint BV_MISFIRE_TIMEOUT = DispatchRuntimeSystem.BV_MISFIRE_TIMEOUT;
        private const bool ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS = DispatchRuntimeSystem.ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS;
        private const uint FORCED_MIDSTOP_BV_GRACE_FRAMES = DispatchRuntimeSystem.FORCED_MIDSTOP_BV_GRACE_FRAMES;
        private const int IDLE_TIMEOUT_MINUTES = DispatchRuntimeSystem.IDLE_TIMEOUT_MINUTES;
        private const uint LAUNCH_COOLDOWN_FRAMES = DispatchRuntimeSystem.LAUNCH_COOLDOWN_FRAMES;
        private const uint PREPARING_WAYPOINT_LIVE_REFRESH_FRAMES = 16;
        private const float ORIGIN_CONGESTION_RADIUS_METERS = DispatchRuntimeSystem.ORIGIN_CONGESTION_RADIUS_METERS;
        private const float ORIGIN_FORCE_IDLE_RADIUS_METERS = DispatchRuntimeSystem.ORIGIN_FORCE_IDLE_RADIUS_METERS;
        private const float DEPARTURE_MOVING_SPEED_SQ = 0.01f;

        private static byte BoardingByte(bool boarding) => boarding ? (byte)1 : (byte)0;

        private void SetLocalizedVehicleLabel(Entity vehicle, string key, string fallback, string suffix = "")
        {
            m_Runtime.m_VehicleLabels.SetLocalized(vehicle, key, fallback, suffix);
        }

        private void SetPrefixedLocalizedVehicleLabel(Entity vehicle, string key, string fallback, string prefix, string suffix = "")
        {
            m_Runtime.m_VehicleLabels.SetPrefixedLocalized(vehicle, key, fallback, prefix, suffix);
        }

        private void SetHoldingVehicleLabel(Entity vehicle, int targetMinute, string tag, bool late = false, bool includeHoldingInWaiting = true)
        {
            if (targetMinute >= 0)
            {
                SetLocalizedVehicleLabel(
                    vehicle,
                    late ? "HoldingLate" : "Holding",
                    late ? "候车 补发" : "候车",
                    " " + DispatchRuntimeSystem.SlotStr(targetMinute) + tag);
                return;
            }

            SetLocalizedVehicleLabel(
                vehicle,
                includeHoldingInWaiting ? "HoldingWaitingDispatch" : "WaitingDispatch",
                includeHoldingInWaiting ? "候车 等待调度" : "等待调度",
                tag);
        }

        private void SetRunningSlotVehicleLabel(Entity vehicle, bool late, int targetMinute, string tag)
        {
            SetLocalizedVehicleLabel(
                vehicle,
                late ? "RunningLate" : "Running",
                late ? "运行中 补发" : "运行中",
                " " + DispatchRuntimeSystem.SlotStr(targetMinute) + tag);
        }

        private bool HasOpenStopSession(Entity vehicle)
        {
            return m_Runtime.m_StopSessionWaypointIndex.ContainsKey(vehicle);
        }

        private void OpenStopSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            m_Runtime.Bypass.ClearBypassHoldSkipped(vehicle);
            m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_Runtime.m_StopSessionLine[vehicle] = line;
            m_Runtime.m_StopSessionWaypointIndex[vehicle] = waypointIndex;
            m_Runtime.m_StopSessionArrivalFrame[vehicle] = nowFrame;
            m_Runtime.m_StopSessionBoardingChangeCount[vehicle] = 0;
            m_Runtime.m_DeparturePendingSinceFrame.Remove(vehicle);
        }

        private void ClearStopSession(Entity vehicle)
        {
            m_Runtime.m_StopSessionLine.Remove(vehicle);
            m_Runtime.m_StopSessionWaypointIndex.Remove(vehicle);
            m_Runtime.m_StopSessionArrivalFrame.Remove(vehicle);
            m_Runtime.m_StopSessionBoardingChangeCount.Remove(vehicle);
            m_Runtime.m_DeparturePendingSinceFrame.Remove(vehicle);
            m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
            m_Runtime.Bypass.ClearBypassHoldSkipped(vehicle);
        }

        private void StartDeparturePending(Entity vehicle, uint nowFrame)
        {
            if (!m_Runtime.m_DeparturePendingSinceFrame.ContainsKey(vehicle))
                m_Runtime.m_DeparturePendingSinceFrame[vehicle] = nowFrame;
        }

        private void CancelDeparturePending(Entity vehicle)
        {
            m_Runtime.m_DeparturePendingSinceFrame.Remove(vehicle);
        }

        public void ForceManualDepart(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            Entity blocker = m_Runtime.Bypass.TryGetLatchedBlocker(vehicle, out Entity latchedBlocker)
                ? latchedBlocker
                : Entity.Null;
            m_Runtime.Bypass.ClearVehicle(vehicle, "UI强制发车");
            m_Runtime.Bypass.MarkBypassHoldSkipped(vehicle, blocker);
            m_Runtime.m_ForcedMidStopBoardingGraceUntil[vehicle] = nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES;
            m_Runtime.m_CommandApplier.ForceDepart(vehicle, ref publicTransport, nowFrame, ecb);
            StartDeparturePending(vehicle, nowFrame);
        }

        private bool TryRecoverInvalidatedMidStopSession(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame,
            ref int currentWaypointIndex)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentWaypointIndex >= 0
                || HasOpenStopSession(vehicle)
                || !m_Runtime.m_InvalidatedMidStopRecoveryPending.Contains(vehicle))
            {
                return false;
            }

            int recoveredWaypointIndex = m_Runtime.m_WaypointIndex.Compute(vehicle, waypoints);
            if (recoveredWaypointIndex <= 0)
                return false;

            m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
            currentWaypointIndex = recoveredWaypointIndex;
            m_Runtime.m_CachedWpIdx[vehicle] = recoveredWaypointIndex;
            m_Runtime.m_StopSessionLine[vehicle] = line;
            m_Runtime.m_StopSessionWaypointIndex[vehicle] = recoveredWaypointIndex;
            m_Runtime.m_StopSessionArrivalFrame[vehicle] = nowFrame;
            m_Runtime.m_StopSessionBoardingChangeCount[vehicle] = 0;
            CancelDeparturePending(vehicle);
            m_Runtime.TrackProjection.NoteVehicleProgressSuspectRecoveryBoarding(vehicle, recoveredWaypointIndex);
            m_Runtime.m_Observation.ClearForcedMidStop(vehicle);
            return true;
        }

        private void ObserveOfficialBoarding(Entity vehicle, bool officialBoarding)
        {
            byte current = BoardingByte(officialBoarding);
            if (m_Runtime.m_LastOfficialBoardingState.TryGetValue(vehicle, out byte previous)
                && previous != current
                && HasOpenStopSession(vehicle))
            {
                uint changes = m_Runtime.m_StopSessionBoardingChangeCount.TryGetValue(vehicle, out uint existing)
                    ? existing
                    : 0;
                m_Runtime.m_StopSessionBoardingChangeCount[vehicle] = changes + 1;
            }

            m_Runtime.m_LastOfficialBoardingState[vehicle] = current;
        }

        private bool IsVehicleMovingForDeparture(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.Exists(vehicle)
                || !EntityManager.HasComponent<Game.Objects.Moving>(vehicle))
            {
                return false;
            }

            float3 velocity = EntityManager.GetComponentData<Game.Objects.Moving>(vehicle).m_Velocity;
            return math.lengthsq(velocity) > DEPARTURE_MOVING_SPEED_SQ;
        }

        private void CompleteObservedDeparture(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            VehicleState state,
            int waypointIndex,
            int waypointCount,
            uint nowFrame,
            int nowMinute,
            string lineTag)
        {
            if (vehicle == Entity.Null
                || line == Entity.Null
                || waypointIndex < 0
                || waypointIndex >= waypointCount
                || waypointIndex >= waypoints.Length)
            {
                return;
            }

            uint officialBoardingChanges = m_Runtime.m_StopSessionBoardingChangeCount.TryGetValue(vehicle, out uint changes)
                ? changes
                : 0;
            uint pendingFrames = m_Runtime.m_DeparturePendingSinceFrame.TryGetValue(vehicle, out uint pendingSince)
                && nowFrame >= pendingSince
                    ? nowFrame - pendingSince
                    : 0;

            m_Runtime.m_Observation.TryRecordObservedStopDwellOnBoardingEnd(vehicle, line, waypointIndex, nowFrame);
            m_Runtime.m_WorkbenchBridge.ObservationStops().Record(vehicle, line, waypoints, false, -1, waypointIndex);
            m_Runtime.m_Announcements.ServiceEnded(vehicle, line, waypoints, waypointIndex);
            if (state == VehicleState.Running)
            {
                if (waypointIndex == waypointCount - 1)
                    this.MarkInbound(vehicle);
                else if (waypointIndex > 0 && waypointIndex < waypointCount - 1)
                    this.ClearInbound(vehicle);
            }

            if (state == VehicleState.Running && DispatchRuntimeSystem.IsDepartureObserveLoggingEnabled())
            {
                StopRef departedStop = m_Runtime.m_Resolve.StopRef(
                    waypoints[waypointIndex].m_Waypoint,
                    m_Runtime.m_WorkbenchBridge.ObservationStops().Latest(vehicle));
                Entity departedStopEntity = departedStop.Ent;
                Entity departedStopBuilding = departedStop.Kind == ResolvedStopKind.Building
                    ? departedStop.Ent
                    : m_Runtime.m_SharedCorridor.GetStationBuildingForWaypoint(waypoints, waypointIndex);
                string departedStopName = departedStop.Kind == ResolvedStopKind.Building
                    ? m_Runtime.EntityName(departedStopEntity)
                    : m_Runtime.EntityName(departedStopBuilding);
                if (string.IsNullOrWhiteSpace(departedStopName))
                {
                    departedStopName = "stop#" + departedStopEntity.Index;
                }

                int nextWaypointIndex = waypointIndex + 1 < waypointCount
                    ? waypointIndex + 1
                    : -1;
                Entity nextStop = nextWaypointIndex >= 0
                    ? m_Runtime.m_SharedCorridor.GetStationBuildingForWaypoint(waypoints, nextWaypointIndex)
                    : Entity.Null;
                string nextStopName = nextStop != Entity.Null
                    ? m_Runtime.EntityName(nextStop)
                    : string.Empty;
                if (nextStop != Entity.Null && string.IsNullOrWhiteSpace(nextStopName))
                {
                    nextStopName = "stop#" + nextStop.Index;
                }

                string departureKey = DispatchRuntimeSystem.SlotStr(nowMinute)
                    + "|wp=" + waypointIndex.ToString()
                    + "|next=" + nextWaypointIndex.ToString();
                if (RtLog.VerboseEnabled)
                {
                    m_Runtime.m_RuntimeLog.Once(
                        m_Runtime.m_RuntimeLog.m_DepartureObserveLogCache,
                        vehicle,
                        departureKey,
                        "[离站观察] " + lineTag
                        + " 车辆" + vehicle.Index
                        + " 从\"" + departedStopName + "\"离站"
                        + (nextWaypointIndex >= 0 ? " next=\"" + nextStopName + "\"" : " next=\"-\"")
                        + " state=" + state.ToString()
                        + " officialBoardingChanges=" + officialBoardingChanges
                        + " departurePendingFrames=" + pendingFrames);
                }
            }

            m_Runtime.TrackProjection.TryClearVehicleProgressSuspectOnStableDeparture(vehicle, waypointIndex);
            m_Runtime.Bypass.ClearBypassHoldSkipped(vehicle);
            m_Runtime.m_CachedWpIdx[vehicle] = -1;
            ClearStopSession(vehicle);
            m_Runtime.m_BVMisfire.Remove(vehicle);
            m_Runtime.m_BVMisfireStartFrame.Remove(vehicle);
            m_Runtime.m_Observation.ClearForcedMidStop(vehicle);
            m_Runtime.m_Observation.ClearDwellDeadlineCache(vehicle);
            m_Runtime.m_ObsPersist.ClearDwell(vehicle);
        }

        public void Adopt(Entity vehicle, Entity line, VehicleState state, uint nowFrame, uint? dispatchFrame)
        {
            m_Vehicles.Track(vehicle, line);
            m_Vehicles.SetState(vehicle, state);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.ClearBoardingGrace(vehicle);

            if (state == VehicleState.Preparing)
                m_Vehicles.SetPreparing(vehicle, nowFrame);
            else
                m_Vehicles.ClearPreparing(vehicle);

            if (dispatchFrame.HasValue)
            {
                m_Vehicles.SetDispatch(vehicle, dispatchFrame.Value);
                m_Runtime.m_Observation.BeginDispatchEta(vehicle, line, dispatchFrame.Value);
            }
            else
                m_Vehicles.ClearDispatch(vehicle);
        }

        public void Retire(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Retiring);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearDispatch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.ClearBoardingGrace(vehicle);
        }

        internal void CaptureRetireSpawnTarget(
            Entity line,
            out int preActive,
            out bool hadSpawnTarget,
            out int oldSpawnTarget)
        {
            m_LineSpawnControl.CaptureRetireTarget(
                line,
                out preActive,
                out hadSpawnTarget,
                out oldSpawnTarget);
        }

        internal void ApplyRetireSpawnTarget(
            Entity line,
            int preActive,
            bool hadSpawnTarget,
            int oldSpawnTarget)
        {
            m_LineSpawnControl.ApplyRetireTarget(
                line,
                preActive,
                hadSpawnTarget,
                oldSpawnTarget);
        }

        public void Hold(Entity vehicle, uint startFrame, ClockSnapshot clockSnapshot)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.SetReady(
                vehicle,
                startFrame,
                DispatchRuntimeSystem.PREPARING_ORIGIN_SETTLE_MINUTES,
                clockSnapshot);
        }

        public void HoldFromIdle(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void RecoverToHolding(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void Launch(Entity vehicle, int slot, uint nowFrame, uint cooldownUntil)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearReady(vehicle);
            m_Vehicles.SetLaunch(vehicle, nowFrame);
            m_Vehicles.SetCooldown(vehicle, cooldownUntil);
            m_Vehicles.SetSlot(vehicle, slot);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void Run(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void RestoreHold(Entity vehicle, int targetMinute)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Holding);
            m_Vehicles.SetTarget(vehicle, targetMinute);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
        }

        public void RestoreRun(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void RecoverToIdle(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Idle);
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.SetIdle(vehicle, nowFrame);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
            m_Vehicles.ClearReady(vehicle);
        }

        public void ArriveIdle(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Idle);
            m_Vehicles.ClearSlot(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearCooldown(vehicle);
            m_Vehicles.ClearInbound(vehicle);
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void Reevaluate(Entity vehicle)
        {
            m_Vehicles.ClearTarget(vehicle);
            m_Vehicles.ClearPreparing(vehicle);
            m_Vehicles.ClearDispatch(vehicle);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void Target(Entity vehicle, int targetMinute)
        {
            m_Vehicles.SetTarget(vehicle, targetMinute);
        }

        public void ReleaseTarget(Entity vehicle)
        {
            m_Vehicles.ClearTarget(vehicle);
        }

        public void MarkInbound(Entity vehicle)
        {
            m_Vehicles.MarkInbound(vehicle);
        }

        public void ClearInbound(Entity vehicle)
        {
            m_Vehicles.ClearInbound(vehicle);
        }

        public void SetPreparing(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetPreparing(vehicle, nowFrame);
        }

        public void SetDispatch(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetDispatch(vehicle, nowFrame);
        }

        public void ClearDispatch(Entity vehicle)
        {
            m_Vehicles.ClearDispatch(vehicle);
        }

        public void SetReady(Entity vehicle, uint startFrame, ClockSnapshot clockSnapshot)
        {
            m_Vehicles.SetReady(
                vehicle,
                startFrame,
                DispatchRuntimeSystem.FORCED_ORIGIN_MIN_DWELL_MINUTES,
                clockSnapshot);
        }

        public void ClearReady(Entity vehicle)
        {
            m_Vehicles.ClearReady(vehicle);
        }

        public void ClearOriginCandidate(Entity vehicle)
        {
            m_Vehicles.ClearOriginCandidate(vehicle);
        }

        public void SetOriginCandidate(Entity vehicle, uint frame)
        {
            m_Vehicles.SetOriginCandidate(vehicle, frame);
        }

        public void SetBoardingGrace(Entity vehicle, uint frame)
        {
            m_Vehicles.SetBoardingGrace(vehicle, frame);
        }

        public void ClearBoardingGrace(Entity vehicle)
        {
            m_Vehicles.ClearBoardingGrace(vehicle);
        }

        public void ArmAssistLaunchPending(Entity vehicle, Entity line, int targetMinute)
        {
            if (vehicle == Entity.Null || line == Entity.Null || targetMinute < 0)
                return;

            m_AssistLaunchPendingByVehicle[vehicle] = new AssistLaunchPendingRecord(line, targetMinute);
        }

        public void ClearAssistLaunchPending(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_AssistLaunchPendingByVehicle.Remove(vehicle);
            m_PreparingWaypointLiveFrames.Remove(vehicle);
        }

        public void ClearAssistLaunchPending()
        {
            m_AssistLaunchPendingByVehicle.Clear();
            m_PreparingWaypointLiveFrames.Clear();
        }

        private bool TryGetAssistLaunchPending(
            Entity vehicle,
            Entity line,
            int targetMinute,
            out AssistLaunchPendingRecord pending)
        {
            if (vehicle != Entity.Null
                && m_AssistLaunchPendingByVehicle.TryGetValue(vehicle, out pending)
                && pending.Line == line
                && pending.TargetMinute >= 0
                && targetMinute == pending.TargetMinute)
            {
                return true;
            }

            pending = default;
            return false;
        }

        public bool TryGetAssistPendingTarget(
            Entity vehicle,
            Entity line,
            int targetMinute,
            out int assistTargetMinute)
        {
            if (TryGetAssistLaunchPending(vehicle, line, targetMinute, out AssistLaunchPendingRecord pending))
            {
                assistTargetMinute = pending.TargetMinute;
                return true;
            }

            assistTargetMinute = -1;
            return false;
        }

        public void SetIdle(Entity vehicle, uint frame)
        {
            m_Vehicles.SetIdle(vehicle, frame);
        }

        public void ClearIdle(Entity vehicle)
        {
            m_Vehicles.ClearIdle(vehicle);
        }

        public void Tick(EntityCommandBuffer ecb, ClockSnapshot clockSnapshot)
        {
            int nowMinute = clockSnapshot.NowMinute;
            TickVehicles(ecb, nowMinute, clockSnapshot);
            m_Runtime.m_Announcements.Tick(m_Runtime.m_SimulationSystem.frameIndex);
            m_Runtime.m_CommandApplier.FinalizeRetireDispatchLockTerminals();
            m_RuntimeVehicleCleanup.Tick();
            TickLineControls(nowMinute);
            m_SchedulerApply.Tick(ecb, clockSnapshot);
            m_Runtime.m_CommandApplier.TickRetireHandoffStages(m_Runtime.m_SimulationSystem.frameIndex);
        }

        private void TickVehicles(EntityCommandBuffer ecb, int nowMinute, ClockSnapshot clockSnapshot)
        {
            var wpBuffers = m_Runtime.GetBufferLookup<RouteWaypoint>(true);
            var publicTransportLookup = m_Runtime.GetComponentLookup<Game.Vehicles.PublicTransport>(true);
            var targetLookup = m_Runtime.GetComponentLookup<Target>(true);
            var currentRouteLookup = m_Runtime.GetComponentLookup<CurrentRoute>(true);
            var vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);

            try
            {
                foreach (var v in vehicles)
                {
                    if (!EntityManager.Exists(v)) continue;
                    if (!m_Runtime.m_VehicleView.TryGetState(v, out var state)) continue;
                    int targetMinute = m_Runtime.m_VehicleView.TryGetTarget(v, out int tm) ? tm : -1;
                    Entity line = m_Runtime.m_VehicleView.TryGetLine(v, out Entity mappedLine)
                        ? mappedLine
                        : Entity.Null;
                    if (line == Entity.Null) continue;
                    if (!publicTransportLookup.HasComponent(v)
                        || !targetLookup.HasComponent(v)
                        || !currentRouteLookup.HasComponent(v))
                    {
                        if (RtLog.VerboseEnabled
                            && (targetMinute >= 0 || state == VehicleState.Holding || state == VehicleState.Preparing))
                        {
                            m_Runtime.m_RuntimeLog.Once(
                                m_Runtime.m_RuntimeLog.m_OriginDispatchTraceLogCache,
                                v,
                                "runtime-skip-core|state=" + state
                                    + "|target=" + targetMinute
                                    + "|pt=" + (publicTransportLookup.HasComponent(v) ? "1" : "0")
                                    + "|tgt=" + (targetLookup.HasComponent(v) ? "1" : "0")
                                    + "|route=" + (currentRouteLookup.HasComponent(v) ? "1" : "0"),
                                "[OriginDispatchTrace] reason=runtime-skip-core line=" + line.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + state
                                    + " target=" + Dispatch.Diagnostics.RuntimeLog.Slot(targetMinute)
                                    + " hasPublicTransport=" + (publicTransportLookup.HasComponent(v) ? "1" : "0")
                                    + " hasTarget=" + (targetLookup.HasComponent(v) ? "1" : "0")
                                    + " hasCurrentRoute=" + (currentRouteLookup.HasComponent(v) ? "1" : "0"));
                        }
                        continue;
                    }

                    var pt = publicTransportLookup[v];
                    var tgt = targetLookup[v];
                    var cr = currentRouteLookup[v];
                    Entity routeEnt = cr.m_Route;

                    if (!wpBuffers.TryGetBuffer(routeEnt, out var wps) || wps.Length < 2)
                    {
                        if (RtLog.VerboseEnabled
                            && (targetMinute >= 0 || state == VehicleState.Holding || state == VehicleState.Preparing))
                        {
                            m_Runtime.m_RuntimeLog.Once(
                                m_Runtime.m_RuntimeLog.m_OriginDispatchTraceLogCache,
                                v,
                                "runtime-skip-wps|route=" + routeEnt.Index,
                                "[OriginDispatchTrace] reason=runtime-skip-wps line=" + line.Index
                                    + " route=" + routeEnt.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + state
                                    + " target=" + Dispatch.Diagnostics.RuntimeLog.Slot(targetMinute)
                                    + " hasWpBuffer=" + (wpBuffers.TryGetBuffer(routeEnt, out _) ? "1" : "0"));
                        }
                        continue;
                    }
                    int waypointCount = wps.Length;

                    bool officialBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
                    ObserveOfficialBoarding(v, officialBoarding);
                    bool boarding = officialBoarding;
                    uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
                    bool suppressForcedMidStopBoardingGhost = state == VehicleState.Running
                        && boarding
                        && m_Runtime.m_Observation.IsSuppressedMidStopGhost(v, tgt, wps, nowFrame, out _);
                    if (suppressForcedMidStopBoardingGhost)
                    {
                        boarding = false;
                        m_Runtime.m_BVMisfire.Remove(v);
                        m_Runtime.m_BVMisfireStartFrame.Remove(v);
                    }

                    Entity lineEnt = line;
                    string lineTag = "线路" + line.Index;
                    if (state == VehicleState.Retiring)
                    {
                        SetLocalizedVehicleLabel(v, "Returning", "回库中", " #" + v.Index);
                        continue;
                    }

                    bool allowOriginHoldingBoardingGhost = false;

                    if (!DispatchRuntimeSystem.IsBvMisfireEnforcementEnabled() && m_Runtime.m_BVMisfire.Contains(v))
                    {
                        m_Runtime.m_BVMisfire.Remove(v);
                        m_Runtime.m_BVMisfireStartFrame.Remove(v);
                    }

                    if (m_Runtime.m_BVMisfire.Contains(v))
                    {
                        allowOriginHoldingBoardingGhost = state == VehicleState.Holding
                            && targetMinute >= 0
                            && m_Runtime.m_LineProfile.IsWithinOriginDistance(v, wps, ORIGIN_FORCE_IDLE_RADIUS_METERS);
                        if (allowOriginHoldingBoardingGhost)
                        {
                            m_Runtime.m_BVMisfire.Remove(v);
                            m_Runtime.m_BVMisfireStartFrame.Remove(v);
                            m_Runtime.m_LastEffectiveBoardingState[v] = 0;
                            m_Runtime.m_CachedWpIdx[v] = 0;
                            boarding = false;
                        }
                    }

                    if (m_Runtime.m_BVMisfire.Contains(v))
                    {
                        if (m_Runtime.m_BVMisfireStartFrame.TryGetValue(v, out uint misfireStart)
                            && (nowFrame - misfireStart) > BV_MISFIRE_TIMEOUT)
                        {
                            if (targetMinute >= 0)
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index
                                    + " 超时，释放班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 并回库");
                                this.ReleaseTarget(v);
                            }
                            else
                            {
                                log.Info("[BVMisfire] " + lineTag + " 车辆" + v.Index + " 超时，回库");
                            }
                            m_Runtime.m_BVMisfire.Remove(v);
                            m_Runtime.m_BVMisfireStartFrame.Remove(v);
                            m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, "BVMisfire超时");
                            continue;
                        }
                        int misfireCurWpIdx = m_Runtime.m_CachedWpIdx.TryGetValue(v, out int misfireCachedWpIdx) ? misfireCachedWpIdx : -1;
                        bool misfireAtA = state == VehicleState.Preparing
                            ? m_Runtime.m_LineProfile.HasPreparingReachedOrigin(v, wps, boarding, misfireCurWpIdx)
                            : (misfireCurWpIdx == 0);
                        if (RtLog.VerboseEnabled)
                        {
                            m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                "bv-misfire-latched",
                                v,
                                lineEnt,
                                routeEnt,
                                wps,
                                state,
                                targetMinute,
                                nowMinute,
                                misfireCurWpIdx,
                                misfireAtA,
                                boarding,
                                HasOpenStopSession(v),
                                nowFrame,
                                "misfireAgeFrames=" + (m_Runtime.m_BVMisfireStartFrame.TryGetValue(v, out uint loggedMisfireStart) ? (nowFrame - loggedMisfireStart).ToString() : "?"));
                        }
                        if (m_Runtime.m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint forcedDepartGraceUntil)
                            && nowFrame < forcedDepartGraceUntil)
                            SetLocalizedVehicleLabel(v, "StopTimeoutAssist", "停站超时协助中", " #" + v.Index);
                        else
                            SetLocalizedVehicleLabel(v, "PathFault", "寻路异常", " #" + v.Index);
                        continue;
                    }

                    bool inCooldown = m_Runtime.m_VehicleView.TryGetCooldown(v, out uint cooldownUntil)
                        && nowFrame < cooldownUntil;

                    bool lastBoarding = HasOpenStopSession(v);
                    bool lastEffectiveBoarding = m_Runtime.m_LastEffectiveBoardingState.TryGetValue(v, out byte lb) && lb != 0;
                    bool boardingChanged = !inCooldown && (boarding != lastEffectiveBoarding);
                    m_Runtime.m_LastEffectiveBoardingState[v] = BoardingByte(boarding);
                    int curWpIdx;
                    int previousCachedWpIdx = m_Runtime.m_CachedWpIdx.TryGetValue(v, out int prevCached) ? prevCached : -1;

                    if (boardingChanged && state != VehicleState.Idle)
                    {
                        if (!boarding)
                        {
                            bool suppressBypassDepartureBounce = false;
                            bool departureGateLatched = false;
                            Entity departureGateLatchedBlocker = Entity.Null;
                            bool departureGateShouldHold = false;
                            bool departureGateCanClearAfterExit = true;
                            Entity departureGateBlocker = Entity.Null;
                            bool departureGateSkipped = m_Runtime.Bypass.TryGetBypassHoldSkipped(v, out _);
                            if (state == VehicleState.Running
                                && previousCachedWpIdx > 0
                                && !departureGateSkipped)
                            {
                                if (m_Runtime.Bypass.TryGetLatchedBlocker(v, out Entity latchedBlocker)
                                    && latchedBlocker != Entity.Null
                                    && EntityManager.Exists(latchedBlocker))
                                {
                                    departureGateLatched = true;
                                    departureGateLatchedBlocker = latchedBlocker;
                                    departureGateShouldHold = true;
                                    departureGateCanClearAfterExit = false;
                                    departureGateBlocker = latchedBlocker;
                                    suppressBypassDepartureBounce = true;
                                }
                                else
                                {
                                    RapidTransitMod.Bypass.BypassDecisionResult departureGateDecision = m_Runtime.Bypass.EvaluateDepartureGate(
                                        v,
                                        lineEnt,
                                        wps,
                                        previousCachedWpIdx,
                                        nowFrame);
                                    departureGateLatched = departureGateDecision.HadLatchedYield;
                                    departureGateLatchedBlocker = departureGateDecision.LatchedBlocker;
                                    departureGateShouldHold = departureGateDecision.ShouldHold;
                                    departureGateCanClearAfterExit = departureGateDecision.CanClearAfterExit;
                                    departureGateBlocker = m_Runtime.Bypass.FindBlocker(departureGateDecision);
                                    suppressBypassDepartureBounce = departureGateShouldHold && !departureGateCanClearAfterExit;
                                }
                            }

                            if (suppressBypassDepartureBounce)
                            {
                                m_Runtime.Bypass.LogDepartureGate(
                                    v,
                                    "gate|suppress|" + previousCachedWpIdx + "|" + departureGateLatched + "|" + departureGateShouldHold + "|" + departureGateCanClearAfterExit,
                                    "[待避离站门] vehicle=" + v.Index
                                        + " line=" + lineEnt.Index
                                        + " state=" + state
                                        + " prevWp=" + previousCachedWpIdx
                                        + " liveWp=-"
                                        + " boarding=" + boarding
                                        + " lastBoarding=" + lastBoarding
                                        + " latched=" + departureGateLatched
                                        + " latchedBlocker=" + departureGateLatchedBlocker.Index
                                        + " shouldHold=" + departureGateShouldHold
                                        + " blocker=" + departureGateBlocker.Index
                                        + " canClear=" + departureGateCanClearAfterExit
                                        + " depFrame=" + pt.m_DepartureFrame
                                        + " action=suppress");
                                curWpIdx = previousCachedWpIdx;
                                m_Runtime.m_CachedWpIdx[v] = previousCachedWpIdx;
                                CancelDeparturePending(v);
                            }
                            else
                            {
                                curWpIdx = previousCachedWpIdx;
                                if (previousCachedWpIdx >= 0)
                                {
                                    m_Runtime.m_CachedWpIdx[v] = previousCachedWpIdx;
                                    StartDeparturePending(v, nowFrame);
                                }
                            }
                        }
                        else
                        {
                            if (HasOpenStopSession(v))
                            {
                                CancelDeparturePending(v);
                                curWpIdx = m_Runtime.m_StopSessionWaypointIndex.TryGetValue(v, out int sessionWaypointIndex)
                                    ? sessionWaypointIndex
                                    : previousCachedWpIdx;
                                m_Runtime.m_CachedWpIdx[v] = curWpIdx;
                            }
                            else
                            {
                                curWpIdx = m_Runtime.m_WaypointIndex.Compute(v, wps);
                                m_Runtime.m_CachedWpIdx[v] = curWpIdx;

                                if (curWpIdx >= 0)
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        if (m_Runtime.m_Observation.Head(v, curWpIdx, out TrainHeadSnapshot boardingHeadSnapshot))
                                            m_Runtime.m_RuntimeLog.RememberBoardingHead(v, boardingHeadSnapshot);
                                        else
                                            m_Runtime.m_RuntimeLog.ForgetBoardingHead(v);
                                    }
                                    m_Runtime.m_Observation.BeginObservedDwellSession(v, lineEnt, curWpIdx, nowFrame);
                                    m_Runtime.m_WorkbenchBridge.ObservationStops().Record(v, lineEnt, wps, true, curWpIdx, previousCachedWpIdx);
                                    m_Runtime.m_Announcements.StopOpened(v, lineEnt, wps, curWpIdx);
                                    OpenStopSession(v, lineEnt, curWpIdx, nowFrame);
                                    m_Runtime.TrackProjection.NoteVehicleProgressSuspectRecoveryBoarding(v, curWpIdx);
                                    m_Runtime.m_BVMisfire.Remove(v);
                                    m_Runtime.m_BVMisfireStartFrame.Remove(v);
                                    m_Runtime.m_Observation.ClearForcedMidStop(v);
                                }
                                else
                                {
                                    m_Runtime.m_RuntimeLog.BvMisfireCandidate(
                                        v,
                                        lineTag,
                                        "boarding-change",
                                        "boarding-without-waypoint",
                                        nowFrame);
                                }
                            }
                        }

                        if (state == VehicleState.Running)
                        {
                            if (curWpIdx < 0 && boarding)
                            {
                                log.Info("[boarding变化] " + lineTag + " 车辆" + v.Index + " BV误写，标记misfire");
                            }
                        }
                    }
                    else
                    {
                        curWpIdx = m_Runtime.m_CachedWpIdx.TryGetValue(v, out int ci) ? ci : -1;
                    }

                    if (state == VehicleState.Running
                        && boarding
                        && curWpIdx < 0
                        && !lastBoarding)
                    {
                        if (TryRecoverInvalidatedMidStopSession(
                            v,
                            lineEnt,
                            wps,
                            nowFrame,
                            ref curWpIdx))
                        {
                            lastBoarding = true;
                        }
                    }

                    if (!boarding
                        && m_Runtime.m_DeparturePendingSinceFrame.ContainsKey(v)
                        && IsVehicleMovingForDeparture(v))
                    {
                        int departureWaypointIndex = m_Runtime.m_StopSessionWaypointIndex.TryGetValue(v, out int sessionDepartureWaypoint)
                            ? sessionDepartureWaypoint
                            : previousCachedWpIdx;
                        CompleteObservedDeparture(
                            v,
                            lineEnt,
                            wps,
                            state,
                            departureWaypointIndex,
                            waypointCount,
                            nowFrame,
                            nowMinute,
                            lineTag);
                        curWpIdx = -1;
                        previousCachedWpIdx = -1;
                        lastBoarding = false;
                    }

                    if (state == VehicleState.Preparing)
                    {
                        m_Runtime.Bypass.ClearVehicle(v);
                        bool hasPreparingStartFrame = m_Runtime.m_VehicleView.TryGetPreparing(v, out uint preparingStartFrame);
                        bool shouldRefreshPreparingWaypoint = boardingChanged
                            || !m_PreparingWaypointLiveFrames.TryGetValue(v, out uint lastPreparingWaypointFrame)
                            || (hasPreparingStartFrame && preparingStartFrame > lastPreparingWaypointFrame)
                            || nowFrame <= lastPreparingWaypointFrame
                            || nowFrame - lastPreparingWaypointFrame >= PREPARING_WAYPOINT_LIVE_REFRESH_FRAMES;
                        if (shouldRefreshPreparingWaypoint)
                        {
                            m_PreparingWaypointLiveFrames[v] = nowFrame;
                            int liveWpIdx = m_Runtime.m_WaypointIndex.Compute(v, wps);
                            if (liveWpIdx >= 0 && liveWpIdx != curWpIdx)
                            {
                                curWpIdx = liveWpIdx;
                                m_Runtime.m_CachedWpIdx[v] = liveWpIdx;
                            }
                        }
                    }

                    if (state != VehicleState.Running)
                    {
                        if (m_Runtime.m_Observation.DropSlice(v, out int droppedSliceIndex))
                            m_Runtime.m_Observation.DebugDrop(v, droppedSliceIndex);
                    }

                    bool atA = state == VehicleState.Preparing
                        ? m_Runtime.m_LineProfile.HasPreparingReachedOrigin(v, wps, boarding, curWpIdx)
                        : (curWpIdx == 0);
                    bool broadcastOriginWaitBusy = atA
                        || boarding
                        || m_Runtime.m_VehicleStateStore.ForcedOriginReadyFrame.ContainsKey(v)
                        || (state != VehicleState.Preparing
                            && m_Runtime.m_CachedWpIdx.TryGetValue(v, out int broadcastCachedWpIdx)
                            && broadcastCachedWpIdx == 0);
                    if (state == VehicleState.Preparing)
                        m_Runtime.m_RuntimeLog.PreparingTargetDrift(lineEnt, v, routeEnt, wps[0].m_Waypoint, tgt.m_Target, targetMinute, curWpIdx, boarding, atA);
                    bool midStopBoarding = state == VehicleState.Running
                        && boarding
                        && curWpIdx > 0;
                    if (state == VehicleState.Running)
                        m_Runtime.m_RuntimeHotPathProbe.CountRunning(boardingChanged, boarding, midStopBoarding, curWpIdx);
                    uint midStopDwellSinceFrame = 0;
                    uint midStopDwellDeadlineFrame = 0;
                    int maxStationDwellMinutes = 0;
                    bool midStopDwellTimedOut = midStopBoarding
                        && m_Runtime.m_Observation.Dwell(
                            v,
                            lineEnt,
                            curWpIdx,
                            boarding,
                            nowFrame,
                            waypointCount,
                            out midStopDwellSinceFrame,
                            out midStopDwellDeadlineFrame,
                            out maxStationDwellMinutes);
                    string vTag = " #" + v.Index;

                    switch (state)
                    {
                        case VehicleState.Preparing:
                            m_Runtime.m_Announcements.Preparing(v, routeEnt, wps, atA, nowFrame);
                            m_Runtime.m_Observation.TryRequestDispatchEta(v, lineEnt, wps, nowFrame);

                            if (targetMinute >= 0 && ScheduleClock.SoftExpired(nowMinute, targetMinute) && !ScheduleClock.CanLate(nowMinute, targetMinute))
                            {
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.Once(
                                        m_Runtime.m_RuntimeLog.m_PreparingSlotLogCache,
                                        v,
                                        "PreparingSlot|" + targetMinute + "|" + overdueMinutes,
                                        "[PreparingSlot] " + lineTag + " 车辆" + v.Index
                                            + " 班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 已过期(" + overdueMinutes + "分钟)，释放重新调度");
                                }
                                this.ReleaseTarget(v);
                                targetMinute = -1;
                            }

                            if (atA)
                            {
                                int preparingAssignedTargetMinute = -1;
                                if (targetMinute < 0 && m_Runtime.m_DispatchScheduler.Plan.TryAssignUpcomingTarget(
                                    routeEnt,
                                    v,
                                    nowMinute,
                                    lineTag,
                                    "Preparing",
                                    out preparingAssignedTargetMinute))
                                {
                                    Target(v, preparingAssignedTargetMinute);
                                    targetMinute = preparingAssignedTargetMinute;
                                }

                                if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                                {
                                    m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                    break;
                                }
                                this.Hold(v, nowFrame, clockSnapshot);
                                m_Runtime.m_Observation.Seed(v, lineEnt, nowFrame);
                                m_Runtime.m_SelectPanel.RecordLineHoldingSummary(lineEnt, nowMinute, v, targetMinute);
                                if (targetMinute >= 0)
                                {
                                    m_Runtime.m_Observation.BindTarget(routeEnt, v, targetMinute, nowFrame, "preparing-holding-assign");
                                }
                                m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                if (targetMinute >= 0)
                                {
                                    SetHoldingVehicleLabel(v, targetMinute, vTag);
                                    if (RtLog.VerboseEnabled)
                                        log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，预分配 " + DispatchRuntimeSystem.SlotStr(targetMinute));
                                }
                                else
                                {
                                    SetHoldingVehicleLabel(v, -1, vTag);
                                    if (RtLog.VerboseEnabled)
                                        log.Info("[Preparing->Holding] " + lineTag + " 车辆" + v.Index + " 到站，等待调度");
                                }
                            }
                            else
                            {
                                m_Runtime.m_CommandApplier.EnsurePreparingRoute(v, ref pt, ref tgt, wps, curWpIdx, boarding, ecb);
                                SetLocalizedVehicleLabel(v, "GoingOrigin", "前往始发站", (targetMinute >= 0 ? " " + DispatchRuntimeSystem.SlotStr(targetMinute) : "") + vTag);
                            }
                            break;

                        case VehicleState.Holding:
                            m_Runtime.m_Announcements.Origin(routeEnt, wps, broadcastOriginWaitBusy);

                            if (!atA)
                            {
                                if (TryGetAssistLaunchPending(v, routeEnt, targetMinute, out AssistLaunchPendingRecord assistPending))
                                {
                                    int assistedTargetMinute = assistPending.TargetMinute;
                                    bool isLateAssistLaunch = ScheduleClock.CanLate(nowMinute, assistedTargetMinute);
                                    this.Launch(v, assistedTargetMinute, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                    m_Runtime.Bypass.RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-assist-launch-sync");
                                    m_Runtime.m_JustLaunched.Add(v);
                                    m_Runtime.m_Observation.Record(v, isLateAssistLaunch ? "协助补发确认" : "协助发车确认");
                                    ClearStopSession(v);
                                    m_Runtime.m_LastEffectiveBoardingState[v] = 0;
                                    m_Runtime.m_CachedWpIdx[v] = -1;
                                    m_Runtime.m_BVMisfire.Remove(v);
                                    m_Runtime.m_BVMisfireStartFrame.Remove(v);
                                    m_Runtime.m_Observation.Launch(routeEnt, v, assistedTargetMinute, nowMinute, nowFrame, isLateAssistLaunch);
                                    ClearAssistLaunchPending(v);
                                    pt.m_DepartureFrame = nowFrame > 0 ? nowFrame - 1 : 0;
                                    pt.m_State &= ~PublicTransportFlags.Boarding;
                                    m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                    SetRunningSlotVehicleLabel(v, isLateAssistLaunch, assistedTargetMinute, vTag);
                                    if (RtLog.VerboseEnabled)
                                    {
                                        log.Info("[AssistLaunchSync] " + lineTag + " 车辆" + v.Index
                                            + " 在始发发车协助后已离站，补记班次" + DispatchRuntimeSystem.SlotStr(assistedTargetMinute)
                                            + " 于 " + DispatchRuntimeSystem.SlotStr(nowMinute)
                                            + (isLateAssistLaunch ? " late=1" : " late=0"));
                                    }
                                    break;
                                }
                                if (m_Runtime.m_Observation.IsWaitingOriginDwell(v, nowFrame))
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                            "holding-not-at-origin-forced-dwell",
                                            v,
                                            lineEnt,
                                            routeEnt,
                                            wps,
                                            state,
                                            targetMinute,
                                            nowMinute,
                                            curWpIdx,
                                            atA,
                                            boarding,
                                            lastBoarding,
                                            nowFrame);
                                    }
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetHoldingVehicleLabel(v, targetMinute, vTag);
                                    break;
                                }
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                        "holding-not-at-origin-abnormal-running",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMinute,
                                        nowMinute,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame,
                                        "assistPending=0");
                                }
                                this.Run(v);
                                m_Runtime.m_Observation.Record(v, "Holding异常离站");
                                SetLocalizedVehicleLabel(v, "RunningAbnormal", "运行中(异常)", vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Holding 时意外离站");
                                break;
                            }
                            if (targetMinute < 0)
                            {
                                int lateSlotMinute = -1;
                                int[] appliedTargets = m_Runtime.m_LineView.Times(routeEnt);
                                Entity releasedVehicle = Entity.Null;
                                bool assigned;
                                if (appliedTargets.Length > 0)
                                {
                                    assigned = m_Runtime.m_DispatchScheduler.Plan.TryAssignCurrentOrLateScheduledTarget(
                                        routeEnt,
                                        v,
                                        nowMinute,
                                        lineTag,
                                        "Holding",
                                        appliedTargets,
                                        out releasedVehicle,
                                        out lateSlotMinute);
                                }
                                else
                                {
                                    assigned = m_Runtime.m_DispatchScheduler.Plan.TryAssignCurrentOrLateSlot(
                                        routeEnt,
                                        v,
                                        nowMinute,
                                        lineTag,
                                        "Holding",
                                        out releasedVehicle,
                                        out lateSlotMinute);
                                }
                                if (assigned)
                                {
                                    if (releasedVehicle != Entity.Null)
                                        ReleaseTarget(releasedVehicle);
                                    Target(v, lateSlotMinute);
                                    targetMinute = lateSlotMinute;
                                    m_Runtime.m_Observation.BindTarget(routeEnt, v, targetMinute, nowFrame, "holding-assigned");
                                }
                                else if (m_Runtime.m_DispatchScheduler.Plan.TryAssignUpcomingTarget(
                                    routeEnt,
                                    v,
                                    nowMinute,
                                    lineTag,
                                    "Holding",
                                    out int upcomingTargetMinute))
                                {
                                    Target(v, upcomingTargetMinute);
                                    targetMinute = upcomingTargetMinute;
                                    m_Runtime.m_Observation.BindTarget(routeEnt, v, targetMinute, nowFrame, "holding-upcoming-assigned");
                                }
                                else
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                            "holding-no-target-demote-idle",
                                            v,
                                            lineEnt,
                                            routeEnt,
                                            wps,
                                            state,
                                            targetMinute,
                                            nowMinute,
                                            curWpIdx,
                                            atA,
                                            boarding,
                                            lastBoarding,
                                            nowFrame);
                                    }
                                    this.RecoverToIdle(v, nowFrame);
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetLocalizedVehicleLabel(v, "WaitingDispatch", "等待调度", vTag);
                                    break;
                                }
                            }

                            if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                            {
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                        "holding-far-future-retire",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMinute,
                                        nowMinute,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                }
                                m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                m_Runtime.Bypass.ClearVehicle(v);
                                break;
                            }

                            if (ScheduleClock.Reached(nowMinute, targetMinute) || ScheduleClock.CanLate(nowMinute, targetMinute))
                            {
                                m_Runtime.Bypass.ClearVehicle(v, "始发候车不参与待避");
                                if (m_Runtime.m_DispatchScheduler.Policy.IsOccupied(routeEnt, v, targetMinute))
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                            "holding-occupied-release",
                                            v,
                                            lineEnt,
                                            routeEnt,
                                            wps,
                                            state,
                                            targetMinute,
                                            nowMinute,
                                            curWpIdx,
                                            atA,
                                            boarding,
                                            lastBoarding,
                                            nowFrame);
                                    }
                                    this.ReleaseTarget(v);
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetHoldingVehicleLabel(v, -1, vTag);
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_HoldingSkipLogCache,
                                            v,
                                            "HoldingSkip|" + targetMinute,
                                            "[HoldingSkip] " + lineTag + " 车辆" + v.Index
                                                + " 班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 已被其他车辆占用，释放重调度");
                                    }
                                    break;
                                }

                                if (m_Runtime.m_Observation.IsWaitingOriginDwell(v, nowFrame))
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                            "holding-time-reached-forced-dwell",
                                            v,
                                            lineEnt,
                                            routeEnt,
                                            wps,
                                            state,
                                            targetMinute,
                                            nowMinute,
                                            curWpIdx,
                                            atA,
                                            boarding,
                                            lastBoarding,
                                            nowFrame);
                                    }
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                    SetHoldingVehicleLabel(v, targetMinute, vTag, ScheduleClock.CanLate(nowMinute, targetMinute));
                                    break;
                                }
                                if (boarding)
                                {
                                    bool shouldRefreshOriginAssist = !m_Runtime.m_VehicleView.TryGetBoardingGrace(v, out uint originBoardingGraceUntil)
                                        || nowFrame >= originBoardingGraceUntil;
                                    if (shouldRefreshOriginAssist)
                                    {
                                        m_Runtime.m_CommandApplier.ForceDepart(v, ref pt, nowFrame, ecb);
                                        this.SetBoardingGrace(v, nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES);
                                        if (RtLog.VerboseEnabled)
                                        {
                                            log.Info("[始发发车协助] " + lineTag + " 车辆" + v.Index
                                                + " 班次" + DispatchRuntimeSystem.SlotStr(targetMinute)
                                                + " wp=" + curWpIdx);
                                        }
                                    }
                                    ArmAssistLaunchPending(v, routeEnt, targetMinute);
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                            "holding-boarding-assist-pending",
                                            v,
                                            lineEnt,
                                            routeEnt,
                                            wps,
                                            state,
                                            targetMinute,
                                            nowMinute,
                                            curWpIdx,
                                            atA,
                                            boarding,
                                            lastBoarding,
                                            nowFrame,
                                            "assistRefreshed=" + (shouldRefreshOriginAssist ? "1" : "0"));
                                    }
                                    SetLocalizedVehicleLabel(v, "BoardingEnd", "结束上客", " " + DispatchRuntimeSystem.SlotStr(targetMinute) + vTag);
                                    break;
                                }
                                bool isLateDispatch = ScheduleClock.CanLate(nowMinute, targetMinute);
                                int overdueMinutes = isLateDispatch ? ScheduleClock.Overdue(nowMinute, targetMinute) : 0;
                                bool hasLaunchHeadSnapshot = m_Runtime.m_Observation.Head(v, curWpIdx, out TrainHeadSnapshot currentLaunchHeadSnapshot);
                                string headDiagnostic = RtLog.VerboseEnabled
                                    ? m_Runtime.m_RuntimeLog.TrainHeadLaunch(v, hasLaunchHeadSnapshot, currentLaunchHeadSnapshot)
                                    : string.Empty;
                                m_Runtime.m_CommandApplier.Launch(v, pt, tgt, wps, ecb);
                                this.Launch(v, targetMinute, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                m_Runtime.Bypass.RequestLineOrderedRuntimeForceRefresh(routeEnt, "origin-launch");
                                m_Runtime.m_JustLaunched.Add(v);
                                if (RtLog.VerboseEnabled)
                                {
                                    if (hasLaunchHeadSnapshot)
                                        m_Runtime.m_RuntimeLog.RememberLaunchHead(v, currentLaunchHeadSnapshot);
                                    else
                                        m_Runtime.m_RuntimeLog.ForgetLaunchHead(v);
                                }
                                m_Runtime.m_Observation.Record(v, isLateDispatch ? "补发" : "计划发车");
                                ClearStopSession(v);
                                m_Runtime.m_LastEffectiveBoardingState[v] = 0;
                                m_Runtime.m_CachedWpIdx[v] = -1;
                                m_Runtime.m_BVMisfire.Remove(v);
                                m_Runtime.m_BVMisfireStartFrame.Remove(v);
                                m_Runtime.m_Observation.Launch(routeEnt, v, targetMinute, nowMinute, nowFrame, isLateDispatch);
                                m_Runtime.m_WorkbenchBridge.ObservationStops().Start(v, lineEnt, wps);
                                if (RtLog.VerboseEnabled)
                                    log.Info("[LaunchHeadCheck] " + lineTag + " vehicle" + v.Index + headDiagnostic);
                                SetRunningSlotVehicleLabel(v, isLateDispatch, targetMinute, vTag);
                                string spawnIntent = m_Runtime.m_SpawnIntentTrace.Launch(v, targetMinute, nowFrame);
                                if (isLateDispatch)
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                                            v,
                                            "LateDispatchLaunch|" + targetMinute,
                                            "[补发] " + lineTag + " 车辆" + v.Index
                                                + " 于 " + DispatchRuntimeSystem.SlotStr(nowMinute) + " 补发（班次 " + DispatchRuntimeSystem.SlotStr(targetMinute) + "）"
                                                + " 已过期" + overdueMinutes + "分钟"
                                                + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES)
                                                + spawnIntent);
                                    }
                                }
                                else
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        log.Info("[发车] " + lineTag + " 车辆" + v.Index
                                            + " 于 " + DispatchRuntimeSystem.SlotStr(nowMinute) + " 发车（班次 " + DispatchRuntimeSystem.SlotStr(targetMinute) + "）"
                                            + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES)
                                            + spawnIntent);
                                    }
                                }
                            }
                            else if (ScheduleClock.HardExpired(nowMinute, targetMinute))
                            {
                                m_Runtime.Bypass.ClearVehicle(v);
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                        + " 班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 大幅过期(" + overdueMinutes + "分钟)，直接回库");
                                }
                                m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, "班次大幅过期" + overdueMinutes + "分钟");
                            }
                            else if (ScheduleClock.SoftExpired(nowMinute, targetMinute))
                            {
                                m_Runtime.Bypass.ClearVehicle(v);
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Holding] " + lineTag + " 车辆" + v.Index
                                        + " 班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 已过期(" + overdueMinutes + "分钟)，释放重新调度");
                                }
                                this.ReleaseTarget(v);
                                m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                SetHoldingVehicleLabel(v, -1, vTag);
                            }
                            else
                            {
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.OriginDispatchTrace(
                                        "holding-waiting-window",
                                        v,
                                        lineEnt,
                                        routeEnt,
                                        wps,
                                        state,
                                        targetMinute,
                                        nowMinute,
                                        curWpIdx,
                                        atA,
                                        boarding,
                                        lastBoarding,
                                        nowFrame);
                                }
                                m_Runtime.Bypass.ClearVehicle(v);
                                m_Runtime.m_CommandApplier.HoldDeparture(v, ref pt, nowFrame, ecb);
                                SetHoldingVehicleLabel(v, targetMinute, vTag, ScheduleClock.CanLate(nowMinute, targetMinute));
                            }
                            break;

                        case VehicleState.Running:
                            m_Runtime.m_Observation.UpdateSlice(v, lineEnt, wps, nowFrame);

                            m_Runtime.m_Announcements.Running(v, routeEnt, wps, curWpIdx, boarding);
                            Entity rescuedLocalVehicle = m_Runtime.Bypass.TickExpressVanillaBlockerRescue(v, lineEnt, nowFrame);
                            if (rescuedLocalVehicle != Entity.Null
                                && EntityManager.Exists(rescuedLocalVehicle)
                                && EntityManager.HasComponent<Game.Vehicles.PublicTransport>(rescuedLocalVehicle))
                            {
                                Game.Vehicles.PublicTransport rescuedPublicTransport = EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(rescuedLocalVehicle);
                                m_Runtime.m_CommandApplier.ForceDepart(rescuedLocalVehicle, ref rescuedPublicTransport, nowFrame, ecb);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[待避防卡死放行] " + lineTag
                                        + " express=" + v.Index
                                        + " local=" + rescuedLocalVehicle.Index
                                        + " reason=vanilla-blocker-chain-stall");
                                }
                            }

                            int bypassControlWaypointIndex = curWpIdx >= 0 ? curWpIdx : previousCachedWpIdx;
                            bool bypassPreparedSkipped = m_Runtime.Bypass.TryGetBypassHoldSkipped(v, out _);
                            if (bypassPreparedSkipped)
                                m_Runtime.m_RuntimeHotPathProbe.CountBypassPreparedSkipped();
                            bool bypassPreparedLatched = m_Runtime.Bypass.TryGetLatchedBlocker(v, out _);
                            if (bypassPreparedLatched)
                                m_Runtime.m_RuntimeHotPathProbe.CountBypassPreparedLatched();

                            bool skipRunningBypassTick = false;
                            if (!boarding && !boardingChanged && !bypassPreparedLatched && !bypassPreparedSkipped)
                            {
                                m_Runtime.m_RuntimeHotPathProbe.CountBypassFastSkipNonBoarding();
                                skipRunningBypassTick = true;
                            }
                            else if (boarding
                                && !bypassPreparedLatched
                                && !bypassPreparedSkipped
                                && bypassControlWaypointIndex > 0)
                            {
                                m_Runtime.m_RuntimeHotPathProbe.CountBypassScenePrecheckCall();
                                bool sceneEligible = m_Runtime.Bypass.IsStopSceneEligible(
                                    routeEnt,
                                    wps,
                                    bypassControlWaypointIndex,
                                    out bool sceneKnown);
                                if (!sceneKnown)
                                {
                                    m_Runtime.m_RuntimeHotPathProbe.CountBypassScenePrecheckUnknown();
                                }
                                else if (sceneEligible)
                                {
                                    m_Runtime.m_RuntimeHotPathProbe.CountBypassScenePrecheckEligible();
                                }
                                else
                                {
                                    m_Runtime.m_RuntimeHotPathProbe.CountBypassScenePrecheckSkipped();
                                    skipRunningBypassTick = true;
                                }
                            }

                            RapidTransitMod.Bypass.BypassControlResult runningBypass = skipRunningBypassTick
                                ? new RapidTransitMod.Bypass.BypassControlResult(
                                    false,
                                    v,
                                    routeEnt,
                                    bypassControlWaypointIndex,
                                    false,
                                    false,
                                    Entity.Null,
                                    true,
                                    null)
                                : m_Runtime.Bypass.TickVehicle(
                                    v,
                                    routeEnt,
                                    wps,
                                    bypassControlWaypointIndex,
                                    boarding,
                                    ref pt,
                                    ecb,
                                    lineTag,
                                    midStopDwellTimedOut,
                                    nowFrame);
                            bool runningShouldHoldBypass = runningBypass.ShouldHold;
                            bool runningCanClearAfterExit = runningBypass.CanClearAfterExit;
                            Entity runningBypassBlocker = runningBypass.Blocker;
                            bool runningBypassLatched = runningBypass.HadLatchedYield;

                            if (midStopDwellTimedOut && !runningShouldHoldBypass)
                            {
                                bool shouldRefreshTimeoutAssist = !m_Runtime.m_ForcedMidStopBoardingGraceUntil.TryGetValue(v, out uint timeoutAssistGraceUntil)
                                    || nowFrame >= timeoutAssistGraceUntil;
                                if (shouldRefreshTimeoutAssist)
                                {
                                    m_Runtime.m_CommandApplier.ForceDepart(v, ref pt, nowFrame, ecb);
                                    m_Runtime.m_ForcedMidStopBoardingGraceUntil[v] = nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES;
                                    if (RtLog.VerboseEnabled)
                                    {
                                        string timeoutLogKey = midStopDwellSinceFrame.ToString();
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_MidStopTimeoutLogCache,
                                            v,
                                            timeoutLogKey,
                                            "[停站超时] " + lineTag + " 车辆" + v.Index
                                                + " 停站超时" + maxStationDwellMinutes + "分钟"
                                                + " sinceFrame=" + midStopDwellSinceFrame
                                                + " deadlineFrame=" + midStopDwellDeadlineFrame
                                                + " curWpIdx=" + curWpIdx
                                                + " nextTargetWp=" + (curWpIdx + 1 < waypointCount ? (curWpIdx + 1).ToString() : "-"));
                                    }
                                }
                                string timeoutBypassReleaseReason = !string.IsNullOrWhiteSpace(runningBypass.ReleaseReason)
                                    ? runningBypass.ReleaseReason
                                    : "timeout-close:no-bypass-release-reason";
                                m_Runtime.Bypass.ClearVehiclePreservingBypassHoldSkipped(v, timeoutBypassReleaseReason);
                                m_Runtime.Bypass.MarkBypassHoldSkipped(v, runningBypassBlocker);
                                SetLocalizedVehicleLabel(v, "StopTimeout", "停站超时", vTag);
                                if (ENABLE_MIDSTOP_TIMEOUT_GATE_LOGS && !shouldRefreshTimeoutAssist)
                                {
                                    Entity currentStop = Entity.Null;
                                    Entity boardingVehicle = Entity.Null;
                                    if (tgt.m_Target != Entity.Null
                                        && EntityManager.HasComponent<Connected>(tgt.m_Target))
                                    {
                                        currentStop = EntityManager.GetComponentData<Connected>(tgt.m_Target).m_Connected;
                                        if (currentStop != Entity.Null
                                            && EntityManager.HasComponent<BoardingVehicle>(currentStop))
                                        {
                                            boardingVehicle = EntityManager.GetComponentData<BoardingVehicle>(currentStop).m_Vehicle;
                                        }
                                    }

                                    if (RtLog.VerboseEnabled)
                                    {
                                        string assistGateKey = "timeout-assist-gate|"
                                            + timeoutAssistGraceUntil.ToString()
                                            + "|stop=" + currentStop.Index.ToString()
                                            + "|bv=" + boardingVehicle.Index.ToString()
                                            + "|dep=" + pt.m_DepartureFrame.ToString()
                                            + "|min=" + pt.m_MinWaitingDistance.ToString("F1")
                                            + "|max=" + pt.m_MaxBoardingDistance.ToString("F1");
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_BvMisfireObserveLogCache,
                                            v,
                                            assistGateKey,
                                            "[停站超时门槛] " + lineTag + " 车辆" + v.Index
                                            + " simulationFrame=" + nowFrame
                                            + " departureFrame=" + pt.m_DepartureFrame
                                            + " minWaitingDistance=" + pt.m_MinWaitingDistance
                                            + " maxBoardingDistance=" + pt.m_MaxBoardingDistance
                                            + " stop=" + currentStop.Index
                                            + " stopBoardingVehicle=" + boardingVehicle.Index
                                            + " stopBoardingVehicleIsSelf=" + (boardingVehicle == v));
                                    }
                                }
                                break;
                            }

                            if (bypassControlWaypointIndex > 0 && runningShouldHoldBypass)
                            {
                                SetPrefixedLocalizedVehicleLabel(v, "BypassExpress", "待避快车", "#" + v.Index + " ");
                                break;
                            }

                            bool hasOriginSettleSignal = atA
                                || boarding
                                || lastBoarding
                                || targetMinute >= 0
                                || m_Runtime.m_VehicleStateStore.OriginArrivalCandidateSinceFrame.ContainsKey(v)
                                || m_Runtime.m_VehicleView.IsInbound(v);
                            bool shouldEvaluateOriginSettle = !inCooldown
                                && hasOriginSettleSignal
                                && m_Runtime.m_LineProfile.ShouldEvaluateOriginSettle(
                                    v,
                                    wps,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    targetMinute);
                            bool settleAtOrigin = shouldEvaluateOriginSettle
                                && m_Runtime.m_LineProfile.ShouldSettleAtOrigin(
                                    v,
                                    wps,
                                    nowFrame,
                                    atA,
                                    boarding,
                                    lastBoarding,
                                    targetMinute);
                            bool forcedAtOrigin = settleAtOrigin && !atA;
                            if ((atA || forcedAtOrigin) && !inCooldown)
                            {
                                bool hasLapStartOdo = m_Runtime.m_ObsQuery.TryLapStart(v, out float ls);
                                bool hasLapStartFrame = m_Runtime.m_ObsQuery.TryLapStartFrame(v, out uint lapStartFrame);
                                bool lapStartValid = hasLapStartOdo && !float.IsNaN(ls) && !float.IsInfinity(ls) && ls >= 0f;
                                bool brokenRecoveredRunning = hasLapStartFrame && !lapStartValid;
                                float lapStart = hasLapStartOdo ? ls : -1f;
                                float nowOdo = EntityManager.HasComponent<Odometer>(v)
                                    ? EntityManager.GetComponentData<Odometer>(v).m_Distance : -1f;
                                const float LAP_MOVED_MIN = 500f;
                                bool hasMoved = (nowOdo >= 0f && lapStartValid && (nowOdo - lapStart) > LAP_MOVED_MIN);
                                float ld = 0f;
                                m_Runtime.m_ObsQuery.TryLapDistance(v, out ld);
                                if (brokenRecoveredRunning)
                                {
                                    this.ArriveIdle(v);
                                    this.ClearReady(v);
                                    m_Runtime.Bypass.RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-recovered-idle");
                                    m_Runtime.m_ObsPersist.ClearLapRestore(v);
                                    m_Runtime.m_CachedWpIdx[v] = 0;
                                    pt.m_DepartureFrame = nowFrame + 9999;
                                    m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                    SetHoldingVehicleLabel(v, targetMinute, vTag, includeHoldingInWaiting: false);
                                    log.Info("[恢复兜底] " + lineTag + " 车辆" + v.Index
                                        + " Running圈起点无效，回站后转Idle"
                                        + " lapStartFrame=" + lapStartFrame
                                        + " lapStartValid=" + lapStartValid
                                        + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                        + " target=" + (targetMinute >= 0 ? DispatchRuntimeSystem.SlotStr(targetMinute) : "-")
                                        + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?"));
                                    break;
                                }
                                if (!hasMoved)
                                {
                                    if (settleAtOrigin)
                                    {
                                        uint originSinceFrame = m_Runtime.m_VehicleView.TryGetOrigin(v, out uint sinceFrame)
                                            ? sinceFrame
                                            : nowFrame;
                                        bool keepAssignedTarget = targetMinute >= 0 && ScheduleClock.CurrentOrRecent(nowMinute, targetMinute);
                                        bool recoverToHolding = keepAssignedTarget;

                                        if (recoverToHolding)
                                            this.RecoverToHolding(v);
                                        else
                                            this.RecoverToIdle(v, nowFrame);
                                        m_Runtime.Bypass.RequestLineOrderedRuntimeForceRefresh(
                                            lineEnt,
                                            recoverToHolding ? "origin-return-holding" : "origin-return-idle");
                                        m_Runtime.m_CachedWpIdx[v] = 0;
                                        pt.m_DepartureFrame = nowFrame + 9999;
                                        m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);

                                        if (recoverToHolding)
                                        {
                                            bool isLateRecoveredTarget = ScheduleClock.CanLate(nowMinute, targetMinute);
                                            SetHoldingVehicleLabel(v, targetMinute, vTag, isLateRecoveredTarget);
                                            log.Info("[Running->Holding兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为候车"
                                                + " target=" + DispatchRuntimeSystem.SlotStr(targetMinute)
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        else
                                        {
                                            SetLocalizedVehicleLabel(v, "WaitingDispatch", "等待调度", vTag);
                                            log.Info("[Running->Idle兜底] " + lineTag + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为Idle"
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        break;
                                    }

                                    pt.m_DepartureFrame = midStopBoarding && midStopDwellDeadlineFrame > 0
                                        ? midStopDwellDeadlineFrame
                                        : nowFrame + 9999;
                                    m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                    string curSlot1 = m_Runtime.m_VehicleView.TryGetSlot(v, out int cs1) ? DispatchRuntimeSystem.SlotStr(cs1) : "?";
                                    string nxtSlot1 = targetMinute >= 0 ? ("->" + DispatchRuntimeSystem.SlotStr(targetMinute)) : "";
                                    SetLocalizedVehicleLabel(v, "Running", "运行中", curSlot1 + nxtSlot1 + vTag);
                                    if (nowFrame % 1800 == 0)
                                    {
                                        uint lastLaunchFrame = m_Runtime.m_VehicleView.TryGetLaunch(v, out uint llf) ? llf : 0;
                                        uint lapStartFrameDbg = m_Runtime.m_ObsQuery.TryLapStartFrame(v, out uint lsfDbg) ? lsfDbg : 0;
                                        string curSlotDbg = m_Runtime.m_VehicleView.TryGetSlot(v, out int csDbg) ? DispatchRuntimeSystem.SlotStr(csDbg) : "?";
                                        string targetSlotDbg = targetMinute >= 0 ? DispatchRuntimeSystem.SlotStr(targetMinute) : "-";
                                        int cachedWpDbg = m_Runtime.m_CachedWpIdx.TryGetValue(v, out int cwDbg) ? cwDbg : -1;
                                        log.Info("[心跳-卡站] " + lineTag + " 车辆" + v.Index
                                            + " atA=true hasMoved=false"
                                            + " traveled=" + (nowOdo >= 0f && lapStart >= 0f
                                                ? ((nowOdo - lapStart) / 1000f).ToString("F2") + "km" : "?")
                                            + " threshold=" + (LAP_MOVED_MIN / 1000f).ToString("F2") + "km"
                                            + " lapDist=" + (ld > 0f ? (ld / 1000f).ToString("F2") + "km" : "未知")
                                            + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                            + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                            + " lapStartValid=" + lapStartValid
                                            + " lapStartRaw=" + (hasLapStartOdo ? ls.ToString("F1") : "?")
                                            + " lapStartFrame=" + (lapStartFrameDbg > 0 ? lapStartFrameDbg.ToString() : "?")
                                            + " lastLaunchFrame=" + (lastLaunchFrame > 0 ? lastLaunchFrame.ToString() : "?")
                                            + " sinceLaunch=" + (lastLaunchFrame > 0 ? (nowFrame - lastLaunchFrame).ToString() : "?")
                                            + " curSlot=" + curSlotDbg
                                            + " targetSlot=" + targetSlotDbg
                                            + " cachedWp=" + cachedWpDbg
                                            + " curWpIdx=" + curWpIdx
                                            + " boarding=" + boarding
                                            + " lastBoarding=" + lastBoarding);
                                    }
                                    break;
                                }
                                m_Runtime.m_Observation.Finish(v, nowFrame, -1, 0f);
                                m_Runtime.m_Observation.Update(v);
                                this.ArriveIdle(v);
                                m_Runtime.Bypass.RequestLineOrderedRuntimeForceRefresh(lineEnt, "origin-return-idle");
                                if (targetMinute >= 0)
                                {
                                    if (ScheduleClock.CurrentOrRecent(nowMinute, targetMinute))
                                        this.Target(v, targetMinute);
                                    else
                                        this.ReleaseTarget(v);
                                }
                                else
                                {
                                    this.ReleaseTarget(v);
                                }
                                m_Runtime.m_CachedWpIdx[v] = 0;
                                this.ClearInbound(v);
                                this.ClearOriginCandidate(v);
                                if (forcedAtOrigin)
                                    this.SetReady(v, nowFrame, clockSnapshot);
                                else
                                    this.ClearReady(v);
                                SetLocalizedVehicleLabel(v, "WaitingDispatch", "等待调度", vTag);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Running->Idle] " + lineTag + " 车辆" + v.Index
                                        + " nowOdo=" + (nowOdo >= 0f ? nowOdo.ToString("F1") : "?")
                                        + " lapStart=" + (lapStartValid ? lapStart.ToString("F1") : "?")
                                        + " curWpIdx=" + curWpIdx
                                        + (targetMinute >= 0 && ScheduleClock.CurrentOrRecent(nowMinute, targetMinute)
                                            ? " keptTarget=" + DispatchRuntimeSystem.SlotStr(targetMinute)
                                            : "")
                                        + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                }
                            }
                            else
                            {
                                if (m_Runtime.m_ObsQuery.NeedsLapStart(v) && !inCooldown)
                                    m_Runtime.m_Observation.Record(v, "Running缺少圈起点自愈");
                                string curSlot2 = m_Runtime.m_VehicleView.TryGetSlot(v, out int cs2) ? DispatchRuntimeSystem.SlotStr(cs2) : "?";
                                string nxtSlot2 = targetMinute >= 0 ? ("->" + DispatchRuntimeSystem.SlotStr(targetMinute)) : "";
                                SetLocalizedVehicleLabel(v, "Running", "运行中", curSlot2 + nxtSlot2 + vTag);
                            }
                            break;

                        case VehicleState.Idle:
                            m_Runtime.Bypass.ClearVehicle(v);
                            m_Runtime.m_Announcements.Origin(routeEnt, wps, broadcastOriginWaitBusy);

                            if (!atA)
                            {
                                this.Run(v);
                                m_Runtime.m_Observation.Record(v, "Idle异常离站");
                                SetLocalizedVehicleLabel(v, "AbnormalDeparture", "运行中(异常离站)", vTag);
                                log.Info("[异常] " + lineTag + " 车辆" + v.Index + " Idle 时意外离站");
                                break;
                            }

                            if (targetMinute < 0)
                            {
                                int[] appliedTargets = m_Runtime.m_LineView.Times(routeEnt);
                                int lateTarget = -1;
                                Entity releasedVehicle = Entity.Null;
                                bool assignedLateTarget;
                                if (appliedTargets.Length > 0)
                                {
                                    assignedLateTarget = m_Runtime.m_DispatchScheduler.Plan.TryAssignCurrentOrLateScheduledTarget(
                                        routeEnt,
                                        v,
                                        nowMinute,
                                        lineTag,
                                        "Idle",
                                        appliedTargets,
                                        out releasedVehicle,
                                        out lateTarget);
                                }
                                else
                                {
                                    assignedLateTarget = m_Runtime.m_DispatchScheduler.Plan.TryAssignCurrentOrLateSlot(
                                        routeEnt,
                                        v,
                                        nowMinute,
                                        lineTag,
                                        "Idle",
                                        out releasedVehicle,
                                        out lateTarget);
                                }
                                if (assignedLateTarget)
                                {
                                    if (releasedVehicle != Entity.Null)
                                        ReleaseTarget(releasedVehicle);
                                    Target(v, lateTarget);
                                    targetMinute = lateTarget;
                                }
                            }

                            if (m_Runtime.m_LineProfile.HasInboundNearOrigin(routeEnt, wps, v, ORIGIN_CONGESTION_RADIUS_METERS, includePreparingVehicles: false))
                            {
                                if (m_Runtime.m_DispatchScheduler.Policy.ShouldProtect(routeEnt, v, nowMinute, -1))
                                {
                                    if (m_Runtime.m_VehicleView.TryGetTarget(v, out int ptm) && ptm >= 0 && ScheduleClock.CanLate(nowMinute, ptm))
                                    {
                                        if (RtLog.VerboseEnabled)
                                        {
                                            m_Runtime.m_RuntimeLog.Once(
                                                m_Runtime.m_RuntimeLog.m_YieldSkipLogCache,
                                                v,
                                                "YieldSkipLate|" + ptm,
                                                "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                    + " 班次" + DispatchRuntimeSystem.SlotStr(ptm)
                                                    + " 已过期" + ScheduleClock.Overdue(nowMinute, ptm) + "分钟，保留补发");
                                        }
                                    }
                                    else
                                    {
                                        if (RtLog.VerboseEnabled)
                                        {
                                            int protectTargetMinute = m_Runtime.m_VehicleView.TryGetTarget(v, out int ptm2) && ptm2 >= 0
                                                ? ptm2
                                                : m_Runtime.m_DispatchScheduler.Policy.Fallback(routeEnt, nowMinute);
                                            m_Runtime.m_RuntimeLog.Once(
                                                m_Runtime.m_RuntimeLog.m_YieldSkipLogCache,
                                                v,
                                                "YieldSkipProtect|" + protectTargetMinute,
                                                "[YieldSkip] " + lineTag + " 车辆" + v.Index
                                                    + " 最近班次" + DispatchRuntimeSystem.SlotStr(protectTargetMinute)
                                                    + " 仅剩" + ScheduleClock.MinutesUntil(nowMinute, protectTargetMinute) + "分钟，保留待避");
                                        }
                                    }
                                    break;
                                }
                                log.Info("[Yield] " + lineTag + " 车辆" + v.Index + " 始发站有回流车压队，回库疏解");
                                m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, "始发站压队疏解");
                                break;
                            }

                            if (targetMinute >= 0)
                            {
                                if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                                {
                                    m_Runtime.m_CommandApplier.Retire(v, pt, tgt, ecb, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                    break;
                                }
                                this.HoldFromIdle(v);
                                pt.m_DepartureFrame = nowFrame + 9999;
                                m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                                bool isLateTarget = ScheduleClock.CanLate(nowMinute, targetMinute);
                                m_Runtime.m_Observation.BindTarget(routeEnt, v, targetMinute, nowFrame, isLateTarget ? "idle-late-claim" : "idle-holding-assign");
                                SetHoldingVehicleLabel(v, targetMinute, vTag, isLateTarget);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.Once(
                                        isLateTarget ? m_Runtime.m_RuntimeLog.m_LateDispatchLogCache : m_Runtime.m_RuntimeLog.m_HoldingSkipLogCache,
                                        v,
                                        (isLateTarget ? "LateDispatchClaim|" : "IdleHoldingAssign|") + targetMinute,
                                        (isLateTarget ? "[补发认领] " : "[Idle->Holding] ")
                                            + lineTag + " 车辆" + v.Index
                                            + (isLateTarget
                                                ? " 认领补发班次" + DispatchRuntimeSystem.SlotStr(targetMinute) + " 于 " + DispatchRuntimeSystem.SlotStr(nowMinute)
                                                : " 进入候车班次" + DispatchRuntimeSystem.SlotStr(targetMinute)));
                                }
                                break;
                            }

                            if (!m_Runtime.m_VehicleStateStore.IdleStartFrame.ContainsKey(v))
                                this.SetIdle(v, nowFrame);

                            if (m_Runtime.m_VehicleView.TryGetIdle(v, out uint idleStartFrame))
                            {
                                uint idleFrames = nowFrame - idleStartFrame;
                                uint idleTimeoutFrames = clockSnapshot.ToFramesCeil(IDLE_TIMEOUT_MINUTES);
                                if (idleFrames >= idleTimeoutFrames)
                                {
                                    ClearIdle(v);
                                    m_Runtime.m_CommandApplier.Retire(
                                        v,
                                        pt,
                                        tgt,
                                        ecb,
                                        "闲置" + clockSnapshot.ToMinutes(idleFrames).ToString("F1") + "分钟");
                                    break;
                                }
                            }

                            pt.m_DepartureFrame = nowFrame + 9999;
                            m_Runtime.m_CommandApplier.CommitPublicTransport(v, pt, ecb);
                            SetLocalizedVehicleLabel(v, "WaitingDispatch", "等待调度", vTag);
                            break;

                        case VehicleState.Retiring:
                            SetLocalizedVehicleLabel(v, "Returning", "回库中", vTag);
                            break;
                    }
                }

            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private void TickLineControls(int nowMinute)
        {
            if (nowMinute != m_Runtime.m_LastPuppetMasterMinute)
            {
                try
                {
                    m_LineSpawnControl.Tick(nowMinute);
                    m_Runtime.m_LastPuppetMasterMinute = nowMinute;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] PuppetMasterControl -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
        }

        private string BuildOriginHoldRetireReason(Entity line, int nowMinute, int targetMinute)
        {
            int waitMinutes = ScheduleClock.MinutesUntil(nowMinute, targetMinute);
            int holdLimitMinutes = m_Runtime.m_LineView.Hold(line);
            return "下一班仍需等待" + waitMinutes + "分钟，超出候车窗口" + holdLimitMinutes + "分钟";
        }
    }
}
