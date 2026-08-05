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
        private readonly CommandHost m_CommandHost;
        private readonly DispatchActions m_DispatchActions;
        private readonly RouteWriter m_RouteWriter;
        private readonly LaunchActions m_LaunchActions;
        private readonly RetireHost m_RetireHost;
        private readonly RetireHandoff m_RetireHandoff;

        public DispatchCommandApplier(ModRuntimeHostSystem runtime)
        {
            m_CommandHost = new CommandHost(runtime);
            m_DispatchActions = new DispatchActions(m_CommandHost);
            m_RouteWriter = new RouteWriter(m_CommandHost);
            m_LaunchActions = new LaunchActions(m_CommandHost, m_RouteWriter);
            m_RetireHost = new RetireHost(runtime);
            m_RetireHandoff = new RetireHandoff(m_RetireHost);
        }

        internal void CommitAssignedSlotHold(Entity vehicle, int slot, EntityCommandBuffer ecb)
        {
            m_DispatchActions.CommitAssignedSlotHold(vehicle, slot, ecb);
        }

        internal void HoldDeparture(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            m_DispatchActions.HoldDeparture(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void HoldDeparture(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            m_DispatchActions.HoldDeparture(vehicle, nowFrame, ecb);
        }

        internal void ForceDepart(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            m_DispatchActions.ForceDepart(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void ForceDepart(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            m_DispatchActions.ForceDepart(vehicle, nowFrame, ecb);
        }

        internal void CommitAssistLaunch(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            m_DispatchActions.CommitAssistLaunch(vehicle, nowFrame, ecb);
        }

        internal void KeepDepartureHeld(Entity vehicle, uint nowFrame, EntityCommandBuffer ecb)
        {
            m_DispatchActions.HoldDeparture(vehicle, nowFrame, ecb);
        }

        internal void Retire(Entity vehicle, string reason)
        {
            m_RetireHandoff.Retire(
                vehicle,
                m_CommandHost.ReadPublicTransport(vehicle),
                m_CommandHost.ReadTarget(vehicle),
                reason);
        }

        internal void Launch(Entity vehicle, Entity line, int waypoint, EntityCommandBuffer ecb)
        {
            m_LaunchActions.Launch(vehicle, line, waypoint, ecb);
        }

        internal bool EnsurePreparingRoute(
            Entity vehicle,
            Entity line,
            int waypoint,
            EntityCommandBuffer ecb)
        {
            return m_LaunchActions.EnsurePreparingRoute(vehicle, line, waypoint, ecb);
        }

        internal void ForceRetireOne()
        {
            m_RetireHandoff.ForceRetireOne();
        }

        internal void TickRetireHandoffStages(uint nowFrame, IReadOnlyList<FramePlanEntry> candidates)
        {
            m_RetireHandoff.TickRetireHandoffStages(nowFrame, candidates);
        }

        internal void FinalizeRetireDispatchLockTerminals()
        {
            m_RetireHandoff.FinalizeRetireDispatchLockTerminals();
        }

        internal void RemoveRetireHandoff(Entity vehicle)
        {
            m_RetireHandoff.RemoveRetireHandoff(vehicle);
        }

        internal void ClearRetireHandoffState()
        {
            m_RetireHandoff.ClearRetireHandoffState();
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
