using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Persist
    {
        private readonly LapStore m_Laps;
        private readonly DwellStore m_Dwell;
        private readonly SliceStore m_Slices;
        private readonly SliceAdmission m_Admission;

        internal Persist(LapStore laps, DwellStore dwell, SliceStore slices, SliceAdmission admission)
        {
            m_Laps = laps;
            m_Dwell = dwell;
            m_Slices = slices;
            m_Admission = admission;
        }

        internal void ClearWaypointDwell() => m_Dwell.Waypoints.Clear();

        internal void PutWaypointDwell(ulong key, DwellObservation observation) =>
            m_Dwell.RecordWaypoint(key, observation);

        internal void ClearStationDwell() => m_Dwell.Stations.Clear();

        internal void PutStationDwell(string key, StationDwellObservation observation) =>
            m_Dwell.RecordStation(key, observation);

        internal void ClearSliceObservations() => m_Slices.ClearObservations();

        internal void PutSlice(Entity line, ulong key, TraversalSliceObservation observation) =>
            m_Slices.Record(line, key, observation);

        internal void PutDailyQuota(LineKey lak, int dateKey, int usedCount) =>
            m_Admission.RestoreDailyQuota(lak, dateKey, usedCount);

        internal void PutColdStart(
            LineKey lak,
            ulong signature,
            int remaining,
            int pendingFinalMinute,
            int pendingFinalDateKey) =>
            m_Admission.RestoreColdStart(
                lak,
                signature,
                remaining,
                pendingFinalMinute,
                pendingFinalDateKey);

        internal void ClearAdmissionState() => m_Admission.ClearPersistedState();

        internal void ClearLaps() => m_Laps.Clear();

        internal void ClearDwell() => m_Dwell.Clear();

        internal void ClearSlices() => m_Slices.Clear();

        internal void ClearVehicle(Entity vehicle)
        {
            m_Laps.Remove(vehicle);
            m_Dwell.Remove(vehicle);
            m_Slices.Remove(vehicle);
        }

        internal void ClearLap(Entity vehicle) => m_Laps.Remove(vehicle);

        internal void ClearDwell(Entity vehicle) => m_Dwell.Remove(vehicle);

        internal void ClearVehicleSlices(Entity vehicle) => m_Slices.Remove(vehicle);

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

        internal bool DropSlice(Entity vehicle, out int sliceIndex)
        {
            if (m_Slices.Sessions.TryGetValue(vehicle, out VehicleTraversalSliceSession session))
            {
                sliceIndex = session.SliceIndex;
                m_Slices.Sessions.Remove(vehicle);
                return true;
            }

            sliceIndex = -1;
            return false;
        }
    }
}
