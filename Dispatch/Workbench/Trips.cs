using System;
using System.Collections.Generic;
using System.Linq;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Observation;
using RapidTransitMod.Dispatch.Scheduling;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class TripPort
    {
        private readonly Func<IEnumerable<MonitorTrip>> m_ActiveTrips;
        private readonly Func<IEnumerable<MonitorDateSlot>> m_DateSlots;
        private readonly Func<int, string> m_Slot;
        private readonly Func<bool> m_DataComplete;
        private readonly Func<int> m_DroppedTripCount;
        private readonly Func<bool> m_PersistenceHealthy;
        private readonly Func<string> m_LastIssueCode;
        private readonly Func<int> m_IssueCount;

        internal TripPort(
            Func<IEnumerable<MonitorTrip>> activeTrips,
            Func<IEnumerable<MonitorDateSlot>> dateSlots,
            Func<int, string> slot,
            Func<bool> dataComplete = null,
            Func<int> droppedTripCount = null,
            Func<bool> persistenceHealthy = null,
            Func<string> lastIssueCode = null,
            Func<int> issueCount = null)
        {
            m_ActiveTrips = activeTrips ?? throw new ArgumentNullException(nameof(activeTrips));
            m_DateSlots = dateSlots ?? throw new ArgumentNullException(nameof(dateSlots));
            m_Slot = slot ?? throw new ArgumentNullException(nameof(slot));
            m_DataComplete = dataComplete ?? (() => true);
            m_DroppedTripCount = droppedTripCount ?? (() => 0);
            m_PersistenceHealthy = persistenceHealthy ?? (() => true);
            m_LastIssueCode = lastIssueCode ?? (() => string.Empty);
            m_IssueCount = issueCount ?? (() => 0);
        }

        internal IEnumerable<MonitorTrip> ActiveTrips() => m_ActiveTrips();
        internal IEnumerable<MonitorDateSlot> DateSlots() => m_DateSlots();
        internal string Slot(int minute) => minute < 0 ? null : m_Slot(minute % 1440);
        internal bool DataComplete() => m_DataComplete();
        internal int DroppedTripCount() => m_DroppedTripCount();
        internal bool PersistenceHealthy() => m_PersistenceHealthy();
        internal string LastIssueCode() => m_LastIssueCode() ?? string.Empty;
        internal int IssueCount() => m_IssueCount();
    }

    internal sealed class Trips
    {
        private const int MaxMonitorHeaders = 256;
        private const int MaxMonitorDetails = 32;
        private readonly TripPort m_Port;

        internal Trips(TripPort port)
        {
            m_Port = port ?? throw new ArgumentNullException(nameof(port));
        }

        internal List<DispatchWorkbenchTripDto> Build(
            WorkbenchLineRuntime active,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            return BuildFormal(active, draft);
        }

        private List<DispatchWorkbenchTripDto> BuildFormal(
            WorkbenchLineRuntime active,
            DispatchWorkbenchDraftState draft)
        {
            List<DispatchWorkbenchTripDto> result = new List<DispatchWorkbenchTripDto>();
            if (active == null)
                return result;

            IEnumerable<MonitorTrip> trips = m_Port.ActiveTrips()
                .Concat(m_Port.DateSlots().SelectMany(slot => slot.Trips.Values));
            foreach (MonitorTrip trip in trips
                .Where(item => item != null && string.Equals(item.LineId, active.Id, StringComparison.Ordinal))
                .OrderBy(item => item.ServiceDateKey)
                .ThenBy(item => item.SlotMinute))
            {
                int count = math.clamp(trip.VisibleStopCount, 0, trip.Stops.Count);
                DispatchWorkbenchTripStopDto[] stops = new DispatchWorkbenchTripStopDto[count];
                for (int i = 0; i < count; i++)
                {
                    MonitorStop stop = trip.Stops[i];
                    stops[i] = new DispatchWorkbenchTripStopDto
                    {
                        stationId = stop.StopKey,
                        time = m_Port.Slot(stop.ActualDeparture >= 0 ? stop.ActualDeparture : stop.ActualArrival),
                        arrivalTime = m_Port.Slot(stop.ActualArrival),
                        departureTime = m_Port.Slot(stop.ActualDeparture),
                        stopType = i == 0 ? "origin" : (stop.Cleared ? "cleared" : "normal")
                    };
                }
                result.Add(new DispatchWorkbenchTripDto
                {
                    id = trip.Key,
                    lineId = trip.LineId,
                    kind = string.IsNullOrEmpty(trip.ServiceKind) ? "local" : trip.ServiceKind,
                    depart = m_Port.Slot(trip.SlotMinute) ?? "--:--",
                    stops = stops
                });
            }
            return result;
        }

        internal DispatchWorkbenchMonitorListResponseDto BuildMonitorHeaders(
            DispatchWorkbenchMonitorListRequestDto request,
            ClockSnapshot clock)
        {
            List<DispatchWorkbenchMonitorTripHeaderDto> headers = new List<DispatchWorkbenchMonitorTripHeaderDto>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            bool hasLineTrips = false;
            int currentServiceDateKey = ScheduleClock.DateKey(clock.NowDate.Date);
            bool validRequest = request != null
                && (request.dayOffset == 0 || request.dayOffset == -1)
                && !string.IsNullOrWhiteSpace(request.lineId)
                && request.startMinute >= 0
                && request.startMinute < 1440
                && request.endMinute > request.startMinute
                && request.endMinute < 1440
                && request.limit > 0
                && request.limit <= MaxMonitorHeaders;
            int serviceDateKey = validRequest
                ? ScheduleClock.DateKey(clock.NowDate.Date.AddDays(request.dayOffset))
                : 0;
            if (validRequest)
            {
                foreach (MonitorTrip trip in MonitorTrips())
                {
                    if (trip == null
                        || trip.ServiceDateKey != serviceDateKey
                        || !string.Equals(trip.LineId, request.lineId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hasLineTrips = true;
                    if (string.IsNullOrEmpty(trip.Key) || !seen.Add(trip.Key))
                        continue;
                    if (MatchesMonitorFilter(trip, request))
                        headers.Add(BuildHeader(trip));
                }
            }

            headers = headers
                .OrderBy(item => item.plannedStartMinute)
                .ThenBy(item => item.tripKey, StringComparer.Ordinal)
                .ToList();
            bool truncated = headers.Count > (request?.limit ?? 0);
            if (truncated)
                headers = headers.Take(request.limit).ToList();
            return new DispatchWorkbenchMonitorListResponseDto
            {
                success = validRequest,
                error = validRequest
                    ? string.Empty
                    : "monitor-list-day-offset-and-line-required",
                hasLineTrips = hasLineTrips,
                dataComplete = m_Port.DataComplete(),
                droppedTripCount = Math.Max(0, m_Port.DroppedTripCount()),
                persistenceHealthy = m_Port.PersistenceHealthy(),
                lastIssueCode = m_Port.LastIssueCode(),
                issueCount = Math.Max(0, m_Port.IssueCount()),
                serviceDateKey = serviceDateKey,
                currentServiceDateKey = currentServiceDateKey,
                nowMinute = clock.NowMinute,
                clockEpoch = clock.ClockEpoch,
                truncated = truncated,
                summary = new DispatchWorkbenchMonitorSummaryDto(),
                trips = headers.ToArray()
            };
        }

        internal DispatchWorkbenchMonitorDetailResponseDto BuildMonitorDetail(
            DispatchWorkbenchMonitorDetailRequestDto request)
        {
            return BuildMonitorDetail(FindMonitor(request?.tripKey), request?.tripKey);
        }

        internal DispatchWorkbenchMonitorDetailsResponseDto BuildMonitorDetails(
            DispatchWorkbenchMonitorDetailsRequestDto request)
        {
            string[] keys = request?.tripKeys ?? Array.Empty<string>();
            if (keys.Length == 0 || keys.Length > MaxMonitorDetails
                || keys.Any(string.IsNullOrEmpty)
                || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            {
                return new DispatchWorkbenchMonitorDetailsResponseDto
                {
                    success = false,
                    error = "monitor-detail-batch-invalid"
                };
            }
            HashSet<string> wanted = new HashSet<string>(keys, StringComparer.Ordinal);
            Dictionary<string, MonitorTrip> found = new Dictionary<string, MonitorTrip>(StringComparer.Ordinal);
            foreach (MonitorTrip trip in MonitorTrips())
                if (trip != null && wanted.Contains(trip.Key))
                    found[trip.Key] = trip;
            DispatchWorkbenchMonitorDetailResponseDto[] details = keys
                .Select(key => BuildMonitorDetail(found.TryGetValue(key, out MonitorTrip trip) ? trip : null, key))
                .ToArray();
            return new DispatchWorkbenchMonitorDetailsResponseDto
            {
                success = true,
                details = details
            };
        }

        private DispatchWorkbenchMonitorDetailResponseDto BuildMonitorDetail(
            MonitorTrip found,
            string tripKey)
        {

            DispatchWorkbenchMonitorDetailResponseDto response = new DispatchWorkbenchMonitorDetailResponseDto
            {
                success = found != null,
                error = found == null
                    ? (string.IsNullOrEmpty(tripKey)
                        ? "monitor-detail-trip-key-required"
                        : "trip-not-found")
                    : null,
                dataComplete = m_Port.DataComplete(),
                droppedTripCount = Math.Max(0, m_Port.DroppedTripCount()),
                persistenceHealthy = m_Port.PersistenceHealthy(),
                lastIssueCode = m_Port.LastIssueCode(),
                issueCount = Math.Max(0, m_Port.IssueCount())
            };
            if (found == null)
            {
                return response;
            }

            response.header = BuildHeader(found);
            int count = math.clamp(found.VisibleStopCount, 0, found.Stops.Count);
            response.stops = new DispatchWorkbenchMonitorStopDto[count + (count > 0 ? 1 : 0)];
            for (int i = 0; i < count; i++)
            {
                MonitorStop stop = found.Stops[i];
                bool plannedVisible = i < found.SuppressPlanFrom;
                response.stops[i] = new DispatchWorkbenchMonitorStopDto
                {
                    order = i,
                    stopKey = stop.StopKey,
                    waypointIndex = stop.WaypointIndex,
                    plannedArrivalMinute = i == 0 ? null : plannedVisible ? NullableMinute(stop.PlannedArrival) : null,
                    plannedDepartureMinute = plannedVisible ? NullableMinute(stop.PlannedDeparture) : null,
                    actualArrivalMinute = i == 0 ? null : NullableMinute(stop.ActualArrival),
                    actualDepartureMinute = NullableMinute(stop.ActualDeparture),
                    skipped = stop.Skipped,
                    cleared = stop.Cleared
                };
            }
            if (count > 0)
            {
                MonitorStop origin = found.Stops[0];
                response.stops[count] = new DispatchWorkbenchMonitorStopDto
                {
                    order = count,
                    stopKey = origin.StopKey,
                    waypointIndex = origin.WaypointIndex,
                    plannedArrivalMinute = NullableMinute(origin.PlannedArrival),
                    plannedDepartureMinute = null,
                    actualArrivalMinute = NullableMinute(origin.ActualArrival),
                    actualDepartureMinute = null,
                    skipped = origin.Skipped,
                    cleared = found.State == MonitorTripState.Cleared
                };
            }
            return response;
        }

        private MonitorTrip FindMonitor(string tripKey)
        {
            return string.IsNullOrEmpty(tripKey)
                ? null
                : MonitorTrips().FirstOrDefault(trip =>
                    trip != null && string.Equals(trip.Key, tripKey, StringComparison.Ordinal));
        }

        private static bool MatchesMonitorFilter(
            MonitorTrip trip,
            DispatchWorkbenchMonitorListRequestDto request)
        {
            DispatchWorkbenchMonitorCoverageDto[] coverages = request.coverageFilter?.coverages;
            if (coverages == null)
                return true;
            return coverages.Any(coverage => CoverageIntersects(trip, coverage, request.startMinute, request.endMinute));
        }

        private static bool CoverageIntersects(
            MonitorTrip trip,
            DispatchWorkbenchMonitorCoverageDto coverage,
            int startMinute,
            int endMinute)
        {
            if (coverage?.points == null || coverage.points.Length == 0)
                return false;
            DispatchWorkbenchRunChartStationDto[] points = coverage.points
                .Where(point => point != null && point.waypointIndex >= 0 && !string.IsNullOrEmpty(point.stationId))
                .OrderBy(point => point.sectionIndex)
                .ToArray();
            if (points.Length == 0)
                return false;

            int minSection = Math.Min(coverage.fromSectionIndex, coverage.toSectionIndex);
            int maxSection = Math.Max(coverage.fromSectionIndex, coverage.toSectionIndex);
            bool hasPrevious = false;
            double previousMinute = 0;
            double previousSection = 0;
            int pointIndex = -1;
            int count = math.clamp(trip.VisibleStopCount, 0, trip.Stops.Count);
            for (int index = 0; index <= count; index++)
            {
                MonitorStop stop = index == count ? (count > 0 ? trip.Stops[0] : null) : trip.Stops[index];
                bool origin = index == 0;
                int arrival = origin || stop == null ? -1 : stop.ActualArrival;
                int departure = index == count ? -1 : stop?.ActualDeparture ?? -1;
                if ((origin && departure < 0) || (!origin && arrival < 0))
                    break;

                DispatchWorkbenchRunChartStationDto point = NextCoveragePoint(points, stop, ref pointIndex);
                if (point != null)
                {
                    if (!origin && HitsWindow(arrival, point.sectionIndex, ref hasPrevious,
                        ref previousMinute, ref previousSection, startMinute, endMinute, minSection, maxSection))
                    {
                        return true;
                    }
                    if (departure >= 0 && (origin || departure != arrival)
                        && HitsWindow(departure, point.sectionIndex, ref hasPrevious,
                            ref previousMinute, ref previousSection, startMinute, endMinute, minSection, maxSection))
                    {
                        return true;
                    }
                }
                if (index < count && departure < 0)
                    break;
            }
            return false;
        }

        private static DispatchWorkbenchRunChartStationDto NextCoveragePoint(
            DispatchWorkbenchRunChartStationDto[] points,
            MonitorStop stop,
            ref int pointIndex)
        {
            if (points == null || points.Length == 0 || stop == null)
                return null;
            if (pointIndex < 0)
            {
                if (!PointMatches(points[0], stop))
                    return null;
                pointIndex = 0;
                return points[0];
            }
            for (int index = pointIndex + 1; index < points.Length; index++)
            {
                if (!PointMatches(points[index], stop))
                    continue;
                pointIndex = index;
                return points[index];
            }
            return null;
        }

        private static bool PointMatches(DispatchWorkbenchRunChartStationDto point, MonitorStop stop)
        {
            return point != null
                && stop != null
                && point.waypointIndex == stop.WaypointIndex
                && string.Equals(point.stationId, stop.StopKey, StringComparison.Ordinal);
        }

        private static bool HitsWindow(
            double minute,
            double section,
            ref bool hasPrevious,
            ref double previousMinute,
            ref double previousSection,
            double startMinute,
            double endMinute,
            double minSection,
            double maxSection)
        {
            bool hit = minute >= startMinute && minute <= endMinute
                && section >= minSection && section <= maxSection;
            if (!hit && hasPrevious)
            {
                hit = SegmentHits(previousMinute, previousSection, minute, section,
                    startMinute, endMinute, minSection, maxSection);
            }
            hasPrevious = true;
            previousMinute = minute;
            previousSection = section;
            return hit;
        }

        private static bool SegmentHits(
            double startX,
            double startY,
            double endX,
            double endY,
            double minX,
            double maxX,
            double minY,
            double maxY)
        {
            double deltaX = endX - startX;
            double deltaY = endY - startY;
            double lower = 0;
            double upper = 1;
            return ClipSegment(-deltaX, startX - minX, ref lower, ref upper)
                && ClipSegment(deltaX, maxX - startX, ref lower, ref upper)
                && ClipSegment(-deltaY, startY - minY, ref lower, ref upper)
                && ClipSegment(deltaY, maxY - startY, ref lower, ref upper);
        }

        private static bool ClipSegment(double direction, double distance, ref double lower, ref double upper)
        {
            if (direction == 0)
                return distance >= 0;
            double ratio = distance / direction;
            if (direction < 0)
            {
                if (ratio > upper)
                    return false;
                if (ratio > lower)
                    lower = ratio;
            }
            else
            {
                if (ratio < lower)
                    return false;
                if (ratio < upper)
                    upper = ratio;
            }
            return true;
        }

        private IEnumerable<MonitorTrip> MonitorTrips()
        {
            foreach (MonitorTrip trip in m_Port.ActiveTrips())
            {
                yield return trip;
            }
            foreach (MonitorDateSlot slot in m_Port.DateSlots())
            {
                if (slot == null)
                {
                    continue;
                }
                foreach (MonitorTrip trip in slot.Trips.Values)
                {
                    yield return trip;
                }
            }
        }

        private static DispatchWorkbenchMonitorTripHeaderDto BuildHeader(MonitorTrip trip)
        {
            return new DispatchWorkbenchMonitorTripHeaderDto
            {
                tripKey = trip.Key,
                lineId = trip.LineId,
                serviceDateKey = trip.ServiceDateKey,
                plannedStartMinute = trip.SlotMinute,
                actualStartMinute = NullableMinute(trip.ActualStartMinute),
                plannedEndMinute = trip.Stops.Count > 0 ? NullableMinute(trip.Stops[0].PlannedArrival) : null,
                actualEndMinute = trip.Stops.Count > 0 ? NullableMinute(trip.Stops[0].ActualArrival) : null,
                scheduleType = trip.Stops.Skip(1).Any(stop => stop.PlannedArrival >= 0 || stop.PlannedDeparture >= 0)
                    ? "custom"
                    : "default",
                state = trip.State.ToString().ToLowerInvariant(),
                endReason = trip.EndReason.ToString().ToLowerInvariant(),
                serviceKind = trip.ServiceKind
            };
        }

        private static int? NullableMinute(int minute) => minute < 0 ? (int?)null : minute;
    }

}
