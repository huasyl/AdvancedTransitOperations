using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Persist
    {
        private readonly LapStore m_Laps;
        private readonly DwellStore m_Dwell;
        private readonly BusSegStore m_BusSeg;

        internal Persist(
            LapStore laps,
            DwellStore dwell,
            BusSegStore busSeg)
        {
            m_Laps = laps;
            m_Dwell = dwell;
            m_BusSeg = busSeg;
        }

        internal void ClearWaypointDwell() => m_Dwell.Waypoints.Clear();

        internal void PutWaypointDwell(ulong key, DwellObservation observation) =>
            m_Dwell.RecordWaypoint(key, observation);

        internal void ClearStationDwell() => m_Dwell.Stations.Clear();

        internal void PutStationDwell(string key, StationDwellObservation observation) =>
            m_Dwell.RecordStation(key, observation);

        internal void ClearBusSeg() => m_BusSeg.Clear();

        internal void PutBusSeg(BusSegKey key, BusSegObservation observation) =>
            m_BusSeg.Put(key, observation);

        internal void ClearLaps() => m_Laps.Clear();

        internal void ClearDwell() => m_Dwell.Clear();

        internal void ClearVehicle(Entity vehicle)
        {
            m_Laps.Remove(vehicle);
            m_Dwell.Remove(vehicle);
        }

        internal void ClearLap(Entity vehicle) => m_Laps.Remove(vehicle);

        internal bool ClearLapForStructure(Entity vehicle) => m_Laps.Remove(vehicle);

        internal void ClearDwell(Entity vehicle) => m_Dwell.Remove(vehicle);

        internal bool RemoveDwellStart(Entity vehicle) =>
            m_Dwell.RemoveStart(vehicle);

        internal void SetDwellStart(Entity vehicle, uint frame) =>
            m_Dwell.SetStart(vehicle, frame);

        internal bool TryLapFrames(Entity vehicle, out uint frames) =>
            m_Laps.TryFrames(vehicle, out frames);

        internal bool TryLapDistance(Entity vehicle, out float distance) =>
            m_Laps.TryDistance(vehicle, out distance);

        internal void StartLap(Entity vehicle, float odometer, uint frame) =>
            m_Laps.Start(vehicle, odometer, frame);

        internal void ClearLapStart(Entity vehicle)
        {
            m_Laps.StartOdometer.Remove(vehicle);
            m_Laps.StartFrame.Remove(vehicle);
        }

        internal void SetLapFrames(Entity vehicle, uint frames) =>
            m_Laps.SetFrames(vehicle, frames);

        internal void SetLapDistance(Entity vehicle, float distance) =>
            m_Laps.SetDistance(vehicle, distance);

        internal void SetLapStartFrame(Entity vehicle, uint frame) =>
            m_Laps.StartFrame[vehicle] = frame;

        internal void SetLapStartOdo(Entity vehicle, float odometer) =>
            m_Laps.StartOdometer[vehicle] = odometer;

        internal void MarkLapRestored(Entity vehicle) =>
            m_Laps.MarkRestored(vehicle);

        internal void ClearLapRestore(Entity vehicle)
        {
            m_Laps.StartFrame.Remove(vehicle);
            m_Laps.Frames.Remove(vehicle);
            m_Laps.RestoredRunning.Remove(vehicle);
        }
    }
}
