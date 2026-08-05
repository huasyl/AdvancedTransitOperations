using Game;
using Game.Serialization;
using Game.Vehicles;
using PassengerFlowJobs = RapidTransitMod.PassengerFlow.Jobs;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed partial class SamplingSystem : GameSystemBase, IPreSerialize
    {
        private const int BucketsPerWindow = 96;
        internal const uint DepartureSampleDelayFrames = 30;
        internal const uint OpenStopProbeIntervalFrames = 60;
        internal const uint OpenStopProbeScanIntervalFrames = 16;
        internal const uint PendingTransferCleanupIntervalFrames = 60;
        internal const int SameModeTransferWindowMinutes = 90;
        internal const int MaxDueSamplesPerTick = 32;
        internal const int MaxOpenStopProbeRequestsPerTick = 32;
        internal const int MaxPendingTransfers = 20000;
        private static SamplingSystem s_Current;
        internal static State CurrentState { get; private set; }
        private readonly Dictionary<Entity, LineSampleMetadata> m_LineMetadata = new Dictionary<Entity, LineSampleMetadata>();

        private readonly struct LineSampleMetadata
        {
            public readonly TransitMode Mode;
            public readonly string LineId;
            public readonly bool Supported;

            public LineSampleMetadata(TransitMode mode, string lineId, bool supported)
            {
                Mode = mode;
                LineId = lineId ?? string.Empty;
                Supported = supported;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            s_Current = this;
            CurrentState = new State();
        }

        protected override void OnUpdate()
        {
            Port port = Runtime.Current;
            State state = CurrentState;
            if (port == null || state == null)
                return;

            uint frame = port.Frame();
            UpdateBucketIfNeeded(state, port.NowDate(), port.NowMinute());
            RunPendingCleanup(state, frame);
            ExpirePendingSamples(port, state, frame);

            RunProbes(port, state, frame);
        }

        protected override void OnDestroy()
        {
            CurrentState?.Clear();
            ClearLineMetadata();
            CurrentState = null;
            if (ReferenceEquals(s_Current, this))
                s_Current = null;
            base.OnDestroy();
        }

        internal static void ClearState()
        {
            CurrentState?.Clear();
            s_Current?.ClearLineMetadata();
        }

        public void PreSerialize(Colossal.Serialization.Entities.Context context)
        {
            ModRuntimeHostSystem runtime = ModRuntimeHostSystem.Instance;
            if (runtime == null || runtime.m_CitySystem == null)
                return;

            try
            {
                Persistence.SaveToCity(EntityManager, runtime.m_CitySystem.City);
            }
            catch (Exception ex)
            {
                Mod.log.Info("[PassengerFlowPersistence] Save failed -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSupportedMode(TransitMode mode)
            => mode == TransitMode.Train || mode == TransitMode.Subway;

        private void ClearLineMetadata()
        {
            m_LineMetadata.Clear();
        }

        private LineSampleMetadata GetLineMetadata(Port port, Entity line)
        {
            if (port == null || !port.LineExists(line))
            {
                m_LineMetadata.Remove(line);
                return new LineSampleMetadata(TransitMode.Unknown, string.Empty, false);
            }

            if (m_LineMetadata.TryGetValue(line, out LineSampleMetadata metadata))
                return metadata;

            if (!port.TryLineMetadata(line, out TransitMode mode, out string lineId))
            {
                m_LineMetadata.Remove(line);
                return new LineSampleMetadata(TransitMode.Unknown, string.Empty, false);
            }

            metadata = new LineSampleMetadata(
                mode,
                lineId,
                IsSupportedMode(mode));
            m_LineMetadata[line] = metadata;
            return metadata;
        }

        internal static void ClockChanged(Port port)
        {
            State state = CurrentState;
            if (port == null || state == null)
                return;

            state.Trips.ClearPending();
            UpdateBucket(state, port.NowDate(), port.NowMinute());
        }

        private static void UpdateBucketIfNeeded(State state, DateTime nowDate, int nowMinute)
        {
            int serviceDayKey = ServiceDayKey(nowDate);
            if (state.ServiceDayKey == serviceDayKey
                && state.LastBucketUpdateMinute == nowMinute
                && state.LastMinute == nowMinute)
            {
                return;
            }

            UpdateBucket(state, nowDate, nowMinute);
            state.LastBucketUpdateMinute = nowMinute;
        }

        private static void UpdateBucket(State state, DateTime nowDate, int nowMinute)
        {
            state.ServiceDayKey = ServiceDayKey(nowDate);
            state.LastMinute = nowMinute;
            int bucketStartMinute = (nowMinute / Snapshot.BucketMinutes) * Snapshot.BucketMinutes;
            state.CurrentBucket = new TimeBucketKey(state.ServiceDayKey, bucketStartMinute);
            state.CurrentAbsoluteBucketIndex = AbsoluteBucketIndex(state.CurrentBucket);
            state.RollingWindow.Add(state.CurrentBucket);
            TrimRollingWindow(state);
        }

        private static void TrimRollingWindow(State state)
        {
            int currentAbsoluteBucket = AbsoluteBucketIndex(state.CurrentBucket);
            int minAbsoluteBucket = currentAbsoluteBucket - (BucketsPerWindow - 1);
            if (minAbsoluteBucket < 0)
                minAbsoluteBucket = 0;

            int minServiceDayKey = BucketFromAbsoluteIndex(minAbsoluteBucket).ServiceDayKey;
            int minBucketStartMinute = (minAbsoluteBucket % BucketsPerWindow) * Snapshot.BucketMinutes;
            state.Aggregates.TrimBefore(minServiceDayKey, minBucketStartMinute);

            if (state.RollingWindow.Count <= BucketsPerWindow)
                return;

            System.Collections.Generic.List<TimeBucketKey> removeBuckets = null;
            foreach (TimeBucketKey bucket in state.RollingWindow)
            {
                int absoluteBucket = AbsoluteBucketIndex(bucket);
                if (absoluteBucket < minAbsoluteBucket)
                {
                    if (removeBuckets == null)
                        removeBuckets = new System.Collections.Generic.List<TimeBucketKey>();
                    removeBuckets.Add(bucket);
                }
            }

            if (removeBuckets == null)
                return;

            for (int i = 0; i < removeBuckets.Count; i++)
                state.RollingWindow.Remove(removeBuckets[i]);
        }

        internal static int AbsoluteBucketIndex(TimeBucketKey bucket)
        {
            DateTime date = DateFromServiceDayKey(bucket.ServiceDayKey);
            return checked((int)(date.Ticks / TimeSpan.TicksPerDay) * BucketsPerWindow
                + (bucket.BucketStartMinute / Snapshot.BucketMinutes));
        }

        internal static TimeBucketKey BucketFromAbsoluteIndex(int absoluteBucketIndex)
        {
            int safeIndex = absoluteBucketIndex < 0 ? 0 : absoluteBucketIndex;
            DateTime date = new DateTime((long)(safeIndex / BucketsPerWindow) * TimeSpan.TicksPerDay);
            return new TimeBucketKey(
                ServiceDayKey(date),
                (safeIndex % BucketsPerWindow) * Snapshot.BucketMinutes);
        }

        internal static int ServiceDayKey(DateTime nowDate)
            => nowDate.Year * 10000 + nowDate.Month * 100 + nowDate.Day;

        internal static DateTime DateFromServiceDayKey(int serviceDayKey)
        {
            int year = serviceDayKey / 10000;
            int month = (serviceDayKey / 100) % 100;
            int day = serviceDayKey % 100;
            return new DateTime(year, month, day);
        }

        internal static void TrimRollingWindowForRestore(State state)
        {
            if (state == null)
                return;

            state.CurrentAbsoluteBucketIndex = AbsoluteBucketIndex(state.CurrentBucket);
            TrimRollingWindow(state);
        }

        private void ExpirePendingSamples(Port port, State state, uint frame)
        {
            List<PendingSample> readySamples = null;
            while (state.PendingSamples.Count > 0
                && state.PendingSamples.Peek().SampleFrame <= frame
                && (readySamples == null || readySamples.Count < MaxDueSamplesPerTick))
            {
                PendingSample sample = state.PendingSamples.Dequeue();
                if (!IsPendingSampleStillValid(port, sample))
                {
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningStalePendingSample,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                if (readySamples == null)
                    readySamples = new List<PendingSample>();
                readySamples.Add(sample);
            }

            if (readySamples == null || readySamples.Count == 0)
                return;

            RunPassengerSampleJobs(port, state, frame, readySamples);
        }

        private static bool IsPendingSampleStillValid(Port port, PendingSample request)
        {
            if (port == null
                || request.Vehicle == Entity.Null
                || request.Line == Entity.Null
                || !port.TryState(request.Vehicle, out _)
                || !port.TryLine(request.Vehicle, out Entity currentLine)
                || currentLine != request.Line
                || !port.TryLineMetadata(request.Line, out TransitMode mode, out _)
                || mode != request.Mode)
            {
                return false;
            }

            return request.OpenWaypointIndex >= 0
                && port.TryDwellAnchor(request.Line, request.OpenWaypointIndex, out _);
        }

        private static void RunPendingCleanup(State state, uint frame)
        {
            if (frame < state.LastPendingCleanupFrame + PendingTransferCleanupIntervalFrames)
                return;

            state.LastPendingCleanupFrame = frame;
            state.Trips.CleanupExpired(frame, state.Aggregates);
            state.Trips.EnforceLimit(MaxPendingTransfers, state.Aggregates);
        }

        private void RunProbes(Port port, State state, uint frame)
        {
            if (state.LastProbeScanFrame != 0
                && frame > state.LastProbeScanFrame
                && frame - state.LastProbeScanFrame < OpenStopProbeScanIntervalFrames)
            {
                return;
            }

            state.LastProbeScanFrame = frame;
            List<OpenStop> openStops = null;
            foreach (OpenStop openStop in state.OpenStops.Values)
            {
                Entity baselineKey = BaselineKey(port, openStop);
                if (!state.Baselines.TryGetValue(baselineKey, out PassengerBaseline baseline)
                    || baseline.Passengers.Count == 0)
                {
                    continue;
                }

                if (state.LastProbeFrames.TryGetValue(openStop.Vehicle, out uint lastProbe)
                    && frame < lastProbe + OpenStopProbeIntervalFrames)
                {
                    continue;
                }

                if (openStops == null)
                    openStops = new List<OpenStop>();
                openStops.Add(openStop);
                state.LastProbeFrames[openStop.Vehicle] = frame;
                if (openStops.Count >= MaxOpenStopProbeRequestsPerTick)
                    break;
            }

            if (openStops == null || openStops.Count == 0)
                return;

            RunProbeJobs(port, state, frame, openStops);
        }

        private void RunProbeJobs(Port port, State state, uint frame, List<OpenStop> openStops)
        {
            int requestCount = openStops.Count;
            NativeArray<VehicleSampleRequest> requests = new NativeArray<VehicleSampleRequest>(requestCount, Allocator.TempJob);
            for (int i = 0; i < requestCount; i++)
            {
                OpenStop openStop = openStops[i];
                requests[i] = new VehicleSampleRequest(
                    frame,
                    openStop.Mode,
                    openStop.Line,
                    openStop.Vehicle,
                    BaselineKey(port, openStop),
                    openStop.OpenWaypointIndex,
                    openStop.OpenStationSakIndex,
                    -1,
                    -1);
            }

            BufferLookup<Passenger> passengerBuffers = GetBufferLookup<Passenger>(true);
            BufferLookup<LayoutElement> layoutBuffers = GetBufferLookup<LayoutElement>(true);
            Dependency.Complete();
            int currentCount = Math.Max(1, EstimateCurrentPassengerCapacity(requests, passengerBuffers, layoutBuffers));
            NativeParallelMultiHashMap<int, Entity> currentPassengers =
                new NativeParallelMultiHashMap<int, Entity>(currentCount, Allocator.TempJob);
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results =
                new NativeArray<PassengerFlowJobs.VehicleSampleResult>(requestCount, Allocator.TempJob);

            try
            {
                PassengerFlowJobs.VehicleScanJob scanJob = new PassengerFlowJobs.VehicleScanJob
                {
                    Requests = requests,
                    PassengerBuffers = passengerBuffers,
                    LayoutBuffers = layoutBuffers,
                    CurrentPassengers = currentPassengers.AsParallelWriter(),
                    Results = results
                };

                Dependency = scanJob.Schedule(requestCount, 1, Dependency);
                Dependency.Complete();
                CommitProbes(state, frame, openStops, results, currentPassengers);
            }
            finally
            {
                results.Dispose();
                currentPassengers.Dispose();
                requests.Dispose();
            }
        }

        private void RunPassengerSampleJobs(Port port, State state, uint frame, List<PendingSample> samples)
        {
            int requestCount = samples.Count;
            NativeArray<VehicleSampleRequest> requests = new NativeArray<VehicleSampleRequest>(requestCount, Allocator.TempJob);
            NativeArray<byte> hasPreviousBaseline = new NativeArray<byte>(requestCount, Allocator.TempJob);
            int previousCapacity = 0;
            for (int i = 0; i < requestCount; i++)
            {
                VehicleSampleRequest jobRequest = samples[i].ToJobRequest();
                requests[i] = jobRequest;
                if (state.Baselines.TryGetValue(jobRequest.BaselineKey, out PassengerBaseline baseline)
                    && baseline.Passengers.Count > 0)
                {
                    hasPreviousBaseline[i] = 1;
                    previousCapacity += baseline.Passengers.Count;
                }
            }

            NativeParallelMultiHashMap<int, Entity> previousPassengers =
                new NativeParallelMultiHashMap<int, Entity>(Math.Max(1, previousCapacity), Allocator.TempJob);
            for (int i = 0; i < requestCount; i++)
            {
                if (hasPreviousBaseline[i] == 0)
                    continue;

                VehicleSampleRequest request = requests[i];
                PassengerBaseline baseline = state.Baselines[request.BaselineKey];
                for (int p = 0; p < baseline.Passengers.Count; p++)
                    previousPassengers.Add(i, baseline.Passengers[p]);
            }

            BufferLookup<Passenger> passengerBuffers = GetBufferLookup<Passenger>(true);
            BufferLookup<LayoutElement> layoutBuffers = GetBufferLookup<LayoutElement>(true);
            int currentCapacity = Math.Max(1, EstimateCurrentPassengerCapacity(requests, passengerBuffers, layoutBuffers));
            NativeParallelMultiHashMap<int, Entity> currentPassengers =
                new NativeParallelMultiHashMap<int, Entity>(currentCapacity, Allocator.TempJob);
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results =
                new NativeArray<PassengerFlowJobs.VehicleSampleResult>(requestCount, Allocator.TempJob);
            NativeList<PassengerFlowJobs.BoardEvent> boardEvents =
                new NativeList<PassengerFlowJobs.BoardEvent>(Allocator.TempJob);
            NativeList<PassengerFlowJobs.AlightEvent> alightEvents =
                new NativeList<PassengerFlowJobs.AlightEvent>(Allocator.TempJob);
            NativeList<PassengerFlowJobs.DepartureLoadEvent> departureLoadEvents =
                new NativeList<PassengerFlowJobs.DepartureLoadEvent>(Allocator.TempJob);
            NativeParallelMultiHashMap<int, Entity> nextBaseline =
                new NativeParallelMultiHashMap<int, Entity>(currentCapacity, Allocator.TempJob);

            try
            {
                PassengerFlowJobs.VehicleScanJob scanJob = new PassengerFlowJobs.VehicleScanJob
                {
                    Requests = requests,
                    PassengerBuffers = passengerBuffers,
                    LayoutBuffers = layoutBuffers,
                    CurrentPassengers = currentPassengers.AsParallelWriter(),
                    Results = results
                };

                JobHandle scanHandle = scanJob.Schedule(requestCount, 1, Dependency);
                PassengerFlowJobs.DiffJob diffJob = new PassengerFlowJobs.DiffJob
                {
                    Requests = requests,
                    PreviousPassengers = previousPassengers,
                    CurrentPassengers = currentPassengers,
                    BoardEvents = boardEvents,
                    AlightEvents = alightEvents,
                    DepartureLoadEvents = departureLoadEvents,
                    NextBaseline = nextBaseline
                };

                Dependency = diffJob.Schedule(scanHandle);
                Dependency.Complete();
                CommitPassengerSampleResults(port, state, frame, samples, requests, hasPreviousBaseline, results, boardEvents, alightEvents, departureLoadEvents, nextBaseline);
            }
            finally
            {
                nextBaseline.Dispose();
                departureLoadEvents.Dispose();
                alightEvents.Dispose();
                boardEvents.Dispose();
                results.Dispose();
                currentPassengers.Dispose();
                previousPassengers.Dispose();
                hasPreviousBaseline.Dispose();
                requests.Dispose();
            }
        }

        private static void CommitProbes(
            State state,
            uint frame,
            List<OpenStop> openStops,
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results,
            NativeParallelMultiHashMap<int, Entity> currentPassengers)
        {
            uint transferWindowFrames = Runtime.Current != null
                ? Runtime.Current.ToFramesCeil(SameModeTransferWindowMinutes)
                : 1u;
            for (int i = 0; i < openStops.Count; i++)
            {
                OpenStop openStop = openStops[i];
                PassengerFlowJobs.VehicleSampleResult result = results[i];
                if (result.StatusCode != (int)PassengerFlowJobs.VehicleScanStatus.Ok)
                {
                    state.Aggregates.RecordWarning(
                        openStop.Mode,
                        result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.LayoutMissing
                            ? Aggregates.WarningLayoutMissing
                            : Aggregates.WarningPassengerBufferMissing,
                        openStop.LineId,
                        openStop.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                HashSet<Entity> currentSet = new HashSet<Entity>();
                NativeParallelMultiHashMapIterator<int> iterator;
                Entity passenger;
                if (currentPassengers.TryGetFirstValue(i, out passenger, out iterator))
                {
                    do
                    {
                        currentSet.Add(passenger);
                        state.Trips.TryCancelReturn(
                            passenger,
                            openStop.Vehicle,
                            frame,
                            state.CurrentBucket,
                            state.Aggregates);
                        state.Trips.TryMatchBoard(
                            passenger,
                            openStop.Vehicle,
                            openStop.Mode,
                            openStop.LineId,
                            openStop.OpenStationSakIndex,
                            frame,
                            state.CurrentBucket,
                            state.Aggregates);
                    }
                    while (currentPassengers.TryGetNextValue(out passenger, ref iterator));
                }

                Entity baselineKey = BaselineKey(openStop);
                if (!state.Baselines.TryGetValue(baselineKey, out PassengerBaseline baseline))
                    continue;

                for (int p = 0; p < baseline.Passengers.Count; p++)
                {
                    Entity baselinePassenger = baseline.Passengers[p];
                    if (currentSet.Contains(baselinePassenger) || !state.Trips.HasActiveTrip(baselinePassenger))
                        continue;

                    state.Trips.TryCreatePending(
                        baselinePassenger,
                        openStop,
                        frame,
                        state.CurrentBucket,
                        frame + transferWindowFrames);
                }
            }

            state.Trips.EnforceLimit(MaxPendingTransfers, state.Aggregates);
        }

        private static Entity BaselineKey(Port port, OpenStop openStop)
        {
            if (port == null || openStop.Vehicle == Entity.Null)
                return openStop.Vehicle;

            Entity runtimeVehicle = port.RuntimeVehicle(openStop.Vehicle);
            return runtimeVehicle != Entity.Null ? runtimeVehicle : openStop.Vehicle;
        }

        private static Entity BaselineKey(OpenStop openStop)
        {
            Entity runtimeVehicle = Runtime.Current != null
                ? Runtime.Current.RuntimeVehicle(openStop.Vehicle)
                : Entity.Null;
            return runtimeVehicle != Entity.Null ? runtimeVehicle : openStop.Vehicle;
        }

        private static int EstimateCurrentPassengerCapacity(
            NativeArray<VehicleSampleRequest> requests,
            BufferLookup<Passenger> passengerBuffers,
            BufferLookup<LayoutElement> layoutBuffers)
        {
            int passengerCountEstimate = 0;
            for (int i = 0; i < requests.Length; i++)
            {
                Entity vehicle = requests[i].RuntimeVehicle != Entity.Null
                    ? requests[i].RuntimeVehicle
                    : requests[i].Vehicle;
                if (vehicle == Entity.Null)
                    continue;

                if (layoutBuffers.HasBuffer(vehicle))
                {
                    DynamicBuffer<LayoutElement> layout = layoutBuffers[vehicle];
                    for (int j = 0; j < layout.Length; j++)
                    {
                        Entity layoutVehicle = layout[j].m_Vehicle;
                        if (layoutVehicle != Entity.Null && passengerBuffers.HasBuffer(layoutVehicle))
                            passengerCountEstimate += passengerBuffers[layoutVehicle].Length;
                    }

                    continue;
                }

                if (passengerBuffers.HasBuffer(vehicle))
                    passengerCountEstimate += passengerBuffers[vehicle].Length;
            }

            return passengerCountEstimate;
        }

        private static void CommitPassengerSampleResults(
            Port port,
            State state,
            uint frame,
            List<PendingSample> samples,
            NativeArray<VehicleSampleRequest> requests,
            NativeArray<byte> hasPreviousBaseline,
            NativeArray<PassengerFlowJobs.VehicleSampleResult> results,
            NativeList<PassengerFlowJobs.BoardEvent> boardEvents,
            NativeList<PassengerFlowJobs.AlightEvent> alightEvents,
            NativeList<PassengerFlowJobs.DepartureLoadEvent> departureLoadEvents,
            NativeParallelMultiHashMap<int, Entity> nextBaseline)
        {
            bool[] okRequests = new bool[samples.Count];
            for (int i = 0; i < results.Length; i++)
            {
                PendingSample sample = samples[i];
                PassengerFlowJobs.VehicleSampleResult result = results[i];
                if (result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.PassengerBufferMissing)
                {
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningPassengerBufferMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                if (result.StatusCode == (int)PassengerFlowJobs.VehicleScanStatus.LayoutMissing)
                {
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningLayoutMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                    continue;
                }

                okRequests[i] = true;
                if (hasPreviousBaseline[i] == 0)
                {
                    state.Aggregates.RecordWarning(
                        sample.Mode,
                        Aggregates.WarningOriginBaselineMissing,
                        sample.LineId,
                        sample.OpenStationSakIndex,
                        state.CurrentBucket,
                        frame);
                }
            }

            for (int i = 0; i < boardEvents.Length; i++)
            {
                PassengerFlowJobs.BoardEvent boardEvent = boardEvents[i];
                if (!okRequests[boardEvent.RequestIndex] || hasPreviousBaseline[boardEvent.RequestIndex] == 0)
                    continue;

                PendingSample sample = samples[boardEvent.RequestIndex];
                state.Aggregates.RecordBoarding(
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                state.Trips.OnBoard(
                    boardEvent.Passenger,
                    sample.Vehicle,
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    frame,
                    state.CurrentBucket,
                    state.Aggregates);
            }

            for (int i = 0; i < alightEvents.Length; i++)
            {
                PassengerFlowJobs.AlightEvent alightEvent = alightEvents[i];
                if (!okRequests[alightEvent.RequestIndex] || hasPreviousBaseline[alightEvent.RequestIndex] == 0)
                    continue;

                PendingSample sample = samples[alightEvent.RequestIndex];
                state.Aggregates.RecordAlighting(
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame);
                state.Trips.OnAlight(
                    alightEvent.Passenger,
                    sample.Mode,
                    sample.LineId,
                    sample.OpenStationSakIndex,
                    state.CurrentBucket,
                    frame,
                    state.Aggregates);
            }

            for (int i = 0; i < departureLoadEvents.Length; i++)
            {
                PassengerFlowJobs.DepartureLoadEvent loadEvent = departureLoadEvents[i];
                if (!okRequests[loadEvent.RequestIndex])
                    continue;

                PendingSample sample = samples[loadEvent.RequestIndex];
                SectionLoadEvent[] sectionLoads = state.Sections.Expand(port, state, sample, loadEvent, frame);
                for (int s = 0; s < sectionLoads.Length; s++)
                {
                    SectionLoadEvent sectionLoad = sectionLoads[s];
                    state.Aggregates.RecordSectionLoad(
                        sectionLoad.Mode,
                        sectionLoad.LineId,
                        sectionLoad.FromStationSakIndex,
                        sectionLoad.ToStationSakIndex,
                        sectionLoad.PassengerCount,
                        state.CurrentBucket,
                        frame);
                }
            }

            for (int i = 0; i < requests.Length; i++)
            {
                if (!okRequests[i])
                    continue;

                List<Entity> passengers = new List<Entity>();
                NativeParallelMultiHashMapIterator<int> iterator;
                Entity passenger;
                if (nextBaseline.TryGetFirstValue(i, out passenger, out iterator))
                {
                    do
                    {
                        passengers.Add(passenger);
                    }
                    while (nextBaseline.TryGetNextValue(out passenger, ref iterator));
                }

                Entity baselineKey = requests[i].BaselineKey;
                if (!state.Baselines.TryGetValue(baselineKey, out PassengerBaseline baseline))
                {
                    baseline = new PassengerBaseline();
                    state.Baselines[baselineKey] = baseline;
                }

                baseline.Replace(passengers);
            }
        }
    }
}
