using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Runtime
{
    internal sealed class LineStructureInvalidator
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Dictionary<Entity, PendingInvalidation> m_Pending = new Dictionary<Entity, PendingInvalidation>();

        private readonly struct PendingInvalidation
        {
            public readonly Entity Line;
            public readonly ulong OldSignature;
            public readonly ulong NewSignature;
            public readonly int OldAtomCount;
            public readonly int NewAtomCount;

            public PendingInvalidation(
                Entity line,
                ulong oldSignature,
                ulong newSignature,
                int oldAtomCount,
                int newAtomCount)
            {
                Line = line;
                OldSignature = oldSignature;
                NewSignature = newSignature;
                OldAtomCount = oldAtomCount;
                NewAtomCount = newAtomCount;
            }

            public PendingInvalidation WithLatest(ulong newSignature, int newAtomCount)
            {
                return new PendingInvalidation(Line, OldSignature, newSignature, OldAtomCount, newAtomCount);
            }
        }

        internal LineStructureInvalidator(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal void Request(Entity line, ulong oldSignature, ulong newSignature, int oldAtomCount, int newAtomCount)
        {
            if (line == Entity.Null)
                return;
            if (!m_Runtime.m_SystemReady)
                return;

            if (m_Pending.TryGetValue(line, out PendingInvalidation pending))
            {
                m_Pending[line] = pending.WithLatest(newSignature, newAtomCount);
                return;
            }

            m_Pending[line] = new PendingInvalidation(line, oldSignature, newSignature, oldAtomCount, newAtomCount);
        }

        internal void Drain()
        {
            if (m_Pending.Count == 0)
                return;

            List<PendingInvalidation> pending = new List<PendingInvalidation>(m_Pending.Values);
            m_Pending.Clear();

            m_Runtime.m_LineTimes.Clear();
            m_Runtime.m_LineMileage.Clear();
            m_Runtime.m_LineView.Clear();
            m_Runtime.m_TrackProjection.ClearLineRunningVehicleSnapshots();
            m_Runtime.m_StationContextQuery.Clear();

            for (int i = 0; i < pending.Count; i++)
                DrainLine(pending[i]);
        }

        private void DrainLine(PendingInvalidation pending)
        {
            Entity line = pending.Line;
            m_Runtime.m_LapCache.RemoveLine(line);
            m_Runtime.m_DispatchCache.RemoveLine(line);
            m_Runtime.m_Observation.InvalidateSliceLine(line);
            m_Runtime.m_LineProfile.RemoveStability(line);
            m_Runtime.m_Bypass.ClearLine(line);

            int clearedVehicles = 0;
            NativeArray<Entity> vehicles = m_Runtime.m_VehicleView.Keys(Allocator.Temp);
            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];
                    if (!m_Runtime.m_VehicleView.TryGetLine(vehicle, out Entity vehicleLine)
                        || vehicleLine != line)
                    {
                        continue;
                    }

                    ClearVehiclePosition(vehicle);
                    clearedVehicles++;
                }
            }
            finally
            {
                vehicles.Dispose();
            }

            if (RtLog.CacheInvalidationDiagnosticsEnabled)
            {
                m_Runtime.log.Info("[LineStructureInvalidated] line=" + line.Index
                    + " oldSig=" + pending.OldSignature
                    + " newSig=" + pending.NewSignature
                    + " oldAtoms=" + pending.OldAtomCount
                    + " newAtoms=" + pending.NewAtomCount
                    + " vehicles=" + clearedVehicles
                    + " clearLineTimes=1"
                    + " clearLineMileage=1"
                    + " clearSlices=1"
                    + " clearBypassLine=1"
                    + " clearDispatchCache=1"
                    + " clearLapCache=1"
                    + " clearLineProfile=1"
                    + " clearVehiclePosition=" + clearedVehicles);
            }
        }

        private void ClearVehiclePosition(Entity vehicle)
        {
            m_Runtime.m_InvalidatedMidStopRecoveryPending.Remove(vehicle);
            if (m_Runtime.m_StopSessionWaypointIndex.TryGetValue(vehicle, out int stopSessionWaypointIndex)
                && stopSessionWaypointIndex > 0
                && !m_Runtime.m_DeparturePendingSinceFrame.ContainsKey(vehicle))
            {
                m_Runtime.m_InvalidatedMidStopRecoveryPending.Add(vehicle);
            }

            m_Runtime.m_CachedWpIdx.Remove(vehicle);
            m_Runtime.m_WaypointIndex.Remove(vehicle);
            m_Runtime.m_RouteProgress.Remove(vehicle);
            m_Runtime.m_TrackProjection.ClearVehicle(vehicle);
            m_Runtime.m_ObsPersist.ClearLap(vehicle);
            m_Runtime.m_Observation.ClearVehicleSlices(vehicle);
            m_Runtime.m_StopSessionLine.Remove(vehicle);
            m_Runtime.m_StopSessionWaypointIndex.Remove(vehicle);
            m_Runtime.m_StopSessionArrivalFrame.Remove(vehicle);
            m_Runtime.m_StopSessionBoardingChangeCount.Remove(vehicle);
            m_Runtime.m_DeparturePendingSinceFrame.Remove(vehicle);
            m_Runtime.m_ForcedMidStopBoardingGraceUntil.Remove(vehicle);
        }
    }
}
