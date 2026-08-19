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
        private const int MonitorVersion = 2;
        private const int MonitorStopVersion = 3;
        private const int MonitorTripVersion = 4;
        private const int MonitorAverageVersion = 2;
        private const int MaxMonitorDateSlots = 2;
        private const int MaxMonitorTripsPerDate = 4096;
        private const int MaxMonitorActiveTrips = 1024;
        private const int MaxMonitorTrips = MaxMonitorTripsPerDate * 2 + MaxMonitorActiveTrips;
        private const int MaxMonitorStops = MaxMonitorTrips * 256;
        private const int MaxMonitorAverageSegments = MonitorAverageStore.MaxLines * MonitorAverageStore.MaxSegmentsPerLine;
        private const int MaxRestoreLineLogSlices = 32;
        private readonly ModRuntimeHostSystem m_Runtime;
        private bool m_MonitorPersistenceHealthy = true;
        private bool m_IgnoreLegacyMonitor;

        public Buffers(ModRuntimeHostSystem runtime)
        {
            m_Runtime = runtime;
        }

        internal bool MonitorPersistenceHealthy => m_MonitorPersistenceHealthy;

        public void Ensure()
        {
            EnsureDwell();
            EnsureStationDwell();
            EnsureSlice();
            EnsureBusSeg();
            EnsureMonitorAverages();
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

        public void EnsureMonitorAverages()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;
            if (!m_Runtime.EntityManager.HasBuffer<MonitorAverageLineElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorAverageLineElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<MonitorAverageSegmentElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorAverageSegmentElement>(city);
        }

        public void EnsureMonitor()
        {
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return;
            if (!m_Runtime.EntityManager.HasBuffer<MonitorDateSlotElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorDateSlotElement>(city);
            if (!m_Runtime.EntityManager.HasBuffer<MonitorIntegrityElement>(city))
                m_Runtime.EntityManager.AddBuffer<MonitorIntegrityElement>(city);
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
            LoadMonitor();
            LoadMonitorIntegrity();
            LoadMonitorAverages();
        }

        public void LoadMonitor()
        {
            EnsureMonitor();
            EnsureMonitorAverages();
            m_IgnoreLegacyMonitor = false;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || m_Runtime.m_ObsRecorder == null
                || !m_Runtime.EntityManager.HasBuffer<MonitorDateSlotElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<MonitorTripElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<MonitorStopElement>(city))
            {
                return;
            }

            m_MonitorPersistenceHealthy = true;
            if (HasLegacyMonitor(city))
            {
                m_IgnoreLegacyMonitor = true;
                m_Runtime.m_ObsRecorder.ClearMonitor();
                m_Runtime.m_MonitorAverages.Clear();
                m_Runtime.m_ObsRecorder.TickDate(m_Runtime.m_SimClock.NowDate);
                return;
            }
            m_Runtime.m_ObsRecorder.ClearMonitor();
            DynamicBuffer<MonitorDateSlotElement> slots =
                m_Runtime.EntityManager.GetBuffer<MonitorDateSlotElement>(city, true);
            HashSet<int> slotKeys = new HashSet<int>();
            for (int i = 0; i < slots.Length && slotKeys.Count < 2; i++)
            {
                MonitorDateSlotElement element = slots[i];
                if (element.m_Version != MonitorVersion
                    || element.m_DateKey <= 0
                    || !slotKeys.Add(element.m_DateKey))
                {
                    RecordLoadIssue("monitor-date-slot-corrupt", false);
                    continue;
                }
                m_Runtime.m_ObsRecorder.RestoreDateSlot(element.m_DateKey);
            }

            DynamicBuffer<MonitorTripElement> trips =
                m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city, true);
            DynamicBuffer<MonitorStopElement> stops =
                m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city, true);
            Dictionary<int, List<MonitorStopElement>> stopsByTrip =
                new Dictionary<int, List<MonitorStopElement>>();
            for (int i = 0; i < stops.Length; i++)
            {
                MonitorStopElement stop = stops[i];
                if ((stop.m_Version != MonitorVersion && stop.m_Version != MonitorStopVersion)
                    || stop.m_TripOrder < 0
                    || stop.m_StopOrder < 0)
                {
                    RecordLoadIssue("monitor-stop-corrupt", false);
                    continue;
                }
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
            Dictionary<Entity, MonitorLayout> layouts =
                new Dictionary<Entity, MonitorLayout>();
            for (int i = 0; i < trips.Length; i++)
            {
                MonitorTripElement element = trips[i];
                string elementKey = element.m_Key.ToString();
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
                if (element.m_Version != MonitorTripVersion
                    || (element.m_Active != 0 && element.m_Active != 1)
                    || element.m_TripOrder < 0
                    || element.m_TripOrder == int.MaxValue
                    || !tripOrders.Add(element.m_TripOrder)
                    || !monitorKeys.Add(element.m_Key.ToString())
                    || string.IsNullOrEmpty(element.m_Key.ToString())
                    || string.IsNullOrEmpty(lineKey)
                    || string.IsNullOrEmpty(rowId)
                    || string.IsNullOrEmpty(element.m_StopSig.ToString())
                    || !string.Equals(element.m_Key.ToString(), expectedKey, StringComparison.Ordinal)
                    || !ValidMonitorDate(element.m_ServiceDateKey)
                    || element.m_SlotMinute < 0
                    || element.m_SlotMinute >= 1440
                    || element.m_StopCount <= 0
                    || element.m_StopCount > 256
                    || element.m_NextArrivalOrder < 1
                    || element.m_NextArrivalOrder > element.m_StopCount
                    || element.m_VisibleStopCount < 1
                    || element.m_VisibleStopCount > element.m_StopCount
                    || element.m_SuppressPlanFrom < 0
                    || element.m_State < 0
                    || element.m_State > (int)MonitorTripState.Cleared
                    || element.m_EndReason < (int)MonitorEndReason.None
                    || element.m_EndReason > (int)MonitorEndReason.Relaunched
                    || (active && element.m_State != (int)MonitorTripState.Active)
                    || (!active && element.m_State == (int)MonitorTripState.Active)
                    || !stopsByTrip.TryGetValue(element.m_TripOrder, out List<MonitorStopElement> savedStops)
                    || savedStops.Count != element.m_StopCount
                    || !activeValid
                    || (active && !activeVehicles.Add(element.m_Vehicle)))
                {
                    RecordLoadIssue("monitor-trip-corrupt", true);
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
                    NextArrivalOrder = element.m_NextArrivalOrder,
                    VisibleStopCount = element.m_VisibleStopCount,
                    SuppressPlanFrom = element.m_SuppressPlanFrom,
                    State = (MonitorTripState)element.m_State,
                    EndReason = (MonitorEndReason)element.m_EndReason,
                    LaunchFrame = element.m_LaunchFrame,
                    UpdatedFrame = element.m_UpdatedFrame
                };
                bool valid = true;
                for (int stopIndex = 0; stopIndex < savedStops.Count; stopIndex++)
                {
                    MonitorStopElement stop = savedStops[stopIndex];
                    if ((stop.m_Version != MonitorVersion && stop.m_Version != MonitorStopVersion)
                        || stop.m_StopOrder != stopIndex
                        || string.IsNullOrEmpty(stop.m_StopKey.ToString())
                        || stop.m_WaypointIndex < -1
                        || stop.m_PlannedArrival < -1
                        || stop.m_PlannedDeparture < -1
                        || stop.m_ActualArrival < -1
                        || stop.m_ActualDeparture < -1
                        || (stop.m_ActualArrival < 0 && stop.m_ActualArrivalFrame != 0u)
                        || (stop.m_ActualDeparture < 0 && (stop.m_ActualDepartureFrame != 0u
                            || stop.m_OpenIntervalMaxFrames != 0u))
                        || (stop.m_ActualDeparture >= 0 && stop.m_OpenIntervalMaxFrames == 0u)
                        || (stop.m_Skipped != 0 && stop.m_Skipped != 1)
                        || (stop.m_Skipped == 1 && (stop.m_ActualArrival < 0
                            || stop.m_ActualDeparture >= 0
                            || stop.m_ActualDepartureFrame != 0u
                            || stop.m_OpenIntervalMaxFrames != 0u))
                        || (stop.m_Cleared != 0 && stop.m_Cleared != 1))
                    {
                        valid = false;
                        break;
                    }
                    trip.Stops.Add(new MonitorStop
                    {
                        StopKey = stop.m_StopKey.ToString(),
                        Station = stop.m_Station,
                        WaypointIndex = stop.m_WaypointIndex,
                        PlannedArrival = stop.m_PlannedArrival,
                        PlannedDeparture = stop.m_PlannedDeparture,
                        ActualArrival = stop.m_ActualArrival,
                        ActualDeparture = stop.m_ActualDeparture,
                        ActualArrivalFrame = stop.m_ActualArrivalFrame,
                        ActualDepartureFrame = stop.m_ActualDepartureFrame,
                        OpenIntervalMaxFrames = stop.m_OpenIntervalMaxFrames,
                        Skipped = stop.m_Skipped == 1,
                        Cleared = stop.m_Cleared == 1
                    });
                }
                if (!valid)
                {
                    RecordLoadIssue("monitor-stop-corrupt", true);
                    continue;
                }
                if (valid && active)
                {
                    if (!layouts.TryGetValue(trip.Line, out MonitorLayout layout))
                    {
                        if (m_Runtime.m_LineView.TryStopLayout(
                                trip.Line,
                                out string currentStopSig,
                                out int[] currentWaypoints))
                        {
                            layout = new MonitorLayout(
                                true,
                                currentStopSig,
                                currentWaypoints);
                        }
                        else
                        {
                            layout = new MonitorLayout(
                                false,
                                string.Empty,
                                Array.Empty<int>());
                        }
                        layouts[trip.Line] = layout;
                    }

                    if (layout.Available
                        && !string.Equals(trip.StopSig, layout.StopSig, StringComparison.Ordinal))
                    {
                        trip.SuppressPlanFrom = Math.Min(
                            trip.SuppressPlanFrom,
                            trip.NextArrivalOrder);
                    }
                    else if (layout.Available
                        && layout.WaypointIndices.Length != trip.Stops.Count)
                    {
                        RecordLoadIssue("monitor-layout-projection-mismatch", true);
                        continue;
                    }
                    else if (layout.Available)
                    {
                        for (int stopIndex = 0; stopIndex < trip.Stops.Count; stopIndex++)
                            trip.Stops[stopIndex].WaypointIndex = layout.WaypointIndices[stopIndex];
                    }
                }
                if (valid && trip.Stops.Count > 0)
                {
                    if (m_Runtime.m_ObsRecorder.RestoreMonitor(trip, active))
                    {
                        // ActualStartMinute 由 Stops[0].ActualDeparture 投影。
                    }
                }
            }
            m_Runtime.m_ObsRecorder.TickDate(m_Runtime.m_SimClock.NowDate);
        }

        public void LoadMonitorIntegrity()
        {
            if (m_IgnoreLegacyMonitor)
                return;
            bool loadDataComplete = !m_Runtime.m_Obs.MonitorOverflowed;
            int loadDroppedTripCount = m_Runtime.m_Obs.MonitorOverflowCount;
            string loadIssueCode = m_Runtime.m_Obs.MonitorIssueCode ?? string.Empty;
            int loadIssueCount = m_Runtime.m_Obs.MonitorIssueCount;
            bool loadPersistenceHealthy = m_MonitorPersistenceHealthy;

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<MonitorIntegrityElement>(city))
            {
                return;
            }

            DynamicBuffer<MonitorIntegrityElement> buffer =
                m_Runtime.EntityManager.GetBuffer<MonitorIntegrityElement>(city, true);
            if (buffer.Length == 0)
                return;
            if (buffer.Length != 1)
            {
                RecordLoadIssue("monitor-integrity-duplicate", false);
                return;
            }

            MonitorIntegrityElement element = buffer[0];
            string issueCode = element.m_LastIssueCode.ToString();
            if (element.m_Version != MonitorVersion
                || (element.m_DataComplete != 0 && element.m_DataComplete != 1)
                || element.m_DroppedTripCount < 0
                || element.m_DroppedTripCount > MaxMonitorTrips
                || element.m_PersistenceHealthy < 0
                || element.m_PersistenceHealthy > 1
                || element.m_IssueCount < 0
                || element.m_IssueCount > MaxMonitorTrips
                || issueCode.Length > 64)
            {
                RecordLoadIssue("monitor-integrity-invalid", false);
                return;
            }

            string mergedIssueCode = !string.IsNullOrEmpty(loadIssueCode)
                ? loadIssueCode
                : issueCode;
            m_Runtime.m_Obs.MonitorOverflowed = !loadDataComplete
                || element.m_DataComplete == 0;
            m_Runtime.m_Obs.MonitorOverflowReason = mergedIssueCode;
            m_Runtime.m_Obs.MonitorOverflowCount = MergeMonitorCount(
                loadDroppedTripCount,
                element.m_DroppedTripCount);
            m_Runtime.m_Obs.MonitorIssueCode = mergedIssueCode;
            m_Runtime.m_Obs.MonitorIssueCount = MergeMonitorCount(
                loadIssueCount,
                element.m_IssueCount);
            m_MonitorPersistenceHealthy = loadPersistenceHealthy
                && element.m_PersistenceHealthy != 0;
        }

        public bool SaveSnapshot()
        {
            MonitorSnapshot snapshot;
            try
            {
                snapshot = BuildMonitorSnapshot();
            }
            catch (Exception ex)
            {
                return FailSnapshot("monitor-snapshot-build-failed", ex);
            }

            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null)
                return FailSnapshot("monitor-snapshot-city-missing", null);

            try
            {
                EnsureMonitor();
                EnsureMonitorAverages();
                if (!HasMonitorBuffers(city)
                    || !m_Runtime.EntityManager.HasBuffer<MonitorAverageLineElement>(city)
                    || !m_Runtime.EntityManager.HasBuffer<MonitorAverageSegmentElement>(city))
                {
                    return FailSnapshot("monitor-snapshot-buffer-missing", null);
                }

                DynamicBuffer<MonitorDateSlotElement> slots =
                    m_Runtime.EntityManager.GetBuffer<MonitorDateSlotElement>(city);
                DynamicBuffer<MonitorIntegrityElement> integrity =
                    m_Runtime.EntityManager.GetBuffer<MonitorIntegrityElement>(city);
                DynamicBuffer<MonitorTripElement> trips =
                    m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city);
                DynamicBuffer<MonitorStopElement> stops =
                    m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city);
                DynamicBuffer<MonitorAverageLineElement> averageLines =
                    m_Runtime.EntityManager.GetBuffer<MonitorAverageLineElement>(city);
                DynamicBuffer<MonitorAverageSegmentElement> averageSegments =
                    m_Runtime.EntityManager.GetBuffer<MonitorAverageSegmentElement>(city);

                slots.EnsureCapacity(snapshot.DateSlots.Count);
                integrity.EnsureCapacity(1);
                trips.EnsureCapacity(snapshot.Trips.Count);
                stops.EnsureCapacity(snapshot.Stops.Count);
                averageLines.EnsureCapacity(snapshot.AverageLines.Count);
                averageSegments.EnsureCapacity(snapshot.AverageSegments.Count);

                snapshot.Integrity.m_PersistenceHealthy = 1;
                slots.Clear();
                integrity.Clear();
                trips.Clear();
                stops.Clear();
                averageLines.Clear();
                averageSegments.Clear();
                for (int i = 0; i < snapshot.DateSlots.Count; i++)
                    slots.Add(snapshot.DateSlots[i]);
                integrity.Add(snapshot.Integrity);
                for (int i = 0; i < snapshot.Trips.Count; i++)
                    trips.Add(snapshot.Trips[i]);
                for (int i = 0; i < snapshot.Stops.Count; i++)
                    stops.Add(snapshot.Stops[i]);
                for (int i = 0; i < snapshot.AverageLines.Count; i++)
                    averageLines.Add(snapshot.AverageLines[i]);
                for (int i = 0; i < snapshot.AverageSegments.Count; i++)
                    averageSegments.Add(snapshot.AverageSegments[i]);

                m_MonitorPersistenceHealthy = true;
                return true;
            }
            catch (Exception ex)
            {
                return FailSnapshot("monitor-snapshot-capacity-failed", ex);
            }
        }

        private bool HasLegacyMonitor(Entity city)
        {
            DynamicBuffer<MonitorDateSlotElement> slots =
                m_Runtime.EntityManager.GetBuffer<MonitorDateSlotElement>(city, true);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].m_Version > 0 && slots[i].m_Version < MonitorVersion)
                    return true;

            DynamicBuffer<MonitorIntegrityElement> integrity =
                m_Runtime.EntityManager.GetBuffer<MonitorIntegrityElement>(city, true);
            for (int i = 0; i < integrity.Length; i++)
                if (integrity[i].m_Version > 0 && integrity[i].m_Version < MonitorVersion)
                    return true;

            DynamicBuffer<MonitorTripElement> trips =
                m_Runtime.EntityManager.GetBuffer<MonitorTripElement>(city, true);
            for (int i = 0; i < trips.Length; i++)
                if (trips[i].m_Version > 0 && trips[i].m_Version < MonitorTripVersion)
                    return true;

            DynamicBuffer<MonitorStopElement> stops =
                m_Runtime.EntityManager.GetBuffer<MonitorStopElement>(city, true);
            for (int i = 0; i < stops.Length; i++)
                if (stops[i].m_Version > 0 && stops[i].m_Version < MonitorVersion)
                    return true;

            DynamicBuffer<MonitorAverageLineElement> averageLines =
                m_Runtime.EntityManager.GetBuffer<MonitorAverageLineElement>(city, true);
            for (int i = 0; i < averageLines.Length; i++)
                if (averageLines[i].m_Version > 0
                    && averageLines[i].m_Version < MonitorAverageVersion)
                    return true;

            DynamicBuffer<MonitorAverageSegmentElement> averageSegments =
                m_Runtime.EntityManager.GetBuffer<MonitorAverageSegmentElement>(city, true);
            for (int i = 0; i < averageSegments.Length; i++)
                if (averageSegments[i].m_Version > 0
                    && averageSegments[i].m_Version < MonitorAverageVersion)
                    return true;
            return false;
        }

        private readonly struct MonitorLayout
        {
            internal readonly bool Available;
            internal readonly string StopSig;
            internal readonly int[] WaypointIndices;

            internal MonitorLayout(
                bool available,
                string stopSig,
                int[] waypointIndices)
            {
                Available = available;
                StopSig = stopSig ?? string.Empty;
                WaypointIndices = waypointIndices ?? Array.Empty<int>();
            }
        }

        private sealed class MonitorSnapshot
        {
            internal readonly List<MonitorDateSlotElement> DateSlots =
                new List<MonitorDateSlotElement>();
            internal readonly List<MonitorTripElement> Trips =
                new List<MonitorTripElement>();
            internal readonly List<MonitorStopElement> Stops =
                new List<MonitorStopElement>();
            internal readonly List<MonitorAverageLineElement> AverageLines =
                new List<MonitorAverageLineElement>();
            internal readonly List<MonitorAverageSegmentElement> AverageSegments =
                new List<MonitorAverageSegmentElement>();
            internal MonitorIntegrityElement Integrity;
        }

        private MonitorSnapshot BuildMonitorSnapshot()
        {
            if (m_Runtime.m_ObsRecorder == null)
                throw new InvalidOperationException("monitor-recorder-missing");

            MonitorSnapshot snapshot = new MonitorSnapshot();
            List<MonitorDateSlot> dateSlots = new List<MonitorDateSlot>();
            foreach (MonitorDateSlot slot in m_Runtime.m_ObsRecorder.MonitorDateSlots)
            {
                if (slot == null)
                    throw new InvalidOperationException("monitor-date-slot-null");
                dateSlots.Add(slot);
            }
            if (dateSlots.Count > MaxMonitorDateSlots)
                throw new InvalidOperationException("monitor-date-slot-capacity");
            dateSlots.Sort((left, right) => left.DateKey.CompareTo(right.DateKey));

            HashSet<int> dateKeys = new HashSet<int>();
            for (int i = 0; i < dateSlots.Count; i++)
            {
                MonitorDateSlot slot = dateSlots[i];
                if (!ValidMonitorDate(slot.DateKey) || !dateKeys.Add(slot.DateKey))
                    throw new InvalidOperationException("monitor-date-slot-invalid");
                snapshot.DateSlots.Add(new MonitorDateSlotElement
                {
                    m_Version = MonitorVersion,
                    m_DateKey = slot.DateKey
                });
            }

            HashSet<string> monitorKeys = new HashSet<string>(StringComparer.Ordinal);
            int tripOrder = 0;
            List<MonitorTrip> activeTrips = new List<MonitorTrip>();
            foreach (MonitorTrip trip in m_Runtime.m_ObsRecorder.ActiveMonitorTrips)
                activeTrips.Add(trip);
            if (activeTrips.Count > MaxMonitorActiveTrips)
                throw new InvalidOperationException("monitor-active-trip-capacity");
            activeTrips.Sort((left, right) => string.CompareOrdinal(left?.Key, right?.Key));
            for (int i = 0; i < activeTrips.Count; i++)
                AppendMonitorSnapshotTrip(snapshot, activeTrips[i], true, ref tripOrder, monitorKeys);

            for (int slotIndex = 0; slotIndex < dateSlots.Count; slotIndex++)
            {
                if (dateSlots[slotIndex].Trips.Count > MaxMonitorTripsPerDate)
                    throw new InvalidOperationException("monitor-date-trip-capacity");
                List<MonitorTrip> archivedTrips = new List<MonitorTrip>();
                foreach (MonitorTrip trip in dateSlots[slotIndex].Trips.Values)
                    archivedTrips.Add(trip);
                archivedTrips.Sort((left, right) => string.CompareOrdinal(left?.Key, right?.Key));
                for (int i = 0; i < archivedTrips.Count; i++)
                {
                    if (archivedTrips[i] == null
                        || archivedTrips[i].ServiceDateKey != dateSlots[slotIndex].DateKey)
                    {
                        throw new InvalidOperationException("monitor-trip-date-mismatch");
                    }
                    AppendMonitorSnapshotTrip(snapshot, archivedTrips[i], false, ref tripOrder, monitorKeys);
                }
            }

            if (tripOrder > MaxMonitorTrips || snapshot.Stops.Count > MaxMonitorStops)
                throw new InvalidOperationException("monitor-trip-stop-capacity");

            List<MonitorAverageLine> averageLines = new List<MonitorAverageLine>();
            foreach (MonitorAverageLine line in m_Runtime.m_MonitorAverages.Lines)
                averageLines.Add(line);
            if (averageLines.Count > MonitorAverageStore.MaxLines)
                throw new InvalidOperationException("monitor-average-line-capacity");
            averageLines.Sort((left, right) => left.Line.Index.CompareTo(right.Line.Index));
            for (int lineIndex = 0; lineIndex < averageLines.Count; lineIndex++)
            {
                MonitorAverageLine line = averageLines[lineIndex];
                if (line == null
                    || line.Line == Entity.Null
                    || string.IsNullOrEmpty(line.StopSig)
                    || line.Segments.Length == 0
                    || line.Segments.Length > MonitorAverageStore.MaxSegmentsPerLine
                    || snapshot.AverageSegments.Count + line.Segments.Length > MaxMonitorAverageSegments)
                {
                    throw new InvalidOperationException("monitor-average-invalid");
                }
                snapshot.AverageLines.Add(new MonitorAverageLineElement
                {
                    m_Version = MonitorAverageVersion,
                    m_Line = line.Line,
                    m_StopSig = line.StopSig,
                    m_Revision = line.Revision,
                    m_SegmentCount = line.Segments.Length
                });
                for (int order = 0; order < line.Segments.Length; order++)
                {
                    MonitorAverageSegment segment = line.Segments[order];
                    if ((segment.SampleCount == 0 && segment.TotalFrames != 0)
                        || (segment.SampleCount > 0 && segment.TotalFrames == 0))
                    {
                        throw new InvalidOperationException("monitor-average-segment-invalid");
                    }
                    snapshot.AverageSegments.Add(new MonitorAverageSegmentElement
                    {
                        m_Version = MonitorAverageVersion,
                        m_Line = line.Line,
                        m_Order = order,
                        m_TotalFrames = segment.TotalFrames,
                        m_SampleCount = segment.SampleCount
                    });
                }
            }

            string issueCode = m_Runtime.m_Obs.MonitorIssueCode ?? string.Empty;
            if (issueCode.Length > 64
                || m_Runtime.m_Obs.MonitorIssueCount < 0
                || m_Runtime.m_Obs.MonitorIssueCount > MaxMonitorTrips
                || m_Runtime.m_Obs.MonitorOverflowCount < 0
                || m_Runtime.m_Obs.MonitorOverflowCount > MaxMonitorTrips)
            {
                throw new InvalidOperationException("monitor-integrity-invalid");
            }

            snapshot.Integrity = new MonitorIntegrityElement
            {
                m_Version = MonitorVersion,
                m_DataComplete = m_Runtime.m_Obs.MonitorOverflowed ? 0 : 1,
                m_DroppedTripCount = m_Runtime.m_Obs.MonitorOverflowed
                    ? m_Runtime.m_Obs.MonitorOverflowCount
                    : 0,
                m_PersistenceHealthy = m_MonitorPersistenceHealthy ? 1 : 0,
                m_LastIssueCode = issueCode,
                m_IssueCount = m_Runtime.m_Obs.MonitorIssueCount
            };
            return snapshot;
        }

        private void AppendMonitorSnapshotTrip(
            MonitorSnapshot snapshot,
            MonitorTrip trip,
            bool active,
            ref int tripOrder,
            HashSet<string> monitorKeys)
        {
            if (!ValidMonitorTripSnapshot(trip, active)
                || !monitorKeys.Add(trip.Key)
                || tripOrder >= MaxMonitorTrips
                || !TryBuildMonitorValues(
                    trip,
                    active,
                    tripOrder,
                    out MonitorTripElement tripValue,
                    out MonitorStopElement[] stopValues))
            {
                throw new InvalidOperationException("monitor-trip-invalid");
            }

            snapshot.Trips.Add(tripValue);
            for (int i = 0; i < stopValues.Length; i++)
                snapshot.Stops.Add(stopValues[i]);
            tripOrder++;
        }

        private bool ValidMonitorTripSnapshot(MonitorTrip trip, bool active)
        {
            if (trip == null
                || string.IsNullOrEmpty(trip.Key)
                || string.IsNullOrEmpty(trip.LineKey)
                || string.IsNullOrEmpty(trip.StopSig)
                || string.IsNullOrEmpty(trip.RowId)
                || trip.Line == Entity.Null
                || !ValidMonitorDate(trip.ServiceDateKey)
                || trip.SlotMinute < 0
                || trip.SlotMinute >= 1440
                || trip.Stops == null
                || trip.Stops.Count == 0
                || trip.Stops.Count > 256
                || trip.NextArrivalOrder < 1
                || trip.NextArrivalOrder > trip.Stops.Count
                || trip.VisibleStopCount < 1
                || trip.VisibleStopCount > trip.Stops.Count
                || trip.SuppressPlanFrom < 0
                || (int)trip.EndReason < (int)MonitorEndReason.None
                || (int)trip.EndReason > (int)MonitorEndReason.Relaunched
                || (active && (trip.State != MonitorTripState.Active || trip.Vehicle == Entity.Null))
                || (!active && trip.State == MonitorTripState.Active))
            {
                return false;
            }

            string expectedKey = trip.LineKey + "|" + trip.RowId + "|" + trip.ServiceDateKey;
            if (!string.Equals(trip.Key, expectedKey, StringComparison.Ordinal)
                || (int)trip.State < (int)MonitorTripState.Active
                || (int)trip.State > (int)MonitorTripState.Cleared)
            {
                return false;
            }

            for (int i = 0; i < trip.Stops.Count; i++)
            {
                MonitorStop stop = trip.Stops[i];
                if (stop == null
                    || string.IsNullOrEmpty(stop.StopKey)
                    || stop.WaypointIndex < -1
                    || stop.PlannedArrival < -1
                    || stop.PlannedDeparture < -1
                    || stop.ActualArrival < -1
                    || stop.ActualDeparture < -1
                    || (stop.ActualArrival < 0 && stop.ActualArrivalFrame != 0u)
                    || (stop.ActualDeparture < 0 && (stop.ActualDepartureFrame != 0u
                        || stop.OpenIntervalMaxFrames != 0u))
                    || (stop.ActualDeparture >= 0 && stop.OpenIntervalMaxFrames == 0u)
                    || (stop.Skipped && (stop.ActualArrival < 0
                        || stop.ActualDeparture >= 0
                        || stop.ActualDepartureFrame != 0u
                        || stop.OpenIntervalMaxFrames != 0u)))
                {
                    return false;
                }
            }
            return true;
        }

        private bool HasMonitorBuffers(Entity city)
        {
            return m_Runtime.EntityManager.HasBuffer<MonitorDateSlotElement>(city)
                && m_Runtime.EntityManager.HasBuffer<MonitorIntegrityElement>(city)
                && m_Runtime.EntityManager.HasBuffer<MonitorTripElement>(city)
                && m_Runtime.EntityManager.HasBuffer<MonitorStopElement>(city);
        }

        private bool FailSnapshot(string issueCode, Exception exception)
        {
            m_MonitorPersistenceHealthy = false;
            m_Runtime.m_Obs.MonitorIssueCode = issueCode ?? "monitor-snapshot-failed";
            if (m_Runtime.m_Obs.MonitorIssueCount < int.MaxValue)
                m_Runtime.m_Obs.MonitorIssueCount++;
            m_Runtime.log.Info("[ObservationPersistence] " + m_Runtime.m_Obs.MonitorIssueCode
                + (exception == null
                    ? string.Empty
                    : " -> " + exception.GetType().Name + ": " + exception.Message));
            return false;
        }

        private void RecordLoadIssue(
            string issueCode,
            bool droppedTrip,
            bool dataIncomplete = true)
        {
            m_MonitorPersistenceHealthy = false;
            string code = issueCode ?? "monitor-integrity-invalid";
            if (dataIncomplete)
                m_Runtime.m_Obs.MonitorOverflowed = true;
            if (droppedTrip)
            {
                m_Runtime.m_Obs.MonitorOverflowed = true;
                m_Runtime.m_Obs.MonitorOverflowCount = MergeMonitorCount(
                    m_Runtime.m_Obs.MonitorOverflowCount,
                    1);
            }
            m_Runtime.m_Obs.MonitorOverflowReason = code;
            m_Runtime.m_Obs.MonitorIssueCode = code;
            m_Runtime.m_Obs.MonitorIssueCount = MergeMonitorCount(
                m_Runtime.m_Obs.MonitorIssueCount,
                1);
            m_Runtime.log.Info("[ObservationPersistence] " + m_Runtime.m_Obs.MonitorIssueCode);
        }

        private static int MergeMonitorCount(int left, int right)
        {
            if (left < 0 || right < 0)
                return MaxMonitorTrips;
            return left > MaxMonitorTrips - right
                ? MaxMonitorTrips
                : left + right;
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
                        m_Version = MonitorStopVersion,
                        m_TripOrder = tripOrder,
                        m_StopOrder = i,
                        m_StopKey = stop.StopKey,
                        m_Station = stop.Station,
                        m_WaypointIndex = stop.WaypointIndex,
                        m_PlannedArrival = stop.PlannedArrival,
                        m_PlannedDeparture = stop.PlannedDeparture,
                        m_ActualArrival = stop.ActualArrival,
                        m_ActualDeparture = stop.ActualDeparture,
                        m_ActualArrivalFrame = stop.ActualArrivalFrame,
                        m_ActualDepartureFrame = stop.ActualDepartureFrame,
                        m_OpenIntervalMaxFrames = stop.OpenIntervalMaxFrames,
                        m_Skipped = stop.Skipped ? 1 : 0,
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
                m_Version = MonitorTripVersion,
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
                m_NextArrivalOrder = trip.NextArrivalOrder,
                m_VisibleStopCount = trip.VisibleStopCount,
                m_SuppressPlanFrom = trip.SuppressPlanFrom,
                m_State = (int)trip.State,
                m_EndReason = (int)trip.EndReason,
                m_LaunchFrame = trip.LaunchFrame,
                m_UpdatedFrame = trip.UpdatedFrame,
                m_StopCount = trip.Stops.Count
            };
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

        public void LoadMonitorAverages()
        {
            EnsureMonitorAverages();
            m_Runtime.m_MonitorAverages.Clear();
            if (m_IgnoreLegacyMonitor)
                return;
            Entity city = m_Runtime.m_CitySystem.City;
            if (city == Entity.Null
                || !m_Runtime.EntityManager.HasBuffer<MonitorAverageLineElement>(city)
                || !m_Runtime.EntityManager.HasBuffer<MonitorAverageSegmentElement>(city))
            {
                return;
            }

            DynamicBuffer<MonitorAverageLineElement> lineBuffer =
                m_Runtime.EntityManager.GetBuffer<MonitorAverageLineElement>(city, true);
            DynamicBuffer<MonitorAverageSegmentElement> segmentBuffer =
                m_Runtime.EntityManager.GetBuffer<MonitorAverageSegmentElement>(city, true);
            if (lineBuffer.Length > MonitorAverageStore.MaxLines
                || segmentBuffer.Length > MaxMonitorAverageSegments)
            {
                RecordLoadIssue("monitor-average-capacity", false, false);
                return;
            }

            Dictionary<Entity, List<MonitorAverageSegmentElement>> segments =
                new Dictionary<Entity, List<MonitorAverageSegmentElement>>();
            HashSet<Entity> invalidLines = new HashSet<Entity>();
            for (int i = 0; i < segmentBuffer.Length; i++)
            {
                MonitorAverageSegmentElement element = segmentBuffer[i];
                bool invalid = element.m_Version != MonitorAverageVersion
                    || element.m_Line == Entity.Null
                    || element.m_Order < 0
                    || element.m_SampleCount < 0
                    || (element.m_SampleCount == 0 && element.m_TotalFrames != 0)
                    || (element.m_SampleCount > 0 && element.m_TotalFrames == 0);
                if (invalid)
                {
                    invalidLines.Add(element.m_Line);
                    continue;
                }
                if (!segments.TryGetValue(element.m_Line, out List<MonitorAverageSegmentElement> values))
                {
                    values = new List<MonitorAverageSegmentElement>();
                    segments[element.m_Line] = values;
                }
                values.Add(element);
            }

            HashSet<Entity> seenLines = new HashSet<Entity>();
            for (int i = 0; i < lineBuffer.Length; i++)
            {
                MonitorAverageLineElement element = lineBuffer[i];
                Entity line = element.m_Line;
                bool layoutValid = line != Entity.Null
                    && m_Runtime.EntityManager.Exists(line)
                    && m_Runtime.m_LineView.TryStopLayout(line, out string currentStopSig, out int[] currentStops)
                    && string.Equals(currentStopSig, element.m_StopSig.ToString(), StringComparison.Ordinal)
                    && currentStops.Length == element.m_SegmentCount;
                if (element.m_Version != MonitorAverageVersion
                    || !seenLines.Add(line)
                    || string.IsNullOrEmpty(element.m_StopSig.ToString())
                    || element.m_SegmentCount < 2
                    || element.m_SegmentCount > MonitorAverageStore.MaxSegmentsPerLine
                    || !layoutValid
                    || invalidLines.Contains(line)
                    || !segments.TryGetValue(line, out List<MonitorAverageSegmentElement> values)
                    || values.Count != element.m_SegmentCount)
                {
                    RecordLoadIssue("monitor-average-corrupt", false, false);
                    continue;
                }

                values.Sort((left, right) => left.m_Order.CompareTo(right.m_Order));
                MonitorAverageSegment[] restored = new MonitorAverageSegment[element.m_SegmentCount];
                bool valid = true;
                for (int order = 0; order < values.Count; order++)
                {
                    MonitorAverageSegmentElement segment = values[order];
                    if (segment.m_Order != order)
                    {
                        valid = false;
                        break;
                    }
                    restored[order] = new MonitorAverageSegment
                    {
                        TotalFrames = segment.m_TotalFrames,
                        SampleCount = segment.m_SampleCount
                    };
                }
                if (!valid || !m_Runtime.m_MonitorAverages.Restore(new MonitorAverageLine(
                        line,
                        element.m_StopSig.ToString(),
                        element.m_Revision,
                        restored,
                        false)))
                {
                    RecordLoadIssue("monitor-average-corrupt", false, false);
                }
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
                if (!string.Equals(buffer[i].m_ObservationKey.ToString(), key, System.StringComparison.Ordinal))
                    continue;

                buffer[i] = new StationDwellObservationElement
                {
                    m_ObservationKey = key,
                    m_AverageFrames = observation.AverageFrames,
                    m_SampleCount = observation.SampleCount,
                    m_LastObservedFrame = observation.LastObservedFrame
                };
                return;
            }

            buffer.Add(new StationDwellObservationElement
            {
                m_ObservationKey = key,
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
            DynamicBuffer<StationDwellObservationElement> buffer = m_Runtime.EntityManager.GetBuffer<StationDwellObservationElement>(city);
            int anchorBufferCount = buffer.Length;
            int removedInvalidCount = 0;
            int removedDuplicateCount = 0;
            int removedLegacyConflictCount = 0;
            int removedLegacyMissingCount = 0;
            Dictionary<string, StationDwellObservationElement> winners =
                new Dictionary<string, StationDwellObservationElement>(StringComparer.Ordinal);
            HashSet<string> migratedWinners = new HashSet<string>(StringComparer.Ordinal);
            List<string> winnerKeys = new List<string>();
            for (int i = 0; i < buffer.Length; i++)
            {
                StationDwellObservationElement entry = buffer[i];
                string observationKey = entry.m_ObservationKey.ToString();
                if (string.IsNullOrWhiteSpace(observationKey)
                    || !Capture.IsStationDwellKey(observationKey)
                    || !math.isfinite(entry.m_AverageFrames)
                    || !(entry.m_AverageFrames > 0f)
                    || entry.m_SampleCount <= 0)
                {
                    removedInvalidCount++;
                    continue;
                }

                int separatorIndex = observationKey.IndexOf('|');
                string lineId = observationKey.Substring(0, separatorIndex);
                if (!LineKey.TryParse(lineId, out LineKey lineKey))
                {
                    removedInvalidCount++;
                    continue;
                }

                bool migrated = false;
                if (!LineKey.IsStableGuidKey(lineKey))
                {
                    if (!LineKey.IsLegacyNumericKey(lineKey))
                    {
                        removedInvalidCount++;
                        continue;
                    }

                    LineAnchorCatalog catalog = m_Runtime.m_LineAnchorCatalog;
                    if (catalog == null || catalog.IsLegacyConflict(lineKey))
                    {
                        removedLegacyConflictCount++;
                        continue;
                    }
                    if (!catalog.TryLegacy(lineKey, out LineKey stableKey))
                    {
                        removedLegacyMissingCount++;
                        continue;
                    }

                    observationKey = stableKey.ToString() + observationKey.Substring(separatorIndex);
                    entry.m_ObservationKey = observationKey;
                    migrated = true;
                }

                if (winners.TryGetValue(observationKey, out StationDwellObservationElement existing))
                {
                    removedDuplicateCount++;
                    bool existingMigrated = migratedWinners.Contains(observationKey);
                    bool replace = (existingMigrated && !migrated)
                        || (existingMigrated == migrated
                            && (entry.m_LastObservedFrame > existing.m_LastObservedFrame
                                || (entry.m_LastObservedFrame == existing.m_LastObservedFrame
                                    && entry.m_SampleCount > existing.m_SampleCount)));
                    if (replace)
                    {
                        winners[observationKey] = entry;
                        if (migrated)
                            migratedWinners.Add(observationKey);
                        else
                            migratedWinners.Remove(observationKey);
                    }
                    continue;
                }

                winners.Add(observationKey, entry);
                if (migrated)
                    migratedWinners.Add(observationKey);
                winnerKeys.Add(observationKey);
            }

            buffer.Clear();
            buffer.EnsureCapacity(winnerKeys.Count);
            for (int i = 0; i < winnerKeys.Count; i++)
            {
                string observationKey = winnerKeys[i];
                StationDwellObservationElement entry = winners[observationKey];
                buffer.Add(entry);
                m_Runtime.m_ObsPersist.PutStationDwell(
                    observationKey,
                    new StationDwellObservation
                    {
                        AverageFrames = entry.m_AverageFrames,
                        SampleCount = math.max(0, entry.m_SampleCount),
                        LastObservedFrame = entry.m_LastObservedFrame
                    });
            }

            int restoredCount = winnerKeys.Count;
            m_Runtime.m_StationDwellObservationCacheLoaded = true;
            m_Runtime.m_LastStationStopDwellAnchorBufferCount = anchorBufferCount;
            m_Runtime.m_LastStationStopDwellAnchorRestoredCount = restoredCount;
            if (RtLog.VerboseEnabled)
            {
                m_Runtime.log.Info("[StopDwellAnchorRestore] anchorBuffer=" + anchorBufferCount
                    + " anchorRestored=" + restoredCount
                    + " legacyMigrated=" + migratedWinners.Count
                    + " removedInvalid=" + removedInvalidCount
                    + " removedDuplicate=" + removedDuplicateCount
                    + " removedLegacyConflict=" + removedLegacyConflictCount
                    + " removedLegacyMissing=" + removedLegacyMissingCount
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
            Dictionary<Entity, int> restoredLineCounts = new Dictionary<Entity, int>();
            Dictionary<Entity, int> restoredLineMinSlices = new Dictionary<Entity, int>();
            Dictionary<Entity, int> restoredLineMaxSlices = new Dictionary<Entity, int>();
            Dictionary<Entity, List<TraversalSliceObservationElement>> restoredLineSlices =
                new Dictionary<Entity, List<TraversalSliceObservationElement>>();
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
                restoredLineCounts[entry.m_LineEntity] = restoredLineCounts.TryGetValue(entry.m_LineEntity, out int lineCount)
                    ? lineCount + 1
                    : 1;
                restoredLineMinSlices[entry.m_LineEntity] = restoredLineMinSlices.TryGetValue(entry.m_LineEntity, out int minSlice)
                    ? math.min(minSlice, entry.m_SliceIndex)
                    : entry.m_SliceIndex;
                restoredLineMaxSlices[entry.m_LineEntity] = restoredLineMaxSlices.TryGetValue(entry.m_LineEntity, out int maxSlice)
                    ? math.max(maxSlice, entry.m_SliceIndex)
                    : entry.m_SliceIndex;
                if (!restoredLineSlices.TryGetValue(entry.m_LineEntity, out List<TraversalSliceObservationElement> slices))
                {
                    slices = new List<TraversalSliceObservationElement>();
                    restoredLineSlices[entry.m_LineEntity] = slices;
                }
                slices.Add(entry);
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
                List<Entity> restoredLines = new List<Entity>(restoredLineCounts.Keys);
                restoredLines.Sort((left, right) =>
                {
                    int result = left.Index.CompareTo(right.Index);
                    return result != 0 ? result : left.Version.CompareTo(right.Version);
                });
                List<string> restoredSummaries = new List<string>(restoredLines.Count);
                for (int i = 0; i < restoredLines.Count; i++)
                {
                    Entity restoredLine = restoredLines[i];
                    restoredSummaries.Add(restoredLine.Index + ":" + restoredLine.Version
                        + "=" + restoredLineCounts[restoredLine]
                        + "[" + restoredLineMinSlices[restoredLine]
                        + ".." + restoredLineMaxSlices[restoredLine] + "]");
                }
                m_Runtime.log.Info("[TraversalSliceRestoreLines] " + string.Join(" | ", restoredSummaries));
                for (int i = 0; i < restoredLines.Count; i++)
                {
                    Entity restoredLine = restoredLines[i];
                    LineKey lineKey = m_Runtime.m_LineAnchorCatalog != null
                        ? m_Runtime.m_LineAnchorCatalog.StableKey(restoredLine)
                        : LineKey.Empty;
                    string stableKey = LineKey.IsStableGuidKey(lineKey) ? lineKey.ToString() : "unavailable";
                    m_Runtime.log.Info("[TraversalSliceRestoreLine] lineKey=" + stableKey
                        + ";line=" + restoredLine.Index + ":" + restoredLine.Version
                        + ";" + RestoreLineSlices(restoredLineSlices[restoredLine]));
                }
            }
        }

        private static string RestoreLineSlices(List<TraversalSliceObservationElement> slices)
        {
            slices.Sort((left, right) => left.m_SliceIndex.CompareTo(right.m_SliceIndex));
            List<string> observed = new List<string>(math.min(slices.Count, MaxRestoreLineLogSlices));
            List<string> holes = new List<string>(MaxRestoreLineLogSlices);
            int holeCount = 0;
            int holeRangeCount = 0;
            int expected = slices[0].m_SliceIndex;
            for (int i = 0; i < slices.Count; i++)
            {
                TraversalSliceObservationElement entry = slices[i];
                if (entry.m_SliceIndex > expected)
                {
                    holeCount += entry.m_SliceIndex - expected;
                    holeRangeCount++;
                    if (holes.Count < MaxRestoreLineLogSlices)
                    {
                        holes.Add(expected == entry.m_SliceIndex - 1
                            ? expected.ToString()
                            : expected + ".." + (entry.m_SliceIndex - 1));
                    }
                }
                if (observed.Count < MaxRestoreLineLogSlices)
                    observed.Add(entry.m_SliceIndex + ":" + math.max(0, entry.m_SampleCount));
                expected = entry.m_SliceIndex + 1;
            }

            return "slices=" + (observed.Count > 0 ? string.Join(",", observed) : "none")
                + ";sliceTotal=" + slices.Count
                + ";sliceTruncated=" + (slices.Count > observed.Count ? 1 : 0)
                + ";holes=" + (holes.Count > 0 ? string.Join(",", holes) : "none")
                + ";holeTotal=" + holeCount
                + ";holeTruncated=" + (holeRangeCount > holes.Count ? 1 : 0);
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
