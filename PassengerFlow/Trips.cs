using System.Collections.Generic;
using Unity.Entities;
using RapidTransitMod.Core;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Trips
    {
        private readonly Dictionary<Entity, ActiveTrip> m_ActiveTrips = new Dictionary<Entity, ActiveTrip>();
        private readonly Dictionary<Entity, PendingTransfer> m_Pending = new Dictionary<Entity, PendingTransfer>();
        private readonly Dictionary<Entity, StopPlanSummary> m_StopPlans = new Dictionary<Entity, StopPlanSummary>();
        private readonly Dictionary<Entity, CompletedSample> m_CompletedSamples = new Dictionary<Entity, CompletedSample>();
        private readonly Queue<PendingExpiry> m_ExpiryQueue = new Queue<PendingExpiry>();
        private readonly List<Entity> m_RemoveKeys = new List<Entity>();
        private readonly Dictionary<Entity, AlightMark> m_HandledAlights = new Dictionary<Entity, AlightMark>();
        private int m_NextGeneration;

        internal int ActiveTripCount => m_ActiveTrips.Count;
        internal int PendingTransferCount => m_Pending.Count;

        internal void Clear()
        {
            m_ActiveTrips.Clear();
            m_Pending.Clear();
            m_StopPlans.Clear();
            m_CompletedSamples.Clear();
            m_ExpiryQueue.Clear();
            m_RemoveKeys.Clear();
            m_HandledAlights.Clear();
            m_NextGeneration = 0;
        }

        internal bool TryActiveVehicle(Entity passenger, out Entity vehicle)
        {
            vehicle = Entity.Null;
            if (!m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip))
                return false;
            vehicle = trip.Vehicle;
            return true;
        }

        internal void AcceptStopPlan(Entity passenger, OpenStop stop, StopPlanRead plan)
        {
            if (plan.AlightsHere && m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip)
                && trip.Vehicle == stop.Vehicle)
                m_StopPlans[passenger] = new StopPlanSummary(stop.Vehicle, stop.OpenFrame, plan);
        }

        internal bool TrySelectRepresentative(Entity passenger, OpenStop stop, StopPlanRead plan,
            SampleGroupKey group, Aggregates aggregates, uint expiresFrame,
            out PassengerFlowTransferSample sample)
        {
            sample = default;
            if (!plan.AlightsHere || plan.NextLeg.Kind != NextLegKind.NextLeg
                || !m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip)
                || trip.Vehicle != stop.Vehicle || !aggregates.TryReserveSample(group))
                return false;
            sample = new PassengerFlowTransferSample
            {
                m_OldVehicle = stop.Vehicle,
                m_OpenFrame = stop.OpenFrame,
                m_TargetWaypoint = plan.BoardWaypoint,
                m_TargetStop = plan.BoardStop,
                m_TargetMode = (int)group.ToMode,
                m_TargetLineId = group.ToLineId,
                m_TargetStationSakIndex = group.ToStationIndex,
                m_TargetStationOccurrence = group.ToStationOccurrence,
                m_ExpiresFrame = expiresFrame
            };
            return true;
        }

        internal bool TryStopPlan(Entity passenger, Entity vehicle, uint openFrame, out StopPlanRead plan)
        {
            if (m_StopPlans.TryGetValue(passenger, out StopPlanSummary summary)
                && summary.Vehicle == vehicle && summary.OpenFrame == openFrame)
            {
                plan = summary.Plan;
                return true;
            }
            plan = default;
            return false;
        }

        internal void CapturePlans(List<PassengerFlowPlanElement> records,
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples)
        {
            foreach (KeyValuePair<Entity, StopPlanSummary> entry in m_StopPlans)
            {
                StopPlanSummary summary = entry.Value;
                if (!activeSamples.Contains((summary.Vehicle, summary.OpenFrame)))
                    continue;
                records.Add(new PassengerFlowPlanElement
                {
                    m_Passenger = entry.Key, m_Vehicle = summary.Vehicle,
                    m_OpenFrame = summary.OpenFrame,
                    m_NextKind = (byte)summary.Plan.NextLeg.Kind,
                    m_BoardStationSakIndex = summary.Plan.NextLeg.BoardStationSakIndex
                });
            }
        }

        internal void RestorePlans(List<PassengerFlowPlanElement> records,
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples)
        {
            for (int i = 0; i < records.Count; i++)
            {
                PassengerFlowPlanElement row = records[i];
                if (!activeSamples.Contains((row.m_Vehicle, row.m_OpenFrame))
                    || m_StopPlans.ContainsKey(row.m_Passenger))
                    continue;
                StopPlanRead plan = new StopPlanRead(true,
                    new NextLegRead((NextLegKind)row.m_NextKind, row.m_BoardStationSakIndex),
                    Entity.Null, Entity.Null, Entity.Null);
                m_StopPlans[row.m_Passenger] = new StopPlanSummary(row.m_Vehicle, row.m_OpenFrame, plan);
            }
        }

        internal void AcceptRepresentative(Entity passenger, Entity oldVehicle, uint openFrame,
            Entity targetStop, TransitMode targetMode, string targetLineId,
            int targetStationIndex, int targetOccurrence, double? distanceMeters, uint walkFrames)
        {
            if (m_Pending.TryGetValue(passenger, out PendingTransfer pending)
                && pending.PreviousVehicle == oldVehicle
                && pending.OpenFrame == openFrame)
            {
                m_CompletedSamples[passenger] = new CompletedSample(oldVehicle, openFrame,
                    targetStop, targetMode, targetLineId, targetStationIndex,
                    targetOccurrence, distanceMeters, walkFrames);
                return;
            }
            if (m_StopPlans.TryGetValue(passenger, out StopPlanSummary plan)
                && plan.Vehicle == oldVehicle && plan.OpenFrame == openFrame)
                m_CompletedSamples[passenger] = new CompletedSample(oldVehicle, openFrame,
                    targetStop, targetMode, targetLineId, targetStationIndex,
                    targetOccurrence, distanceMeters, walkFrames);
        }

        internal void RemoveVehicle(Entity vehicle)
        {
            if (vehicle == Entity.Null)
                return;

            m_RemoveKeys.Clear();
            foreach (KeyValuePair<Entity, ActiveTrip> entry in m_ActiveTrips)
            {
                if (entry.Value.Vehicle == vehicle && !m_Pending.ContainsKey(entry.Key))
                    m_RemoveKeys.Add(entry.Key);
            }

            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                m_ActiveTrips.Remove(m_RemoveKeys[i]);
                m_StopPlans.Remove(m_RemoveKeys[i]);
                m_CompletedSamples.Remove(m_RemoveKeys[i]);
            }
            m_RemoveKeys.Clear();
            foreach (KeyValuePair<Entity, AlightMark> entry in m_HandledAlights)
            {
                if (entry.Value.Vehicle == vehicle)
                    m_RemoveKeys.Add(entry.Key);
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
                m_HandledAlights.Remove(m_RemoveKeys[i]);
        }

        internal void OnBoard(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string lineId,
            int originStationSakIndex,
            StopPosition boardPosition,
            uint frame,
            TimeBucketKey bucket,
            Aggregates aggregates,
            PurposeInfo purpose,
            OpenStop? previousStop,
            TimeBucketKey previousBucket,
            uint previousAlightFrame,
            uint transferWindowFrames,
            uint observationFrame,
            bool hasRepresentative,
            in PassengerFlowTransferSample representative)
        {
            if (passenger == Entity.Null)
                return;

            if (previousStop.HasValue && m_ActiveTrips.TryGetValue(passenger, out ActiveTrip previousTrip)
                && previousTrip.Vehicle == previousStop.Value.Vehicle && !m_Pending.ContainsKey(passenger))
            {
                OpenStop stop = previousStop.Value;
                BeginAlight(passenger, previousTrip, stop.OpenStationSakIndex, previousBucket,
                    stop.Position,
                    previousAlightFrame, stop.OpenFrame,
                    previousAlightFrame + transferWindowFrames);
                m_HandledAlights[passenger] = new AlightMark(stop.Vehicle, stop.OpenFrame);
            }

            if (TryMatchBoard(passenger, vehicle, mode, lineId, originStationSakIndex,
                boardPosition, frame, bucket,
                aggregates, purpose, observationFrame, hasRepresentative, in representative))
                return;

            if (m_ActiveTrips.ContainsKey(passenger))
                return;

            m_ActiveTrips[passenger] = new ActiveTrip(
                passenger,
                vehicle,
                mode,
                lineId,
                lineId,
                lineId,
                originStationSakIndex,
                purpose,
                purpose);
        }

        internal void OnAlight(
            Entity passenger,
            Entity vehicle,
            uint openFrame,
            TransitMode mode,
            string lineId,
            int destinationStationSakIndex,
            StopPosition alightPosition,
            TimeBucketKey actualAlightBucket,
            uint frame,
            uint expiresFrame,
            NextLegRead nextLeg,
            Aggregates aggregates)
        {
            if (m_HandledAlights.TryGetValue(passenger, out AlightMark mark)
                && mark.Vehicle == vehicle && mark.OpenFrame == openFrame)
                return;
            m_HandledAlights[passenger] = new AlightMark(vehicle, openFrame);
            if (passenger == Entity.Null || !m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip))
            {
                aggregates.RecordWarning(
                    mode, Aggregates.WarningUnknownOriginAlighting, lineId,
                    destinationStationSakIndex, actualAlightBucket, frame);
                return;
            }

            if (trip.Vehicle != vehicle)
                return;

            if (m_Pending.ContainsKey(passenger))
                return;

            if (nextLeg.Kind == NextLegKind.NoNextLeg || nextLeg.Kind == NextLegKind.UnsupportedLeg)
            {
                aggregates.RecordCompletedOd(
                    trip.Mode, trip.FirstLineId, trip.LastLineId, trip.OriginStationSakIndex,
                        destinationStationSakIndex, actualAlightBucket, trip.OriginPurpose);
                m_ActiveTrips.Remove(passenger);
                m_CompletedSamples.Remove(passenger);
                return;
            }

            BeginAlight(passenger, trip, destinationStationSakIndex, actualAlightBucket,
                alightPosition,
                frame, openFrame, expiresFrame);
        }

        private void BeginAlight(Entity passenger, ActiveTrip trip, int stationSakIndex,
            TimeBucketKey bucket, StopPosition position,
            uint frame, uint openFrame, uint expiresFrame)
        {
            int generation = ++m_NextGeneration;
            PendingTransfer pending = new PendingTransfer(passenger, trip.Mode,
                trip.OriginStationSakIndex, stationSakIndex, position,
                frame, bucket,
                trip.FirstLineId, trip.CurrentLineId, trip.Vehicle, expiresFrame,
                generation, openFrame);
            m_Pending[passenger] = pending;
            m_ExpiryQueue.Enqueue(new PendingExpiry(expiresFrame, passenger, generation));
        }

        internal void ReleaseAlightMarks(Entity vehicle, uint openFrame)
        {
            m_RemoveKeys.Clear();
            foreach (KeyValuePair<Entity, AlightMark> entry in m_HandledAlights)
            {
                if (entry.Value.Vehicle == vehicle && entry.Value.OpenFrame == openFrame)
                    m_RemoveKeys.Add(entry.Key);
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
                m_HandledAlights.Remove(m_RemoveKeys[i]);
            m_RemoveKeys.Clear();
            foreach (KeyValuePair<Entity, StopPlanSummary> entry in m_StopPlans)
            {
                if (entry.Value.Vehicle == vehicle && entry.Value.OpenFrame == openFrame)
                    m_RemoveKeys.Add(entry.Key);
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                m_StopPlans.Remove(m_RemoveKeys[i]);
                if (!m_Pending.TryGetValue(m_RemoveKeys[i], out PendingTransfer pending)
                    || pending.PreviousVehicle != vehicle || pending.OpenFrame != openFrame)
                    m_CompletedSamples.Remove(m_RemoveKeys[i]);
            }
        }

        private bool TryMatchBoard(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string lineId,
            int boardStationSakIndex,
            StopPosition boardPosition,
            uint frame,
            TimeBucketKey bucket,
            Aggregates aggregates,
            PurposeInfo purpose,
            uint observationFrame,
            bool hasRepresentative,
            in PassengerFlowTransferSample representative)
        {
            if (passenger == Entity.Null || !m_Pending.TryGetValue(passenger, out PendingTransfer pending))
                return false;

            if (frame > pending.ExpiresFrame)
            {
                SubmitPending(pending, Aggregates.WarningTransferWindowExpired, aggregates);
                ClosePending(pending);
                return false;
            }

            if (vehicle == pending.PreviousVehicle)
            {
                RemovePending(pending);
                return true;
            }

            if (!m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip))
            {
                aggregates?.RecordWarning(
                    mode,
                    Aggregates.WarningProvisionalTransferLost,
                    lineId,
                    boardStationSakIndex,
                    bucket,
                    frame);
                RemovePending(pending);
                return false;
            }

            bool transfer = pending.Mode != mode
                || !string.Equals(lineId, pending.PreviousLineId, System.StringComparison.Ordinal);
            if (transfer && pending.FromPosition.IsValid && boardPosition.IsValid)
            {
                double? distance = null;
                uint? walkFrames = null;
                if (m_CompletedSamples.TryGetValue(passenger, out CompletedSample completed)
                    && completed.OldVehicle == pending.PreviousVehicle
                    && completed.OpenFrame == pending.OpenFrame
                    && completed.TargetStop == boardPosition.Stop
                    && completed.TargetMode == mode
                    && string.Equals(completed.TargetLineId, lineId, System.StringComparison.Ordinal)
                    && completed.TargetStationIndex == boardStationSakIndex
                    && completed.TargetOccurrence == boardPosition.StationOccurrence)
                {
                    distance = completed.DistanceMeters;
                    walkFrames = completed.WalkFrames;
                }
                else if (hasRepresentative
                    && representative.m_OldVehicle == pending.PreviousVehicle
                    && representative.m_OpenFrame == pending.OpenFrame
                    && frame <= representative.m_ExpiresFrame
                    && representative.m_TargetStop == boardPosition.Stop
                    && representative.m_TargetMode == (int)mode
                    && string.Equals(representative.m_TargetLineId.ToString(), lineId, System.StringComparison.Ordinal)
                    && representative.m_TargetStationSakIndex == boardStationSakIndex
                    && representative.m_TargetStationOccurrence == boardPosition.StationOccurrence)
                {
                    distance = representative.m_HasWalkPath != 0 ? representative.m_WalkPathMeters : (double?)null;
                    uint alightFrame = representative.m_AlightFrame != 0
                        ? representative.m_AlightFrame : pending.ActualAlightFrame;
                    walkFrames = observationFrame - alightFrame;
                }
                aggregates.RecordTransfer(new TransferRecord(
                    pending.Mode, pending.PreviousLineId, pending.ActualAlightStationSakIndex,
                    pending.FromPosition,
                    mode, lineId, boardStationSakIndex,
                    boardPosition,
                    pending.ActualAlightBucket, pending.ActualAlightFrame, frame,
                    distance, walkFrames, trip.CurrentPurpose, purpose));
            }
            if (pending.Mode == mode)
            {
                m_ActiveTrips[passenger] = new ActiveTrip(
                    passenger, vehicle, mode, lineId, trip.FirstLineId, lineId,
                    trip.OriginStationSakIndex, purpose, trip.OriginPurpose);
            }
            else
            {
                aggregates.RecordCompletedOd(
                    trip.Mode, trip.FirstLineId, trip.LastLineId, trip.OriginStationSakIndex,
                    pending.ActualAlightStationSakIndex, pending.ActualAlightBucket,
                    trip.OriginPurpose);
                m_ActiveTrips[passenger] = new ActiveTrip(
                    passenger, vehicle, mode, lineId, lineId, lineId,
                    boardStationSakIndex, purpose, purpose);
            }
            RemovePending(pending);
            return true;
        }

        internal void CleanupExpired(uint frame, bool hasPendingDeparture, uint earliestPendingDepartureFrame, Aggregates aggregates)
        {
            while (m_ExpiryQueue.Count > 0 && m_ExpiryQueue.Peek().ExpiresFrame <= frame)
            {
                PendingExpiry expiry = m_ExpiryQueue.Peek();
                if (!m_Pending.TryGetValue(expiry.Passenger, out PendingTransfer pending)
                    || pending.Generation != expiry.Generation)
                {
                    m_ExpiryQueue.Dequeue();
                    continue;
                }

                if (hasPendingDeparture && earliestPendingDepartureFrame <= pending.ExpiresFrame)
                    return;

                m_ExpiryQueue.Dequeue();
                SubmitPending(pending, Aggregates.WarningTransferWindowExpired, aggregates);
                ClosePending(pending);
            }
        }

        internal void EnforceLimit(int maxPending, Aggregates aggregates)
        {
            if (maxPending <= 0)
                return;

            while (m_Pending.Count > maxPending && m_ExpiryQueue.Count > 0)
            {
                PendingExpiry expiry = m_ExpiryQueue.Dequeue();
                if (!m_Pending.TryGetValue(expiry.Passenger, out PendingTransfer pending)
                    || pending.Generation != expiry.Generation)
                {
                    continue;
                }

                SubmitPending(pending, Aggregates.WarningPendingTransferOverflow, aggregates);
                ClosePending(pending);
            }
        }

        internal void CaptureRuntime(List<PassengerFlowTripElement> records, uint frame,
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples)
        {
            foreach (KeyValuePair<Entity, ActiveTrip> entry in m_ActiveTrips)
            {
                if (m_Pending.ContainsKey(entry.Key))
                    continue;
                ActiveTrip trip = entry.Value;
                records.Add(new PassengerFlowTripElement
                {
                    m_Passenger = trip.Passenger, m_Vehicle = trip.Vehicle, m_Mode = (int)trip.Mode,
                    m_OriginStationSakIndex = trip.OriginStationSakIndex, m_FirstLineId = trip.FirstLineId,
                    m_CurrentLineId = trip.CurrentLineId,
                    m_RawPurpose = trip.CurrentPurpose.RawPurpose,
                    m_PurposeCategory = (byte)trip.CurrentPurpose.Category,
                    m_OriginRawPurpose = trip.OriginPurpose.RawPurpose,
                    m_OriginPurposeCategory = (byte)trip.OriginPurpose.Category
                });
            }
            foreach (PendingTransfer pending in m_Pending.Values)
            {
                ActiveTrip trip = m_ActiveTrips[pending.Passenger];
                records.Add(new PassengerFlowTripElement
                {
                    m_Passenger = pending.Passenger, m_Vehicle = pending.PreviousVehicle,
                    m_PreviousVehicle = pending.PreviousVehicle, m_Mode = (int)pending.Mode,
                    m_OriginStationSakIndex = pending.OriginStationSakIndex,
                    m_AlightStationSakIndex = pending.ActualAlightStationSakIndex,
                    m_FromStop = pending.FromPosition.Stop,
                    m_FromWaypoint = pending.FromPosition.Waypoint,
                    m_FromWaypointIndex = pending.FromPosition.WaypointIndex,
                    m_FromStationOccurrence = pending.FromPosition.StationOccurrence,
                    m_DayIndex = pending.ActualAlightBucket.DayIndex,
                    m_BucketMinute = pending.ActualAlightBucket.BucketStartMinute,
                    m_AlightFrame = pending.ActualAlightFrame,
                    m_OpenFrame = pending.OpenFrame,
                    m_RemainingExpiryFrames = pending.ExpiresFrame > frame
                        ? pending.ExpiresFrame - frame : 0u,
                    m_Generation = pending.Generation, m_Pending = 1,
                    m_FirstLineId = pending.FirstLineId, m_CurrentLineId = pending.PreviousLineId,
                    m_RawPurpose = trip.CurrentPurpose.RawPurpose,
                    m_PurposeCategory = (byte)trip.CurrentPurpose.Category,
                    m_OriginRawPurpose = trip.OriginPurpose.RawPurpose,
                    m_OriginPurposeCategory = (byte)trip.OriginPurpose.Category
                });
            }
            foreach (KeyValuePair<Entity, AlightMark> mark in m_HandledAlights)
            {
                if (!activeSamples.Contains((mark.Value.Vehicle, mark.Value.OpenFrame)))
                    continue;
                records.Add(new PassengerFlowTripElement
                {
                    m_Passenger = mark.Key, m_Vehicle = mark.Value.Vehicle,
                    m_OpenFrame = mark.Value.OpenFrame, m_Pending = 2
                });
            }
        }

        internal void RestoreRuntime(List<PassengerFlowTripElement> records, uint frame,
            HashSet<(Entity Vehicle, uint OpenFrame)> activeSamples)
        {
            for (int i = 0; i < records.Count; i++)
            {
                PassengerFlowTripElement record = records[i];
                if (record.m_Pending == 2)
                {
                    if (activeSamples.Contains((record.m_Vehicle, record.m_OpenFrame)))
                        m_HandledAlights[record.m_Passenger] = new AlightMark(record.m_Vehicle, record.m_OpenFrame);
                    continue;
                }
                TransitMode mode = (TransitMode)record.m_Mode;
                PurposeInfo currentPurpose = new PurposeInfo(record.m_RawPurpose,
                    (PurposeCategory)record.m_PurposeCategory);
                PurposeInfo originPurpose = new PurposeInfo(record.m_OriginRawPurpose,
                    (PurposeCategory)record.m_OriginPurposeCategory);
                if (record.m_Pending == 0)
                {
                    m_ActiveTrips[record.m_Passenger] = new ActiveTrip(record.m_Passenger, record.m_Vehicle,
                        mode, record.m_CurrentLineId.ToString(), record.m_FirstLineId.ToString(),
                        record.m_CurrentLineId.ToString(), record.m_OriginStationSakIndex,
                        currentPurpose, originPurpose);
                    continue;
                }

                int generation = record.m_Generation;
                if (generation > m_NextGeneration)
                    m_NextGeneration = generation;
                PendingTransfer pending = new PendingTransfer(record.m_Passenger, mode,
                    record.m_OriginStationSakIndex, record.m_AlightStationSakIndex,
                    new StopPosition(record.m_FromStop, record.m_FromWaypoint,
                        record.m_FromWaypointIndex, record.m_FromStationOccurrence),
                    record.m_AlightFrame,
                    new TimeBucketKey(record.m_DayIndex, record.m_BucketMinute), record.m_FirstLineId.ToString(),
                    record.m_CurrentLineId.ToString(), record.m_PreviousVehicle, frame + record.m_RemainingExpiryFrames,
                    generation, record.m_OpenFrame);
                m_ActiveTrips[record.m_Passenger] = new ActiveTrip(record.m_Passenger, record.m_PreviousVehicle,
                    mode, record.m_CurrentLineId.ToString(), record.m_FirstLineId.ToString(),
                    record.m_CurrentLineId.ToString(), record.m_OriginStationSakIndex,
                    currentPurpose, originPurpose);
                m_Pending[record.m_Passenger] = pending;
            }
            RebuildExpiryQueue();
        }

        private void RebuildExpiryQueue()
        {
            List<PendingTransfer> pending = new List<PendingTransfer>(m_Pending.Values);
            pending.Sort((left, right) => left.ExpiresFrame.CompareTo(right.ExpiresFrame));
            m_ExpiryQueue.Clear();
            for (int i = 0; i < pending.Count; i++)
                m_ExpiryQueue.Enqueue(new PendingExpiry(pending[i].ExpiresFrame, pending[i].Passenger, pending[i].Generation));
        }

        private void SubmitPending(PendingTransfer pending, string warningCode, Aggregates aggregates)
        {
            aggregates.RecordCompletedOd(
                pending.Mode,
                pending.FirstLineId,
                pending.PreviousLineId,
                pending.OriginStationSakIndex,
                pending.ActualAlightStationSakIndex,
                pending.ActualAlightBucket,
                m_ActiveTrips[pending.Passenger].OriginPurpose);
            aggregates.RecordWarning(
                pending.Mode,
                warningCode,
                pending.PreviousLineId,
                pending.ActualAlightStationSakIndex,
                pending.ActualAlightBucket,
                pending.ActualAlightFrame);
        }

        private void RemovePending(PendingTransfer pending)
        {
            m_Pending.Remove(pending.Passenger);
            m_CompletedSamples.Remove(pending.Passenger);
        }

        private void ClosePending(PendingTransfer pending)
        {
            m_Pending.Remove(pending.Passenger);
            m_ActiveTrips.Remove(pending.Passenger);
            m_CompletedSamples.Remove(pending.Passenger);
        }
    }

    internal readonly struct StopPlanSummary
    {
        internal readonly Entity Vehicle;
        internal readonly uint OpenFrame;
        internal readonly StopPlanRead Plan;

        internal StopPlanSummary(Entity vehicle, uint openFrame, StopPlanRead plan)
        {
            Vehicle = vehicle;
            OpenFrame = openFrame;
            Plan = plan;
        }
    }

    internal readonly struct CompletedSample
    {
        internal readonly Entity OldVehicle;
        internal readonly uint OpenFrame;
        internal readonly Entity TargetStop;
        internal readonly TransitMode TargetMode;
        internal readonly string TargetLineId;
        internal readonly int TargetStationIndex;
        internal readonly int TargetOccurrence;
        internal readonly double? DistanceMeters;
        internal readonly uint WalkFrames;

        internal CompletedSample(Entity oldVehicle, uint openFrame, Entity targetStop,
            TransitMode targetMode, string targetLineId, int targetStationIndex,
            int targetOccurrence, double? distanceMeters, uint walkFrames)
        {
            OldVehicle = oldVehicle;
            OpenFrame = openFrame;
            TargetStop = targetStop; TargetMode = targetMode; TargetLineId = targetLineId;
            TargetStationIndex = targetStationIndex; TargetOccurrence = targetOccurrence;
            DistanceMeters = distanceMeters;
            WalkFrames = walkFrames;
        }
    }

    internal readonly struct TransferRecord
    {
        internal readonly TransitMode FromMode;
        internal readonly string FromLineId;
        internal readonly int FromStationIndex;
        internal readonly StopPosition FromPosition;
        internal readonly TransitMode ToMode;
        internal readonly string ToLineId;
        internal readonly int ToStationIndex;
        internal readonly StopPosition ToPosition;
        internal readonly TimeBucketKey Bucket;
        internal readonly uint AlightFrame;
        internal readonly uint BoardFrame;
        internal readonly double? WalkMeters;
        internal readonly uint? WalkFrames;
        internal readonly PurposeInfo FromPurpose;
        internal readonly PurposeInfo ToPurpose;

        internal TransferRecord(TransitMode fromMode, string fromLineId, int fromStationIndex,
            StopPosition fromPosition,
            TransitMode toMode, string toLineId, int toStationIndex,
            StopPosition toPosition,
            TimeBucketKey bucket, uint alightFrame, uint boardFrame,
            double? walkMeters, uint? walkFrames, PurposeInfo fromPurpose, PurposeInfo toPurpose)
        {
            FromMode = fromMode; FromLineId = fromLineId; FromStationIndex = fromStationIndex;
            FromPosition = fromPosition;
            ToMode = toMode; ToLineId = toLineId; ToStationIndex = toStationIndex;
            ToPosition = toPosition;
            Bucket = bucket; AlightFrame = alightFrame; BoardFrame = boardFrame;
            WalkMeters = walkMeters; WalkFrames = walkFrames;
            FromPurpose = fromPurpose; ToPurpose = toPurpose;
        }
    }

    internal readonly struct ActiveTrip
    {
        internal readonly Entity Passenger;
        internal readonly Entity Vehicle;
        internal readonly TransitMode Mode;
        internal readonly string CurrentLineId;
        internal readonly string FirstLineId;
        internal readonly string LastLineId;
        internal readonly int OriginStationSakIndex;
        internal readonly PurposeInfo CurrentPurpose;
        internal readonly PurposeInfo OriginPurpose;

        internal ActiveTrip(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string currentLineId,
            string firstLineId,
            string lastLineId,
            int originStationSakIndex,
            PurposeInfo currentPurpose,
            PurposeInfo originPurpose)
        {
            Passenger = passenger;
            Vehicle = vehicle;
            Mode = mode;
            CurrentLineId = currentLineId;
            FirstLineId = firstLineId;
            LastLineId = lastLineId;
            OriginStationSakIndex = originStationSakIndex;
            CurrentPurpose = currentPurpose;
            OriginPurpose = originPurpose;
        }
    }

    internal readonly struct PendingTransfer
    {
        internal readonly Entity Passenger;
        internal readonly TransitMode Mode;
        internal readonly int OriginStationSakIndex;
        internal readonly int ActualAlightStationSakIndex;
        internal readonly StopPosition FromPosition;
        internal readonly uint ActualAlightFrame;
        internal readonly TimeBucketKey ActualAlightBucket;
        internal readonly string FirstLineId;
        internal readonly string PreviousLineId;
        internal readonly Entity PreviousVehicle;
        internal readonly uint ExpiresFrame;
        internal readonly int Generation;
        internal readonly uint OpenFrame;

        internal PendingTransfer(Entity passenger, TransitMode mode, int originStationSakIndex,
            int actualAlightStationSakIndex, StopPosition fromPosition,
            uint actualAlightFrame, TimeBucketKey actualAlightBucket,
            string firstLineId, string previousLineId, Entity previousVehicle,
            uint expiresFrame, int generation, uint openFrame)
        {
            Passenger = passenger;
            Mode = mode;
            OriginStationSakIndex = originStationSakIndex;
            ActualAlightStationSakIndex = actualAlightStationSakIndex;
            FromPosition = fromPosition;
            ActualAlightFrame = actualAlightFrame;
            ActualAlightBucket = actualAlightBucket;
            FirstLineId = firstLineId;
            PreviousLineId = previousLineId;
            PreviousVehicle = previousVehicle;
            ExpiresFrame = expiresFrame;
            Generation = generation;
            OpenFrame = openFrame;
        }

    }
    internal readonly struct AlightMark
    {
        internal readonly Entity Vehicle;
        internal readonly uint OpenFrame;

        internal AlightMark(Entity vehicle, uint openFrame)
        {
            Vehicle = vehicle;
            OpenFrame = openFrame;
        }
    }

    internal readonly struct PendingExpiry
    {
        internal readonly uint ExpiresFrame;
        internal readonly Entity Passenger;
        internal readonly int Generation;

        internal PendingExpiry(uint expiresFrame, Entity passenger, int generation)
        {
            ExpiresFrame = expiresFrame;
            Passenger = passenger;
            Generation = generation;
        }
    }
}
