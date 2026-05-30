using Unity.Entities;

namespace RapidTransitMod
{
    internal sealed class StationAnchorDiagnostics
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private StationAnchorDiag m_StationAnchorDiag;

        public StationAnchorDiagnostics(DispatchRuntimeSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Dump()
        {
            GetStationAnchorDiag().Dump();
        }

        public StationAnchorObservationDiagnosticsDto Build()
        {
            return GetStationAnchorDiag().Build();
        }

        private StationAnchorDiag GetStationAnchorDiag()
        {
            if (m_StationAnchorDiag == null)
            {
                m_StationAnchorDiag = new StationAnchorDiag(
                    m_Runtime.EntityManager,
                    m_Runtime.m_LineQuery,
                    m_Runtime.m_StopDwell,
                    m_Runtime.m_SimulationSystem,
                    m_Runtime.m_CitySystem,
                    message => m_Runtime.log.Info(message),
                    m_Runtime.GetWorkbenchLineId,
                    m_Runtime.ResolveWorkbenchStopEntity,
                    m_Runtime.ResolveWorkbenchStationName,
                    DispatchRuntimeSystem.MakeLineWaypointStopObservationKey,
                    (line, waypointIndex) =>
                    {
                        if (!m_Runtime.TryResolveStationStopDwellAnchor(line, waypointIndex, out var anchor))
                            return (false, string.Empty, -1);

                        return (
                            true,
                            anchor.StationAnchorId,
                            anchor.BuildingEntity == Entity.Null ? -1 : anchor.BuildingEntity.Index);
                    },
                    m_Runtime.MakeStationStopDwellObservationKey,
                    () => m_Runtime.m_StationAnchorDiagTotalAnchorMissing,
                    () => m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal,
                    () => m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal,
                    () => m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell,
                    () => m_Runtime.m_LastStationStopDwellLegacyRestoredCount,
                    () => m_Runtime.m_LastStationStopDwellAnchorRestoredCount,
                    (int)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            }

            return m_StationAnchorDiag;
        }
    }
}
