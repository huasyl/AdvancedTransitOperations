using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Recorder
    {
        private readonly Port m_Port;
        private TraceStore m_Store => m_Port.Store;

        internal Recorder(Port port)
        {
            m_Port = port ?? throw new ArgumentNullException(nameof(port));
        }

        internal string SnapshotJson()
        {
            EnsureSeeded();
            return m_Port.Json(BuildSnapshot());
        }

        internal void EnsureSeeded()
        {
            if (m_Store.Session != null)
                return;

            m_Port.LoadApplied();
            IReadOnlyDictionary<string, LinePlan> lines = m_Port.Lines();
            if (lines == null || lines.Count == 0)
                return;

            bool hasAppliedRows = lines.Values.Any(
                state => state != null && state.Rows != null && state.Rows.Count > 0);
            if (!hasAppliedRows)
                return;

            Seed(m_Port.Preferred());
        }

        internal void Seed(string selectedLineId)
        {
            DateTime appliedGameDate = GameDate();
            m_Store.Session = new Session
            {
                SnapshotId = "runtime-observation-" + m_Port.Frame().ToString(),
                Status = "active",
                AppliedFrame = m_Port.Frame(),
                UpdatedFrame = m_Port.Frame(),
                AppliedDate = appliedGameDate
            };
            m_Store.ClearIndexes();

            foreach (KeyValuePair<string, LinePlan> entry in m_Port.Lines())
            {
                LinePlan lineState = entry.Value;
                if (lineState == null || lineState.Line == Entity.Null || lineState.Rows == null)
                    continue;

                string lineId = entry.Key;
                foreach (RowPlan row in lineState.Rows)
                {
                    if (row == null)
                        continue;

                    int targetMinute = m_Port.Parse(row.Time);
                    if (targetMinute < 0)
                        continue;

                    string rowLineId = !string.IsNullOrEmpty(row.LineId) ? row.LineId : lineId;
                    CreateTrip(
                        lineState.Line,
                        rowLineId,
                        row.Id ?? string.Empty,
                        row.Source ?? string.Empty,
                        row.Kind ?? string.Empty,
                        targetMinute,
                        1,
                        m_Store.Session.AppliedFrame,
                        appliedGameDate);
                }
            }

            if (m_Store.Session.Trips.Count == 0)
            {
                m_Store.Session.Status = "empty";
            }

            m_Port.Log("[Observation] seeded snapshot=" + m_Store.Session.SnapshotId
                + " selectedLine=" + (selectedLineId ?? string.Empty)
                + " trips=" + m_Store.Session.Trips.Count);
        }

        private DateTime GameDate()
        {
            return m_Port.Date().Date;
        }

        private int DayIndex(DateTime serviceDate)
        {
            if (m_Store.Session == null)
                return -1;

            DateTime appliedDate = m_Store.Session.AppliedDate.Date;
            if (serviceDate == DateTime.MinValue.Date || appliedDate == DateTime.MinValue.Date)
                return -1;

            return (serviceDate - appliedDate).Days;
        }

        private static string ServiceDate(DateTime serviceDate)
        {
            return serviceDate == DateTime.MinValue.Date
                ? string.Empty
                : serviceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string BaseKey(string lineId, int targetMinute, string rowId)
        {
            return (lineId ?? string.Empty)
                + "|"
                + targetMinute.ToString(CultureInfo.InvariantCulture)
                + "|"
                + (rowId ?? string.Empty);
        }

        private Trip CreateTrip(
            Entity line,
            string lineId,
            string rowId,
            string source,
            string serviceKind,
            int targetMinute,
            int occurrenceIndex,
            uint nowFrame,
            DateTime serviceDate)
        {
            if (m_Store.Session == null)
                return null;

            Trip trip = new Trip
            {
                Id = "slot|"
                    + (lineId ?? string.Empty)
                    + "|"
                    + targetMinute.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + (rowId ?? string.Empty)
                    + "|occ:"
                    + occurrenceIndex.ToString(CultureInfo.InvariantCulture),
                BaseKey = BaseKey(lineId, targetMinute, rowId),
                LineId = lineId ?? string.Empty,
                RowId = rowId ?? string.Empty,
                Source = source ?? string.Empty,
                ServiceKind = serviceKind ?? string.Empty,
                TargetMin = targetMinute,
                Line = line,
                ServiceDate = ServiceDate(serviceDate),
                ServiceDayIndex = DayIndex(serviceDate),
                OccurrenceIndex = Math.Max(1, occurrenceIndex),
                UpdatedFrame = nowFrame
            };
            m_Store.Session.Trips[trip.Id] = trip;
            AddTripSlotIndex(trip);
            return trip;
        }

        private static bool HasTripCompletedCycle(Trip trip)
        {
            return Dispatch.Observation.Trips.Done(trip);
        }

        private Trip CreateNextTripOccurrence(Trip previousTrip, uint nowFrame)
        {
            if (previousTrip == null)
                return null;

            return CreateTrip(
                previousTrip.Line,
                previousTrip.LineId,
                previousTrip.RowId,
                previousTrip.Source,
                previousTrip.ServiceKind,
                previousTrip.TargetMin,
                previousTrip.OccurrenceIndex + 1,
                nowFrame,
                GameDate());
        }

        private Trip[] GetTripActiveOccurrences(Entity line, int targetMinute, uint nowFrame)
        {
            if (line == Entity.Null
                || targetMinute < 0
                || !m_Store.BySlot.TryGetValue(SlotKey(line, targetMinute), out List<Trip> trips)
                || trips == null
                || trips.Count == 0)
            {
                return Array.Empty<Trip>();
            }

            List<Trip> activeTrips = new List<Trip>();
            foreach (IGrouping<string, Trip> group in trips
                .Where(trip => trip != null)
                .GroupBy(trip => trip.BaseKey, StringComparer.Ordinal))
            {
                Trip latestTrip = group
                    .OrderByDescending(trip => trip.OccurrenceIndex)
                    .ThenByDescending(trip => trip.UpdatedFrame)
                    .FirstOrDefault();
                if (latestTrip == null)
                    continue;

                if (HasTripCompletedCycle(latestTrip))
                {
                    latestTrip = CreateNextTripOccurrence(latestTrip, nowFrame);
                }

                if (latestTrip != null)
                {
                    activeTrips.Add(latestTrip);
                }
            }

            return activeTrips.ToArray();
        }

        private void AddTripSlotIndex(Trip trip)
        {
            if (trip == null || trip.Line == Entity.Null || trip.TargetMin < 0)
                return;

            string key = SlotKey(trip.Line, trip.TargetMin);
            if (!m_Store.BySlot.TryGetValue(key, out List<Trip> trips))
            {
                trips = new List<Trip>();
                m_Store.BySlot[key] = trips;
            }
            trips.Add(trip);
        }

        private void AddTripVehicleIndex(Entity vehicle, Trip trip)
        {
            if (vehicle == Entity.Null || trip == null)
                return;

            if (!m_Store.ByVehicle.TryGetValue(vehicle, out List<Trip> trips))
            {
                trips = new List<Trip>();
                m_Store.ByVehicle[vehicle] = trips;
            }
            if (!trips.Contains(trip))
            {
                trips.Add(trip);
            }
        }

        internal void TargetBound(Entity line, Entity vehicle, int targetMinute, uint nowFrame, string reasonCode)
        {
            if (line == Entity.Null || vehicle == Entity.Null || targetMinute < 0 || m_Store.Session == null)
                return;

            Trip[] trips = GetTripActiveOccurrences(line, targetMinute, nowFrame);
            if (trips.Length == 0)
                return;

            foreach (Trip trip in trips)
            {
                trip.State = trip.State == "pending" ? "bound" : trip.State;
                trip.Vehicle = vehicle;
                DateTime serviceDate = GameDate();
                trip.ServiceDate = ServiceDate(serviceDate);
                trip.ServiceDayIndex = DayIndex(serviceDate);
                trip.ReasonCode = reasonCode ?? string.Empty;
                trip.BindingConfidence = "target-bound";
                trip.UpdatedFrame = nowFrame;
                AddTripVehicleIndex(vehicle, trip);
            }
            m_Store.Session.UpdatedFrame = nowFrame;
        }

        internal void Launch(Entity line, Entity vehicle, int targetMinute, int actualMinute, uint launchFrame, bool lateDispatch)
        {
            if (line == Entity.Null || vehicle == Entity.Null || targetMinute < 0 || m_Store.Session == null)
                return;

            Trip[] trips = GetTripActiveOccurrences(line, targetMinute, launchFrame);
            if (trips.Length == 0)
                return;

            foreach (Trip trip in trips)
            {
                trip.State = "departed";
                trip.Vehicle = vehicle;
                DateTime serviceDate = GameDate();
                trip.ServiceDate = ServiceDate(serviceDate);
                trip.ServiceDayIndex = DayIndex(serviceDate);
                trip.ActualMin = actualMinute;
                trip.LaunchFrame = launchFrame;
                trip.ReasonCode = lateDispatch ? "late-dispatch-launch" : "origin-launch";
                trip.BindingConfidence = "vehicle-launch";
                trip.UpdatedFrame = launchFrame;
                AddTripVehicleIndex(vehicle, trip);
            }
            m_Store.Session.UpdatedFrame = launchFrame;
        }

        internal void Stop(
            Entity vehicle,
            Entity line,
            Entity station,
            ResolvedStopKind kind,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            string clockTime,
            uint frame)
        {
            if (vehicle == Entity.Null || line == Entity.Null || station == Entity.Null || m_Store.Session == null)
                return;

            Trip observedTrip = ResolveTrip(
                vehicle,
                TryGetObservedVehicleTargetMin(vehicle),
                line);
            if (observedTrip == null)
                return;

            StopEvent stopEvent = new StopEvent
            {
                EventId = "stop|" + vehicle.Index + "|" + station.Index + "|" + frame,
                EventType = arrival ? "arrival" : "departure",
                TripId = observedTrip.Id,
                RowId = observedTrip.RowId,
                LineId = observedTrip.LineId,
                ServiceDate = observedTrip.ServiceDate,
                ServiceDayIndex = observedTrip.ServiceDayIndex,
                OccurrenceIndex = observedTrip.OccurrenceIndex,
                Line = line,
                Vehicle = vehicle,
                TargetMin = observedTrip.TargetMin,
                Station = station,
                Kind = kind,
                WaypointIndex = waypointIndex,
                IsOrigin = isOrigin,
                ArrivalTime = arrival ? clockTime : string.Empty,
                DepartureTime = arrival ? string.Empty : clockTime,
                ArrivalFrame = arrival ? frame : 0,
                DepartureFrame = arrival ? 0 : frame,
                UpdatedFrame = frame
            };
            m_Store.Session.Stops.Add(stopEvent);
            Dispatch.Observation.Trips.Trim(m_Store.Session.Stops, 256);
            m_Store.Session.UpdatedFrame = frame;
        }

        internal void Hold(
            Entity vehicle,
            Entity blocker,
            Entity holdStation,
            int waypointIndex,
            uint nowFrame,
            string reasonCode)
        {
            if (vehicle == Entity.Null || m_Store.Session == null)
                return;

            Trip localTrip = ResolveTrip(
                vehicle,
                TryGetObservedVehicleTargetMin(vehicle),
                m_Port.LineOf(vehicle));
            Trip priorityTrip = ResolveTrip(
                blocker,
                TryGetObservedVehicleTargetMin(blocker),
                m_Port.LineOf(blocker));

            if (m_Store.ActiveBypass.TryGetValue(vehicle, out BypassEvent activeEvent)
                && activeEvent.PriorityVehicle == blocker
                && activeEvent.State == "holding")
            {
                if (activeEvent.HoldStation == Entity.Null && holdStation != Entity.Null)
                    activeEvent.HoldStation = holdStation;
                if (activeEvent.WaypointIndex < 0 && waypointIndex >= 0)
                    activeEvent.WaypointIndex = waypointIndex;
                if (priorityTrip != null)
                {
                    activeEvent.PriorityTripId = priorityTrip.Id;
                    activeEvent.PriorityRowId = priorityTrip.RowId;
                    activeEvent.PriorityServiceDate = priorityTrip.ServiceDate;
                    activeEvent.PriorityServiceDayIndex = priorityTrip.ServiceDayIndex;
                    activeEvent.PriorityOccurrenceIndex = priorityTrip.OccurrenceIndex;
                }
                activeEvent.UpdatedFrame = nowFrame;
                m_Store.Session.UpdatedFrame = nowFrame;
                return;
            }
            if (activeEvent != null && activeEvent.State == "holding")
            {
                activeEvent.State = "released";
                activeEvent.HoldReleaseFrame = nowFrame;
                activeEvent.ReleaseReason = "superseded-by-new-bypass-hold";
                activeEvent.UpdatedFrame = nowFrame;
            }

            Entity localLine = m_Port.LineOf(vehicle);
            Entity priorityLine = m_Port.LineOf(blocker);
            BypassEvent bypassEvent = new BypassEvent
            {
                EventId = "bypass|" + vehicle.Index + "|" + nowFrame,
                State = "holding",
                LocalTripId = localTrip?.Id ?? string.Empty,
                LocalRowId = localTrip?.RowId ?? string.Empty,
                LocalServiceDate = localTrip?.ServiceDate ?? string.Empty,
                LocalServiceDayIndex = localTrip?.ServiceDayIndex ?? -1,
                LocalOccurrenceIndex = localTrip?.OccurrenceIndex ?? 1,
                PriorityTripId = priorityTrip?.Id ?? string.Empty,
                PriorityRowId = priorityTrip?.RowId ?? string.Empty,
                PriorityServiceDate = priorityTrip?.ServiceDate ?? string.Empty,
                PriorityServiceDayIndex = priorityTrip?.ServiceDayIndex ?? -1,
                PriorityOccurrenceIndex = priorityTrip?.OccurrenceIndex ?? 1,
                LocalLine = localLine,
                PriorityLine = priorityLine,
                LocalVehicle = vehicle,
                PriorityVehicle = blocker,
                LocalTargetMin = TryGetObservedVehicleTargetMin(vehicle),
                PriorityTargetMin = TryGetObservedVehicleTargetMin(blocker),
                HoldStation = holdStation,
                WaypointIndex = waypointIndex,
                HoldStartFrame = nowFrame,
                DecisionReason = reasonCode ?? "bypass-hold",
                UpdatedFrame = nowFrame
            };
            m_Store.ActiveBypass[vehicle] = bypassEvent;
            m_Store.Session.Bypass.Add(bypassEvent);
            Dispatch.Observation.Trips.Trim(m_Store.Session.Bypass, 128);
            m_Store.Session.UpdatedFrame = nowFrame;

            if (m_Store.ByVehicle.TryGetValue(vehicle, out List<Trip> trips))
            {
                foreach (Trip trip in trips)
                {
                    trip.State = "holding";
                    trip.UpdatedFrame = nowFrame;
                }
            }
        }

        internal void Release(Entity vehicle, Entity blocker, uint nowFrame, string releaseReason)
        {
            if (vehicle == Entity.Null || m_Store.Session == null)
                return;

            if (!m_Store.ActiveBypass.TryGetValue(vehicle, out BypassEvent bypassEvent))
                return;

            bypassEvent.State = "released";
            bypassEvent.PriorityVehicle = blocker;
            bypassEvent.PriorityLine = m_Port.LineOf(blocker);
            bypassEvent.PriorityTargetMin = TryGetObservedVehicleTargetMin(blocker);
            Trip priorityTrip = ResolveTrip(
                blocker,
                bypassEvent.PriorityTargetMin,
                bypassEvent.PriorityLine);
            if (priorityTrip != null)
            {
                bypassEvent.PriorityTripId = priorityTrip.Id;
                bypassEvent.PriorityRowId = priorityTrip.RowId;
                bypassEvent.PriorityServiceDate = priorityTrip.ServiceDate;
                bypassEvent.PriorityServiceDayIndex = priorityTrip.ServiceDayIndex;
                bypassEvent.PriorityOccurrenceIndex = priorityTrip.OccurrenceIndex;
            }
            bypassEvent.HoldReleaseFrame = nowFrame;
            bypassEvent.ReleaseReason = releaseReason ?? string.Empty;
            bypassEvent.UpdatedFrame = nowFrame;
            m_Store.ActiveBypass.Remove(vehicle);
            m_Store.Session.UpdatedFrame = nowFrame;

            if (m_Store.ByVehicle.TryGetValue(vehicle, out List<Trip> trips))
            {
                foreach (Trip trip in trips)
                {
                    trip.State = trip.LaunchFrame > 0 ? "departed" : "bound";
                    trip.UpdatedFrame = nowFrame;
                }
            }
        }

        private SnapshotDto BuildSnapshot()
        {
            BaselineDto[] baselineRows = BuildBaselineRows();
            ContractDto[] plannerContracts = BuildPlannerContracts();
            Session session = m_Store.Session;
            if (session == null)
            {
                TripDto[] emptyTrips = Array.Empty<TripDto>();
                StopDto[] emptyStops = Array.Empty<StopDto>();
                BypassDto[] emptyBypassEvents = Array.Empty<BypassDto>();
                CorridorDto[] emptyCorridors = Array.Empty<CorridorDto>();
                return new SnapshotDto
                {
                    schemaVersion = 2,
                    snapshotId = string.Empty,
                    status = "empty",
                    generatedAtFrame = m_Port.Frame(),
                    appliedTrips = emptyTrips,
                    stopEvents = emptyStops,
                    bypassEvents = emptyBypassEvents,
                    corridorPassages = emptyCorridors,
                    baselineRows = baselineRows,
                    plannerContracts = plannerContracts,
                    attainmentReport = BuildReport(
                        baselineRows,
                        plannerContracts,
                        emptyTrips,
                        emptyBypassEvents)
                };
            }

            TripDto[] appliedTrips = session.Trips.Values
                .OrderBy(trip => trip.LineId, StringComparer.Ordinal)
                .ThenBy(trip => trip.TargetMin)
                .ThenBy(trip => trip.RowId, StringComparer.Ordinal)
                .Select(BuildTripDto)
                .ToArray();
            StopDto[] stopEvents = session.Stops.Select(BuildStopDto).ToArray();
            BypassDto[] bypassEvents = session.Bypass.Select(BuildBypassDto).ToArray();
            CorridorDto[] corridorPassages = session.Corridors.ToArray();

            return new SnapshotDto
            {
                schemaVersion = 2,
                snapshotId = session.SnapshotId,
                status = session.Status,
                generatedAtFrame = m_Port.Frame(),
                appliedAtFrame = session.AppliedFrame,
                lastUpdatedFrame = session.UpdatedFrame,
                appliedTrips = appliedTrips,
                stopEvents = stopEvents,
                bypassEvents = bypassEvents,
                corridorPassages = corridorPassages,
                baselineRows = baselineRows,
                plannerContracts = plannerContracts,
                attainmentReport = BuildReport(
                    baselineRows,
                    plannerContracts,
                    appliedTrips,
                    bypassEvents)
            };
        }

        private TripDto BuildTripDto(Trip trip)
        {
            int deltaMinutes = trip.ActualMin >= 0 && trip.TargetMin >= 0
                ? NormalizeMinuteDelta(trip.ActualMin - trip.TargetMin)
                : 0;
            return new TripDto
            {
                tripObservationId = trip.Id,
                state = trip.State,
                lineId = trip.LineId,
                rowId = trip.RowId,
                source = trip.Source,
                serviceKind = trip.ServiceKind,
                plannedTime = trip.TargetMin >= 0 ? m_Port.Slot(trip.TargetMin) : string.Empty,
                serviceDate = trip.ServiceDate,
                serviceDayIndex = trip.ServiceDayIndex,
                occurrenceIndex = trip.OccurrenceIndex,
                actualDepartureTime = trip.ActualMin >= 0 ? m_Port.Slot(trip.ActualMin) : string.Empty,
                targetMinute = trip.TargetMin,
                actualDepartureMinute = trip.ActualMin,
                deltaMinutes = deltaMinutes,
                vehicleIndex = trip.Vehicle != Entity.Null ? trip.Vehicle.Index : -1,
                launchFrame = trip.LaunchFrame,
                bindingConfidence = trip.BindingConfidence,
                reasonCode = trip.ReasonCode,
                lastUpdatedFrame = trip.UpdatedFrame
            };
        }

        private StopDto BuildStopDto(StopEvent stopEvent)
        {
            return new StopDto
            {
                eventId = stopEvent.EventId,
                eventType = stopEvent.EventType,
                tripObservationId = stopEvent.TripId,
                rowId = stopEvent.RowId,
                lineId = !string.IsNullOrEmpty(stopEvent.LineId)
                    ? stopEvent.LineId
                    : (stopEvent.Line != Entity.Null ? m_Port.LineId(stopEvent.Line) : string.Empty),
                serviceDate = stopEvent.ServiceDate,
                serviceDayIndex = stopEvent.ServiceDayIndex,
                occurrenceIndex = stopEvent.OccurrenceIndex,
                vehicleIndex = stopEvent.Vehicle != Entity.Null ? stopEvent.Vehicle.Index : -1,
                targetMinute = stopEvent.TargetMin,
                stationId = m_Port.StopId(stopEvent.Station, stopEvent.Kind),
                plannerStationId = StationId(stopEvent.Line, stopEvent.WaypointIndex),
                stationName = m_Port.StopName(stopEvent.Station, stopEvent.Kind),
                waypointIndex = stopEvent.WaypointIndex,
                isOrigin = stopEvent.IsOrigin,
                arrivalTime = stopEvent.ArrivalTime,
                departureTime = stopEvent.DepartureTime,
                arrivalFrame = stopEvent.ArrivalFrame,
                departureFrame = stopEvent.DepartureFrame,
                dwellMinutes = DwellMinutes(stopEvent),
                lastUpdatedFrame = stopEvent.UpdatedFrame
            };
        }

        private BypassDto BuildBypassDto(BypassEvent bypassEvent)
        {
            return new BypassDto
            {
                eventId = bypassEvent.EventId,
                state = bypassEvent.State,
                localTripObservationId = bypassEvent.LocalTripId,
                localRowId = bypassEvent.LocalRowId,
                localServiceDate = bypassEvent.LocalServiceDate,
                localServiceDayIndex = bypassEvent.LocalServiceDayIndex,
                localOccurrenceIndex = bypassEvent.LocalOccurrenceIndex,
                priorityTripObservationId = bypassEvent.PriorityTripId,
                priorityRowId = bypassEvent.PriorityRowId,
                priorityServiceDate = bypassEvent.PriorityServiceDate,
                priorityServiceDayIndex = bypassEvent.PriorityServiceDayIndex,
                priorityOccurrenceIndex = bypassEvent.PriorityOccurrenceIndex,
                localLineId = bypassEvent.LocalLine != Entity.Null ? m_Port.LineId(bypassEvent.LocalLine) : string.Empty,
                priorityLineId = bypassEvent.PriorityLine != Entity.Null ? m_Port.LineId(bypassEvent.PriorityLine) : string.Empty,
                localVehicleIndex = bypassEvent.LocalVehicle != Entity.Null ? bypassEvent.LocalVehicle.Index : -1,
                priorityVehicleIndex = bypassEvent.PriorityVehicle != Entity.Null ? bypassEvent.PriorityVehicle.Index : -1,
                localTargetMinute = bypassEvent.LocalTargetMin,
                priorityTargetMinute = bypassEvent.PriorityTargetMin,
                holdStationId = m_Port.OriginId(bypassEvent.HoldStation),
                holdPlannerStationId = StationId(bypassEvent.LocalLine, bypassEvent.WaypointIndex),
                holdStationName = m_Port.StationName(bypassEvent.HoldStation),
                waypointIndex = bypassEvent.WaypointIndex,
                holdStartFrame = bypassEvent.HoldStartFrame,
                holdReleaseFrame = bypassEvent.HoldReleaseFrame,
                actualHoldMinutes = HoldMinutes(bypassEvent),
                decisionReason = bypassEvent.DecisionReason,
                releaseReason = bypassEvent.ReleaseReason,
                sceneKey = bypassEvent.SceneKey,
                protectedIntervalIndex = bypassEvent.ProtectedIntervalIndex,
                lastUpdatedFrame = bypassEvent.UpdatedFrame
            };
        }

        private BaselineDto[] BuildBaselineRows()
        {
            m_Port.LoadApplied();
            List<BaselineDto> rows = new List<BaselineDto>();
            foreach (KeyValuePair<string, LinePlan> entry in m_Port.Lines().OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                LinePlan state = entry.Value;
                if (state == null || state.Line == Entity.Null || state.Rows == null)
                    continue;

                (string originStationId, string originStationName) = m_Port.Origin(state.Line);
                string lineId = m_Port.LineId(state.Line);
                foreach (RowPlan row in state.Rows)
                {
                    if (row == null)
                        continue;

                    int plannedMinute = m_Port.Parse(row.Time);
                    rows.Add(new BaselineDto
                    {
                        draftKey = entry.Key,
                        rowId = row.Id ?? string.Empty,
                        lineId = !string.IsNullOrEmpty(row.LineId) ? row.LineId : lineId,
                        plannedTime = row.Time ?? string.Empty,
                        plannedMinute = plannedMinute,
                        serviceKind = row.Kind ?? string.Empty,
                        source = row.Source ?? string.Empty,
                        originStationId = originStationId ?? string.Empty,
                        originStationName = originStationName ?? string.Empty
                    });
                }
            }

            return rows
                .OrderBy(row => row.lineId, StringComparer.Ordinal)
                .ThenBy(row => row.plannedMinute)
                .ThenBy(row => row.rowId, StringComparer.Ordinal)
                .ToArray();
        }

        private ContractDto[] BuildPlannerContracts()
        {
            return (m_Port.Contracts() ?? Array.Empty<ContractDto>())
                .OrderBy(contract => contract?.draftKey ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
        }

        private ReportDto BuildReport(
            BaselineDto[] baselineRows,
            ContractDto[] plannerContracts,
            TripDto[] appliedTrips,
            BypassDto[] bypassEvents)
        {
            baselineRows ??= Array.Empty<BaselineDto>();
            plannerContracts ??= Array.Empty<ContractDto>();
            appliedTrips ??= Array.Empty<TripDto>();
            bypassEvents ??= Array.Empty<BypassDto>();

            Dictionary<string, TripDto> latestTripsByRowId = appliedTrips
                .Where(trip => !string.IsNullOrEmpty(trip?.rowId))
                .GroupBy(trip => trip.rowId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.occurrenceIndex)
                        .ThenByDescending(item => item.lastUpdatedFrame)
                        .First(),
                    StringComparer.Ordinal);
            Dictionary<string, TripDto> latestTripsBySemanticKey = appliedTrips
                .Where(trip => trip != null)
                .GroupBy(trip => TripKey(trip.lineId, trip.serviceKind, trip.targetMinute), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.occurrenceIndex)
                        .ThenByDescending(item => item.lastUpdatedFrame)
                        .First(),
                    StringComparer.Ordinal);

            Dictionary<string, (string PlanId, string ContractTripId)> contractTripByRowId =
                new Dictionary<string, (string PlanId, string ContractTripId)>(StringComparer.Ordinal);
            foreach (ContractDto contract in plannerContracts)
            {
                foreach (ChangeDto row in contract.changedRows ?? Array.Empty<ChangeDto>())
                {
                    if (row == null || string.IsNullOrEmpty(row.tripId))
                        continue;

                    contractTripByRowId[ImportedRowId(row.tripId)] =
                        (contract.importedPlanId ?? string.Empty, row.tripId);
                }
            }

            List<TripResultDto> tripResults = new List<TripResultDto>(baselineRows.Length);
            foreach (BaselineDto baselineRow in baselineRows)
            {
                TripDto observedTrip = null;
                string matchMode = string.Empty;
                string contractPlanId = string.Empty;
                string contractTripId = string.Empty;
                if (!string.IsNullOrEmpty(baselineRow.rowId)
                    && latestTripsByRowId.TryGetValue(baselineRow.rowId, out TripDto rowMatchedTrip))
                {
                    observedTrip = rowMatchedTrip;
                    matchMode = "rowId-latest-occurrence";
                }
                else if (latestTripsBySemanticKey.TryGetValue(
                    TripKey(
                        baselineRow.lineId,
                        baselineRow.serviceKind,
                        baselineRow.plannedMinute),
                    out TripDto semanticMatchedTrip))
                {
                    observedTrip = semanticMatchedTrip;
                    matchMode = "line-kind-time-latest-occurrence";
                }
                if (!string.IsNullOrEmpty(baselineRow.rowId)
                    && contractTripByRowId.TryGetValue(baselineRow.rowId, out var contractRef))
                {
                    contractPlanId = contractRef.PlanId;
                    contractTripId = contractRef.ContractTripId;
                }

                tripResults.Add(new TripResultDto
                {
                    draftKey = baselineRow.draftKey,
                    rowId = baselineRow.rowId,
                    lineId = baselineRow.lineId,
                    plannedTime = baselineRow.plannedTime,
                    plannedMinute = baselineRow.plannedMinute,
                    serviceDate = observedTrip?.serviceDate ?? string.Empty,
                    serviceDayIndex = observedTrip?.serviceDayIndex ?? -1,
                    occurrenceIndex = observedTrip?.occurrenceIndex ?? 0,
                    actualDepartureTime = observedTrip?.actualDepartureTime ?? string.Empty,
                    actualDepartureMinute = observedTrip?.actualDepartureMinute ?? -1,
                    deltaMinutes = observedTrip?.deltaMinutes ?? 0,
                    serviceKind = baselineRow.serviceKind,
                    source = baselineRow.source,
                    state = observedTrip?.state ?? "missing",
                    bindingConfidence = observedTrip?.bindingConfidence ?? string.Empty,
                    reasonCode = observedTrip?.reasonCode ?? "no-runtime-trip",
                    matchMode = matchMode,
                    contractPlanId = contractPlanId,
                    contractTripId = contractTripId
                });
            }

            Dictionary<string, BaselineDto> baselineRowsById = baselineRows
                .Where(row => !string.IsNullOrEmpty(row?.rowId))
                .ToDictionary(row => row.rowId, row => row, StringComparer.Ordinal);
            Dictionary<string, BaselineDto> baselineRowsByAppliedSemanticKey = baselineRows
                .Where(row => row != null)
                .GroupBy(
                    row => TripKey(row.lineId, row.serviceKind, row.plannedMinute),
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => !string.IsNullOrEmpty(item.rowId)).First(),
                    StringComparer.Ordinal);
            List<ActionResultDto> actionResults = new List<ActionResultDto>();
            foreach (ContractDto contract in plannerContracts)
            {
                ActionDto[] actions = contract.structuredActions ?? Array.Empty<ActionDto>();
                for (int i = 0; i < actions.Length; i++)
                {
                    ActionDto action = actions[i];
                    string[] tripRowIds = (action?.affectedTripIds ?? action?.tripIds ?? Array.Empty<string>())
                        .Select(ImportedRowId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    string[] lineIds = (action?.affectedLineIds ?? Array.Empty<string>())
                        .Concat(!string.IsNullOrEmpty(action?.affectedLineId) ? new[] { action.affectedLineId } : Array.Empty<string>())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    string[] stationIds = (action?.stationIds ?? Array.Empty<string>())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    float expectedMinutes = ComputeExpectedActionMinutes(action);
                    float actualMinutes = 0f;
                    string status;
                    string reason;
                    string actionType = action?.actionType ?? action?.type ?? string.Empty;

                    switch (actionType)
                    {
                        case "predictedHold":
                        {
                            BypassDto[] matchedBypassEvents = bypassEvents
                                .Where(item =>
                                    tripRowIds.Contains(item.localRowId)
                                    && (stationIds.Length == 0 || stationIds.Contains(item.holdPlannerStationId)))
                                .GroupBy(item => item.localTripObservationId ?? string.Empty, StringComparer.Ordinal)
                                .Select(group => group
                                    .OrderByDescending(item => item.localOccurrenceIndex)
                                    .ThenByDescending(item => item.lastUpdatedFrame)
                                    .First())
                                .ToArray();
                            actualMinutes = matchedBypassEvents.Length > 0
                                ? matchedBypassEvents.Max(item => item.actualHoldMinutes)
                                : 0f;
                            if (matchedBypassEvents.Length == 0)
                            {
                                status = "unobserved";
                                reason = "no-bypass-event-latest-occurrence";
                            }
                            else if (actualMinutes + 0.25f < expectedMinutes)
                            {
                                status = "shortfall";
                                reason = "hold-shortfall-latest-occurrence";
                            }
                            else
                            {
                                status = "satisfied";
                                reason = "hold-observed-latest-occurrence";
                            }
                            break;
                        }

                        case "retime":
                        case "expressOffset":
                        {
                            ChangeDto[] relatedChangedRows = ChangedRowsForAction(
                                contract,
                                actionType,
                                tripRowIds,
                                lineIds);
                            int matchedBaselineCount = 0;
                            bool anyShiftMismatch = false;
                            actualMinutes = 0f;
                            for (int changedRowIndex = 0; changedRowIndex < relatedChangedRows.Length; changedRowIndex++)
                            {
                                ChangeDto changedRow = relatedChangedRows[changedRowIndex];
                                BaselineDto matchedBaselineRow = null;
                                string plannerRowId = ImportedRowId(changedRow?.tripId);
                                if (!string.IsNullOrEmpty(plannerRowId)
                                    && baselineRowsById.TryGetValue(plannerRowId, out BaselineDto directMatchedRow))
                                {
                                    matchedBaselineRow = directMatchedRow;
                                }
                                else
                                {
                                    int afterMinute = m_Port.Parse(changedRow?.afterTime);
                                    if (afterMinute >= 0)
                                    {
                                        baselineRowsByAppliedSemanticKey.TryGetValue(
                                            TripKey(
                                                changedRow?.lineId,
                                                changedRow?.kind,
                                                afterMinute),
                                            out matchedBaselineRow);
                                    }
                                }

                                if (matchedBaselineRow == null)
                                    continue;

                                matchedBaselineCount++;
                                int beforeMinute = m_Port.Parse(changedRow?.beforeTime);
                                if (beforeMinute < 0)
                                    continue;

                                float actualShiftMinutes = Math.Abs(NormalizeMinuteDelta(matchedBaselineRow.plannedMinute - beforeMinute));
                                actualMinutes = Math.Max(actualMinutes, actualShiftMinutes);
                                if (Math.Abs(actualShiftMinutes - Math.Abs(changedRow.scheduleShiftMinutes)) > 0.25f)
                                {
                                    anyShiftMismatch = true;
                                }
                            }

                            if (relatedChangedRows.Length == 0)
                            {
                                status = "informational";
                                reason = "no-related-changed-rows";
                            }
                            else if (matchedBaselineCount == 0)
                            {
                                status = "notApplied";
                                reason = "planned-row-missing-from-baseline";
                            }
                            else if (anyShiftMismatch)
                            {
                                status = "diverged";
                                reason = "baseline-shift-mismatch";
                            }
                            else if (matchedBaselineCount < relatedChangedRows.Length)
                            {
                                status = "partial";
                                reason = "baseline-rows-missing";
                            }
                            else
                            {
                                status = "applied";
                                reason = "baseline-shift-applied";
                            }
                            break;
                        }

                        case "bypassSet":
                        {
                            bool anyBypassObserved = stationIds.Length > 0
                                && bypassEvents
                                    .Where(item => stationIds.Contains(item.holdPlannerStationId))
                                    .GroupBy(item => item.localTripObservationId ?? string.Empty, StringComparer.Ordinal)
                                    .Any(group => group
                                        .OrderByDescending(item => item.localOccurrenceIndex)
                                        .ThenByDescending(item => item.lastUpdatedFrame)
                                        .FirstOrDefault() != null);
                            status = anyBypassObserved ? "observed" : "unobserved";
                            reason = anyBypassObserved ? "bypass-station-used" : "bypass-station-unused";
                            actualMinutes = 0f;
                            break;
                        }

                        default:
                        {
                            status = "informational";
                            reason = "action-type-not-evaluated";
                            actualMinutes = 0f;
                            break;
                        }
                    }

                    actionResults.Add(new ActionResultDto
                    {
                        contractPlanId = contract.importedPlanId ?? string.Empty,
                        actionId = (contract.importedPlanId ?? "contract") + "#action-" + i.ToString(),
                        actionType = actionType,
                        lineIds = lineIds,
                        tripRowIds = tripRowIds,
                        stationIds = stationIds,
                        expectedMinutes = expectedMinutes,
                        actualMinutes = Round2(actualMinutes),
                        status = status,
                        reason = reason
                    });
                }
            }

            ReportSummaryDto summary = new ReportSummaryDto
            {
                baselineTripCount = baselineRows.Length,
                observedTripCount = tripResults.Count(item => item.occurrenceIndex > 0),
                launchedTripCount = tripResults.Count(item => item.actualDepartureMinute >= 0),
                missingTripCount = tripResults.Count(item => string.Equals(item.state, "missing", StringComparison.Ordinal)),
                plannerContractCount = plannerContracts.Length,
                plannerChangedTripCount = plannerContracts.Sum(item => item.changedRows?.Length ?? 0),
                plannerActionCount = actionResults.Count,
                satisfiedActionCount = actionResults.Count(item =>
                    string.Equals(item.status, "satisfied", StringComparison.Ordinal)
                    || string.Equals(item.status, "applied", StringComparison.Ordinal)
                    || string.Equals(item.status, "observed", StringComparison.Ordinal)),
                unresolvedActionCount = actionResults.Count(item =>
                    string.Equals(item.status, "shortfall", StringComparison.Ordinal)
                    || string.Equals(item.status, "wrongStation", StringComparison.Ordinal)
                    || string.Equals(item.status, "unobserved", StringComparison.Ordinal)
                    || string.Equals(item.status, "notApplied", StringComparison.Ordinal))
            };

            return new ReportDto
            {
                summary = summary,
                tripResults = tripResults.ToArray(),
                actionResults = actionResults.ToArray()
            };
        }

        private static string TripKey(string lineId, string serviceKind, int minute)
        {
            return (lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(serviceKind) ? "local" : serviceKind)
                + "|"
                + minute.ToString();
        }

        private static string ImportedRowId(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return string.Empty;

            return tripId.StartsWith("planner-", StringComparison.Ordinal)
                ? tripId
                : "planner-" + tripId;
        }

        private static string ContractTripId(string rowId)
        {
            if (string.IsNullOrEmpty(rowId))
                return string.Empty;

            return rowId.StartsWith("planner-", StringComparison.Ordinal)
                ? rowId.Substring("planner-".Length)
                : rowId;
        }

        private static ChangeDto[] ChangedRowsForAction(
            ContractDto contract,
            string actionType,
            string[] tripRowIds,
            string[] lineIds)
        {
            ChangeDto[] changedRows = contract?.changedRows ?? Array.Empty<ChangeDto>();
            if (tripRowIds != null && tripRowIds.Length > 0)
            {
                HashSet<string> tripIds = new HashSet<string>(
                    tripRowIds
                        .Select(ContractTripId)
                        .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.Ordinal);
                return changedRows
                    .Where(row => row != null && tripIds.Contains(row.tripId ?? string.Empty))
                    .ToArray();
            }

            if (lineIds != null && lineIds.Length > 0)
            {
                HashSet<string> targetLineIds = new HashSet<string>(lineIds.Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
                return changedRows
                    .Where(row =>
                        row != null
                        && targetLineIds.Contains(row.lineId ?? string.Empty)
                        && (!string.Equals(actionType, "expressOffset", StringComparison.Ordinal)
                            || string.Equals(row.kind, "express", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }

            return Array.Empty<ChangeDto>();
        }

        private static float ComputeExpectedActionMinutes(ActionDto action)
        {
            if (action == null)
                return 0f;

            if (action.deltaPattern != null && action.deltaPattern.Length > 0)
                return Round2(action.deltaPattern.Max(value => Math.Abs(value)));

            return Round2(Math.Abs(action.deltaMinutes));
        }

        private static ChangeDto FindContractChangedRow(ContractDto contract, string contractTripId)
        {
            if (contract == null || string.IsNullOrEmpty(contractTripId))
                return null;

            return (contract.changedRows ?? Array.Empty<ChangeDto>())
                .FirstOrDefault(row => string.Equals(row?.tripId, contractTripId, StringComparison.Ordinal));
        }

        private static float Round2(float value) =>
            (float)Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private Trip ResolveTrip(Entity vehicle, int preferredTargetMinute, Entity preferredLine)
        {
            if (vehicle == Entity.Null
                || !m_Store.ByVehicle.TryGetValue(vehicle, out List<Trip> trips)
                || trips == null
                || trips.Count == 0)
            {
                return null;
            }

            Trip exact = trips
                .Where(trip =>
                    trip != null
                    && trip.TargetMin == preferredTargetMinute
                    && (preferredLine == Entity.Null || trip.Line == preferredLine))
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.UpdatedFrame)
                .FirstOrDefault();
            if (exact != null)
                return exact;

            Trip sameLine = trips
                .Where(trip => trip != null && (preferredLine == Entity.Null || trip.Line == preferredLine))
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.UpdatedFrame)
                .FirstOrDefault();
            if (sameLine != null)
                return sameLine;

            return trips
                .Where(trip => trip != null)
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.UpdatedFrame)
                .FirstOrDefault();
        }

        private int TryGetObservedVehicleTargetMin(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return -1;
            return m_Port.TargetMin(vehicle);
        }

        private static string SlotKey(Entity line, int targetMinute)
        {
            return Dispatch.Observation.Trips.SlotKey(line, targetMinute);
        }

        private string StationId(Entity line, int waypointIndex)
        {
            if (line == Entity.Null || waypointIndex < 0)
                return string.Empty;
            if (!m_Port.HasWaypoints(line))
                return string.Empty;

            string lineId = m_Port.LineId(line);
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            DynamicBuffer<RouteWaypoint> waypoints = m_Port.Waypoints(line);
            Dictionary<Entity, int> stationOrderByStopEntity = new Dictionary<Entity, int>();
            int nextOrder = 0;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = m_Port.Stop(waypoints[i].m_Waypoint);
                if (stopEntity == Entity.Null)
                    continue;

                if (!stationOrderByStopEntity.TryGetValue(stopEntity, out int order))
                {
                    order = nextOrder;
                    stationOrderByStopEntity[stopEntity] = order;
                    nextOrder++;
                }

                if (i == waypointIndex)
                    return lineId + ":station-" + order.ToString();
            }

            return string.Empty;
        }

        private float DwellMinutes(StopEvent stopEvent)
        {
            if (stopEvent == null || stopEvent.ArrivalFrame == 0 || stopEvent.DepartureFrame == 0 || stopEvent.DepartureFrame <= stopEvent.ArrivalFrame)
                return 0f;
            return (float)Math.Round((stopEvent.DepartureFrame - stopEvent.ArrivalFrame) / m_Port.FramesPerMinute, 1);
        }

        private float HoldMinutes(BypassEvent bypassEvent)
        {
            if (bypassEvent == null || bypassEvent.HoldStartFrame == 0)
                return 0f;

            uint endFrame = bypassEvent.HoldReleaseFrame > 0
                ? bypassEvent.HoldReleaseFrame
                : m_Port.Frame();
            if (endFrame <= bypassEvent.HoldStartFrame)
                return 0f;

            return (float)Math.Round((endFrame - bypassEvent.HoldStartFrame) / m_Port.FramesPerMinute, 1);
        }

        private static int NormalizeMinuteDelta(int delta)
        {
            while (delta > 720)
                delta -= 1440;
            while (delta < -720)
                delta += 1440;
            return delta;
        }
    }
}
