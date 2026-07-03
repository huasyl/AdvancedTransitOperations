using System;
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

        public DispatchCommandApplier(DispatchRuntimeSystem runtime)
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

        internal void CommitPublicTransport(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            EntityCommandBuffer ecb)
        {
            m_DispatchActions.CommitPublicTransport(vehicle, publicTransport, ecb);
        }

        internal void HoldDeparture(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            m_DispatchActions.HoldDeparture(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void ForceDepart(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            uint nowFrame,
            EntityCommandBuffer ecb)
        {
            m_DispatchActions.ForceDepart(vehicle, ref publicTransport, nowFrame, ecb);
        }

        internal void Retire(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb,
            string reason = "")
        {
            m_RetireHandoff.Retire(vehicle, publicTransport, target, ecb, reason);
        }

        internal void Launch(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            EntityCommandBuffer ecb)
        {
            m_LaunchActions.Launch(vehicle, publicTransport, target, waypoints, ecb);
        }

        internal void EnsurePreparingRoute(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport publicTransport,
            ref Target target,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            bool boarding,
            EntityCommandBuffer ecb)
        {
            m_LaunchActions.EnsurePreparingRoute(
                vehicle,
                ref publicTransport,
                ref target,
                waypoints,
                currentWaypointIndex,
                boarding,
                ecb);
        }

        internal void Repath(
            Entity vehicle,
            Game.Vehicles.PublicTransport publicTransport,
            Target target,
            EntityCommandBuffer ecb)
        {
            m_RouteWriter.Repath(vehicle, publicTransport, target, ecb);
        }

        internal void ForceRetireOne(EntityCommandBuffer ecb)
        {
            m_RetireHandoff.ForceRetireOne(ecb);
        }

        internal void GuardRetireHandoffInputs(uint nowFrame)
        {
            m_RetireHandoff.GuardRetireHandoffInputs(nowFrame);
        }

        internal void TickRetireHandoffWatch(EntityCommandBuffer ecb, uint nowFrame)
        {
            m_RetireHandoff.TickRetireHandoffWatch(ecb, nowFrame);
        }

        internal void ReleaseCompletedRetireHandoffs()
        {
            m_RetireHandoff.ReleaseCompletedRetireHandoffs();
        }

        internal void RemoveRetireHandoff(Entity vehicle)
        {
            m_RetireHandoff.RemoveRetireHandoff(vehicle);
        }

        internal void ClearRetireHandoffState()
        {
            m_RetireHandoff.ClearRetireHandoffState();
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
