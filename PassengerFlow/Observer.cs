using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Observer
    {
        internal static void OpenStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame)
        {
            RecordOpenStop(port, state, vehicle, line, waypointIndex, frame);
        }

        internal static void RestoreStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame)
        {
            RecordOpenStop(port, state, vehicle, line, waypointIndex, frame);
        }

        internal static void ConfirmDeparture(Port port, State state, Entity vehicle, uint frame)
        {
            if (state == null || !state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
                return;

            EnqueueDepartureSample(port, state, frame, openStop);
            state.OpenStops.Remove(vehicle);
            state.LastProbeFrames.Remove(vehicle);
        }

        internal static void LaunchOrigin(Port port, State state, Entity vehicle, uint frame)
        {
            if (port == null
                || state == null
                || (state.LastLaunchFrames.TryGetValue(vehicle, out uint launchFrame) && launchFrame == frame))
            {
                return;
            }

            state.LastLaunchFrames[vehicle] = frame;
            if (state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
            {
                EnqueueDepartureSample(port, state, frame, openStop);
                state.OpenStops.Remove(vehicle);
                state.LastProbeFrames.Remove(vehicle);
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

            state.OpenStops.Remove(vehicle);
            state.LastProbeFrames.Remove(vehicle);
        }

        internal static void RemoveVehicle(Port port, State state, Entity vehicle)
        {
            if (state == null)
                return;

            Entity runtimeVehicle = port != null ? port.RuntimeVehicle(vehicle) : vehicle;
            state.OpenStops.Remove(vehicle);
            state.LastProbeFrames.Remove(vehicle);
            state.LastLaunchFrames.Remove(vehicle);
            state.Baselines.Remove(vehicle);
            if (runtimeVehicle != Entity.Null)
                state.Baselines.Remove(runtimeVehicle);

            if (state.PendingSamples.Count == 0)
                return;

            int pendingCount = state.PendingSamples.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                PendingSample sample = state.PendingSamples.Dequeue();
                if (sample.Vehicle != vehicle
                    && sample.RuntimeVehicle != vehicle
                    && sample.RuntimeVehicle != runtimeVehicle)
                {
                    state.PendingSamples.Enqueue(sample);
                }
                else
                {
                    state.Baselines.Remove(sample.RuntimeVehicle);
                }
            }
        }

        private static void RecordOpenStop(
            Port port,
            State state,
            Entity vehicle,
            Entity line,
            int waypointIndex,
            uint frame)
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
            state.LastProbeFrames.Remove(vehicle);
            if (TryCreateOpenStop(port, state, vehicle, line, waypointIndex, frame, out OpenStop openStop))
                state.OpenStops[vehicle] = openStop;
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
            if (port == null
                || !port.TryLineMetadata(line, out mode, out lineId)
                || (mode != TransitMode.Train && mode != TransitMode.Subway))
            {
                state.Aggregates.RecordWarning(
                    mode,
                    Aggregates.WarningUnsupportedMode,
                    lineId,
                    -1,
                    state.CurrentBucket,
                    frame);
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

            openStop = new OpenStop(
                vehicle,
                mode,
                lineId,
                line,
                waypointIndex,
                stationKey.Index,
                frame,
                0);
            return true;
        }

        private static void EnqueueDepartureSample(
            Port port,
            State state,
            uint currentFrame,
            OpenStop openStop)
        {
            int nextWaypointIndex = ResolveNextWaypoint(port, openStop.Line, openStop.OpenWaypointIndex);
            int nextStationSakIndex = -1;
            if (nextWaypointIndex < 0
                || !state.Anchors.TryForWaypoint(port, openStop.Line, nextWaypointIndex, out StationKey nextStation))
            {
                state.Aggregates.RecordWarning(
                    openStop.Mode,
                    Aggregates.WarningSectionAnchorMissing,
                    openStop.LineId,
                    openStop.OpenStationSakIndex,
                    state.CurrentBucket,
                    currentFrame);
            }
            else
            {
                nextStationSakIndex = nextStation.Index;
            }

            state.PendingSamples.Enqueue(new PendingSample(
                currentFrame + SamplingSystem.DepartureSampleDelayFrames,
                openStop.Mode,
                openStop.LineId,
                openStop.Line,
                openStop.Vehicle,
                port != null ? port.RuntimeVehicle(openStop.Vehicle) : openStop.Vehicle,
                openStop.OpenWaypointIndex,
                openStop.OpenStationSakIndex,
                nextWaypointIndex,
                nextStationSakIndex));
        }

        private static int ResolveNextWaypoint(Port port, Entity line, int openWaypointIndex)
        {
            if (port == null || line == Entity.Null || openWaypointIndex < 0 || !port.HasWaypoints(line))
                return -1;

            Unity.Entities.DynamicBuffer<Game.Routes.RouteWaypoint> waypoints = port.Waypoints(line);
            if (waypoints.Length <= 1 || openWaypointIndex >= waypoints.Length)
                return -1;

            int next = openWaypointIndex + 1;
            return next < waypoints.Length ? next : 0;
        }
    }
}
