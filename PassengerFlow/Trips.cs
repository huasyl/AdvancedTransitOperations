using System.Collections.Generic;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal sealed class Trips
    {
        private readonly Dictionary<Entity, ActiveTrip> m_ActiveTrips = new Dictionary<Entity, ActiveTrip>();
        private readonly Dictionary<Entity, PendingTransfer> m_Pending = new Dictionary<Entity, PendingTransfer>();
        private readonly Queue<PendingExpiry> m_ExpiryQueue = new Queue<PendingExpiry>();
        private int m_NextGeneration;

        internal int ActiveTripCount => m_ActiveTrips.Count;
        internal int PendingTransferCount => m_Pending.Count;

        internal void Clear()
        {
            m_ActiveTrips.Clear();
            m_Pending.Clear();
            m_ExpiryQueue.Clear();
            m_NextGeneration = 0;
        }

        internal bool HasActiveTrip(Entity passenger)
        {
            return passenger != Entity.Null && m_ActiveTrips.ContainsKey(passenger);
        }

        internal void OnBoard(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string lineId,
            int originStationSakIndex,
            uint frame,
            TimeBucketKey bucket,
            Aggregates aggregates)
        {
            if (passenger == Entity.Null)
                return;

            if (TryMatchBoard(passenger, vehicle, mode, lineId, originStationSakIndex, frame, bucket, aggregates))
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
                frame,
                0);
        }

        internal bool TryCreatePending(
            Entity passenger,
            OpenStop openStop,
            uint actualAlightFrame,
            TimeBucketKey actualAlightBucket,
            uint expiresFrame)
        {
            if (passenger == Entity.Null)
                return false;

            if (m_Pending.ContainsKey(passenger))
                return false;

            if (!m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip))
                return false;

            int generation = ++m_NextGeneration;
            PendingTransfer pending = new PendingTransfer(
                passenger,
                trip.Mode,
                trip.OriginStationSakIndex,
                openStop.OpenStationSakIndex,
                actualAlightFrame,
                actualAlightBucket,
                trip.FirstLineId,
                trip.CurrentLineId,
                trip.Vehicle,
                expiresFrame,
                PendingTransferState.Provisional,
                generation,
                actualAlightFrame);
            m_Pending[passenger] = pending;
            m_ExpiryQueue.Enqueue(new PendingExpiry(expiresFrame, passenger, generation));
            return true;
        }

        internal bool TryMatchBoard(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string lineId,
            int boardStationSakIndex,
            uint frame,
            TimeBucketKey bucket,
            Aggregates aggregates)
        {
            if (passenger == Entity.Null || !m_Pending.TryGetValue(passenger, out PendingTransfer pending))
                return false;

            if (frame > pending.ExpiresFrame)
            {
                SubmitPending(pending, Aggregates.WarningTransferWindowExpired, aggregates);
                ClosePending(pending);
                return false;
            }

            if (pending.Mode != mode || boardStationSakIndex != pending.ActualAlightStationSakIndex)
            {
                SubmitPending(pending, Aggregates.WarningTransferBoardStationMismatch, aggregates);
                ClosePending(pending);
                return false;
            }

            if (string.Equals(lineId, pending.PreviousLineId, System.StringComparison.Ordinal)
                || vehicle == pending.PreviousVehicle)
            {
                aggregates?.RecordWarning(
                    mode,
                    Aggregates.WarningTransferBoardLineMismatch,
                    lineId,
                    boardStationSakIndex,
                    bucket,
                    frame);
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

            m_ActiveTrips[passenger] = new ActiveTrip(
                passenger,
                vehicle,
                mode,
                lineId,
                trip.FirstLineId,
                lineId,
                trip.OriginStationSakIndex,
                trip.FirstBoardFrame,
                trip.TransferCount + 1);
            RemovePending(pending);
            return true;
        }

        internal bool TryCancelReturn(
            Entity passenger,
            Entity vehicle,
            uint frame,
            TimeBucketKey bucket,
            Aggregates aggregates)
        {
            if (passenger == Entity.Null
                || !m_Pending.TryGetValue(passenger, out PendingTransfer pending)
                || pending.PreviousVehicle != vehicle)
            {
                return false;
            }

            aggregates?.RecordWarning(
                pending.Mode,
                Aggregates.WarningProvisionalTransferCancelled,
                pending.PreviousLineId,
                pending.ActualAlightStationSakIndex,
                bucket,
                frame);
            RemovePending(pending);
            return true;
        }

        internal void CleanupExpired(uint frame, Aggregates aggregates)
        {
            while (m_ExpiryQueue.Count > 0 && m_ExpiryQueue.Peek().ExpiresFrame <= frame)
            {
                PendingExpiry expiry = m_ExpiryQueue.Dequeue();
                if (!m_Pending.TryGetValue(expiry.Passenger, out PendingTransfer pending)
                    || pending.Generation != expiry.Generation)
                {
                    continue;
                }

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

        internal void OnAlight(
            Entity passenger,
            TransitMode mode,
            string lineId,
            int destinationStationSakIndex,
            TimeBucketKey actualAlightBucket,
            uint frame,
            Aggregates aggregates)
        {
            if (aggregates == null)
                return;

            if (passenger != Entity.Null && m_Pending.ContainsKey(passenger))
                return;

            if (passenger == Entity.Null || !m_ActiveTrips.TryGetValue(passenger, out ActiveTrip trip))
            {
                aggregates.RecordWarning(
                    mode,
                    Aggregates.WarningUnknownOriginAlighting,
                    lineId,
                    destinationStationSakIndex,
                    actualAlightBucket,
                    frame);
                return;
            }

            aggregates.RecordCompletedOd(
                trip.Mode,
                trip.FirstLineId,
                trip.LastLineId,
                trip.OriginStationSakIndex,
                destinationStationSakIndex,
                actualAlightBucket,
                frame);
            m_ActiveTrips.Remove(passenger);
        }

        private void SubmitPending(PendingTransfer pending, string warningCode, Aggregates aggregates)
        {
            if (aggregates == null)
                return;
            aggregates.RecordCompletedOd(
                pending.Mode,
                pending.FirstLineId,
                pending.PreviousLineId,
                pending.OriginStationSakIndex,
                pending.ActualAlightStationSakIndex,
                pending.ActualAlightBucket,
                pending.ActualAlightFrame);
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
        }

        private void ClosePending(PendingTransfer pending)
        {
            m_Pending.Remove(pending.Passenger);
            m_ActiveTrips.Remove(pending.Passenger);
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
        internal readonly uint FirstBoardFrame;
        internal readonly int TransferCount;

        internal ActiveTrip(
            Entity passenger,
            Entity vehicle,
            TransitMode mode,
            string currentLineId,
            string firstLineId,
            string lastLineId,
            int originStationSakIndex,
            uint firstBoardFrame,
            int transferCount)
        {
            Passenger = passenger;
            Vehicle = vehicle;
            Mode = mode;
            CurrentLineId = currentLineId ?? string.Empty;
            FirstLineId = firstLineId ?? string.Empty;
            LastLineId = lastLineId ?? string.Empty;
            OriginStationSakIndex = originStationSakIndex;
            FirstBoardFrame = firstBoardFrame;
            TransferCount = transferCount;
        }
    }

    internal enum PendingTransferState
    {
        Provisional,
        Confirmed
    }

    internal readonly struct PendingTransfer
    {
        internal readonly Entity Passenger;
        internal readonly TransitMode Mode;
        internal readonly int OriginStationSakIndex;
        internal readonly int ActualAlightStationSakIndex;
        internal readonly uint ActualAlightFrame;
        internal readonly TimeBucketKey ActualAlightBucket;
        internal readonly string FirstLineId;
        internal readonly string PreviousLineId;
        internal readonly Entity PreviousVehicle;
        internal readonly uint ExpiresFrame;
        internal readonly PendingTransferState State;
        internal readonly int Generation;
        internal readonly uint LastSeenOnOriginalVehicleFrame;

        internal PendingTransfer(
            Entity passenger,
            TransitMode mode,
            int originStationSakIndex,
            int actualAlightStationSakIndex,
            uint actualAlightFrame,
            TimeBucketKey actualAlightBucket,
            string firstLineId,
            string previousLineId,
            Entity previousVehicle,
            uint expiresFrame,
            PendingTransferState state,
            int generation,
            uint lastSeenOnOriginalVehicleFrame)
        {
            Passenger = passenger;
            Mode = mode;
            OriginStationSakIndex = originStationSakIndex;
            ActualAlightStationSakIndex = actualAlightStationSakIndex;
            ActualAlightFrame = actualAlightFrame;
            ActualAlightBucket = actualAlightBucket;
            FirstLineId = firstLineId ?? string.Empty;
            PreviousLineId = previousLineId ?? string.Empty;
            PreviousVehicle = previousVehicle;
            ExpiresFrame = expiresFrame;
            State = state;
            Generation = generation;
            LastSeenOnOriginalVehicleFrame = lastSeenOnOriginalVehicleFrame;
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
