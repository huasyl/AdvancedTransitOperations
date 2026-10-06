using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Unity.Collections;
using Unity.Entities;

namespace RapidTransitMod.PassengerFlow
{
    internal static partial class Persistence
    {
        private const int SchemaVersion = 6;
        private static readonly DefaultContractResolver JsonResolver = new RowResolver();

        internal sealed class PreparedSave
        {
            internal PassengerFlowPersistentState State;
            internal List<string> Chunks;
            internal int PayloadLength;
            internal double CaptureMilliseconds;
            internal double EncodeMilliseconds;
        }

        // 只编码已经脱离运行态的快照，同步保存与后台准备共用此入口。
        internal static PreparedSave Prepare(PassengerFlowPersistentState state, double captureMilliseconds)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var strings = new List<string>();
            var settings = new JsonSerializerSettings
            {
                ContractResolver = JsonResolver,
                Converters = { new Rows(strings) }
            };
            string payload = JsonConvert.SerializeObject(new Envelope
            {
                schemaVersion = state.schemaVersion,
                strings = strings,
                state = state
            }, settings);
            List<string> chunks = Workbenches.Buffer.Split(payload);
            return new PreparedSave
            {
                State = state,
                Chunks = chunks,
                PayloadLength = payload.Length,
                CaptureMilliseconds = captureMilliseconds,
                EncodeMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                    * 1000d / System.Diagnostics.Stopwatch.Frequency
            };
        }

        internal static void SaveToCity(EntityManager entityManager, Entity city, PreparedSave prepared = null)
        {
            if (city == Entity.Null)
            {
                Diagnostics.Log("PassengerFlowPersistSave", "result=skip reason=cityNull");
                return;
            }

            bool timing = Diagnostics.Enabled;
            long started = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (!entityManager.HasBuffer<PassengerFlowStateElement>(city))
            {
                entityManager.AddBuffer<PassengerFlowStateElement>(city);
                Diagnostics.Log("PassengerFlowPersistSave", "action=createBuffer city=" + Diagnostics.DescribeEntity(city));
            }

            bool background = prepared != null;
            long captureStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            PassengerFlowPersistentState state = prepared != null ? prepared.State : Capture();
            double captureMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - captureStarted)
                * 1000d / System.Diagnostics.Stopwatch.Frequency;
            if (state == null)
            {
                DynamicBuffer<PassengerFlowStateElement> emptyBuffer = entityManager.GetBuffer<PassengerFlowStateElement>(city);
                emptyBuffer.Clear();
                Diagnostics.Log("PassengerFlowPersistSave", "result=cleared reason=captureNull city=" + Diagnostics.DescribeEntity(city));
                return;
            }

            prepared ??= Prepare(state, captureMilliseconds);
            long runtimeStarted = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            SaveRuntime(entityManager, city, SamplingSystem.CurrentState, Runtime.Current.Frame());
            long runtimeFinished = timing ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            DynamicBuffer<PassengerFlowStateElement> buffer = entityManager.GetBuffer<PassengerFlowStateElement>(city);
            Write(buffer, prepared.Chunks);
            if (timing)
            {
                long finished = System.Diagnostics.Stopwatch.GetTimestamp();
                double tickMs = 1000d / System.Diagnostics.Stopwatch.Frequency;
                long runtimeTicks = runtimeFinished - runtimeStarted;
                Diagnostics.Log(
                    "PassengerFlowPersistSave",
                    "result=saved city=" + Diagnostics.DescribeEntity(city)
                    + " scope=saveToCity mode=" + (background ? "background" : "synchronous")
                    + " totalMs=" + ((finished - started) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " captureMainMs=" + prepared.CaptureMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " encodeMs=" + prepared.EncodeMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " encodeThread=" + (background ? "background" : "main")
                    + " commitMainMs=" + ((finished - runtimeStarted) * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " runtimeMs=" + (runtimeTicks * tickMs).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    + " payloadLength=" + prepared.PayloadLength.ToString()
                    + " chunkCount=" + prepared.Chunks.Count.ToString()
                    + " " + DescribePersistedState(state)
                    + " " + DescribeRuntimeState(SamplingSystem.CurrentState));
            }
        }

        internal static bool RestoreFromCity(EntityManager entityManager, Entity city)
        {
            if (city == Entity.Null || !entityManager.HasBuffer<PassengerFlowStateElement>(city))
            {
                if (RtLog.VerboseEnabled)
                {
                    Diagnostics.Log(
                        "PassengerFlowPersistRestore",
                        "result=skip reason=" + (city == Entity.Null ? "cityNull" : "bufferMissing")
                        + " city=" + Diagnostics.DescribeEntity(city));
                }
                return false;
            }

            DynamicBuffer<PassengerFlowStateElement> buffer = entityManager.GetBuffer<PassengerFlowStateElement>(city, true);
            if (buffer.Length == 0)
            {
                if (RtLog.VerboseEnabled)
                {
                    Diagnostics.Log(
                        "PassengerFlowPersistRestore",
                        "result=skip reason=bufferEmpty city=" + Diagnostics.DescribeEntity(city));
                }
                return false;
            }

            string payload = Read(buffer);
            if (string.IsNullOrEmpty(payload))
            {
                if (RtLog.VerboseEnabled)
                {
                    Diagnostics.Log(
                        "PassengerFlowPersistRestore",
                        "result=skip reason=payloadEmpty city=" + Diagnostics.DescribeEntity(city)
                        + " bufferLength=" + buffer.Length.ToString());
                }
                return false;
            }

            PassengerFlowPersistentState persisted;
            try
            {
                Envelope envelope = JsonConvert.DeserializeObject<Envelope>(payload);
                if (envelope == null)
                    throw new JsonSerializationException("Missing passenger flow envelope.");
                if (envelope.schemaVersion != SchemaVersion)
                {
                    SamplingSystem.ClearState();
                    ClearCityBuffers(entityManager, city);
                    Diagnostics.Log("PassengerFlowPersistRestore", "result=reset schemaVersion=" + envelope.schemaVersion);
                    return false;
                }
                if (envelope.strings == null || !(envelope.state is JObject body))
                    throw new JsonSerializationException("Missing passenger flow state or strings.");
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    ContractResolver = JsonResolver,
                    Converters = { new Rows(envelope.strings) }
                });
                persisted = body.ToObject<PassengerFlowPersistentState>(serializer);
                persisted.schemaVersion = envelope.schemaVersion;
            }
            catch (Exception ex)
            {
                if (RtLog.VerboseEnabled)
                {
                    Diagnostics.Log(
                        "PassengerFlowPersistRestore",
                        "result=error reason=deserializeException city=" + Diagnostics.DescribeEntity(city)
                        + " payloadLength=" + payload.Length.ToString()
                        + " exception=" + ex.GetType().Name
                        + " message=" + ex.Message);
                }
                throw;
            }

            string failureReason = Restore(persisted);
            if (string.IsNullOrEmpty(failureReason))
            {
                if (RtLog.VerboseEnabled)
                {
                    Diagnostics.Log(
                        "PassengerFlowPersistRestore",
                        "result=restored city=" + Diagnostics.DescribeEntity(city)
                        + " payloadLength=" + payload.Length.ToString()
                        + " bufferLength=" + buffer.Length.ToString()
                        + " " + DescribePersistedState(persisted)
                        + " " + DescribeRuntimeState(SamplingSystem.CurrentState));
                }
                ReadRuntime(entityManager, city, SamplingSystem.CurrentState);
                return true;
            }

            if (RtLog.VerboseEnabled)
            {
                Diagnostics.Log(
                    "PassengerFlowPersistRestore",
                    "result=rejected city=" + Diagnostics.DescribeEntity(city)
                    + " reason=" + failureReason
                    + " payloadLength=" + payload.Length.ToString()
                    + " bufferLength=" + buffer.Length.ToString()
                    + " " + DescribePersistedState(persisted)
                    + " " + DescribeRuntimeState(SamplingSystem.CurrentState));
            }
            return false;
        }

        private sealed class Envelope
        {
            [JsonProperty(Order = 0, Required = Required.Always)] public int schemaVersion;
            [JsonProperty(Order = 2)] public List<string> strings;
            [JsonProperty(Order = 1)] public object state;
        }

        private sealed class RowResolver : DefaultContractResolver
        {
            protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization members)
            {
                return base.CreateProperties(type, members)
                    .Where(property => !property.Ignored
                        && (type != typeof(PassengerFlowPersistentState) || property.PropertyName != nameof(PassengerFlowPersistentState.schemaVersion)))
                    .OrderBy(property => property.Order ?? -1)
                    .ThenBy(property => property.PropertyName, StringComparer.Ordinal)
                    .ToList();
            }
        }

        private sealed class Rows : JsonConverter
        {
            private readonly List<string> strings;
            private readonly Dictionary<string, int> indices = new Dictionary<string, int>(StringComparer.Ordinal);

            internal Rows(List<string> strings)
            {
                this.strings = strings;
            }

            public override bool CanConvert(Type type)
            {
                return !type.IsArray
                    && type.Assembly == typeof(PassengerFlowPersistentState).Assembly
                    && type.Namespace == typeof(PassengerFlowPersistentState).Namespace
                    && type.Name.StartsWith("PassengerFlowPersisted", StringComparison.Ordinal);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(value.GetType());
                JsonPropertyCollection properties = contract.Properties;
                var row = new object[properties.Count];
                for (int i = 0; i < properties.Count; i++)
                {
                    object item = properties[i].ValueProvider.GetValue(value);
                    if (item is string text)
                    {
                        if (!indices.TryGetValue(text, out int index))
                        {
                            index = strings.Count;
                            indices.Add(text, index);
                            strings.Add(text);
                        }
                        item = index;
                    }
                    row[i] = item;
                }
                serializer.Serialize(writer, row);
            }

            public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null)
                    return null;
                var row = (JArray)((JTokenReader)reader).CurrentToken;
                var contract = (JsonObjectContract)serializer.ContractResolver.ResolveContract(type);
                JsonPropertyCollection properties = contract.Properties;
                if (row.Count != properties.Count)
                    throw new JsonSerializationException("Invalid passenger flow row length.");
                reader.Skip();
                object value = contract.DefaultCreator();
                for (int i = 0; i < properties.Count; i++)
                {
                    JsonProperty property = properties[i];
                    object item = property.PropertyType == typeof(string) && row[i].Type != JTokenType.Null
                        ? strings[row[i].ToObject<int>(serializer)]
                        : row[i].ToObject(property.PropertyType, serializer);
                    property.ValueProvider.SetValue(value, item);
                }
                return value;
            }
        }

        internal static PassengerFlowPersistentState Capture()
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return null;

            Port port = Runtime.Current;
            var persisted = new PassengerFlowPersistentState
            {
                schemaVersion = SchemaVersion,
                bucketMinutes = Snapshot.BucketMinutes,
                dayIndex = state.DayIndex,
                lastMinute = state.LastMinute,
                currentAbsoluteBucketIndex = SamplingSystem.AbsoluteBucketIndex(state.CurrentBucket),
                currentBucketDayIndex = state.CurrentBucket.DayIndex,
                currentBucketStartMinute = state.CurrentBucket.BucketStartMinute,
                stationCatalog = state.Anchors.ExportCatalog(port),
                stationVolumes = state.Aggregates.ExportStationVolumes(),
                sectionVolumes = state.Aggregates.ExportSectionVolumes(),
                odFlows = state.Aggregates.ExportOdFlows(),
                transferFlows = state.Aggregates.ExportTransfers(),
                sampleGroups = state.Aggregates.ExportSampleGroups(),
                lineTimeLoads = state.Aggregates.ExportLineTimeLoads(),
                networkTimeLoads = state.Aggregates.ExportNetworkTimeLoads(),
                stationWaiting = state.Aggregates.ExportStationWaiting(),
                warnings = state.Aggregates.ExportWarnings()
            };
            ConvertBucketTimes(persisted, false);
            return persisted;
        }

        private static string Restore(PassengerFlowPersistentState persisted)
        {
            State state = SamplingSystem.CurrentState;
            if (state == null)
                return "runtimeStateNull";

            if (TryGetSupportFailureReason(persisted, out string failureReason))
                return failureReason;

            ConvertBucketTimes(persisted, true);
            state.Clear();

            int currentAbsoluteBucket = ResolveCurrentAbsoluteBucket(persisted);
            int minAbsoluteBucket = SamplingSystem.RetainedFromAbsolute(persisted.currentBucketDayIndex);
            PassengerFlowPersistentState trimmed = Trim(persisted, minAbsoluteBucket, currentAbsoluteBucket);
            if (Diagnostics.Enabled)
            {
                Diagnostics.Log(
                    "PassengerFlowPersistRestoreApply",
                    "currentAbsoluteBucket=" + currentAbsoluteBucket.ToString()
                    + " minAbsoluteBucket=" + minAbsoluteBucket.ToString()
                    + " trimmedStationVolumes=" + Diagnostics.DescribeCount(trimmed.stationVolumes)
                    + " trimmedSectionVolumes=" + Diagnostics.DescribeCount(trimmed.sectionVolumes)
                    + " trimmedOdFlows=" + Diagnostics.DescribeCount(trimmed.odFlows)
                    + " trimmedWarnings=" + Diagnostics.DescribeCount(trimmed.warnings));
            }

            state.DayIndex = persisted.dayIndex;
            state.LastMinute = persisted.lastMinute;
            state.CurrentBucket = new TimeBucketKey(
                persisted.currentBucketDayIndex,
                persisted.currentBucketStartMinute);
            state.CurrentAbsoluteBucketIndex = currentAbsoluteBucket;
            state.Anchors.RestoreCatalog(trimmed.stationCatalog);
            state.Aggregates.Restore(
                trimmed.stationVolumes,
                trimmed.sectionVolumes,
                trimmed.odFlows,
                trimmed.transferFlows,
                trimmed.sampleGroups,
                trimmed.lineTimeLoads,
                trimmed.networkTimeLoads,
                trimmed.stationWaiting,
                trimmed.warnings);

            SamplingSystem.TrimRetainedDays(state);
            return string.Empty;
        }

        private static int ResolveCurrentAbsoluteBucket(PassengerFlowPersistentState persisted)
        {
            if (persisted == null)
                return 0;

            if (persisted.currentAbsoluteBucketIndex > 0)
                return persisted.currentAbsoluteBucketIndex;

            return SamplingSystem.AbsoluteBucketIndex(new TimeBucketKey(
                persisted.currentBucketDayIndex,
                persisted.currentBucketStartMinute));
        }

        private static bool TryGetSupportFailureReason(PassengerFlowPersistentState persisted, out string failureReason)
        {
            failureReason = string.Empty;
            if (persisted == null)
            {
                failureReason = "persistedNull";
                return true;
            }

            if (persisted.bucketMinutes != Snapshot.BucketMinutes)
            {
                failureReason = "bucketMinutesMismatch(expected=" + Snapshot.BucketMinutes.ToString()
                    + ",actual=" + persisted.bucketMinutes.ToString() + ")";
                return true;
            }

            int persistedMinute = persisted.lastMinute;
            if (persistedMinute < -1 || persistedMinute >= 1440)
            {
                failureReason = "lastMinuteInvalid(" + persistedMinute.ToString() + ")";
                return true;
            }

            int persistedDayIndex = persisted.currentBucketDayIndex;
            if (!IsValidBucket(persistedDayIndex, persisted.currentBucketStartMinute))
            {
                failureReason = "currentBucketInvalid(day=" + persistedDayIndex.ToString()
                    + ",minute=" + persisted.currentBucketStartMinute.ToString() + ")";
                return true;
            }

            int computedAbsoluteBucket = SamplingSystem.AbsoluteBucketIndex(
                new TimeBucketKey(persistedDayIndex, persisted.currentBucketStartMinute));
            if (persisted.currentAbsoluteBucketIndex > 0
                && persisted.currentAbsoluteBucketIndex != computedAbsoluteBucket)
            {
                failureReason = "absoluteBucketMismatch(expected=" + computedAbsoluteBucket.ToString()
                    + ",actual=" + persisted.currentAbsoluteBucketIndex.ToString() + ")";
                return true;
            }

            return false;
        }

        private static bool IsValidBucket(int dayIndex, int bucketStartMinute)
        {
            return dayIndex >= 0
                && bucketStartMinute >= 0
                && bucketStartMinute < 1440
                && bucketStartMinute % Snapshot.BucketMinutes == 0;
        }

        private static PassengerFlowPersistentState Trim(
            PassengerFlowPersistentState persisted,
            int minAbsoluteBucket,
            int maxAbsoluteBucket)
        {
            return new PassengerFlowPersistentState
            {
                schemaVersion = persisted.schemaVersion,
                bucketMinutes = persisted.bucketMinutes,
                dayIndex = persisted.dayIndex,
                lastMinute = persisted.lastMinute,
                currentAbsoluteBucketIndex = maxAbsoluteBucket,
                currentBucketDayIndex = persisted.currentBucketDayIndex,
                currentBucketStartMinute = persisted.currentBucketStartMinute,
                stationCatalog = persisted.stationCatalog ?? Array.Empty<PassengerFlowPersistedStationCatalog>(),
                stationVolumes = (persisted.stationVolumes ?? Array.Empty<PassengerFlowPersistedStationVolume>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                sectionVolumes = (persisted.sectionVolumes ?? Array.Empty<PassengerFlowPersistedSectionVolume>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                odFlows = (persisted.odFlows ?? Array.Empty<PassengerFlowPersistedOdFlow>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                transferFlows = (persisted.transferFlows ?? Array.Empty<PassengerFlowPersistedTransferFlow>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                sampleGroups = (persisted.sampleGroups ?? Array.Empty<PassengerFlowPersistedSampleGroup>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                lineTimeLoads = (persisted.lineTimeLoads ?? Array.Empty<PassengerFlowPersistedLineTimeLoad>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                networkTimeLoads = (persisted.networkTimeLoads ?? Array.Empty<PassengerFlowPersistedNetworkTimeLoad>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                stationWaiting = (persisted.stationWaiting ?? Array.Empty<PassengerFlowPersistedStationWaiting>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
                warnings = (persisted.warnings ?? Array.Empty<PassengerFlowPersistedWarning>())
                    .Where(row => IsWithinWindow(row, minAbsoluteBucket, maxAbsoluteBucket))
                    .ToArray(),
            };
        }

        private static bool IsWithinWindow(PassengerFlowPersistedBucketRow row, int minAbsoluteBucket, int maxAbsoluteBucket)
        {
            if (row == null)
                return false;

            if (!IsValidBucket(row.dayIndex, row.bucketStartMinute))
                return false;

            int absoluteBucket = SamplingSystem.AbsoluteBucketIndex(
                new TimeBucketKey(row.dayIndex, row.bucketStartMinute));
            return absoluteBucket >= minAbsoluteBucket && absoluteBucket <= maxAbsoluteBucket;
        }

        private static void ConvertBucketTimes(PassengerFlowPersistentState persisted, bool restore)
        {
            int baseBucket = SamplingSystem.RetainedFromAbsolute(persisted.dayIndex);
            var tables = new PassengerFlowPersistedBucketRow[][]
            {
                persisted.stationVolumes, persisted.sectionVolumes, persisted.odFlows,
                persisted.transferFlows, persisted.sampleGroups, persisted.lineTimeLoads,
                persisted.networkTimeLoads, persisted.stationWaiting, persisted.warnings
            };
            foreach (var rows in tables)
            {
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    if (restore)
                    {
                        TimeBucketKey bucket = SamplingSystem.BucketFromAbsoluteIndex(baseBucket + row.bucketIndex);
                        row.dayIndex = bucket.DayIndex;
                        row.bucketStartMinute = bucket.BucketStartMinute;
                    }
                    else
                    {
                        row.bucketIndex = SamplingSystem.AbsoluteBucketIndex(
                            new TimeBucketKey(row.dayIndex, row.bucketStartMinute)) - baseBucket;
                    }
                }
            }
        }

        private static string Read(DynamicBuffer<PassengerFlowStateElement> buffer)
        {
            PassengerFlowStateElement[] ordered = new PassengerFlowStateElement[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
                ordered[i] = buffer[i];

            Array.Sort(ordered, (left, right) => left.m_ChunkIndex.CompareTo(right.m_ChunkIndex));
            System.Text.StringBuilder payload = new System.Text.StringBuilder();
            for (int i = 0; i < ordered.Length; i++)
                payload.Append(ordered[i].m_PayloadChunk.ToString());

            return payload.ToString();
        }

        private static void Write(DynamicBuffer<PassengerFlowStateElement> buffer, List<string> chunks)
        {
            buffer.Clear();
            if (chunks == null || chunks.Count == 0)
                return;

            for (int i = 0; i < chunks.Count; i++)
            {
                buffer.Add(new PassengerFlowStateElement
                {
                    m_ChunkIndex = i,
                    m_PayloadChunk = new FixedString4096Bytes(chunks[i] ?? string.Empty)
                });
            }
        }

        private static string DescribePersistedState(PassengerFlowPersistentState persisted)
        {
            if (persisted == null)
                return "persistedState=null";

            return "persistedState="
                + "schemaVersion:" + persisted.schemaVersion.ToString()
                + ",bucketMinutes:" + persisted.bucketMinutes.ToString()
                + ",dayIndex:" + persisted.dayIndex.ToString()
                + ",lastMinute:" + persisted.lastMinute.ToString()
                + ",currentAbsoluteBucketIndex:" + persisted.currentAbsoluteBucketIndex.ToString()
                + ",currentBucket:" + persisted.currentBucketDayIndex.ToString()
                + ":" + persisted.currentBucketStartMinute.ToString()
                + ",stationCatalog:" + Diagnostics.DescribeCount(persisted.stationCatalog)
                + ",stationVolumes:" + Diagnostics.DescribeCount(persisted.stationVolumes)
                + ",sectionVolumes:" + Diagnostics.DescribeCount(persisted.sectionVolumes)
                + ",odFlows:" + Diagnostics.DescribeCount(persisted.odFlows)
                + ",warnings:" + Diagnostics.DescribeCount(persisted.warnings);
        }

        private static string DescribeRuntimeState(State state)
        {
            if (state == null)
                return "runtimeState=null";

            return "runtimeState="
                + "dayIndex:" + state.DayIndex.ToString()
                + ",lastMinute:" + state.LastMinute.ToString()
                + ",currentBucket:" + Diagnostics.DescribeBucket(state.CurrentBucket)
                + ",currentAbsoluteBucketIndex:" + state.CurrentAbsoluteBucketIndex.ToString()
                + ",anchors:" + state.Anchors.StationCount.ToString()
                + ",openStops:" + state.OpenStops.Count.ToString()
                + ",pendingSamples:" + state.PendingSamples.Count.ToString()
                + ",baselines:" + state.Baselines.Count.ToString()
                + ",activeTrips:" + state.Trips.ActiveTripCount.ToString()
                + ",pendingTransfers:" + state.Trips.PendingTransferCount.ToString()
                + ",stationVolumes:" + state.Aggregates.StationVolumeCount.ToString()
                + ",sectionVolumes:" + state.Aggregates.SectionVolumeCount.ToString()
                + ",odFlows:" + state.Aggregates.OdFlowCount.ToString()
                + ",warnings:" + state.Aggregates.WarningCount.ToString();
        }
    }

    [DataContract]
    public sealed class PassengerFlowPersistentState
    {
        [DataMember] public int schemaVersion;
        [DataMember] public int bucketMinutes;
        [DataMember] public int dayIndex;
        [DataMember] public int lastMinute;
        [DataMember] public int currentBucketDayIndex;
        [DataMember] public int currentAbsoluteBucketIndex;
        [DataMember] public int currentBucketStartMinute;
        [DataMember] public PassengerFlowPersistedStationCatalog[] stationCatalog;
        [DataMember] public PassengerFlowPersistedStationVolume[] stationVolumes;
        [DataMember] public PassengerFlowPersistedSectionVolume[] sectionVolumes;
        [DataMember] public PassengerFlowPersistedOdFlow[] odFlows;
        [DataMember] public PassengerFlowPersistedTransferFlow[] transferFlows;
        [DataMember] public PassengerFlowPersistedSampleGroup[] sampleGroups;
        [DataMember] public PassengerFlowPersistedLineTimeLoad[] lineTimeLoads;
        [DataMember] public PassengerFlowPersistedNetworkTimeLoad[] networkTimeLoads;
        [DataMember] public PassengerFlowPersistedStationWaiting[] stationWaiting;
        [DataMember] public PassengerFlowPersistedWarning[] warnings;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedStationCatalog
    {
        [DataMember] public int stationSakIndex;
        [DataMember] public string stationId;
        [DataMember] public string stationName;
    }

    [DataContract]
    public abstract class PassengerFlowPersistedBucketRow
    {
        [DataMember] public int bucketIndex;
        [IgnoreDataMember] public int dayIndex;
        [IgnoreDataMember] public int bucketStartMinute;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedStationVolume : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string lineId;
        [DataMember] public int stationSakIndex;
        [DataMember] public int boardings;
        [DataMember] public int alightings;
        [DataMember] public PassengerFlowPersistedStationPurposeCount[] purposeCounts;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedSectionVolume : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string sectionKind;
        [DataMember] public string lineId;
        [DataMember] public int fromStationSakIndex;
        [DataMember] public int toStationSakIndex;
        [DataMember] public long loadPassengersSum;
        [DataMember] public long capacitySum;
        [DataMember] public long ratedLoadPassengersSum;
        [DataMember] public int sampleCount;
        [DataMember] public int capacitySampleCount;
        [DataMember] public PassengerFlowPersistedSectionPurposeCount[] purposeCounts;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedTransferFlow : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string fromMode;
        [DataMember] public string fromLineId;
        [DataMember] public int fromStationSakIndex;
        [DataMember] public int fromStationOccurrence;
        [DataMember] public string toMode;
        [DataMember] public string toLineId;
        [DataMember] public int toStationSakIndex;
        [DataMember] public int toStationOccurrence;
        [DataMember] public int completedCount;
        [DataMember] public double walkPathMetersSum;
        [DataMember] public int walkPathSampleCount;
        [DataMember] public long walkFramesSum;
        [DataMember] public int walkTimeSampleCount;
        [DataMember] public PassengerFlowPersistedTransferPurposeCount[] purposeCounts;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedSampleGroup : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string fromMode;
        [DataMember] public string fromLineId;
        [DataMember] public int fromStationSakIndex;
        [DataMember] public int fromStationOccurrence;
        [DataMember] public string toMode;
        [DataMember] public string toLineId;
        [DataMember] public int toStationSakIndex;
        [DataMember] public int toStationOccurrence;
        [DataMember] public int selectedCount;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedLineTimeLoad : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string lineId;
        [DataMember] public long passengerFrames;
        [DataMember] public long observedFrames;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public long capacityObservedFrames;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedNetworkTimeLoad : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public long passengerFrames;
        [DataMember] public long observedFrames;
        [DataMember] public long ratedPassengerFrames;
        [DataMember] public long capacityFrames;
        [DataMember] public long capacityObservedFrames;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedStationWaiting : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string lineId;
        [DataMember] public int stationOccurrence;
        [DataMember] public int stationSakIndex;
        [DataMember] public int latestWaitingCount;
        [DataMember] public int peakWaitingCount;
        [DataMember] public long waitingCountFrames;
        [DataMember] public double latestEstimatedWaitFrames;
        [DataMember] public double estimatedWaitFrameFrames;
        [DataMember] public long observedFrames;
        [DataMember] public int sampleCount;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedOdFlow : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string firstLineId;
        [DataMember] public string lastLineId;
        [DataMember] public int originStationSakIndex;
        [DataMember] public int destinationStationSakIndex;
        [DataMember] public int completedCount;
        [DataMember] public PassengerFlowPersistedOdPurposeCount[] purposeCounts;
    }

    [DataContract]
    public abstract class PassengerFlowPersistedPurposeCount
    {
        [DataMember] public int rawPurpose;
        [DataMember] public byte category;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedStationPurposeCount : PassengerFlowPersistedPurposeCount
    {
        [DataMember] public int boardings;
        [DataMember] public int alightings;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedSectionPurposeCount : PassengerFlowPersistedPurposeCount
    {
        [DataMember] public long loadPassengersSum;
    }

    [DataContract]
    public class PassengerFlowPersistedOdPurposeCount : PassengerFlowPersistedPurposeCount
    {
        [DataMember] public int completedCount;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedTransferPurposeCount : PassengerFlowPersistedOdPurposeCount
    {
        [DataMember] public int toRawPurpose;
        [DataMember] public byte toCategory;
    }

    [DataContract]
    public sealed class PassengerFlowPersistedWarning : PassengerFlowPersistedBucketRow
    {
        [DataMember] public string mode;
        [DataMember] public string code;
        [DataMember] public string lineId;
        [DataMember] public int stationSakIndex;
        [DataMember] public int count;
        [DataMember] public uint lastFrame;
    }
}
