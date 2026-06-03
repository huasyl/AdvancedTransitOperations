using Game.Routes;
using RapidTransitMod.TrackModel;
using RapidTransitMod.TrackProjection;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class RuntimeObs
    {
        private readonly Capture m_Capture;

        public RuntimeObs(Capture capture)
        {
            m_Capture = capture;
        }

        public void RecordLap(Entity vehicle, string reason)
        {
            m_Capture.RecordLapStart(vehicle, reason);
        }

        public void UpdateLap(Entity vehicle)
        {
            m_Capture.UpdateLapStats(vehicle);
        }

        public bool LapTiming(
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out float runFrames,
            out float stopFrames,
            out int stopCount,
            out int passCount)
        {
            return m_Capture.TryGetTraversalProfileLapTiming(line, waypoints, out runFrames, out stopFrames, out stopCount, out passCount);
        }

        public void UpdateSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            m_Capture.UpdateVehicleTraversalSliceObservation(vehicle, line, waypoints, nowFrame);
        }

        public bool ShouldSample(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            uint nowFrame)
        {
            return m_Capture.ShouldSampleVehicleTraversalSliceObservation(vehicle, line, waypoints, nowFrame);
        }

        public bool BuildPlan(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out TraversalSliceSamplingPlan plan)
        {
            return m_Capture.TryBuildTraversalSliceSamplingPlan(vehicle, line, waypoints, out plan);
        }

        public bool BuildPlanRaw(
            Entity vehicle,
            DynamicBuffer<RouteWaypoint> waypoints,
            LineTrackChain chain,
            out TraversalSliceSamplingPlan plan)
        {
            return m_Capture.TryBuildTraversalSliceSamplingPlanUncached(vehicle, waypoints, chain, out plan);
        }

        public void FinishSlice(
            Entity vehicle,
            uint nowFrame,
            int exitAtomIndex,
            float exitAtomPosition01)
        {
            m_Capture.FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame, exitAtomIndex, exitAtomPosition01);
        }

        public void RecordSample(
            Entity vehicle,
            Entity line,
            LineTrackChain chain,
            int sliceIndex,
            VehicleTrackCursor cursor,
            uint nowFrame)
        {
            m_Capture.MaybeRecordTraversalPositionSample(vehicle, line, chain, sliceIndex, cursor, nowFrame);
        }

        public bool CurrentSlice(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            out LineTrackChain chain,
            out int sliceIndex,
            out VehicleTrackCursor cursor)
        {
            return m_Capture.TryGetCurrentTraversalRunSlice(vehicle, line, waypoints, out chain, out sliceIndex, out cursor);
        }

        public bool EffectiveFrames(Entity line, TraversalRunSlice slice, out float frames)
        {
            return m_Capture.TryGetEffectiveTraversalRunSliceFrames(line, slice, out frames);
        }

        public void DebugStart(Entity vehicle, TraversalRunSlice slice, int atomIndex, float atomPosition01)
        {
            m_Capture.RecordTraversalSliceLapDebugStart(vehicle, slice, atomIndex, atomPosition01);
        }

        public void DebugDrop(Entity vehicle, int sliceIndex)
        {
            m_Capture.RecordTraversalSliceLapDebugDropped(vehicle, sliceIndex);
        }

        public void DebugFinish(Entity vehicle, int sliceIndex, float observedFrames)
        {
            m_Capture.RecordTraversalSliceLapDebugFinalize(vehicle, sliceIndex, observedFrames);
        }

        public void ClearDebug(Entity vehicle)
        {
            m_Capture.ClearVehicleTraversalSliceLapDebug(vehicle);
        }

        public bool DwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            return m_Capture.TryStationDwellAnchor(line, waypointIndex, out anchor);
        }

        public string DwellKey(Entity line, string stationAnchorId)
        {
            return m_Capture.StationDwellKey(line, stationAnchorId);
        }
    }
}
