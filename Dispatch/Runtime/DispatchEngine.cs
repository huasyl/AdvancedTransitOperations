using System;
using System.Collections.Generic;
using RapidTransitMod.Dispatch.Scheduling;
using RapidTransitMod.Dispatch.Runtime;
using RapidTransitMod.Core;
using RapidTransitMod.Runtime;
using Unity.Entities;

namespace RapidTransitMod
{
    internal readonly struct OriginPlanChange
    {
        internal readonly Entity Vehicle;
        internal readonly Entity Line;
        internal readonly bool HasSlot;
        internal readonly SlotKey Slot;
        internal readonly uint IdleUntilFrame;

        internal OriginPlanChange(Entity vehicle, Entity line, bool hasSlot, SlotKey slot, uint idleUntilFrame)
        {
            Vehicle = vehicle;
            Line = line;
            HasSlot = hasSlot;
            Slot = slot;
            IdleUntilFrame = idleUntilFrame;
        }
    }

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
        private readonly List<LaunchCommit> m_LaunchCommits = new List<LaunchCommit>();
        private readonly List<RunningCommit> m_RunningCommits = new List<RunningCommit>();
        private readonly List<WaypointCommit> m_WaypointCommits = new List<WaypointCommit>();
        private readonly List<OriginPlanChange> m_OriginPlanChanges = new List<OriginPlanChange>();
        private readonly List<OriginPlanChange> m_IdlePlans = new List<OriginPlanChange>();

        public DispatchEngine(VehicleRegistry vehicles, ModRuntimeHostSystem runtime)
        {
            m_Vehicles = vehicles;
            m_Runtime = runtime;
        }

        private TimedLogger log => m_Runtime.log;

        private const int IDLE_TIMEOUT_MINUTES = ModRuntimeHostSystem.IDLE_TIMEOUT_MINUTES;

        internal IReadOnlyList<LaunchCommit> LaunchCommits => m_LaunchCommits;
        internal IReadOnlyList<RunningCommit> RunningCommits => m_RunningCommits;
        internal IReadOnlyList<WaypointCommit> WaypointCommits => m_WaypointCommits;
        internal IReadOnlyList<OriginPlanChange> OriginPlanChanges => m_OriginPlanChanges;

        private void SetState(Entity vehicle, VehicleState state)
        {
            m_Vehicles.SetState(vehicle, state);
        }

        private void ConfirmLaunch(
            FrameEvents events,
            Entity vehicle,
            Entity line,
            LaunchedSlot launched,
            int waypointIndex,
            int actualMinute,
            uint nowFrame,
            string reason)
        {
            events.AppendLaunchConfirmed(
                vehicle,
                nowFrame,
                line,
                launched.Slot.Minute,
                launched.Slot.Minute,
                actualMinute,
                reason,
                launched.Slot.Date);
            m_LaunchCommits.Add(new LaunchCommit(vehicle, line, waypointIndex));
            m_Runtime.m_RuntimeFramePlan.AddStage(vehicle, RuntimeStageMask.Dispatch);
        }

        public void Adopt(Entity vehicle, Entity line, VehicleState state, uint nowFrame, uint? spawnOrderRequestFrame)
        {
            m_Vehicles.Track(vehicle, line);
            SetState(vehicle, state);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
            m_Vehicles.ClearInbound(vehicle);

            if (state == VehicleState.Preparing)
                m_Vehicles.SetPreparingStartFrame(vehicle, nowFrame);
            else
                m_Vehicles.ClearPreparingData(vehicle);

            if (spawnOrderRequestFrame.HasValue)
            {
                m_Vehicles.SetSpawnOrderRequestFrame(vehicle, spawnOrderRequestFrame.Value);
                m_Runtime.m_Observation.BeginDispatchEta(vehicle, line, spawnOrderRequestFrame.Value);
            }
            else
                m_Vehicles.ClearSpawnOrderRequestFrame(vehicle);

        }

        public void Retire(Entity vehicle)
        {
            SetState(vehicle, VehicleState.Retiring);
            m_Runtime.m_DispatchScheduler.Release(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.ClearPreparingData(vehicle);
            m_Vehicles.ClearSpawnOrderRequestFrame(vehicle);
            m_Vehicles.ClearInbound(vehicle);
        }

        public void HoldFromIdle(Entity vehicle)
        {
            SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearIdle(vehicle);
        }

        public void Launch(Entity vehicle, uint nowFrame)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparingData(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            m_Vehicles.SetLaunch(vehicle, nowFrame);
        }

        public void Run(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparingData(vehicle);
            m_Vehicles.ClearIdle(vehicle);
            QueueRunningCommit(vehicle, m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line) ? line : Entity.Null);
        }

        public void RestoreHold(Entity vehicle)
        {
            SetState(vehicle, VehicleState.Holding);
            m_Vehicles.ClearLaunch(vehicle);
        }

        public void RestoreRun(Entity vehicle)
        {
            m_Vehicles.SetState(vehicle, VehicleState.Running);
            m_Vehicles.ClearPreparingData(vehicle);
            m_Vehicles.ClearLaunch(vehicle);
        }

        public void CommitRunning(Entity vehicle, Entity line) => QueueRunningCommit(vehicle, line);

        internal IReadOnlyList<OriginPlanChange> CollectIdlePlans(uint nowFrame, ClockSnapshot clock)
        {
            m_IdlePlans.Clear();
            foreach (Entity vehicle in m_Runtime.m_VehicleWorksets.State(VehicleState.Idle))
            {
                if (!m_Runtime.m_SlotBook.HasFormal(vehicle)
                    && m_Runtime.m_VehicleView.TryGetIdle(vehicle, out uint start)
                    && m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity line))
                {
                    m_IdlePlans.Add(new OriginPlanChange(vehicle, line, false, default,
                        unchecked(start + clock.ToFramesCeil(IDLE_TIMEOUT_MINUTES))));
                }
            }
            return m_IdlePlans;
        }
        public void ProcessFrame(
            ClockSnapshot clockSnapshot,
            FrameEvents events,
            IReadOnlyList<DispatchInput> inputs)
        {
            m_LaunchCommits.Clear();
            m_WaypointCommits.Clear();
            m_OriginPlanChanges.Clear();
            for (int i = 0; i < inputs.Count; i++)
            {
                ProcessVehicle(
                    clockSnapshot,
                    inputs[i],
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
            ClockSnapshot clockSnapshot,
            DispatchInput input,
            FrameEvents events)
        {
                    Entity v = input.Vehicle;
                    if (!m_Runtime.m_VehicleView.TryGetState(v, out var state)) return;
                    uint nowFrame = m_Runtime.m_SimulationSystem.frameIndex;
                    int targetMinute = m_Runtime.m_VehicleView.TryGetTarget(v, out int tm) ? tm : -1;
                    Entity line = input.Line;
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
                    if (state == VehicleState.Retiring)
                    {
                        return;
                    }

                    bool atA = input.AtOrigin;
                    switch (state)
                    {
                        case VehicleState.Preparing:
                        case VehicleState.Running:
                            if (atA)
                                Arrive(v, line, input.OriginArrivalFrame, nowFrame, clockSnapshot);
                            break;

                        case VehicleState.Holding:
                            ProcessHolding(input, nowFrame, clockSnapshot, events);
                            break;

                        case VehicleState.Idle:
                            if (input.OriginSessionRecoveredAway || (!atA && !input.Boarding))
                            {
                                m_Runtime.m_DispatchScheduler.Release(v);
                                this.Run(v);
                                events.AppendUnplannedRun(
                                    v,
                                    nowFrame,
                                    line,
                                    "idle-unplanned-run");
                                m_Runtime.m_RuntimeFramePlan.AddStage(v, RuntimeStageMask.Dispatch);
                                log.Info("[UnplannedRun] line=" + line.Index + " vehicle=" + v.Index + " previousState=Idle");
                                break;
                            }

                            if (m_Runtime.m_SlotBook.HasFormal(v))
                            {
                                this.HoldFromIdle(v);
                                break;
                            }

                            if (m_Runtime.m_VehicleView.TryGetIdle(v, out uint idleStartFrame))
                            {
                                uint idleFrames = nowFrame - idleStartFrame;
                                uint idleTimeoutFrames = clockSnapshot.ToFramesCeil(IDLE_TIMEOUT_MINUTES);
                                if (unchecked((int)(nowFrame - unchecked(idleStartFrame + idleTimeoutFrames))) >= 0)
                                {
                                    m_Runtime.m_CommandApplier.Retire(
                                        v,
                                        "idle-timeout minutes=" + clockSnapshot.ToMinutes(idleFrames).ToString("F1"));
                                    break;
                                }
                            }

                            break;

                        case VehicleState.Retiring:
                            break;
                    }
        }

        private void Arrive(Entity vehicle, Entity line, uint arrivalFrame, uint nowFrame, ClockSnapshot clock)
        {
            if (m_Runtime.m_LineRange.NeedsDepot(vehicle, line))
            {
                m_Runtime.m_CommandApplier.Retire(vehicle, "maintenance-two-laps");
                return;
            }
            m_Vehicles.ClearInbound(vehicle);
            QueueWaypointCommit(vehicle, 0);
            SlotGrant grant = m_Runtime.m_DispatchScheduler.RequestAtArrival(
                vehicle, line, arrivalFrame, nowFrame, clock);
            m_Vehicles.ClearLaunch(vehicle);
            if (grant.Granted)
            {
                SetState(vehicle, VehicleState.Holding);
                m_Vehicles.ClearIdle(vehicle);
                m_OriginPlanChanges.Add(new OriginPlanChange(vehicle, line, true, grant.Slot, 0u));
                m_Runtime.m_SelectPanel.RecordLineHoldingSummary(line, clock.NowMinute, vehicle, grant.Slot.Minute);
                if (RtLog.VerboseEnabled)
                    log.Info("[OriginArrival] line=" + line.Index + " vehicle=" + vehicle.Index
                        + " state=Holding slot=" + grant.Slot.Minute + " date=" + grant.Slot.Date.ToString("yyyy-MM-dd"));
            }
            else
            {
                EnterIdle(vehicle, line, nowFrame, clock);
            }
        }

        private void EnterIdle(Entity vehicle, Entity line, uint nowFrame, ClockSnapshot clock)
        {
            SetState(vehicle, VehicleState.Idle);
            m_Vehicles.SetIdle(vehicle, nowFrame);
            m_Vehicles.ClearLaunch(vehicle);
            m_OriginPlanChanges.Add(new OriginPlanChange(vehicle, line, false, default,
                unchecked(nowFrame + clock.ToFramesCeil(IDLE_TIMEOUT_MINUTES))));
            if (RtLog.VerboseEnabled)
                log.Info("[OriginIdle] line=" + line.Index + " vehicle=" + vehicle.Index + " timeoutMinutes=" + IDLE_TIMEOUT_MINUTES);
        }

        private void ProcessHolding(DispatchInput input, uint nowFrame, ClockSnapshot clock, FrameEvents events)
        {
            Entity vehicle = input.Vehicle;
            Entity line = input.Line;
            if (input.OriginSessionRecoveredAway)
            {
                m_Runtime.m_DispatchScheduler.Release(vehicle);
                Run(vehicle);
                events.AppendUnplannedRun(vehicle, nowFrame, line, "origin-session-recovered-away");
                log.Info("[UnplannedRun] line=" + line.Index + " vehicle=" + vehicle.Index + " reason=origin-session-recovered-away");
                return;
            }
            if (!input.OriginDepartureConfirmed && input.HoldingStopUnknown)
                return;
            if (input.OriginDepartureConfirmed || !input.AtOrigin)
            {
                uint movingFrame = input.OriginDepartureConfirmed ? input.OriginMovingFrame : nowFrame;
                if (!input.OriginDepartureConfirmed)
                    log.Info("[UnexpectedDeparture] line=" + line.Index + " vehicle=" + vehicle.Index + " reason=missing-departure-confirmation");
                if (m_Runtime.m_DispatchScheduler.MarkLaunched(vehicle, movingFrame, nowFrame, clock, out LaunchedSlot launched))
                {
                    Launch(vehicle, movingFrame);
                    ConfirmLaunch(events, vehicle, line, launched, input.CurrentWaypoint,
                        clock.NowMinute, movingFrame, "origin-departure");
                    log.Info("[LaunchConfirmed] line=" + line.Index + " vehicle=" + vehicle.Index
                        + " slot=" + launched.Slot.Minute + " date=" + launched.Slot.Date.ToString("yyyy-MM-dd")
                        + " movingFrame=" + movingFrame + " earlyMinutes=" + launched.EarlyMinutes);
                }
                else
                {
                    m_Runtime.m_DispatchScheduler.Release(vehicle);
                    Run(vehicle);
                    events.AppendUnplannedRun(vehicle, nowFrame, line, "holding-without-formal-slot");
                    log.Info("[UnplannedRun] line=" + line.Index + " vehicle=" + vehicle.Index + " reason=holding-without-formal-slot");
                }
                return;
            }
            if (!m_Runtime.m_SlotBook.HasFormal(vehicle))
                EnterIdle(vehicle, line, nowFrame, clock);
        }

    }
}
