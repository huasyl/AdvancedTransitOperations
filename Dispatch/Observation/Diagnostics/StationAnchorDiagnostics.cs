using Unity.Entities;
using RapidTransitMod.Dispatch.Observation;

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
                    m_Runtime.m_ObsQuery,
                    m_Runtime.m_SimulationSystem,
                    m_Runtime.m_CitySystem,
                    message => m_Runtime.log.Info(message),
                    m_Runtime.LineStableId,
                    m_Runtime.m_Resolve.Stop,
                    m_Runtime.m_Resolve.StationName,
                    Keys.WaypointDwell,
                    (line, waypointIndex) =>
                    {
                        if (!m_Runtime.m_Observation.DwellAnchor(line, waypointIndex, out var anchor))
                            return (false, string.Empty, -1);

                        return (
                            true,
                            anchor.StationAnchorId,
                            anchor.BuildingEntity == Entity.Null ? -1 : anchor.BuildingEntity.Index);
                    },
                    m_Runtime.m_Observation.DwellKey,
                    () => m_Runtime.m_StationAnchorDiagTotalAnchorMissing,
                    () => m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal,
                    () => m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal,
                    () => m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell,
                    () => m_Runtime.m_LastStationStopDwellLegacyRestoredCount,
                    () => m_Runtime.m_LastStationStopDwellAnchorRestoredCount,
                    () => m_Runtime.m_SimClock.Snapshot);
            }

            return m_StationAnchorDiag;
        }
    }
}
