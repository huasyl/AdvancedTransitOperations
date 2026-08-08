using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Core;
using RapidTransitMod.Runtime;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct LaunchCommit
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly int Waypoint;
        public readonly bool ArmExpressRescue;
        public readonly bool ClearRescue;
        public readonly bool RefreshLine;

        public LaunchCommit(Entity vehicle, Entity line, int waypointIndex)
        {
            Vehicle = vehicle;
            Line = line;
            Waypoint = waypointIndex;
            ArmExpressRescue = true;
            ClearRescue = false;
            RefreshLine = true;
        }
    }

    internal readonly struct RunningCommit
    {
        public readonly Entity Vehicle;
        public readonly Entity Line;
        public readonly bool ArmExpressRescue;
        public readonly bool ClearRescue;
        public readonly bool RefreshLine;

        public RunningCommit(Entity vehicle, Entity line)
        {
            Vehicle = vehicle;
            Line = line;
            ArmExpressRescue = true;
            ClearRescue = false;
            RefreshLine = true;
        }
    }

    internal readonly struct WaypointCommit
    {
        public readonly Entity Vehicle;
        public readonly int Waypoint;

        public WaypointCommit(Entity vehicle, int waypoint)
        {
            Vehicle = vehicle;
            Waypoint = waypoint;
        }
    }

    internal sealed class DispatchEngine
    {
        private readonly VehicleRegistry m_Vehicles;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Action<StopFact> m_PublishStopFact;
        private readonly Dictionary<Entity, AssistLaunchPendingRecord> m_AssistLaunchPendingByVehicle = new Dictionary<Entity, AssistLaunchPendingRecord>();
        private readonly List<LaunchCommit> m_LaunchCommits = new List<LaunchCommit>();
        private readonly List<RunningCommit> m_RunningCommits = new List<RunningCommit>();
        private readonly List<WaypointCommit> m_WaypointCommits = new List<WaypointCommit>();

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

        public DispatchEngine(VehicleRegistry vehicles, ModRuntimeHostSystem runtime, Action<StopFact> publishStopFact)
        {
            m_Vehicles = vehicles;
            m_Runtime = runtime;
            m_PublishStopFact = publishStopFact;
        }

        private TimedLogger log => m_Runtime.log;

        private const uint FORCED_MIDSTOP_BV_GRACE_FRAMES = ModRuntimeHostSystem.FORCED_MIDSTOP_BV_GRACE_FRAMES;
        private const int IDLE_TIMEOUT_MINUTES = ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES;
        private const uint LAUNCH_COOLDOWN_FRAMES = ModRuntimeHostSystem.LAUNCH_COOLDOWN_FRAMES;

        internal IReadOnlyList<LaunchCommit> LaunchCommits => m_LaunchCommits;
        internal IReadOnlyList<RunningCommit> RunningCommits => m_RunningCommits;
        internal IReadOnlyList<WaypointCommit> WaypointCommits => m_WaypointCommits;

        private void SetState(Entity vehicle, VehicleState state)
        {
            m_Vehicles.SetState(vehicle, state);
        }

        private void ConfirmLaunch(
            FrameEvents events,
            Entity vehicle,
            Entity line,
            int targetMinute,
            int waypointIndex,
            int actualMinute,
            uint nowFrame,
            bool late,
            string reason)
        {
            events.AppendLaunchConfirmed(
                vehicle,
                nowFrame,
                line,
                targetMinute,
                targetMinute,
                actualMinute,
                late,
                reason);
            m_LaunchCommits.Add(new LaunchCommit(vehicle, line, waypointIndex));
            m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void Adopt(Entity vehicle, Entity line, VehicleState state, uint nowFrame, uint? dispatchFrame)
        {
            m_Vehicles.Track(vehicle, line);
            SetState(vehicle, state);
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
            SetState(vehicle, VehicleState.Retiring);
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
            m_Runtime.m_LineSpawnControl.CaptureRetireTarget(
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
            m_Runtime.m_LineSpawnControl.ApplyRetireTarget(
                line,
                preActive,
                hadSpawnTarget,
                oldSpawnTarget);
        }

        public void Hold(Entity vehicle, uint startFrame, ClockSnapshot clockSnapshot)
        {
            SetState(vehicle, VehicleState.Holding);
            m_Vehicles.SetReady(
                vehicle,
                startFrame,
                ModRuntimeHostSystem.PREPARING_ORIGIN_SETTLE_MINUTES,
                clockSnapshot);
        }

        public void HoldFromIdle(Entity vehicle)
        {
            SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void RecoverToHolding(Entity vehicle)
        {
            SetState(vehicle, VehicleState.Holding);
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
            QueueRunningCommit(vehicle, m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null);
        }

        public void RestoreHold(Entity vehicle, int targetMinute)
        {
            SetState(vehicle, VehicleState.Holding);
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

        public void CommitRunning(Entity vehicle, Entity line) => QueueRunningCommit(vehicle, line);

        public void RecoverToIdle(Entity vehicle, uint nowFrame)
        {
            SetState(vehicle, VehicleState.Idle);
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
            SetState(vehicle, VehicleState.Idle);
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
                ModRuntimeHostSystem.FORCED_ORIGIN_MIN_DWELL_MINUTES,
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
        }

        public void ClearAssistLaunchPending()
        {
            m_AssistLaunchPendingByVehicle.Clear();
            m_RunningCommits.Clear();
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

        public void ProcessFrame(
            EntityCommandBuffer ecb,
            ClockSnapshot clockSnapshot,
            RuntimeFramePlan worksets,
            FrameEvents events,
            IReadOnlyList<DispatchInput> inputs)
        {
            m_LaunchCommits.Clear();
            m_WaypointCommits.Clear();
            int nowMinute = clockSnapshot.NowMinute;
            for (int i = 0; i < inputs.Count; i++)
            {
                ProcessVehicle(
                    ecb,
                    nowMinute,
                    clockSnapshot,
                    inputs[i],
                    worksets,
                    events);
            }
        }

        internal void ClearRunningCommits() => m_RunningCommits.Clear();

        private void QueueRunningCommit(Entity vehicle, Entity line)
        {
            if (vehicle != Entity.Null && line != Entity.Null)
                m_RunningCommits.Add(new RunningCommit(vehicle, line));
        }

        private void QueueWaypointCommit(Entity vehicle, int waypoint)
        {
            if (vehicle != Entity.Null)
                m_WaypointCommits.Add(new WaypointCommit(vehicle, waypoint));
        }

        private void ProcessVehicle(
            EntityCommandBuffer ecb,
            int nowMinute,
            ClockSnapshot clockSnapshot,
            DispatchInput input,
            RuntimeFramePlan worksets,
            FrameEvents events)
        {
                    Entity v = input.Vehicle;
                    if (!m_Runtime.m_VehicleView.TryGetState(v, out var state)) return;
                    uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
                    if (worksets.IsDeadlineDue(v, DeadlineKind.Ready, nowFrame))
                    {
                        this.ClearReady(v);
                    }
                    int targetMinute = m_Runtime.m_VehicleView.TryGetTarget(v, out int tm) ? tm : -1;
                    Entity line = input.Line;
                    Entity routeEnt = input.Route;
                    if (line == Entity.Null || !input.InputValid)
                    {
                        if (RtLog.VerboseEnabled
                            && (targetMinute >= 0 || state == VehicleState.Holding || state == VehicleState.Preparing))
                        {
                            m_Runtime.m_RuntimeLog.Once(
                                m_Runtime.m_RuntimeLog.m_OriginDispatchTraceLogCache,
                                v,
                                "runtime-skip-core|state=" + state
                                    + "|target=" + targetMinute,
                                "[OriginDispatchTrace] reason=runtime-skip-core line=" + line.Index
                                    + " vehicle=" + v.Index
                                    + " state=" + state
                                    + " target=" + Dispatch.Diagnostics.RuntimeLog.Slot(targetMinute));
                        }
                        return;
                    }
                    int waypointCount = input.WaypointCount;
                    bool boarding = input.Boarding;
                    Entity lineEnt = line;
                    string cachedLineTag = null;
                    string LineTag() => cachedLineTag ??= "线路" + line.Index;
                    if (state == VehicleState.Retiring)
                    {
                        return;
                    }

                    bool inCooldown = m_Runtime.m_VehicleView.TryGetCooldown(v, out uint cooldownUntil)
                        && nowFrame < cooldownUntil;

                    bool lastBoarding = input.HadStopSession;
                    bool processedBoardingChanged = input.BoardingChanged;
                    int previousCachedWpIdx = input.PreviousWaypoint;
                    int curWpIdx = input.CurrentWaypoint;
                    bool atA = state == VehicleState.Preparing
                        ? input.PreparingAtOrigin
                        : input.AtOrigin;
                    bool midStopBoarding = state == VehicleState.Running
                        && boarding
                        && curWpIdx > 0;
                    switch (state)
                    {
                        case VehicleState.Preparing:
                            if (targetMinute >= 0 && ScheduleClock.SoftExpired(nowMinute, targetMinute) && !ScheduleClock.CanLate(nowMinute, targetMinute))
                            {
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.Once(
                                        m_Runtime.m_RuntimeLog.m_PreparingSlotLogCache,
                                        v,
                                        "PreparingSlot|" + targetMinute + "|" + overdueMinutes,
                                        "[PreparingSlot] " + LineTag() + " 车辆" + v.Index
                                            + " 班次" + ModRuntimeHostSystem.SlotStr(targetMinute) + " 已过期(" + overdueMinutes + "分钟)，释放重新调度");
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
                                    LineTag(),
                                    "Preparing",
                                    out preparingAssignedTargetMinute))
                                {
                                    Target(v, preparingAssignedTargetMinute);
                                    targetMinute = preparingAssignedTargetMinute;
                                }

                                if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                                {
                                    m_Runtime.m_CommandApplier.Retire(v, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                    break;
                                }
                                this.Hold(v, nowFrame, clockSnapshot);
                                m_Runtime.m_SelectPanel.RecordLineHoldingSummary(lineEnt, nowMinute, v, targetMinute);
                                m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                                if (targetMinute >= 0)
                                {
                                    if (RtLog.VerboseEnabled)
                                        log.Info("[Preparing->Holding] " + LineTag() + " 车辆" + v.Index + " 到站，预分配 " + ModRuntimeHostSystem.SlotStr(targetMinute));
                                }
                                else
                                {
                                    if (RtLog.VerboseEnabled)
                                        log.Info("[Preparing->Holding] " + LineTag() + " 车辆" + v.Index + " 到站，等待调度");
                                }
                            }
                            else
                            {
                            }
                            break;

                        case VehicleState.Holding:
                            if (!atA)
                            {
                                if (TryGetAssistLaunchPending(v, routeEnt, targetMinute, out AssistLaunchPendingRecord assistPending))
                                {
                                    int assistedTargetMinute = assistPending.TargetMinute;
                                    bool isLateAssistLaunch = ScheduleClock.CanLate(nowMinute, assistedTargetMinute);
                                    this.Launch(v, assistedTargetMinute, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                    m_Runtime.m_JustLaunched.Add(v);
                                    ClearAssistLaunchPending(v);
                                    m_Runtime.m_CommandApplier.CommitAssistLaunch(v, nowFrame, ecb);
                                    ConfirmLaunch(
                                        events,
                                        v,
                                        lineEnt,
                                        assistedTargetMinute,
                                        curWpIdx,
                                        nowMinute,
                                        nowFrame,
                                        isLateAssistLaunch,
                                        "assist-launch");
                                    if (RtLog.VerboseEnabled)
                                    {
                                        log.Info("[AssistLaunchSync] " + LineTag() + " 车辆" + v.Index
                                            + " 在始发发车协助后已离站，补记班次" + ModRuntimeHostSystem.SlotStr(assistedTargetMinute)
                                            + " 于 " + ModRuntimeHostSystem.SlotStr(nowMinute)
                                            + (isLateAssistLaunch ? " late=1" : " late=0"));
                                    }
                                    break;
                                }
                                if (m_Runtime.m_Observation.IsWaitingOriginDwell(v, nowFrame))
                                {
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                                    break;
                                }
                                this.Run(v);
                                events.AppendUnplannedRun(
                                    v,
                                    nowFrame,
                                    lineEnt,
                                    "holding-unplanned-run");
                                m_Runtime.m_RuntimeFramePlan.AddStage(v, RuntimeStageMask.Dispatch);
                                log.Info("[异常] " + LineTag() + " 车辆" + v.Index + " Holding 时意外离站");
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
                                        LineTag(),
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
                                        LineTag(),
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
                                }
                                else if (m_Runtime.m_DispatchScheduler.Plan.TryAssignUpcomingTarget(
                                    routeEnt,
                                    v,
                                    nowMinute,
                                    LineTag(),
                                    "Holding",
                                    out int upcomingTargetMinute))
                                {
                                    Target(v, upcomingTargetMinute);
                                    targetMinute = upcomingTargetMinute;
                                }
                                else
                                {
                                    this.RecoverToIdle(v, nowFrame);
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                                    break;
                                }
                            }

                            if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                            {
                                m_Runtime.m_CommandApplier.Retire(v, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                break;
                            }

                            if (ScheduleClock.Reached(nowMinute, targetMinute) || ScheduleClock.CanLate(nowMinute, targetMinute))
                            {
                                if (m_Runtime.m_DispatchScheduler.Policy.IsOccupied(routeEnt, v, targetMinute))
                                {
                                    this.ReleaseTarget(v);
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_HoldingSkipLogCache,
                                            v,
                                            "HoldingSkip|" + targetMinute,
                                            "[HoldingSkip] " + LineTag() + " 车辆" + v.Index
                                                + " 班次" + ModRuntimeHostSystem.SlotStr(targetMinute) + " 已被其他车辆占用，释放重调度");
                                    }
                                    break;
                                }

                                if (m_Runtime.m_Observation.IsWaitingOriginDwell(v, nowFrame))
                                {
                                    m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                                    break;
                                }
                                if (boarding)
                                {
                                    bool shouldRefreshOriginAssist = !m_Runtime.m_VehicleView.TryGetBoardingGrace(v, out uint originBoardingGraceUntil)
                                        || nowFrame >= originBoardingGraceUntil;
                                    if (shouldRefreshOriginAssist)
                                    {
                                        m_Runtime.m_CommandApplier.ForceDepart(v, nowFrame, ecb);
                                        this.SetBoardingGrace(v, nowFrame + FORCED_MIDSTOP_BV_GRACE_FRAMES);
                                        if (RtLog.VerboseEnabled)
                                        {
                                            log.Info("[始发发车协助] " + LineTag() + " 车辆" + v.Index
                                                + " 班次" + ModRuntimeHostSystem.SlotStr(targetMinute)
                                                + " wp=" + curWpIdx);
                                        }
                                    }
                                    ArmAssistLaunchPending(v, routeEnt, targetMinute);
                                    m_PublishStopFact(new StopFact(
                                        StopFactKind.BoardingCloseRequested,
                                        v,
                                        lineEnt,
                                        curWpIdx,
                                        nowFrame,
                                        reason: "holding-origin-assist"));
                                    break;
                                }
                                bool isLateDispatch = ScheduleClock.CanLate(nowMinute, targetMinute);
                                int overdueMinutes = isLateDispatch ? ScheduleClock.Overdue(nowMinute, targetMinute) : 0;
                                ClearAssistLaunchPending(v);
                                ClearBoardingGrace(v);
                                m_Runtime.m_CommandApplier.Launch(v, lineEnt, curWpIdx, ecb);
                                this.Launch(v, targetMinute, nowFrame, nowFrame + LAUNCH_COOLDOWN_FRAMES);
                                m_Runtime.m_JustLaunched.Add(v);
                                ConfirmLaunch(
                                    events,
                                    v,
                                    lineEnt,
                                    targetMinute,
                                    curWpIdx,
                                    nowMinute,
                                    nowFrame,
                                    isLateDispatch,
                                    "normal-launch");
                                string spawnIntent = m_Runtime.m_SpawnIntentTrace.Launch(v, targetMinute, nowFrame);
                                if (isLateDispatch)
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        m_Runtime.m_RuntimeLog.Once(
                                            m_Runtime.m_RuntimeLog.m_LateDispatchLogCache,
                                            v,
                                            "LateDispatchLaunch|" + targetMinute,
                                            "[补发] " + LineTag() + " 车辆" + v.Index
                                                + " 于 " + ModRuntimeHostSystem.SlotStr(nowMinute) + " 补发（班次 " + ModRuntimeHostSystem.SlotStr(targetMinute) + "）"
                                                + " 已过期" + overdueMinutes + "分钟"
                                                + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES)
                                                + spawnIntent);
                                    }
                                }
                                else
                                {
                                    if (RtLog.VerboseEnabled)
                                    {
                                        log.Info("[发车] " + LineTag() + " 车辆" + v.Index
                                            + " 于 " + ModRuntimeHostSystem.SlotStr(nowMinute) + " 发车（班次 " + ModRuntimeHostSystem.SlotStr(targetMinute) + "）"
                                            + " 冷却至帧" + (nowFrame + LAUNCH_COOLDOWN_FRAMES)
                                            + spawnIntent);
                                    }
                                }
                            }
                            else if (ScheduleClock.HardExpired(nowMinute, targetMinute))
                            {
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Holding] " + LineTag() + " 车辆" + v.Index
                                        + " 班次" + ModRuntimeHostSystem.SlotStr(targetMinute) + " 大幅过期(" + overdueMinutes + "分钟)，直接回库");
                                }
                                m_Runtime.m_CommandApplier.Retire(v, "班次大幅过期" + overdueMinutes + "分钟");
                            }
                            else if (ScheduleClock.SoftExpired(nowMinute, targetMinute))
                            {
                                int overdueMinutes = ScheduleClock.Overdue(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Holding] " + LineTag() + " 车辆" + v.Index
                                        + " 班次" + ModRuntimeHostSystem.SlotStr(targetMinute) + " 已过期(" + overdueMinutes + "分钟)，释放重新调度");
                                }
                                this.ReleaseTarget(v);
                                m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                            }
                            else
                            {
                                m_Runtime.m_CommandApplier.HoldDeparture(v, nowFrame, ecb);
                            }
                            break;

                        case VehicleState.Running:
                            int bypassControlWaypoint = curWpIdx >= 0 ? curWpIdx : previousCachedWpIdx;
                            var runningBypass = input.BypassControl;
                            bool runningShouldHoldBypass = runningBypass.ShouldHold;

                            if (bypassControlWaypoint > 0 && runningShouldHoldBypass)
                            {
                                break;
                            }

                            bool shouldEvaluateOriginSettle = input.ShouldEvaluateOriginSettle;
                            bool settleAtOrigin = input.SettledAtOrigin;
                            bool forcedAtOrigin = input.ForcedAtOrigin;
                            if (shouldEvaluateOriginSettle
                                && (atA || forcedAtOrigin)
                                && (!inCooldown || input.IgnoreOriginCooldown))
                            {
                                bool brokenRecoveredRunning = input.BrokenRecoveredRun;
                                bool hasMoved = input.RunDistanceReady || input.OriginSettleReady;
                                bool moving = input.Moving;
                                float travelledDistance = input.TravelledDistance;
                                float observedLapDistance = input.ObservedLapDistance;
                                if (brokenRecoveredRunning)
                                {
                                    this.ArriveIdle(v);
                                    this.SetReady(v, nowFrame, clockSnapshot);
                                    events.AppendDispatch(
                                        v,
                                        nowFrame,
                                        DispatchFactKind.RunningRecovery,
                                        VehicleState.Running,
                                        VehicleState.Idle,
                                        lineEnt,
                                        fact: new DispatchBusinessFact(targetMinute, -1, -1, false, "broken-lap-recovered"));
                                    m_Runtime.m_ObsPersist.ClearLapRestore(v);
                                    QueueWaypointCommit(v, 0);
                                    m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);
                                    log.Info("[恢复兜底] " + LineTag() + " 车辆" + v.Index
                                        + " Running圈起点无效，回站后转Idle"
                                        + " target=" + (targetMinute >= 0 ? ModRuntimeHostSystem.SlotStr(targetMinute) : "-")
                                        + " travelled=" + travelledDistance.ToString("F1"));
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
                                        QueueWaypointCommit(v, 0);
                                        m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);

                                        if (recoverToHolding)
                                        {
                                            bool isLateRecoveredTarget = ScheduleClock.CanLate(nowMinute, targetMinute);
                                            log.Info("[Running->Holding兜底] " + LineTag() + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为候车"
                                                + " target=" + ModRuntimeHostSystem.SlotStr(targetMinute)
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        else
                                        {
                                            log.Info("[Running->Idle兜底] " + LineTag() + " 车辆" + v.Index
                                                + " 到达始发站后长时间静止，回收为Idle"
                                                + " waitedFrames=" + (nowFrame - originSinceFrame)
                                                + " boarding=" + boarding
                                                + " lastBoarding=" + lastBoarding
                                                + " curWpIdx=" + curWpIdx
                                                + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                        }
                                        break;
                                    }

                                    m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);
                                    int currentSlot1 = m_Runtime.m_VehicleView.TryGetSlot(v, out int cs1) ? cs1 : int.MinValue;
                                    if (nowFrame % 1800 == 0)
                                    {
                                        uint lastLaunchFrame = m_Runtime.m_VehicleView.TryGetLaunch(v, out uint llf) ? llf : 0;
                                        string curSlotDbg = m_Runtime.m_VehicleView.TryGetSlot(v, out int csDbg) ? ModRuntimeHostSystem.SlotStr(csDbg) : "?";
                                        string targetSlotDbg = targetMinute >= 0 ? ModRuntimeHostSystem.SlotStr(targetMinute) : "-";
                                        log.Info("[心跳-卡站] " + LineTag() + " 车辆" + v.Index
                                            + " atA=true hasMoved=false"
                                            + " travelled=" + (travelledDistance >= 0f ? (travelledDistance / 1000f).ToString("F2") + "km" : "?")
                                            + " lapDist=" + (observedLapDistance > 0f ? (observedLapDistance / 1000f).ToString("F2") + "km" : "未知")
                                            + " moving=" + (moving ? "1" : "0")
                                            + " lastLaunchFrame=" + (lastLaunchFrame > 0 ? lastLaunchFrame.ToString() : "?")
                                            + " sinceLaunch=" + (lastLaunchFrame > 0 ? (nowFrame - lastLaunchFrame).ToString() : "?")
                                            + " curSlot=" + curSlotDbg
                                            + " targetSlot=" + targetSlotDbg
                                            + " curWpIdx=" + curWpIdx
                                            + " boarding=" + boarding
                                            + " lastBoarding=" + lastBoarding);
                                    }
                                    break;
                                }
                                this.ArriveIdle(v);
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
                                m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);
                                QueueWaypointCommit(v, 0);
                                this.ClearInbound(v);
                                this.ClearOriginCandidate(v);
                                if (forcedAtOrigin)
                                    this.SetReady(v, nowFrame, clockSnapshot);
                                else
                                    this.ClearReady(v);
                                if (RtLog.VerboseEnabled)
                                {
                                    log.Info("[Running->Idle] " + LineTag() + " 车辆" + v.Index
                                        + " travelled=" + travelledDistance.ToString("F1")
                                        + " curWpIdx=" + curWpIdx
                                        + (targetMinute >= 0 && ScheduleClock.CurrentOrRecent(nowMinute, targetMinute)
                                            ? " keptTarget=" + ModRuntimeHostSystem.SlotStr(targetMinute)
                                            : "")
                                        + (forcedAtOrigin ? " forcedAtOrigin=true" : ""));
                                }
                            }
                            else
                            {
                                int currentSlot2 = m_Runtime.m_VehicleView.TryGetSlot(v, out int cs2) ? cs2 : int.MinValue;
                            }
                            break;

                        case VehicleState.Idle:
                            if (!atA)
                            {
                                this.Run(v);
                                events.AppendUnplannedRun(
                                    v,
                                    nowFrame,
                                    lineEnt,
                                    "idle-unplanned-run");
                                m_Runtime.m_RuntimeFramePlan.AddStage(v, RuntimeStageMask.Dispatch);
                                log.Info("[异常] " + LineTag() + " 车辆" + v.Index + " Idle 时意外离站");
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
                                        LineTag(),
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
                                        LineTag(),
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

                            if (input.OriginBusy)
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
                                                "[YieldSkip] " + LineTag() + " 车辆" + v.Index
                                                    + " 班次" + ModRuntimeHostSystem.SlotStr(ptm)
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
                                                "[YieldSkip] " + LineTag() + " 车辆" + v.Index
                                                    + " 最近班次" + ModRuntimeHostSystem.SlotStr(protectTargetMinute)
                                                    + " 仅剩" + ScheduleClock.MinutesUntil(nowMinute, protectTargetMinute) + "分钟，保留待避");
                                        }
                                    }
                                    break;
                                }
                                log.Info("[Yield] " + LineTag() + " 车辆" + v.Index + " 始发站有回流车压队，回库疏解");
                                m_Runtime.m_CommandApplier.Retire(v, "始发站压队疏解");
                                break;
                            }

                            if (targetMinute >= 0)
                            {
                                if (m_Runtime.m_DispatchScheduler.Policy.ShouldRetire(routeEnt, nowMinute, targetMinute))
                                {
                                    m_Runtime.m_CommandApplier.Retire(v, BuildOriginHoldRetireReason(routeEnt, nowMinute, targetMinute));
                                    break;
                                }
                                this.HoldFromIdle(v);
                                m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);
                                bool isLateTarget = ScheduleClock.CanLate(nowMinute, targetMinute);
                                if (RtLog.VerboseEnabled)
                                {
                                    m_Runtime.m_RuntimeLog.Once(
                                        isLateTarget ? m_Runtime.m_RuntimeLog.m_LateDispatchLogCache : m_Runtime.m_RuntimeLog.m_HoldingSkipLogCache,
                                        v,
                                        (isLateTarget ? "LateDispatchClaim|" : "IdleHoldingAssign|") + targetMinute,
                                        (isLateTarget ? "[补发认领] " : "[Idle->Holding] ")
                                            + LineTag() + " 车辆" + v.Index
                                            + (isLateTarget
                                                ? " 认领补发班次" + ModRuntimeHostSystem.SlotStr(targetMinute) + " 于 " + ModRuntimeHostSystem.SlotStr(nowMinute)
                                                : " 进入候车班次" + ModRuntimeHostSystem.SlotStr(targetMinute)));
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
                                        "闲置" + clockSnapshot.ToMinutes(idleFrames).ToString("F1") + "分钟");
                                    break;
                                }
                            }

                            m_Runtime.m_CommandApplier.KeepDepartureHeld(v, nowFrame, ecb);
                            break;

                        case VehicleState.Retiring:
                            break;
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
