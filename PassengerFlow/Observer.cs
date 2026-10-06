using Unity.Entities;
using RapidTransitMod.Core;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Observer
    {
        internal static void RegisterVehicle(Port port, State state, Entity vehicle,
            Entity line, VehicleState vehicleState, uint frame)
        {
            if (state == null || !port.IsReady
                || !port.TryLineMetadata(line, out TransitMode mode, out string lineId)
                || !SamplingSystem.SupportsMode(mode))
                return;
            bool qualified = vehicleState != VehicleState.Preparing
                && (vehicleState != VehicleState.Retiring || port.ServiceActive(vehicle) != false);
            state.RegisterVehicle(vehicle, line, qualified, mode, lineId, frame, port.Clock());
        }

        internal static void EndService(Port port, State state, Entity vehicle, Entity line, uint frame)
        {
            state?.EndVehicleService(vehicle, line, frame, port.Clock());
        }

        internal static void OpenStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame)
        {
            RecordOpenStop(port, state, vehicle, line, waypointIndex, frame, true);
        }

        internal static void RestoreStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame)
        {
            RecordOpenStop(port, state, vehicle, line, waypointIndex, frame, false);
        }

        internal static void ConfirmDeparture(Port port, State state, Entity vehicle, uint frame)
        {
            if (state == null || !state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
                return;

            EnqueueDepartureSample(port, state, frame, openStop);
            state.OpenStops.Remove(vehicle);
        }

        internal static void LaunchOrigin(Port port, State state, Entity vehicle, uint frame)
        {
            if (port == null
                || state == null
                || (state.LastLaunchFrames.TryGetValue(vehicle, out uint launchFrame) && launchFrame == frame))
            {
                return;
            }

            if (state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
            {
                EnqueueDepartureSample(port, state, frame, openStop);
                state.OpenStops.Remove(vehicle);
                return;
            }

            if (!port.TryLine(vehicle, out Entity line)
                || !TryCreateOpenStop(port, state, vehicle, line, 0, frame, out OpenStop origin))
            {
                return;
            }

            EnqueueDepartureSample(port, state, frame, origin);
        }

        internal static void CancelStop(State state, Entity vehicle)
        {
            if (state == null)
                return;

            if (state.OpenStops.TryGetValue(vehicle, out OpenStop stop))
                state.Trips.ReleaseAlightMarks(vehicle, stop.OpenFrame);
            state.OpenStops.Remove(vehicle);
        }

        internal static void RemoveVehicle(Port port, State state, Entity vehicle,
            bool preserveVehicleId = false, uint? factFrame = null)
        {
            if (state == null)
                return;

            Entity runtimeVehicle = port != null ? port.RuntimeVehicle(vehicle) : vehicle;
            state.RemoveVehicleLoad(vehicle, factFrame ?? port.Frame(), port.Clock());
            state.OpenStops.Remove(vehicle);
            state.LastLaunchFrames.Remove(vehicle);
            state.Baselines.Remove(vehicle);
            if (!preserveVehicleId)
                state.VehicleIds.Remove(vehicle);
            if (runtimeVehicle != Entity.Null)
            {
                state.OpenStops.Remove(runtimeVehicle);
                state.LastLaunchFrames.Remove(runtimeVehicle);
                state.Baselines.Remove(runtimeVehicle);
                if (!preserveVehicleId)
                    state.VehicleIds.Remove(runtimeVehicle);
            }

            state.Trips.RemoveVehicle(vehicle);
            if (runtimeVehicle != Entity.Null && runtimeVehicle != vehicle)
                state.Trips.RemoveVehicle(runtimeVehicle);

            if (state.PendingSamples.Count == 0)
                return;

            int pendingCount = state.PendingSamples.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                PendingSample sample = state.PendingSamples.Dequeue();
                if (sample.Vehicle != vehicle
                    && sample.Vehicle != runtimeVehicle
                    && sample.RuntimeVehicle != vehicle
                    && sample.RuntimeVehicle != runtimeVehicle)
                {
                    state.PendingSamples.Enqueue(sample);
                }
                else
                {
                    state.Baselines.Remove(sample.RuntimeVehicle);
                    state.Trips.ReleaseAlightMarks(sample.Vehicle, sample.OpenFrame);
                }
            }
        }

        private static void RecordOpenStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame,
            bool samplePlan)
        {
            if (state == null || waypointIndex < 0)
                return;

            if (state.OpenStops.TryGetValue(vehicle, out OpenStop existing)
                && existing.Line == line
                && existing.OpenWaypointIndex == waypointIndex)
            {
                return;
            }

            state.OpenStops.Remove(vehicle);
            if (TryCreateOpenStop(port, state, vehicle, line, waypointIndex, frame, out OpenStop openStop))
            {
                for (int i = 0; i < state.RestoredStops.Count; i++)
                {
                    PassengerFlowStopElement restored = state.RestoredStops[i];
                    if (restored.m_Vehicle == vehicle && restored.m_Line == line
                        && restored.m_WaypointIndex == waypointIndex)
                    {
                        openStop = new OpenStop(vehicle, openStop.Mode, openStop.LineId, line, waypointIndex,
                            openStop.OpenStationSakIndex, restored.m_OpenFrame, 0,
                            new StopPosition(restored.m_OpenStop, restored.m_OpenWaypoint, waypointIndex,
                                restored.m_StationOccurrence));
                        break;
                    }
                }
                state.OpenStops[vehicle] = openStop;
                if (samplePlan && openStop.OpenFrame == frame)
                    state.StopSamples.Enqueue(openStop);
            }
        }

        private static bool TryCreateOpenStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame,
            out OpenStop openStop)
        {
            openStop = default;
            TransitMode mode = TransitMode.Unknown;
            string lineId = string.Empty;
            bool hasMetadata = port != null && port.TryLineMetadata(line, out mode, out lineId);
            bool supportsMode = mode == TransitMode.Tram || SamplingSystem.SupportsMode(mode);
            if (!hasMetadata || !supportsMode)
            {
                if (!supportsMode)
                {
                    state.Aggregates.RecordWarning(
                        mode,
                        Aggregates.WarningUnsupportedMode,
                        lineId,
                        -1,
                        state.CurrentBucket,
                        frame);
                }
                return false;
            }

            if (!state.Anchors.TryForWaypoint(port, line, waypointIndex, out StationKey stationKey))
            {
                state.Aggregates.RecordWarning(
                    mode,
                    Aggregates.WarningAnchorMissing,
                    lineId,
                    -1,
                    state.CurrentBucket,
                    frame);
                return false;
            }

            state.Anchors.TryPosition(port, line, waypointIndex, out StopPosition position);
            openStop = new OpenStop(
                vehicle,
                mode,
                lineId,
                line,
                waypointIndex,
                stationKey.Index,
                frame,
                0, position);
            return true;
        }

        private static void EnqueueDepartureSample(
            Port port,
            State state,
            uint currentFrame,
            OpenStop openStop)
        {
            // 停站确认与始发事实共用一次离站消费，不为同帧的第二个入口重复采样。
            if (state.LastLaunchFrames.TryGetValue(openStop.Vehicle, out uint departureFrame)
                && departureFrame == currentFrame)
                return;
            state.LastLaunchFrames[openStop.Vehicle] = currentFrame;
            SectionSegment[] stopSections = Sections.PrepareStops(port, state, openStop);
            SectionSegment[] trackSections = state.Sections.PrepareTrack(port, state, openStop, currentFrame);

            ClockSnapshot clock = port.Clock();
            TimeBucketKey departureBucket = SamplingSystem.BucketFor(clock.DayIndex, clock.NowMinute);
            Entity runtimeVehicle = port.RuntimeVehicle(openStop.Vehicle);
            state.PendingSamples.Enqueue(new PendingSample(
                currentFrame + SamplingSystem.DepartureSampleDelayFrames,
                openStop.OpenFrame,
                currentFrame,
                departureBucket,
                state.GetVehicleId(runtimeVehicle != Entity.Null ? runtimeVehicle : openStop.Vehicle),
                openStop.Mode,
                openStop.LineId,
                openStop.Line,
                openStop.Vehicle,
                runtimeVehicle,
                openStop.OpenWaypointIndex,
                openStop.OpenStationSakIndex,
                openStop.Position, stopSections, trackSections));
        }

    }
}
