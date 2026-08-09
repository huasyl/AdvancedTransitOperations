using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Routes;
using Game.Vehicles;
using RapidTransitMod.Dispatch.Commands;
using RapidTransitMod.Dispatch.Runtime;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class DispatchCommandApplier
    {
        private struct RoadCommandLogState
        {
            internal string Action;
            internal uint Frame;
        }

        private readonly CommandHost m_CommandHost;
        private readonly RoadCommandHost m_RoadCommandHost;
        private readonly DispatchActions m_RailDispatchActions;
        private readonly DispatchActions m_RoadDispatchActions;
        private readonly RouteWriter m_RouteWriter;
        private readonly LaunchActions m_LaunchActions;
        private readonly RetireHost m_RetireHost;
        private readonly RetireHandoff m_RetireHandoff;
        private readonly RoadRetireHandoff m_RoadRetireHandoff;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<Entity, RoadCommandLogState> m_RoadCommandLogFrames = new Dictionary<Entity, RoadCommandLogState>();

        private const uint ROAD_COMMAND_LOG_COOLDOWN_FRAMES = 120;

        public DispatchCommandApplier(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
            m_CommandHost = new CommandHost(runtime);
            m_RoadCommandHost = new RoadCommandHost(runtime);
            m_RailDispatchActions = new DispatchActions(m_CommandHost);
            m_RoadDispatchActions = new DispatchActions(m_RoadCommandHost);
            m_RouteWriter = new RouteWriter(m_CommandHost);
            m_LaunchActions = new LaunchActions(m_CommandHost, m_RouteWriter);
            m_RetireHost = new RetireHost(runtime);
            m_RetireHandoff = new RetireHandoff(m_RetireHost, m_CommandHost);
            m_RoadRetireHandoff = new RoadRetireHandoff(runtime, m_RetireHost, m_RoadCommandHost);
        }

        internal void CommitAssignedSlotHold(Entity vehicle, int slot, EntityCommandBuffer ecb)
        {
            if (TryGetDispatchActions(vehicle, out DispatchActions actions, out _))
                actions.CommitAssignedSlotHold(vehicle, slot, ecb);
        }

        internal void HoldDeparture(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            if (!TryGetDispatchActions(vehicle, out DispatchActions actions, out bool isRoad))
                return;

            if (isRoad)
                actions.HoldDeparture(vehicle, nowFrame, ecb);
            else
                actions.HoldDeparture(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void HoldDeparture(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            if (TryGetDispatchActions(vehicle, out DispatchActions actions, out _))
                actions.HoldDeparture(vehicle, nowFrame, ecb);
        }

        internal void ForceDepart(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            if (!TryGetDispatchActions(vehicle, out DispatchActions actions, out bool isRoad))
                return;

            if (isRoad)
                actions.ForceDepart(vehicle, nowFrame, ecb);
            else
                actions.ForceDepart(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void ForceDepart(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            if (TryGetDispatchActions(vehicle, out DispatchActions actions, out _))
                actions.ForceDepart(vehicle, nowFrame, ecb);
        }

        internal void CommitAssistLaunch(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            if (TryGetDispatchActions(vehicle, out DispatchActions actions, out _))
                actions.CommitAssistLaunch(vehicle, nowFrame, ecb);
        }

        internal void KeepDepartureHeld(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            if (TryGetDispatchActions(vehicle, out DispatchActions actions, out _))
                actions.HoldDeparture(vehicle, nowFrame, ecb);
        }

        internal void Retire(Entity vehicle, string reason)
        {
            if (!RuntimePorts.TryResolveVehicleLifecycle(m_Runtime, vehicle, out LifecycleKind lifecycle))
            {
                LogRoadCommand(vehicle, "车辆生命周期解析失败，拒绝退役交接");
                return;
            }

            if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity sourceLine))
                return;
            if (lifecycle == LifecycleKind.Road)
            {
                if (!m_RoadCommandHost.EntityManager.HasComponent<PublicTransport>(vehicle)
                    || !m_RoadCommandHost.EntityManager.HasComponent<Target>(vehicle)
                    || !m_RoadCommandHost.EntityManager.HasComponent<Owner>(vehicle))
                {
                    return;
                }

                RetireStartInput roadInput = new RetireStartInput(
                    vehicle,
                    sourceLine,
                    m_RoadCommandHost.ReadPublicTransport(vehicle),
                    m_RoadCommandHost.ReadTarget(vehicle),
                    m_RoadCommandHost.ReadOwner(vehicle),
                    reason);
                if (m_RetireHost.BeginRetire(roadInput, out RetireStartContext roadStart))
                    m_RoadRetireHandoff.Begin(roadInput, roadStart);
                return;
            }

            if (!m_CommandHost.EntityManager.HasComponent<PublicTransport>(vehicle)
                || !m_CommandHost.EntityManager.HasComponent<Target>(vehicle)
                || !m_CommandHost.EntityManager.HasComponent<Owner>(vehicle))
            {
                return;
            }

            RetireStartInput railInput = new RetireStartInput(
                vehicle,
                sourceLine,
                m_CommandHost.ReadPublicTransport(vehicle),
                m_CommandHost.ReadTarget(vehicle),
                m_CommandHost.ReadOwner(vehicle),
                reason);
            if (m_RetireHost.BeginRetire(railInput, out RetireStartContext railStart))
                m_RetireHandoff.Begin(railInput, railStart);
        }

        internal void Launch(Entity vehicle, Entity line, int waypoint, EntityCommandBuffer ecb)
        {
            if (!TryGetDispatchActions(vehicle, out DispatchActions actions, out bool isRoad))
                return;

            if (isRoad)
                actions.ReleaseDeparture(vehicle, m_RoadCommandHost.Frame, ecb);
            else
                m_LaunchActions.Launch(vehicle, line, waypoint, ecb);
        }

        internal bool EnsurePreparingRoute(
            Entity vehicle,
            Entity line,
            int waypoint,
            EntityCommandBuffer ecb)
        {
            if (!TryGetDispatchActions(vehicle, out _, out bool isRoad))
                return false;

            if (isRoad)
            {
                if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                    || state != VehicleState.Preparing)
                {
                    return false;
                }

                RoadPreparingResult result = m_RoadCommandHost.EnsurePreparingOrigin(
                    vehicle,
                    line,
                    ecb);
                LogRoadPreparing(vehicle, line, result);
                return result == RoadPreparingResult.Pin
                    || result == RoadPreparingResult.Retarget;
            }

            return m_LaunchActions.EnsurePreparingRoute(vehicle, line, waypoint, ecb);
        }

        internal void EnsureRunningOriginStop(
            Entity vehicle,
            Entity line,
            EntityCommandBuffer ecb)
        {
            if (!TryGetDispatchActions(vehicle, out _, out bool isRoad) || !isRoad)
                return;

            if (!m_Runtime.m_VehicleView.TryGetState(vehicle, out VehicleState state)
                || state != VehicleState.Running)
            {
                return;
            }

            RoadOriginGuardResult result = m_RoadCommandHost.EnsureRunningOriginStop(
                vehicle,
                line,
                ecb);
            LogRoadOriginGuard(vehicle, line, result);
        }

        private bool TryGetDispatchActions(
            Entity vehicle,
            out DispatchActions actions,
            out bool isRoad)
        {
            actions = null;
            isRoad = false;
            if (!RuntimePorts.TryResolveVehicleLifecycle(m_Runtime, vehicle, out LifecycleKind lifecycle))
            {
                LogRoadCommand(vehicle, "车辆生命周期解析失败，拒绝道路命令");
                return false;
            }

            isRoad = lifecycle == LifecycleKind.Road;
            actions = isRoad ? m_RoadDispatchActions : m_RailDispatchActions;
            return true;
        }

        private void LogRoadCommand(Entity vehicle, string message)
        {
            if (!RtLog.VerboseEnabled)
                return;

            if (!ShouldLogRoadCommand(vehicle, "command"))
                return;

            m_RoadCommandHost.Log.Info("[RoadCommand] 车辆" + vehicle.Index + " " + message);
        }

        private void LogRoadPreparing(
            Entity vehicle,
            Entity line,
            RoadPreparingResult result)
        {
            if (!RtLog.VerboseEnabled)
                return;

            string action;
            string message;
            switch (result)
            {
                case RoadPreparingResult.Pin:
                    action = "pin";
                    message = "钉住始发RequireStop";
                    break;
                case RoadPreparingResult.Retarget:
                    action = "retarget";
                    message = "目标纠正到wp0并钉住RequireStop";
                    break;
                case RoadPreparingResult.Pending:
                    action = "pending";
                    message = "PathOwner仍在Pending，等待下一偏移1轮次";
                    break;
                case RoadPreparingResult.Stuck:
                    action = "stuck";
                    message = "PathOwner为Stuck，拒绝纠偏";
                    break;
                case RoadPreparingResult.UnsafeState:
                    action = "unsafe-state";
                    message = "已进入Testing/Arriving/Boarding，拒绝修改旧异常车";
                    break;
                case RoadPreparingResult.InvalidOrigin:
                    action = "invalid-origin";
                    message = "wp0或车辆组件不满足Preparing始发纠偏契约";
                    break;
                default:
                    return;
            }

            if (!ShouldLogRoadCommand(vehicle, action))
                return;

            m_RoadCommandHost.Log.Info("[RoadPreparing] action=" + action
                + " line=" + line.Index
                + " vehicle=" + vehicle.Index
                + " " + message);
        }

        private void LogRoadOriginGuard(
            Entity vehicle,
            Entity line,
            RoadOriginGuardResult result)
        {
            if (!RtLog.VerboseEnabled)
                return;

            string action;
            string message;
            switch (result)
            {
                case RoadOriginGuardResult.Pin:
                    action = "origin-pin";
                    message = "检测到严格wp0，补RequireStop";
                    break;
                case RoadOriginGuardResult.Protected:
                    action = "origin-protected";
                    message = "检测到严格wp0，RequireStop已保持";
                    break;
                case RoadOriginGuardResult.Boarding:
                    action = "origin-boarding";
                    message = "检测到严格wp0且已Boarding，等待Running转Idle";
                    break;
                case RoadOriginGuardResult.InvalidOrigin:
                    action = "origin-invalid";
                    message = "道路始发输入或线路契约无效，拒绝补停";
                    break;
                default:
                    return;
            }

            if (!ShouldLogRoadCommand(vehicle, action))
                return;

            m_RoadCommandHost.Log.Info("[RoadOriginGuard] action=" + action
                + " line=" + line.Index
                + " vehicle=" + vehicle.Index
                + " " + message);
        }

        private bool ShouldLogRoadCommand(Entity vehicle, string action)
        {
            uint nowFrame = m_RoadCommandHost.Frame;
            if (m_RoadCommandLogFrames.TryGetValue(vehicle, out RoadCommandLogState state)
                && string.Equals(state.Action, action, StringComparison.Ordinal)
                && nowFrame - state.Frame < ROAD_COMMAND_LOG_COOLDOWN_FRAMES)
            {
                return false;
            }

            m_RoadCommandLogFrames[vehicle] = new RoadCommandLogState
            {
                Action = action,
                Frame = nowFrame
            };
            return true;
        }

        internal void RemoveRoadCommandLog(Entity vehicle)
        {
            if (vehicle != Entity.Null)
                m_RoadCommandLogFrames.Remove(vehicle);
        }

        internal void ClearRoadCommandLogs()
        {
            m_RoadCommandLogFrames.Clear();
        }

        internal void ForceRetireOne()
        {
            if (!m_RetireHandoff.TryGetForceRetireVehicle(out Entity vehicle))
                return;

            m_CommandHost.Log.Info("[F7] 强制回库车辆" + vehicle.Index);
            Retire(vehicle, "F7强制");
        }

        internal void TickRetireHandoffStages(uint nowFrame, IReadOnlyList<FramePlanEntry> candidates)
        {
            m_RetireHandoff.TickRetireHandoffStages(nowFrame, candidates);
            m_RoadRetireHandoff.Tick(nowFrame, candidates);
        }

        internal void FinalizeRetireDispatchLockTerminals()
        {
            m_RetireHandoff.FinalizeRetireDispatchLockTerminals();
        }

        internal void RemoveRetireHandoff(Entity vehicle)
        {
            m_RetireHandoff.RemoveRetireHandoff(vehicle);
            m_RoadRetireHandoff.Remove(vehicle);
        }

        internal void ClearRetireHandoffState()
        {
            m_RetireHandoff.ClearRetireHandoffState();
            m_RoadRetireHandoff.Clear();
        }

        internal void ResetRetireDispatchLockStages()
        {
            m_RetireHandoff.ResetRetireDispatchLockStages();
        }

        internal void ProjectRetireDispatchLocksImmediatelyOnLoad()
        {
            m_RetireHandoff.ProjectRetireDispatchLocksImmediatelyOnLoad();
        }

        internal void ReconcileRetireDispatchLocksOnReady()
        {
            m_RetireHandoff.ReconcileRetireDispatchLocksOnReady();
        }

        internal string DescribeRetireShadowTargetKind(Entity entity)
        {
            return m_RetireHandoff.DescribeRetireShadowTargetKind(entity);
        }

        internal static string DescribeRetireShadowEntity(Entity entity)
        {
            return RetireHandoff.DescribeRetireShadowEntity(entity);
        }

        internal void FlushRetireShadowSnapshots(Entity vehicle, string reason)
        {
            m_RetireHandoff.FlushRetireShadowSnapshots(vehicle, reason);
        }

        internal void ResetRetireShadowSnapshots(Entity vehicle)
        {
            m_RetireHandoff.ResetRetireShadowSnapshots(vehicle);
        }
    }
}
