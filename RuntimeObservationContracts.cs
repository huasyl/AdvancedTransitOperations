using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using RapidTransitMod.Planner;
using Game.Routes;
using Unity.Entities;

namespace RapidTransitMod
{
    public partial class DispatchRuntimeSystem
    {
        [DataContract]
        public class DispatchRuntimeObservationSnapshotDto
        {
            [DataMember] public int schemaVersion;
            [DataMember] public string snapshotId;
            [DataMember] public string status;
            [DataMember] public uint generatedAtFrame;
            [DataMember] public uint appliedAtFrame;
            [DataMember] public uint lastUpdatedFrame;
            [DataMember] public RuntimeObservedTripDto[] appliedTrips;
            [DataMember] public RuntimeObservedStopEventDto[] stopEvents;
            [DataMember] public RuntimeObservedBypassEventDto[] bypassEvents;
            [DataMember] public RuntimeObservedCorridorPassageDto[] corridorPassages;
            [DataMember] public RuntimeObservedBaselineRowDto[] baselineRows;
            [DataMember] public RuntimeObservedPlannerContractDto[] plannerContracts;
            [DataMember] public RuntimeObservedAttainmentReportDto attainmentReport;
        }

        [DataContract]
        public class RuntimeObservedBaselineRowDto
        {
            [DataMember] public string draftKey;
            [DataMember] public string rowId;
            [DataMember] public string lineId;
            [DataMember] public string plannedTime;
            [DataMember] public int plannedMinute;
            [DataMember] public string serviceKind;
            [DataMember] public string source;
            [DataMember] public string originStationId;
            [DataMember] public string originStationName;
        }

        [DataContract]
        public class RuntimeObservedPlannerContractDto
        {
            [DataMember] public string draftKey;
            [DataMember] public string importedFrom;
            [DataMember] public string importedPlanId;
            [DataMember] public string importedObjectiveId;
            [DataMember] public string[] importedLineIds;
            [DataMember] public DispatchPlannerRequestEchoDto requestEcho;
            [DataMember] public DispatchPlannerLineRoleSummaryDto lineRoleSummary;
            [DataMember] public string[] selectedBypassStationIds;
            [DataMember] public DispatchPlannerChangedRowDto[] changedRows;
            [DataMember] public DispatchPlannerScheduleActionDto[] structuredActions;
            [DataMember] public DispatchPlannerRiskItemDto[] riskItems;
        }

        [DataContract]
        public class RuntimeObservedAttainmentReportDto
        {
            [DataMember] public RuntimeObservedAttainmentSummaryDto summary;
            [DataMember] public RuntimeObservedTripAttainmentDto[] tripResults;
            [DataMember] public RuntimeObservedActionAttainmentDto[] actionResults;
        }

        [DataContract]
        public class RuntimeObservedAttainmentSummaryDto
        {
            [DataMember] public int baselineTripCount;
            [DataMember] public int observedTripCount;
            [DataMember] public int launchedTripCount;
            [DataMember] public int missingTripCount;
            [DataMember] public int plannerContractCount;
            [DataMember] public int plannerChangedTripCount;
            [DataMember] public int plannerActionCount;
            [DataMember] public int satisfiedActionCount;
            [DataMember] public int unresolvedActionCount;
        }

        [DataContract]
        public class RuntimeObservedTripAttainmentDto
        {
            [DataMember] public string draftKey;
            [DataMember] public string rowId;
            [DataMember] public string lineId;
            [DataMember] public string plannedTime;
            [DataMember] public int plannedMinute;
            [DataMember] public string serviceDate;
            [DataMember] public int serviceDayIndex;
            [DataMember] public int occurrenceIndex;
            [DataMember] public string actualDepartureTime;
            [DataMember] public int actualDepartureMinute;
            [DataMember] public int deltaMinutes;
            [DataMember] public string serviceKind;
            [DataMember] public string source;
            [DataMember] public string state;
            [DataMember] public string bindingConfidence;
            [DataMember] public string reasonCode;
            [DataMember] public string matchMode;
            [DataMember] public string contractPlanId;
            [DataMember] public string contractTripId;
        }

        [DataContract]
        public class RuntimeObservedActionAttainmentDto
        {
            [DataMember] public string contractPlanId;
            [DataMember] public string actionId;
            [DataMember] public string actionType;
            [DataMember] public string[] lineIds;
            [DataMember] public string[] tripRowIds;
            [DataMember] public string[] stationIds;
            [DataMember] public float expectedMinutes;
            [DataMember] public float actualMinutes;
            [DataMember] public string status;
            [DataMember] public string reason;
        }

        [DataContract]
        public class RuntimeObservedTripDto
        {
            [DataMember] public string tripObservationId;
            [DataMember] public string state;
            [DataMember] public string lineId;
            [DataMember] public string rowId;
            [DataMember] public string source;
            [DataMember] public string serviceKind;
            [DataMember] public string plannedTime;
            [DataMember] public string serviceDate;
            [DataMember] public int serviceDayIndex;
            [DataMember] public int occurrenceIndex;
            [DataMember] public string actualDepartureTime;
            [DataMember] public int targetMinute;
            [DataMember] public int actualDepartureMinute;
            [DataMember] public int deltaMinutes;
            [DataMember] public int vehicleIndex;
            [DataMember] public uint launchFrame;
            [DataMember] public string bindingConfidence;
            [DataMember] public string reasonCode;
            [DataMember] public uint lastUpdatedFrame;
        }

        [DataContract]
        public class RuntimeObservedStopEventDto
        {
            [DataMember] public string eventId;
            [DataMember] public string eventType;
            [DataMember] public string tripObservationId;
            [DataMember] public string rowId;
            [DataMember] public string lineId;
            [DataMember] public string serviceDate;
            [DataMember] public int serviceDayIndex;
            [DataMember] public int occurrenceIndex;
            [DataMember] public int vehicleIndex;
            [DataMember] public int targetMinute;
            [DataMember] public string stationId;
            [DataMember] public string plannerStationId;
            [DataMember] public string stationName;
            [DataMember] public int waypointIndex;
            [DataMember] public bool isOrigin;
            [DataMember] public string arrivalTime;
            [DataMember] public string departureTime;
            [DataMember] public uint arrivalFrame;
            [DataMember] public uint departureFrame;
            [DataMember] public float dwellMinutes;
            [DataMember] public uint lastUpdatedFrame;
        }

        [DataContract]
        public class RuntimeObservedBypassEventDto
        {
            [DataMember] public string eventId;
            [DataMember] public string state;
            [DataMember] public string localTripObservationId;
            [DataMember] public string localRowId;
            [DataMember] public string localServiceDate;
            [DataMember] public int localServiceDayIndex;
            [DataMember] public int localOccurrenceIndex;
            [DataMember] public string priorityTripObservationId;
            [DataMember] public string priorityRowId;
            [DataMember] public string priorityServiceDate;
            [DataMember] public int priorityServiceDayIndex;
            [DataMember] public int priorityOccurrenceIndex;
            [DataMember] public string localLineId;
            [DataMember] public string priorityLineId;
            [DataMember] public int localVehicleIndex;
            [DataMember] public int priorityVehicleIndex;
            [DataMember] public int localTargetMinute;
            [DataMember] public int priorityTargetMinute;
            [DataMember] public string holdStationId;
            [DataMember] public string holdPlannerStationId;
            [DataMember] public string holdStationName;
            [DataMember] public int waypointIndex;
            [DataMember] public uint holdStartFrame;
            [DataMember] public uint holdReleaseFrame;
            [DataMember] public float actualHoldMinutes;
            [DataMember] public string decisionReason;
            [DataMember] public string releaseReason;
            [DataMember] public string sceneKey;
            [DataMember] public int protectedIntervalIndex;
            [DataMember] public uint lastUpdatedFrame;
        }

        [DataContract]
        public class RuntimeObservedCorridorPassageDto
        {
            [DataMember] public string passageId;
            [DataMember] public string tripObservationId;
            [DataMember] public string rowId;
            [DataMember] public string lineId;
            [DataMember] public string serviceDate;
            [DataMember] public int serviceDayIndex;
            [DataMember] public int occurrenceIndex;
            [DataMember] public int vehicleIndex;
            [DataMember] public int targetMinute;
            [DataMember] public string corridorId;
            [DataMember] public string fromStationId;
            [DataMember] public string toStationId;
            [DataMember] public uint entryFrame;
            [DataMember] public uint exitFrame;
            [DataMember] public string entryTime;
            [DataMember] public string exitTime;
            [DataMember] public int entryAtomIndex;
            [DataMember] public int exitAtomIndex;
        }

        public string GetRuntimeObservationSnapshotJson()
        {
            EnsureRuntimeObservationSessionSeeded();
            return DispatchWorkbenchJson.Serialize(BuildRuntimeObservationSnapshot());
        }

        public void RequestDumpRuntimeObservationSnapshot()
        {
            try
            {
                string json = GetRuntimeObservationSnapshotJson();
                string logsDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow",
                    "Colossal Order",
                    "Cities Skylines II",
                    "Logs");
                Directory.CreateDirectory(logsDirectory);
                string filePath = Path.Combine(logsDirectory, "RapidTransitMod-runtime-observation-latest.json");
                File.WriteAllText(filePath, json);
                Mod.log.Info("[RuntimeObservationDump] exported to " + filePath);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[RuntimeObservationDump] export failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void EnsureRuntimeObservationSessionSeeded()
        {
            if (m_RuntimeObservations.Session != null)
                return;

            EnsureAppliedWorkbenchPersistenceLoaded();
            if (m_AppliedWorkbenchLines == null || m_AppliedWorkbenchLines.Count == 0)
                return;

            bool hasAppliedRows = m_AppliedWorkbenchLines.Values.Any(
                state => state != null && state.StagedRows != null && state.StagedRows.Count > 0);
            if (!hasAppliedRows)
                return;

            SeedObservationFromAppliedRows(GetPreferredWorkbenchLineId());
        }

        private void SeedObservationFromAppliedRows(string selectedLineId)
        {
            DateTime appliedGameDate = ResolveRuntimeObservationGameDate();
            m_RuntimeObservations.Session = new RuntimeObservationSession
            {
                SnapshotId = "runtime-observation-" + (m_SimulationSystem != null ? m_SimulationSystem.frameIndex.ToString() : "0"),
                Status = "active",
                AppliedAtFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0,
                LastUpdatedFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0,
                AppliedGameDate = appliedGameDate
            };
            m_RuntimeObservations.ClearIndexes();

            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines)
            {
                AppliedWorkbenchLineState lineState = entry.Value;
                if (lineState == null || lineState.LineEntity == Entity.Null || lineState.StagedRows == null)
                    continue;

                string lineId = entry.Key;
                foreach (DispatchWorkbenchStagedRowDto row in lineState.StagedRows)
                {
                    if (row == null)
                        continue;

                    int targetMinute = ParseTimeMinutes(row.time);
                    if (targetMinute < 0)
                        continue;

                    string rowLineId = !string.IsNullOrEmpty(row.lineId) ? row.lineId : lineId;
                    CreateRuntimeObservedTrip(
                        lineState.LineEntity,
                        rowLineId,
                        row.id ?? string.Empty,
                        row.source ?? string.Empty,
                        row.kind ?? string.Empty,
                        targetMinute,
                        1,
                        m_RuntimeObservations.Session.AppliedAtFrame,
                        appliedGameDate);
                }
            }

            if (m_RuntimeObservations.Session.TripsById.Count == 0)
            {
                m_RuntimeObservations.Session.Status = "empty";
            }

            log.Info("[RuntimeObservation] seeded snapshot=" + m_RuntimeObservations.Session.SnapshotId
                + " selectedLine=" + (selectedLineId ?? string.Empty)
                + " trips=" + m_RuntimeObservations.Session.TripsById.Count);
        }

        private DateTime ResolveRuntimeObservationGameDate()
        {
            if (m_TimeSystem != null)
            {
                return m_TimeSystem.GetCurrentDateTime().Date;
            }

            return DateTime.MinValue.Date;
        }

        private int ResolveRuntimeObservationServiceDayIndex(DateTime serviceDate)
        {
            if (m_RuntimeObservations.Session == null)
                return -1;

            DateTime appliedDate = m_RuntimeObservations.Session.AppliedGameDate.Date;
            if (serviceDate == DateTime.MinValue.Date || appliedDate == DateTime.MinValue.Date)
                return -1;

            return (serviceDate - appliedDate).Days;
        }

        private static string FormatRuntimeObservationServiceDate(DateTime serviceDate)
        {
            return serviceDate == DateTime.MinValue.Date
                ? string.Empty
                : serviceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string BuildRuntimeObservationBaseKey(string lineId, int targetMinute, string rowId)
        {
            return (lineId ?? string.Empty)
                + "|"
                + targetMinute.ToString(CultureInfo.InvariantCulture)
                + "|"
                + (rowId ?? string.Empty);
        }

        private RuntimeObservedTrip CreateRuntimeObservedTrip(
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
            if (m_RuntimeObservations.Session == null)
                return null;

            RuntimeObservedTrip trip = new RuntimeObservedTrip
            {
                TripObservationId = "slot|"
                    + (lineId ?? string.Empty)
                    + "|"
                    + targetMinute.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + (rowId ?? string.Empty)
                    + "|occ:"
                    + occurrenceIndex.ToString(CultureInfo.InvariantCulture),
                BaseObservationKey = BuildRuntimeObservationBaseKey(lineId, targetMinute, rowId),
                LineId = lineId ?? string.Empty,
                RowId = rowId ?? string.Empty,
                Source = source ?? string.Empty,
                ServiceKind = serviceKind ?? string.Empty,
                TargetMinute = targetMinute,
                Line = line,
                ServiceDate = FormatRuntimeObservationServiceDate(serviceDate),
                ServiceDayIndex = ResolveRuntimeObservationServiceDayIndex(serviceDate),
                OccurrenceIndex = Math.Max(1, occurrenceIndex),
                LastUpdatedFrame = nowFrame
            };
            m_RuntimeObservations.Session.TripsById[trip.TripObservationId] = trip;
            AddRuntimeObservedTripLineSlotIndex(trip);
            return trip;
        }

        private static bool HasRuntimeObservedTripCompletedCycle(RuntimeObservedTrip trip)
        {
            return trip != null
                && (trip.LaunchFrame > 0
                    || trip.ActualDepartureMinute >= 0
                    || string.Equals(trip.State, "departed", StringComparison.Ordinal));
        }

        private RuntimeObservedTrip CreateNextRuntimeObservedTripOccurrence(RuntimeObservedTrip previousTrip, uint nowFrame)
        {
            if (previousTrip == null)
                return null;

            return CreateRuntimeObservedTrip(
                previousTrip.Line,
                previousTrip.LineId,
                previousTrip.RowId,
                previousTrip.Source,
                previousTrip.ServiceKind,
                previousTrip.TargetMinute,
                previousTrip.OccurrenceIndex + 1,
                nowFrame,
                ResolveRuntimeObservationGameDate());
        }

        private RuntimeObservedTrip[] GetRuntimeObservedTripActiveOccurrences(Entity line, int targetMinute, uint nowFrame)
        {
            if (line == Entity.Null
                || targetMinute < 0
                || !m_RuntimeObservations.TripsByLineSlot.TryGetValue(BuildRuntimeObservationLineSlotKey(line, targetMinute), out List<RuntimeObservedTrip> trips)
                || trips == null
                || trips.Count == 0)
            {
                return Array.Empty<RuntimeObservedTrip>();
            }

            List<RuntimeObservedTrip> activeTrips = new List<RuntimeObservedTrip>();
            foreach (IGrouping<string, RuntimeObservedTrip> group in trips
                .Where(trip => trip != null)
                .GroupBy(trip => trip.BaseObservationKey, StringComparer.Ordinal))
            {
                RuntimeObservedTrip latestTrip = group
                    .OrderByDescending(trip => trip.OccurrenceIndex)
                    .ThenByDescending(trip => trip.LastUpdatedFrame)
                    .FirstOrDefault();
                if (latestTrip == null)
                    continue;

                if (HasRuntimeObservedTripCompletedCycle(latestTrip))
                {
                    latestTrip = CreateNextRuntimeObservedTripOccurrence(latestTrip, nowFrame);
                }

                if (latestTrip != null)
                {
                    activeTrips.Add(latestTrip);
                }
            }

            return activeTrips.ToArray();
        }

        private void AddRuntimeObservedTripLineSlotIndex(RuntimeObservedTrip trip)
        {
            if (trip == null || trip.Line == Entity.Null || trip.TargetMinute < 0)
                return;

            string key = BuildRuntimeObservationLineSlotKey(trip.Line, trip.TargetMinute);
            if (!m_RuntimeObservations.TripsByLineSlot.TryGetValue(key, out List<RuntimeObservedTrip> trips))
            {
                trips = new List<RuntimeObservedTrip>();
                m_RuntimeObservations.TripsByLineSlot[key] = trips;
            }
            trips.Add(trip);
        }

        private void AddRuntimeObservedTripVehicleIndex(Entity vehicle, RuntimeObservedTrip trip)
        {
            if (vehicle == Entity.Null || trip == null)
                return;

            if (!m_RuntimeObservations.TripsByVehicle.TryGetValue(vehicle, out List<RuntimeObservedTrip> trips))
            {
                trips = new List<RuntimeObservedTrip>();
                m_RuntimeObservations.TripsByVehicle[vehicle] = trips;
            }
            if (!trips.Contains(trip))
            {
                trips.Add(trip);
            }
        }

        private void RecordRuntimeObservationTargetBound(Entity line, Entity vehicle, int targetMinute, uint nowFrame, string reasonCode)
        {
            if (line == Entity.Null || vehicle == Entity.Null || targetMinute < 0 || m_RuntimeObservations.Session == null)
                return;

            RuntimeObservedTrip[] trips = GetRuntimeObservedTripActiveOccurrences(line, targetMinute, nowFrame);
            if (trips.Length == 0)
                return;

            foreach (RuntimeObservedTrip trip in trips)
            {
                trip.State = trip.State == "pending" ? "bound" : trip.State;
                trip.Vehicle = vehicle;
                DateTime serviceDate = ResolveRuntimeObservationGameDate();
                trip.ServiceDate = FormatRuntimeObservationServiceDate(serviceDate);
                trip.ServiceDayIndex = ResolveRuntimeObservationServiceDayIndex(serviceDate);
                trip.ReasonCode = reasonCode ?? string.Empty;
                trip.BindingConfidence = "target-bound";
                trip.LastUpdatedFrame = nowFrame;
                AddRuntimeObservedTripVehicleIndex(vehicle, trip);
            }
            m_RuntimeObservations.Session.LastUpdatedFrame = nowFrame;
        }

        private void RecordRuntimeObservationLaunch(Entity line, Entity vehicle, int targetMinute, int actualMinute, uint launchFrame, bool lateDispatch)
        {
            if (line == Entity.Null || vehicle == Entity.Null || targetMinute < 0 || m_RuntimeObservations.Session == null)
                return;

            RuntimeObservedTrip[] trips = GetRuntimeObservedTripActiveOccurrences(line, targetMinute, launchFrame);
            if (trips.Length == 0)
                return;

            foreach (RuntimeObservedTrip trip in trips)
            {
                trip.State = "departed";
                trip.Vehicle = vehicle;
                DateTime serviceDate = ResolveRuntimeObservationGameDate();
                trip.ServiceDate = FormatRuntimeObservationServiceDate(serviceDate);
                trip.ServiceDayIndex = ResolveRuntimeObservationServiceDayIndex(serviceDate);
                trip.ActualDepartureMinute = actualMinute;
                trip.LaunchFrame = launchFrame;
                trip.ReasonCode = lateDispatch ? "late-dispatch-launch" : "origin-launch";
                trip.BindingConfidence = "vehicle-launch";
                trip.LastUpdatedFrame = launchFrame;
                AddRuntimeObservedTripVehicleIndex(vehicle, trip);
            }
            m_RuntimeObservations.Session.LastUpdatedFrame = launchFrame;
        }

        private void RecordRuntimeObservationStopEvent(
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
            if (vehicle == Entity.Null || line == Entity.Null || station == Entity.Null || m_RuntimeObservations.Session == null)
                return;

            RuntimeObservedTrip observedTrip = ResolveRuntimeObservedTrip(
                vehicle,
                TryGetRuntimeObservedVehicleTargetMinute(vehicle),
                line);
            if (observedTrip == null)
                return;

            RuntimeObservedStopEvent stopEvent = new RuntimeObservedStopEvent
            {
                EventId = "stop|" + vehicle.Index + "|" + station.Index + "|" + frame,
                EventType = arrival ? "arrival" : "departure",
                TripObservationId = observedTrip.TripObservationId,
                RowId = observedTrip.RowId,
                LineId = observedTrip.LineId,
                ServiceDate = observedTrip.ServiceDate,
                ServiceDayIndex = observedTrip.ServiceDayIndex,
                OccurrenceIndex = observedTrip.OccurrenceIndex,
                Line = line,
                Vehicle = vehicle,
                TargetMinute = observedTrip.TargetMinute,
                Station = station,
                Kind = kind,
                WaypointIndex = waypointIndex,
                IsOrigin = isOrigin,
                ArrivalTime = arrival ? clockTime : string.Empty,
                DepartureTime = arrival ? string.Empty : clockTime,
                ArrivalFrame = arrival ? frame : 0,
                DepartureFrame = arrival ? 0 : frame,
                LastUpdatedFrame = frame
            };
            m_RuntimeObservations.Session.StopEvents.Add(stopEvent);
            TrimRuntimeObservationList(m_RuntimeObservations.Session.StopEvents, 256);
            m_RuntimeObservations.Session.LastUpdatedFrame = frame;
        }

        private void RecordRuntimeObservationBypassHoldStart(
            Entity vehicle,
            Entity blocker,
            Entity holdStation,
            int waypointIndex,
            uint nowFrame,
            string reasonCode)
        {
            if (vehicle == Entity.Null || m_RuntimeObservations.Session == null)
                return;

            RuntimeObservedTrip localTrip = ResolveRuntimeObservedTrip(
                vehicle,
                TryGetRuntimeObservedVehicleTargetMinute(vehicle),
                ResolveVehicleLine(vehicle));
            RuntimeObservedTrip priorityTrip = ResolveRuntimeObservedTrip(
                blocker,
                TryGetRuntimeObservedVehicleTargetMinute(blocker),
                ResolveVehicleLine(blocker));

            if (m_RuntimeObservations.ActiveBypassByVehicle.TryGetValue(vehicle, out RuntimeObservedBypassEvent activeEvent)
                && activeEvent.PriorityVehicle == blocker
                && activeEvent.State == "holding")
            {
                if (activeEvent.HoldStation == Entity.Null && holdStation != Entity.Null)
                    activeEvent.HoldStation = holdStation;
                if (activeEvent.WaypointIndex < 0 && waypointIndex >= 0)
                    activeEvent.WaypointIndex = waypointIndex;
                if (priorityTrip != null)
                {
                    activeEvent.PriorityTripObservationId = priorityTrip.TripObservationId;
                    activeEvent.PriorityRowId = priorityTrip.RowId;
                    activeEvent.PriorityServiceDate = priorityTrip.ServiceDate;
                    activeEvent.PriorityServiceDayIndex = priorityTrip.ServiceDayIndex;
                    activeEvent.PriorityOccurrenceIndex = priorityTrip.OccurrenceIndex;
                }
                activeEvent.LastUpdatedFrame = nowFrame;
                m_RuntimeObservations.Session.LastUpdatedFrame = nowFrame;
                return;
            }
            if (activeEvent != null && activeEvent.State == "holding")
            {
                activeEvent.State = "released";
                activeEvent.HoldReleaseFrame = nowFrame;
                activeEvent.ReleaseReason = "superseded-by-new-bypass-hold";
                activeEvent.LastUpdatedFrame = nowFrame;
            }

            Entity localLine = ResolveVehicleLine(vehicle);
            Entity priorityLine = ResolveVehicleLine(blocker);
            RuntimeObservedBypassEvent bypassEvent = new RuntimeObservedBypassEvent
            {
                EventId = "bypass|" + vehicle.Index + "|" + nowFrame,
                State = "holding",
                LocalTripObservationId = localTrip?.TripObservationId ?? string.Empty,
                LocalRowId = localTrip?.RowId ?? string.Empty,
                LocalServiceDate = localTrip?.ServiceDate ?? string.Empty,
                LocalServiceDayIndex = localTrip?.ServiceDayIndex ?? -1,
                LocalOccurrenceIndex = localTrip?.OccurrenceIndex ?? 1,
                PriorityTripObservationId = priorityTrip?.TripObservationId ?? string.Empty,
                PriorityRowId = priorityTrip?.RowId ?? string.Empty,
                PriorityServiceDate = priorityTrip?.ServiceDate ?? string.Empty,
                PriorityServiceDayIndex = priorityTrip?.ServiceDayIndex ?? -1,
                PriorityOccurrenceIndex = priorityTrip?.OccurrenceIndex ?? 1,
                LocalLine = localLine,
                PriorityLine = priorityLine,
                LocalVehicle = vehicle,
                PriorityVehicle = blocker,
                LocalTargetMinute = TryGetRuntimeObservedVehicleTargetMinute(vehicle),
                PriorityTargetMinute = TryGetRuntimeObservedVehicleTargetMinute(blocker),
                HoldStation = holdStation,
                WaypointIndex = waypointIndex,
                HoldStartFrame = nowFrame,
                DecisionReason = reasonCode ?? "bypass-hold",
                LastUpdatedFrame = nowFrame
            };
            m_RuntimeObservations.ActiveBypassByVehicle[vehicle] = bypassEvent;
            m_RuntimeObservations.Session.BypassEvents.Add(bypassEvent);
            TrimRuntimeObservationList(m_RuntimeObservations.Session.BypassEvents, 128);
            m_RuntimeObservations.Session.LastUpdatedFrame = nowFrame;

            if (m_RuntimeObservations.TripsByVehicle.TryGetValue(vehicle, out List<RuntimeObservedTrip> trips))
            {
                foreach (RuntimeObservedTrip trip in trips)
                {
                    trip.State = "holding";
                    trip.LastUpdatedFrame = nowFrame;
                }
            }
        }

        private void RecordRuntimeObservationBypassHoldRelease(Entity vehicle, Entity blocker, uint nowFrame, string releaseReason)
        {
            if (vehicle == Entity.Null || m_RuntimeObservations.Session == null)
                return;

            if (!m_RuntimeObservations.ActiveBypassByVehicle.TryGetValue(vehicle, out RuntimeObservedBypassEvent bypassEvent))
                return;

            bypassEvent.State = "released";
            bypassEvent.PriorityVehicle = blocker;
            bypassEvent.PriorityLine = ResolveVehicleLine(blocker);
            bypassEvent.PriorityTargetMinute = TryGetRuntimeObservedVehicleTargetMinute(blocker);
            RuntimeObservedTrip priorityTrip = ResolveRuntimeObservedTrip(
                blocker,
                bypassEvent.PriorityTargetMinute,
                bypassEvent.PriorityLine);
            if (priorityTrip != null)
            {
                bypassEvent.PriorityTripObservationId = priorityTrip.TripObservationId;
                bypassEvent.PriorityRowId = priorityTrip.RowId;
                bypassEvent.PriorityServiceDate = priorityTrip.ServiceDate;
                bypassEvent.PriorityServiceDayIndex = priorityTrip.ServiceDayIndex;
                bypassEvent.PriorityOccurrenceIndex = priorityTrip.OccurrenceIndex;
            }
            bypassEvent.HoldReleaseFrame = nowFrame;
            bypassEvent.ReleaseReason = releaseReason ?? string.Empty;
            bypassEvent.LastUpdatedFrame = nowFrame;
            m_RuntimeObservations.ActiveBypassByVehicle.Remove(vehicle);
            m_RuntimeObservations.Session.LastUpdatedFrame = nowFrame;

            if (m_RuntimeObservations.TripsByVehicle.TryGetValue(vehicle, out List<RuntimeObservedTrip> trips))
            {
                foreach (RuntimeObservedTrip trip in trips)
                {
                    trip.State = trip.LaunchFrame > 0 ? "departed" : "bound";
                    trip.LastUpdatedFrame = nowFrame;
                }
            }
        }

        private DispatchRuntimeObservationSnapshotDto BuildRuntimeObservationSnapshot()
        {
            RuntimeObservedBaselineRowDto[] baselineRows = BuildRuntimeObservedBaselineRows();
            RuntimeObservedPlannerContractDto[] plannerContracts = BuildRuntimeObservedPlannerContracts();
            RuntimeObservationSession session = m_RuntimeObservations.Session;
            if (session == null)
            {
                RuntimeObservedTripDto[] emptyTrips = Array.Empty<RuntimeObservedTripDto>();
                RuntimeObservedStopEventDto[] emptyStops = Array.Empty<RuntimeObservedStopEventDto>();
                RuntimeObservedBypassEventDto[] emptyBypassEvents = Array.Empty<RuntimeObservedBypassEventDto>();
                RuntimeObservedCorridorPassageDto[] emptyCorridors = Array.Empty<RuntimeObservedCorridorPassageDto>();
                return new DispatchRuntimeObservationSnapshotDto
                {
                    schemaVersion = 2,
                    snapshotId = string.Empty,
                    status = "empty",
                    generatedAtFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0,
                    appliedTrips = emptyTrips,
                    stopEvents = emptyStops,
                    bypassEvents = emptyBypassEvents,
                    corridorPassages = emptyCorridors,
                    baselineRows = baselineRows,
                    plannerContracts = plannerContracts,
                    attainmentReport = BuildRuntimeObservationAttainmentReport(
                        baselineRows,
                        plannerContracts,
                        emptyTrips,
                        emptyBypassEvents)
                };
            }

            RuntimeObservedTripDto[] appliedTrips = session.TripsById.Values
                .OrderBy(trip => trip.LineId, StringComparer.Ordinal)
                .ThenBy(trip => trip.TargetMinute)
                .ThenBy(trip => trip.RowId, StringComparer.Ordinal)
                .Select(BuildRuntimeObservedTripDto)
                .ToArray();
            RuntimeObservedStopEventDto[] stopEvents = session.StopEvents.Select(BuildRuntimeObservedStopEventDto).ToArray();
            RuntimeObservedBypassEventDto[] bypassEvents = session.BypassEvents.Select(BuildRuntimeObservedBypassEventDto).ToArray();
            RuntimeObservedCorridorPassageDto[] corridorPassages = session.CorridorPassages.ToArray();

            return new DispatchRuntimeObservationSnapshotDto
            {
                schemaVersion = 2,
                snapshotId = session.SnapshotId,
                status = session.Status,
                generatedAtFrame = m_SimulationSystem != null ? m_SimulationSystem.frameIndex : 0,
                appliedAtFrame = session.AppliedAtFrame,
                lastUpdatedFrame = session.LastUpdatedFrame,
                appliedTrips = appliedTrips,
                stopEvents = stopEvents,
                bypassEvents = bypassEvents,
                corridorPassages = corridorPassages,
                baselineRows = baselineRows,
                plannerContracts = plannerContracts,
                attainmentReport = BuildRuntimeObservationAttainmentReport(
                    baselineRows,
                    plannerContracts,
                    appliedTrips,
                    bypassEvents)
            };
        }

        private RuntimeObservedTripDto BuildRuntimeObservedTripDto(RuntimeObservedTrip trip)
        {
            int deltaMinutes = trip.ActualDepartureMinute >= 0 && trip.TargetMinute >= 0
                ? NormalizeMinuteDelta(trip.ActualDepartureMinute - trip.TargetMinute)
                : 0;
            return new RuntimeObservedTripDto
            {
                tripObservationId = trip.TripObservationId,
                state = trip.State,
                lineId = trip.LineId,
                rowId = trip.RowId,
                source = trip.Source,
                serviceKind = trip.ServiceKind,
                plannedTime = trip.TargetMinute >= 0 ? SlotStr(trip.TargetMinute) : string.Empty,
                serviceDate = trip.ServiceDate,
                serviceDayIndex = trip.ServiceDayIndex,
                occurrenceIndex = trip.OccurrenceIndex,
                actualDepartureTime = trip.ActualDepartureMinute >= 0 ? SlotStr(trip.ActualDepartureMinute) : string.Empty,
                targetMinute = trip.TargetMinute,
                actualDepartureMinute = trip.ActualDepartureMinute,
                deltaMinutes = deltaMinutes,
                vehicleIndex = trip.Vehicle != Entity.Null ? trip.Vehicle.Index : -1,
                launchFrame = trip.LaunchFrame,
                bindingConfidence = trip.BindingConfidence,
                reasonCode = trip.ReasonCode,
                lastUpdatedFrame = trip.LastUpdatedFrame
            };
        }

        private RuntimeObservedStopEventDto BuildRuntimeObservedStopEventDto(RuntimeObservedStopEvent stopEvent)
        {
            return new RuntimeObservedStopEventDto
            {
                eventId = stopEvent.EventId,
                eventType = stopEvent.EventType,
                tripObservationId = stopEvent.TripObservationId,
                rowId = stopEvent.RowId,
                lineId = !string.IsNullOrEmpty(stopEvent.LineId)
                    ? stopEvent.LineId
                    : (stopEvent.Line != Entity.Null ? GetWorkbenchLineId(stopEvent.Line) : string.Empty),
                serviceDate = stopEvent.ServiceDate,
                serviceDayIndex = stopEvent.ServiceDayIndex,
                occurrenceIndex = stopEvent.OccurrenceIndex,
                vehicleIndex = stopEvent.Vehicle != Entity.Null ? stopEvent.Vehicle.Index : -1,
                targetMinute = stopEvent.TargetMinute,
                stationId = CreateStopId(stopEvent.Station, stopEvent.Kind),
                plannerStationId = BuildRuntimeObservationPlannerStationId(stopEvent.Line, stopEvent.WaypointIndex),
                stationName = ResolveStopName(stopEvent.Station, stopEvent.Kind),
                waypointIndex = stopEvent.WaypointIndex,
                isOrigin = stopEvent.IsOrigin,
                arrivalTime = stopEvent.ArrivalTime,
                departureTime = stopEvent.DepartureTime,
                arrivalFrame = stopEvent.ArrivalFrame,
                departureFrame = stopEvent.DepartureFrame,
                dwellMinutes = ComputeRuntimeObservationDwellMinutes(stopEvent),
                lastUpdatedFrame = stopEvent.LastUpdatedFrame
            };
        }

        private RuntimeObservedBypassEventDto BuildRuntimeObservedBypassEventDto(RuntimeObservedBypassEvent bypassEvent)
        {
            return new RuntimeObservedBypassEventDto
            {
                eventId = bypassEvent.EventId,
                state = bypassEvent.State,
                localTripObservationId = bypassEvent.LocalTripObservationId,
                localRowId = bypassEvent.LocalRowId,
                localServiceDate = bypassEvent.LocalServiceDate,
                localServiceDayIndex = bypassEvent.LocalServiceDayIndex,
                localOccurrenceIndex = bypassEvent.LocalOccurrenceIndex,
                priorityTripObservationId = bypassEvent.PriorityTripObservationId,
                priorityRowId = bypassEvent.PriorityRowId,
                priorityServiceDate = bypassEvent.PriorityServiceDate,
                priorityServiceDayIndex = bypassEvent.PriorityServiceDayIndex,
                priorityOccurrenceIndex = bypassEvent.PriorityOccurrenceIndex,
                localLineId = bypassEvent.LocalLine != Entity.Null ? GetWorkbenchLineId(bypassEvent.LocalLine) : string.Empty,
                priorityLineId = bypassEvent.PriorityLine != Entity.Null ? GetWorkbenchLineId(bypassEvent.PriorityLine) : string.Empty,
                localVehicleIndex = bypassEvent.LocalVehicle != Entity.Null ? bypassEvent.LocalVehicle.Index : -1,
                priorityVehicleIndex = bypassEvent.PriorityVehicle != Entity.Null ? bypassEvent.PriorityVehicle.Index : -1,
                localTargetMinute = bypassEvent.LocalTargetMinute,
                priorityTargetMinute = bypassEvent.PriorityTargetMinute,
                holdStationId = CreateWorkbenchOriginStationId(bypassEvent.HoldStation),
                holdPlannerStationId = BuildRuntimeObservationPlannerStationId(bypassEvent.LocalLine, bypassEvent.WaypointIndex),
                holdStationName = ResolveWorkbenchStationName(bypassEvent.HoldStation),
                waypointIndex = bypassEvent.WaypointIndex,
                holdStartFrame = bypassEvent.HoldStartFrame,
                holdReleaseFrame = bypassEvent.HoldReleaseFrame,
                actualHoldMinutes = ComputeRuntimeObservationHoldMinutes(bypassEvent),
                decisionReason = bypassEvent.DecisionReason,
                releaseReason = bypassEvent.ReleaseReason,
                sceneKey = bypassEvent.SceneKey,
                protectedIntervalIndex = bypassEvent.ProtectedIntervalIndex,
                lastUpdatedFrame = bypassEvent.LastUpdatedFrame
            };
        }

        private RuntimeObservedBaselineRowDto[] BuildRuntimeObservedBaselineRows()
        {
            EnsureAppliedWorkbenchPersistenceLoaded();
            List<RuntimeObservedBaselineRowDto> rows = new List<RuntimeObservedBaselineRowDto>();
            foreach (KeyValuePair<string, AppliedWorkbenchLineState> entry in m_AppliedWorkbenchLines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppliedWorkbenchLineState state = entry.Value;
                if (state == null || state.LineEntity == Entity.Null || state.StagedRows == null)
                    continue;

                ResolveWorkbenchLineOrigin(state.LineEntity, out string originStationId, out string originStationName);
                string lineId = GetWorkbenchLineId(state.LineEntity);
                foreach (DispatchWorkbenchStagedRowDto row in state.StagedRows)
                {
                    if (row == null)
                        continue;

                    int plannedMinute = ParseTimeMinutes(row.time);
                    rows.Add(new RuntimeObservedBaselineRowDto
                    {
                        draftKey = entry.Key,
                        rowId = row.id ?? string.Empty,
                        lineId = !string.IsNullOrEmpty(row.lineId) ? row.lineId : lineId,
                        plannedTime = row.time ?? string.Empty,
                        plannedMinute = plannedMinute,
                        serviceKind = row.kind ?? string.Empty,
                        source = row.source ?? string.Empty,
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

        private RuntimeObservedPlannerContractDto[] BuildRuntimeObservedPlannerContracts()
        {
            List<RuntimeObservedPlannerContractDto> contracts = new List<RuntimeObservedPlannerContractDto>();
            foreach (KeyValuePair<string, DispatchWorkbenchPlannerImportContractDto> entry in m_AppliedPlannerImportContracts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                DispatchWorkbenchPlannerImportContractDto contract = entry.Value;
                if (contract?.plan == null)
                    continue;

                DispatchPlannerChangedRowDto[] changedRows = (contract.plan.changedWindows ?? Array.Empty<DispatchPlannerChangedWindowDto>())
                    .SelectMany(window => window?.rowDiffs ?? Array.Empty<DispatchPlannerChangedRowDto>())
                    .ToArray();
                contracts.Add(new RuntimeObservedPlannerContractDto
                {
                    draftKey = entry.Key,
                    importedFrom = contract.importedFrom ?? string.Empty,
                    importedPlanId = contract.importedPlanId ?? contract.plan.planId ?? string.Empty,
                    importedObjectiveId = contract.importedObjectiveId ?? contract.plan.objectiveId ?? string.Empty,
                    importedLineIds = contract.importedLineIds ?? Array.Empty<string>(),
                    requestEcho = contract.requestEcho,
                    lineRoleSummary = contract.plan.lineRoleSummary,
                    selectedBypassStationIds = contract.plan.selectedBypassStationIds ?? Array.Empty<string>(),
                    changedRows = changedRows,
                    structuredActions = contract.plan.structuredScheduleActions ?? Array.Empty<DispatchPlannerScheduleActionDto>(),
                    riskItems = contract.plan.riskItems ?? Array.Empty<DispatchPlannerRiskItemDto>()
                });
            }

            return contracts.ToArray();
        }

        private RuntimeObservedAttainmentReportDto BuildRuntimeObservationAttainmentReport(
            RuntimeObservedBaselineRowDto[] baselineRows,
            RuntimeObservedPlannerContractDto[] plannerContracts,
            RuntimeObservedTripDto[] appliedTrips,
            RuntimeObservedBypassEventDto[] bypassEvents)
        {
            baselineRows ??= Array.Empty<RuntimeObservedBaselineRowDto>();
            plannerContracts ??= Array.Empty<RuntimeObservedPlannerContractDto>();
            appliedTrips ??= Array.Empty<RuntimeObservedTripDto>();
            bypassEvents ??= Array.Empty<RuntimeObservedBypassEventDto>();

            Dictionary<string, RuntimeObservedTripDto> latestTripsByRowId = appliedTrips
                .Where(trip => !string.IsNullOrEmpty(trip?.rowId))
                .GroupBy(trip => trip.rowId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.occurrenceIndex)
                        .ThenByDescending(item => item.lastUpdatedFrame)
                        .First(),
                    StringComparer.Ordinal);
            Dictionary<string, RuntimeObservedTripDto> latestTripsBySemanticKey = appliedTrips
                .Where(trip => trip != null)
                .GroupBy(trip => BuildRuntimeObservationTripSemanticKey(trip.lineId, trip.serviceKind, trip.targetMinute), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.occurrenceIndex)
                        .ThenByDescending(item => item.lastUpdatedFrame)
                        .First(),
                    StringComparer.Ordinal);

            Dictionary<string, (string PlanId, string ContractTripId)> contractTripByRowId =
                new Dictionary<string, (string PlanId, string ContractTripId)>(StringComparer.Ordinal);
            foreach (RuntimeObservedPlannerContractDto contract in plannerContracts)
            {
                foreach (DispatchPlannerChangedRowDto row in contract.changedRows ?? Array.Empty<DispatchPlannerChangedRowDto>())
                {
                    if (row == null || string.IsNullOrEmpty(row.tripId))
                        continue;

                    contractTripByRowId[BuildRuntimeObservationPlannerImportedRowId(row.tripId)] =
                        (contract.importedPlanId ?? string.Empty, row.tripId);
                }
            }

            List<RuntimeObservedTripAttainmentDto> tripResults = new List<RuntimeObservedTripAttainmentDto>(baselineRows.Length);
            foreach (RuntimeObservedBaselineRowDto baselineRow in baselineRows)
            {
                RuntimeObservedTripDto observedTrip = null;
                string matchMode = string.Empty;
                string contractPlanId = string.Empty;
                string contractTripId = string.Empty;
                if (!string.IsNullOrEmpty(baselineRow.rowId)
                    && latestTripsByRowId.TryGetValue(baselineRow.rowId, out RuntimeObservedTripDto rowMatchedTrip))
                {
                    observedTrip = rowMatchedTrip;
                    matchMode = "rowId-latest-occurrence";
                }
                else if (latestTripsBySemanticKey.TryGetValue(
                    BuildRuntimeObservationTripSemanticKey(
                        baselineRow.lineId,
                        baselineRow.serviceKind,
                        baselineRow.plannedMinute),
                    out RuntimeObservedTripDto semanticMatchedTrip))
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

                tripResults.Add(new RuntimeObservedTripAttainmentDto
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

            Dictionary<string, RuntimeObservedBaselineRowDto> baselineRowsById = baselineRows
                .Where(row => !string.IsNullOrEmpty(row?.rowId))
                .ToDictionary(row => row.rowId, row => row, StringComparer.Ordinal);
            Dictionary<string, RuntimeObservedBaselineRowDto> baselineRowsByAppliedSemanticKey = baselineRows
                .Where(row => row != null)
                .GroupBy(
                    row => BuildRuntimeObservationTripSemanticKey(row.lineId, row.serviceKind, row.plannedMinute),
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => !string.IsNullOrEmpty(item.rowId)).First(),
                    StringComparer.Ordinal);
            List<RuntimeObservedActionAttainmentDto> actionResults = new List<RuntimeObservedActionAttainmentDto>();
            foreach (RuntimeObservedPlannerContractDto contract in plannerContracts)
            {
                DispatchPlannerScheduleActionDto[] actions = contract.structuredActions ?? Array.Empty<DispatchPlannerScheduleActionDto>();
                for (int i = 0; i < actions.Length; i++)
                {
                    DispatchPlannerScheduleActionDto action = actions[i];
                    string[] tripRowIds = (action?.affectedTripIds ?? action?.tripIds ?? Array.Empty<string>())
                        .Select(BuildRuntimeObservationPlannerImportedRowId)
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
                            RuntimeObservedBypassEventDto[] matchedBypassEvents = bypassEvents
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
                            DispatchPlannerChangedRowDto[] relatedChangedRows = ResolveRuntimeObservationChangedRowsForAction(
                                contract,
                                actionType,
                                tripRowIds,
                                lineIds);
                            int matchedBaselineCount = 0;
                            bool anyShiftMismatch = false;
                            actualMinutes = 0f;
                            for (int changedRowIndex = 0; changedRowIndex < relatedChangedRows.Length; changedRowIndex++)
                            {
                                DispatchPlannerChangedRowDto changedRow = relatedChangedRows[changedRowIndex];
                                RuntimeObservedBaselineRowDto matchedBaselineRow = null;
                                string plannerRowId = BuildRuntimeObservationPlannerImportedRowId(changedRow?.tripId);
                                if (!string.IsNullOrEmpty(plannerRowId)
                                    && baselineRowsById.TryGetValue(plannerRowId, out RuntimeObservedBaselineRowDto directMatchedRow))
                                {
                                    matchedBaselineRow = directMatchedRow;
                                }
                                else
                                {
                                    int afterMinute = ParseTimeMinutes(changedRow?.afterTime);
                                    if (afterMinute >= 0)
                                    {
                                        baselineRowsByAppliedSemanticKey.TryGetValue(
                                            BuildRuntimeObservationTripSemanticKey(
                                                changedRow?.lineId,
                                                changedRow?.kind,
                                                afterMinute),
                                            out matchedBaselineRow);
                                    }
                                }

                                if (matchedBaselineRow == null)
                                    continue;

                                matchedBaselineCount++;
                                int beforeMinute = ParseTimeMinutes(changedRow?.beforeTime);
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

                    actionResults.Add(new RuntimeObservedActionAttainmentDto
                    {
                        contractPlanId = contract.importedPlanId ?? string.Empty,
                        actionId = (contract.importedPlanId ?? "contract") + "#action-" + i.ToString(),
                        actionType = actionType,
                        lineIds = lineIds,
                        tripRowIds = tripRowIds,
                        stationIds = stationIds,
                        expectedMinutes = expectedMinutes,
                        actualMinutes = PlannerMath.Round2(actualMinutes),
                        status = status,
                        reason = reason
                    });
                }
            }

            RuntimeObservedAttainmentSummaryDto summary = new RuntimeObservedAttainmentSummaryDto
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

            return new RuntimeObservedAttainmentReportDto
            {
                summary = summary,
                tripResults = tripResults.ToArray(),
                actionResults = actionResults.ToArray()
            };
        }

        private static string BuildRuntimeObservationTripSemanticKey(string lineId, string serviceKind, int minute)
        {
            return (lineId ?? string.Empty)
                + "|"
                + (string.IsNullOrEmpty(serviceKind) ? "local" : serviceKind)
                + "|"
                + minute.ToString();
        }

        private static string BuildRuntimeObservationPlannerImportedRowId(string tripId)
        {
            if (string.IsNullOrEmpty(tripId))
                return string.Empty;

            return tripId.StartsWith("planner-", StringComparison.Ordinal)
                ? tripId
                : "planner-" + tripId;
        }

        private static string BuildRuntimeObservationContractTripIdFromPlannerRowId(string rowId)
        {
            if (string.IsNullOrEmpty(rowId))
                return string.Empty;

            return rowId.StartsWith("planner-", StringComparison.Ordinal)
                ? rowId.Substring("planner-".Length)
                : rowId;
        }

        private static DispatchPlannerChangedRowDto[] ResolveRuntimeObservationChangedRowsForAction(
            RuntimeObservedPlannerContractDto contract,
            string actionType,
            string[] tripRowIds,
            string[] lineIds)
        {
            DispatchPlannerChangedRowDto[] changedRows = contract?.changedRows ?? Array.Empty<DispatchPlannerChangedRowDto>();
            if (tripRowIds != null && tripRowIds.Length > 0)
            {
                HashSet<string> tripIds = new HashSet<string>(
                    tripRowIds
                        .Select(BuildRuntimeObservationContractTripIdFromPlannerRowId)
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

            return Array.Empty<DispatchPlannerChangedRowDto>();
        }

        private static float ComputeExpectedActionMinutes(DispatchPlannerScheduleActionDto action)
        {
            if (action == null)
                return 0f;

            if (action.deltaPattern != null && action.deltaPattern.Length > 0)
                return PlannerMath.Round2(action.deltaPattern.Max(value => Math.Abs(value)));

            return PlannerMath.Round2(Math.Abs(action.deltaMinutes));
        }

        private static DispatchPlannerChangedRowDto FindContractChangedRow(RuntimeObservedPlannerContractDto contract, string contractTripId)
        {
            if (contract == null || string.IsNullOrEmpty(contractTripId))
                return null;

            return (contract.changedRows ?? Array.Empty<DispatchPlannerChangedRowDto>())
                .FirstOrDefault(row => string.Equals(row?.tripId, contractTripId, StringComparison.Ordinal));
        }

        private RuntimeObservedTrip ResolveRuntimeObservedTrip(Entity vehicle, int preferredTargetMinute, Entity preferredLine)
        {
            if (vehicle == Entity.Null
                || !m_RuntimeObservations.TripsByVehicle.TryGetValue(vehicle, out List<RuntimeObservedTrip> trips)
                || trips == null
                || trips.Count == 0)
            {
                return null;
            }

            RuntimeObservedTrip exact = trips
                .Where(trip =>
                    trip != null
                    && trip.TargetMinute == preferredTargetMinute
                    && (preferredLine == Entity.Null || trip.Line == preferredLine))
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.LastUpdatedFrame)
                .FirstOrDefault();
            if (exact != null)
                return exact;

            RuntimeObservedTrip sameLine = trips
                .Where(trip => trip != null && (preferredLine == Entity.Null || trip.Line == preferredLine))
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.LastUpdatedFrame)
                .FirstOrDefault();
            if (sameLine != null)
                return sameLine;

            return trips
                .Where(trip => trip != null)
                .OrderByDescending(trip => trip.OccurrenceIndex)
                .ThenByDescending(trip => trip.LastUpdatedFrame)
                .FirstOrDefault();
        }

        private int TryGetRuntimeObservedVehicleTargetMinute(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return -1;
            if (m_VehicleRuntime.CurrentSlot.IsCreated && m_VehicleView.TryGetSlot(vehicle, out int currentSlot))
                return currentSlot;
            if (m_VehicleRuntime.TargetMin.IsCreated && m_VehicleView.TryGetTarget(vehicle, out int targetMinute))
                return targetMinute;
            return -1;
        }

        private static string BuildRuntimeObservationLineSlotKey(Entity line, int targetMinute)
        {
            return line.Index.ToString() + ":" + targetMinute.ToString();
        }

        private string BuildRuntimeObservationPlannerStationId(Entity line, int waypointIndex)
        {
            if (line == Entity.Null || waypointIndex < 0)
                return string.Empty;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return string.Empty;

            string lineId = GetWorkbenchLineId(line);
            if (string.IsNullOrEmpty(lineId))
                return string.Empty;

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, true);
            Dictionary<Entity, int> stationOrderByStopEntity = new Dictionary<Entity, int>();
            int nextOrder = 0;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity stopEntity = ResolveWorkbenchStopEntity(waypoints[i].m_Waypoint);
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

        private static void TrimRuntimeObservationList<T>(List<T> list, int maxCount)
        {
            if (list == null || maxCount <= 0)
                return;
            while (list.Count > maxCount)
            {
                list.RemoveAt(0);
            }
        }

        private float ComputeRuntimeObservationDwellMinutes(RuntimeObservedStopEvent stopEvent)
        {
            if (stopEvent == null || stopEvent.ArrivalFrame == 0 || stopEvent.DepartureFrame == 0 || stopEvent.DepartureFrame <= stopEvent.ArrivalFrame)
                return 0f;
            return (float)Math.Round((stopEvent.DepartureFrame - stopEvent.ArrivalFrame) / SIM_FRAMES_PER_MINUTE, 1);
        }

        private float ComputeRuntimeObservationHoldMinutes(RuntimeObservedBypassEvent bypassEvent)
        {
            if (bypassEvent == null || bypassEvent.HoldStartFrame == 0)
                return 0f;

            uint endFrame = bypassEvent.HoldReleaseFrame > 0
                ? bypassEvent.HoldReleaseFrame
                : (m_SimulationSystem != null ? m_SimulationSystem.frameIndex : bypassEvent.HoldStartFrame);
            if (endFrame <= bypassEvent.HoldStartFrame)
                return 0f;

            return (float)Math.Round((endFrame - bypassEvent.HoldStartFrame) / SIM_FRAMES_PER_MINUTE, 1);
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
