using System;
using System.Collections.Generic;
using System.Linq;

#pragma warning disable CS0649

namespace RapidTransitMod.PassengerFlow
{
    internal readonly struct TimeBucketKey : IEquatable<TimeBucketKey>
    {
        internal readonly int ServiceDayIndex;
        internal readonly int BucketStartMinute;

        internal TimeBucketKey(int serviceDayIndex, int bucketStartMinute)
        {
            ServiceDayIndex = serviceDayIndex;
            BucketStartMinute = bucketStartMinute;
        }

        public bool Equals(TimeBucketKey other)
            => ServiceDayIndex == other.ServiceDayIndex && BucketStartMinute == other.BucketStartMinute;

        public override bool Equals(object obj)
            => obj is TimeBucketKey other && Equals(other);

        public override int GetHashCode()
            => (ServiceDayIndex * 397) ^ BucketStartMinute;
    }

    internal readonly struct StationVolumeKey : IEquatable<StationVolumeKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly int StationSakIndex;
        internal readonly TimeBucketKey Bucket;

        internal StationVolumeKey(TransitMode mode, string lineId, int stationSakIndex, TimeBucketKey bucket)
        {
            Mode = mode;
            LineId = lineId ?? string.Empty;
            StationSakIndex = stationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(StationVolumeKey other)
            => Mode == other.Mode
                && string.Equals(LineId, other.LineId, StringComparison.Ordinal)
                && StationSakIndex == other.StationSakIndex
                && Bucket.Equals(other.Bucket);

        public override bool Equals(object obj)
            => obj is StationVolumeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ (LineId != null ? LineId.GetHashCode() : 0);
                hash = (hash * 397) ^ StationSakIndex;
                hash = (hash * 397) ^ Bucket.GetHashCode();
                return hash;
            }
        }
    }

    internal struct StationVolumeAggregate
    {
        internal int Boardings;
        internal int Alightings;
        internal int ThroughPassengersSum;
        internal int ThroughSampleCount;
        internal int WaitingPassengersSnapshot;
        internal uint LastUpdatedFrame;
    }

    internal readonly struct SectionVolumeKey : IEquatable<SectionVolumeKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly int FromStationSakIndex;
        internal readonly int ToStationSakIndex;
        internal readonly TimeBucketKey Bucket;

        internal SectionVolumeKey(
            TransitMode mode,
            string lineId,
            int fromStationSakIndex,
            int toStationSakIndex,
            TimeBucketKey bucket)
        {
            Mode = mode;
            LineId = lineId ?? string.Empty;
            FromStationSakIndex = fromStationSakIndex;
            ToStationSakIndex = toStationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(SectionVolumeKey other)
            => Mode == other.Mode
                && string.Equals(LineId, other.LineId, StringComparison.Ordinal)
                && FromStationSakIndex == other.FromStationSakIndex
                && ToStationSakIndex == other.ToStationSakIndex
                && Bucket.Equals(other.Bucket);

        public override bool Equals(object obj)
            => obj is SectionVolumeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ (LineId != null ? LineId.GetHashCode() : 0);
                hash = (hash * 397) ^ FromStationSakIndex;
                hash = (hash * 397) ^ ToStationSakIndex;
                hash = (hash * 397) ^ Bucket.GetHashCode();
                return hash;
            }
        }
    }

    internal struct SectionVolumeAggregate
    {
        internal int LoadPassengersSum;
        internal int SampleCount;
        internal uint LastUpdatedFrame;
    }

    internal readonly struct OdFlowKey : IEquatable<OdFlowKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string FirstLineId;
        internal readonly string LastLineId;
        internal readonly int OriginStationSakIndex;
        internal readonly int DestinationStationSakIndex;
        internal readonly TimeBucketKey Bucket;

        internal OdFlowKey(
            TransitMode mode,
            string firstLineId,
            string lastLineId,
            int originStationSakIndex,
            int destinationStationSakIndex,
            TimeBucketKey bucket)
        {
            Mode = mode;
            FirstLineId = firstLineId ?? string.Empty;
            LastLineId = lastLineId ?? string.Empty;
            OriginStationSakIndex = originStationSakIndex;
            DestinationStationSakIndex = destinationStationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(OdFlowKey other)
            => Mode == other.Mode
                && string.Equals(FirstLineId, other.FirstLineId, StringComparison.Ordinal)
                && string.Equals(LastLineId, other.LastLineId, StringComparison.Ordinal)
                && OriginStationSakIndex == other.OriginStationSakIndex
                && DestinationStationSakIndex == other.DestinationStationSakIndex
                && Bucket.Equals(other.Bucket);

        public override bool Equals(object obj)
            => obj is OdFlowKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ (FirstLineId != null ? FirstLineId.GetHashCode() : 0);
                hash = (hash * 397) ^ (LastLineId != null ? LastLineId.GetHashCode() : 0);
                hash = (hash * 397) ^ OriginStationSakIndex;
                hash = (hash * 397) ^ DestinationStationSakIndex;
                hash = (hash * 397) ^ Bucket.GetHashCode();
                return hash;
            }
        }
    }

    internal struct OdFlowAggregate
    {
        internal int CompletedCount;
        internal uint LastUpdatedFrame;
    }

    internal readonly struct WarningKey : IEquatable<WarningKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string Code;
        internal readonly string LineId;
        internal readonly int StationSakIndex;
        internal readonly TimeBucketKey Bucket;

        internal WarningKey(TransitMode mode, string code, string lineId, int stationSakIndex, TimeBucketKey bucket)
        {
            Mode = mode;
            Code = code ?? string.Empty;
            LineId = lineId ?? string.Empty;
            StationSakIndex = stationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(WarningKey other)
            => Mode == other.Mode
                && string.Equals(Code, other.Code, StringComparison.Ordinal)
                && string.Equals(LineId, other.LineId, StringComparison.Ordinal)
                && StationSakIndex == other.StationSakIndex
                && Bucket.Equals(other.Bucket);

        public override bool Equals(object obj)
            => obj is WarningKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Mode;
                hash = (hash * 397) ^ (Code != null ? Code.GetHashCode() : 0);
                hash = (hash * 397) ^ (LineId != null ? LineId.GetHashCode() : 0);
                hash = (hash * 397) ^ StationSakIndex;
                hash = (hash * 397) ^ Bucket.GetHashCode();
                return hash;
            }
        }
    }

    internal struct WarningAggregate
    {
        internal int Count;
        internal uint LastFrame;
    }

    internal sealed class Aggregates
    {
        internal const string WarningAnchorMissing = "anchorMissing";
        internal const string WarningSectionAnchorMissing = "sectionAnchorMissing";
        internal const string WarningSectionTopologyMissing = "sectionTopologyMissing";
        internal const string WarningSectionPassAnchorMissing = "sectionPassAnchorMissing";
        internal const string WarningOriginBaselineMissing = "originBaselineMissing";
        internal const string WarningPassengerBufferMissing = "passengerBufferMissing";
        internal const string WarningLayoutMissing = "layoutMissing";
        internal const string WarningUnsupportedMode = "unsupportedMode";
        internal const string WarningStalePendingSample = "stalePendingSample";
        internal const string WarningUnknownOriginAlighting = "unknownOriginAlighting";
        internal const string WarningTransferWindowExpired = "transferWindowExpired";
        internal const string WarningTransferBoardStationMismatch = "transferBoardStationMismatch";
        internal const string WarningTransferBoardLineMismatch = "transferBoardLineMismatch";
        internal const string WarningProvisionalTransferCancelled = "provisionalTransferCancelled";
        internal const string WarningProvisionalTransferLost = "provisionalTransferLost";
        internal const string WarningPendingTransferOverflow = "pendingTransferOverflow";

        private readonly Dictionary<StationVolumeKey, StationVolumeAggregate> m_StationVolumes =
            new Dictionary<StationVolumeKey, StationVolumeAggregate>();
        private readonly Dictionary<SectionVolumeKey, SectionVolumeAggregate> m_SectionVolumes =
            new Dictionary<SectionVolumeKey, SectionVolumeAggregate>();
        private readonly Dictionary<OdFlowKey, OdFlowAggregate> m_OdFlows =
            new Dictionary<OdFlowKey, OdFlowAggregate>();
        private readonly Dictionary<WarningKey, WarningAggregate> m_Warnings =
            new Dictionary<WarningKey, WarningAggregate>();

        internal int StationVolumeCount => m_StationVolumes.Count;
        internal int SectionVolumeCount => m_SectionVolumes.Count;
        internal int OdFlowCount => m_OdFlows.Count;
        internal int WarningCount => m_Warnings.Count;

        internal void Clear()
        {
            m_StationVolumes.Clear();
            m_SectionVolumes.Clear();
            m_OdFlows.Clear();
            m_Warnings.Clear();
        }

        internal PassengerFlowPersistedStationVolume[] ExportStationVolumes()
        {
            return m_StationVolumes
                .Select(pair => new PassengerFlowPersistedStationVolume
                {
                    mode = TransitModeCodec.Format(pair.Key.Mode),
                    lineId = pair.Key.LineId,
                    stationSakIndex = pair.Key.StationSakIndex,
                    serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    boardings = pair.Value.Boardings,
                    alightings = pair.Value.Alightings,
                    throughPassengersSum = pair.Value.ThroughPassengersSum,
                    throughSampleCount = pair.Value.ThroughSampleCount,
                    waitingPassengersSnapshot = pair.Value.WaitingPassengersSnapshot,
                    lastUpdatedFrame = pair.Value.LastUpdatedFrame
                })
                .ToArray();
        }

        internal PassengerFlowPersistedSectionVolume[] ExportSectionVolumes()
        {
            return m_SectionVolumes
                .Select(pair => new PassengerFlowPersistedSectionVolume
                {
                    mode = TransitModeCodec.Format(pair.Key.Mode),
                    lineId = pair.Key.LineId,
                    fromStationSakIndex = pair.Key.FromStationSakIndex,
                    toStationSakIndex = pair.Key.ToStationSakIndex,
                    serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    loadPassengersSum = pair.Value.LoadPassengersSum,
                    sampleCount = pair.Value.SampleCount,
                    lastUpdatedFrame = pair.Value.LastUpdatedFrame
                })
                .ToArray();
        }

        internal PassengerFlowPersistedOdFlow[] ExportOdFlows()
        {
            return m_OdFlows
                .Select(pair => new PassengerFlowPersistedOdFlow
                {
                    mode = TransitModeCodec.Format(pair.Key.Mode),
                    firstLineId = pair.Key.FirstLineId,
                    lastLineId = pair.Key.LastLineId,
                    originStationSakIndex = pair.Key.OriginStationSakIndex,
                    destinationStationSakIndex = pair.Key.DestinationStationSakIndex,
                    serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    completedCount = pair.Value.CompletedCount,
                    lastUpdatedFrame = pair.Value.LastUpdatedFrame
                })
                .ToArray();
        }

        internal PassengerFlowPersistedWarning[] ExportWarnings()
        {
            return m_Warnings
                .Select(pair => new PassengerFlowPersistedWarning
                {
                    mode = TransitModeCodec.Format(pair.Key.Mode),
                    code = pair.Key.Code,
                    lineId = pair.Key.LineId,
                    stationSakIndex = pair.Key.StationSakIndex,
                    serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    count = pair.Value.Count,
                    lastFrame = pair.Value.LastFrame
                })
                .ToArray();
        }

        internal void Restore(
            PassengerFlowPersistedStationVolume[] stationVolumes,
            PassengerFlowPersistedSectionVolume[] sectionVolumes,
            PassengerFlowPersistedOdFlow[] odFlows,
            PassengerFlowPersistedWarning[] warnings)
        {
            m_StationVolumes.Clear();
            m_SectionVolumes.Clear();
            m_OdFlows.Clear();
            m_Warnings.Clear();

            if (stationVolumes != null)
            {
                for (int i = 0; i < stationVolumes.Length; i++)
                {
                    PassengerFlowPersistedStationVolume row = stationVolumes[i];
                    if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                        continue;

                    m_StationVolumes[new StationVolumeKey(
                        mode,
                        row.lineId,
                        row.stationSakIndex,
                        new TimeBucketKey(row.serviceDayIndex, row.bucketStartMinute))] =
                        new StationVolumeAggregate
                        {
                            Boardings = row.boardings,
                            Alightings = row.alightings,
                            ThroughPassengersSum = row.throughPassengersSum,
                            ThroughSampleCount = row.throughSampleCount,
                            WaitingPassengersSnapshot = row.waitingPassengersSnapshot,
                            LastUpdatedFrame = row.lastUpdatedFrame
                        };
                }
            }

            if (sectionVolumes != null)
            {
                for (int i = 0; i < sectionVolumes.Length; i++)
                {
                    PassengerFlowPersistedSectionVolume row = sectionVolumes[i];
                    if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                        continue;

                    m_SectionVolumes[new SectionVolumeKey(
                        mode,
                        row.lineId,
                        row.fromStationSakIndex,
                        row.toStationSakIndex,
                        new TimeBucketKey(row.serviceDayIndex, row.bucketStartMinute))] =
                        new SectionVolumeAggregate
                        {
                            LoadPassengersSum = row.loadPassengersSum,
                            SampleCount = row.sampleCount,
                            LastUpdatedFrame = row.lastUpdatedFrame
                        };
                }
            }

            if (odFlows != null)
            {
                for (int i = 0; i < odFlows.Length; i++)
                {
                    PassengerFlowPersistedOdFlow row = odFlows[i];
                    if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                        continue;

                    m_OdFlows[new OdFlowKey(
                        mode,
                        row.firstLineId,
                        row.lastLineId,
                        row.originStationSakIndex,
                        row.destinationStationSakIndex,
                        new TimeBucketKey(row.serviceDayIndex, row.bucketStartMinute))] =
                        new OdFlowAggregate
                        {
                            CompletedCount = row.completedCount,
                            LastUpdatedFrame = row.lastUpdatedFrame
                        };
                }
            }

            if (warnings == null)
                return;

            for (int i = 0; i < warnings.Length; i++)
            {
                PassengerFlowPersistedWarning row = warnings[i];
                if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                    continue;

                m_Warnings[new WarningKey(
                    mode,
                    row.code,
                    row.lineId,
                    row.stationSakIndex,
                    new TimeBucketKey(row.serviceDayIndex, row.bucketStartMinute))] =
                    new WarningAggregate
                    {
                        Count = row.count,
                        LastFrame = row.lastFrame
                    };
            }
        }

        internal void RecordBoarding(
            TransitMode mode,
            string lineId,
            int stationSakIndex,
            TimeBucketKey bucket,
            uint frame)
        {
            StationVolumeKey key = new StationVolumeKey(mode, lineId, stationSakIndex, bucket);
            m_StationVolumes.TryGetValue(key, out StationVolumeAggregate aggregate);
            aggregate.Boardings++;
            aggregate.LastUpdatedFrame = frame;
            m_StationVolumes[key] = aggregate;
        }

        internal void RecordAlighting(
            TransitMode mode,
            string lineId,
            int stationSakIndex,
            TimeBucketKey bucket,
            uint frame)
        {
            StationVolumeKey key = new StationVolumeKey(mode, lineId, stationSakIndex, bucket);
            m_StationVolumes.TryGetValue(key, out StationVolumeAggregate aggregate);
            aggregate.Alightings++;
            aggregate.LastUpdatedFrame = frame;
            m_StationVolumes[key] = aggregate;
        }

        internal void RecordSectionLoad(
            TransitMode mode,
            string lineId,
            int fromStationSakIndex,
            int toStationSakIndex,
            int passengerCount,
            TimeBucketKey bucket,
            uint frame)
        {
            SectionVolumeKey key = new SectionVolumeKey(mode, lineId, fromStationSakIndex, toStationSakIndex, bucket);
            m_SectionVolumes.TryGetValue(key, out SectionVolumeAggregate aggregate);
            aggregate.LoadPassengersSum += passengerCount;
            aggregate.SampleCount++;
            aggregate.LastUpdatedFrame = frame;
            m_SectionVolumes[key] = aggregate;
        }

        internal void RecordWarning(
            TransitMode mode,
            string code,
            string lineId,
            int stationSakIndex,
            TimeBucketKey bucket,
            uint frame)
        {
            WarningKey key = new WarningKey(mode, code, lineId, stationSakIndex, bucket);
            m_Warnings.TryGetValue(key, out WarningAggregate aggregate);
            aggregate.Count++;
            aggregate.LastFrame = frame;
            m_Warnings[key] = aggregate;
        }

        internal void RecordCompletedOd(
            TransitMode mode,
            string firstLineId,
            string lastLineId,
            int originStationSakIndex,
            int destinationStationSakIndex,
            TimeBucketKey bucket,
            uint frame)
        {
            OdFlowKey key = new OdFlowKey(
                mode,
                firstLineId,
                lastLineId,
                originStationSakIndex,
                destinationStationSakIndex,
                bucket);
            m_OdFlows.TryGetValue(key, out OdFlowAggregate aggregate);
            aggregate.CompletedCount++;
            aggregate.LastUpdatedFrame = frame;
            m_OdFlows[key] = aggregate;
        }

        internal void TrimBefore(int minServiceDayIndex, int minBucketStartMinute)
        {
            bool hasAny = m_StationVolumes.Count != 0
                || m_SectionVolumes.Count != 0
                || m_OdFlows.Count != 0
                || m_Warnings.Count != 0;
            if (!hasAny)
                return;

            List<StationVolumeKey> removeStationKeys = null;
            foreach (StationVolumeKey key in m_StationVolumes.Keys)
            {
                if (IsBefore(key.Bucket, minServiceDayIndex, minBucketStartMinute))
                {
                    if (removeStationKeys == null)
                        removeStationKeys = new List<StationVolumeKey>();
                    removeStationKeys.Add(key);
                }
            }

            if (removeStationKeys != null)
            {
                for (int i = 0; i < removeStationKeys.Count; i++)
                    m_StationVolumes.Remove(removeStationKeys[i]);
            }

            List<SectionVolumeKey> removeSectionKeys = null;
            foreach (SectionVolumeKey key in m_SectionVolumes.Keys)
            {
                if (IsBefore(key.Bucket, minServiceDayIndex, minBucketStartMinute))
                {
                    if (removeSectionKeys == null)
                        removeSectionKeys = new List<SectionVolumeKey>();
                    removeSectionKeys.Add(key);
                }
            }

            if (removeSectionKeys != null)
            {
                for (int i = 0; i < removeSectionKeys.Count; i++)
                    m_SectionVolumes.Remove(removeSectionKeys[i]);
            }

            List<OdFlowKey> removeOdKeys = null;
            foreach (OdFlowKey key in m_OdFlows.Keys)
            {
                if (IsBefore(key.Bucket, minServiceDayIndex, minBucketStartMinute))
                {
                    if (removeOdKeys == null)
                        removeOdKeys = new List<OdFlowKey>();
                    removeOdKeys.Add(key);
                }
            }

            if (removeOdKeys != null)
            {
                for (int i = 0; i < removeOdKeys.Count; i++)
                    m_OdFlows.Remove(removeOdKeys[i]);
            }

            List<WarningKey> removeKeys = null;
            foreach (WarningKey key in m_Warnings.Keys)
            {
                if (IsBefore(key.Bucket, minServiceDayIndex, minBucketStartMinute))
                {
                    if (removeKeys == null)
                        removeKeys = new List<WarningKey>();
                    removeKeys.Add(key);
                }
            }

            if (removeKeys == null)
                return;

            for (int i = 0; i < removeKeys.Count; i++)
                m_Warnings.Remove(removeKeys[i]);
        }

        private static bool IsBefore(TimeBucketKey bucket, int minServiceDayIndex, int minBucketStartMinute)
        {
            return bucket.ServiceDayIndex < minServiceDayIndex
                || (bucket.ServiceDayIndex == minServiceDayIndex
                    && bucket.BucketStartMinute < minBucketStartMinute);
        }

        internal SnapshotRows BuildSnapshotRows(ModeScope scope, Anchors anchors)
        {
            SnapshotRows rows = SnapshotRows.Empty(scope);
            rows.StationVolumes = m_StationVolumes
                .Where(pair => pair.Key.Mode == scope.Mode)
                .Select(pair =>
                {
                    string stationId = string.Empty;
                    if (anchors != null)
                        anchors.TryGetSak(pair.Key.StationSakIndex, out stationId);

                    int throughPassengers = pair.Value.ThroughSampleCount > 0
                        ? pair.Value.ThroughPassengersSum / pair.Value.ThroughSampleCount
                        : 0;

                    return new StationVolumeDto
                    {
                        mode = scope.Token,
                        lineId = pair.Key.LineId,
                        stationId = stationId,
                        stationName = stationId,
                        boardings = pair.Value.Boardings,
                        alightings = pair.Value.Alightings,
                        waitingPassengers = pair.Value.WaitingPassengersSnapshot,
                        throughPassengers = throughPassengers,
                        serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                        bucketStartMinute = pair.Key.Bucket.BucketStartMinute
                    };
                })
                .ToArray();
            rows.SectionVolumes = m_SectionVolumes
                .Where(pair => pair.Key.Mode == scope.Mode)
                .Select(pair =>
                {
                    string fromStationId = string.Empty;
                    string toStationId = string.Empty;
                    if (anchors != null)
                    {
                        anchors.TryGetSak(pair.Key.FromStationSakIndex, out fromStationId);
                        anchors.TryGetSak(pair.Key.ToStationSakIndex, out toStationId);
                    }

                    return new SectionVolumeDto
                    {
                        mode = scope.Token,
                        lineId = pair.Key.LineId,
                        fromStationId = fromStationId,
                        toStationId = toStationId,
                        averageLoadPassengers = pair.Value.SampleCount > 0
                            ? pair.Value.LoadPassengersSum / pair.Value.SampleCount
                            : 0,
                        sampleCount = pair.Value.SampleCount,
                        serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                        bucketStartMinute = pair.Key.Bucket.BucketStartMinute
                    };
                })
                .ToArray();
            rows.OdFlows = m_OdFlows
                .Where(pair => pair.Key.Mode == scope.Mode)
                .Select(pair =>
                {
                    string originStationId = string.Empty;
                    string destinationStationId = string.Empty;
                    if (anchors != null)
                    {
                        anchors.TryGetSak(pair.Key.OriginStationSakIndex, out originStationId);
                        anchors.TryGetSak(pair.Key.DestinationStationSakIndex, out destinationStationId);
                    }

                    return new OdFlowDto
                    {
                        mode = scope.Token,
                        lineId = pair.Key.FirstLineId,
                        firstLineId = pair.Key.FirstLineId,
                        lastLineId = pair.Key.LastLineId,
                        originStationId = originStationId,
                        destinationStationId = destinationStationId,
                        completedCount = pair.Value.CompletedCount,
                        serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                        bucketStartMinute = pair.Key.Bucket.BucketStartMinute
                    };
                })
                .ToArray();
            rows.Warnings = m_Warnings
                .Where(pair => pair.Key.Mode == scope.Mode)
                .Select(pair =>
                {
                    string stationId = string.Empty;
                    if (pair.Key.StationSakIndex >= 0 && anchors != null)
                        anchors.TryGetSak(pair.Key.StationSakIndex, out stationId);

                    return new WarningDto
                    {
                        mode = scope.Token,
                        code = pair.Key.Code,
                        lineId = pair.Key.LineId,
                        stationId = stationId,
                        count = pair.Value.Count,
                        lastFrame = pair.Value.LastFrame,
                        serviceDayIndex = pair.Key.Bucket.ServiceDayIndex,
                        bucketStartMinute = pair.Key.Bucket.BucketStartMinute
                    };
                })
                .ToArray();
            return rows;
        }
    }

    internal sealed class SnapshotRows
    {
        internal StationVolumeDto[] StationVolumes;
        internal SectionVolumeDto[] SectionVolumes;
        internal OdFlowDto[] OdFlows;
        internal WarningDto[] Warnings;

        internal static SnapshotRows Empty(ModeScope scope)
        {
            return new SnapshotRows
            {
                StationVolumes = Array.Empty<StationVolumeDto>(),
                SectionVolumes = Array.Empty<SectionVolumeDto>(),
                OdFlows = Array.Empty<OdFlowDto>(),
                Warnings = Array.Empty<WarningDto>()
            };
        }
    }
}

#pragma warning restore CS0649
