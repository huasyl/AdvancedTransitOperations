using Game.Objects;
using Game.Routes;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class ObservationPort
    {
        private readonly DispatchRuntimeSystem m_Runtime;
        private readonly Capture m_Capture;

        public ObservationPort(DispatchRuntimeSystem runtime, Capture capture)
        {
            m_Runtime = runtime;
            m_Capture = capture;
        }

        public void Record(Entity vehicle, string reason)
        {
            m_Capture.RecordLapStart(vehicle, reason);
        }

        public void Update(Entity vehicle)
        {
            m_Capture.UpdateLapStats(vehicle);
        }

        public void Finish(Entity vehicle, uint nowFrame, int exitAtomIndex, float exitAtomPosition01)
        {
            m_Capture.FinalizeVehicleTraversalSliceObservation(vehicle, nowFrame, exitAtomIndex, exitAtomPosition01);
        }

        public bool DwellAnchor(Entity line, int waypointIndex, out StationDwellAnchor anchor)
        {
            return m_Capture.TryStationDwellAnchor(line, waypointIndex, out anchor);
        }

        public string DwellKey(Entity line, string stationAnchorId)
        {
            return m_Capture.StationDwellKey(line, stationAnchorId);
        }

        public bool Dwell(
            Entity vehicle,
            Entity line,
            int currentWaypointIndex,
            bool boarding,
            uint nowFrame,
            int waypointCount,
            out uint dwellSinceFrame,
            out uint dwellDeadlineFrame,
            out int maxDwellMinutes)
        {
            dwellSinceFrame = 0;
            dwellDeadlineFrame = 0;
            maxDwellMinutes = m_Runtime.m_LineView.Dwell(line);
            if (!boarding || currentWaypointIndex <= 0 || currentWaypointIndex >= waypointCount)
            {
                if (m_Runtime.m_ObsPersist.RemoveDwellStart(vehicle))
                {
                    m_Runtime.ClearForcedMidStopClosingConsist(vehicle);
                    m_Runtime.log.Info("[StopDwellEnd] line" + line.Index
                        + " vehicle" + vehicle.Index
                        + " boarding=" + boarding
                        + " wp=" + currentWaypointIndex
                        + "/" + (waypointCount - 1)
                        + " nowFrame=" + nowFrame);
                }
                return false;
            }

            if (maxDwellMinutes <= 0)
                return false;

            if (!m_Runtime.m_ObsQuery.TryDwellStart(vehicle, out dwellSinceFrame))
            {
                dwellSinceFrame = nowFrame;
                m_Runtime.m_ObsPersist.SetDwellStart(vehicle, dwellSinceFrame);
                dwellDeadlineFrame = ComputeDeadline(line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
                m_Runtime.log.Info("[StopDwellBegin] line" + line.Index
                    + " vehicle" + vehicle.Index
                    + " wp=" + currentWaypointIndex
                    + "/" + (waypointCount - 1)
                    + " limit=" + maxDwellMinutes + "min"
                    + " deadlineFrame=" + dwellDeadlineFrame);
                return false;
            }

            dwellDeadlineFrame = ComputeDeadline(line, currentWaypointIndex, dwellSinceFrame, maxDwellMinutes);
            return nowFrame >= dwellDeadlineFrame;
        }

        public uint ComputeAdjustedStopDwellDeadlineFrame(
            Entity line,
            int waypointIndex,
            uint dwellSinceFrame,
            int maxDwellMinutes)
        {
            float configuredFrames = math.max(0f, maxDwellMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            float earlyCloseFrames = 0f;

            if (line != Entity.Null
                && waypointIndex >= 0
                && m_Runtime.TryGetObservedWaypointStopFrames(line, waypointIndex, out float observationFrames)
                && observationFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observationFrames - configuredFrames,
                    DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
        }

        public bool TryGetObservedWaypointStopFrames(Entity line, int waypointIndex, out float dwellFrames)
        {
            dwellFrames = 0f;
            if (line == Entity.Null || waypointIndex < 0)
                return false;

            if (!DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
                return false;

            return m_Capture.TryGetObservedWaypointStopFrames(line, waypointIndex, anchor.StationAnchorId, out dwellFrames);
        }

        public bool TryEstimateRemainingBoardingDwellFrames(
            Entity vehicle,
            Entity line,
            DynamicBuffer<RouteWaypoint> waypoints,
            int currentWaypointIndex,
            Entity currentBypassBuilding,
            uint nowFrame,
            out float remainingFrames)
        {
            remainingFrames = 0f;
            if (vehicle == Entity.Null
                || line == Entity.Null
                || currentWaypointIndex <= 0
                || currentWaypointIndex >= waypoints.Length
                || currentBypassBuilding == Entity.Null
                || m_Runtime.GetBypassBuildingForWaypoint(waypoints, currentWaypointIndex) != currentBypassBuilding
                || !m_Runtime.EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                return false;
            }

            if ((m_Runtime.EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle).m_State & PublicTransportFlags.Boarding) == 0)
                return false;

            if (!m_Runtime.m_ObsQuery.TryDwellStart(vehicle, out uint dwellSinceFrame) || nowFrame <= dwellSinceFrame)
                return false;

            float elapsedFrames = nowFrame - dwellSinceFrame;
            int maxStationDwellMinutes = m_Runtime.m_LineView.Dwell(line);
            float timeoutRemainingFrames = 0f;
            if (maxStationDwellMinutes > 0)
            {
                uint timeoutDeadlineFrame = ComputeAdjustedStopDwellDeadlineFrame(line, currentWaypointIndex, dwellSinceFrame, maxStationDwellMinutes);
                if (nowFrame < timeoutDeadlineFrame)
                    timeoutRemainingFrames = timeoutDeadlineFrame - nowFrame;
            }

            float estimatedRemainingFrames = 0f;
            if (TryGetObservedWaypointStopFrames(line, currentWaypointIndex, out float observedDwellFrames)
                && observedDwellFrames > 0f)
            {
                estimatedRemainingFrames = math.max(0f, observedDwellFrames - elapsedFrames);
            }

            if (!(estimatedRemainingFrames > 0f) && timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = timeoutRemainingFrames;

            if (timeoutRemainingFrames > 0f)
                estimatedRemainingFrames = math.min(estimatedRemainingFrames, timeoutRemainingFrames);

            if (!(estimatedRemainingFrames > 0f))
                return false;

            remainingFrames = estimatedRemainingFrames;
            return true;
        }

        public bool Head(Entity vehicle, int waypointIndex, out DispatchRuntimeSystem.TrainHeadSnapshot snapshot)
        {
            snapshot = default;
            if (vehicle == Entity.Null || !m_Runtime.EntityManager.Exists(vehicle))
                return false;

            Entity headVehicle = vehicle;
            if (m_Runtime.EntityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = m_Runtime.EntityManager.GetBuffer<LayoutElement>(vehicle, true);
                if (layout.Length > 0 && layout[0].m_Vehicle != Entity.Null)
                    headVehicle = layout[0].m_Vehicle;
            }

            if (headVehicle == Entity.Null
                || !m_Runtime.EntityManager.Exists(headVehicle)
                || !m_Runtime.EntityManager.HasComponent<TrainCurrentLane>(headVehicle))
            {
                return false;
            }

            TrainCurrentLane currentLane = m_Runtime.EntityManager.GetComponentData<TrainCurrentLane>(headVehicle);
            bool reversed = m_Runtime.EntityManager.HasComponent<Train>(headVehicle)
                && (m_Runtime.EntityManager.GetComponentData<Train>(headVehicle).m_Flags & TrainFlags.Reversed) != 0;

            snapshot = new DispatchRuntimeSystem.TrainHeadSnapshot(
                m_Runtime.m_SimulationSystem.frameIndex,
                headVehicle,
                currentLane.m_Front.m_Lane,
                currentLane.m_Rear.m_Lane,
                reversed,
                waypointIndex);
            return true;
        }

        public void BeginObservedDwellSession(Entity vehicle, Entity line, int waypointIndex, uint nowFrame)
        {
            m_Capture.BeginObservedDwellSession(vehicle, line, waypointIndex, nowFrame);
        }

        public void TryRecordObservedStopDwellOnBoardingEnd(Entity vehicle, Entity line, int fallbackWaypointIndex, uint nowFrame)
        {
            if (!m_Capture.TryRecordObservedStopDwellOnBoardingEnd(vehicle, line, fallbackWaypointIndex, nowFrame, out ObservedDwellSample sample))
                return;

            RecordStationDwellObservation(sample.Line, sample.WaypointIndex, sample.SampleFrames, sample.Frame, sample.SampleMinutes);
            m_Runtime.m_Bypass.ExpireLine(sample.Line);
        }

        public void Seed(Entity vehicle, Entity line, uint nowFrame)
        {
            if (line == Entity.Null)
            {
                m_Runtime.m_VehicleRegistry.ClearPreparing(vehicle);
                m_Runtime.m_VehicleRegistry.ClearDispatch(vehicle);
                return;
            }

            uint frames = 0;
            bool hasSample = false;
            if (m_Runtime.m_VehicleView.TryGetDispatch(vehicle, out uint dispatchRequestStart))
            {
                frames = nowFrame - dispatchRequestStart;
                hasSample = true;
            }
            else if (m_Runtime.m_VehicleView.TryGetPreparing(vehicle, out uint prepStart))
            {
                frames = nowFrame - prepStart;
                hasSample = true;
            }

            m_Runtime.m_VehicleRegistry.ClearPreparing(vehicle);
            m_Runtime.m_VehicleRegistry.ClearDispatch(vehicle);
            if (!hasSample || frames == 0)
                return;

            float sampleMinutes = frames / (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE;
            if (sampleMinutes < DispatchRuntimeSystem.DISPATCH_ESTIMATE_MIN_MINUTES
                || sampleMinutes > DispatchRuntimeSystem.DISPATCH_ESTIMATE_MAX_MINUTES)
            {
                m_Runtime.log.Info("[DispatchSample] line" + line.Index + " vehicle" + vehicle.Index
                    + " sample=" + sampleMinutes.ToString("F1") + "min out-of-range skip");
                return;
            }

            int nowMin = (int)(m_Runtime.m_TimeSystem.normalizedTime * 1440f) % 1440;
            m_Runtime.m_SelectPanel.RecordLineDispatchSampleSummary(line, nowMin, vehicle, sampleMinutes);
            m_Runtime.m_DispatchCache.Update(line, vehicle, frames);
        }

        private uint ComputeDeadline(Entity line, int waypointIndex, uint dwellSinceFrame, int maxDwellMinutes)
        {
            float configuredFrames = math.max(0f, maxDwellMinutes * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            float earlyCloseFrames = 0f;

            if (line != Entity.Null
                && waypointIndex >= 0
                && m_Runtime.TryGetObservedWaypointStopFrames(line, waypointIndex, out float observationFrames)
                && observationFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    observationFrames - configuredFrames,
                    DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            }

            if (!(earlyCloseFrames > 0f)
                && DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor)
                && m_Capture.TryGetObservedWaypointStopFrames(line, waypointIndex, anchor.StationAnchorId, out float anchoredFrames)
                && anchoredFrames > configuredFrames)
            {
                earlyCloseFrames = math.min(
                    anchoredFrames - configuredFrames,
                    DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES * (float)DispatchRuntimeSystem.SIM_FRAMES_PER_MINUTE);
            }

            float adjustedFrames = math.max(0f, configuredFrames - earlyCloseFrames);
            return dwellSinceFrame + (uint)math.round(adjustedFrames);
        }

        public void ClearStationAnchorObservationDiagnosticsState()
        {
            m_Runtime.m_LastStationStopDwellLegacyBufferCount = 0;
            m_Runtime.m_LastStationStopDwellLegacyRestoredCount = 0;
            m_Runtime.m_LastStationStopDwellAnchorBufferCount = 0;
            m_Runtime.m_LastStationStopDwellAnchorRestoredCount = 0;
            m_Runtime.m_StationAnchorObservationDiagLastLogFrame = 0;
            m_Runtime.m_StationAnchorDiagAcceptedSamples = 0;
            m_Runtime.m_StationAnchorDiagLegacyWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousLongDwell = 0;
            m_Runtime.m_StationAnchorDiagTotalAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell = 0;
        }

        private void RecordStationDwellObservation(
            Entity line,
            int waypointIndex,
            float sampleFrames,
            uint nowFrame,
            float sampleMinutes)
        {
            m_Runtime.m_StationAnchorDiagAcceptedSamples++;
            bool suspiciousOriginOrTerminal = IsSuspiciousOriginOrTerminalStationStopDwellSample(line, waypointIndex);
            if (suspiciousOriginOrTerminal)
            {
                m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal++;
                m_Runtime.m_StationAnchorDiagTotalSuspiciousOriginOrTerminal++;
            }
            if (sampleMinutes > DispatchRuntimeSystem.EARLY_STOP_DWELL_CLOSE_MAX_MINUTES)
            {
                m_Runtime.m_StationAnchorDiagSuspiciousLongDwell++;
                m_Runtime.m_StationAnchorDiagTotalSuspiciousLongDwell++;
            }

            if (!DwellAnchor(line, waypointIndex, out StationDwellAnchor anchor))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            if (suspiciousOriginOrTerminal)
            {
                m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal++;
                m_Runtime.m_StationAnchorDiagTotalAnchorRejectedOriginOrTerminal++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            string observationKey = DwellKey(line, anchor.StationAnchorId);
            if (string.IsNullOrWhiteSpace(observationKey))
            {
                m_Runtime.m_StationAnchorDiagAnchorMissing++;
                m_Runtime.m_StationAnchorDiagTotalAnchorMissing++;
                MaybeLogStationAnchorObservationDiagnostics(nowFrame);
                return;
            }

            m_Capture.RecordStationDwellObservation(observationKey, sampleFrames, nowFrame);
            m_Runtime.m_StationAnchorDiagAnchorWritten++;
            MaybeLogStationAnchorObservationDiagnostics(nowFrame);
        }

        private bool IsSuspiciousOriginOrTerminalStationStopDwellSample(Entity line, int waypointIndex)
        {
            if (line == Entity.Null || waypointIndex < 0 || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            return waypointIndex == 0;
        }

        private void MaybeLogStationAnchorObservationDiagnostics(uint nowFrame)
        {
            if (m_Runtime.m_StationAnchorObservationDiagLastLogFrame != 0
                && nowFrame - m_Runtime.m_StationAnchorObservationDiagLastLogFrame < DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES)
            {
                return;
            }

            if (m_Runtime.m_StationAnchorDiagAcceptedSamples == 0
                && m_Runtime.m_StationAnchorDiagLegacyWritten == 0
                && m_Runtime.m_StationAnchorDiagAnchorWritten == 0
                && m_Runtime.m_StationAnchorDiagAnchorMissing == 0)
            {
                m_Runtime.m_StationAnchorObservationDiagLastLogFrame = nowFrame;
                return;
            }

            StationAnchorObservationSummaryDto coverage = m_Runtime.m_StationAnchorDiagnostics.Build().summary;
            m_Runtime.log.Info("[StationAnchorDiag] intervalFrames=" + DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " lines=" + coverage.lineCount
                + " stopWaypoints=" + coverage.stopWaypointCount
                + " anchorResolved=" + coverage.anchorResolvedCount
                + " anchorMissing=" + coverage.anchorMissingCount
                + " uniqueAnchors=" + coverage.uniqueAnchorCount
                + " duplicateAnchorOccurrences=" + coverage.duplicateAnchorOccurrenceCount);

            m_Runtime.log.Info("[StopDwellAnchorDiag] intervalFrames=" + DispatchRuntimeSystem.STATION_ANCHOR_OBSERVATION_DIAG_INTERVAL_FRAMES
                + " accepted=" + m_Runtime.m_StationAnchorDiagAcceptedSamples
                + " legacyWritten=" + m_Runtime.m_StationAnchorDiagLegacyWritten
                + " anchorWritten=" + m_Runtime.m_StationAnchorDiagAnchorWritten
                + " anchorMissing=" + m_Runtime.m_StationAnchorDiagAnchorMissing
                + " anchorRejectedOriginOrTerminal=" + m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal
                + " uniqueAnchors=" + m_Runtime.m_ObsQuery.StationDwellCount
                + " suspiciousOriginOrTerminal=" + m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal
                + " suspiciousLongDwell=" + m_Runtime.m_StationAnchorDiagSuspiciousLongDwell);

            m_Runtime.m_StationAnchorObservationDiagLastLogFrame = nowFrame;
            m_Runtime.m_StationAnchorDiagAcceptedSamples = 0;
            m_Runtime.m_StationAnchorDiagLegacyWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorWritten = 0;
            m_Runtime.m_StationAnchorDiagAnchorMissing = 0;
            m_Runtime.m_StationAnchorDiagAnchorRejectedOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousOriginOrTerminal = 0;
            m_Runtime.m_StationAnchorDiagSuspiciousLongDwell = 0;
        }
    }
}
