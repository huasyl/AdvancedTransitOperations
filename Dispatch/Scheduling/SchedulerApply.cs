using System;
using System.Collections.Generic;
using Game.Common;
using Game.Vehicles;
using RapidTransitMod.Core;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SchedulerApply
    {
        private readonly ModRuntimeHostSystem m_Runtime;

        public SchedulerApply(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;

        public void Tick(
            EntityCommandBuffer ecb,
            ClockSnapshot clockSnapshot,
            IReadOnlyList<Entity> lineCandidates,
            bool fullMinuteSweep)
        {
            int nowMinute = clockSnapshot.NowMinute;
            if (!fullMinuteSweep && (lineCandidates == null || lineCandidates.Count == 0))
                return;

            try
            {
                Apply(ecb, clockSnapshot, lineCandidates);
                if (fullMinuteSweep)
                    m_Runtime.m_LastSchedulerTickMinute = nowMinute;
            }
            catch (Exception ex)
            {
                log.Info("[运行异常] SchedulerTick -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }

        private void Apply(
            EntityCommandBuffer ecb,
            ClockSnapshot clockSnapshot,
            IReadOnlyList<Entity> lineCandidates)
        {
            m_Runtime.m_DispatchScheduler.Tick(clockSnapshot, lineCandidates);

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

                m_Runtime.m_CommandApplier.Retire(vehicle, retireDecisions[i].Reason);
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
