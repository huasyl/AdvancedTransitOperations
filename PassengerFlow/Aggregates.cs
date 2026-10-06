using System;
using System.Collections.Generic;
using System.Linq;

#pragma warning disable CS0649

namespace RapidTransitMod.PassengerFlow
{
    internal readonly struct TimeBucketKey : IEquatable<TimeBucketKey>
    {
        internal readonly int DayIndex;
        internal readonly int BucketStartMinute;

        internal TimeBucketKey(int dayIndex, int bucketStartMinute)
        {
            DayIndex = dayIndex;
            BucketStartMinute = bucketStartMinute;
        }

        public bool Equals(TimeBucketKey other)
            => DayIndex == other.DayIndex && BucketStartMinute == other.BucketStartMinute;

        public override bool Equals(object obj)
            => obj is TimeBucketKey other && Equals(other);

        public override int GetHashCode()
            => (DayIndex * 397) ^ BucketStartMinute;
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
        internal Dictionary<PurposeInfo, PurposeCount> Purposes;
    }

    internal readonly struct SectionVolumeKey : IEquatable<SectionVolumeKey>
    {
        internal readonly SectionKind Kind;
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
            TimeBucketKey bucket, SectionKind kind)
        {
            Kind = kind;
            Mode = mode;
            LineId = lineId ?? string.Empty;
            FromStationSakIndex = fromStationSakIndex;
            ToStationSakIndex = toStationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(SectionVolumeKey other)
            => Kind == other.Kind && Mode == other.Mode
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
                hash = (hash * 397) ^ (int)Kind;
                hash = (hash * 397) ^ Bucket.GetHashCode();
                return hash;
            }
        }
    }

    internal struct SectionVolumeAggregate
    {
        internal long LoadPassengersSum;
        internal long CapacitySum;
        internal long RatedLoadPassengersSum;
        internal int SampleCount;
        internal int CapacitySampleCount;
        internal Dictionary<PurposeInfo, PurposeCount> Purposes;
    }

    internal readonly struct TransferKey : IEquatable<TransferKey>
    {
        internal readonly TransitMode FromMode;
        internal readonly string FromLineId;
        internal readonly int FromStationSakIndex;
        internal readonly int FromStationOccurrence;
        internal readonly TransitMode ToMode;
        internal readonly string ToLineId;
        internal readonly int ToStationSakIndex;
        internal readonly int ToStationOccurrence;
        internal readonly TimeBucketKey Bucket;

        internal TransferKey(TransitMode fromMode, string fromLineId, int fromStationSakIndex,
            int fromStationOccurrence, TransitMode toMode, string toLineId,
            int toStationSakIndex, int toStationOccurrence, TimeBucketKey bucket)
        {
            FromMode = fromMode;
            FromLineId = fromLineId;
            FromStationSakIndex = fromStationSakIndex;
            FromStationOccurrence = fromStationOccurrence;
            ToMode = toMode;
            ToLineId = toLineId;
            ToStationSakIndex = toStationSakIndex;
            ToStationOccurrence = toStationOccurrence;
            Bucket = bucket;
        }

        public bool Equals(TransferKey other)
            => FromMode == other.FromMode && ToMode == other.ToMode
                && FromStationSakIndex == other.FromStationSakIndex && ToStationSakIndex == other.ToStationSakIndex
                && FromStationOccurrence == other.FromStationOccurrence
                && ToStationOccurrence == other.ToStationOccurrence
                && Bucket.Equals(other.Bucket)
                && string.Equals(FromLineId, other.FromLineId, StringComparison.Ordinal)
                && string.Equals(ToLineId, other.ToLineId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is TransferKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)FromMode;
                hash = (hash * 397) ^ FromLineId.GetHashCode();
                hash = (hash * 397) ^ FromStationSakIndex;
                hash = (hash * 397) ^ FromStationOccurrence;
                hash = (hash * 397) ^ (int)ToMode;
                hash = (hash * 397) ^ ToLineId.GetHashCode();
                hash = (hash * 397) ^ ToStationSakIndex;
                hash = (hash * 397) ^ ToStationOccurrence;
                return (hash * 397) ^ Bucket.GetHashCode();
            }
        }
    }

    internal struct TransferAggregate
    {
        internal int CompletedCount;
        internal double WalkPathMetersSum;
        internal int WalkPathSampleCount;
        internal long WalkFramesSum;
        internal int WalkTimeSampleCount;
        internal Dictionary<PurposePair, PurposeCount> Purposes;
    }

    internal readonly struct PurposePair : IEquatable<PurposePair>
    {
        internal readonly PurposeInfo From;
        internal readonly PurposeInfo To;
        internal PurposePair(PurposeInfo from, PurposeInfo to) { From = from; To = to; }
        public bool Equals(PurposePair other) => From.Equals(other.From) && To.Equals(other.To);
        public override bool Equals(object obj) => obj is PurposePair other && Equals(other);
        public override int GetHashCode() => (From.GetHashCode() * 397) ^ To.GetHashCode();
    }

    internal struct PurposeCount
    {
        internal int Boardings;
        internal int Alightings;
        internal long LoadPassengersSum;
        internal int CompletedCount;
    }

    internal readonly struct SampleGroupKey : IEquatable<SampleGroupKey>
    {
        private readonly TransferKey m_Key;
        internal TransitMode FromMode => m_Key.FromMode;
        internal string FromLineId => m_Key.FromLineId;
        internal int FromStationIndex => m_Key.FromStationSakIndex;
        internal int FromStationOccurrence => m_Key.FromStationOccurrence;
        internal TransitMode ToMode => m_Key.ToMode;
        internal string ToLineId => m_Key.ToLineId;
        internal int ToStationIndex => m_Key.ToStationSakIndex;
        internal int ToStationOccurrence => m_Key.ToStationOccurrence;
        internal TimeBucketKey Bucket => m_Key.Bucket;

        internal SampleGroupKey(TransitMode fromMode, string fromLineId, int fromStationIndex,
            int fromStationOccurrence, TransitMode toMode, string toLineId,
            int toStationIndex, int toStationOccurrence, TimeBucketKey bucket)
        {
            m_Key = new TransferKey(fromMode, fromLineId, fromStationIndex,
                fromStationOccurrence, toMode, toLineId, toStationIndex,
                toStationOccurrence, bucket);
        }

        public bool Equals(SampleGroupKey other) => m_Key.Equals(other.m_Key);
        public override bool Equals(object obj) => obj is SampleGroupKey other && Equals(other);
        public override int GetHashCode() => m_Key.GetHashCode();
    }
    internal readonly struct StationWaitingKey : IEquatable<StationWaitingKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly int StationOccurrence;
        internal readonly int StationSakIndex;
        internal readonly TimeBucketKey Bucket;

        internal StationWaitingKey(TransitMode mode, string lineId, int stationOccurrence, int stationSakIndex, TimeBucketKey bucket)
        {
            Mode = mode;
            LineId = lineId;
            StationOccurrence = stationOccurrence;
            StationSakIndex = stationSakIndex;
            Bucket = bucket;
        }

        public bool Equals(StationWaitingKey other)
            => Mode == other.Mode && StationOccurrence == other.StationOccurrence && StationSakIndex == other.StationSakIndex
                && Bucket.Equals(other.Bucket) && string.Equals(LineId, other.LineId, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is StationWaitingKey other && Equals(other);
        public override int GetHashCode()
            => (((((int)Mode * 397) ^ LineId.GetHashCode()) * 397 ^ StationOccurrence) * 397 ^ StationSakIndex) * 397 ^ Bucket.GetHashCode();
    }

    internal struct StationWaitingAggregate
    {
        internal int LatestWaitingCount;
        internal int PeakWaitingCount;
        internal long WaitingCountFrames;
        internal double LatestEstimatedWaitFrames;
        internal double EstimatedWaitFrameFrames;
        internal long ObservedFrames;
        internal int SampleCount;
    }

    internal readonly struct LineTimeKey : IEquatable<LineTimeKey>
    {
        internal readonly TransitMode Mode;
        internal readonly string LineId;
        internal readonly TimeBucketKey Bucket;

        internal LineTimeKey(TransitMode mode, string lineId, TimeBucketKey bucket)
        {
            Mode = mode; LineId = lineId; Bucket = bucket;
        }

        public bool Equals(LineTimeKey other)
            => Mode == other.Mode && Bucket.Equals(other.Bucket)
                && string.Equals(LineId, other.LineId, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is LineTimeKey other && Equals(other);
        public override int GetHashCode()
            => (((int)Mode * 397) ^ LineId.GetHashCode()) * 397 ^ Bucket.GetHashCode();
    }

    internal struct LineTimeAggregate
    {
        internal long PassengerFrames;
        internal long ObservedFrames;
        internal long RatedPassengerFrames;
        internal long CapacityFrames;
        internal long CapacityObservedFrames;
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
        internal Dictionary<PurposeInfo, PurposeCount> Purposes;
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

        // 表内只保存关系身份一次；索引持有同一序列引用，不保存第二份累计值。
        private sealed class BucketTable<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
        {
            internal sealed class Series
            {
                internal readonly TKey Key;
                private readonly TValue[] m_Values;
                private readonly int[] m_Absolute;
                private readonly bool[] m_Observed;
                private readonly Dictionary<int, TValue> m_Sparse;
                internal int Count { get; private set; }
                internal int Bindings;

                internal Series(TKey key, bool dense)
                {
                    Key = key;
                    if (dense)
                    {
                        m_Values = new TValue[SamplingSystem.RetainedBucketCapacity];
                        m_Absolute = new int[SamplingSystem.RetainedBucketCapacity];
                        m_Observed = new bool[SamplingSystem.RetainedBucketCapacity];
                    }
                    else m_Sparse = new Dictionary<int, TValue>();
                }

                private static int Slot(int absolute) => absolute % SamplingSystem.RetainedBucketCapacity;

                internal bool TryGet(int absolute, out TValue value)
                {
                    if (m_Sparse != null) return m_Sparse.TryGetValue(absolute, out value);
                    int slot = Slot(absolute);
                    bool found = m_Observed[slot] && m_Absolute[slot] == absolute;
                    value = found ? m_Values[slot] : default;
                    return found;
                }

                internal void Set(int absolute, TValue value)
                {
                    if (m_Sparse != null)
                    {
                        if (!m_Sparse.ContainsKey(absolute)) Count++;
                        m_Sparse[absolute] = value;
                    }
                    else
                    {
                        int slot = Slot(absolute);
                        if (!m_Observed[slot]) Count++;
                        m_Absolute[slot] = absolute;
                        m_Observed[slot] = true;
                        m_Values[slot] = value;
                    }
                }

                internal IEnumerable<KeyValuePair<int, TValue>> Read(int from, int to)
                {
                    if (m_Sparse != null)
                    {
                        foreach (KeyValuePair<int, TValue> pair in m_Sparse)
                            if (pair.Key >= from && pair.Key < to) yield return pair;
                    }
                    else
                    {
                        for (int absolute = from; absolute < to; absolute++)
                            if (TryGet(absolute, out TValue value))
                                yield return new KeyValuePair<int, TValue>(absolute, value);
                    }
                }

                internal IEnumerable<KeyValuePair<int, TValue>> All()
                {
                    if (m_Sparse != null)
                    {
                        foreach (KeyValuePair<int, TValue> pair in m_Sparse) yield return pair;
                    }
                    else
                    {
                        for (int slot = 0; slot < SamplingSystem.RetainedBucketCapacity; slot++)
                            if (m_Observed[slot])
                                yield return new KeyValuePair<int, TValue>(m_Absolute[slot], m_Values[slot]);
                    }
                }

                internal void Trim(int minimum)
                {
                    if (m_Sparse != null)
                    {
                        foreach (int absolute in m_Sparse.Keys.Where(value => value < minimum).ToArray())
                        { m_Sparse.Remove(absolute); Count--; }
                    }
                    else
                    {
                        for (int slot = 0; slot < SamplingSystem.RetainedBucketCapacity; slot++)
                            if (m_Observed[slot] && m_Absolute[slot] < minimum)
                            { m_Observed[slot] = false; m_Values[slot] = default; Count--; }
                    }
                }

                internal bool Remove(int absolute)
                {
                    if (m_Sparse != null)
                    {
                        if (!m_Sparse.Remove(absolute)) return false;
                    }
                    else
                    {
                        int slot = Slot(absolute);
                        if (!m_Observed[slot] || m_Absolute[slot] != absolute) return false;
                        m_Observed[slot] = false; m_Values[slot] = default;
                    }
                    Count--;
                    return true;
                }

                internal void Clear()
                {
                    if (m_Sparse != null) m_Sparse.Clear();
                    else
                    {
                        Array.Clear(m_Values, 0, m_Values.Length);
                        Array.Clear(m_Observed, 0, m_Observed.Length);
                    }
                    Count = 0;
                }
            }

            private readonly bool m_Dense;
            private readonly Func<TKey, TimeBucketKey> m_Bucket;
            private readonly Func<TKey, TimeBucketKey, TKey> m_WithBucket;
            private readonly Func<TKey, (TransitMode Mode, string Line, int Station)[]> m_Endpoints;
            private readonly Dictionary<TKey, Series> m_Relations = new Dictionary<TKey, Series>();
            private readonly Dictionary<TransitMode, HashSet<Series>> m_Modes = new Dictionary<TransitMode, HashSet<Series>>();
            private readonly Dictionary<string, HashSet<Series>> m_Lines = new Dictionary<string, HashSet<Series>>(StringComparer.Ordinal);
            private readonly Dictionary<int, HashSet<Series>> m_Stations = new Dictionary<int, HashSet<Series>>();
            private int m_Minimum = int.MinValue;
            internal int Count { get; private set; }

            internal BucketTable(bool dense, Func<TKey, TimeBucketKey> bucket,
                Func<TKey, TimeBucketKey, TKey> withBucket,
                Func<TKey, (TransitMode Mode, string Line, int Station)[]> endpoints)
            { m_Dense = dense; m_Bucket = bucket; m_WithBucket = withBucket; m_Endpoints = endpoints; }

            internal bool TryGetValue(TKey key, out TValue value)
            {
                if (m_Relations.TryGetValue(m_WithBucket(key, default), out Series series))
                    return series.TryGet(SamplingSystem.AbsoluteBucketIndex(m_Bucket(key)), out value);
                value = default;
                return false;
            }

            internal TValue this[TKey key]
            {
                set
                {
                    int absolute = SamplingSystem.AbsoluteBucketIndex(m_Bucket(key));
                    if (absolute < m_Minimum) return;
                    TKey relation = m_WithBucket(key, default);
                    if (!m_Relations.TryGetValue(relation, out Series series))
                    {
                        m_Relations[relation] = series = new Series(relation, m_Dense);
                        Index(series, false);
                    }
                    int before = series.Count;
                    series.Set(absolute, value);
                    Count += series.Count - before;
                }
            }

            internal bool ContainsKey(TKey key) => TryGetValue(key, out _);

            internal Series Bind(TKey key)
            {
                TKey relation = m_WithBucket(key, default);
                if (!m_Relations.TryGetValue(relation, out Series series))
                {
                    m_Relations[relation] = series = new Series(relation, m_Dense);
                    Index(series, false);
                }
                series.Bindings++;
                return series;
            }

            internal void Unbind(Series series)
            {
                series.Bindings--;
                if (series.Bindings == 0 && series.Count == 0)
                { Index(series, true); m_Relations.Remove(series.Key); }
            }

            internal void Set(Series series, int absolute, TValue value)
            {
                if (absolute < m_Minimum) return;
                int before = series.Count;
                series.Set(absolute, value);
                Count += series.Count - before;
            }

            internal void Remove(TKey key)
            {
                TKey relation = m_WithBucket(key, default);
                if (!m_Relations.TryGetValue(relation, out Series series)
                    || !series.Remove(SamplingSystem.AbsoluteBucketIndex(m_Bucket(key)))) return;
                Count--;
                if (series.Count == 0 && series.Bindings == 0)
                { Index(series, true); m_Relations.Remove(relation); }
            }

            private static void ChangeIndex<TIndex>(Dictionary<TIndex, HashSet<Series>> index,
                TIndex key, Series series, bool remove)
            {
                if (remove)
                {
                    HashSet<Series> rows = index[key];
                    rows.Remove(series);
                    if (rows.Count == 0) index.Remove(key);
                }
                else
                {
                    if (!index.TryGetValue(key, out HashSet<Series> rows))
                        index[key] = rows = new HashSet<Series>();
                    rows.Add(series);
                }
            }

            private void Index(Series series, bool remove)
            {
                if (m_Endpoints == null) return;
                var endpoints = m_Endpoints(series.Key);
                for (int i = 0; i < endpoints.Length; i++)
                {
                    var end = endpoints[i];
                    if (i == 0 || end.Mode != endpoints[0].Mode)
                        ChangeIndex(m_Modes, end.Mode, series, remove);
                    if (!string.IsNullOrEmpty(end.Line) && (i == 0 || end.Line != endpoints[0].Line))
                        ChangeIndex(m_Lines, end.Line, series, remove);
                    if (end.Station >= 0 && (i == 0 || end.Station != endpoints[0].Station))
                        ChangeIndex(m_Stations, end.Station, series, remove);
                }
            }

            internal IEnumerable<Series> Select(FlowQuery query, bool ignoreStation = false)
            {
                HashSet<Series> rows;
                if (!string.IsNullOrEmpty(query.LineId))
                    return m_Lines.TryGetValue(query.LineId, out rows) ? rows : Enumerable.Empty<Series>();
                if (!ignoreStation && !string.IsNullOrEmpty(query.StationId))
                {
                    var selected = new HashSet<Series>();
                    foreach (int station in query.StationIndices)
                        if (m_Stations.TryGetValue(station, out rows)) selected.UnionWith(rows);
                    return selected;
                }
                if (!query.AllModes)
                    return m_Modes.TryGetValue(query.Mode, out rows) ? rows : Enumerable.Empty<Series>();
                return m_Relations.Values;
            }

            internal void Trim(int minimum)
            {
                m_Minimum = minimum;
                List<TKey> remove = new List<TKey>();
                foreach (Series series in m_Relations.Values)
                {
                    int before = series.Count;
                    series.Trim(minimum);
                    Count += series.Count - before;
                    if (series.Count == 0 && series.Bindings == 0) remove.Add(series.Key);
                }
                foreach (TKey key in remove)
                { Index(m_Relations[key], true); m_Relations.Remove(key); }
            }

            internal void Clear()
            {
                m_Relations.Clear(); m_Modes.Clear(); m_Lines.Clear(); m_Stations.Clear();
                Count = 0; m_Minimum = int.MinValue;
            }

            public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
            {
                foreach (Series series in m_Relations.Values)
                    foreach (KeyValuePair<int, TValue> pair in series.All())
                        yield return new KeyValuePair<TKey, TValue>(
                            m_WithBucket(series.Key, SamplingSystem.BucketFromAbsoluteIndex(pair.Key)), pair.Value);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        internal sealed class LineLoadSeries
        {
            private readonly BucketTable<LineTimeKey, LineTimeAggregate>.Series m_Series;

            private LineLoadSeries(BucketTable<LineTimeKey, LineTimeAggregate>.Series series)
                => m_Series = series;

            internal static LineLoadSeries Bind(Aggregates owner, TransitMode mode, string lineId)
                => new LineLoadSeries(owner.m_LineTimeLoads.Bind(new LineTimeKey(mode, lineId, default)));

            internal void Unbind(Aggregates owner) => owner.m_LineTimeLoads.Unbind(m_Series);

            internal void Record(Aggregates owner, TimeBucketKey bucket, LineTimeAggregate value)
            {
                if (owner.OutsideWindow(bucket)) return;
                int absolute = SamplingSystem.AbsoluteBucketIndex(bucket);
                m_Series.TryGet(absolute, out LineTimeAggregate total);
                AddTime(ref total, value);
                owner.m_LineTimeLoads.Set(m_Series, absolute, total);
            }
        }

        private readonly BucketTable<StationVolumeKey, StationVolumeAggregate> m_StationVolumes =
            new BucketTable<StationVolumeKey, StationVolumeAggregate>(true, k => k.Bucket,
                (k, b) => new StationVolumeKey(k.Mode, k.LineId, k.StationSakIndex, b),
                k => new[] { (k.Mode, k.LineId, k.StationSakIndex) });
        private readonly BucketTable<SectionVolumeKey, SectionVolumeAggregate> m_SectionVolumes =
            new BucketTable<SectionVolumeKey, SectionVolumeAggregate>(true, k => k.Bucket,
                (k, b) => new SectionVolumeKey(k.Mode, k.LineId, k.FromStationSakIndex, k.ToStationSakIndex, b, k.Kind),
                k => new[] { (k.Mode, k.LineId, k.FromStationSakIndex), (k.Mode, k.LineId, k.ToStationSakIndex) });
        private readonly BucketTable<OdFlowKey, OdFlowAggregate> m_OdFlows =
            new BucketTable<OdFlowKey, OdFlowAggregate>(false, k => k.Bucket,
                (k, b) => new OdFlowKey(k.Mode, k.FirstLineId, k.LastLineId, k.OriginStationSakIndex, k.DestinationStationSakIndex, b),
                k => new[] { (k.Mode, k.FirstLineId, k.OriginStationSakIndex), (k.Mode, k.LastLineId, k.DestinationStationSakIndex) });
        private readonly BucketTable<TransferKey, TransferAggregate> m_Transfers =
            new BucketTable<TransferKey, TransferAggregate>(false, k => k.Bucket,
                (k, b) => new TransferKey(k.FromMode, k.FromLineId, k.FromStationSakIndex, k.FromStationOccurrence,
                    k.ToMode, k.ToLineId, k.ToStationSakIndex, k.ToStationOccurrence, b),
                k => new[] { (k.FromMode, k.FromLineId, k.FromStationSakIndex), (k.ToMode, k.ToLineId, k.ToStationSakIndex) });
        private readonly BucketTable<SampleGroupKey, int> m_SampleGroups =
            new BucketTable<SampleGroupKey, int>(false, k => k.Bucket,
                (k, b) => new SampleGroupKey(k.FromMode, k.FromLineId, k.FromStationIndex, k.FromStationOccurrence,
                    k.ToMode, k.ToLineId, k.ToStationIndex, k.ToStationOccurrence, b),
                null);
        private readonly BucketTable<StationWaitingKey, StationWaitingAggregate> m_StationWaiting =
            new BucketTable<StationWaitingKey, StationWaitingAggregate>(true, k => k.Bucket,
                (k, b) => new StationWaitingKey(k.Mode, k.LineId, k.StationOccurrence, k.StationSakIndex, b),
                k => new[] { (k.Mode, k.LineId, k.StationSakIndex) });
        private readonly BucketTable<LineTimeKey, LineTimeAggregate> m_LineTimeLoads =
            new BucketTable<LineTimeKey, LineTimeAggregate>(true, k => k.Bucket,
                (k, b) => new LineTimeKey(k.Mode, k.LineId, b), k => new[] { (k.Mode, k.LineId, -1) });
        private readonly BucketTable<TransitMode, LineTimeAggregate>.Series[] m_NetworkTimeLoads =
        {
            new BucketTable<TransitMode, LineTimeAggregate>.Series(TransitMode.Train, true),
            new BucketTable<TransitMode, LineTimeAggregate>.Series(TransitMode.Subway, true),
            new BucketTable<TransitMode, LineTimeAggregate>.Series(TransitMode.Tram, true),
            new BucketTable<TransitMode, LineTimeAggregate>.Series(TransitMode.Bus, true)
        };
        private readonly BucketTable<WarningKey, WarningAggregate> m_Warnings =
            new BucketTable<WarningKey, WarningAggregate>(false, k => k.Bucket,
                (k, b) => new WarningKey(k.Mode, k.Code, k.LineId, k.StationSakIndex, b),
                k => new[] { (k.Mode, k.LineId, k.StationSakIndex) });
        private readonly BucketTable<StationVolumeKey, StationVolumeAggregate> m_NetworkStations =
            new BucketTable<StationVolumeKey, StationVolumeAggregate>(true, k => k.Bucket,
                (k, b) => new StationVolumeKey(k.Mode, string.Empty, k.StationSakIndex, b),
                k => new[] { (k.Mode, string.Empty, k.StationSakIndex) });

        private bool m_HasTrimBoundary;
        private int m_MinDayIndex;
        private int m_MinBucketMinute;

        internal int StationVolumeCount => m_StationVolumes.Count;
        internal int SectionVolumeCount => m_SectionVolumes.Count;
        internal int OdFlowCount => m_OdFlows.Count;
        internal int WarningCount => m_Warnings.Count;
        internal int SampleGroupCount => m_SampleGroups.Count;

        internal bool TryReserveSample(SampleGroupKey key)
        {
            if (OutsideWindow(key.Bucket)) return false;
            m_SampleGroups.TryGetValue(key, out int selected);
            if (selected >= 3)
                return false;
            m_SampleGroups[key] = selected + 1;
            return true;
        }

        internal void Clear()
        {
            m_StationVolumes.Clear();
            m_SectionVolumes.Clear();
            m_OdFlows.Clear();
            m_Transfers.Clear();
            m_SampleGroups.Clear();
            m_StationWaiting.Clear();
            m_LineTimeLoads.Clear();
            foreach (var series in m_NetworkTimeLoads)
                series.Clear();
            m_Warnings.Clear();
            m_NetworkStations.Clear();
            m_HasTrimBoundary = false;
        }

        internal PassengerFlowPersistedStationVolume[] ExportStationVolumes()
        {
            return m_StationVolumes
                .Select(pair => new PassengerFlowPersistedStationVolume
                {
                    mode = TransitModeCodec.Format(pair.Key.Mode),
                    lineId = pair.Key.LineId,
                    stationSakIndex = pair.Key.StationSakIndex,
                    dayIndex = pair.Key.Bucket.DayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    boardings = pair.Value.Boardings,
                    alightings = pair.Value.Alightings,
                    purposeCounts = ExportStationPurposes(pair.Value.Purposes)
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
                    sectionKind = pair.Key.Kind == SectionKind.Stops ? "stops" : "track",
                    toStationSakIndex = pair.Key.ToStationSakIndex,
                    dayIndex = pair.Key.Bucket.DayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    loadPassengersSum = pair.Value.LoadPassengersSum,
                    capacitySum = pair.Value.CapacitySum,
                    ratedLoadPassengersSum = pair.Value.RatedLoadPassengersSum,
                    sampleCount = pair.Value.SampleCount,
                    capacitySampleCount = pair.Value.CapacitySampleCount,
                    purposeCounts = ExportSectionPurposes(pair.Value.Purposes)
                })
                .ToArray();
        }

        internal PassengerFlowPersistedTransferFlow[] ExportTransfers()
        {
            return m_Transfers.Select(pair => new PassengerFlowPersistedTransferFlow
            {
                fromMode = TransitModeCodec.Format(pair.Key.FromMode), fromLineId = pair.Key.FromLineId,
                fromStationSakIndex = pair.Key.FromStationSakIndex,
                fromStationOccurrence = pair.Key.FromStationOccurrence,
                toMode = TransitModeCodec.Format(pair.Key.ToMode), toLineId = pair.Key.ToLineId,
                toStationSakIndex = pair.Key.ToStationSakIndex,
                toStationOccurrence = pair.Key.ToStationOccurrence,
                dayIndex = pair.Key.Bucket.DayIndex, bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                completedCount = pair.Value.CompletedCount, walkPathMetersSum = pair.Value.WalkPathMetersSum,
                walkPathSampleCount = pair.Value.WalkPathSampleCount, walkFramesSum = pair.Value.WalkFramesSum,
                walkTimeSampleCount = pair.Value.WalkTimeSampleCount,
                purposeCounts = ExportPurposes(pair.Value.Purposes)
            }).ToArray();
        }

        internal PassengerFlowPersistedSampleGroup[] ExportSampleGroups()
        {
            return m_SampleGroups.Select(pair => new PassengerFlowPersistedSampleGroup
            {
                fromMode = TransitModeCodec.Format(pair.Key.FromMode), fromLineId = pair.Key.FromLineId,
                fromStationSakIndex = pair.Key.FromStationIndex,
                fromStationOccurrence = pair.Key.FromStationOccurrence,
                toMode = TransitModeCodec.Format(pair.Key.ToMode), toLineId = pair.Key.ToLineId,
                toStationSakIndex = pair.Key.ToStationIndex,
                toStationOccurrence = pair.Key.ToStationOccurrence,
                dayIndex = pair.Key.Bucket.DayIndex,
                bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                selectedCount = pair.Value
            }).ToArray();
        }

        internal PassengerFlowPersistedLineTimeLoad[] ExportLineTimeLoads()
        {
            return m_LineTimeLoads.Select(pair => new PassengerFlowPersistedLineTimeLoad
            {
                mode = TransitModeCodec.Format(pair.Key.Mode), lineId = pair.Key.LineId,
                dayIndex = pair.Key.Bucket.DayIndex,
                bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                passengerFrames = pair.Value.PassengerFrames,
                observedFrames = pair.Value.ObservedFrames,
                ratedPassengerFrames = pair.Value.RatedPassengerFrames,
                capacityFrames = pair.Value.CapacityFrames,
                capacityObservedFrames = pair.Value.CapacityObservedFrames
            }).ToArray();
        }

        internal PassengerFlowPersistedNetworkTimeLoad[] ExportNetworkTimeLoads()
        {
            List<PassengerFlowPersistedNetworkTimeLoad> rows = new List<PassengerFlowPersistedNetworkTimeLoad>();
            foreach (var series in m_NetworkTimeLoads)
                foreach (var pair in series.All())
                {
                    TimeBucketKey bucket = SamplingSystem.BucketFromAbsoluteIndex(pair.Key);
                    rows.Add(new PassengerFlowPersistedNetworkTimeLoad
                    {
                        mode = TransitModeCodec.Format(series.Key),
                        dayIndex = bucket.DayIndex, bucketStartMinute = bucket.BucketStartMinute,
                        passengerFrames = pair.Value.PassengerFrames, observedFrames = pair.Value.ObservedFrames,
                        ratedPassengerFrames = pair.Value.RatedPassengerFrames, capacityFrames = pair.Value.CapacityFrames,
                        capacityObservedFrames = pair.Value.CapacityObservedFrames
                    });
                }
            return rows.ToArray();
        }

        internal PassengerFlowPersistedStationWaiting[] ExportStationWaiting()
        {
            return m_StationWaiting.Select(pair => new PassengerFlowPersistedStationWaiting
            {
                mode = TransitModeCodec.Format(pair.Key.Mode), lineId = pair.Key.LineId,
                stationOccurrence = pair.Key.StationOccurrence, stationSakIndex = pair.Key.StationSakIndex,
                dayIndex = pair.Key.Bucket.DayIndex, bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                latestWaitingCount = pair.Value.LatestWaitingCount, peakWaitingCount = pair.Value.PeakWaitingCount,
                waitingCountFrames = pair.Value.WaitingCountFrames,
                latestEstimatedWaitFrames = pair.Value.LatestEstimatedWaitFrames,
                estimatedWaitFrameFrames = pair.Value.EstimatedWaitFrameFrames,
                observedFrames = pair.Value.ObservedFrames, sampleCount = pair.Value.SampleCount
            }).ToArray();
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
                    dayIndex = pair.Key.Bucket.DayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    completedCount = pair.Value.CompletedCount,
                    purposeCounts = ExportOdPurposes(pair.Value.Purposes)
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
                    dayIndex = pair.Key.Bucket.DayIndex,
                    bucketStartMinute = pair.Key.Bucket.BucketStartMinute,
                    count = pair.Value.Count,
                    lastFrame = pair.Value.LastFrame
                })
                .ToArray();
        }

        private static PassengerFlowPersistedStationPurposeCount[] ExportStationPurposes(
            Dictionary<PurposeInfo, PurposeCount> counts)
        {
            return counts?.Select(pair => new PassengerFlowPersistedStationPurposeCount
            {
                rawPurpose = pair.Key.RawPurpose, category = (byte)pair.Key.Category,
                boardings = pair.Value.Boardings, alightings = pair.Value.Alightings
            }).ToArray();
        }

        private static PassengerFlowPersistedSectionPurposeCount[] ExportSectionPurposes(
            Dictionary<PurposeInfo, PurposeCount> counts)
        {
            return counts?.Select(pair => new PassengerFlowPersistedSectionPurposeCount
            {
                rawPurpose = pair.Key.RawPurpose, category = (byte)pair.Key.Category,
                loadPassengersSum = pair.Value.LoadPassengersSum
            }).ToArray();
        }

        private static PassengerFlowPersistedOdPurposeCount[] ExportOdPurposes(
            Dictionary<PurposeInfo, PurposeCount> counts)
        {
            return counts?.Select(pair => new PassengerFlowPersistedOdPurposeCount
            {
                rawPurpose = pair.Key.RawPurpose, category = (byte)pair.Key.Category,
                completedCount = pair.Value.CompletedCount
            }).ToArray();
        }

        private static PassengerFlowPersistedTransferPurposeCount[] ExportPurposes(
            Dictionary<PurposePair, PurposeCount> counts)
        {
            return counts?.Select(pair => new PassengerFlowPersistedTransferPurposeCount
            {
                rawPurpose = pair.Key.From.RawPurpose, category = (byte)pair.Key.From.Category,
                toRawPurpose = pair.Key.To.RawPurpose, toCategory = (byte)pair.Key.To.Category,
                completedCount = pair.Value.CompletedCount
            }).ToArray();
        }

        private static Dictionary<PurposeInfo, PurposeCount> RestorePurposes(
            PassengerFlowPersistedPurposeCount[] rows)
        {
            if (rows == null || rows.Length == 0)
                return null;
            Dictionary<PurposeInfo, PurposeCount> counts = new Dictionary<PurposeInfo, PurposeCount>();
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedPurposeCount row = rows[i];
                PurposeInfo key = new PurposeInfo(row.rawPurpose, (PurposeCategory)row.category);
                if (row is PassengerFlowPersistedStationPurposeCount station)
                    counts[key] = new PurposeCount { Boardings = station.boardings, Alightings = station.alightings };
                else if (row is PassengerFlowPersistedSectionPurposeCount section)
                    counts[key] = new PurposeCount { LoadPassengersSum = section.loadPassengersSum };
                else if (row is PassengerFlowPersistedOdPurposeCount od)
                    counts[key] = new PurposeCount { CompletedCount = od.completedCount };
            }
            return counts;
        }

        private static Dictionary<PurposePair, PurposeCount> RestorePurposePairs(
            PassengerFlowPersistedTransferPurposeCount[] rows)
        {
            if (rows == null || rows.Length == 0)
                return null;
            Dictionary<PurposePair, PurposeCount> counts = new Dictionary<PurposePair, PurposeCount>();
            for (int i = 0; i < rows.Length; i++)
            {
                PassengerFlowPersistedTransferPurposeCount row = rows[i];
                PurposePair key = new PurposePair(
                    new PurposeInfo(row.rawPurpose, (PurposeCategory)row.category),
                    new PurposeInfo(row.toRawPurpose, (PurposeCategory)row.toCategory));
                counts[key] = new PurposeCount { CompletedCount = row.completedCount };
            }
            return counts;
        }

        internal void Restore(
            PassengerFlowPersistedStationVolume[] stationVolumes,
            PassengerFlowPersistedSectionVolume[] sectionVolumes,
            PassengerFlowPersistedOdFlow[] odFlows,
            PassengerFlowPersistedTransferFlow[] transferFlows,
            PassengerFlowPersistedSampleGroup[] sampleGroups,
            PassengerFlowPersistedLineTimeLoad[] lineTimeLoads,
            PassengerFlowPersistedNetworkTimeLoad[] networkTimeLoads,
            PassengerFlowPersistedStationWaiting[] stationWaiting,
            PassengerFlowPersistedWarning[] warnings)
        {
            m_StationVolumes.Clear();
            m_SectionVolumes.Clear();
            m_OdFlows.Clear();
            m_Transfers.Clear();
            m_SampleGroups.Clear();
            m_LineTimeLoads.Clear();
            foreach (var series in m_NetworkTimeLoads)
                series.Clear();
            m_StationWaiting.Clear();
            m_Warnings.Clear();

            if (sampleGroups != null)
            {
                for (int i = 0; i < sampleGroups.Length; i++)
                {
                    PassengerFlowPersistedSampleGroup row = sampleGroups[i];
                    if (row == null || !TransitModeCodec.TryParse(row.fromMode, out TransitMode fromMode)
                        || !TransitModeCodec.TryParse(row.toMode, out TransitMode toMode))
                        continue;
                    SampleGroupKey key = new SampleGroupKey(fromMode, row.fromLineId,
                        row.fromStationSakIndex, row.fromStationOccurrence,
                        toMode, row.toLineId, row.toStationSakIndex, row.toStationOccurrence,
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute));
                    m_SampleGroups[key] = row.selectedCount;
                }
            }

            if (lineTimeLoads != null)
            {
                for (int i = 0; i < lineTimeLoads.Length; i++)
                {
                    PassengerFlowPersistedLineTimeLoad row = lineTimeLoads[i];
                    if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                        continue;
                    m_LineTimeLoads[new LineTimeKey(mode, row.lineId,
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] = new LineTimeAggregate
                    {
                        PassengerFrames = row.passengerFrames,
                        ObservedFrames = row.observedFrames,
                        RatedPassengerFrames = row.ratedPassengerFrames,
                        CapacityFrames = row.capacityFrames,
                        CapacityObservedFrames = row.capacityObservedFrames
                    };
                }
            }

            if (networkTimeLoads != null)
                foreach (PassengerFlowPersistedNetworkTimeLoad row in networkTimeLoads)
                {
                    if (!TransitModeCodec.TryParse(row.mode, out TransitMode mode) || mode == TransitMode.Unknown)
                        continue;
                    m_NetworkTimeLoads[(int)mode - 1].Set(SamplingSystem.AbsoluteBucketIndex(
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute)), new LineTimeAggregate
                    {
                        PassengerFrames = row.passengerFrames, ObservedFrames = row.observedFrames,
                        RatedPassengerFrames = row.ratedPassengerFrames, CapacityFrames = row.capacityFrames,
                        CapacityObservedFrames = row.capacityObservedFrames
                    });
                }

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
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] =
                        new StationVolumeAggregate
                        {
                            Boardings = row.boardings,
                            Alightings = row.alightings,
                            Purposes = RestorePurposes(row.purposeCounts)
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
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute),
                        row.sectionKind == "stops" ? SectionKind.Stops : SectionKind.Track)] =
                        new SectionVolumeAggregate
                        {
                            LoadPassengersSum = row.loadPassengersSum,
                            CapacitySum = row.capacitySum,
                            RatedLoadPassengersSum = row.ratedLoadPassengersSum,
                            SampleCount = row.sampleCount,
                            CapacitySampleCount = row.capacitySampleCount,
                            Purposes = RestorePurposes(row.purposeCounts)
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
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] =
                        new OdFlowAggregate
                        {
                            CompletedCount = row.completedCount,
                            Purposes = RestorePurposes(row.purposeCounts)
                        };
                }
            }

            if (transferFlows != null)
            {
                for (int i = 0; i < transferFlows.Length; i++)
                {
                    PassengerFlowPersistedTransferFlow row = transferFlows[i];
                    if (row == null
                        || !TransitModeCodec.TryParse(row.fromMode, out TransitMode fromMode)
                        || !TransitModeCodec.TryParse(row.toMode, out TransitMode toMode))
                    {
                        continue;
                    }

                    m_Transfers[new TransferKey(
                        fromMode, row.fromLineId, row.fromStationSakIndex,
                        row.fromStationOccurrence,
                        toMode, row.toLineId, row.toStationSakIndex,
                        row.toStationOccurrence,
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] = new TransferAggregate
                    {
                        CompletedCount = row.completedCount,
                        WalkPathMetersSum = row.walkPathMetersSum,
                        WalkPathSampleCount = row.walkPathSampleCount,
                        WalkFramesSum = row.walkFramesSum,
                        WalkTimeSampleCount = row.walkTimeSampleCount,
                        Purposes = RestorePurposePairs(row.purposeCounts)
                    };
                }
            }

            if (stationWaiting != null)
            {
                for (int i = 0; i < stationWaiting.Length; i++)
                {
                    PassengerFlowPersistedStationWaiting row = stationWaiting[i];
                    if (row == null || !TransitModeCodec.TryParse(row.mode, out TransitMode mode))
                        continue;

                    m_StationWaiting[new StationWaitingKey(
                        mode, row.lineId, row.stationOccurrence, row.stationSakIndex,
                        new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] = new StationWaitingAggregate
                    {
                        LatestWaitingCount = row.latestWaitingCount,
                        PeakWaitingCount = row.peakWaitingCount,
                        WaitingCountFrames = row.waitingCountFrames,
                        LatestEstimatedWaitFrames = row.latestEstimatedWaitFrames,
                        EstimatedWaitFrameFrames = row.estimatedWaitFrameFrames,
                        ObservedFrames = row.observedFrames,
                        SampleCount = row.sampleCount
                    };
                }
            }

            RebuildNetworkStations();
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
                    new TimeBucketKey(row.dayIndex, row.bucketStartMinute))] =
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
            PurposeInfo purpose)
        {
            if (OutsideWindow(bucket)) return;
            StationVolumeKey key = new StationVolumeKey(mode, lineId, stationSakIndex, bucket);
            m_StationVolumes.TryGetValue(key, out StationVolumeAggregate aggregate);
            aggregate.Boardings++;
            if (aggregate.Purposes == null)
                aggregate.Purposes = new Dictionary<PurposeInfo, PurposeCount>();
            aggregate.Purposes.TryGetValue(purpose, out PurposeCount boardCount);
            boardCount.Boardings++;
            aggregate.Purposes[purpose] = boardCount;
            m_StationVolumes[key] = aggregate;
            RecordNetworkStation(mode, stationSakIndex, bucket, true, purpose);
        }

        internal void RecordAlighting(
            TransitMode mode,
            string lineId,
            int stationSakIndex,
            TimeBucketKey bucket,
            PurposeInfo purpose)
        {
            if (OutsideWindow(bucket)) return;
            StationVolumeKey key = new StationVolumeKey(mode, lineId, stationSakIndex, bucket);
            m_StationVolumes.TryGetValue(key, out StationVolumeAggregate aggregate);
            aggregate.Alightings++;
            if (aggregate.Purposes == null)
                aggregate.Purposes = new Dictionary<PurposeInfo, PurposeCount>();
            aggregate.Purposes.TryGetValue(purpose, out PurposeCount alightCount);
            alightCount.Alightings++;
            aggregate.Purposes[purpose] = alightCount;
            m_StationVolumes[key] = aggregate;
            RecordNetworkStation(mode, stationSakIndex, bucket, false, purpose);
        }

        internal void RecordSectionLoad(
            TransitMode mode,
            string lineId,
            int fromStationSakIndex,
            int toStationSakIndex,
            int passengerCount,
            int passengerCapacity,
            bool hasCapacity,
            TimeBucketKey bucket,
            Dictionary<PurposeInfo, PurposeCount> purposes, SectionKind kind)
        {
            if (OutsideWindow(bucket))
                return;
            SectionVolumeKey key = new SectionVolumeKey(mode, lineId, fromStationSakIndex, toStationSakIndex, bucket, kind);
            RecordSectionLoad(m_SectionVolumes, key, passengerCount, passengerCapacity, hasCapacity, purposes);
        }

        private static void RecordSectionLoad<TKey>(
            BucketTable<TKey, SectionVolumeAggregate> target,
            TKey key,
            int passengerCount,
            int passengerCapacity,
            bool hasCapacity,
            Dictionary<PurposeInfo, PurposeCount> purposes)
        {
            target.TryGetValue(key, out SectionVolumeAggregate aggregate);
            aggregate.LoadPassengersSum += passengerCount;
            aggregate.SampleCount++;
            if (hasCapacity)
            {
                aggregate.CapacitySum += passengerCapacity;
                aggregate.RatedLoadPassengersSum += passengerCount;
                aggregate.CapacitySampleCount++;
            }
            MergePurposes(ref aggregate.Purposes, purposes);
            target[key] = aggregate;
        }

        internal void RecordTransfer(TransferRecord record)
        {
            if (OutsideWindow(record.Bucket))
                return;
            TransferKey key = new TransferKey(record.FromMode, record.FromLineId,
                record.FromStationIndex, record.FromPosition.StationOccurrence,
                record.ToMode, record.ToLineId, record.ToStationIndex,
                record.ToPosition.StationOccurrence, record.Bucket);
            m_Transfers.TryGetValue(key, out TransferAggregate aggregate);
            aggregate.CompletedCount++;
            if (record.WalkMeters.HasValue)
            {
                aggregate.WalkPathMetersSum += record.WalkMeters.Value;
                aggregate.WalkPathSampleCount++;
            }
            if (record.WalkFrames.HasValue)
            {
                aggregate.WalkFramesSum += record.WalkFrames.Value;
                aggregate.WalkTimeSampleCount++;
            }
            if (aggregate.Purposes == null)
                aggregate.Purposes = new Dictionary<PurposePair, PurposeCount>();
            PurposePair pair = new PurposePair(record.FromPurpose, record.ToPurpose);
            aggregate.Purposes.TryGetValue(pair, out PurposeCount transferCount);
            transferCount.CompletedCount++;
            aggregate.Purposes[pair] = transferCount;
            m_Transfers[key] = aggregate;
        }

        internal LineLoadSeries BindTimeLoad(TransitMode mode, string lineId)
            => LineLoadSeries.Bind(this, mode, lineId);

        internal void UnbindTimeLoad(LineLoadSeries series) => series.Unbind(this);

        internal void RecordTimeLoad(LineLoadSeries series, TimeBucketKey bucket, LineTimeAggregate value)
            => series.Record(this, bucket, value);

        internal void RecordNetworkLoad(TransitMode mode, TimeBucketKey bucket, LineTimeAggregate value)
        {
            if (OutsideWindow(bucket)) return;
            var series = m_NetworkTimeLoads[(int)mode - 1];
            int absolute = SamplingSystem.AbsoluteBucketIndex(bucket);
            series.TryGet(absolute, out LineTimeAggregate total);
            AddTime(ref total, value);
            series.Set(absolute, total);
        }

        internal void RecordWaiting(
            TransitMode mode, string lineId, int stationOccurrence, int stationSakIndex,
            TimeBucketKey bucket, int waitingCount, double estimatedWaitFrames)
        {
            StationWaitingKey key = new StationWaitingKey(mode, lineId, stationOccurrence, stationSakIndex, bucket);
            m_StationWaiting.TryGetValue(key, out StationWaitingAggregate aggregate);
            aggregate.LatestWaitingCount = waitingCount;
            if (waitingCount > aggregate.PeakWaitingCount)
                aggregate.PeakWaitingCount = waitingCount;
            aggregate.LatestEstimatedWaitFrames = estimatedWaitFrames;
            aggregate.SampleCount++;
            m_StationWaiting[key] = aggregate;
        }

        internal void AccumulateWaiting(
            TransitMode mode, string lineId, int stationOccurrence, int stationSakIndex,
            TimeBucketKey bucket, int waitingCount, double estimatedWaitFrames, uint coveredFrames)
        {
            if (coveredFrames == 0)
                return;

            StationWaitingKey key = new StationWaitingKey(mode, lineId, stationOccurrence, stationSakIndex, bucket);
            m_StationWaiting.TryGetValue(key, out StationWaitingAggregate aggregate);
            aggregate.WaitingCountFrames += (long)waitingCount * coveredFrames;
            aggregate.EstimatedWaitFrameFrames += estimatedWaitFrames * coveredFrames;
            aggregate.ObservedFrames += coveredFrames;
            m_StationWaiting[key] = aggregate;
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
            PurposeInfo purpose)
        {
            if (OutsideWindow(bucket))
                return;
            OdFlowKey key = new OdFlowKey(
                mode,
                firstLineId,
                lastLineId,
                originStationSakIndex,
                destinationStationSakIndex,
                bucket);
            m_OdFlows.TryGetValue(key, out OdFlowAggregate aggregate);
            aggregate.CompletedCount++;
            if (aggregate.Purposes == null)
                aggregate.Purposes = new Dictionary<PurposeInfo, PurposeCount>();
            aggregate.Purposes.TryGetValue(purpose, out PurposeCount odCount);
            odCount.CompletedCount++;
            aggregate.Purposes[purpose] = odCount;
            m_OdFlows[key] = aggregate;
        }

        private void RecordNetworkStation(TransitMode mode, int station, TimeBucketKey bucket,
            bool boarding, PurposeInfo purpose)
        {
            StationVolumeKey key = new StationVolumeKey(mode, string.Empty, station, bucket);
            m_NetworkStations.TryGetValue(key, out StationVolumeAggregate total);
            if (boarding) total.Boardings++; else total.Alightings++;
            if (total.Purposes == null) total.Purposes = new Dictionary<PurposeInfo, PurposeCount>();
            total.Purposes.TryGetValue(purpose, out PurposeCount count);
            if (boarding) count.Boardings++; else count.Alightings++;
            total.Purposes[purpose] = count;
            m_NetworkStations[key] = total;
        }

        private void RebuildNetworkStations()
        {
            m_NetworkStations.Clear();
            foreach (KeyValuePair<StationVolumeKey, StationVolumeAggregate> pair in m_StationVolumes)
            {
                StationVolumeKey key = new StationVolumeKey(pair.Key.Mode, string.Empty,
                    pair.Key.StationSakIndex, pair.Key.Bucket);
                m_NetworkStations.TryGetValue(key, out StationVolumeAggregate total);
                AddStation(ref total, pair.Value, true);
                m_NetworkStations[key] = total;
            }
        }


        internal void TrimBefore(int minDayIndex, int minBucketStartMinute)
        {
            if (m_HasTrimBoundary && m_MinDayIndex == minDayIndex
                && m_MinBucketMinute == minBucketStartMinute)
                return;
            m_HasTrimBoundary = true;
            m_MinDayIndex = minDayIndex;
            m_MinBucketMinute = minBucketStartMinute;
            int minimum = SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(minDayIndex, minBucketStartMinute));
            m_StationVolumes.Trim(minimum);
            m_SectionVolumes.Trim(minimum);
            m_OdFlows.Trim(minimum);
            m_Transfers.Trim(minimum);
            m_SampleGroups.Trim(minimum);
            m_LineTimeLoads.Trim(minimum);
            foreach (var series in m_NetworkTimeLoads)
                series.Trim(minimum);
            m_StationWaiting.Trim(minimum);
            m_Warnings.Trim(minimum);
            m_NetworkStations.Trim(minimum);
        }

        private static bool IsBefore(TimeBucketKey bucket, int minDayIndex, int minBucketStartMinute)
        {
            return bucket.DayIndex < minDayIndex
                || (bucket.DayIndex == minDayIndex
                    && bucket.BucketStartMinute < minBucketStartMinute);
        }

        private bool OutsideWindow(TimeBucketKey bucket)
            => m_HasTrimBoundary && IsBefore(bucket, m_MinDayIndex, m_MinBucketMinute);

        internal SnapshotRows BuildSnapshotRows(Anchors anchors, FlowQuery query, Core.ClockSnapshot clock)
        {
            FlowOptions options = query.Options;
            SnapshotRows rows = SnapshotRows.Empty();
            FlowSummaryDto summary = options.includeSummary ? new FlowSummaryDto
            {
                stationVolumes = Array.Empty<StationSummaryDto>(), sectionVolumes = Array.Empty<SectionSummaryDto>(),
                odFlows = Array.Empty<OdSummaryDto>(),
                transferFlows = Array.Empty<TransferSummaryDto>(), stationWaiting = Array.Empty<WaitingSummaryDto>(),
                lineVolumes = Array.Empty<LineVolumeDto>(),
                lineTimeLoads = Array.Empty<LineTimeSummaryDto>(), stationTotals = Array.Empty<StationSummaryDto>(),
                networkTimeLoads = Array.Empty<NetworkTimeSummaryDto>(),
                stationRanking = Array.Empty<StationSummaryDto>()
            } : null;
            rows.Summary = summary;
            if (!query.HasSelection) return rows;
            if (options.includeNetworkLoads && (options.includeSeries || options.includeSummary))
            {
                var series = m_NetworkTimeLoads[(int)query.Mode - 1];
                var networkRows = options.includeSeries ? new List<NetworkTimeLoadDto>() : null;
                LineTimeAggregate total = default;
                bool observed = false;
                foreach (var pair in series.Read(query.FromAbsolute, query.ToAbsolute))
                {
                    observed = true;
                    if (options.includeSummary) AddTime(ref total, pair.Value);
                    if (options.includeSeries)
                    {
                        TimeBucketKey bucket = SamplingSystem.BucketFromAbsoluteIndex(pair.Key);
                        networkRows.Add(new NetworkTimeLoadDto
                        {
                            mode = query.ModeToken, dayIndex = bucket.DayIndex, bucketStartMinute = bucket.BucketStartMinute,
                            passengerFrames = pair.Value.PassengerFrames, observedFrames = pair.Value.ObservedFrames,
                            ratedPassengerFrames = pair.Value.RatedPassengerFrames, capacityFrames = pair.Value.CapacityFrames,
                            capacityObservedFrames = pair.Value.CapacityObservedFrames,
                            averageOnboardPassengers = pair.Value.ObservedFrames > 0 ? (double?)pair.Value.PassengerFrames / pair.Value.ObservedFrames : null,
                            averageLoadRatio = pair.Value.CapacityFrames > 0 ? (double?)pair.Value.RatedPassengerFrames / pair.Value.CapacityFrames : null
                        });
                    }
                }
                if (options.includeSeries) rows.NetworkTimeLoads = networkRows.ToArray();
                if (options.includeSummary && observed)
                    summary.networkTimeLoads = new[] { new NetworkTimeSummaryDto
                    {
                        mode = query.ModeToken, passengerFrames = total.PassengerFrames, observedFrames = total.ObservedFrames,
                        ratedPassengerFrames = total.RatedPassengerFrames, capacityFrames = total.CapacityFrames,
                        capacityObservedFrames = total.CapacityObservedFrames,
                        averageOnboardPassengers = total.ObservedFrames > 0 ? (double?)total.PassengerFrames / total.ObservedFrames : null,
                        averageLoadRatio = total.CapacityFrames > 0 ? (double?)total.RatedPassengerFrames / total.CapacityFrames : null
                    } };
            }
            bool stationTotalsNeeded = options.includeSummary && (query.View == "station" || query.View == "network");
            Dictionary<int, StationVolumeAggregate> stationTotals = stationTotalsNeeded
                ? new Dictionary<int, StationVolumeAggregate>() : null;
            StationVolumeAggregate scopeStations = default;

            if (options.includeSeries || options.includeSummary)
            {
                var stationSeries = options.includeSeries ? new List<StationVolumeDto>() : null;
                var stationSummary = options.includeSummary ? new List<StationSummaryDto>() : null;
                var stationGroups = new Dictionary<StationVolumeKey, StationVolumeAggregate>();
                var stationBuckets = new Dictionary<StationVolumeKey, StationVolumeAggregate>();
                var source = query.View == "network" && string.IsNullOrEmpty(query.LineId)
                    ? m_NetworkStations : m_StationVolumes;
                foreach (var series in source.Select(query))
                {
                    StationVolumeKey key = series.Key;
                    if (!query.Includes(key.Mode) || !query.Line(key.LineId) || !query.Station(key.StationSakIndex)) continue;
                    StationVolumeAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddStation(ref total, bucket.Value, options.includePurposes);
                        if (options.includeSeries)
                        {
                            var grouped = new StationVolumeKey(key.Mode, key.LineId,
                                query.GroupIndex(key.StationSakIndex), SamplingSystem.BucketFromAbsoluteIndex(bucket.Key));
                            stationBuckets.TryGetValue(grouped, out StationVolumeAggregate value);
                            AddStation(ref value, bucket.Value, options.includePurposeSeries);
                            stationBuckets[grouped] = value;
                        }
                    }
                    if (observed && options.includeSummary)
                    {
                        int groupIndex = query.GroupIndex(key.StationSakIndex);
                        var groupKey = new StationVolumeKey(key.Mode, key.LineId, groupIndex, default);
                        stationGroups.TryGetValue(groupKey, out StationVolumeAggregate groupTotal);
                        AddStation(ref groupTotal, total, options.includePurposes);
                        stationGroups[groupKey] = groupTotal;
                        AddStation(ref scopeStations, total, options.includePurposes);
                        if (stationTotalsNeeded && string.IsNullOrEmpty(query.StationId))
                        {
                            stationTotals.TryGetValue(groupIndex, out StationVolumeAggregate stationTotal);
                            AddStation(ref stationTotal, total, options.includePurposes);
                            stationTotals[groupIndex] = stationTotal;
                        }
                    }
                }
                if (options.includeSeries)
                {
                    foreach (var pair in stationBuckets)
                        stationSeries.Add(BuildStation(anchors, pair.Key, pair.Value, pair.Key.Bucket, options.includePurposeSeries));
                    rows.StationVolumes = stationSeries.ToArray();
                }
                if (options.includeSummary)
                {
                    foreach (var pair in stationGroups)
                        stationSummary.Add(BuildStationSummary(anchors, pair.Key, pair.Value, options.includePurposes));
                    summary.stationVolumes = stationSummary.ToArray();
                    summary.boardings = scopeStations.Boardings; summary.alightings = scopeStations.Alightings;
                    if (stationTotalsNeeded && !string.IsNullOrEmpty(query.StationId) && stationSummary.Count > 0)
                        stationTotals[query.StationIndex] = scopeStations;
                    if (options.includePurposes)
                        summary.purposeCounts = BuildPurposes(scopeStations.Purposes, scopeStations.Boardings, scopeStations.Alightings, 0, 0);
                }
            }

            if (options.includeRelationSeries || options.includeSummary)
            {
                var odSeries = options.includeRelationSeries ? new List<OdFlowDto>() : null;
                var odSummary = options.includeSummary ? new List<OdSummaryDto>() : null;
                bool networkOdSummary = options.includeSummary && (query.Mode == TransitMode.Tram || query.AllModes
                    || query.View == "network" && string.IsNullOrEmpty(query.LineId));
                var odBuckets = new Dictionary<OdFlowKey, OdFlowAggregate>();
                var networkOdTotals = networkOdSummary
                    ? new Dictionary<OdFlowKey, (OdFlowAggregate Total,
                        List<(string FirstLineId, string LastLineId, int CompletedCount)> Lines)>() : null;
                foreach (var series in m_OdFlows.Select(query))
                {
                    OdFlowKey key = series.Key;
                    if (!query.Includes(key.Mode) || (!query.Line(key.FirstLineId) && !query.Line(key.LastLineId))
                        || !query.EitherStation(key.OriginStationSakIndex, key.DestinationStationSakIndex)) continue;
                    OdFlowAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddOd(ref total, bucket.Value, options.includePurposes);
                        if (options.includeRelationSeries)
                        {
                            var grouped = new OdFlowKey(key.Mode, key.FirstLineId, key.LastLineId,
                                query.GroupIndex(key.OriginStationSakIndex),
                                query.GroupIndex(key.DestinationStationSakIndex), SamplingSystem.BucketFromAbsoluteIndex(bucket.Key));
                            odBuckets.TryGetValue(grouped, out OdFlowAggregate value);
                            AddOd(ref value, bucket.Value, options.includePurposes);
                            odBuckets[grouped] = value;
                        }
                    }
                    if (observed && options.includeSummary)
                    {
                        if (networkOdSummary)
                        {
                            OdFlowKey group = new OdFlowKey(key.Mode, string.Empty, string.Empty,
                                query.GroupIndex(key.OriginStationSakIndex),
                                query.GroupIndex(key.DestinationStationSakIndex), default);
                            if (!networkOdTotals.TryGetValue(group, out var value))
                                value = (default,
                                    new List<(string FirstLineId, string LastLineId, int CompletedCount)>());
                            AddOd(ref value.Total, total, options.includePurposes);
                            value.Lines.Add((key.FirstLineId, key.LastLineId, total.CompletedCount));
                            networkOdTotals[group] = value;
                        }
                        else
                        {
                            odSummary.Add(BuildOdSummary(anchors, key, total, options.includePurposes));
                            summary.completedOdCount += total.CompletedCount;
                        }
                    }
                }
                if (options.includeRelationSeries)
                {
                    foreach (var pair in odBuckets)
                        odSeries.Add(BuildOd(anchors, pair.Key, pair.Value, pair.Key.Bucket, options.includePurposes));
                    rows.OdFlows = odSeries.ToArray();
                }
                if (options.includeSummary)
                {
                    if (networkOdSummary)
                    {
                        foreach (var pair in networkOdTotals)
                        {
                            OdSummaryDto row = BuildOdSummary(anchors, pair.Key, pair.Value.Total, options.includePurposes);
                            var contributions = new List<OdLineDto>(pair.Value.Lines.Count);
                            var firstLines = new Dictionary<string, int>();
                            foreach (var line in pair.Value.Lines)
                            {
                                contributions.Add(new OdLineDto
                                {
                                    firstLineId = line.FirstLineId,
                                    lastLineId = line.LastLineId,
                                    completedCount = line.CompletedCount
                                });
                                firstLines.TryGetValue(line.FirstLineId, out int completedCount);
                                firstLines[line.FirstLineId] = completedCount + line.CompletedCount;
                            }
                            string dominantLineId = string.Empty;
                            int dominantCount = -1;
                            foreach (var line in firstLines)
                            {
                                if (line.Value > dominantCount || (line.Value == dominantCount
                                    && string.CompareOrdinal(line.Key, dominantLineId) < 0))
                                {
                                    dominantLineId = line.Key;
                                    dominantCount = line.Value;
                                }
                            }
                            row.lineContributions = contributions.ToArray();
                            row.dominantLineId = dominantLineId;
                            odSummary.Add(row);
                            summary.completedOdCount += pair.Value.Total.CompletedCount;
                        }
                    }
                    summary.odFlows = odSummary.ToArray();
                }
            }

            if (options.includeSections && (options.includeRelationSeries || options.includeSummary))
            {
                var sectionSeries = options.includeRelationSeries ? new List<SectionVolumeDto>() : null;
                var sectionSummary = options.includeSummary ? new List<SectionSummaryDto>() : null;
                bool networkTrack = query.View == "network" && query.SectionKind == SectionKind.Track;
                var networkTotals = networkTrack && options.includeSummary
                    ? new Dictionary<SectionVolumeKey, (SectionVolumeAggregate Total,
                        List<(string LineId, SectionVolumeAggregate Value)> Lines)>() : null;
                foreach (var series in m_SectionVolumes.Select(query))
                {
                    SectionVolumeKey key = series.Key;
                    if (!SectionMatches(query, key)) continue;
                    SectionVolumeAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddSection(ref total, bucket.Value, options.includePurposes);
                        if (!options.includeRelationSeries) continue;
                        TimeBucketKey time = SamplingSystem.BucketFromAbsoluteIndex(bucket.Key);
                        sectionSeries.Add(BuildSection(anchors, key, bucket.Value, time, options.includePurposes));
                    }
                    if (!observed || !options.includeSummary) continue;
                    if (networkTrack)
                    {
                        SectionVolumeKey group = new SectionVolumeKey(key.Mode, string.Empty,
                            key.FromStationSakIndex, key.ToStationSakIndex, default, key.Kind);
                        if (!networkTotals.TryGetValue(group, out var value))
                            value = (default, new List<(string LineId, SectionVolumeAggregate Value)>());
                        AddSection(ref value.Total, total, options.includePurposes);
                        value.Lines.Add((key.LineId, total));
                        networkTotals[group] = value;
                    }
                    else sectionSummary.Add(BuildSectionSummary(anchors, key, total, options.includePurposes));
                }
                if (options.includeRelationSeries)
                    rows.SectionVolumes = sectionSeries.ToArray();
                if (options.includeSummary)
                {
                    if (networkTrack)
                        foreach (var pair in networkTotals)
                        {
                            SectionSummaryDto row = BuildSectionSummary(anchors, pair.Key,
                                pair.Value.Total, options.includePurposes);
                            var contributions = new List<SectionLineDto>(pair.Value.Lines.Count);
                            foreach (var line in pair.Value.Lines)
                                contributions.Add(BuildSectionLine(line.LineId, line.Value));
                            row.lineContributions = contributions.ToArray();
                            sectionSummary.Add(row);
                        }
                    summary.sectionVolumes = sectionSummary.ToArray();
                }
            }

            if (options.includeTransfers && (options.includeRelationSeries || options.includeSummary))
            {
                var transferSeries = options.includeRelationSeries ? new List<TransferFlowDto>() : null;
                var transferSummary = options.includeSummary ? new List<TransferSummaryDto>() : null;
                foreach (var series in m_Transfers.Select(query))
                {
                    TransferKey key = series.Key;
                    if (!query.Transfer(key)) continue;
                    TransferAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddTransfer(ref total, bucket.Value, options.includePurposes);
                        if (options.includeRelationSeries)
                        {
                            TransferKey timed = new TransferKey(key.FromMode, key.FromLineId, key.FromStationSakIndex,
                                key.FromStationOccurrence, key.ToMode, key.ToLineId, key.ToStationSakIndex,
                                key.ToStationOccurrence, SamplingSystem.BucketFromAbsoluteIndex(bucket.Key));
                            transferSeries.Add(BuildTransfer(anchors,
                                new KeyValuePair<TransferKey, TransferAggregate>(timed, bucket.Value), clock, options.includePurposes));
                        }
                    }
                    if (observed && options.includeSummary)
                        transferSummary.Add(BuildTransferSummary(anchors, options, clock,
                            new KeyValuePair<TransferKey, TransferAggregate>(key, total)));
                }
                if (options.includeRelationSeries) rows.TransferFlows = transferSeries.ToArray();
                if (options.includeSummary) summary.transferFlows = transferSummary.ToArray();
            }

            if (options.includeStationWaiting && (options.includeRelationSeries || options.includeSummary))
            {
                var waitingSeries = options.includeRelationSeries ? new List<StationWaitingDto>() : null;
                var waitingSummary = options.includeSummary ? new List<WaitingSummaryDto>() : null;
                foreach (var series in m_StationWaiting.Select(query))
                {
                    StationWaitingKey key = series.Key;
                    if (!query.Includes(key.Mode) || !query.Line(key.LineId) || !query.Station(key.StationSakIndex)) continue;
                    StationWaitingAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddWaiting(ref total, bucket.Value);
                        if (options.includeRelationSeries)
                        {
                            StationWaitingKey timed = new StationWaitingKey(key.Mode, key.LineId, key.StationOccurrence,
                                key.StationSakIndex, SamplingSystem.BucketFromAbsoluteIndex(bucket.Key));
                            waitingSeries.Add(BuildWaiting(anchors,
                                new KeyValuePair<StationWaitingKey, StationWaitingAggregate>(timed, bucket.Value), clock));
                        }
                    }
                    if (observed && options.includeSummary) waitingSummary.Add(BuildWaitingSummary(anchors, clock, key, total));
                }
                if (options.includeRelationSeries) rows.StationWaiting = waitingSeries.ToArray();
                if (options.includeSummary) summary.stationWaiting = waitingSummary.ToArray();
            }

            if (query.View == "network" && options.includeLineComparison && options.includeSummary)
                summary.lineVolumes = BuildLineVolumes(query);

            bool lineLoads = query.View != "network"
                && (query.View != "station" || !string.IsNullOrEmpty(query.LineId));
            if (lineLoads && (options.includeLineSeries || options.includeSummary))
            {
                var loadSeries = options.includeLineSeries ? new List<LineTimeLoadDto>() : null;
                var loadSummary = options.includeSummary ? new List<LineTimeSummaryDto>() : null;
                foreach (var series in m_LineTimeLoads.Select(query, true))
                {
                    LineTimeKey key = series.Key;
                    if (!query.Includes(key.Mode) || !query.Line(key.LineId)) continue;
                    LineTimeAggregate total = default;
                    bool observed = false;
                    foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                    {
                        observed = true;
                        if (options.includeSummary) AddTime(ref total, bucket.Value);
                        if (options.includeLineSeries)
                        {
                            TimeBucketKey time = SamplingSystem.BucketFromAbsoluteIndex(bucket.Key);
                            LineTimeAggregate value = bucket.Value;
                            loadSeries.Add(new LineTimeLoadDto
                            {
                                mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId,
                                dayIndex = time.DayIndex, bucketStartMinute = time.BucketStartMinute,
                                passengerFrames = value.PassengerFrames, observedFrames = value.ObservedFrames,
                                ratedPassengerFrames = value.RatedPassengerFrames, capacityFrames = value.CapacityFrames,
                                capacityObservedFrames = value.CapacityObservedFrames,
                                averageOnboardPassengers = value.ObservedFrames > 0 ? (double?)value.PassengerFrames / value.ObservedFrames : null,
                                averageLoadRatio = value.CapacityFrames > 0 ? (double?)value.RatedPassengerFrames / value.CapacityFrames : null
                            });
                        }
                    }
                    if (observed && options.includeSummary) loadSummary.Add(new LineTimeSummaryDto
                    {
                        mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId,
                        passengerFrames = total.PassengerFrames, observedFrames = total.ObservedFrames,
                        ratedPassengerFrames = total.RatedPassengerFrames, capacityFrames = total.CapacityFrames,
                        capacityObservedFrames = total.CapacityObservedFrames,
                        averageOnboardPassengers = total.ObservedFrames > 0 ? (double?)total.PassengerFrames / total.ObservedFrames : null,
                        averageLoadRatio = total.CapacityFrames > 0 ? (double?)total.RatedPassengerFrames / total.CapacityFrames : null
                    });
                }
                if (options.includeLineSeries) rows.LineTimeLoads = loadSeries.ToArray();
                if (options.includeSummary) summary.lineTimeLoads = loadSummary.ToArray();
            }

            if (stationTotalsNeeded)
                BuildStationTotals(anchors, query, summary, stationTotals);

            var warnings = new List<WarningDto>();
            foreach (var series in m_Warnings.Select(query))
            {
                WarningKey key = series.Key;
                if (!query.Includes(key.Mode) || !query.Line(key.LineId) || !query.Station(key.StationSakIndex)) continue;
                anchors.TryGetSak(key.StationSakIndex, out string stationId);
                foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                {
                    TimeBucketKey time = SamplingSystem.BucketFromAbsoluteIndex(bucket.Key);
                    warnings.Add(new WarningDto
                    {
                        mode = TransitModeCodec.Format(key.Mode), code = key.Code, lineId = key.LineId,
                        stationId = stationId, count = bucket.Value.Count, lastFrame = bucket.Value.LastFrame,
                        dayIndex = time.DayIndex, bucketStartMinute = time.BucketStartMinute
                    });
                }
            }
            rows.Warnings = warnings.ToArray();
            return rows;
        }

        private LineVolumeDto[] BuildLineVolumes(FlowQuery query)
        {
            var lines = new Dictionary<(TransitMode Mode, string Line), LineVolumeDto>();
            foreach (var series in m_StationVolumes.Select(query))
            {
                StationVolumeKey key = series.Key;
                if (!query.Includes(key.Mode) || !query.Line(key.LineId) || !query.Station(key.StationSakIndex)) continue;
                foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                {
                    var identity = (key.Mode, key.LineId);
                    if (!lines.TryGetValue(identity, out LineVolumeDto row))
                        lines[identity] = row = new LineVolumeDto
                        { mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId };
                    row.boardings += bucket.Value.Boardings;
                }
            }
            foreach (var series in m_LineTimeLoads.Select(query, true))
            {
                LineTimeKey key = series.Key;
                if (!query.Includes(key.Mode) || !query.Line(key.LineId)) continue;
                foreach (var bucket in series.Read(query.FromAbsolute, query.ToAbsolute))
                {
                    var identity = (key.Mode, key.LineId);
                    if (!lines.TryGetValue(identity, out LineVolumeDto row))
                        lines[identity] = row = new LineVolumeDto
                        { mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId };
                    row.ratedPassengerFrames += bucket.Value.RatedPassengerFrames;
                    row.capacityFrames += bucket.Value.CapacityFrames;
                }
            }
            foreach (LineVolumeDto row in lines.Values)
                row.averageLoadRatio = row.capacityFrames > 0
                    ? (double?)row.ratedPassengerFrames / row.capacityFrames : null;
            return lines.Values.ToArray();
        }

        private static bool SectionMatches(FlowQuery query, SectionVolumeKey key)
            => query.Includes(key.Mode) && query.Line(key.LineId) && key.Kind == query.SectionKind
                && (key.Kind != SectionKind.Track || key.Mode != TransitMode.Bus)
                && query.EitherStation(key.FromStationSakIndex, key.ToStationSakIndex);

        private static void BuildStationTotals(Anchors anchors, FlowQuery query, FlowSummaryDto summary,
            Dictionary<int, StationVolumeAggregate> stations)
        {
            var totals = new List<StationSummaryDto>();
            foreach (var pair in stations)
            {
                StationSummaryDto row = BuildStationSummary(anchors,
                    new StationVolumeKey(query.Mode, string.Empty, pair.Key, default), pair.Value, query.Options.includePurposes);
                row.mode = query.ModeToken;
                totals.Add(row);
            }
            summary.stationCount = totals.Count;
            summary.stationTotals = totals.ToArray();
            if (query.View == "network") summary.stationRanking = totals
                .OrderByDescending(row => (long)row.boardings + row.alightings)
                .ThenBy(row => query.Groups.Id(row.stationId), StringComparer.Ordinal).Take(query.StationRankLimit).ToArray();
        }

        private static void AddStation(ref StationVolumeAggregate total, StationVolumeAggregate value, bool purposes)
        {
            total.Boardings += value.Boardings; total.Alightings += value.Alightings;
            if (purposes) MergePurposes(ref total.Purposes, value.Purposes);
        }

        private static void AddOd(ref OdFlowAggregate total, OdFlowAggregate value, bool purposes)
        {
            total.CompletedCount += value.CompletedCount;
            if (purposes) MergePurposes(ref total.Purposes, value.Purposes);
        }

        private static void AddSection(ref SectionVolumeAggregate total, SectionVolumeAggregate value, bool purposes)
        {
            total.LoadPassengersSum += value.LoadPassengersSum; total.SampleCount += value.SampleCount;
            total.RatedLoadPassengersSum += value.RatedLoadPassengersSum; total.CapacitySum += value.CapacitySum;
            total.CapacitySampleCount += value.CapacitySampleCount;
            if (purposes) MergePurposes(ref total.Purposes, value.Purposes);
        }

        private static void AddTransfer(ref TransferAggregate total, TransferAggregate value, bool purposes)
        {
            total.CompletedCount += value.CompletedCount; total.WalkPathMetersSum += value.WalkPathMetersSum;
            total.WalkPathSampleCount += value.WalkPathSampleCount; total.WalkFramesSum += value.WalkFramesSum;
            total.WalkTimeSampleCount += value.WalkTimeSampleCount;
            if (purposes) MergePurposePairs(ref total.Purposes, value.Purposes);
        }

        private static void AddWaiting(ref StationWaitingAggregate total, StationWaitingAggregate value)
        {
            total.WaitingCountFrames += value.WaitingCountFrames; total.EstimatedWaitFrameFrames += value.EstimatedWaitFrameFrames;
            total.ObservedFrames += value.ObservedFrames; total.SampleCount += value.SampleCount;
        }

        private static void AddTime(ref LineTimeAggregate total, LineTimeAggregate value)
        {
            total.PassengerFrames += value.PassengerFrames; total.ObservedFrames += value.ObservedFrames;
            total.RatedPassengerFrames += value.RatedPassengerFrames; total.CapacityFrames += value.CapacityFrames;
            total.CapacityObservedFrames += value.CapacityObservedFrames;
        }

        private static void MergePurposes(ref Dictionary<PurposeInfo, PurposeCount> target,
            Dictionary<PurposeInfo, PurposeCount> source)
        {
            if (source == null)
                return;
            if (target == null)
                target = new Dictionary<PurposeInfo, PurposeCount>();
            foreach (KeyValuePair<PurposeInfo, PurposeCount> pair in source)
            {
                target.TryGetValue(pair.Key, out PurposeCount total);
                total.Boardings += pair.Value.Boardings;
                total.Alightings += pair.Value.Alightings;
                total.LoadPassengersSum += pair.Value.LoadPassengersSum;
                total.CompletedCount += pair.Value.CompletedCount;
                target[pair.Key] = total;
            }
        }

        private static void MergePurposePairs(ref Dictionary<PurposePair, PurposeCount> target,
            Dictionary<PurposePair, PurposeCount> source)
        {
            if (source == null)
                return;
            if (target == null)
                target = new Dictionary<PurposePair, PurposeCount>();
            foreach (KeyValuePair<PurposePair, PurposeCount> pair in source)
            {
                target.TryGetValue(pair.Key, out PurposeCount total);
                total.CompletedCount += pair.Value.CompletedCount;
                target[pair.Key] = total;
            }
        }

        private static SectionSummaryDto BuildSectionSummary(Anchors anchors,
            SectionVolumeKey key, SectionVolumeAggregate value, bool purposes)
        {
            anchors.TryGetSak(key.FromStationSakIndex, out string fromId);
            anchors.TryGetSak(key.ToStationSakIndex, out string toId);
            SectionSummaryDto row = new SectionSummaryDto();
            row.mode = TransitModeCodec.Format(key.Mode); row.lineId = key.LineId;
            row.fromStationId = fromId; row.toStationId = toId;
            row.loadPassengersSum = value.LoadPassengersSum; row.sampleCount = value.SampleCount;
            row.ratedLoadPassengersSum = value.RatedLoadPassengersSum;
            row.capacitySum = value.CapacitySum; row.capacitySampleCount = value.CapacitySampleCount;
            row.averageLoadPassengers = value.SampleCount > 0 ? (double?)value.LoadPassengersSum / value.SampleCount : null;
            row.loadRatio = value.CapacitySum > 0 ? (double?)value.RatedLoadPassengersSum / value.CapacitySum : null;
            if (purposes) row.purposeCounts = BuildPurposes(value.Purposes, 0, 0, value.LoadPassengersSum, 0);
            return row;
        }

        private static SectionLineDto BuildSectionLine(string lineId, SectionVolumeAggregate value)
        {
            return new SectionLineDto
            {
                lineId = lineId,
                loadPassengersSum = value.LoadPassengersSum,
                sampleCount = value.SampleCount,
                ratedLoadPassengersSum = value.RatedLoadPassengersSum,
                capacitySum = value.CapacitySum,
                capacitySampleCount = value.CapacitySampleCount,
                averageLoadPassengers = value.SampleCount > 0
                    ? (double?)value.LoadPassengersSum / value.SampleCount : null,
                loadRatio = value.CapacitySum > 0
                    ? (double?)value.RatedLoadPassengersSum / value.CapacitySum : null
            };
        }

        private static TransferSummaryDto BuildTransferSummary(Anchors anchors,
            FlowOptions options, Core.ClockSnapshot clock,
            KeyValuePair<TransferKey, TransferAggregate> pair)
        {
            anchors.TryGetSak(pair.Key.FromStationSakIndex, out string fromId);
            anchors.TryGetSak(pair.Key.ToStationSakIndex, out string toId);
            TransferAggregate value = pair.Value;
            return new TransferSummaryDto
            {
                fromMode = TransitModeCodec.Format(pair.Key.FromMode),
                fromLineId = pair.Key.FromLineId, fromStationId = fromId,
                fromStationOccurrence = pair.Key.FromStationOccurrence,
                toMode = TransitModeCodec.Format(pair.Key.ToMode),
                toLineId = pair.Key.ToLineId, toStationId = toId,
                toStationOccurrence = pair.Key.ToStationOccurrence,
                completedCount = value.CompletedCount,
                walkPathMetersSum = value.WalkPathMetersSum,
                walkPathSampleCount = value.WalkPathSampleCount,
                walkFramesSum = value.WalkFramesSum,
                walkTimeSampleCount = value.WalkTimeSampleCount,
                averageWalkPathMeters = value.WalkPathSampleCount > 0
                    ? (double?)value.WalkPathMetersSum / value.WalkPathSampleCount : null,
                averageWalkMinutes = value.WalkTimeSampleCount > 0
                    ? (double?)clock.ToMinutes((double)value.WalkFramesSum / value.WalkTimeSampleCount) : null,
                purposeCounts = options.includePurposes
                    ? BuildPurposePairs(value.Purposes, value.CompletedCount) : null
            };
        }

        private static WaitingSummaryDto BuildWaitingSummary(Anchors anchors, Core.ClockSnapshot clock,
            StationWaitingKey key, StationWaitingAggregate value)
        {
            anchors.TryGetSak(key.StationSakIndex, out string stationId);
            return new WaitingSummaryDto
            {
                mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId,
                stationOccurrence = key.StationOccurrence, stationId = stationId,
                waitingCountFrames = value.WaitingCountFrames, estimatedWaitFrameFrames = value.EstimatedWaitFrameFrames,
                observedFrames = value.ObservedFrames, sampleCount = value.SampleCount,
                averageWaitingCount = value.ObservedFrames > 0 ? (double?)value.WaitingCountFrames / value.ObservedFrames : null,
                averageEstimatedWaitMinutes = value.ObservedFrames > 0
                    ? (double?)clock.ToMinutes(value.EstimatedWaitFrameFrames / value.ObservedFrames) : null
            };
        }

        private static StationSummaryDto BuildStationSummary(Anchors anchors, StationVolumeKey key,
            StationVolumeAggregate value, bool purposes)
        {
            anchors.TryGetSak(key.StationSakIndex, out string stationId);
            return new StationSummaryDto
            {
                mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId, stationId = stationId,
                boardings = value.Boardings, alightings = value.Alightings,
                purposeCounts = purposes ? BuildPurposes(value.Purposes, value.Boardings, value.Alightings, 0, 0) : null
            };
        }

        private static StationVolumeDto BuildStation(Anchors anchors, StationVolumeKey key,
            StationVolumeAggregate value, TimeBucketKey bucket, bool purposes)
        {
            anchors.TryGetSak(key.StationSakIndex, out string stationId);
            return new StationVolumeDto
            {
                mode = TransitModeCodec.Format(key.Mode), lineId = key.LineId, stationId = stationId,
                stationName = stationId, boardings = value.Boardings, alightings = value.Alightings,
                waitingPassengers = 0,
                throughPassengers = 0,
                dayIndex = bucket.DayIndex, bucketStartMinute = bucket.BucketStartMinute,
                purposeCounts = purposes ? BuildPurposes(value.Purposes, value.Boardings, value.Alightings, 0, 0) : null
            };
        }

        private static OdSummaryDto BuildOdSummary(Anchors anchors, OdFlowKey key, OdFlowAggregate value, bool purposes)
        {
            anchors.TryGetSak(key.OriginStationSakIndex, out string fromId);
            anchors.TryGetSak(key.DestinationStationSakIndex, out string toId);
            return new OdSummaryDto
            {
                mode = TransitModeCodec.Format(key.Mode), firstLineId = key.FirstLineId, lastLineId = key.LastLineId,
                originStationId = fromId, destinationStationId = toId, completedCount = value.CompletedCount,
                purposeCounts = purposes ? BuildPurposes(value.Purposes, 0, 0, 0, value.CompletedCount) : null
            };
        }

        private static OdFlowDto BuildOd(Anchors anchors, OdFlowKey key, OdFlowAggregate value,
            TimeBucketKey bucket, bool purposes)
        {
            anchors.TryGetSak(key.OriginStationSakIndex, out string fromId);
            anchors.TryGetSak(key.DestinationStationSakIndex, out string toId);
            return new OdFlowDto
            {
                mode = TransitModeCodec.Format(key.Mode), lineId = key.FirstLineId,
                firstLineId = key.FirstLineId, lastLineId = key.LastLineId,
                originStationId = fromId, destinationStationId = toId, completedCount = value.CompletedCount,
                dayIndex = bucket.DayIndex, bucketStartMinute = bucket.BucketStartMinute,
                purposeCounts = purposes ? BuildPurposes(value.Purposes, 0, 0, 0, value.CompletedCount) : null
            };
        }

        private static SectionVolumeDto BuildSection(Anchors anchors, SectionVolumeKey key,
            SectionVolumeAggregate value, TimeBucketKey bucket, bool purposes)
        {
            anchors.TryGetSak(key.FromStationSakIndex, out string fromId);
            anchors.TryGetSak(key.ToStationSakIndex, out string toId);
            SectionVolumeDto row = new SectionVolumeDto();
            row.mode = TransitModeCodec.Format(key.Mode); row.lineId = key.LineId;
            row.fromStationId = fromId; row.toStationId = toId;
            row.averageLoadPassengers = value.SampleCount > 0 ? (int)(value.LoadPassengersSum / value.SampleCount) : 0;
            row.loadPassengersSum = value.LoadPassengersSum; row.sampleCount = value.SampleCount;
            row.ratedLoadPassengersSum = value.RatedLoadPassengersSum;
            row.capacitySum = value.CapacitySum; row.capacitySampleCount = value.CapacitySampleCount;
            row.loadRatio = value.CapacitySum > 0 ? (double?)value.RatedLoadPassengersSum / value.CapacitySum : null;
            row.dayIndex = bucket.DayIndex; row.bucketStartMinute = bucket.BucketStartMinute;
            if (purposes) row.purposeCounts = BuildPurposes(value.Purposes, 0, 0, value.LoadPassengersSum, 0);
            return row;
        }

        private static TransferFlowDto BuildTransfer(Anchors anchors, KeyValuePair<TransferKey, TransferAggregate> pair,
            Core.ClockSnapshot clock, bool includePurposes)
        {
            string fromStationId = string.Empty;
            string toStationId = string.Empty;
            anchors.TryGetSak(pair.Key.FromStationSakIndex, out fromStationId);
            anchors.TryGetSak(pair.Key.ToStationSakIndex, out toStationId);
            return new TransferFlowDto
            {
                fromMode = TransitModeCodec.Format(pair.Key.FromMode), fromLineId = pair.Key.FromLineId, fromStationId = fromStationId,
                fromStationOccurrence = pair.Key.FromStationOccurrence,
                toMode = TransitModeCodec.Format(pair.Key.ToMode), toLineId = pair.Key.ToLineId, toStationId = toStationId,
                toStationOccurrence = pair.Key.ToStationOccurrence,
                completedCount = pair.Value.CompletedCount,
                purposeCounts = includePurposes
                    ? BuildPurposePairs(pair.Value.Purposes, pair.Value.CompletedCount) : null,
                walkPathMetersSum = pair.Value.WalkPathMetersSum, walkPathSampleCount = pair.Value.WalkPathSampleCount,
                walkFramesSum = pair.Value.WalkFramesSum, walkTimeSampleCount = pair.Value.WalkTimeSampleCount,
                averageWalkMinutes = pair.Value.WalkTimeSampleCount > 0
                    ? (double?)clock.ToMinutes((double)pair.Value.WalkFramesSum / pair.Value.WalkTimeSampleCount)
                    : null,
                dayIndex = pair.Key.Bucket.DayIndex, bucketStartMinute = pair.Key.Bucket.BucketStartMinute
            };
        }

        private static PurposeCountDto[] BuildPurposes(Dictionary<PurposeInfo, PurposeCount> source,
            int boardings, int alightings, long load, int completed)
        {
            List<PurposeCountDto> rows = new List<PurposeCountDto>();
            PurposeCountDto unknown = null;
            if (source != null)
            foreach (KeyValuePair<PurposeInfo, PurposeCount> pair in source)
            {
                PurposeCount count = pair.Value;
                boardings -= count.Boardings;
                alightings -= count.Alightings;
                load -= count.LoadPassengersSum;
                completed -= count.CompletedCount;
                PurposeCountDto row = new PurposeCountDto
                {
                    rawPurpose = pair.Key.RawPurpose < 0 ? (int?)null : pair.Key.RawPurpose,
                    category = pair.Key.CategoryCode,
                    boardings = count.Boardings, alightings = count.Alightings,
                    loadPassengersSum = count.LoadPassengersSum, completedCount = count.CompletedCount
                };
                rows.Add(row);
                if (pair.Key.Equals(PurposeInfo.Unknown)) unknown = row;
            }
            if (boardings != 0 || alightings != 0 || load != 0 || completed != 0)
            {
                if (unknown == null)
                {
                    unknown = new PurposeCountDto { rawPurpose = null, category = "unknown" };
                    rows.Add(unknown);
                }
                unknown.boardings += boardings; unknown.alightings += alightings;
                unknown.loadPassengersSum += load; unknown.completedCount += completed;
            }
            return rows.ToArray();
        }

        private static PurposeCountDto[] BuildPurposePairs(
            Dictionary<PurposePair, PurposeCount> source, int completed)
        {
            List<PurposeCountDto> rows = new List<PurposeCountDto>();
            PurposeCountDto unknown = null;
            if (source != null)
            foreach (KeyValuePair<PurposePair, PurposeCount> pair in source)
            {
                completed -= pair.Value.CompletedCount;
                PurposeCountDto row = new PurposeCountDto
                {
                    rawPurpose = pair.Key.From.RawPurpose < 0 ? (int?)null : pair.Key.From.RawPurpose,
                    category = pair.Key.From.CategoryCode,
                    toRawPurpose = pair.Key.To.RawPurpose < 0 ? (int?)null : pair.Key.To.RawPurpose,
                    toCategory = pair.Key.To.CategoryCode, completedCount = pair.Value.CompletedCount
                };
                rows.Add(row);
                if (pair.Key.From.Equals(PurposeInfo.Unknown) && pair.Key.To.Equals(PurposeInfo.Unknown)) unknown = row;
            }
            if (completed != 0)
            {
                if (unknown == null)
                {
                    unknown = new PurposeCountDto { rawPurpose = null, category = "unknown",
                        toRawPurpose = null, toCategory = "unknown" };
                    rows.Add(unknown);
                }
                unknown.completedCount += completed;
            }
            return rows.ToArray();
        }

        private static StationWaitingDto BuildWaiting(Anchors anchors,
            KeyValuePair<StationWaitingKey, StationWaitingAggregate> pair, Core.ClockSnapshot clock)
        {
            string stationId = string.Empty;
            anchors.TryGetSak(pair.Key.StationSakIndex, out stationId);
            long observed = pair.Value.ObservedFrames;
            return new StationWaitingDto
            {
                mode = TransitModeCodec.Format(pair.Key.Mode), lineId = pair.Key.LineId,
                stationOccurrence = pair.Key.StationOccurrence, stationId = stationId,
                latestWaitingCount = pair.Value.LatestWaitingCount, peakWaitingCount = pair.Value.PeakWaitingCount,
                waitingCountFrames = pair.Value.WaitingCountFrames,
                latestEstimatedWaitFrames = pair.Value.LatestEstimatedWaitFrames,
                estimatedWaitFrameFrames = pair.Value.EstimatedWaitFrameFrames,
                observedFrames = observed,
                latestEstimatedWaitMinutes = clock.ToMinutes(pair.Value.LatestEstimatedWaitFrames),
                observedGameMinutes = clock.ToMinutes(observed),
                sampleCount = pair.Value.SampleCount,
                averageWaitingCount = observed > 0 ? (double?)pair.Value.WaitingCountFrames / observed : null,
                averageEstimatedWaitMinutes = observed > 0
                    ? (double?)clock.ToMinutes(pair.Value.EstimatedWaitFrameFrames / observed) : null,
                dayIndex = pair.Key.Bucket.DayIndex, bucketStartMinute = pair.Key.Bucket.BucketStartMinute
            };
        }
    }

    internal sealed class SnapshotRows
    {
        internal StationVolumeDto[] StationVolumes;
        internal SectionVolumeDto[] SectionVolumes;
        internal OdFlowDto[] OdFlows;
        internal WarningDto[] Warnings;
        internal TransferFlowDto[] TransferFlows;
        internal StationWaitingDto[] StationWaiting;
        internal LineTimeLoadDto[] LineTimeLoads;
        internal NetworkTimeLoadDto[] NetworkTimeLoads;
        internal FlowSummaryDto Summary;

        internal static SnapshotRows Empty()
        {
            return new SnapshotRows
            {
                StationVolumes = Array.Empty<StationVolumeDto>(),
                SectionVolumes = Array.Empty<SectionVolumeDto>(),
                OdFlows = Array.Empty<OdFlowDto>(),
                Warnings = Array.Empty<WarningDto>(),
                TransferFlows = Array.Empty<TransferFlowDto>(),
                StationWaiting = Array.Empty<StationWaitingDto>(),
                LineTimeLoads = Array.Empty<LineTimeLoadDto>(),
                NetworkTimeLoads = Array.Empty<NetworkTimeLoadDto>()
            };
        }
    }
}

#pragma warning restore CS0649
