using System;
using System.Collections.Generic;
using System.Linq;
using Game.Routes;
using RapidTransitMod.Dispatch.Observation;
using System.Runtime.Serialization;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Workbench
{
    internal sealed class TripPort
    {
        private readonly Func<Entity, Entity> m_Stop;
        private readonly Func<Entity, Entity> m_Building;
        private readonly Func<Entity, string> m_LineId;
        private readonly Func<Entity, string> m_Kind;
        private readonly Func<string, int> m_Minutes;
        private readonly Func<string> m_Clock;
        private readonly TryRouteProgress m_TryProgress;
        private readonly TryVehicleState m_TryState;
        private readonly Func<IEnumerable<MonitorTrip>> m_ActiveTrips;
        private readonly Func<IEnumerable<MonitorDateSlot>> m_DateSlots;
        private readonly Func<int, string> m_Slot;

        internal delegate bool TryRouteProgress(Entity vehicle, out int nextWp, out float progress);
        internal delegate bool TryVehicleState(Entity vehicle, out VehicleState state);

        internal TripPort(
            EntityManager entityManager,
            IReadOnlyDictionary<Entity, VehicleTrace> vehicles,
            Func<Entity, Entity> stop,
            Func<Entity, Entity> building,
            Func<Entity, string> lineId,
            Func<Entity, string> kind,
            Func<string, int> minutes,
            Func<string> clock,
            TryRouteProgress tryProgress,
            TryVehicleState tryState,
            Func<IEnumerable<MonitorTrip>> activeTrips,
            Func<IEnumerable<MonitorDateSlot>> dateSlots,
            Func<int, string> slot)
        {
            EntityManager = entityManager;
            Vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            m_Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            m_Building = building ?? throw new ArgumentNullException(nameof(building));
            m_LineId = lineId ?? throw new ArgumentNullException(nameof(lineId));
            m_Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            m_Minutes = minutes ?? throw new ArgumentNullException(nameof(minutes));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_TryProgress = tryProgress ?? throw new ArgumentNullException(nameof(tryProgress));
            m_TryState = tryState ?? throw new ArgumentNullException(nameof(tryState));
            m_ActiveTrips = activeTrips ?? throw new ArgumentNullException(nameof(activeTrips));
            m_DateSlots = dateSlots ?? throw new ArgumentNullException(nameof(dateSlots));
            m_Slot = slot ?? throw new ArgumentNullException(nameof(slot));
        }

        internal EntityManager EntityManager { get; }
        internal IReadOnlyDictionary<Entity, VehicleTrace> Vehicles { get; }

        internal Entity Stop(Entity waypoint) => m_Stop(waypoint);
        internal Entity Building(Entity stop) => m_Building(stop);
        internal string LineId(Entity line) => m_LineId(line);
        internal string Kind(Entity line) => m_Kind(line);
        internal int Minutes(string time) => m_Minutes(time);
        internal string Clock() => m_Clock();
        internal bool TryProgress(Entity vehicle, out int nextWp, out float progress) => m_TryProgress(vehicle, out nextWp, out progress);
        internal bool TryState(Entity vehicle, out VehicleState state) => m_TryState(vehicle, out state);
        internal IEnumerable<MonitorTrip> ActiveTrips() => m_ActiveTrips();
        internal IEnumerable<MonitorDateSlot> DateSlots() => m_DateSlots();
        internal string Slot(int minute) => minute < 0 ? null : m_Slot(minute % 1440);
    }

    internal sealed class Trips
    {
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

        private List<DispatchWorkbenchTripDto> BuildLegacy(
            WorkbenchLineRuntime active,
            List<DispatchWorkbenchStationDto> stations,
            DispatchWorkbenchDraftState draft)
        {
            List<DispatchWorkbenchTripDto> trips = new List<DispatchWorkbenchTripDto>();
            if (active == null || stations == null || stations.Count == 0)
            {
                return trips;
            }

            if (!m_Port.EntityManager.HasBuffer<RouteWaypoint>(active.Entity))
            {
                return trips;
            }

            HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in NormalizeLineIdList(draft?.MergedView?.localLineIds, draft?.MergedView?.localLineId))
            {
                allowed.Add(id);
            }
            foreach (string id in NormalizeLineIdList(draft?.MergedView?.expressLineIds, draft?.MergedView?.expressLineId))
            {
                allowed.Add(id);
            }
            allowed.Add(active.Id);
            if (allowed.Count == 0)
            {
                allowed.Add(active.Id);
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Port.EntityManager.GetBuffer<RouteWaypoint>(active.Entity, true);
            Dictionary<Entity, string> stationIds = new Dictionary<Entity, string>();
            HashSet<Entity> seenStops = new HashSet<Entity>();
            for (int i = 0; i < waypoints.Length && i < stations.Count + 8; i++)
            {
                Entity stop = m_Port.Stop(waypoints[i].m_Waypoint);
                if (stop == Entity.Null || !seenStops.Add(stop))
                {
                    continue;
                }

                int stationIndex = stationIds.Count;
                if (stationIndex >= stations.Count)
                {
                    break;
                }

                stationIds[stop] = stations[stationIndex].id;
                Entity building = m_Port.Building(stop);
                if (building != Entity.Null && !stationIds.ContainsKey(building))
                {
                    stationIds[building] = stations[stationIndex].id;
                }
            }

            foreach (KeyValuePair<Entity, VehicleTrace> pair in m_Port.Vehicles)
            {
                Entity vehicle = pair.Key;
                VehicleTrace record = pair.Value;
                if (record == null || !m_Port.EntityManager.Exists(vehicle))
                {
                    continue;
                }

                if (record.Line == Entity.Null || !m_Port.EntityManager.Exists(record.Line))
                {
                    continue;
                }

                string lineId = m_Port.LineId(record.Line);
                if (!allowed.Contains(lineId))
                {
                    continue;
                }

                string kind = string.Equals(m_Port.Kind(record.Line), "express", StringComparison.Ordinal)
                    ? "express"
                    : "local";

                for (int tripIndex = 0; tripIndex < record.Trips.Count; tripIndex++)
                {
                    TripTrace trip = record.Trips[tripIndex];
                    if (trip == null || trip.Stops.Count == 0)
                    {
                        continue;
                    }

                    List<DispatchWorkbenchTripStopDto> stops = new List<DispatchWorkbenchTripStopDto>();
                    int previousStopMin = -1;
                    for (int i = 0; i < trip.Stops.Count; i++)
                    {
                        StopTrace stop = trip.Stops[i];
                        if (!stationIds.TryGetValue(stop.Stop, out string stationId))
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(stop.Arrival) && string.IsNullOrEmpty(stop.Departure))
                        {
                            continue;
                        }

                        string time = !string.IsNullOrEmpty(stop.Departure) ? stop.Departure : stop.Arrival;
                        int stopMin = m_Port.Minutes(time);
                        if (previousStopMin >= 0 && stopMin >= 0 && stopMin + 5 < previousStopMin)
                        {
                            break;
                        }

                        stops.Add(new DispatchWorkbenchTripStopDto
                        {
                            stationId = stationId,
                            time = time,
                            arrivalTime = string.IsNullOrEmpty(stop.Arrival) ? null : stop.Arrival,
                            departureTime = string.IsNullOrEmpty(stop.Departure) ? null : stop.Departure,
                            stopType = i == 0 ? "origin" : "normal"
                        });
                        previousStopMin = stopMin;
                    }

                    if (stops.Count == 0)
                    {
                        continue;
                    }

                    string realtimeFromStationId = null;
                    string realtimeToStationId = null;
                    string realtimeTime = null;
                    float realtimeProgress = 0f;
                    bool latest = tripIndex == record.Trips.Count - 1;
                    bool running = m_Port.TryState(vehicle, out VehicleState state)
                        && state == VehicleState.Running;
                    if (latest
                        && running
                        && record.Line == active.Entity
                        && m_Port.TryProgress(vehicle, out int nextWp, out float progress))
                    {
                        int previousWp = nextWp == 0 ? waypoints.Length - 1 : nextWp - 1;
                        Entity fromStop = previousWp >= 0 && previousWp < waypoints.Length
                            ? m_Port.Stop(waypoints[previousWp].m_Waypoint)
                            : Entity.Null;
                        Entity toStop = nextWp >= 0 && nextWp < waypoints.Length
                            ? m_Port.Stop(waypoints[nextWp].m_Waypoint)
                            : Entity.Null;

                        if (fromStop != Entity.Null && stationIds.TryGetValue(fromStop, out string fromStationId))
                        {
                            realtimeFromStationId = fromStationId;
                        }
                        if (toStop != Entity.Null && stationIds.TryGetValue(toStop, out string toStationId))
                        {
                            realtimeToStationId = toStationId;
                        }
                        realtimeProgress = math.saturate(progress);
                        realtimeTime = m_Port.Clock();
                    }

                    trips.Add(new DispatchWorkbenchTripDto
                    {
                        id = "RT-" + vehicle.Index.ToString() + "-" + trip.Seq.ToString(),
                        lineId = lineId,
                        kind = kind,
                        depart = stops.FirstOrDefault()?.departureTime ?? stops.FirstOrDefault()?.time ?? "--:--",
                        realtimeSegment = 0,
                        realtimeProgress = realtimeProgress,
                        realtimeFromStationId = realtimeFromStationId,
                        realtimeToStationId = realtimeToStationId,
                        realtimeTime = realtimeTime,
                        stops = stops.ToArray()
                    });
                }
            }

            return trips;
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

        internal MonitorSnapshotDto BuildMonitor()
        {
            MonitorTripDto[] active = m_Port.ActiveTrips()
                .Where(trip => trip != null)
                .Select(BuildMonitorTrip)
                .ToArray();
            MonitorDayDto[] days = m_Port.DateSlots()
                .OrderByDescending(slot => slot.DateKey)
                .Select(slot => new MonitorDayDto
                {
                    serviceDateKey = slot.DateKey,
                    trips = slot.Trips.Values
                        .OrderBy(trip => trip.LineKey, StringComparer.Ordinal)
                        .ThenBy(trip => trip.SlotMinute)
                        .Select(BuildMonitorTrip)
                        .ToArray()
                })
                .ToArray();
            return new MonitorSnapshotDto { activeTrips = active, days = days };
        }

        private MonitorTripDto BuildMonitorTrip(MonitorTrip trip)
        {
            int count = math.clamp(trip.VisibleStopCount, 0, trip.Stops.Count);
            MonitorStopDto[] stops = new MonitorStopDto[count];
            for (int i = 0; i < count; i++)
            {
                MonitorStop stop = trip.Stops[i];
                bool plannedVisible = i < trip.SuppressPlanFrom;
                stops[i] = new MonitorStopDto
                {
                    order = i,
                    stopKey = stop.StopKey,
                    waypointIndex = stop.WaypointIndex,
                    plannedArrivalMinute = plannedVisible ? NullableMinute(stop.PlannedArrival) : null,
                    plannedDepartureMinute = plannedVisible ? NullableMinute(stop.PlannedDeparture) : null,
                    actualArrivalMinute = NullableMinute(stop.ActualArrival),
                    actualDepartureMinute = NullableMinute(stop.ActualDeparture),
                    cleared = stop.Cleared
                };
            }
            return new MonitorTripDto
            {
                key = trip.Key,
                lineKey = trip.LineKey,
                lineId = trip.LineId,
                rowId = trip.RowId,
                serviceKind = trip.ServiceKind,
                serviceDateKey = trip.ServiceDateKey,
                slotMinute = trip.SlotMinute,
                actualStartMinute = NullableMinute(trip.ActualStartMinute),
                state = trip.State.ToString().ToLowerInvariant(),
                vehicleIndex = trip.Vehicle == Entity.Null ? -1 : trip.Vehicle.Index,
                stops = stops
            };
        }

        private static int? NullableMinute(int minute) => minute < 0 ? (int?)null : minute;

        private static List<string> NormalizeLineIdList(string[] ids, string fallbackId)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> normalized = new List<string>();

            if (ids != null)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    {
                        continue;
                    }

                    normalized.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(fallbackId) && seen.Add(fallbackId))
            {
                normalized.Add(fallbackId);
            }

            return normalized;
        }
    }

    [DataContract]
    internal sealed class MonitorSnapshotDto
    {
        [DataMember] public MonitorTripDto[] activeTrips = Array.Empty<MonitorTripDto>();
        [DataMember] public MonitorDayDto[] days = Array.Empty<MonitorDayDto>();
    }

    [DataContract]
    internal sealed class MonitorDayDto
    {
        [DataMember] public int serviceDateKey;
        [DataMember] public MonitorTripDto[] trips = Array.Empty<MonitorTripDto>();
    }

    [DataContract]
    internal sealed class MonitorTripDto
    {
        [DataMember] public string key;
        [DataMember] public string lineKey;
        [DataMember] public string lineId;
        [DataMember] public string rowId;
        [DataMember] public string serviceKind;
        [DataMember] public int serviceDateKey;
        [DataMember] public int slotMinute;
        [DataMember] public int? actualStartMinute;
        [DataMember] public string state;
        [DataMember] public int vehicleIndex;
        [DataMember] public MonitorStopDto[] stops = Array.Empty<MonitorStopDto>();
    }

    [DataContract]
    internal sealed class MonitorStopDto
    {
        [DataMember] public int order;
        [DataMember] public string stopKey;
        [DataMember] public int waypointIndex;
        [DataMember] public int? plannedArrivalMinute;
        [DataMember] public int? plannedDepartureMinute;
        [DataMember] public int? actualArrivalMinute;
        [DataMember] public int? actualDepartureMinute;
        [DataMember] public bool cleared;
    }
}
