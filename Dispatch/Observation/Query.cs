using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Query
    {
        private readonly LapStore m_Laps;
        private readonly DwellStore m_Dwell;
        private readonly SliceStore m_Slices;
        private readonly BusSegStore m_BusSeg;

        internal Query(LapStore laps, DwellStore dwell, SliceStore slices, BusSegStore busSeg)
        {
            m_Laps = laps;
            m_Dwell = dwell;
            m_Slices = slices;
            m_BusSeg = busSeg;
        }

        internal bool TryLapStart(Entity vehicle, out float odometer) =>
            m_Laps.TryStart(vehicle, out odometer);

        internal bool TryLapStartFrame(Entity vehicle, out uint frame) =>
            m_Laps.TryStartFrame(vehicle, out frame);

        internal bool TryLapDistance(Entity vehicle, out float distance) =>
            m_Laps.TryDistance(vehicle, out distance);

        internal bool TryLapFrames(Entity vehicle, out uint frames) =>
            m_Laps.TryFrames(vehicle, out frames);

        internal bool NeedsLapStart(Entity vehicle) =>
            !m_Laps.StartOdometer.ContainsKey(vehicle);

        internal bool TryStationDwell(string key, out StationDwellObservation observation) =>
            m_Dwell.TryStation(key, out observation);

        internal bool TryWaypointDwell(ulong key, out DwellObservation observation) =>
            m_Dwell.TryWaypoint(key, out observation);

        internal bool TryDwellStart(Entity vehicle, out uint frame) =>
            m_Dwell.TryStart(vehicle, out frame);

        internal bool TrySlice(ulong key, out TraversalSliceObservation observation) =>
            m_Slices.TryObservation(key, out observation);

        internal bool TryBusSeg(BusSegKey key, out BusSegObservation observation) =>
            m_BusSeg.TryObservation(key, out observation);

        internal int WaypointDwellCount => m_Dwell.Waypoints.Count;

        internal int StationDwellCount => m_Dwell.Stations.Count;

        internal IEnumerable<DwellObservation> WaypointDwells => m_Dwell.Waypoints.Values;

        internal IEnumerable<StationDwellObservation> StationDwells => m_Dwell.Stations.Values;

        internal IEnumerable<KeyValuePair<BusSegKey, BusSegObservation>> BusSegs => m_BusSeg.Observations;

        internal IReadOnlyList<TraversalSliceActualSample> ActualSamples => m_Slices.RecentActualSamples;

        internal IReadOnlyList<TraversalPositionSample> PositionSamples => m_Slices.RecentPositionSamples;
    }
}
