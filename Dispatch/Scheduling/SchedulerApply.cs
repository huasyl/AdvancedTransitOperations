using System;
using System.Collections.Generic;
using Game.Common;
using Game.Vehicles;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SchedulerApply
    {
        private readonly DispatchRuntimeSystem m_Runtime;

        public SchedulerApply(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public void Tick(EntityCommandBuffer ecb, int nowMin)
        {
            if (nowMin != m_Runtime.m_LastSchedulerTickMinute)
            {
                try
                {
                    Apply(ecb, nowMin);
                    m_Runtime.m_LastSchedulerTickMinute = nowMin;
                }
                catch (Exception ex)
                {
                    log.Info("[运行异常] SchedulerTick -> " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
            }
        }

        private void Apply(EntityCommandBuffer ecb, int nowMin)
        {
            m_Runtime.m_DispatchScheduler.Tick(nowMin);

            IReadOnlyList<DispatchScheduler.RetireDecision> retireDecisions = m_Runtime.m_DispatchScheduler.RetireDecisions;
            for (int i = 0; i < retireDecisions.Count; i++)
            {
                Entity vehicle = retireDecisions[i].Vehicle;
                if (vehicle == Entity.Null
                    || !EntityManager.Exists(vehicle)
                    || !EntityManager.HasComponent<PublicTransport>(vehicle)
                    || !EntityManager.HasComponent<Target>(vehicle))
                {
                    continue;
                }

                PublicTransport publicTransport = EntityManager.GetComponentData<PublicTransport>(vehicle);
                Target target = EntityManager.GetComponentData<Target>(vehicle);
                m_Runtime.m_CommandApplier.Retire(vehicle, publicTransport, target, ecb, retireDecisions[i].Reason);
            }

            IReadOnlyList<DispatchScheduler.SlotClaim> slotClaims = m_Runtime.m_DispatchScheduler.SlotClaims;
            for (int i = 0; i < slotClaims.Count; i++)
            {
                DispatchScheduler.SlotClaim claim = slotClaims[i];
                if (claim.ReleasedVehicle != Entity.Null)
                    m_Runtime.m_VehicleRegistry.ClearTarget(claim.ReleasedVehicle);
                if (claim.Vehicle == Entity.Null || !EntityManager.Exists(claim.Vehicle))
                    continue;

                m_Runtime.m_VehicleRegistry.SetTarget(claim.Vehicle, claim.Target);
                if (claim.ClearIdle)
                    m_Runtime.m_VehicleRegistry.ClearIdle(claim.Vehicle);
                if (claim.CommitHold)
                    m_Runtime.m_CommandApplier.CommitAssignedSlotHold(claim.Vehicle, claim.Target, ecb);
            }
        }
    }
}
