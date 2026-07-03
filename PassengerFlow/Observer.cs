using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal static class Observer
    {
        internal static void Observe(
            Port port,
            State state,
            uint currentFrame,
            Entity vehicle,
            VehicleState vehicleState,
            Entity line,
            TransitMode mode,
            string lineId,
            int cachedWaypointIndex,
            bool acceptedBoarding,
            bool hasLaunchFrame,
            uint launchFrame)
        {
            if (state.OpenStops.TryGetValue(vehicle, out OpenStop openStop))
            {
                if (!acceptedBoarding && cachedWaypointIndex < 0 && openStop.Line == line)
                {
                    EnqueueDepartureSample(port, state, currentFrame, openStop);
                    state.OpenStops.Remove(vehicle);
                    return;
                }

                if (openStop.Line != line)
                    state.OpenStops.Remove(vehicle);
            }

            if (acceptedBoarding && cachedWaypointIndex >= 0)
            {
                if (!state.Anchors.TryForWaypoint(port, line, cachedWaypointIndex, out StationKey stationKey))
                {
                    state.Aggregates.RecordWarning(
                        mode,
                        Aggregates.WarningAnchorMissing,
                        lineId,
                        -1,
                        state.CurrentBucket,
                        currentFrame);
                    return;
                }

                if (!state.OpenStops.TryGetValue(vehicle, out OpenStop existing)
                    || existing.Line != line
                    || existing.OpenWaypointIndex != cachedWaypointIndex)
                {
                    state.OpenStops[vehicle] = new OpenStop(
                        vehicle,
                        mode,
                        lineId,
                        line,
                        cachedWaypointIndex,
                        stationKey.Index,
                        currentFrame,
                        0);
                }

                return;
            }

            if (hasLaunchFrame && launchFrame == currentFrame && !state.OpenStops.ContainsKey(vehicle))
            {
                if (!state.Anchors.TryForWaypoint(port, line, 0, out StationKey stationKey))
                {
                    state.Aggregates.RecordWarning(
                        mode,
                        Aggregates.WarningAnchorMissing,
                        lineId,
                        -1,
                        state.CurrentBucket,
                        currentFrame);
                    return;
                }

                OpenStop origin = new OpenStop(
                    vehicle,
                    mode,
                    lineId,
                    line,
                    0,
                    stationKey.Index,
                    currentFrame,
                    0);
                EnqueueDepartureSample(port, state, currentFrame, origin);
            }
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
