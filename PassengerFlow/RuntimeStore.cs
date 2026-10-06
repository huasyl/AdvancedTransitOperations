using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    // Persistence 的实体型运行态分支；只保存尚未提交的观测连续性，不另建保存入口。
    internal static partial class Persistence
    {
        private static void ClearCityBuffers(EntityManager entityManager, Entity city)
        {
            ClearCityBuffer<PassengerFlowStateElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowVehicleElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowBaselineElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowSampleElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowSectionElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowStopElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowTripElement>(entityManager, city);
            ClearCityBuffer<PassengerFlowPlanElement>(entityManager, city);
        }

        private static void ClearCityBuffer<T>(EntityManager entityManager, Entity city)
            where T : unmanaged, IBufferElementData
        {
            if (entityManager.HasBuffer<T>(city))
                entityManager.GetBuffer<T>(city).Clear();
        }

        internal static void SaveRuntime(EntityManager entityManager, Entity city, State state, uint frame)
        {
            if (state.RuntimeRestorePending)
                return;
            EnsureBuffer<PassengerFlowVehicleElement>(entityManager, city);
            EnsureBuffer<PassengerFlowBaselineElement>(entityManager, city);
            EnsureBuffer<PassengerFlowSampleElement>(entityManager, city);
            EnsureBuffer<PassengerFlowSectionElement>(entityManager, city);
            EnsureBuffer<PassengerFlowStopElement>(entityManager, city);
            EnsureBuffer<PassengerFlowTripElement>(entityManager, city);
            EnsureBuffer<PassengerFlowPlanElement>(entityManager, city);
            DynamicBuffer<PassengerFlowVehicleElement> vehicles = entityManager.GetBuffer<PassengerFlowVehicleElement>(city);
            DynamicBuffer<PassengerFlowBaselineElement> baselines = entityManager.GetBuffer<PassengerFlowBaselineElement>(city);
            DynamicBuffer<PassengerFlowSampleElement> samples = entityManager.GetBuffer<PassengerFlowSampleElement>(city);
            DynamicBuffer<PassengerFlowSectionElement> sections = entityManager.GetBuffer<PassengerFlowSectionElement>(city);
            DynamicBuffer<PassengerFlowStopElement> stops = entityManager.GetBuffer<PassengerFlowStopElement>(city);
            DynamicBuffer<PassengerFlowTripElement> trips = entityManager.GetBuffer<PassengerFlowTripElement>(city);
            DynamicBuffer<PassengerFlowPlanElement> plans = entityManager.GetBuffer<PassengerFlowPlanElement>(city);
            vehicles.Clear(); baselines.Clear(); samples.Clear(); stops.Clear(); trips.Clear(); plans.Clear();
            sections.Clear();

            foreach (KeyValuePair<Entity, string> entry in state.VehicleIds)
                vehicles.Add(new PassengerFlowVehicleElement { m_Vehicle = entry.Key, m_VehicleId = entry.Value });

            foreach (KeyValuePair<Entity, PassengerBaseline> entry in state.Baselines)
            {
                if (entry.Value.Passengers.Count == 0)
                {
                    baselines.Add(new PassengerFlowBaselineElement { m_Vehicle = entry.Key, m_IsEmpty = 1 });
                    continue;
                }
                for (int i = 0; i < entry.Value.Passengers.Count; i++)
                {
                    Entity passenger = entry.Value.Passengers[i];
                    PurposeInfo purpose = entry.Value.Purposes.TryGetValue(passenger, out PurposeInfo known)
                        ? known : PurposeInfo.Unknown;
                    baselines.Add(new PassengerFlowBaselineElement
                    {
                        m_Vehicle = entry.Key, m_Passenger = passenger,
                        m_RawPurpose = purpose.RawPurpose, m_PurposeCategory = (byte)purpose.Category
                    });
                }
            }

            foreach (PendingSample sample in state.PendingSamples)
            {
                samples.Add(new PassengerFlowSampleElement
                {
                    m_Line = sample.Line, m_Vehicle = sample.Vehicle, m_RuntimeVehicle = sample.RuntimeVehicle,
                    m_LineId = sample.LineId, m_VehicleId = sample.VehicleId,
                    m_DepartureFrame = sample.DepartureFrame, m_OpenFrame = sample.OpenFrame,
                    m_DepartureDayIndex = sample.DepartureBucket.DayIndex,
                    m_DepartureBucketMinute = sample.DepartureBucket.BucketStartMinute,
                    m_RemainingSampleFrames = sample.SampleFrame > frame ? sample.SampleFrame - frame : 0u,
                    m_Mode = (int)sample.Mode, m_OpenWaypointIndex = sample.OpenWaypointIndex,
                    m_OpenStationSakIndex = sample.OpenStationSakIndex,
                    m_OpenStop = sample.Position.Stop, m_OpenWaypoint = sample.Position.Waypoint,
                    m_StationOccurrence = sample.Position.StationOccurrence
                });
                SaveSections(sections, sample, sample.StopSections, SectionKind.Stops);
                SaveSections(sections, sample, sample.TrackSections, SectionKind.Track);
            }

            foreach (OpenStop stop in state.OpenStops.Values)
            {
                stops.Add(new PassengerFlowStopElement
                {
                    m_Vehicle = stop.Vehicle, m_Line = stop.Line, m_Mode = (int)stop.Mode,
                    m_LineId = stop.LineId, m_WaypointIndex = stop.OpenWaypointIndex,
                    m_StationSakIndex = stop.OpenStationSakIndex, m_OpenFrame = stop.OpenFrame,
                    m_OpenStop = stop.Position.Stop, m_OpenWaypoint = stop.Position.Waypoint,
                    m_StationOccurrence = stop.Position.StationOccurrence
                });
            }
            List<PassengerFlowTripElement> tripRecords = new List<PassengerFlowTripElement>();
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples = ActiveSampleKeys(state);
            state.Trips.CaptureRuntime(tripRecords, frame, activeSamples);
            for (int i = 0; i < tripRecords.Count; i++)
                trips.Add(tripRecords[i]);
            List<PassengerFlowPlanElement> planRecords = new List<PassengerFlowPlanElement>();
            state.Trips.CapturePlans(planRecords, activeSamples);
            for (int i = 0; i < planRecords.Count; i++)
                plans.Add(planRecords[i]);
        }

        internal static void ReadRuntime(EntityManager entityManager, Entity city, State state)
        {
            state.RestoredVehicles.Clear(); state.RestoredBaselines.Clear(); state.RestoredSamples.Clear(); state.RestoredStops.Clear(); state.RestoredTrips.Clear(); state.RestoredPlans.Clear();
            state.RestoredSections.Clear();
            Copy(entityManager, city, state.RestoredVehicles);
            Copy(entityManager, city, state.RestoredBaselines);
            Copy(entityManager, city, state.RestoredSamples);
            Copy(entityManager, city, state.RestoredSections);
            Copy(entityManager, city, state.RestoredStops);
            Copy(entityManager, city, state.RestoredTrips);
            Copy(entityManager, city, state.RestoredPlans);
            state.RuntimeRestorePending = state.RestoredVehicles.Count != 0 || state.RestoredBaselines.Count != 0
                || state.RestoredSamples.Count != 0 || state.RestoredStops.Count != 0
                || state.RestoredTrips.Count != 0 || state.RestoredPlans.Count != 0;
        }

        internal static void ResumeRuntime(EntityManager entityManager, State state, uint frame)
        {
            foreach (PassengerFlowVehicleElement entry in state.RestoredVehicles)
            {
                if (entry.m_Vehicle != Entity.Null && entityManager.Exists(entry.m_Vehicle))
                    state.VehicleIds[entry.m_Vehicle] = entry.m_VehicleId.ToString();
            }

            foreach (PassengerFlowBaselineElement entry in state.RestoredBaselines)
            {
                if (entry.m_Vehicle == Entity.Null || !entityManager.Exists(entry.m_Vehicle)
                    || (entry.m_IsEmpty == 0 && (entry.m_Passenger == Entity.Null || !entityManager.Exists(entry.m_Passenger))))
                {
                    continue;
                }
                if (!state.Baselines.TryGetValue(entry.m_Vehicle, out PassengerBaseline baseline))
                {
                    baseline = new PassengerBaseline();
                    state.Baselines[entry.m_Vehicle] = baseline;
                }
                if (entry.m_IsEmpty == 0)
                {
                    baseline.Passengers.Add(entry.m_Passenger);
                    baseline.Purposes[entry.m_Passenger] = new PurposeInfo(entry.m_RawPurpose,
                        (PurposeCategory)entry.m_PurposeCategory);
                }
            }

            List<PendingSample> mergedSamples = new List<PendingSample>(state.PendingSamples);
            Dictionary<(Entity, uint, SectionKind), List<SectionSegment>> restoredSections =
                new Dictionary<(Entity, uint, SectionKind), List<SectionSegment>>();
            foreach (PassengerFlowSectionElement entry in state.RestoredSections)
            {
                var key = (entry.m_Vehicle, entry.m_OpenFrame, (SectionKind)entry.m_SectionKind);
                if (!restoredSections.TryGetValue(key, out List<SectionSegment> segments))
                    restoredSections[key] = segments = new List<SectionSegment>();
                segments.Add(new SectionSegment(entry.m_FromStationSakIndex, entry.m_ToStationSakIndex,
                    entry.m_FromStationOccurrence, entry.m_ToStationOccurrence));
            }
            foreach (PassengerFlowSampleElement entry in state.RestoredSamples)
            {
                if (entry.m_Line == Entity.Null || entry.m_Vehicle == Entity.Null
                    || !entityManager.Exists(entry.m_Line) || !entityManager.Exists(entry.m_Vehicle))
                {
                    continue;
                }
                PendingSample restored = new PendingSample(
                    frame + entry.m_RemainingSampleFrames, entry.m_OpenFrame, entry.m_DepartureFrame,
                    new TimeBucketKey(entry.m_DepartureDayIndex, entry.m_DepartureBucketMinute),
                    entry.m_VehicleId.ToString(), (TransitMode)entry.m_Mode, entry.m_LineId.ToString(),
                    entry.m_Line, entry.m_Vehicle, entry.m_RuntimeVehicle, entry.m_OpenWaypointIndex,
                    entry.m_OpenStationSakIndex,
                    new StopPosition(entry.m_OpenStop, entry.m_OpenWaypoint, entry.m_OpenWaypointIndex,
                        entry.m_StationOccurrence),
                    RestoreSections(restoredSections, entry, SectionKind.Stops),
                    RestoreSections(restoredSections, entry, SectionKind.Track));
                bool duplicate = false;
                for (int i = 0; i < mergedSamples.Count; i++)
                {
                    if (mergedSamples[i].Vehicle == restored.Vehicle && mergedSamples[i].OpenFrame == restored.OpenFrame)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    mergedSamples.Add(restored);
            }
            mergedSamples.Sort((left, right) => left.SampleFrame.CompareTo(right.SampleFrame));
            state.PendingSamples.Clear();
            for (int i = 0; i < mergedSamples.Count; i++)
                state.PendingSamples.Enqueue(mergedSamples[i]);

            foreach (PassengerFlowStopElement entry in state.RestoredStops)
            {
                if (entry.m_Vehicle != Entity.Null && entry.m_Line != Entity.Null
                    && entityManager.Exists(entry.m_Vehicle) && entityManager.Exists(entry.m_Line)
                    && !state.OpenStops.ContainsKey(entry.m_Vehicle)
                    && !HasSample(state.PendingSamples, entry.m_Vehicle, entry.m_OpenFrame))
                {
                    state.OpenStops[entry.m_Vehicle] = new OpenStop(entry.m_Vehicle, (TransitMode)entry.m_Mode,
                        entry.m_LineId.ToString(), entry.m_Line, entry.m_WaypointIndex, entry.m_StationSakIndex,
                        entry.m_OpenFrame, 0,
                        new StopPosition(entry.m_OpenStop, entry.m_OpenWaypoint, entry.m_WaypointIndex,
                            entry.m_StationOccurrence));
                }
            }

            List<PassengerFlowTripElement> validTrips = new List<PassengerFlowTripElement>();
            foreach (PassengerFlowTripElement entry in state.RestoredTrips)
            {
                if (entry.m_Passenger != Entity.Null && entityManager.Exists(entry.m_Passenger))
                    validTrips.Add(entry);
            }
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples = ActiveSampleKeys(state);
            state.Trips.RestoreRuntime(validTrips, frame, activeSamples);
            List<PassengerFlowPlanElement> validPlans = new List<PassengerFlowPlanElement>();
            foreach (PassengerFlowPlanElement row in state.RestoredPlans)
            {
                if (row.m_Passenger != Entity.Null && entityManager.Exists(row.m_Passenger))
                    validPlans.Add(row);
            }
            state.Trips.RestorePlans(validPlans, activeSamples);

            state.RestoredVehicles.Clear(); state.RestoredBaselines.Clear(); state.RestoredSamples.Clear(); state.RestoredStops.Clear(); state.RestoredTrips.Clear(); state.RestoredPlans.Clear();
            state.RestoredSections.Clear();
            state.RuntimeRestorePending = false;
        }

        private static void EnsureBuffer<T>(EntityManager entityManager, Entity city) where T : unmanaged, IBufferElementData
        {
            if (!entityManager.HasBuffer<T>(city))
                entityManager.AddBuffer<T>(city);
        }

        private static void Copy<T>(EntityManager entityManager, Entity city, List<T> target) where T : unmanaged, IBufferElementData
        {
            if (!entityManager.HasBuffer<T>(city))
                return;
            DynamicBuffer<T> buffer = entityManager.GetBuffer<T>(city, true);
            for (int i = 0; i < buffer.Length; i++)
                target.Add(buffer[i]);
        }

        private static void SaveSections(DynamicBuffer<PassengerFlowSectionElement> target,
            PendingSample sample, SectionSegment[] sections, SectionKind kind)
        {
            for (int i = 0; i < sections.Length; i++)
                target.Add(new PassengerFlowSectionElement
                {
                    m_Vehicle = sample.Vehicle, m_OpenFrame = sample.OpenFrame, m_SectionKind = (byte)kind,
                    m_FromStationSakIndex = sections[i].FromStationSakIndex,
                    m_ToStationSakIndex = sections[i].ToStationSakIndex,
                    m_FromStationOccurrence = sections[i].FromStationOccurrence,
                    m_ToStationOccurrence = sections[i].ToStationOccurrence
                });
        }

        private static SectionSegment[] RestoreSections(
            Dictionary<(Entity, uint, SectionKind), List<SectionSegment>> source,
            PassengerFlowSampleElement sample, SectionKind kind)
            => source.TryGetValue((sample.m_Vehicle, sample.m_OpenFrame, kind), out List<SectionSegment> segments)
                ? segments.ToArray() : System.Array.Empty<SectionSegment>();

        private static bool HasSample(Queue<PendingSample> samples, Entity vehicle, uint openFrame)
        {
            foreach (PendingSample sample in samples)
            {
                if (sample.Vehicle == vehicle && sample.OpenFrame == openFrame)
                    return true;
            }
            return false;
        }

        private static HashSet<(Entity Vehicle, uint OpenFrame)> ActiveSampleKeys(State state)
        {
            HashSet<(Entity Vehicle, uint OpenFrame)> keys =
                new HashSet<(Entity, uint)>();
            foreach (OpenStop stop in state.OpenStops.Values)
                keys.Add((stop.Vehicle, stop.OpenFrame));
            foreach (PendingSample sample in state.PendingSamples)
                keys.Add((sample.Vehicle, sample.OpenFrame));
            return keys;
        }
    }
}
