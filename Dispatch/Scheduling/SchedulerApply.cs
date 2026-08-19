using System;
using System.Collections.Generic;
using Game.Common;
using Game.Vehicles;
using RapidTransitMod.Core;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class SchedulerApply
    {
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly HashSet<string> m_CurrentDirtyLineKeys = new HashSet<string>();
        private readonly HashSet<string> m_PendingDirtyLineKeys = new HashSet<string>();
        private readonly List<Entity> m_ResolvedDirtyLines = new List<Entity>();
        private bool m_AllLinesDirty;
        private bool m_PendingAllLinesDirty;

        public SchedulerApply(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        private EntityManager EntityManager => m_Runtime.EntityManager;
        private TimedLogger log => m_Runtime.log;
        internal IReadOnlyList<Entity> ResolvedDirtyLines => m_ResolvedDirtyLines;

        internal void BeginFrame()
        {
            m_CurrentDirtyLineKeys.Clear();
            m_CurrentDirtyLineKeys.UnionWith(m_PendingDirtyLineKeys);
            m_PendingDirtyLineKeys.Clear();
            m_AllLinesDirty = m_PendingAllLinesDirty;
            m_PendingAllLinesDirty = false;
            m_ResolvedDirtyLines.Clear();
        }

        internal void MarkDirty(Entity line)
        {
            if (line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && m_Runtime.m_LineView.TryFrame(line, out LineFrame frame))
            {
                m_CurrentDirtyLineKeys.Add(frame.StoreKey.ToString());
                return;
            }
            m_AllLinesDirty = true;
        }

        internal void MarkAllDirty() => m_AllLinesDirty = true;

        internal void MarkPendingDirty(string lineKey)
        {
            if (!string.IsNullOrEmpty(lineKey))
                m_PendingDirtyLineKeys.Add(lineKey);
        }

        internal void MarkPendingAllDirty() => m_PendingAllLinesDirty = true;

        internal void SealDirtyLines()
        {
            m_ResolvedDirtyLines.Clear();
            if (!m_AllLinesDirty && m_CurrentDirtyLineKeys.Count == 0)
                return;

            NativeArray<Entity> lines = m_Runtime.m_LineQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!m_AllLinesDirty
                        && (!m_Runtime.m_LineView.TryFrame(line, out LineFrame frame)
                            || !m_CurrentDirtyLineKeys.Contains(frame.StoreKey.ToString())))
                    {
                        continue;
                    }
                    m_ResolvedDirtyLines.Add(line);
                }
            }
            finally
            {
                lines.Dispose();
            }
            m_ResolvedDirtyLines.Sort(CompareEntity);
        }

        internal void ResetCity()
        {
            m_CurrentDirtyLineKeys.Clear();
            m_PendingDirtyLineKeys.Clear();
            m_ResolvedDirtyLines.Clear();
            m_AllLinesDirty = false;
            m_PendingAllLinesDirty = false;
        }

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
                Apply(ecb, clockSnapshot, lineCandidates, fullMinuteSweep);
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
            IReadOnlyList<Entity> lineCandidates,
            bool fullMinuteSweep)
        {
            m_Runtime.m_DispatchScheduler.Tick(clockSnapshot, lineCandidates, fullMinuteSweep);

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

        private static int CompareEntity(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Version.CompareTo(right.Version);
        }
    }
}
