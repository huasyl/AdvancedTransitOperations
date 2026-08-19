using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Entities;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Recorder
    {
        private const int MaxTripsPerDate = 4096;
        private const int MaxActiveTrips = 1024;
        private const int MaxStopsPerTrip = 256;
        private readonly Port m_Port;
        private TraceStore m_Store => m_Port.Store;

        internal Recorder(Port port)
        {
            m_Port = port ?? throw new ArgumentNullException(nameof(port));
        }

        internal string SnapshotJson()
        {
            return m_Port.Json(BuildSnapshot());
        }

        internal void Seed(string selectedLineId)
        {
            // 正式监控只在实际始发或最终漏发时创建记录，不预建全天 pending。
        }

        internal bool TickDate(DateTime currentDate)
        {
            int currentKey = ScheduleClock.DateKey(currentDate.Date);
            if (m_Store.MonitorCurrentDateKey == currentKey)
                return false;
            int previousKey = ScheduleClock.DateKey(currentDate.Date.AddDays(-1));
            bool changed = false;
            int[] existing = m_Store.DateSlots.Keys.ToArray();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == currentKey || existing[i] == previousKey)
                    continue;
                m_Store.DateSlots.Remove(existing[i]);
                changed = true;
            }
            changed |= EnsureDateSlot(currentKey);
            changed |= EnsureDateSlot(previousKey);
            m_Store.MonitorCurrentDateKey = currentKey;
            return changed;
        }

        internal string Launch(
            Entity line,
            Entity vehicle,
            AppliedMonitorRow row,
            ClockSnapshot clock,
            uint launchFrame,
            out string endedKey)
        {
            endedKey = string.Empty;
            if (row.Stops != null && row.Stops.Length > MaxStopsPerTrip)
            {
                NoteOverflow("trip-stop-capacity");
                return string.Empty;
            }
            if (!ValidRow(line, row) || vehicle == Entity.Null)
                return string.Empty;

            TickDate(clock.NowDate);
            DateTime serviceDate = ScheduleClock.ServiceDate(clock, row.SlotMinute);
            int serviceDateKey = ScheduleClock.DateKey(serviceDate);
            string key = MonitorKey(row.LineKey.ToString(), row.RowId, serviceDateKey);
            if (m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip active)
                && string.Equals(active.Key, key, StringComparison.Ordinal))
            {
                return string.Empty;
            }
            if (active != null)
            {
                endedKey = End(vehicle, launchFrame, MonitorEndReason.Relaunched);
                if (string.IsNullOrEmpty(endedKey))
                    return string.Empty;
            }

            foreach (MonitorTrip existing in m_Store.ActiveTrips.Values)
                if (string.Equals(existing?.Key, key, StringComparison.Ordinal))
                    return string.Empty;

            if (HasFinalMissed(key))
                return string.Empty;

            if (m_Store.ActiveTrips.Count >= MaxActiveTrips)
            {
                NoteOverflow("active-trip-capacity");
                return string.Empty;
            }

            RemoveArchived(key, serviceDateKey);
            MonitorTrip trip = BuildMonitorTrip(line, vehicle, row, serviceDateKey, MonitorTripState.Active, launchFrame);
            trip.LaunchFrame = launchFrame;
            trip.Stops[0].ActualDeparture = EventMinute(clock, serviceDate);
            trip.Stops[0].ActualDepartureFrame = launchFrame;
            trip.Stops[0].OpenIntervalMaxFrames = clock.ToFramesCeil(1440d);
            m_Store.ActiveTrips[vehicle] = trip;
            ClearMonitorClaim(vehicle);
            return trip.Key;
        }

        internal string MarkMissed(
            Entity line,
            AppliedMonitorRow row,
            DateTime serviceDate,
            bool final,
            uint frame)
        {
            if (row.Stops != null && row.Stops.Length > MaxStopsPerTrip)
            {
                NoteOverflow("trip-stop-capacity");
                return string.Empty;
            }
            if (!ValidRow(line, row))
                return string.Empty;

            int serviceDateKey = ScheduleClock.DateKey(serviceDate);
            string key = MonitorKey(row.LineKey.ToString(), row.RowId, serviceDateKey);
            if (ContainsMonitorKey(key))
                return string.Empty;
            if (!final && HasMonitorClaim(line, row.SlotMinute, serviceDate))
                return string.Empty;

            MonitorTrip trip = BuildMonitorTrip(
                line,
                Entity.Null,
                row,
                serviceDateKey,
                MonitorTripState.Missed,
                frame);
            Archive(trip);
            ClearMonitorSlotClaim(line, row.SlotMinute, serviceDate);
            return trip.Key;
        }

        internal string End(Entity vehicle, uint frame, MonitorEndReason reason)
        {
            if (!m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip))
                return string.Empty;
            int order = trip.NextArrivalOrder < trip.Stops.Count
                ? Math.Max(1, trip.NextArrivalOrder)
                : -1;
            if (order >= 0)
                trip.Stops[order].Cleared = true;
            trip.VisibleStopCount = order < 0
                ? trip.Stops.Count
                : Math.Min(trip.Stops.Count, order + 1);
            trip.State = MonitorTripState.Cleared;
            trip.EndReason = reason;
            trip.UpdatedFrame = frame;
            Archive(trip);
            return trip.Key;
        }

        internal string SuppressPlan(Entity vehicle, string stopSig, uint frame)
        {
            if (string.IsNullOrEmpty(stopSig)
                || !m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip)
                || string.Equals(trip.StopSig, stopSig, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            trip.SuppressPlanFrom = Math.Min(trip.SuppressPlanFrom, trip.NextArrivalOrder);
            trip.UpdatedFrame = frame;
            return trip.Key;
        }

        internal string ReprojectPlan(
            Entity vehicle,
            string stopSig,
            int[] waypointIndices,
            uint frame)
        {
            if (string.IsNullOrEmpty(stopSig)
                || !m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip))
            {
                return string.Empty;
            }
            if (!string.Equals(trip.StopSig, stopSig, StringComparison.Ordinal))
                return SuppressPlan(vehicle, stopSig, frame);
            if (waypointIndices == null || waypointIndices.Length != trip.Stops.Count)
            {
                NoteIssue("monitor-layout-projection-mismatch");
                trip.SuppressPlanFrom = Math.Min(trip.SuppressPlanFrom, trip.NextArrivalOrder);
                trip.UpdatedFrame = frame;
                return trip.Key;
            }

            bool changed = false;
            for (int i = 0; i < trip.Stops.Count; i++)
            {
                if (trip.Stops[i].WaypointIndex == waypointIndices[i])
                    continue;
                trip.Stops[i].WaypointIndex = waypointIndices[i];
                changed = true;
            }
            if (!changed)
                return string.Empty;
            trip.UpdatedFrame = frame;
            return trip.Key;
        }

        internal void ReleaseLinePlan(Entity line, uint frame)
        {
            foreach (MonitorTrip trip in m_Store.ActiveTrips.Values)
            {
                if (trip == null || trip.Line != line)
                    continue;
                trip.SuppressPlanFrom = Math.Min(trip.SuppressPlanFrom, trip.NextArrivalOrder);
                trip.UpdatedFrame = frame;
            }
        }

        internal bool TryVehicleTimes(
            Entity vehicle,
            out int currentWaypointIndex,
            out int nextWaypointIndex,
            out int nextArrival,
            out int plannedArrival,
            out int actualArrival,
            out int plannedDeparture)
        {
            currentWaypointIndex = -1;
            nextWaypointIndex = -1;
            nextArrival = -1;
            plannedArrival = -1;
            actualArrival = -1;
            plannedDeparture = -1;
            if (vehicle == Entity.Null
                || !m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip)
                || trip == null
                || trip.State != MonitorTripState.Active
                || trip.Stops.Count == 0)
            {
                return false;
            }

            int currentOrder = Math.Min(trip.NextArrivalOrder - 1, trip.Stops.Count - 1);
            bool hasCurrent = false;
            if (currentOrder >= 1 && currentOrder < trip.SuppressPlanFrom)
            {
                MonitorStop current = trip.Stops[currentOrder];
                if (current.ActualArrival >= 0
                    && current.ActualDeparture < 0)
                {
                    currentWaypointIndex = current.WaypointIndex;
                    plannedArrival = current.PlannedArrival;
                    actualArrival = current.ActualArrival;
                    plannedDeparture = current.PlannedDeparture;
                    hasCurrent = true;
                }
            }

            int nextOrder = Math.Max(1, trip.NextArrivalOrder);
            if (nextOrder >= trip.Stops.Count || nextOrder >= trip.SuppressPlanFrom)
                return hasCurrent;

            MonitorStop next = trip.Stops[nextOrder];
            if (next.PlannedArrival < 0)
                return hasCurrent;

            nextWaypointIndex = next.WaypointIndex;
            nextArrival = next.PlannedArrival;
            return true;
        }

        internal IEnumerable<MonitorTrip> ActiveMonitorTrips => m_Store.ActiveTrips.Values;

        internal IEnumerable<MonitorDateSlot> MonitorDateSlots => m_Store.DateSlots.Values;

        internal bool MonitorOverflowed => m_Store.MonitorOverflowed;

        internal string MonitorOverflowReason => m_Store.MonitorOverflowReason;

        internal int MonitorOverflowCount => m_Store.MonitorOverflowCount;

        internal bool MonitorDataComplete => !m_Store.MonitorOverflowed;

        internal int MonitorDroppedTripCount =>
            m_Store.MonitorOverflowed ? m_Store.MonitorOverflowCount : 0;

        internal string MonitorIssueCode => m_Store.MonitorIssueCode;

        internal int MonitorIssueCount => m_Store.MonitorIssueCount;

        internal bool MonitorClaimsRestored => m_Store.MonitorClaimsRestored;

        internal bool TryMonitor(string key, out MonitorTrip trip, out bool active)
        {
            foreach (MonitorTrip value in m_Store.ActiveTrips.Values)
            {
                if (string.Equals(value?.Key, key, StringComparison.Ordinal))
                {
                    trip = value;
                    active = true;
                    return true;
                }
            }
            foreach (MonitorDateSlot slot in m_Store.DateSlots.Values)
            {
                if (slot.Trips.TryGetValue(key, out trip))
                {
                    active = false;
                    return true;
                }
            }
            trip = null;
            active = false;
            return false;
        }

        internal bool RestoreMonitor(MonitorTrip trip, bool active)
        {
            if (trip == null
                || string.IsNullOrEmpty(trip.Key)
                || string.IsNullOrEmpty(trip.LineKey)
                || string.IsNullOrEmpty(trip.RowId)
                || trip.Stops.Count == 0
                || trip.Stops.Count > MaxStopsPerTrip)
            {
                if (trip != null && trip.Stops.Count > MaxStopsPerTrip)
                    NoteOverflow("trip-stop-capacity");
                return false;
            }

            trip.NextArrivalOrder = Math.Max(1, Math.Min(trip.NextArrivalOrder, trip.Stops.Count));
            trip.VisibleStopCount = Math.Max(1, Math.Min(trip.VisibleStopCount, trip.Stops.Count));
            trip.SuppressPlanFrom = Math.Max(0, Math.Min(trip.SuppressPlanFrom, int.MaxValue));

            if (active)
            {
                if (trip.Vehicle == Entity.Null)
                    return false;
                if (m_Store.ActiveTrips.Count >= MaxActiveTrips)
                {
                    NoteOverflow("active-trip-capacity");
                    return false;
                }
                m_Store.ActiveTrips[trip.Vehicle] = trip;
                return true;
            }

            return Archive(trip);
        }

        internal void RestoreDateSlot(int dateKey)
        {
            if (dateKey > 0)
                EnsureDateSlot(dateKey);
        }

        internal void ClearMonitor()
        {
            m_Store.ActiveTrips.Clear();
            m_Store.DateSlots.Clear();
            m_Store.MonitorCurrentDateKey = 0;
            m_Store.MonitorOverflowed = false;
            m_Store.MonitorOverflowReason = string.Empty;
            m_Store.MonitorOverflowCount = 0;
            m_Store.MonitorIssueCode = string.Empty;
            m_Store.MonitorIssueCount = 0;
            m_Store.MonitorClaims.Clear();
            m_Store.VehicleMonitorClaims.Clear();
            m_Store.MonitorClaimsRestored = false;
        }

        internal void RestoreMonitorClaims(
            IReadOnlyList<MonitorClaimSeed> seeds,
            ClockSnapshot clock)
        {
            if (m_Store.MonitorClaimsRestored)
                return;

            if (seeds != null)
            {
                for (int i = 0; i < seeds.Count; i++)
                {
                    MonitorClaimSeed seed = seeds[i];
                    if (seed.Vehicle == Entity.Null
                        || seed.Line == Entity.Null
                        || seed.SlotMinute < 0
                        || seed.SlotMinute >= 1440)
                    {
                        continue;
                    }

                    int serviceDateKey = ScheduleClock.DateKey(
                        ScheduleClock.MonitorOccurrenceDate(clock, seed.SlotMinute));
                    MonitorSlotKey key = new MonitorSlotKey(seed.Line, seed.SlotMinute, serviceDateKey);
                    if (m_Store.MonitorClaims.ContainsKey(key)
                        || m_Store.VehicleMonitorClaims.ContainsKey(seed.Vehicle))
                    {
                        continue;
                    }

                    m_Store.MonitorClaims[key] = new MonitorClaim
                    {
                        Vehicle = seed.Vehicle
                    };
                    m_Store.VehicleMonitorClaims[seed.Vehicle] = key;
                }
            }

            m_Store.MonitorClaimsRestored = true;
        }

        private bool EnsureDateSlot(int dateKey)
        {
            if (dateKey <= 0 || m_Store.DateSlots.ContainsKey(dateKey))
                return false;
            m_Store.DateSlots[dateKey] = new MonitorDateSlot { DateKey = dateKey };
            return true;
        }

        private static bool ValidRow(Entity line, AppliedMonitorRow row)
        {
            return line != Entity.Null
                && !row.LineKey.IsEmpty
                && !string.IsNullOrEmpty(row.RowId)
                && !string.IsNullOrEmpty(row.StopSig)
                && row.SlotMinute >= 0
                && row.SlotMinute < 1440
                && row.Stops != null
                && row.Stops.Length > 0
                && row.Stops.Length <= MaxStopsPerTrip;
        }

        private static string MonitorKey(string lineKey, string rowId, int serviceDateKey)
        {
            return (lineKey ?? string.Empty)
                + "|"
                + (rowId ?? string.Empty)
                + "|"
                + serviceDateKey.ToString(CultureInfo.InvariantCulture);
        }

        private MonitorTrip BuildMonitorTrip(
            Entity line,
            Entity vehicle,
            AppliedMonitorRow row,
            int serviceDateKey,
            MonitorTripState state,
            uint frame)
        {
            MonitorTrip trip = new MonitorTrip
            {
                Key = MonitorKey(row.LineKey.ToString(), row.RowId, serviceDateKey),
                LineKey = row.LineKey.ToString(),
                LineId = row.LineId,
                RowId = row.RowId,
                ServiceKind = row.ServiceKind,
                StopSig = row.StopSig,
                Line = line,
                Vehicle = vehicle,
                ServiceDateKey = serviceDateKey,
                SlotMinute = row.SlotMinute,
                State = state,
                VisibleStopCount = row.Stops.Length,
                UpdatedFrame = frame
            };
            for (int i = 0; i < row.Stops.Length; i++)
            {
                AppliedMonitorStop stop = row.Stops[i];
                trip.Stops.Add(new MonitorStop
                {
                    StopKey = stop.StopKey,
                    Station = stop.Station,
                    WaypointIndex = stop.WaypointIndex,
                    PlannedArrival = stop.Arrive,
                    PlannedDeparture = stop.Depart
                });
            }
            return trip;
        }

        private bool ContainsMonitorKey(string key)
        {
            foreach (MonitorTrip trip in m_Store.ActiveTrips.Values)
                if (string.Equals(trip?.Key, key, StringComparison.Ordinal))
                    return true;
            foreach (MonitorDateSlot slot in m_Store.DateSlots.Values)
                if (slot.Trips.ContainsKey(key))
                    return true;
            return false;
        }

        private bool HasFinalMissed(string key)
        {
            foreach (MonitorDateSlot slot in m_Store.DateSlots.Values)
            {
                if (slot != null
                    && slot.Trips.TryGetValue(key, out MonitorTrip trip)
                    && trip != null
                    && trip.State == MonitorTripState.Missed)
                {
                    return true;
                }
            }

            return false;
        }

        private void RemoveArchived(string key, int dateKey)
        {
            if (m_Store.DateSlots.TryGetValue(dateKey, out MonitorDateSlot slot))
                slot.Trips.Remove(key);
        }

        private bool Archive(MonitorTrip trip)
        {
            if (trip == null)
                return false;

            if (trip.Vehicle != Entity.Null
                && m_Store.ActiveTrips.TryGetValue(trip.Vehicle, out MonitorTrip active)
                && ReferenceEquals(active, trip))
            {
                m_Store.ActiveTrips.Remove(trip.Vehicle);
            }
            ClearMonitorClaim(trip.Vehicle);
            if (!CanArchive(trip))
                return false;

            MonitorDateSlot slot = m_Store.DateSlots[trip.ServiceDateKey];
            slot.Trips[trip.Key] = trip;
            return true;
        }

        private bool CanArchive(MonitorTrip trip)
        {
            if (trip == null)
                return false;
            if (!m_Store.DateSlots.TryGetValue(trip.ServiceDateKey, out MonitorDateSlot slot))
            {
                NoteOverflow("expired-date-slot");
                return false;
            }
            if (!slot.Trips.ContainsKey(trip.Key) && slot.Trips.Count >= MaxTripsPerDate)
            {
                NoteOverflow("date-trip-capacity");
                return false;
            }
            return true;
        }

        private void NoteOverflow(string reason)
        {
            m_Store.MonitorOverflowed = true;
            m_Store.MonitorOverflowReason = reason ?? string.Empty;
            if (m_Store.MonitorOverflowCount < int.MaxValue)
                m_Store.MonitorOverflowCount++;
            m_Store.MonitorIssueCode = m_Store.MonitorOverflowReason;
            if (m_Store.MonitorIssueCount < int.MaxValue)
                m_Store.MonitorIssueCount++;
            m_Port.Log("[ServiceMonitorOverflow] reason=" + m_Store.MonitorOverflowReason
                + " count=" + m_Store.MonitorOverflowCount);
        }

        private void NoteIssue(string reason)
        {
            m_Store.MonitorIssueCode = reason ?? string.Empty;
            if (m_Store.MonitorIssueCount < int.MaxValue)
                m_Store.MonitorIssueCount++;
            m_Port.Log("[ServiceMonitorIssue] reason=" + m_Store.MonitorIssueCode
                + " count=" + m_Store.MonitorIssueCount);
        }

        private static int EventMinute(ClockSnapshot clock, DateTime serviceDate)
        {
            long days = (clock.NowDate.Date - serviceDate.Date).Days;
            long minute = days * 1440L + clock.NowMinute;
            if (minute < int.MinValue)
                return int.MinValue;
            if (minute > int.MaxValue)
                return int.MaxValue;
            return (int)minute;
        }

        private bool RecordMonitorStop(
            Entity vehicle,
            Entity station,
            string stopKey,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            ClockSnapshot clock,
            uint frame,
            out MonitorStopResult result)
        {
            result = default;
            if (!m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip))
                return false;
            if (trip.LastFactFrame == frame
                && trip.LastFactArrival == arrival
                && string.Equals(trip.LastFactStopKey, stopKey, StringComparison.Ordinal))
            {
                TraceMonitor(trip, vehicle, frame, arrival, stopKey, waypointIndex, null, -1, "reject", "duplicate-fact", trip.SuppressPlanFrom == int.MaxValue);
                return false;
            }
            DateTime serviceDate = ParseDateKey(trip.ServiceDateKey);
            int minute = EventMinute(clock, serviceDate);
            bool exactLayout = trip.SuppressPlanFrom == int.MaxValue;
            if (arrival && trip.State == MonitorTripState.Active && isOrigin)
            {
                bool originMatches = MatchesMonitorStop(trip.Stops[0], stopKey, station, waypointIndex, false);
                if (originMatches)
                {
                    trip.Stops[0].ActualArrival = minute;
                    trip.Stops[0].ActualArrivalFrame = frame;
                }
                TraceMonitor(trip, vehicle, frame, true, stopKey, waypointIndex, trip.Stops[0], 0, "accept", originMatches ? "origin-complete" : "origin-mismatch-complete", false);
                trip.NextArrivalOrder = trip.Stops.Count;
                trip.State = MonitorTripState.Completed;
                trip.UpdatedFrame = frame;
                Archive(trip);
                result = new MonitorStopResult(
                    true,
                    trip.Line,
                    trip.ServiceDateKey,
                    trip.Key,
                    originMatches ? BuildClosingSample(trip) : default);
                return true;
            }

            if (arrival)
            {
                int matched = Math.Max(1, trip.NextArrivalOrder);
                while (matched < trip.Stops.Count
                    && !MatchesMonitorStop(
                        trip.Stops[matched],
                        stopKey,
                        station,
                        waypointIndex,
                        exactLayout))
                {
                    if (exactLayout)
                    {
                        TraceMonitor(trip, vehicle, frame, true, stopKey, waypointIndex, trip.Stops[matched], matched, "reject", "arrival-layout-mismatch", true);
                        return false;
                    }
                    matched++;
                }
                if (matched >= trip.Stops.Count || trip.Stops[matched].ActualArrival >= 0)
                {
                    MonitorStop expected = matched < trip.Stops.Count ? trip.Stops[matched] : null;
                    TraceMonitor(trip, vehicle, frame, true, stopKey, waypointIndex, expected, matched, "reject", matched >= trip.Stops.Count ? "arrival-after-plan" : "arrival-duplicate", exactLayout);
                    return false;
                }
                trip.Stops[matched].ActualArrival = minute;
                trip.Stops[matched].ActualArrivalFrame = frame;
                trip.NextArrivalOrder = matched + 1;
                TraceMonitor(trip, vehicle, frame, true, stopKey, waypointIndex, trip.Stops[matched], matched, "accept", "arrival", exactLayout);
                result = new MonitorStopResult(
                    true,
                    trip.Line,
                    trip.ServiceDateKey,
                    trip.Key,
                    BuildIntervalSample(trip, matched, exactLayout));
            }
            else
            {
                int matched = Math.Min(trip.NextArrivalOrder - 1, trip.Stops.Count - 1);
                if (matched < 1)
                {
                    TraceMonitor(trip, vehicle, frame, false, stopKey, waypointIndex, null, matched, "reject", "departure-without-arrival", exactLayout);
                    return false;
                }
                MonitorStop stop = trip.Stops[matched];
                if (stop.ActualArrival < 0
                    || stop.ActualDeparture >= 0
                    || !MatchesMonitorStop(
                        stop,
                        stopKey,
                        station,
                        waypointIndex,
                        exactLayout))
                {
                    string reason = stop.ActualArrival < 0
                        ? "departure-before-arrival"
                        : stop.ActualDeparture >= 0
                            ? "departure-duplicate"
                            : "departure-layout-mismatch";
                    TraceMonitor(trip, vehicle, frame, false, stopKey, waypointIndex, stop, matched, "reject", reason, exactLayout);
                    return false;
                }
                trip.Stops[matched].ActualDeparture = minute;
                trip.Stops[matched].ActualDepartureFrame = frame;
                trip.Stops[matched].OpenIntervalMaxFrames = clock.ToFramesCeil(1440d);
                TraceMonitor(trip, vehicle, frame, false, stopKey, waypointIndex, stop, matched, "accept", "departure", exactLayout);
            }
            trip.UpdatedFrame = frame;
            trip.LastFactStopKey = stopKey;
            trip.LastFactFrame = frame;
            trip.LastFactArrival = arrival;
            if (!arrival)
            {
                result = new MonitorStopResult(
                    true,
                    trip.Line,
                    trip.ServiceDateKey,
                    trip.Key,
                    default);
            }
            return true;
        }

        internal bool Skip(
            Entity vehicle,
            Entity line,
            Entity station,
            string stopKey,
            int waypointIndex,
            ClockSnapshot clock,
            uint frame,
            out MonitorStopResult result)
        {
            result = default;
            if (!m_Store.ActiveTrips.TryGetValue(vehicle, out MonitorTrip trip)
                || trip.Line != line
                || trip.State != MonitorTripState.Active)
            {
                return false;
            }

            int matched = Math.Max(1, trip.NextArrivalOrder);
            if (matched >= trip.Stops.Count
                || !MatchesMonitorStop(
                    trip.Stops[matched],
                    stopKey,
                    station,
                    waypointIndex,
                    true)
                || trip.Stops[matched].ActualArrival >= 0)
            {
                return false;
            }

            DateTime serviceDate = ParseDateKey(trip.ServiceDateKey);
            MonitorStop stop = trip.Stops[matched];
            stop.Skipped = true;
            stop.ActualArrival = EventMinute(clock, serviceDate);
            stop.ActualArrivalFrame = frame;
            stop.ActualDeparture = -1;
            stop.ActualDepartureFrame = 0u;
            stop.OpenIntervalMaxFrames = 0u;
            trip.NextArrivalOrder = matched + 1;
            trip.UpdatedFrame = frame;
            result = new MonitorStopResult(
                true,
                trip.Line,
                trip.ServiceDateKey,
                trip.Key,
                default);
            return true;
        }

        private static MonitorIntervalSample BuildIntervalSample(
            MonitorTrip trip,
            int toOrder,
            bool exactLayout)
        {
            int fromOrder = toOrder - 1;
            if (!exactLayout
                || trip == null
                || string.IsNullOrEmpty(trip.StopSig)
                || fromOrder < 0
                || toOrder >= trip.Stops.Count
                || trip.Stops[fromOrder].ActualDeparture < 0
                || trip.Stops[toOrder].ActualArrival < 0
                || !TryIntervalFrames(
                    trip.Stops[fromOrder].ActualDepartureFrame,
                    trip.Stops[toOrder].ActualArrivalFrame,
                    trip.Stops[fromOrder].OpenIntervalMaxFrames,
                    out uint frames))
            {
                return default;
            }

            return new MonitorIntervalSample(
                trip.Line,
                trip.StopSig,
                trip.Stops.Count,
                fromOrder,
                toOrder,
                frames,
                false);
        }

        private static MonitorIntervalSample BuildClosingSample(MonitorTrip trip)
        {
            if (trip == null
                || string.IsNullOrEmpty(trip.StopSig)
                || trip.Stops.Count < 2
                || trip.SuppressPlanFrom != int.MaxValue
                || trip.Stops[trip.Stops.Count - 1].ActualDeparture < 0
                || trip.Stops[0].ActualArrival < 0
                || !TryIntervalFrames(
                    trip.Stops[trip.Stops.Count - 1].ActualDepartureFrame,
                    trip.Stops[0].ActualArrivalFrame,
                    trip.Stops[trip.Stops.Count - 1].OpenIntervalMaxFrames,
                    out uint frames))
            {
                return default;
            }

            int fromOrder = trip.Stops.Count - 1;
            return new MonitorIntervalSample(
                trip.Line,
                trip.StopSig,
                trip.Stops.Count,
                fromOrder,
                0,
                frames,
                true);
        }

        private static bool TryIntervalFrames(
            uint startFrame,
            uint endFrame,
            uint maxFrames,
            out uint frames)
        {
            frames = unchecked(endFrame - startFrame);
            return maxFrames > 0u
                && frames > 0u
                && frames < 0x80000000u
                && frames <= maxFrames;
        }

        private void TraceMonitor(
            MonitorTrip trip,
            Entity vehicle,
            uint frame,
            bool arrival,
            string stopKey,
            int waypointIndex,
            MonitorStop expected,
            int expectedOrder,
            string outcome,
            string reason,
            bool exactLayout)
        {
            if (!RtLog.VerboseEnabled)
                return;

            RtLog.Diagnostics(
                "[StopTraceMonitor] frame=" + frame
                + " vehicle=" + vehicle.Index
                + " trip=" + (trip?.Key ?? string.Empty)
                + " event=" + (arrival ? "arrival" : "departure")
                + " outcome=" + outcome
                + " reason=" + reason
                + " actualKey=" + (stopKey ?? string.Empty)
                + " actualWp=" + waypointIndex
                + " expectedOrder=" + expectedOrder
                + " expectedKey=" + (expected?.StopKey ?? string.Empty)
                + " expectedWp=" + (expected?.WaypointIndex ?? -1)
                + " nextOrder=" + (trip?.NextArrivalOrder ?? -1)
                + " exact=" + (exactLayout ? 1 : 0));
        }

        private static bool MatchesMonitorStop(
            MonitorStop stop,
            string stopKey,
            Entity station,
            int waypointIndex,
            bool exactLayout)
        {
            if (stop == null
                || string.IsNullOrEmpty(stopKey)
                || !string.Equals(stop.StopKey, stopKey, StringComparison.Ordinal))
            {
                return false;
            }
            if (!exactLayout)
                return true;
            if (stop.WaypointIndex >= 0 || waypointIndex >= 0)
            {
                return stop.WaypointIndex >= 0
                    && waypointIndex >= 0
                    && stop.WaypointIndex == waypointIndex;
            }
            return stop.Station != Entity.Null
                && station != Entity.Null
                && stop.Station == station;
        }

        private static DateTime ParseDateKey(int dateKey)
        {
            int year = dateKey / 10000;
            int month = dateKey / 100 % 100;
            int day = dateKey % 100;
            try
            {
                return new DateTime(year, month, day);
            }
            catch
            {
                return DateTime.MinValue.Date;
            }
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

        private void RecordMonitorClaim(
            Entity line,
            Entity vehicle,
            int slotMinute,
            uint frame)
        {
            if (line == Entity.Null || vehicle == Entity.Null || slotMinute < 0)
                return;

            ClearMonitorClaim(vehicle);
            ClockSnapshot clock = m_Port.ClockSnapshot();
            int serviceDateKey = ScheduleClock.DateKey(
                ScheduleClock.MonitorOccurrenceDate(clock, slotMinute));
            MonitorSlotKey key = new MonitorSlotKey(line, slotMinute, serviceDateKey);
            m_Store.MonitorClaims[key] = new MonitorClaim
            {
                Vehicle = vehicle
            };
            m_Store.VehicleMonitorClaims[vehicle] = key;
        }

        private void ClearMonitorClaim(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !m_Store.VehicleMonitorClaims.TryGetValue(vehicle, out MonitorSlotKey key))
            {
                return;
            }

            m_Store.VehicleMonitorClaims.Remove(vehicle);
            if (m_Store.MonitorClaims.TryGetValue(key, out MonitorClaim claim)
                && claim != null
                && claim.Vehicle == vehicle)
            {
                m_Store.MonitorClaims.Remove(key);
            }
        }

        private void ClearMonitorSlotClaim(Entity line, int slotMinute, DateTime serviceDate)
        {
            MonitorSlotKey key = new MonitorSlotKey(
                line,
                slotMinute,
                ScheduleClock.DateKey(serviceDate));
            if (!m_Store.MonitorClaims.TryGetValue(key, out MonitorClaim claim)
                || claim == null)
            {
                return;
            }

            m_Store.MonitorClaims.Remove(key);
            if (m_Store.VehicleMonitorClaims.TryGetValue(claim.Vehicle, out MonitorSlotKey vehicleKey)
                && vehicleKey.Equals(key))
            {
                m_Store.VehicleMonitorClaims.Remove(claim.Vehicle);
            }
        }

        private bool HasMonitorClaim(Entity line, int slotMinute, DateTime serviceDate)
        {
            return m_Store.MonitorClaims.ContainsKey(new MonitorSlotKey(
                line,
                slotMinute,
                ScheduleClock.DateKey(serviceDate)));
        }

        internal void TargetBound(Entity line, Entity vehicle, int targetMinute, uint nowFrame, string reasonCode)
        {
            if (line == Entity.Null || vehicle == Entity.Null || targetMinute < 0)
                return;

            RecordMonitorClaim(line, vehicle, targetMinute, nowFrame);
            if (m_Store.Session == null)
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

        internal bool Stop(
            Entity vehicle,
            Entity line,
            Entity station,
            string stopKey,
            ResolvedStopKind kind,
            int waypointIndex,
            bool isOrigin,
            bool arrival,
            string clockTime,
            ClockSnapshot clock,
            uint frame,
            out MonitorStopResult result)
        {
            return RecordMonitorStop(
                vehicle,
                station,
                stopKey,
                waypointIndex,
                isOrigin,
                arrival,
                clock,
                frame,
                out result);
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
                TripDto[] formalTrips = FormalMonitorTrips()
                    .Select(BuildMonitorTripDto)
                    .ToArray();
                StopDto[] emptyStops = Array.Empty<StopDto>();
                BypassDto[] emptyBypassEvents = Array.Empty<BypassDto>();
                CorridorDto[] emptyCorridors = Array.Empty<CorridorDto>();
                return new SnapshotDto
                {
                    schemaVersion = 2,
                    snapshotId = string.Empty,
                    status = MonitorSnapshotStatus(formalTrips.Length > 0 ? "active" : "empty"),
                    generatedAtFrame = m_Port.Frame(),
                    appliedTrips = formalTrips,
                    stopEvents = emptyStops,
                    bypassEvents = emptyBypassEvents,
                    corridorPassages = emptyCorridors,
                    baselineRows = baselineRows,
                    plannerContracts = plannerContracts,
                    attainmentReport = BuildReport(
                        baselineRows,
                        plannerContracts,
                        formalTrips,
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
                status = MonitorSnapshotStatus(session.Status),
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

        private IEnumerable<MonitorTrip> FormalMonitorTrips()
        {
            foreach (MonitorTrip trip in m_Store.ActiveTrips.Values)
                if (trip != null)
                    yield return trip;
            foreach (MonitorDateSlot slot in m_Store.DateSlots.Values)
                foreach (MonitorTrip trip in slot.Trips.Values)
                    if (trip != null)
                        yield return trip;
        }

        private string MonitorSnapshotStatus(string normalStatus)
        {
            return m_Store.MonitorOverflowed ? "overflow" : normalStatus;
        }

        private TripDto BuildMonitorTripDto(MonitorTrip trip)
        {
            int actualMinute = trip.Stops.Count > 0
                && trip.Stops[0].ActualDeparture >= 0
                ? trip.Stops[0].ActualDeparture % 1440
                : -1;
            int delta = actualMinute >= 0
                ? NormalizeMinuteDelta(actualMinute - trip.SlotMinute)
                : 0;
            return new TripDto
            {
                tripObservationId = trip.Key,
                state = trip.State.ToString().ToLowerInvariant(),
                lineId = trip.LineId,
                rowId = trip.RowId,
                serviceKind = trip.ServiceKind,
                plannedTime = m_Port.Slot(trip.SlotMinute),
                serviceDate = FormatDateKey(trip.ServiceDateKey),
                serviceDayIndex = 0,
                occurrenceIndex = 1,
                actualDepartureTime = actualMinute >= 0 ? m_Port.Slot(actualMinute) : string.Empty,
                targetMinute = trip.SlotMinute,
                actualDepartureMinute = actualMinute,
                deltaMinutes = delta,
                vehicleIndex = trip.Vehicle != Entity.Null ? trip.Vehicle.Index : -1,
                launchFrame = trip.LaunchFrame,
                bindingConfidence = trip.State == MonitorTripState.Missed ? "final-missed" : "vehicle-launch",
                reasonCode = trip.State.ToString().ToLowerInvariant(),
                lastUpdatedFrame = trip.UpdatedFrame
            };
        }

        private static string FormatDateKey(int dateKey)
        {
            DateTime date = ParseDateKey(dateKey);
            return date == DateTime.MinValue.Date
                ? string.Empty
                : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
            return m_Port.TargetMinute(vehicle);
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
            return (float)Math.Round(m_Port.ClockSnapshot().ToMinutes(stopEvent.DepartureFrame - stopEvent.ArrivalFrame), 1);
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

            return (float)Math.Round(m_Port.ClockSnapshot().ToMinutes(endFrame - bypassEvent.HoldStartFrame), 1);
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
