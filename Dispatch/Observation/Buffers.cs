using Game.Common;
using Game.Pathfind;
using Game.Routes;
using RapidTransitMod.Core;
using RapidTransitMod.Dispatch.Lines;
using RapidTransitMod.Dispatch.Persistence;
using RapidTransitMod.TrackModel;
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace RapidTransitMod.Dispatch.Observation
{
    internal sealed class Buffers
    {
        private const ulong SignatureSeed = 1469598103934665603UL;
        private readonly ModRuntimeHostSystem m_Runtime;
        private readonly Dictionary<string, int> m_MonitorTripOrders =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<int, int> m_MonitorTripIndices =
            new Dictionary<int, int>();
        private readonly Dictionary<long, int> m_MonitorStopIndices =
            new Dictionary<long, int>();
        private readonly Dictionary<int, int> m_MonitorTripOrderCounts =
            new Dictionary<int, int>();
        private readonly Dictionary<string, int> m_MonitorTripKeyCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<long, int> m_MonitorStopOrderCounts =
            new Dictionary<long, int>();
        private readonly Dictionary<int, int> m_MonitorStopTripCounts =
            new Dictionary<int, int>();
        private int m_NextMonitorTripOrder;

        public Buffers(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        public void Ensure()
        {
            EnsureDwell();
            EnsureStationDwell();
            EnsureSlice();
            EnsureBusSeg();
            EnsureRailSegments();
            EnsureMonitor();
        }

        public void EnsureDwell()
        {
            EnsureDwellCore();
        }

        public void EnsureStationDwell()
        {
            EnsureStationDwellCore();
        }

        public void EnsureSlice()
        {
            EnsureSliceCore();
        }

        public void EnsureBusSeg()
        {
            EnsureBusSegCore();
        }

        public void EnsureRailSegments()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || m_Runtime.EntityManager.HasBuffer<RailSegmentObservationElement>(city))
                return;

            m_Runtime.EntityManager.AddBuffer<RailSegmentObservationElement>(city);
        }

        public void EnsureMonitor()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;
            if (!m_Runtime.EntityManager.HasBuffer<MonitorDateSlotElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorDateSlotElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<MonitorTripElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorTripElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<MonitorStopElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorStopElement>(city);
        }

        public void Load()
        {
            LoadDwell();
            LoadStationDwell();
            LoadSlice();
            LoadBusSeg();
            LoadRailSegments();
            LoadMonitor();
        }

        public void LoadMonitor()
        {
            EnsureMonitor();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || m_Runtime.m_ObsRecorder == null
                || !m_Runtime.EntityManager.HasBuffer<MonitorDateSlotElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<MonitorTripElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<MonitorStopElement>(city))
            {
                return;
            }

            m_Runtime.m_ObsRecorder.ClearMonitor();
            ResetMonitorIndices();
            DynamicBuffer<MonitorDateSlotElement> slots =
                m_Runtime.EntityManager.GetBuffer<MonitorDateSlotElement>(city, true);
            HashSet<int> slotKeys = new HashSet<int>();
            for (int i = 0; i < slots.Length && slotKeys.Count < 2; i++)
            {
                MonitorDateSlotElement element = slots[i];
                if (element.m_Version != 1 || element.m_DateKey <= 0 || !slotKeys.Add(element.m_DateKey))
                    continue;
                m_Runtime.m_ObsRecorder.RestoreDateSlot(element.m_DateKey);
            }

            DynamicBuffer<MonitorTripElement> trips =
                m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city, true);
            DynamicBuffer<MonitorStopElement> stops =
                m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city, true);
            Dictionary<int, List<MonitorStopElement>> stopsByTrip =
                new Dictionary<int, List<MonitorStopElement>>();
            Dictionary<long, int> loadedStopIndices = new Dictionary<long, int>();
            for (int i = 0; i < stops.Length; i++)
            {
                MonitorStopElement stop = stops[i];
                long stopKey = MonitorStopIndexKey(stop.m_TripOrder, stop.m_StopOrder);
                IncrementCount(m_MonitorStopOrderCounts, stopKey);
                IncrementCount(m_MonitorStopTripCounts, stop.m_TripOrder);
                if (stop.m_Version != 1 || stop.m_TripOrder < 0 || stop.m_StopOrder < 0)
                    continue;
                if (loadedStopIndices.ContainsKey(stopKey))
                    loadedStopIndices[stopKey] = -1;
                else
                    loadedStopIndices[stopKey] = i;
                if (!stopsByTrip.TryGetValue(stop.m_TripOrder, out List<MonitorStopElement> list))
                {
                    list = new List<MonitorStopElement>();
                    stopsByTrip[stop.m_TripOrder] = list;
                }
                list.Add(stop);
            }

            HashSet<int> tripOrders = new HashSet<int>();
            HashSet<string> monitorKeys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<Entity> activeVehicles = new HashSet<Entity>();
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                IncrementCount(m_MonitorTripOrderCounts, element.m_TripOrder);
                string elementKey = element.m_Key.ToString();
                if (!string.IsNullOrEmpty(elementKey))
                    IncrementCount(m_MonitorTripKeyCounts, elementKey);
                if (element.m_TripOrder >= 0
                    && element.m_TripOrder < int.MaxValue
                    && element.m_TripOrder >= m_NextMonitorTripOrder)
                    m_NextMonitorTripOrder = element.m_TripOrder + 1;
                bool active = element.m_Active == 1;
                string lineKey = element.m_LineKey.ToString();
                string rowId = element.m_RowId.ToString();
                string expectedKey = lineKey + "|" + rowId + "|" + element.m_ServiceDateKey;
                bool activeValid = !active
                    || (element.m_Vehicle != Entity.Null
                        && element.m_Line != Entity.Null
                        && m_Runtime.EntityManager.Exists(element.m_Vehicle)
                        && m_Runtime.EntityManager.Exists(element.m_Line)
                        && m_Runtime.m_VehicleView.TryGetLine(element.m_Vehicle, out Entity restoredLine)
                        && restoredLine == element.m_Line
                        && m_Runtime.m_VehicleView.TryGetState(element.m_Vehicle, out VehicleState restoredState)
                        && restoredState == VehicleState.Running);
                if (element.m_Version != 1
                    || element.m_TripOrder < 0
                    || element.m_TripOrder == int.MaxValue
                    || !tripOrders.Add(element.m_TripOrder)
                    || !monitorKeys.Add(element.m_Key.ToString())
                    || string.IsNullOrEmpty(element.m_Key.ToString())
                    || string.IsNullOrEmpty(lineKey)
                    || string.IsNullOrEmpty(rowId)
                    || !string.Equals(element.m_Key.ToString(), expectedKey, StringComparison.Ordinal)
                    || !ValidMonitorDate(element.m_ServiceDateKey)
                    || element.m_SlotMinute < 0
                    || element.m_SlotMinute >= 1440
                    || element.m_StopCount <= 0
                    || element.m_StopCount > 256
                    || element.m_State < 0
                    || element.m_State > (int)MonitorTripState.Cleared
                    || (active && element.m_State != (int)MonitorTripState.Active)
                    || (!active && element.m_State == (int)MonitorTripState.Active)
                    || !stopsByTrip.TryGetValue(element.m_TripOrder, out List<MonitorStopElement> savedStops)
                    || savedStops.Count != element.m_StopCount
                    || !activeValid
                    || (active && !activeVehicles.Add(element.m_Vehicle)))
                {
                    continue;
                }

                savedStops.Sort((left, right) => left.m_StopOrder.CompareTo(right.m_StopOrder));
                MonitorTrip trip = new MonitorTrip
                {
                    Key = element.m_Key.ToString(),
                    LineKey = element.m_LineKey.ToString(),
                    LineId = element.m_LineId.ToString(),
                    RowId = element.m_RowId.ToString(),
                    ServiceKind = element.m_ServiceKind.ToString(),
                    StopSig = element.m_StopSig.ToString(),
                    Line = element.m_Line,
                    Vehicle = element.m_Vehicle,
                    ServiceDateKey = element.m_ServiceDateKey,
                    SlotMinute = element.m_SlotMinute,
                    ActualStartMinute = element.m_ActualStartMinute,
                    NextArrivalOrder = element.m_NextArrivalOrder,
                    VisibleStopCount = element.m_VisibleStopCount,
                    SuppressPlanFrom = element.m_SuppressPlanFrom,
                    State = (MonitorTripState)element.m_State,
                    LaunchFrame = element.m_LaunchFrame,
                    UpdatedFrame = element.m_UpdatedFrame
                };
                bool valid = true;
                for (int stopIndex = 0; stopIndex < savedStops.Count; stopIndex++)
                {
                    MonitorStopElement stop = savedStops[stopIndex];
                    if (stop.m_StopOrder != stopIndex || string.IsNullOrEmpty(stop.m_StopKey.ToString()))
                    {
                        valid = false;
                        break;
                    }
                    trip.Stops.Add(new MonitorStop
                    {
                        Order = stopIndex,
                        StopKey = stop.m_StopKey.ToString(),
                        Station = stop.m_Station,
                        WaypointIndex = stop.m_WaypointIndex,
                        PlannedArrival = stop.m_PlannedArrival,
                        PlannedDeparture = stop.m_PlannedDeparture,
                        ActualArrival = stop.m_ActualArrival,
                        ActualDeparture = stop.m_ActualDeparture,
                        Cleared = stop.m_Cleared == 1
                    });
                }
                if (valid && active)
                {
                    if (m_Runtime.m_LineView.TryStopLayout(
                            trip.Line,
                            out string currentStopSig,
                            out _)
                        && !string.IsNullOrEmpty(currentStopSig)
                        && !string.Equals(trip.StopSig, currentStopSig, StringComparison.Ordinal))
                    {
                        trip.SuppressPlanFrom = Math.Min(
                            trip.SuppressPlanFrom,
                            trip.NextArrivalOrder);
                    }
                }
                if (valid && m_Runtime.m_ObsRecorder.RestoreMonitor(trip, active))
                {
                    m_MonitorTripOrders[trip.Key] = element.m_TripOrder;
                    m_MonitorTripIndices[element.m_TripOrder] = i;
                    for (int stopIndex = 0; stopIndex < trip.Stops.Count; stopIndex++)
                    {
                        long indexKey = MonitorStopIndexKey(element.m_TripOrder, stopIndex);
                        if (loadedStopIndices.TryGetValue(indexKey, out int bufferIndex)
                            && bufferIndex >= 0)
                        {
                            m_MonitorStopIndices[indexKey] = bufferIndex;
                        }
                    }
                }
            }
            m_Runtime.m_ObsRecorder.TickDate(m_Runtime.m_SimClock.NowDate);
        }

        public void FlushMonitor()
        {
            if (m_Runtime.m_ObsRecorder == null)
                return;
            EnsureMonitor();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            DynamicBuffer<MonitorDateSlotElement> slots =
                m_Runtime.EntityManager.GetBuffer<MonitorDateSlotElement>(city);
            DynamicBuffer<MonitorTripElement> trips =
                m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city);
            DynamicBuffer<MonitorStopElement> stops =
                m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city);
            slots.Clear();
            trips.Clear();
            stops.Clear();
            ResetMonitorIndices();
            foreach (MonitorDateSlot slot in m_Runtime.m_ObsRecorder.MonitorDateSlots)
                slots.Add(new MonitorDateSlotElement { m_Version = 1, m_DateKey = slot.DateKey });

            int tripOrder = 0;
            foreach (MonitorTrip trip in m_Runtime.m_ObsRecorder.ActiveMonitorTrips)
            {
                if (AppendMonitorTrip(trip, true, tripOrder, trips, stops))
                    tripOrder++;
            }
            foreach (MonitorDateSlot slot in m_Runtime.m_ObsRecorder.MonitorDateSlots)
            {
                foreach (MonitorTrip trip in slot.Trips.Values)
                {
                    if (AppendMonitorTrip(trip, false, tripOrder, trips, stops))
                        tripOrder++;
                }
            }
            m_NextMonitorTripOrder = tripOrder;
        }

        public void FlushMonitor(string key)
        {
            if (string.IsNullOrEmpty(key)
                || m_Runtime.m_ObsRecorder == null
                || !m_Runtime.m_ObsRecorder.TryMonitor(key, out MonitorTrip trip, out bool active))
            {
                return;
            }

            EnsureMonitor();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;
            DynamicBuffer<MonitorTripElement> trips =
                m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city);
            DynamicBuffer<MonitorStopElement> stops =
                m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city);

            if (!m_MonitorTripOrders.TryGetValue(key, out int tripOrder))
            {
                if (!TryRebuildMonitorIndices(key, trip, trips, stops, out bool found))
                {
                    if (!RewriteMonitorTrip(key, trip, active, trips, stops))
                        m_Runtime.m_ObsRecorder.MonitorPersistFailed("trip-rewrite-failed");
                    return;
                }
                if (!found)
                {
                    if (!TryFindMonitorTripOrder(trips, stops, out int newOrder)
                        || !AppendMonitorTrip(trip, active, newOrder, trips, stops))
                    {
                        m_Runtime.m_ObsRecorder.MonitorPersistFailed("trip-append-failed");
                    }
                    return;
                }
                tripOrder = m_MonitorTripOrders[key];
            }
            if (!HasMonitorIndices(trip, tripOrder, trips, stops, out int tripIndex))
            {
                if (!TryRebuildMonitorIndices(key, trip, trips, stops, out bool found)
                    || !found
                    || !m_MonitorTripOrders.TryGetValue(key, out tripOrder)
                    || !HasMonitorIndices(trip, tripOrder, trips, stops, out tripIndex))
                {
                    if (!RewriteMonitorTrip(key, trip, active, trips, stops))
                        m_Runtime.m_ObsRecorder.MonitorPersistFailed("trip-rewrite-failed");
                    return;
                }
            }

            trips[tripIndex] = MonitorTripValue(trip, active, tripOrder);
            for (int stopOrder = 0; stopOrder < trip.Stops.Count; stopOrder++)
            {
                long indexKey = MonitorStopIndexKey(tripOrder, stopOrder);
                int stopIndex = m_MonitorStopIndices[indexKey];
                MonitorStop stop = trip.Stops[stopOrder];
                MonitorStopElement saved = stops[stopIndex];
                int cleared = stop.Cleared ? 1 : 0;
                if (saved.m_WaypointIndex == stop.WaypointIndex
                    && saved.m_ActualArrival == stop.ActualArrival
                    && saved.m_ActualDeparture == stop.ActualDeparture
                    && saved.m_Cleared == cleared)
                {
                    continue;
                }
                saved.m_WaypointIndex = stop.WaypointIndex;
                saved.m_ActualArrival = stop.ActualArrival;
                saved.m_ActualDeparture = stop.ActualDeparture;
                saved.m_Cleared = cleared;
                stops[stopIndex] = saved;
            }
        }

        private bool HasMonitorIndices(
            MonitorTrip trip,
            int tripOrder,
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops,
            out int tripIndex)
        {
            long stopIndexKey;
            if (!m_MonitorTripIndices.TryGetValue(tripOrder, out tripIndex)
                || tripIndex < 0
                || tripIndex >= trips.Length
                || trips[tripIndex].m_Version != 1
                || trips[tripIndex].m_TripOrder != tripOrder
                || trips[tripIndex].m_StopCount != trip.Stops.Count
                || !m_MonitorTripOrderCounts.TryGetValue(tripOrder, out int tripOrderCount)
                || tripOrderCount != 1
                || !m_MonitorTripKeyCounts.TryGetValue(trip.Key, out int tripKeyCount)
                || tripKeyCount != 1
                || !m_MonitorStopTripCounts.TryGetValue(tripOrder, out int stopTripCount)
                || stopTripCount != trip.Stops.Count
                || !string.Equals(
                    trips[tripIndex].m_Key.ToString(),
                    trip.Key,
                    StringComparison.Ordinal))
            {
                return false;
            }
            for (int stopOrder = 0; stopOrder < trip.Stops.Count; stopOrder++)
            {
                MonitorStop expectedStop = trip.Stops[stopOrder];
                stopIndexKey = MonitorStopIndexKey(tripOrder, stopOrder);
                if (!m_MonitorStopIndices.TryGetValue(
                        stopIndexKey,
                        out int stopIndex)
                    || stopIndex < 0
                    || stopIndex >= stops.Length
                    || stops[stopIndex].m_Version != 1
                    || stops[stopIndex].m_TripOrder != tripOrder
                    || stops[stopIndex].m_StopOrder != stopOrder
                    || string.IsNullOrEmpty(expectedStop.StopKey)
                    || !string.Equals(
                        stops[stopIndex].m_StopKey.ToString(),
                        expectedStop.StopKey,
                        StringComparison.Ordinal)
                    || !m_MonitorStopOrderCounts.TryGetValue(stopIndexKey, out int stopOrderCount)
                    || stopOrderCount != 1)
                {
                    return false;
                }
            }
            return true;
        }

        private bool RewriteMonitorTrip(
            string key,
            MonitorTrip trip,
            bool active,
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops)
        {
            HashSet<int> removedOrders = new HashSet<int>();
            int removedTripCount = 0;
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                if (!string.Equals(element.m_Key.ToString(), key, StringComparison.Ordinal))
                    continue;
                if (element.m_TripOrder < 0 || element.m_TripOrder == int.MaxValue)
                    return false;
                removedTripCount++;
                removedOrders.Add(element.m_TripOrder);
            }
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                if (removedOrders.Contains(element.m_TripOrder)
                    && !string.Equals(element.m_Key.ToString(), key, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            int removedStopCount = 0;
            for (int i = 0; i < stops.Length; i++)
            {
                if (removedOrders.Contains(stops[i].m_TripOrder))
                    removedStopCount++;
            }

            if (!TryFindMonitorTripOrder(trips, stops, out int newOrder)
                || !TryBuildMonitorValues(
                    trip,
                    active,
                    newOrder,
                    out MonitorTripElement preparedTrip,
                    out MonitorStopElement[] preparedStops))
            {
                return false;
            }

            int tripCapacity = trips.Length - removedTripCount + 1;
            int stopCapacity = stops.Length - removedStopCount + preparedStops.Length;
            try
            {
                trips.EnsureCapacity(tripCapacity);
                stops.EnsureCapacity(stopCapacity);
            }
            catch
            {
                return false;
            }

            for (int i = trips.Length - 1; i >= 0; i--)
            {
                if (!string.Equals(trips[i].m_Key.ToString(), key, StringComparison.Ordinal))
                    continue;
                trips.RemoveAt(i);
            }
            if (removedOrders.Count > 0)
            {
                for (int i = stops.Length - 1; i >= 0; i--)
                    if (removedOrders.Contains(stops[i].m_TripOrder))
                        stops.RemoveAt(i);
            }
            trips.Add(preparedTrip);
            for (int i = 0; i < preparedStops.Length; i++)
                stops.Add(preparedStops[i]);
            RebuildMonitorIndices(trips, stops);
            return true;
        }

        private bool TryFindMonitorTripOrder(
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops,
            out int tripOrder)
        {
            tripOrder = Math.Max(0, m_NextMonitorTripOrder);
            while (tripOrder < int.MaxValue)
            {
                bool occupied = false;
                for (int i = 0; i < trips.Length; i++)
                {
                    if (trips[i].m_TripOrder == tripOrder)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (!occupied)
                {
                    for (int i = 0; i < stops.Length; i++)
                    {
                        if (stops[i].m_TripOrder == tripOrder)
                        {
                            occupied = true;
                            break;
                        }
                    }
                }
                if (!occupied)
                    return true;
                tripOrder++;
            }
            tripOrder = -1;
            return false;
        }

        private void RebuildMonitorIndices(
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops)
        {
            ResetMonitorIndices();
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                string key = element.m_Key.ToString();
                IncrementCount(m_MonitorTripOrderCounts, element.m_TripOrder);
                if (!string.IsNullOrEmpty(key))
                    IncrementCount(m_MonitorTripKeyCounts, key);
                if (element.m_Version != 1
                    || element.m_TripOrder < 0
                    || element.m_TripOrder == int.MaxValue
                    || string.IsNullOrEmpty(key))
                {
                    continue;
                }
                m_MonitorTripOrders[key] = element.m_TripOrder;
                m_MonitorTripIndices[element.m_TripOrder] = i;
                if (element.m_TripOrder >= m_NextMonitorTripOrder)
                    m_NextMonitorTripOrder = element.m_TripOrder + 1;
            }
            for (int i = 0; i < stops.Length; i++)
            {
                MonitorStopElement element = stops[i];
                long indexKey = MonitorStopIndexKey(element.m_TripOrder, element.m_StopOrder);
                IncrementCount(m_MonitorStopOrderCounts, indexKey);
                IncrementCount(m_MonitorStopTripCounts, element.m_TripOrder);
                if (element.m_Version == 1
                    && element.m_TripOrder >= 0
                    && element.m_StopOrder >= 0)
                {
                    m_MonitorStopIndices[indexKey] = i;
                }
            }
        }

        private bool TryRebuildMonitorIndices(
            string key,
            MonitorTrip trip,
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops,
            out bool found)
        {
            found = false;
            int tripIndex = -1;
            int tripOrder = -1;
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                if (!string.Equals(element.m_Key.ToString(), key, StringComparison.Ordinal))
                    continue;
                if (found)
                    return false;
                found = true;
                tripIndex = i;
                tripOrder = element.m_TripOrder;
                if (element.m_Version != 1
                    || tripOrder < 0
                    || tripOrder == int.MaxValue
                    || element.m_StopCount != trip.Stops.Count)
                {
                    return false;
                }
            }
            if (!found)
                return true;

            for (int i = 0; i < trips.Length; i++)
            {
                if (i != tripIndex && trips[i].m_TripOrder == tripOrder)
                    return false;
            }

            int[] stopIndices = new int[trip.Stops.Count];
            for (int i = 0; i < stopIndices.Length; i++)
                stopIndices[i] = -1;
            int stopCount = 0;
            for (int i = 0; i < stops.Length; i++)
            {
                MonitorStopElement element = stops[i];
                if (element.m_TripOrder != tripOrder)
                    continue;
                stopCount++;
                if (element.m_Version != 1
                    || element.m_StopOrder < 0
                    || element.m_StopOrder >= stopIndices.Length
                    || stopIndices[element.m_StopOrder] >= 0
                    || string.IsNullOrEmpty(trip.Stops[element.m_StopOrder].StopKey)
                    || !string.Equals(
                        element.m_StopKey.ToString(),
                        trip.Stops[element.m_StopOrder].StopKey,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                stopIndices[element.m_StopOrder] = i;
            }
            if (stopCount != trip.Stops.Count)
                return false;
            for (int i = 0; i < stopIndices.Length; i++)
                if (stopIndices[i] < 0)
                    return false;

            m_MonitorTripOrders[key] = tripOrder;
            m_MonitorTripIndices[tripOrder] = tripIndex;
            m_MonitorTripOrderCounts[tripOrder] = 1;
            m_MonitorTripKeyCounts[key] = 1;
            m_MonitorStopTripCounts[tripOrder] = stopCount;
            for (int i = 0; i < stopIndices.Length; i++)
            {
                long indexKey = MonitorStopIndexKey(tripOrder, i);
                m_MonitorStopIndices[indexKey] = stopIndices[i];
                m_MonitorStopOrderCounts[indexKey] = 1;
            }
            return true;
        }

        private bool AppendMonitorTrip(
            MonitorTrip trip,
            bool active,
            int tripOrder,
            DynamicBuffer<MonitorTripElement> trips,
            DynamicBuffer<MonitorStopElement> stops)
        {
            if (!TryBuildMonitorValues(
                    trip,
                    active,
                    tripOrder,
                    out MonitorTripElement preparedTrip,
                    out MonitorStopElement[] preparedStops))
                return false;
            try
            {
                trips.EnsureCapacity(trips.Length + 1);
                stops.EnsureCapacity(stops.Length + preparedStops.Length);
            }
            catch
            {
                return false;
            }

            int tripIndex = trips.Length;
            trips.Add(preparedTrip);
            m_MonitorTripOrders[trip.Key] = tripOrder;
            m_MonitorTripIndices[tripOrder] = tripIndex;
            IncrementCount(m_MonitorTripOrderCounts, tripOrder);
            IncrementCount(m_MonitorTripKeyCounts, trip.Key);
            for (int i = 0; i < preparedStops.Length; i++)
            {
                int stopIndex = stops.Length;
                stops.Add(preparedStops[i]);
                m_MonitorStopIndices[MonitorStopIndexKey(tripOrder, i)] = stopIndex;
                IncrementCount(m_MonitorStopOrderCounts, MonitorStopIndexKey(tripOrder, i));
                IncrementCount(m_MonitorStopTripCounts, tripOrder);
            }
            if (tripOrder >= m_NextMonitorTripOrder)
                m_NextMonitorTripOrder = tripOrder + 1;
            return true;
        }

        private static bool TryBuildMonitorValues(
            MonitorTrip trip,
            bool active,
            int tripOrder,
            out MonitorTripElement tripValue,
            out MonitorStopElement[] stopValues)
        {
            tripValue = default;
            stopValues = null;
            if (trip == null
                || string.IsNullOrEmpty(trip.Key)
                || tripOrder < 0
                || tripOrder == int.MaxValue
                || trip.Stops.Count == 0
                || trip.Stops.Count > 256)
            {
                return false;
            }
            try
            {
                tripValue = MonitorTripValue(trip, active, tripOrder);
                stopValues = new MonitorStopElement[trip.Stops.Count];
                for (int i = 0; i < trip.Stops.Count; i++)
                {
                    MonitorStop stop = trip.Stops[i];
                    if (stop == null || string.IsNullOrEmpty(stop.StopKey))
                        return false;
                    stopValues[i] = new MonitorStopElement
                    {
                        m_Version = 1,
                        m_TripOrder = tripOrder,
                        m_StopOrder = i,
                        m_StopKey = stop.StopKey,
                        m_Station = stop.Station,
                        m_WaypointIndex = stop.WaypointIndex,
                        m_PlannedArrival = stop.PlannedArrival,
                        m_PlannedDeparture = stop.PlannedDeparture,
                        m_ActualArrival = stop.ActualArrival,
                        m_ActualDeparture = stop.ActualDeparture,
                        m_Cleared = stop.Cleared ? 1 : 0
                    };
                }
                return true;
            }
            catch
            {
                tripValue = default;
                stopValues = null;
                return false;
            }
        }

        private static MonitorTripElement MonitorTripValue(
            MonitorTrip trip,
            bool active,
            int tripOrder)
        {
            return new MonitorTripElement
            {
                m_Version = 1,
                m_TripOrder = tripOrder,
                m_Active = active ? 1 : 0,
                m_Key = trip.Key,
                m_LineKey = trip.LineKey,
                m_LineId = trip.LineId,
                m_RowId = trip.RowId,
                m_ServiceKind = trip.ServiceKind,
                m_StopSig = trip.StopSig,
                m_Line = trip.Line,
                m_Vehicle = trip.Vehicle,
                m_ServiceDateKey = trip.ServiceDateKey,
                m_SlotMinute = trip.SlotMinute,
                m_ActualStartMinute = trip.ActualStartMinute,
                m_NextArrivalOrder = trip.NextArrivalOrder,
                m_VisibleStopCount = trip.VisibleStopCount,
                m_SuppressPlanFrom = trip.SuppressPlanFrom,
                m_State = (int)trip.State,
                m_LaunchFrame = trip.LaunchFrame,
                m_UpdatedFrame = trip.UpdatedFrame,
                m_StopCount = trip.Stops.Count
            };
        }

        private void ResetMonitorIndices()
        {
            m_MonitorTripOrders.Clear();
            m_MonitorTripIndices.Clear();
            m_MonitorStopIndices.Clear();
            m_MonitorTripOrderCounts.Clear();
            m_MonitorTripKeyCounts.Clear();
            m_MonitorStopOrderCounts.Clear();
            m_MonitorStopTripCounts.Clear();
            m_NextMonitorTripOrder = 0;
        }

        private static void IncrementCount<TKey>(Dictionary<TKey, int> counts, TKey key)
        {
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        private static long MonitorStopIndexKey(int tripOrder, int stopOrder)
        {
            return ((long)tripOrder << 32) | (uint)stopOrder;
        }

        private static bool ValidMonitorDate(int dateKey)
        {
            try
            {
                _ = new DateTime(dateKey / 10000, dateKey / 100 % 100, dateKey % 100);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void LoadDwell()
        {
            RestoreDwellCore();
        }

        public void LoadStationDwell()
        {
            RestoreStationDwellCore();
        }

        public void LoadSlice()
        {
            RestoreSliceCore();
        }

        public void LoadBusSeg()
        {
            RestoreBusSegCore();
        }

        public void LoadRailSegments()
        {
            EnsureRailSegments();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<RailSegmentObservationElement>(city)
                || m_Runtime.m_ObsRecorder == null)
            {
                return;
            }

            DynamicBuffer<RailSegmentObservationElement> buffer =
                m_Runtime.EntityManager.GetBuffer<RailSegmentObservationElement>(city, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                RailSegmentObservationElement element = buffer[i];
                if (!ValidRailSegment(element.m_LineEntity, element.m_FromWaypointEntity, element.m_FromStopEntity,
                    element.m_ToWaypointEntity, element.m_ToStopEntity, element.m_AverageFrames, element.m_SampleCount))
                {
                    continue;
                }

                m_Runtime.m_ObsRecorder.RestoreRailSegment(
                    new RailSegmentKey(
                        element.m_LineEntity,
                        element.m_FromWaypointEntity,
                        element.m_FromStopEntity,
                        element.m_ToWaypointEntity,
                        element.m_ToStopEntity),
                    element.m_AverageFrames,
                    element.m_SampleCount,
                    element.m_LastObservedFrame);
            }
        }

        public void FlushRailSegments()
        {
            if (m_Runtime.m_ObsRecorder == null)
                return;

            EnsureRailSegments();
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<RailSegmentObservationElement>(city))
            {
                return;
            }

            DynamicBuffer<RailSegmentObservationElement> buffer =
                m_Runtime.EntityManager.GetBuffer<RailSegmentObservationElement>(city);
            buffer.Clear();
            foreach (KeyValuePair<RailSegmentKey, RailSegmentObservation> entry in
                m_Runtime.m_ObsRecorder.RailSegmentValues)
            {
                RailSegmentKey key = entry.Key;
                RailSegmentObservation observation = entry.Value;
                if (observation == null
                    || !ValidRailSegment(key.Line, key.FromWaypoint, key.FromStop, key.ToWaypoint, key.ToStop,
                        observation.AverageFrames, observation.SampleCount))
                {
                    continue;
                }

                buffer.Add(new RailSegmentObservationElement
                {
                    m_LineEntity = key.Line,
                    m_FromWaypointEntity = key.FromWaypoint,
                    m_FromStopEntity = key.FromStop,
                    m_ToWaypointEntity = key.ToWaypoint,
                    m_ToStopEntity = key.ToStop,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                });
            }
        }

        internal bool TrySliceSignature(Entity line, out ulong signature)
        {
            bool success = TrySliceSignatures(line, out signature, out _);
            return success;
        }

        public void Flush(Entity line, int index, DwellObservation observation)
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled())
                return;

            if (line == Entity.Null
                || index < 0
                || !(observation.AverageFrames > 0f)
                || observation.SampleCount <= 0
                || !m_Runtime.m_DwellObservationBufferReady)
            {
                return;
            }

            if (!TryGetSignature(line, out ulong profileSignature))
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                return;

            DynamicBuffer<DwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<DwellObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_WaypointIndex != index)
                    continue;

                buffer[i] = new DwellObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_WaypointIndex = index,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount
                };
                return;
            }

            buffer.Add(new DwellObservationElement
            {
                m_LineEntity = line,
                m_ProfileSignature = profileSignature,
                m_WaypointIndex = index,
                m_AverageFrames = observation.AverageFrames,
                m_SampleCount = observation.SampleCount
            });
        }

        public void Flush(string key, StationDwellObservation observation)
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled())
                return;

            if (string.IsNullOrWhiteSpace(key)
                || !Capture.IsStationDwellKey(key)
                || !(observation.AverageFrames > 0f)
                || observation.SampleCount <= 0
                || !m_Runtime.m_StationDwellObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                return;

            DynamicBuffer<StationDwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<StationDwellObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (!string.Equals(buffer[i].m_StationAnchorId.ToString(), key, System.StringComparison.Ordinal))
                    continue;

                buffer[i] = new StationDwellObservationElement
                {
                    m_StationAnchorId = key,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                };
                return;
            }

            buffer.Add(new StationDwellObservationElement
            {
                m_StationAnchorId = key,
                m_AverageFrames = observation.AverageFrames,
                m_SampleCount = observation.SampleCount,
                m_LastObservedFrame = observation.LastObservedFrame
            });
        }

        public void Flush(Entity line, int index, TraversalSliceObservation observation)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (line == Entity.Null || index < 0 || observation.SampleCount <= 0)
                return;

            if (!TrySliceSignatures(line, out ulong profileSignature, out _))
                return;

            if (!m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_LineEntity != line || buffer[i].m_SliceIndex != index)
                    continue;

                buffer[i] = new TraversalSliceObservationElement
                {
                    m_LineEntity = line,
                    m_ProfileSignature = profileSignature,
                    m_SliceIndex = index,
                    m_AverageFrames = observation.AverageFrames,
                    m_FastBaselineFrames = observation.FastBaselineFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                };
                return;
            }

            buffer.Add(new TraversalSliceObservationElement
            {
                m_LineEntity = line,
                m_ProfileSignature = profileSignature,
                m_SliceIndex = index,
                m_AverageFrames = observation.AverageFrames,
                m_FastBaselineFrames = observation.FastBaselineFrames,
                m_SampleCount = observation.SampleCount,
                m_LastObservedFrame = observation.LastObservedFrame
            });
        }

        public void SyncBusSeg(Entity line)
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || line == Entity.Null
                || !m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
            {
                return;
            }

            DynamicBuffer<BusSegObservationElement> observations =
                m_Runtime.EntityManager.GetBuffer<BusSegObservationElement>(city);
            for (int i = observations.Length - 1; i >= 0; i--)
            {
                if (observations[i].m_LineEntity == line)
                    observations.RemoveAt(i);
            }

            if (IsBusLine(line))
            {
                foreach (KeyValuePair<BusSegKey, BusSegObservation> pair in m_Runtime.m_ObsQuery.BusSegs)
                {
                    BusSegKey key = pair.Key;
                    BusSegObservation observation = pair.Value;
                    if (key.Line != line || !ValidBusObservation(key, observation))
                        continue;

                    observations.Add(new BusSegObservationElement
                    {
                        m_LineEntity = key.Line,
                        m_FromWaypointEntity = key.FromWaypoint,
                        m_FromStopEntity = key.FromStop,
                        m_ToWaypointEntity = key.ToWaypoint,
                        m_ToStopEntity = key.ToStop,
                        m_EstimatedFrames = observation.EstimatedFrames,
                        m_SampleCount = observation.SampleCount
                    });
                }
            }

            DynamicBuffer<BusRouteSnapshotElement> routes =
                m_Runtime.EntityManager.GetBuffer<BusRouteSnapshotElement>(city);
            for (int i = routes.Length - 1; i >= 0; i--)
            {
                if (routes[i].m_LineEntity == line)
                    routes.RemoveAt(i);
            }

            if (!TryBusRoute(line, out LineProfile.RoadRouteSnapshot snapshot))
                return;

            for (int i = 0; i < snapshot.Waypoints.Length; i++)
            {
                routes.Add(new BusRouteSnapshotElement
                {
                    m_LineEntity = line,
                    m_Order = i,
                    m_WaypointEntity = snapshot.Waypoints[i],
                    m_ResolvedStopEntity = snapshot.Stops[i]
                });
            }
        }

        public void RemoveSliceLine(Entity line)
        {
            if (line == Entity.Null || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i].m_LineEntity == line)
                    buffer.RemoveAt(i);
            }
        }

        internal bool TryFlushDailyQuota(LineKey lak, TraversalSliceDailyQuota quota)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled()
                || lak.IsEmpty
                || !m_Runtime.m_TraversalSliceObservationBufferReady)
            {
                return false;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
                return false;

            DynamicBuffer<TraversalSliceQuotaElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceQuotaElement>(city);
            string lineKey = lak.ToString();
            int found = -1;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }
            for (int i = buffer.Length - 1; i > found; i--)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
            }

            TraversalSliceQuotaElement entry = new TraversalSliceQuotaElement
            {
                m_Version = 1,
                m_LineKey = lineKey,
                m_DateKey = quota.DateKey,
                m_UsedCount = quota.UsedCount
            };
            if (found >= 0)
                buffer[found] = entry;
            else
                buffer.Add(entry);
            return true;
        }

        internal bool TryFlushColdStart(LineKey lak, TraversalSliceColdStart coldStart)
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled()
                || lak.IsEmpty
                || !m_Runtime.m_TraversalSliceObservationBufferReady)
            {
                return false;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                return false;

            DynamicBuffer<TraversalSliceColdStartElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city);
            string lineKey = lak.ToString();
            int found = -1;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }
            for (int i = buffer.Length - 1; i > found; i--)
            {
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
            }

            TraversalSliceColdStartElement entry = new TraversalSliceColdStartElement
            {
                m_Version = 2,
                m_LineKey = lineKey,
                m_ProfileSignature = coldStart.ProfileSignature,
                m_Remaining = coldStart.Remaining,
                m_PendingFinalMinute = coldStart.PendingFinalMinute,
                m_PendingFinalDateKey = coldStart.PendingFinalDateKey
            };
            if (found >= 0)
                buffer[found] = entry;
            else
                buffer.Add(entry);
            return true;
        }

        internal void RemoveColdStart(LineKey lak)
        {
            if (lak.IsEmpty || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                return;

            DynamicBuffer<TraversalSliceColdStartElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city);
            string lineKey = lak.ToString();
            for (int i = buffer.Length - 1; i >= 0; i--)
                if (string.Equals(buffer[i].m_LineKey.ToString(), lineKey, System.StringComparison.Ordinal))
                    buffer.RemoveAt(i);
        }

        public bool TryWaypointPosition(Entity waypoint, out float3 position)
        {
            return m_Runtime.m_MileageStore.TryWaypointPosition(waypoint, out position);
        }

        private void EnsureDwellCore()
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled() || m_Runtime.m_DwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<DwellObservationElement>(city);

            m_Runtime.m_DwellObservationBufferReady = true;
        }

        private void RestoreDwellCore()
        {
            if (!ModRuntimeHostSystem.IsDwellObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_DwellObservationCacheLoaded || !m_Runtime.m_DwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<DwellObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearWaypointDwell();
            DynamicBuffer<DwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<DwellObservationElement>(city, true);
            int restoredCount = 0;
            int restoredByLegacyTopologyCount = 0;
            int skippedSignatureMismatchCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                DwellObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null
                    || entry.m_WaypointIndex < 0
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    continue;
                }

                bool signatureMatched = TryGetSignature(entry.m_LineEntity, out ulong currentSignature)
                    && currentSignature == entry.m_ProfileSignature;
                if (!signatureMatched && !CanRestoreLegacy(entry.m_LineEntity, entry.m_WaypointIndex))
                {
                    skippedSignatureMismatchCount++;
                    continue;
                }
                if (!signatureMatched)
                    restoredByLegacyTopologyCount++;

                m_Runtime.m_ObsPersist.PutWaypointDwell(
                    Keys.WaypointDwell(entry.m_LineEntity, entry.m_WaypointIndex),
                    new DwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount)
                    });
                restoredCount++;
            }

            m_Runtime.m_DwellObservationCacheLoaded = true;
            m_Runtime.m_LastStationStopDwellLegacyBufferCount = buffer.Length;
            m_Runtime.m_LastStationStopDwellLegacyRestoredCount = restoredCount;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] DwellObservations buffer=" + buffer.Length
                    + " restored=" + restoredCount
                    + " legacyTopologyFallback=" + restoredByLegacyTopologyCount
                    + " skippedSignatureMismatch=" + skippedSignatureMismatchCount);
            }
        }

        private void EnsureStationDwellCore()
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled() || m_Runtime.m_StationDwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<StationDwellObservationElement>(city);

            m_Runtime.m_StationDwellObservationBufferReady = true;
        }

        private void RestoreStationDwellCore()
        {
            if (!ModRuntimeHostSystem.IsStationDwellObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_StationDwellObservationCacheLoaded || !m_Runtime.m_StationDwellObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<StationDwellObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearStationDwell();
            DynamicBuffer<StationDwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<StationDwellObservationElement>(city, true);
            int restoredCount = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                StationDwellObservationElement entry = buffer[i];
                string observationKey = entry.m_StationAnchorId.ToString();
                if (string.IsNullOrWhiteSpace(observationKey)
                    || !Capture.IsStationDwellKey(observationKey)
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    continue;
                }

                m_Runtime.m_ObsPersist.PutStationDwell(
                    observationKey,
                    new StationDwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount),
                        LastObservedFrame = entry.m_LastObservedFrame
                    });
                restoredCount++;
            }

            m_Runtime.m_StationDwellObservationCacheLoaded = true;
            m_Runtime.m_LastStationStopDwellAnchorBufferCount = buffer.Length;
            m_Runtime.m_LastStationStopDwellAnchorRestoredCount = restoredCount;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[StopDwellAnchorRestore] anchorBuffer=" + buffer.Length
                    + " anchorRestored=" + restoredCount
                    + " legacyBuffer=" + m_Runtime.m_LastStationStopDwellLegacyBufferCount
                    + " legacyRestored=" + m_Runtime.m_LastStationStopDwellLegacyRestoredCount
                    + " legacyPreserved=1");
            }
        }

        private void EnsureSliceCore()
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled() || m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceObservationElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceQuotaElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
                m_Runtime.EntityManager.AddBuffer<TraversalSliceColdStartElement>(city);

            m_Runtime.m_TraversalSliceObservationBufferReady = true;
        }

        private void EnsureBusSegCore()
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;

            if (!m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city))
                m_Runtime.EntityManager.AddBuffer<BusSegObservationElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
                m_Runtime.EntityManager.AddBuffer<BusRouteSnapshotElement>(city);

            m_Runtime.m_BusSegObservationBufferReady = true;
        }

        private void RestoreSliceCore()
        {
            if (!ModRuntimeHostSystem.IsTraversalSliceObservationPersistenceEnabled())
                return;

            if (m_Runtime.m_TraversalSliceObservationCacheLoaded || !m_Runtime.m_TraversalSliceObservationBufferReady)
                return;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null || !m_Runtime.EntityManager.HasBuffer<TraversalSliceObservationElement>(city))
                return;

            m_Runtime.m_ObsPersist.ClearSliceObservations();
            DynamicBuffer<TraversalSliceObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<TraversalSliceObservationElement>(city);
            int storedCount = buffer.Length;
            int restoredCount = 0;
            int legacyRestoredCount = 0;
            int removedMismatchCount = 0;
            int unavailableCount = 0;
            int removedDuplicateCount = 0;
            int removedInvalidCount = 0;
            Dictionary<Entity, ulong> geometrySignatures = new Dictionary<Entity, ulong>();
            Dictionary<Entity, ulong> legacySignatures = new Dictionary<Entity, ulong>();
            HashSet<Entity> unavailableLines = new HashSet<Entity>();
            Dictionary<ulong, int> winners = new Dictionary<ulong, int>();
            for (int i = 0; i < buffer.Length; i++)
            {
                TraversalSliceObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_SliceIndex < 0)
                    continue;

                if (!geometrySignatures.ContainsKey(entry.m_LineEntity)
                    && !unavailableLines.Contains(entry.m_LineEntity))
                {
                    if (TrySliceSignatures(entry.m_LineEntity, out ulong geometrySignature, out ulong legacySignature))
                    {
                        geometrySignatures[entry.m_LineEntity] = geometrySignature;
                        legacySignatures[entry.m_LineEntity] = legacySignature;
                    }
                    else
                    {
                        unavailableLines.Add(entry.m_LineEntity);
                    }
                }

                ulong key = Keys.Slice(entry.m_LineEntity, entry.m_SliceIndex);
                if (!winners.TryGetValue(key, out int winner)
                    || IsBetterSlice(
                        entry,
                        buffer[winner],
                        unavailableLines.Contains(entry.m_LineEntity),
                        geometrySignatures.TryGetValue(entry.m_LineEntity, out ulong geometry) ? geometry : 0UL,
                        legacySignatures.TryGetValue(entry.m_LineEntity, out ulong legacy) ? legacy : 0UL))
                    winners[key] = i;
            }

            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                TraversalSliceObservationElement entry = buffer[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_SliceIndex < 0)
                {
                    buffer.RemoveAt(i);
                    removedInvalidCount++;
                    continue;
                }

                ulong key = Keys.Slice(entry.m_LineEntity, entry.m_SliceIndex);
                if (winners[key] != i)
                {
                    buffer.RemoveAt(i);
                    removedDuplicateCount++;
                    continue;
                }

                if (unavailableLines.Contains(entry.m_LineEntity))
                {
                    unavailableCount++;
                    continue;
                }

                ulong geometrySignature = geometrySignatures[entry.m_LineEntity];
                if (entry.m_ProfileSignature != geometrySignature)
                {
                    if (entry.m_ProfileSignature != legacySignatures[entry.m_LineEntity])
                    {
                        buffer.RemoveAt(i);
                        removedMismatchCount++;
                        continue;
                    }

                    entry.m_ProfileSignature = geometrySignature;
                    buffer[i] = entry;
                    legacyRestoredCount++;
                }

                m_Runtime.m_ObsPersist.PutSlice(
                    entry.m_LineEntity,
                    key,
                    new TraversalSliceObservation(
                        entry.m_AverageFrames,
                        entry.m_FastBaselineFrames > 0f ? entry.m_FastBaselineFrames : entry.m_AverageFrames,
                        math.max(0, entry.m_SampleCount),
                        entry.m_LastObservedFrame));
                restoredCount++;
            }

            m_Runtime.m_ObsPersist.ClearAdmissionState();
            if (m_Runtime.EntityManager.HasBuffer<TraversalSliceQuotaElement>(city))
            {
                DynamicBuffer<TraversalSliceQuotaElement> quotas = m_Runtime.EntityManager.GetBuffer<TraversalSliceQuotaElement>(city, true);
                for (int i = 0; i < quotas.Length; i++)
                {
                    TraversalSliceQuotaElement entry = quotas[i];
                    if (entry.m_Version != 1
                        || !LineKey.TryParse(entry.m_LineKey.ToString(), out LineKey lak)
                        || !LineKey.IsStableGuidKey(lak))
                    {
                        continue;
                    }
                    m_Runtime.m_ObsPersist.PutDailyQuota(lak, entry.m_DateKey, math.clamp(entry.m_UsedCount, 0, 4));
                }
            }
            if (m_Runtime.EntityManager.HasBuffer<TraversalSliceColdStartElement>(city))
            {
                DynamicBuffer<TraversalSliceColdStartElement> coldStarts = m_Runtime.EntityManager.GetBuffer<TraversalSliceColdStartElement>(city, true);
                for (int i = 0; i < coldStarts.Length; i++)
                {
                    TraversalSliceColdStartElement entry = coldStarts[i];
                    if ((entry.m_Version != 1 && entry.m_Version != 2)
                        || entry.m_Remaining < 0
                        || entry.m_Remaining > 3
                        || !LineKey.TryParse(entry.m_LineKey.ToString(), out LineKey lak)
                        || !LineKey.IsStableGuidKey(lak))
                    {
                        continue;
                    }
                    m_Runtime.m_ObsPersist.PutColdStart(
                        lak,
                        entry.m_ProfileSignature,
                        entry.m_Remaining,
                        entry.m_PendingFinalMinute,
                        entry.m_PendingFinalDateKey);
                }
            }

            m_Runtime.m_TraversalSliceObservationCacheLoaded = true;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] TraversalSliceObservations buffer=" + storedCount
                    + " restored=" + restoredCount
                    + " legacyRestored=" + legacyRestoredCount
                    + " removedMismatch=" + removedMismatchCount
                    + " unavailable=" + unavailableCount
                    + " removedDuplicate=" + removedDuplicateCount
                    + " removedInvalid=" + removedInvalidCount);
            }
        }

        private void RestoreBusSegCore()
        {
            if (!ModRuntimeHostSystem.IsBusSegObservationPersistenceEnabled()
                || m_Runtime.m_BusSegObservationCacheLoaded
                || !m_Runtime.m_BusSegObservationBufferReady)
            {
                return;
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<BusSegObservationElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<BusRouteSnapshotElement>(city))
            {
                return;
            }

            m_Runtime.m_ObsPersist.ClearBusSeg();
            DynamicBuffer<BusSegObservationElement> observations =
                m_Runtime.EntityManager.GetBuffer<BusSegObservationElement>(city, true);
            DynamicBuffer<BusRouteSnapshotElement> routeEntries =
                m_Runtime.EntityManager.GetBuffer<BusRouteSnapshotElement>(city, true);
            var savedRoutes = new Dictionary<Entity, List<BusRouteSnapshotElement>>();
            for (int i = 0; i < routeEntries.Length; i++)
            {
                BusRouteSnapshotElement entry = routeEntries[i];
                if (entry.m_LineEntity == Entity.Null || entry.m_Order < 0)
                    continue;

                if (!savedRoutes.TryGetValue(entry.m_LineEntity, out List<BusRouteSnapshotElement> entries))
                {
                    entries = new List<BusRouteSnapshotElement>();
                    savedRoutes[entry.m_LineEntity] = entries;
                }
                entries.Add(entry);
            }

            var currentRoutes = new Dictionary<Entity, LineProfile.RoadRouteSnapshot>();
            var invalidLines = new HashSet<Entity>();
            int restored = 0;
            for (int i = 0; i < observations.Length; i++)
            {
                BusSegObservationElement entry = observations[i];
                BusSegKey key = new BusSegKey(
                    entry.m_LineEntity,
                    entry.m_FromWaypointEntity,
                    entry.m_FromStopEntity,
                    entry.m_ToWaypointEntity,
                    entry.m_ToStopEntity);
                BusSegObservation observation = new BusSegObservation(
                    entry.m_EstimatedFrames,
                    entry.m_SampleCount);
                if (!ValidBusObservation(key, observation)
                    || !savedRoutes.TryGetValue(key.Line, out List<BusRouteSnapshotElement> entries)
                    || !TrySnapshot(entries, out LineProfile.RoadRouteSnapshot saved))
                {
                    continue;
                }

                if (!currentRoutes.TryGetValue(key.Line, out LineProfile.RoadRouteSnapshot current))
                {
                    if (invalidLines.Contains(key.Line) || !TryBusRoute(key.Line, out current))
                    {
                        invalidLines.Add(key.Line);
                        continue;
                    }
                    currentRoutes[key.Line] = current;
                }

                if (!BusSegCapture.MatchesSegment(key, saved, current))
                    continue;

                m_Runtime.m_ObsPersist.PutBusSeg(key, observation);
                restored++;
            }

            m_Runtime.m_BusSegObservationCacheLoaded = true;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[恢复] BusSegObservations buffer=" + observations.Length
                    + " restored=" + restored);
            }
        }

        private bool IsBusLine(Entity line)
        {
            return line != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && TransportModeResolver.Resolve(m_Runtime.EntityManager, line) == TransitMode.Bus;
        }

        private bool ValidBusObservation(BusSegKey key, BusSegObservation observation)
        {
            return IsBusLine(key.Line)
                && key.FromWaypoint != Entity.Null
                && key.FromStop != Entity.Null
                && key.ToWaypoint != Entity.Null
                && key.ToStop != Entity.Null
                && m_Runtime.EntityManager.Exists(key.FromWaypoint)
                && m_Runtime.EntityManager.Exists(key.FromStop)
                && m_Runtime.EntityManager.Exists(key.ToWaypoint)
                && m_Runtime.EntityManager.Exists(key.ToStop)
                && math.isfinite(observation.EstimatedFrames)
                && observation.EstimatedFrames > 0f
                && observation.SampleCount > 0
                && observation.SampleCount <= 32;
        }

        private bool TryBusRoute(Entity line, out LineProfile.RoadRouteSnapshot snapshot)
        {
            snapshot = null;
            if (!IsBusLine(line) || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints =
                m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length < 2)
                return false;

            snapshot = new LineProfile.RoadRouteSnapshot
            {
                Waypoints = new Entity[waypoints.Length],
                Stops = new Entity[waypoints.Length]
            };
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;
                if (waypoint == Entity.Null || !m_Runtime.EntityManager.Exists(waypoint))
                    return false;

                snapshot.Waypoints[i] = waypoint;
                snapshot.Stops[i] = m_Runtime.m_Resolve.Stop(waypoint);
            }

            return true;
        }

        private bool TrySnapshot(
            List<BusRouteSnapshotElement> entries,
            out LineProfile.RoadRouteSnapshot snapshot)
        {
            snapshot = null;
            if (entries == null || entries.Count < 2)
                return false;

            int last = -1;
            for (int i = 0; i < entries.Count; i++)
                last = math.max(last, entries[i].m_Order);
            if (last != entries.Count - 1)
                return false;

            var seen = new bool[entries.Count];
            snapshot = new LineProfile.RoadRouteSnapshot
            {
                Waypoints = new Entity[entries.Count],
                Stops = new Entity[entries.Count]
            };
            for (int i = 0; i < entries.Count; i++)
            {
                BusRouteSnapshotElement entry = entries[i];
                if (entry.m_Order < 0
                    || entry.m_Order >= entries.Count
                    || seen[entry.m_Order]
                    || entry.m_WaypointEntity == Entity.Null
                    || !m_Runtime.EntityManager.Exists(entry.m_WaypointEntity)
                    || (entry.m_ResolvedStopEntity != Entity.Null
                        && !m_Runtime.EntityManager.Exists(entry.m_ResolvedStopEntity)))
                {
                    snapshot = null;
                    return false;
                }

                seen[entry.m_Order] = true;
                snapshot.Waypoints[entry.m_Order] = entry.m_WaypointEntity;
                snapshot.Stops[entry.m_Order] = entry.m_ResolvedStopEntity;
            }

            return true;
        }

        private static bool IsBetterSlice(
            TraversalSliceObservationElement candidate,
            TraversalSliceObservationElement current,
            bool signatureUnavailable,
            ulong geometrySignature,
            ulong legacySignature)
        {
            int candidateRank = SliceSignatureRank(
                candidate.m_ProfileSignature,
                signatureUnavailable,
                geometrySignature,
                legacySignature);
            int currentRank = SliceSignatureRank(
                current.m_ProfileSignature,
                signatureUnavailable,
                geometrySignature,
                legacySignature);
            return candidateRank > currentRank
                || (candidateRank == currentRank
                    && (candidate.m_SampleCount > current.m_SampleCount
                || (candidate.m_SampleCount == current.m_SampleCount
                        && candidate.m_LastObservedFrame > current.m_LastObservedFrame)));
        }

        private static int SliceSignatureRank(
            ulong storedSignature,
            bool signatureUnavailable,
            ulong geometrySignature,
            ulong legacySignature)
        {
            if (signatureUnavailable)
                return 0;
            if (storedSignature == geometrySignature)
                return 2;
            return storedSignature == legacySignature ? 1 : 0;
        }

        private bool TrySliceSignatures(Entity line, out ulong geometry, out ulong legacyFull)
        {
            geometry = 0UL;
            legacyFull = 0UL;
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            BufferLookup<RouteSegment> segmentBuffers = m_Runtime.GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            geometry = SignatureSeed;
            legacyFull = SignatureSeed;
            geometry = m_Runtime.m_LineProfile.MixSignature(geometry, waypoints.Length);
            geometry = m_Runtime.m_LineProfile.MixSignature(geometry, segments.Length);
            legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, waypoints.Length);
            legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, segments.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, i);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, i);
                Entity waypointEntity = waypoints[i].m_Waypoint;
                int waypointIndex = -1;
                if (waypointEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypointEntity)
                    && m_Runtime.EntityManager.HasComponent<Waypoint>(waypointEntity))
                {
                    waypointIndex = m_Runtime.EntityManager.GetComponentData<Waypoint>(waypointEntity).m_Index;
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, waypointIndex);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, waypointIndex);

                int positionX = 0;
                int positionY = 0;
                int positionZ = 0;
                if (TryWaypointPosition(waypointEntity, out float3 waypointPosition))
                {
                    positionX = Quantize(waypointPosition.x);
                    positionY = Quantize(waypointPosition.y);
                    positionZ = Quantize(waypointPosition.z);
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionX);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionY);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, positionZ);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionX);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionY);
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, positionZ);

                int startCurve = 0;
                int endCurve = 0;
                if (m_Runtime.EntityManager.HasComponent<RouteLane>(waypointEntity))
                {
                    RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(waypointEntity);
                    startCurve = (int)math.round(routeLane.m_StartCurvePos * 1000f);
                    endCurve = (int)math.round(routeLane.m_EndCurvePos * 1000f);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, routeLane.m_StartLane.Index);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, routeLane.m_EndLane.Index);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, startCurve);
                    legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, endCurve);
                }
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, startCurve);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, endCurve);

                Entity segmentEntity = segments[i].m_Segment;
                float durationSeconds = 0f;
                if (segmentEntity == Entity.Null
                    || !m_Runtime.EntityManager.Exists(segmentEntity)
                    || !m_Runtime.EntityManager.HasComponent<PathInformation>(segmentEntity))
                {
                    durationSeconds = 0f;
                }
                else
                {
                    durationSeconds = math.max(0f, m_Runtime.EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Duration);
                }
                int distance = Quantize(m_Runtime.m_LineMileage.ReadSegment(segmentEntity, waypoints, i));
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, Quantize(durationSeconds));
                legacyFull = m_Runtime.m_LineProfile.MixSignature(legacyFull, distance);
                geometry = m_Runtime.m_LineProfile.MixSignature(geometry, distance);
            }

            if (TransportModeResolver.Resolve(m_Runtime.EntityManager, line) != TransitMode.Tram)
                return geometry != 0UL && legacyFull != 0UL;

            if (m_Runtime.m_TrackModel == null
                || !m_Runtime.m_TrackModel.TryGetChainForLine(line, waypoints, out LineTrackChain chain)
                || chain == null
                || chain.TraversalProfile == null)
            {
                geometry = 0UL;
                legacyFull = 0UL;
                return false;
            }

            MixTraversalEvents(chain, ref geometry);
            MixTraversalEvents(chain, ref legacyFull);

            return geometry != 0UL && legacyFull != 0UL;
        }

        private void MixTraversalEvents(LineTrackChain chain, ref ulong signature)
        {
            signature = m_Runtime.m_LineProfile.MixSignature(signature, chain.TraversalProfile.Events.Count);
            for (int eventIndex = 0; eventIndex < chain.TraversalProfile.Events.Count; eventIndex++)
            {
                TraversalEvent traversalEvent = chain.TraversalProfile.Events[eventIndex];
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.EventIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, (int)traversalEvent.Kind);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    traversalEvent.Building == Entity.Null ? -1 : traversalEvent.Building.Index);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    traversalEvent.Building == Entity.Null ? -1 : traversalEvent.Building.Version);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.WaypointIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.PassIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.StartAtomIndex);
                signature = m_Runtime.m_LineProfile.MixSignature(signature, traversalEvent.EndAtomIndexExclusive);
                signature = m_Runtime.m_LineProfile.MixSignature(
                    signature,
                    (int)math.round(traversalEvent.StopFrames * 10f));
            }
        }

        private bool TryGetSignature(Entity line, out ulong signature)
        {
            signature = 0UL;
            if (line == Entity.Null || !m_Runtime.EntityManager.Exists(line) || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;

            BufferLookup<RouteSegment> segmentBuffers = m_Runtime.GetBufferLookup<RouteSegment>(true);
            if (!segmentBuffers.TryGetBuffer(line, out DynamicBuffer<RouteSegment> segments))
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypoints.Length == 0 || segments.Length != waypoints.Length)
                return false;

            signature = ComputeSignature(waypoints, segments);
            return signature != 0UL;
        }

        private ulong ComputeSignature(DynamicBuffer<RouteWaypoint> waypoints, DynamicBuffer<RouteSegment> segments)
        {
            ulong hash = SignatureSeed;
            hash = m_Runtime.m_LineProfile.MixSignature(hash, waypoints.Length);
            hash = m_Runtime.m_LineProfile.MixSignature(hash, segments.Length);
            int count = math.min(waypoints.Length, segments.Length);
            for (int i = 0; i < count; i++)
            {
                hash = m_Runtime.m_LineProfile.MixSignature(hash, i);

                Entity waypointEntity = waypoints[i].m_Waypoint;
                if (waypointEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(waypointEntity)
                    && m_Runtime.EntityManager.HasComponent<Waypoint>(waypointEntity))
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, m_Runtime.EntityManager.GetComponentData<Waypoint>(waypointEntity).m_Index);
                }
                else
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, -1);
                }

                if (TryWaypointPosition(waypointEntity, out float3 waypointPosition))
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.x));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.y));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(waypointPosition.z));
                }
                else
                {
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, 0);
                }

                if (m_Runtime.EntityManager.HasComponent<RouteLane>(waypointEntity))
                {
                    RouteLane routeLane = m_Runtime.EntityManager.GetComponentData<RouteLane>(waypointEntity);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, routeLane.m_StartLane.Index);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, routeLane.m_EndLane.Index);
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, (int)math.round(routeLane.m_StartCurvePos * 1000f));
                    hash = m_Runtime.m_LineProfile.MixSignature(hash, (int)math.round(routeLane.m_EndCurvePos * 1000f));
                }

                Entity segmentEntity = segments[i].m_Segment;
                float durationSeconds = 0f;
                if (segmentEntity != Entity.Null
                    && m_Runtime.EntityManager.Exists(segmentEntity)
                    && m_Runtime.EntityManager.HasComponent<PathInformation>(segmentEntity))
                {
                    durationSeconds = math.max(0f, m_Runtime.EntityManager.GetComponentData<PathInformation>(segmentEntity).m_Duration);
                }

                hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(durationSeconds));
                hash = m_Runtime.m_LineProfile.MixSignature(hash, Quantize(m_Runtime.m_LineMileage.ReadSegment(segmentEntity, waypoints, i)));
            }

            return hash;
        }

        private static int Quantize(float value)
        {
            if (!math.isfinite(value))
                return 0;

            return (int)math.round(value * 10f);
        }

        private bool ValidRailSegment(
            Entity line,
            Entity fromWaypoint,
            Entity fromStop,
            Entity toWaypoint,
            Entity toStop,
            float averageFrames,
            int sampleCount)
        {
            return line != Entity.Null
                && fromWaypoint != Entity.Null
                && fromStop != Entity.Null
                && toWaypoint != Entity.Null
                && toStop != Entity.Null
                && m_Runtime.EntityManager.Exists(line)
                && m_Runtime.EntityManager.Exists(fromWaypoint)
                && m_Runtime.EntityManager.Exists(fromStop)
                && m_Runtime.EntityManager.Exists(toWaypoint)
                && m_Runtime.EntityManager.Exists(toStop)
                && TransportModeProfile.GetProfile(
                    TransportModeResolver.Resolve(m_Runtime.EntityManager, line)).Lifecycle == LifecycleKind.Rail
                && math.isfinite(averageFrames)
                && averageFrames > 0f
                && sampleCount > 0;
        }

        private bool CanRestoreLegacy(Entity line, int waypointIndex)
        {
            if (line == Entity.Null
                || !m_Runtime.EntityManager.Exists(line)
                || waypointIndex < 0
                || !m_Runtime.EntityManager.HasBuffer<RouteWaypoint>(line))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints = m_Runtime.EntityManager.GetBuffer<RouteWaypoint>(line, true);
            if (waypointIndex >= waypoints.Length)
                return false;

            Entity stopEntity = m_Runtime.m_Resolve.Stop(waypoints[waypointIndex].m_Waypoint);
            return stopEntity != Entity.Null && m_Runtime.EntityManager.Exists(stopEntity);
        }
    }
}
