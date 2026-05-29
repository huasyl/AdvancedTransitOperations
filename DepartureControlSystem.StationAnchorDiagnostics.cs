using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        private StationAnchorDiag m_StationAnchorDiag;

        public void RequestDumpStationAnchorObservationDiagnostics()
        {
            GetStationAnchorDiag().Dump();
        }

        private StationAnchorObservationDiagnosticsDto BuildStationAnchorObservationDiagnostics()
        {
            return GetStationAnchorDiag().Build();
        }

        private StationAnchorDiag GetStationAnchorDiag()
        {
            if (m_StationAnchorDiag == null)
            {
                m_StationAnchorDiag = new StationAnchorDiag(
                    EntityManager,
                    m_LineQuery,
                    m_StopDwell,
                    m_SimulationSystem,
                    m_CitySystem,
                    message => log.Info(message),
                    GetWorkbenchLineId,
                    ResolveWorkbenchStopEntity,
                    ResolveWorkbenchStationName,
                    MakeLineWaypointStopObservationKey,
                    (line, waypointIndex) =>
                    {
                        if (!TryResolveStationStopDwellAnchor(line, waypointIndex, out var anchor))
                            return (false, string.Empty, -1);

                        return (
                            true,
                            anchor.StationAnchorId,
                            anchor.BuildingEntity == Entity.Null ? -1 : anchor.BuildingEntity.Index);
                    },
                    MakeStationStopDwellObservationKey,
                    () => m_StationAnchorDiagTotalAnchorMissing,
                    () => m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal,
                    () => m_StationAnchorDiagTotalSuspiciousOriginOrTerminal,
                    () => m_StationAnchorDiagTotalSuspiciousLongDwell,
                    () => m_LastStationStopDwellLegacyRestoredCount,
                    () => m_LastStationStopDwellAnchorRestoredCount,
                    (int)SIM_FRAMES_PER_MINUTE);
            }

            return m_StationAnchorDiag;
        }
    }
}
